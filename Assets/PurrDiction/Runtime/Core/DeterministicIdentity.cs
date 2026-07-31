using System;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class DeterministicIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        [Tooltip("How the server responds when this identity's deterministic state diverges on a client. Inherit uses the PredictionManager's global policy.")]
        [SerializeField] private DesyncPolicyOverride _desyncPolicy = DesyncPolicyOverride.Inherit;

        /// <summary>
        /// Per-identity override of the PredictionManager's global desync policy.
        /// Resolved once during Setup; changing it after the identity is set up has no effect.
        /// </summary>
        public DesyncPolicyOverride desyncPolicy
        {
            get => _desyncPolicy;
            set => _desyncPolicy = value;
        }

        public override bool isDeterministic => true;

        protected virtual void Simulate(ref STATE state, sfloat delta) { }

        protected virtual void LateSimulate(ref STATE state, sfloat delta) { }

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, ulong baselineTick)
        {
            return WritePredictionMetadata(packer, baselineTick, in fullPredictedState.prediction);
        }

        internal override void ReadState(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick)
        {
            PredictedIdentityState prediction = default;
            ReadPredictionMetadata(packer, baselineTick, serverTick, ref prediction);

            if (_stateHistory.ReadOrPrevious(tick, out var stateAtTick))
            {
                var verified = stateAtTick.DeepCopy();
                verified.prediction = prediction;
                WriteOwnedStateIfChanged(tick, ref verified);
            }
            else
            {
                fullPredictedState.prediction = prediction;
                SetOwner(prediction.owner);
            }
        }

        internal override bool HasUnchangedStateBaseline(ulong baselineTick)
            => HasPredictionMetadataBaseline(baselineTick);

        internal override void ReadUnchangedState(
            ulong tick,
            ulong baselineTick,
            ulong serverTick)
        {
            if (!TryGetPredictionMetadataBaseline(baselineTick, out var prediction))
            {
                throw new InvalidOperationException(
                    $"Missing deterministic metadata baseline at tick {baselineTick}.");
            }

            StoreVerifiedMetadata(serverTick, in prediction);

            if (_stateHistory.ReadOrPrevious(tick, out var stateAtTick))
            {
                var verified = stateAtTick.DeepCopy();
                verified.prediction = prediction;
                WriteOwnedStateIfChanged(tick, ref verified);
            }
            else
            {
                fullPredictedState.prediction = prediction;
                SetOwner(prediction.owner);
            }
        }

        internal override bool TryGetDeterministicStateHash(ulong tick, out ushort hash)
        {
            hash = 0;
            if (_stateHistory == null || !_stateHistory.ReadOrPrevious(tick, out var stateAtTick))
                return false;

            using var packer = BitPackerPool.Get();
            Packer<STATE>.Write(packer, stateAtTick.state);
            hash = DeterministicStateHash.Compute(packer, tick);
            return true;
        }

        internal override string GetDeterministicStateString(ulong tick)
        {
            if (_stateHistory == null || !_stateHistory.ReadOrPrevious(tick, out var stateAtTick))
                return "<no state history at tick>";
            return stateAtTick.state.ToString();
        }

        public PredictedHierarchy hierarchy { get; private set; }

        public override string ToString()
        {
            return currentState.ToString();
        }

        internal override void ClearFuture(ulong stateTick)
        {
            _stateHistory.ClearFuture(stateTick);
        }

        private InterpolatedWithDispose<FULL_STATE<STATE>> _interpolatedState;
        private History<FULL_STATE<STATE>> _stateHistory;

        protected TickManager tickModule { get; private set; }

        public override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
        }

        public override void ResetState()
        {
            base.ResetState();
            DisposeStateStorage();
            _firstViewUpdate = true;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            DisposeStateStorage();
        }

        internal override void ReleasePredictionStateForPool()
        {
            base.ReleasePredictionStateForPool();
            DisposeStateStorage();
        }

        private void DisposeStateStorage()
        {
            _viewState?.Dispose();
            _viewState = null;

            _interpolatedState?.Teleport(default);
            _stateHistory?.Clear();

            fullPredictedState.Dispose();
            fullPredictedState = default;
            viewState = default;
        }

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate) { }

        private FULL_STATE<STATE> FULLInterpolate(FULL_STATE<STATE> from, FULL_STATE<STATE> to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            return new FULL_STATE<STATE>
            {
                state = state,
                prediction = from.prediction
            };
        }

        internal FULL_STATE<STATE> fullPredictedState;

        public ref STATE currentState
        {
            get => ref fullPredictedState.state;
        }

        internal Type myType;

        private void ResetStateToInitialState()
        {
            fullPredictedState.prediction.wasOnSimulationStartCalled = false;
            fullPredictedState.state.Dispose();
            fullPredictedState.state = GetInitialState();
        }

        protected override void OnOwnerAssigned(PlayerID? player)
        {
            fullPredictedState.prediction.owner = player;
        }

        internal override void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            myType = GetType();
            hierarchy = world.hierarchy;

            resolvedDesyncPolicy = DesyncPolicyResolution.Resolve(_desyncPolicy, world.desyncPolicy);

            base.Setup(manager, world, id, owner);

            tickModule = manager.tickModule;

            if (tickModule == null)
                return;

            ResetStateToInitialState();
            GetLatestUnityState();

            var interpolationBuffer = PredictionManager.GetViewInterpolationMaxBufferSize(world.tickRate);

            if (_interpolatedState == null)
            {
                _interpolatedState = new InterpolatedWithDispose<FULL_STATE<STATE>>(
                    FULLInterpolate, 1f / world.tickRate, fullPredictedState.DeepCopy(), interpolationBuffer);
            }
            else
                _interpolatedState.Teleport(fullPredictedState.DeepCopy());

            _viewState?.Dispose();
            _viewState = null;

            if (_stateHistory == null)
                _stateHistory = new History<FULL_STATE<STATE>>(world.tickRate * 10);
            else _stateHistory.Clear();
            _stateHistory.Write(0, fullPredictedState.DeepCopy());
        }

        protected virtual void GetUnityState(ref STATE state) {}

        internal override void GetLatestUnityState()
        {
            fullPredictedState.prediction.owner = owner;
            GetUnityState(ref fullPredictedState.state);
        }

        protected virtual void SimulationStart() {}

        internal override void SimulateTick(ulong tick, float delta)
        {
            using (simulateMarker.Auto())
            {
                if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
                {
                    SimulationStart();
                    fullPredictedState.prediction.wasOnSimulationStartCalled = true;
                }

                Simulate(ref fullPredictedState.state, sfloat.FromFloat(delta));
            }
        }

        internal override void LateSimulateTick(float delta)
            => LateSimulate(ref fullPredictedState.state, sfloat.FromFloat(delta));

        internal override void SaveStateInHistory(ulong tick)
        {
            _stateHistory.PruneByTickWindow(tick);
            _stateHistory.Write(tick, fullPredictedState.DeepCopy());
        }

        private bool LatestHistoryMatches(ulong tick, ref FULL_STATE<STATE> state)
        {
            if (_stateHistory == null || _stateHistory.Count <= 0)
                return false;

            _stateHistory.PruneByTickWindow(tick);

            int lastIndex = _stateHistory.Count - 1;
            if (_stateHistory.GetEntryTick(lastIndex) > tick)
                return false;

            var last = _stateHistory[lastIndex];
            return last.HasSameContents(ref state);
        }

        private void WriteOwnedStateIfChanged(ulong tick, ref FULL_STATE<STATE> state)
        {
            if (LatestHistoryMatches(tick, ref state))
            {
                state.Dispose();
                state = default;
                return;
            }

            _stateHistory.Write(tick, state);
        }

        FULL_STATE<STATE>? _viewState;

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError)
        {
            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            bool refreshOnly = predictionManager && predictionManager.refreshViewLatchOnly;
            if (!PredictionManager.ShouldReplaceViewLatch(refreshOnly, _viewState.HasValue))
            {
                copy.Dispose();
                return;
            }

            _viewState?.Dispose();
            _viewState = copy;
        }

        protected virtual void ModifyRollbackViewState(ref STATE state, float delta, bool accumulateError) { }

        protected virtual STATE GetInitialState() => default;

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.ReadOrPrevious(tick, out var state))
                return;

            fullPredictedState.Dispose();
            fullPredictedState = state.DeepCopy();

            SetOwner(fullPredictedState.prediction.owner);
            SetUnityState(fullPredictedState.state);
        }

        protected virtual void SetUnityState(STATE state) {}

        internal override void WriteFirstState(ulong tick, BitPacker packer)
        {
            if (!_stateHistory.ReadOrPrevious(tick, out var state))
            {
                PurrLogger.LogError($"Failed to write first state for tick {tick}");
                return;
            }

            RefreshMetadataLedger(tick, in state.prediction);
            Packer<PredictedIdentityState>.Write(packer, state.prediction);
            Packer<STATE>.Write(packer, state.state);
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            PredictedIdentityState prediction = default;
            STATE state = default;

            Packer<PredictedIdentityState>.Read(packer, ref prediction);
            Packer<STATE>.Read(packer, ref state);
            StoreVerifiedMetadata(serverTick, in prediction);

            FULL_STATE<STATE> newState = new FULL_STATE<STATE>
            {
                state = state,
                prediction = prediction
            };
            WriteOwnedStateIfChanged(tick, ref newState);
        }

        internal override void QueueInput(BitPacker packer, PlayerID sender) { }

        public STATE viewState;

        public STATE? verifiedState
        {
            get
            {
                if (lastVerifiedTick.HasValue && _stateHistory.ReadOrPrevious(lastVerifiedTick.Value, out var state))
                    return state.state;
                return null;
            }
        }

        internal override void LateUpdateView(float deltaTime)
        {
            LateUpdateView(viewState, verifiedState);
        }

        private bool _firstViewUpdate = true;

        internal override void UpdateView(float deltaTime)
        {
            base.UpdateView(deltaTime);

            if (_interpolatedState == null)
                return;

            if (_viewState.HasValue)
            {
                int depthBeforeAdd = _interpolatedState.bufferSize;
                _interpolatedState.Add(_viewState.Value);
                if (_interpolatedState.bufferSize <= depthBeforeAdd && predictionManager)
                    predictionManager.ReportViewBufferTrim();
                _viewState = null;
            }

            viewState = _interpolatedState.Advance(deltaTime).state;

            if (_interpolatedState.bufferSize == 0 && predictionManager)
                predictionManager.ReportViewBufferStarved();

            if (_firstViewUpdate)
            {
                ViewStart(viewState, verifiedState);
                _firstViewUpdate = false;
            }

            UpdateView(viewState, verifiedState);
        }

        protected virtual void ViewStart(STATE viewState, STATE? verified) {}

        protected virtual void UpdateView(STATE viewState, STATE? verified) {}

        protected virtual void LateUpdateView(STATE viewState, STATE? verified) {}

        /// <summary>
        /// Produces a transient, non-owning view state. Implementations must not allocate
        /// disposable members in the returned value.
        /// </summary>
        protected virtual STATE Interpolate(STATE from, STATE to, float t)
        {
            var offset = to.Add(to, from.Negate(from));
            var scaled = offset.Scale(offset, t);
            return from.Add(from, scaled);
        }

        public override void ReadFirstInput(ulong localTick, BitPacker packer) {}

        public override void WriteFirstInput(ulong localTick, BitPacker packer) {}
    }
}
