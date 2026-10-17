using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
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

    public sealed class PassRosterTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void OwnershipChangedMidPassAppliesWithinThatPass()
        {
            var networkObject = new GameObject("NetworkManager");
            var managerObject = new GameObject("PredictionManager");
            var firstObject = new GameObject("First");
            var secondObject = new GameObject("Second");
            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var tickManager = new TickManager(20, networkManager, null, false);
                Set(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);
                var manager = managerObject.AddComponent<PredictionManager>();
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", networkManager);
                Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
                manager.SetIsSpawned(true, false);
                var local = new PlayerID(7, false);
                var remote = new PlayerID(8, false);
                Set(typeof(NetworkIdentity), manager, "_localPlayer", (PlayerID?)local);

                var first = firstObject.AddComponent<RosterOwnerProbe>();
                var second = secondObject.AddComponent<RosterOwnerProbe>();
                first.AttachForTest(manager);
                second.AttachForTest(manager);
                first.owner = local;
                second.owner = remote;

                var systems = (PredictedSystemList)typeof(PredictionManager)
                    .GetField("_systems", InstanceFields).GetValue(manager);
                systems.Add(first);
                systems.Add(second);
                Set(typeof(PredictionManager), manager, "_systemsCount", 2);
                Set(typeof(PredictionManager), manager, "<isReplaying>k__BackingField", true);
                Assert.That(first.IsOwner(), Is.True);
                Assert.That(manager.hasStableLocalPlayer, Is.True, "the pass should take the roster path");

                // The first identity hands the second to the local player before the second runs.
                first.grantTo = second;
                first.grantee = local;
                RunPass(manager);
                Assert.That(first.simulations, Is.EqualTo(1));
                Assert.That(second.simulations, Is.EqualTo(1),
                    "a full walk reaches the second identity after it became locally owned");

                // Ownership that holds through the pass keeps the remote identity skipped.
                first.grantTo = null;
                second.owner = remote;
                RunPass(manager);
                Assert.That(first.simulations, Is.EqualTo(2));
                Assert.That(second.simulations, Is.EqualTo(1));

                // Losing ownership mid-pass skips the identity in that pass.
                second.owner = local;
                first.grantTo = second;
                first.grantee = remote;
                RunPass(manager);
                Assert.That(first.simulations, Is.EqualTo(3));
                Assert.That(second.simulations, Is.EqualTo(1));

                systems.Remove(first);
                systems.Remove(second);
                Set(typeof(PredictionManager), manager, "_systemsCount", 0);
                first.DetachForTest();
                second.DetachForTest();
            }
            finally
            {
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(secondObject);
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        private static void RunPass(PredictionManager manager)
        {
            Invoke(manager, "PrepareSimulationInputs", 5UL, 0.05f);
            Invoke(manager, "SimulateSystems", 5UL, 0.05f);
        }

        private static void Invoke(PredictionManager manager, string name, params object[] arguments)
        {
            var method = typeof(PredictionManager).GetMethod(name, InstanceFields);
            Assert.That(method, Is.Not.Null, name);
            method.Invoke(manager, arguments);
        }

        private static void Set(System.Type type, object target, string name, object value)
        {
            var field = type.GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"{type.Name}.{name}");
            field.SetValue(target, value);
        }
    }

    public sealed class RosterOwnerProbe : PredictedIdentity<EmptyState>
    {
        public RosterOwnerProbe grantTo;
        public PlayerID grantee;
        public int simulations;

        public void AttachForTest(PredictionManager manager)
        {
            predictionManager = manager;
            SetPredictionPolicyOverride(PredictionPolicy.PredictedIfOwned);
        }

        public void DetachForTest() => predictionManager = null;

        protected override void Simulate(ref EmptyState state, float delta)
        {
            simulations++;
            if (grantTo)
                grantTo.owner = grantee;
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
