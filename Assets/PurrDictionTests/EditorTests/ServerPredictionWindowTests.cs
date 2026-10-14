using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class ServerPredictionWindowTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        static void Set(Type type, object target, string field, object value)
            => type.GetField(field, Fields).SetValue(target, value);

        [OneTimeSetUp]
        public void Register() => NetworkManager.CallAllRegisters();

        [TestCase(true, 20)]
        [TestCase(true, 60)]
        [TestCase(true, 240)]
        [TestCase(false, 20)]
        [TestCase(false, 60)]
        [TestCase(false, 240)]
        public void RetainsTheRequiredWindowAndLatestVerifiedViewWithoutGrowth(bool server, int rate)
        {
            var networkObject = new GameObject("window network");
            var worldObject = new GameObject("window world");
            var probeObject = new GameObject("window probe");
            try
            {
                var network = networkObject.AddComponent<NetworkManager>();
                var world = worldObject.AddComponent<PredictionManager>();
                var probe = probeObject.AddComponent<ServerPredictionWindowProbe>();
                Set(typeof(NetworkManager), network, "<isServer>k__BackingField", server);
                Set(typeof(NetworkIdentity), world, "<networkManager>k__BackingField", network);
                Set(typeof(NetworkIdentity), world, "_isSpawnedServer", server);
                Set(typeof(PredictionManager), world, "<tickRate>k__BackingField", rate);
                Set(typeof(PredictedIdentity), probe, "<predictionManager>k__BackingField", world);
                probe.PrewarmPredictionState(world);
                var history = (History<FULL_STATE<CompactStateProbe>>)typeof(PredictedIdentity<CompactStateProbe>)
                    .GetField("_stateHistory", Fields).GetValue(probe);
                var inputs = (History<ServerPredictionWindowInput>)typeof(PredictedIdentity<ServerPredictionWindowInput, CompactStateProbe>)
                    .GetField("_inputHistory", Fields).GetValue(probe);
                int window = server ? (int)world.verifiedHistoryWindowTicks : rate * 10;
                int inputWindow = server ? window : rate * 5;
                Assert.That(history.Capacity, Is.EqualTo(window));
                Assert.That(inputs.Capacity, Is.EqualTo(inputWindow));
                for (ulong tick = 0; tick <= (ulong)(window * 3); tick++)
                {
                    probe.currentState.a = (int)tick;
                    probe.PrepareInput(server, true, tick, false);
                    probe.SaveStateInHistory(tick);
                    probe.lastVerifiedTick = tick;
                    ulong oldest = tick > (ulong)window ? tick - (ulong)window : 0;
                    Assert.That(history.Read(oldest, out var baseline), Is.True);
                    Assert.That(baseline.state.a, Is.EqualTo((int)oldest));
                    Assert.That(probe.verifiedState.Value.a, Is.EqualTo((int)tick));
                    // Client entry-count histories guarantee inputWindow entries;
                    // server tick-window histories additionally retain the cutoff anchor.
                    ulong retainedInputAge = (ulong)(server ? inputWindow : inputWindow - 1);
                    ulong oldestInput = tick > retainedInputAge ? tick - retainedInputAge : 0;
                    Assert.That(inputs.Read(oldestInput, out var input), Is.True);
                    Assert.That(input.sequence, Is.EqualTo((int)oldestInput));
                }
                Assert.That(((Array)typeof(History<FULL_STATE<CompactStateProbe>>)
                    .GetField("m_values", Fields).GetValue(history)).Length, Is.EqualTo(window + 2));
                int inputSlots = server ? inputWindow + 2 : Math.Max(inputWindow + 10, inputWindow + inputWindow / 2) + 1;
                Assert.That(((Array)typeof(History<ServerPredictionWindowInput>)
                    .GetField("m_values", Fields).GetValue(inputs)).Length, Is.EqualTo(inputSlots));
                probe.Rollback((ulong)(window * 2));
                Assert.That(probe.currentState.a, Is.EqualTo(window * 2));
                probe.PrewarmPredictionState(world);
                Assert.That(typeof(PredictedIdentity<CompactStateProbe>).GetField("_stateHistory", Fields)
                    .GetValue(probe), Is.SameAs(history));
                Assert.That(typeof(PredictedIdentity<ServerPredictionWindowInput, CompactStateProbe>)
                    .GetField("_inputHistory", Fields).GetValue(probe), Is.SameAs(inputs));
                Set(typeof(NetworkIdentity), world, "_isSpawnedServer", false);
                Set(typeof(NetworkManager), network, "<isServer>k__BackingField", false);
            }
            finally
            {
                Object.DestroyImmediate(probeObject);
                Object.DestroyImmediate(worldObject);
                Object.DestroyImmediate(networkObject);
            }
        }
    }

    public struct ServerPredictionWindowInput : IPredictedData
    {
        public int sequence;
        public void Dispose() { }
    }

    public sealed class ServerPredictionWindowProbe : PredictedIdentity<ServerPredictionWindowInput, CompactStateProbe>
    {
        protected override void GetFinalInput(ref ServerPredictionWindowInput input) => input.sequence = currentState.a;
        protected override void Simulate(ServerPredictionWindowInput input, ref CompactStateProbe state, float delta) { }
    }
}
