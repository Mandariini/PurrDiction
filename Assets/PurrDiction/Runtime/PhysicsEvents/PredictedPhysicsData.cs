using PurrNet.Packing;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedPhysicsData : IPredictedData<PredictedPhysicsData>, IDuplicate<PredictedPhysicsData>
    {
        public DisposableList<PhysicsEvent> events;

        public PredictedPhysicsData Duplicate()
        {
            return new PredictedPhysicsData
            {
                events = events.isDisposed ? default : events.Count == 0
                    ? DisposableList<PhysicsEvent>.Create(0) : events.Duplicate()
            };
        }

        public void Dispose()
        {
            if (events.isDisposed)
                return;

            int count = events.Count;
            for (var i = 0; i < count; i++)
                events[i].Dispose();
            events.Dispose();
        }

        public override string ToString()
            => events.isDisposed ? "{events=<disposed>}" : $"{{events({events.Count})={events}}}";
    }
}
