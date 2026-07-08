using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;

namespace PurrNet.Prediction
{
    public abstract partial class PredictedIdentity
    {
        internal void RunSimulateTick(ulong tick, float delta)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            SimulateModules(tick, delta);
            SimulateTick(tick, delta);
        }

        internal void RunLateSimulateTick(float delta)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            LateSimulateModules(delta);
            LateSimulateTick(delta);
        }

        internal void RunPrepareSimulationInputs(ulong tick, float delta)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            OnPrepareSimulationInputs(tick, delta);
        }

        internal void RunPostSimulate()
        {
            if (SkipsCurrentSimulationPhase())
                return;

            PostSimulate();
        }

        internal void RunGetLatestUnityState()
        {
            if (SkipsCurrentSimulationPhase())
                return;

            GetLatestUnityState();
        }

        internal void RunUpdateView(float deltaTime)
        {
            UpdateView(deltaTime);
            UpdateModuleView(deltaTime);
        }

        internal void RunLateUpdateView(float deltaTime)
        {
            LateUpdateView(deltaTime);
            LateUpdateModuleView(deltaTime);
        }

        internal void RunRollback(ulong tick)
        {
            RollbackDynamicModules(tick);
            RollbackModules(tick);
            Rollback(tick);
        }

        internal void RunSaveState(ulong tick)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            PredictionHistoryTelemetry.RecordSave(isEventHandler);
            SaveModulesState(tick);
            SaveStateInHistory(tick);
            SaveDynamicModuleSnapshot(tick);
        }

        internal void RunUpdateRollbackInterpolation(float delta, bool accumulateError)
        {
            bool shouldAccumulateError = accumulateError && AccumulatesRollbackInterpolationError();
            UpdateModulesInterpolation(delta, shouldAccumulateError);
            UpdateRollbackInterpolationState(delta, shouldAccumulateError);
        }

        internal void RunResetInterpolation()
        {
            ResetModulesInterpolation();
            ResetInterpolation();
        }

        internal bool RunWriteCurrentState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
        {
            bool moduleSetChanged = WriteDynamicModuleSnapshot(receiver, packer, deltaModule);
            bool modulesChanged = WriteModules(receiver, packer, deltaModule);
            bool identityChanged = WriteCurrentState(receiver, packer, deltaModule);

            return moduleSetChanged || modulesChanged || identityChanged;
        }

        internal void RunReadState(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            ReadDynamicModuleSnapshot(tick, packer, deltaModule);
            ReadModules(tick, packer, deltaModule);
            ReadState(tick, packer, deltaModule);
        }

        internal void RunWriteFirstState(ulong tick, BitPacker packer)
        {
            WriteFirstDynamicModuleSnapshot(tick, packer);
            WriteFirstStateModules(tick, packer);
            WriteFirstState(tick, packer);
        }

        internal void RunReadFirstState(ulong tick, BitPacker packer)
        {
            ReadFirstDynamicModuleSnapshot(tick, packer);
            ReadFirstStateModules(tick, packer);
            ReadFirstState(tick, packer);
        }

        internal void RunClearFuture(ulong tick)
        {
            ClearFutureDynamicModules(tick);
            ClearFutureModules(tick);
            ClearFuture(tick);
        }
    }
}
