using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedPhysicsData : IPredictedData<PredictedPhysicsData>
    {
        public DisposableList<PhysicsEvent> events;

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
