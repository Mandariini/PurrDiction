using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public class PredictedEventTests
    {
        private GameObject _worldObject;
        private GameObject _identityObject;
        private PredictionManager _world;
        private EventTestIdentity _identity;
        private uint _pass;

        [SetUp]
        public void SetUp()
        {
            _worldObject = new GameObject("Event world");
            _world = _worldObject.AddComponent<PredictionManager>();
            _world.hasStableLocalPlayer = true;
            _world.stableLocalPlayerId = 1;
            Set("<tickRate>k__BackingField", 20);
            _identityObject = new GameObject("Event identity");
            _identity = _identityObject.AddComponent<EventTestIdentity>();
            _identity.owner = new PlayerID(1, false);
            _identity.Attach(_world, 1);
            _pass = 0;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_identityObject);
            Object.DestroyImmediate(_worldObject);
        }

        [Test]
        public void VerifiedOnlyRespawnReachesOwningClient()
        {
            Assert.That(_identity.IsOwner(), Is.True);
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12, verified: true, replaying: true);
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void RealVerifiedSimulationPassDeliversOwnerEventOnce()
        {
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            _world.onBeforePhysicsPass += ev.Invoke;
            Pass(12, verified: true, replaying: true);
            Set("_physicsProvider", default(PredictionPhysicsProvider));
            // A real verified pass requires a received transcript, even with no input identities.
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(PredictionManager).GetMethod("BeginVerifiedInputTranscript", flags)
                .Invoke(_world, new object[] { 12UL, 12UL });
            typeof(PredictionManager).GetMethod("BeginVerifiedInputTick", flags)
                .Invoke(_world, null);
            var simulate = typeof(PredictionManager).GetMethod("SimulateFrame",
                flags);
            var parameters = simulate.GetParameters();
            var args = new[] { (object)12UL,
                System.Enum.Parse(parameters[1].ParameterType, "None"),
                System.Enum.Parse(parameters[2].ParameterType, "Verified") };
            try
            {
                simulate.Invoke(_world, args);
                simulate.Invoke(_world, args);
                Assert.That(calls, Is.EqualTo(1));
            }
            finally
            {
                typeof(PredictionManager).GetMethod("ClearVerifiedInputTranscript", flags)
                    .Invoke(_world, null);
            }
        }

        [Test]
        public void TwoPredictedInvocationsDoNotFireAgainOnVerification()
        {
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12);
            ev.Invoke();
            ev.Invoke();
            Pass(12, verified: true, replaying: true);
            ev.Invoke();
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void RemoteClientWaitsForVerification()
        {
            _world.stableLocalPlayerId = 2;
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12);
            ev.Invoke();
            Assert.That(calls, Is.Zero);
            Pass(12, verified: true, replaying: true);
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void CatchUpDoesNotConsumeVerifiedOnlyEvent()
        {
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12, verified: true, replaying: true, catchingUp: true);
            ev.Invoke();
            Assert.That(calls, Is.Zero);
            Pass(12, verified: true, replaying: true);
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void ExtraVerifiedOccurrenceIsDeliveredWithoutRepeatingPredictedOccurrence()
        {
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12);
            ev.Invoke();
            Pass(12, verified: true, replaying: true);
            ev.Invoke();
            ev.Invoke();
            Pass(12, verified: true, replaying: true);
            ev.Invoke();
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void DefaultModeDoesNotPresentNewSpeculativeReplayOccurrence()
        {
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12, replaying: true);
            ev.Invoke();
            Assert.That(calls, Is.Zero);
            Pass(12, verified: true, replaying: true);
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void ViewModePresentsRemotePredictionOnlyOnceAcrossReplaysAndVerification()
        {
            _world.stableLocalPlayerId = 2;
            var ev = new PredictedEvent<int, string>(_world, _identity, PredictedEventMode.View);
            int calls = 0;
            ev.AddListener((value, text) => { Assert.That(value, Is.EqualTo(7)); Assert.That(text, Is.EqualTo("hit")); calls++; });
            Pass(12);
            ev.Invoke(7, "hit");
            Assert.That(calls, Is.EqualTo(1));
            Pass(12, replaying: true);
            ev.Invoke(7, "hit");
            Pass(12, verified: true, replaying: true);
            ev.Invoke(7, "hit");
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void ViewModeCanFirstAppearDuringSpeculativeReplay()
        {
            var ev = new PredictedEvent(_world, _identity, PredictedEventMode.View);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12, replaying: true);
            ev.Invoke();
            Pass(12, replaying: true);
            ev.Invoke();
            Pass(12, verified: true, replaying: true);
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void NewTickAndNewIdentityLifetimeCanPresentAgain()
        {
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12);
            ev.Invoke();
            Pass(13);
            ev.Invoke();
            _identity.Attach(_world, 2);
            Pass(12);
            ev.Invoke();
            ev.ResetDeliveryHistory();
            Pass(12);
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(4));
        }

        [Test]
        public void PrunedOccurrencesStaySuppressed()
        {
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12);
            ev.Invoke();
            Pass(200);
            ev.Invoke();
            Pass(12, verified: true, replaying: true);
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void ServerKeepsEveryInvocationAndCatchUpSuppressesPresentation()
        {
            var ev = new PredictedEvent(_world, _identity);
            int calls = 0;
            ev.AddListener(() => calls++);
            Pass(12, verified: true, server: true);
            ev.Invoke();
            ev.Invoke();
            Pass(13, verified: true, server: true, catchingUp: true);
            ev.Invoke();
            Assert.That(calls, Is.EqualTo(2));
        }

        private void Pass(ulong tick, bool verified = false, bool replaying = false,
            bool catchingUp = false, bool server = false)
        {
            Set("<spawnPass>k__BackingField", ++_pass);
            Set("<localTickInContext>k__BackingField", tick);
            Set("<localTick>k__BackingField", tick);
            Set("<isSimulating>k__BackingField", true);
            Set("<isVerified>k__BackingField", verified);
            Set("<isReplaying>k__BackingField", replaying);
            Set("<isCatchingUpFrames>k__BackingField", catchingUp);
            Set("<cachedIsServer>k__BackingField", server);
        }

        private void Set(string field, object value)
            => typeof(PredictionManager).GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_world, value);
    }

    public class EventTestIdentity : StatelessPredictedIdentity
    {
        public void Attach(PredictionManager world, uint objectId)
        {
            predictionManager = world;
            id = new PredictedComponentID(new PredictedObjectID(objectId), 0);
        }
    }
}
