using System;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Packing;
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
        public void Register()
        {
            NetworkManager.CallAllRegisters();
            Packer<ServerPredictionWindowInput>.RegisterWriter((p, v) => Packer<int>.Write(p, v.sequence));
            Packer<ServerPredictionWindowInput>.RegisterReader((BitPacker p, ref ServerPredictionWindowInput v) =>
                v.sequence = Packer<int>.Read(p));
        }

        [TestCase(true, 20)]
        [TestCase(true, 60)]
        [TestCase(true, 240)]
        [TestCase(false, 20)]
        [TestCase(false, 60)]
        [TestCase(false, 240)]
        public void RetainsTheRequiredWindowAndLatestVerifiedViewAcrossPruningAndGrowth(bool server, int rate)
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

        [TestCase(20, false)]
        [TestCase(60, false)]
        [TestCase(240, false)]
        [TestCase(20, true)]
        [TestCase(60, true)]
        [TestCase(240, true)]
        public void AcceptedClientBaselinesReleaseOldSnapshotsWithoutGrowingPrewarmedArrays(int rate, bool sparse)
        {
            using var f = new ClientFixture(rate);
            int initialSlots = f.Slots;
            int initialInputSlots = f.InputSlots;
            using var receivedInput = BitPackerPool.Get();
            for (ulong tick = 0; tick <= (ulong)(rate * 20); tick++)
            {
                ulong verified = tick > 8 ? tick - 8 : 0;
                ulong floor = tick > 16 ? tick - 16 : 0;
                f.Floor(floor);
                f.probe.lastVerifiedTick = verified;
                f.probe.currentState.a = Value(tick);
                if (sparse)
                {
                    // Verified input may keep arriving while local prediction is paused.
                    receivedInput.ResetPositionAndMode(false);
                    Packer<bool>.Write(receivedInput, true);
                    Packer<int>.Write(receivedInput, Value(tick));
                    receivedInput.ResetPositionAndMode(true);
                    f.probe.ReadFirstInput(tick, receivedInput);
                }
                else f.probe.PrepareInput(false, true, tick, false);
                f.probe.SaveStateInHistory(tick);
                Assert.That(f.history.ReadOrPrevious(floor, out var anchor), Is.True);
                Assert.That(anchor.state.a, Is.EqualTo(Value(floor)));
                Assert.That(f.probe.verifiedState.Value.a, Is.EqualTo(Value(verified)));
                Assert.That(f.Slots, Is.EqualTo(initialSlots));
                Assert.That(f.InputSlots, Is.EqualTo(initialInputSlots));
                Assert.That(f.inputs.Read(floor, out var input), Is.True);
                Assert.That(input.sequence, Is.EqualTo(Value(floor)));
                Assert.That(f.history.Count, Is.LessThanOrEqualTo(18));
            }
            ulong finalFloor = (ulong)(rate * 20 - 16);
            f.probe.Rollback(finalFloor);
            Assert.That(f.probe.currentState.a, Is.EqualTo(Value(finalFloor)));
            Assert.That(f.history.Capacity, Is.EqualTo(rate * 10), "unacknowledged history keeps its original limit");
            int Value(ulong tick) => (int)(sparse ? tick / 8 : tick);
        }

        [TestCase(20)]
        [TestCase(60)]
        [TestCase(240)]
        public void DelayedBaselinesGrowClientStorageAndRetainEveryRequiredStateAndInput(int rate)
        {
            using var f = new ClientFixture(rate);
            int initialSlots = f.Slots;
            int initialInputSlots = f.InputSlots;
            int protocolWindow = (int)PredictionManager.VerifiedHistoryWindowTicks(rate);
            Assert.That(initialSlots, Is.LessThan(protocolWindow + 2));
            Assert.That(initialInputSlots, Is.LessThan(protocolWindow + 2));
            ulong backlog = (ulong)protocolWindow - 8;
            for (ulong tick = 0; tick <= backlog * 3; tick++)
            {
                ulong floor = tick > backlog ? tick - backlog : 0;
                ulong verified = tick > backlog - 4 ? tick - (backlog - 4) : 0;
                f.Floor(floor);
                f.probe.lastVerifiedTick = verified;
                f.probe.currentState.a = (int)tick;
                f.probe.PrepareInput(false, true, tick, false);
                f.probe.SaveStateInHistory(tick);

                Assert.That(f.history.Read(floor, out var anchor), Is.True);
                Assert.That(anchor.state.a, Is.EqualTo((int)floor));
                Assert.That(f.inputs.Read(floor, out var input), Is.True);
                Assert.That(input.sequence, Is.EqualTo((int)floor));
                Assert.That(f.probe.verifiedState.Value.a, Is.EqualTo((int)verified));
                Assert.That(f.history.Read(tick, out var latest), Is.True);
                Assert.That(latest.state.a, Is.EqualTo((int)tick));
                if (floor > 0)
                {
                    Assert.That(f.history.Read(floor - 1, out _), Is.False);
                    Assert.That(f.inputs.Read(floor - 1, out _), Is.False);
                }
            }
            Assert.That(f.Slots, Is.GreaterThan(initialSlots));
            Assert.That(f.InputSlots, Is.GreaterThan(initialInputSlots));
            Assert.That(f.history.Capacity, Is.EqualTo(rate * 10));
            Assert.That(f.inputs.Capacity, Is.EqualTo(rate * 5));
            f.probe.Rollback(backlog * 2);
            Assert.That(f.probe.currentState.a, Is.EqualTo((int)(backlog * 2)));
        }

        [TestCase(20)]
        [TestCase(60)]
        [TestCase(240)]
        public void StalledComponentKeepsItsVerifiedAnchorAsOtherComponentsAdvance(int rate)
        {
            using var f = new ClientFixture(rate);
            int initialSlots = f.Slots;
            ulong through = (ulong)(rate * 5 + 40);
            for (ulong tick = 0; tick <= through; tick++)
            {
                f.Floor(tick > 20 ? tick - 4 : 0);
                f.probe.lastVerifiedTick = tick > 10 ? 10UL : tick;
                f.probe.currentState.a = (int)tick;
                f.probe.PrepareInput(false, true, tick, false);
                f.probe.SaveStateInHistory(tick);
                Assert.That(f.probe.verifiedState.Value.a, Is.EqualTo((int)Math.Min(tick, 10)));
            }
            Assert.That(f.history.Read(10, out var anchor), Is.True);
            Assert.That(anchor.state.a, Is.EqualTo(10));
            Assert.That(f.inputs.Read(10, out var inputAnchor), Is.True);
            Assert.That(inputAnchor.sequence, Is.EqualTo(10));
            Assert.That(f.Slots, Is.GreaterThan(initialSlots), "a stalled component grows to retain its unverified states");
            Assert.That(f.Slots, Is.LessThanOrEqualTo(rate * 10 + 2));
            f.probe.Rollback(10);
            Assert.That(f.probe.currentState.a, Is.EqualTo(10));
        }

        sealed class ClientFixture : IDisposable
        {
            readonly GameObject networkObject = new("client history network");
            readonly GameObject worldObject = new("client history world");
            readonly GameObject probeObject = new("client history probe");
            readonly NetworkManager network;
            readonly PredictionManager world;
            public readonly ServerPredictionWindowProbe probe;
            public readonly History<FULL_STATE<CompactStateProbe>> history;
            public readonly History<ServerPredictionWindowInput> inputs;

            public ClientFixture(int rate)
            {
                network = networkObject.AddComponent<NetworkManager>();
                world = worldObject.AddComponent<PredictionManager>();
                probe = probeObject.AddComponent<ServerPredictionWindowProbe>();
                Set(typeof(NetworkManager), network, "<isClient>k__BackingField", true);
                Set(typeof(NetworkIdentity), world, "<networkManager>k__BackingField", network);
                Set(typeof(NetworkIdentity), world, "_isSpawnedClient", true);
                Set(typeof(PredictionManager), world, "<tickRate>k__BackingField", rate);
                Set(typeof(PredictedIdentity), probe, "<predictionManager>k__BackingField", world);
                probe.PrewarmPredictionState(world);
                history = (History<FULL_STATE<CompactStateProbe>>)typeof(PredictedIdentity<CompactStateProbe>)
                    .GetField("_stateHistory", Fields).GetValue(probe);
                inputs = (History<ServerPredictionWindowInput>)typeof(PredictedIdentity<ServerPredictionWindowInput, CompactStateProbe>)
                    .GetField("_inputHistory", Fields).GetValue(probe);
            }

            public int Slots => ((Array)typeof(History<FULL_STATE<CompactStateProbe>>)
                .GetField("m_values", Fields).GetValue(history)).Length;

            public int InputSlots => ((Array)typeof(History<ServerPredictionWindowInput>)
                .GetField("m_values", Fields).GetValue(inputs)).Length;

            public void Floor(ulong tick) => Set(typeof(PredictionManager), world, "_verifiedHistoryBaselineFloor", tick);

            public void Dispose()
            {
                Set(typeof(NetworkIdentity), world, "_isSpawnedClient", false);
                Set(typeof(NetworkManager), network, "<isClient>k__BackingField", false);
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
