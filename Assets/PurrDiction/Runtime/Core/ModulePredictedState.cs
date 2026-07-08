using System;
using PurrNet.Packing;
using Unity.Profiling;

namespace PurrNet.Prediction
{
    internal struct ModulePredictedState : IPredictedData<ModulePredictedState>
    {
        public bool wasOnSimulationStartCalled;

        public void Dispose() { }
    }

    internal struct MODULE_STATE<T> : IDisposable, IPackedAuto
        where T : struct, IPredictedData<T>
    {
        public T state;
        public ModulePredictedState prediction;

        static readonly ProfilerMarker SimulateMarker = new("DeepCopy.Module." + typeof(T).FullName);

        public MODULE_STATE<T> DeepCopy()
        {
            using (SimulateMarker.Auto())
            {
                return new MODULE_STATE<T>
                {
                    state = PurrCopy<T>.Copy(state),
                    prediction = prediction
                };
            }
        }

        public void Dispose()
        {
            state.Dispose();
            prediction.Dispose();
        }
    }
}
