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
                int window = server ? (int)world.verifiedHistoryWindowTicks : rate * 10;
                Assert.That(history.Capacity, Is.EqualTo(window));
                for (ulong tick = 0; tick <= (ulong)(window * 3); tick++)
                {
                    probe.currentState.a = (int)tick;
                    probe.SaveStateInHistory(tick);
                    probe.lastVerifiedTick = tick;
                    ulong oldest = tick > (ulong)window ? tick - (ulong)window : 0;
                    Assert.That(history.Read(oldest, out var baseline), Is.True);
                    Assert.That(baseline.state.a, Is.EqualTo((int)oldest));
                    Assert.That(probe.verifiedState.Value.a, Is.EqualTo((int)tick));
                }
                Assert.That(((Array)typeof(History<FULL_STATE<CompactStateProbe>>)
                    .GetField("m_values", Fields).GetValue(history)).Length, Is.EqualTo(window + 2));
                probe.Rollback((ulong)(window * 2));
                Assert.That(probe.currentState.a, Is.EqualTo(window * 2));
                probe.PrewarmPredictionState(world);
                Assert.That(typeof(PredictedIdentity<CompactStateProbe>).GetField("_stateHistory", Fields)
                    .GetValue(probe), Is.SameAs(history));
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

    public sealed class ServerPredictionWindowProbe : PredictedIdentity<CompactStateProbe> { }
}
