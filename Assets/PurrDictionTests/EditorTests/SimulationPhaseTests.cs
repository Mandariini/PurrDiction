using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class SimulationPhaseTests
    {
        private static byte PhasesOf<T>() where T : PredictedIdentity
        {
            var gameObject = new GameObject(typeof(T).Name);
            try
            {
                return gameObject.AddComponent<T>().simulationPhases;
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void EmptyDefaultHooksAreSkipped()
        {
            Assert.That(PhasesOf<PhasePlainProbe>(), Is.Zero);
            Assert.That(PhasesOf<PhasePlainInputProbe>(), Is.Zero);
        }

        [Test]
        public void OverriddenHooksRun()
        {
            Assert.That(PhasesOf<PhaseLateProbe>(), Is.EqualTo(PredictedIdentity.LateSimulatePhase));
            Assert.That(PhasesOf<PhaseLateInputProbe>(), Is.EqualTo(PredictedIdentity.LateSimulatePhase));
            Assert.That(PhasesOf<PhasePostProbe>(), Is.EqualTo(PredictedIdentity.PostSimulatePhase));
            // Overriding the stateless LateSimulate of a state-and-input type does not reach LateSimulateTick.
            Assert.That(PhasesOf<PhaseShadowedLateProbe>(), Is.Zero);
        }

        [Test]
        public void AddingAModuleTurnsOnLateSimulateForARegisteredSystem()
        {
            var managerObject = new GameObject("phase manager");
            var identityObject = new GameObject("phase identity");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var identity = identityObject.AddComponent<PolicyProbe>();
                identity.AttachForTest(manager);
                var systems = (PredictedSystemList)typeof(PredictionManager)
                    .GetField("_systems", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
                systems.Add(identity);
                int index = systems.IndexOf(identity);
                Assert.That(systems.Runs(index, PredictedIdentity.LateSimulatePhase), Is.False);

                _ = new ModuleHistoryProbe(identity);

                Assert.That(systems.Runs(index, PredictedIdentity.LateSimulatePhase), Is.True,
                    "the module's LateSimulate runs from the tick it is added");
                systems.Remove(identity);
                identity.DetachForTest();
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }
    }

    public sealed class PhasePlainProbe : PredictedIdentity<EmptyState> { }

    public sealed class PhaseLateProbe : PredictedIdentity<EmptyState>
    {
        protected override void LateSimulate(ref EmptyState state, float delta) { }
    }

    public sealed class PhasePostProbe : PredictedIdentity<EmptyState>
    {
        public override void PostSimulate() { }
    }

    public sealed class PhasePlainInputProbe : PredictedIdentity<ServerPredictionWindowInput, EmptyState>
    {
        protected override void Simulate(ServerPredictionWindowInput input, ref EmptyState state, float delta) { }
    }

    public sealed class PhaseLateInputProbe : PredictedIdentity<ServerPredictionWindowInput, EmptyState>
    {
        protected override void Simulate(ServerPredictionWindowInput input, ref EmptyState state, float delta) { }
        protected override void LateSimulate(ServerPredictionWindowInput input, ref EmptyState state, float delta) { }
    }

    public sealed class PhaseShadowedLateProbe : PredictedIdentity<ServerPredictionWindowInput, EmptyState>
    {
        protected override void Simulate(ServerPredictionWindowInput input, ref EmptyState state, float delta) { }
        protected override void LateSimulate(ref EmptyState state, float delta) { }
    }
}
