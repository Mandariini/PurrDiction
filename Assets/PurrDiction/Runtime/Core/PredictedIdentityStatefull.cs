using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        protected readonly struct DeltaKey<T, S> : IStableHashable
        {
            private readonly PredictedComponentID id;
            private readonly SceneID scene;

            public DeltaKey(SceneID scene, PredictedComponentID id)
            {
                this.id = id;
                this.scene = scene;
            }

            public uint GetStableHash()
            {
                const uint Off = 2166136261u;
                const uint Pri = 16777619u;
                uint h = Off;
                h = (h ^ Hasher<T>.stableHash) * Pri;
                h = (h ^ Hasher<S>.stableHash) * Pri;
                h = (h ^ id.componentId.value) * Pri;
                h = (h ^ id.objectId.instanceId.value) * Pri;
                h = (h ^ scene.id.value) * Pri;
                return h;
            }
        }

        protected readonly struct DeltaKey<T> : IStableHashable
        {
            private readonly PredictedComponentID id;
            private readonly SceneID scene;

            public DeltaKey(SceneID scene, PredictedComponentID id)
            {
                this.id = id;
                this.scene = scene;
            }

            public uint GetStableHash()
            {
                const uint Off = 2166136261u;
                const uint Pri = 16777619u;
                uint h = Off;
                h = (h ^ Hasher<T>.stableHash) * Pri;
                h = (h ^ id.componentId.value) * Pri;
                h = (h ^ id.objectId.instanceId.value) * Pri;
                h = (h ^ scene.id.value) * Pri;
                return h;
            }
        }

        public PredictedHierarchy hierarchy { get; private set; }

        public override string ToString()
        {
            return currentState.ToString();
        }

        private InterpolatedWithDispose<FULL_STATE<STATE>> _interpolatedState;
        private History<FULL_STATE<STATE>> _stateHistory;

        protected TickManager tickModule { get; private set; }
        private bool _firstViewUpdate = true;


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

        protected Type myType;

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

            base.Setup(manager, world, id, owner);

            tickModule = manager.tickModule;

            if (tickModule == null)
                return;

            bool preserveSoftCorrection = preservesStateOnSetup &&
                                          UsesSoftCorrectionTimeline() &&
                                          _interpolatedState != null &&
                                          _stateHistory != null;

            if (preserveSoftCorrection)
            {
                fullPredictedState.prediction.owner = owner;
                return;
            }

            ResetStateToInitialState();
            GetLatestUnityState();

            var interpolationBuffer = (int)Mathf.Max(world.tickRate / (float)10, 2);

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

                Simulate(ref fullPredictedState.state, delta);
            }
        }

        internal override void LateSimulateTick(float delta)
            => LateSimulate(ref fullPredictedState.state, delta);

        internal override void SaveStateInHistory(ulong tick)
        {
            if (LatestHistoryMatches(tick, ref fullPredictedState))
                return;

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

            _viewState?.Dispose();
            _viewState = copy;
        }

        protected virtual void ModifyRollbackViewState(ref STATE state, float delta, bool accumulateError) { }

        protected virtual STATE GetInitialState() => default;

        protected virtual void Simulate(ref STATE state, float delta) {}

        protected virtual void LateSimulate(ref STATE state, float delta) {}

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.ReadOrPrevious(tick, out var state))
                return;

            fullPredictedState.Dispose();
            fullPredictedState = state.DeepCopy();

            ApplyVerifiedPredictionMetadata(in fullPredictedState.prediction);
            SetUnityState(fullPredictedState.state);
        }

        protected virtual void SetUnityState(STATE state) {}

        private void ApplyVerifiedPredictionMetadata(in PredictedIdentityState prediction)
        {
            SetOwner(prediction.owner);
        }

        protected DeltaKey<STATE> stateKey => new (sceneId, id);

        private DeltaKey<PredictedIdentityState, STATE> internalKey => new (sceneId, id);

        internal override void WriteFirstState(ulong tick, BitPacker packer)
        {
            var savedState = fullPredictedState;

            if (tick > 0 && _stateHistory.ReadOrPrevious(tick, out var state))
                savedState = state;

            Packer<PredictedIdentityState>.Write(packer, savedState.prediction);
            Packer<STATE>.Write(packer, savedState.state);
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer)
        {
            PredictedIdentityState prediction = default;
            STATE state = default;

            Packer<PredictedIdentityState>.Read(packer, ref prediction);
            Packer<STATE>.Read(packer, ref state);

            FULL_STATE<STATE> newState = new FULL_STATE<STATE>
            {
                state = state,
                prediction = prediction
            };
            WriteOwnedStateIfChanged(tick, ref newState);
        }

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            int pos = packer.positionInBits;
            int flagPos = packer.AdvanceBits(1);

            bool changed = deltaModule.WriteReliable(packer, target, internalKey, fullPredictedState.prediction);
            changed = WriteDeltaState(target, packer, deltaModule) || changed;

            packer.WriteAt(flagPos, changed);
            if (!changed)
                packer.SetBitPosition(flagPos + 1);

            TickBandwidthProfiler.OnWroteState(myType, packer.positionInBits - pos, this);
            return changed;
        }

        protected virtual bool WriteDeltaState(PlayerID target, BitPacker packer, DeltaModule deltaModule)
        {
            return deltaModule.WriteReliable(packer, target, stateKey, fullPredictedState.state);
        }

        [UsedImplicitly]
        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            int pos = packer.positionInBits;

            bool changed = Packer<bool>.Read(packer);
            FULL_STATE<STATE> newState = default;

            if (changed)
            {
                deltaModule.ReadReliable(packer, internalKey, ref newState.prediction);
            }
            else
            {
                packer.SetBitPosition(pos);
                deltaModule.ReadReliable(packer, internalKey, ref newState.prediction);
                packer.SetBitPosition(pos);
            }

            ReadDeltaState(packer, deltaModule, ref newState.state);

            if (UsesSoftCorrectionTimeline())
            {
                if (_stateHistory.ReadOrPrevious(tick, out var predictedAtTick))
                {
                    OnVerifiedStateReceived(tick, in predictedAtTick.state, in newState.state);
                    ApplyVerifiedPredictionMetadata(in newState.prediction);
                }
                else
                {
                    fullPredictedState.Dispose();
                    fullPredictedState = newState.DeepCopy();
                    ApplyVerifiedPredictionMetadata(in fullPredictedState.prediction);
                    SetUnityState(fullPredictedState.state);
                    ResetInterpolation();
                }
            }

            WriteOwnedStateIfChanged(tick, ref newState);
            TickBandwidthProfiler.OnReadState(myType, packer.positionInBits - pos, this);
        }

        protected virtual void OnVerifiedStateReceived(ulong tick, in STATE predicted, in STATE verified) { }

        protected virtual void ReadDeltaState(BitPacker packer, DeltaModule deltaModule, ref STATE state)
        {
            deltaModule.ReadReliable(packer, stateKey, ref state);
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable) { }

        internal override void ReadInput(ulong tick,  PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable) { }

        internal override void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable) { }

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

        internal override void UpdateView(float deltaTime)
        {
            base.UpdateView(deltaTime);

            if (_interpolatedState == null)
                return;

            if (_viewState.HasValue)
            {
                _interpolatedState.Add(_viewState.Value);
                _viewState = null;
            }

            viewState = _interpolatedState.Advance(deltaTime).state;

            if (_firstViewUpdate)
            {
                ViewStart(viewState, verifiedState);
                _firstViewUpdate = false;
            }

            UpdateView(viewState, verifiedState);
        }

        protected virtual void LateUpdateView(STATE viewState, STATE? verified) {}

        protected virtual void ViewStart(STATE viewState, STATE? verified) {}

        protected virtual void UpdateView(STATE viewState, STATE? verified) {}

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

        internal override void ClearFuture(ulong stateTick)
        {
            _stateHistory.ClearFuture(stateTick);
        }

        public override void ReadFirstInput(ulong localTick, BitPacker packer) {}

        public override void WriteFirstInput(ulong localTick, BitPacker packer) {}
    }
}
