using System;
using System.Runtime.CompilerServices;
using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract class PredictedModule<TState> : PredictedModule where TState : struct, IPredictedData<TState>
    {
        internal MODULE_STATE<TState> fullPredictedState;

        /// <summary>
        /// The current simulation relevant state
        /// </summary>
        public ref TState currentState => ref fullPredictedState.state;

        private History<MODULE_STATE<TState>> _history;

        private InterpolatedWithDispose<MODULE_STATE<TState>> _interpolatedState;
        private MODULE_STATE<TState>? _viewState;

        public TState viewState;

        /// <summary>
        /// The last fully verified state received from the server (or authoritative state if local).
        /// Returns null if no history exists yet.
        /// </summary>
        public TState? verifiedState => _history.Count > 0 ? _history[^1].state : null;

        public PredictedModule(PredictedIdentity identity) : base(identity) { }

        private ModuleDeltaKey<ModulePredictedState> predictionKey => new ModuleDeltaKey<ModulePredictedState>(identity.id, moduleIndex);
        protected ModuleDeltaKey<TState> stateKey => new ModuleDeltaKey<TState>(identity.id, moduleIndex);

        public override string ToString()
        {
            return $"State:\n{fullPredictedState.state}";
        }

        private void ResetStateToInitialState()
        {
            fullPredictedState.prediction.wasOnSimulationStartCalled = false;
            fullPredictedState.state.Dispose();
            fullPredictedState.state = GetInitialState();
        }

        internal override void OnCoreInitialize()
        {
            var tickRate = predictionManager.tickRate;
            var bufferSize = (int)Math.Max(tickRate / 10f, 2);

            _history = new History<MODULE_STATE<TState>>(tickRate * 10);

            _interpolatedState = new InterpolatedWithDispose<MODULE_STATE<TState>>(
                FULLInterpolate,
                1f / tickRate,
                fullPredictedState.DeepCopy(),
                bufferSize
            );
        }

        protected override void Setup(PredictedIdentity parent, PredictionManager world)
        {
            base.Setup(parent, world);

            bool preserveInterpolation = parent.preservesStateOnSetup && _interpolatedState != null && _history != null;

            if (preserveInterpolation && parent.UsesSoftCorrectionTimeline())
                return;

            ResetStateToInitialState();
            _history?.Clear();
            _viewState?.Dispose();
            _viewState = null;

            if (!preserveInterpolation)
                _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
        }

        protected sealed override void UpdateView(float delta)
        {
            if (_interpolatedState == null) return;

            if (_viewState.HasValue)
            {
                _interpolatedState.Add(_viewState.Value);
                _viewState = null;
            }

            var result = _interpolatedState.Advance(delta);
            viewState = result.state;

            UpdateView(viewState, verifiedState);
        }

        protected virtual void UpdateView(TState viewState, TState? verifiedState) { }

        protected virtual TState Interpolate(TState from, TState to, float t)
        {
            var offset = to.Add(to, from.Negate(from));
            var scaled = offset.Scale(offset, t);
            return from.Add(from, scaled);
        }

        private MODULE_STATE<TState> FULLInterpolate(MODULE_STATE<TState> from, MODULE_STATE<TState> to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            return new MODULE_STATE<TState>
            {
                state = state,
                prediction = from.prediction
            };
        }

        protected override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
        }

        protected override void UpdateInterpolation(float delta, bool accumulateError)
        {
            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            _viewState?.Dispose();
            _viewState = copy;
        }

        protected virtual void ModifyRollbackViewState(ref TState state, float delta, bool accumulateError) { }

        protected override void Simulate(ulong tick, float delta)
        {
            if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
            {
                SimulationStart();
                fullPredictedState.prediction.wasOnSimulationStartCalled = true;
            }
            Simulate(ref fullPredictedState.state, delta);
        }

        protected virtual void SimulationStart() { }

        protected virtual TState GetInitialState() => default;

        protected virtual void Simulate(ref TState state, float delta) { }

        protected override void Rollback(ulong tick)
        {
            if (_history.ReadOrPrevious(tick, out var result))
            {
                fullPredictedState.Dispose();
                fullPredictedState = result.DeepCopy();
            }
        }

        protected override void SaveState(ulong tick)
        {
            _history.Write(tick, fullPredictedState.DeepCopy());
        }

        protected override bool WriteState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
        {
            int flagPos = packer.AdvanceBits(1);

            bool changed = deltaModule.WriteReliable(packer, receiver, predictionKey, fullPredictedState.prediction);
            changed |= deltaModule.WriteReliable(packer, receiver, stateKey, fullPredictedState.state);

            packer.WriteAt(flagPos, changed);

            if (!changed)
                packer.SetBitPosition(flagPos + 1);

            return changed;
        }

        protected override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            int pos = packer.positionInBits;
            bool changed = Packer<bool>.Read(packer);
            MODULE_STATE<TState> newState = default;

            if (changed)
            {
                deltaModule.ReadReliable(packer, predictionKey, ref newState.prediction);
            }
            else
            {
                packer.SetBitPosition(pos);
                deltaModule.ReadReliable(packer, predictionKey, ref newState.prediction);
                packer.SetBitPosition(pos);
            }

            deltaModule.ReadReliable(packer, stateKey, ref newState.state);

            if (identity.UsesSoftCorrectionTimeline())
            {
                if (_history.Read(tick, out var predictedAtTick))
                {
                    OnVerifiedStateReceived(tick, in predictedAtTick.state, in newState.state);
                }
                else
                {
                    fullPredictedState.Dispose();
                    fullPredictedState = newState.DeepCopy();
                    ResetInterpolation();
                }

                _history.Write(tick, newState);
                return;
            }

            fullPredictedState.Dispose();
            fullPredictedState = newState;
            _history.Write(tick, fullPredictedState.DeepCopy());
        }

        protected virtual void OnVerifiedStateReceived(ulong tick, in TState predicted, in TState verified) { }

        protected override void WriteFirstState(ulong tick, BitPacker packer)
        {
            var savedState = fullPredictedState;

            if (tick > 0 && _history.ReadOrPrevious(tick, out var historyState))
                savedState = historyState;

            Packer<ModulePredictedState>.Write(packer, savedState.prediction);
            Packer<TState>.Write(packer, savedState.state);
        }

        protected override void ReadFirstState(ulong tick, BitPacker packer)
        {
            MODULE_STATE<TState> newState = default;
            Packer<ModulePredictedState>.Read(packer, ref newState.prediction);
            Packer<TState>.Read(packer, ref newState.state);
            fullPredictedState.Dispose();
            fullPredictedState = newState;
            _history.Write(tick, fullPredictedState.DeepCopy());
        }

        protected override void ClearFuture(ulong tick)
        {
            _history.ClearFuture(tick);
        }

        protected override void OnDisposed()
        {
            base.OnDisposed();
            _history?.Clear();
            _interpolatedState?.Teleport(default);
            _interpolatedState = null;
            fullPredictedState.Dispose();
        }
    }
}
