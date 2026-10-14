using System;
using NUnit.Framework;
using PurrNet.Packing;

namespace PurrNet.Prediction.Tests.Editor
{
    public struct CompactStateProbe : IPredictedData<CompactStateProbe>
    {
        public int a, b, c, d, e, f, g, h;
        public void Dispose() { }
    }

    public struct LegacyStateSnapshot : IPackedAuto, IDisposable
    {
        public CompactStateProbe state;
        public PredictedIdentityState prediction;
        public void Dispose() { }
    }

    public sealed class CompactIdentityStateTests
    {
        [OneTimeSetUp]
        public void Register() => NetworkManager.CallAllRegisters();

        private static PredictedIdentityState[] Cases()
        {
            var owners = new PlayerID?[] { null, PlayerID.Server, new PlayerID(0, true),
                new PlayerID(57, false), new PlayerID(57, true), new PlayerID(ulong.MaxValue, true) };
            var result = new PredictedIdentityState[owners.Length * 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = new PredictedIdentityState { owner = owners[i / 2], wasOnSimulationStartCalled = (i & 1) != 0 };
            return result;
        }

        private static void Check(PredictedIdentityState expected, PredictedIdentityState actual)
        {
            Assert.That(actual.owner.HasValue, Is.EqualTo(expected.owner.HasValue));
            if (expected.owner.HasValue)
            {
                Assert.That(actual.owner.Value.id.value, Is.EqualTo(expected.owner.Value.id.value));
                Assert.That(actual.owner.Value.isBot, Is.EqualTo(expected.owner.Value.isBot));
            }
            Assert.That(actual.wasOnSimulationStartCalled, Is.EqualTo(expected.wasOnSimulationStartCalled));
        }

        [Test]
        public void CompactMetadataPreservesPublicValuesAndLegacyWire()
        {
            foreach (var value in Cases())
            {
                CompactIdentityState compact = value;
                Check(value, compact);
                using var expected = new BitPacker();
                using var actual = new BitPacker();
                Packer<PredictedIdentityState>.Write(expected, value);
                Packer<CompactIdentityState>.Write(actual, compact);
                Assert.That(new BitData(actual, 0, actual.positionInBits).Equals(
                    new BitData(expected, 0, expected.positionInBits)), Is.True);
                actual.ResetPositionAndMode(true);
                CompactIdentityState read = default;
                Packer<CompactIdentityState>.Read(actual, ref read);
                Check(value, read);
                compact.owner = null;
                Assert.That(compact.owner, Is.Null);
                Assert.That(compact.wasOnSimulationStartCalled, Is.EqualTo(value.wasOnSimulationStartCalled));
            }
        }

        [Test]
        public void CompactDeltasPreserveLegacyFlagsAndDecodedValues()
        {
            foreach (var baseline in Cases())
            foreach (var value in Cases())
            {
                using var expected = new BitPacker();
                using var actual = new BitPacker();
                bool legacyChanged = DeltaPacker<PredictedIdentityState>.Write(expected, baseline, value);
                bool compactChanged = DeltaPacker<CompactIdentityState>.Write(actual, baseline, value);
                Assert.That(compactChanged, Is.EqualTo(legacyChanged));
                Assert.That(new BitData(actual, 0, actual.positionInBits).Equals(
                    new BitData(expected, 0, expected.positionInBits)), Is.True);
                expected.ResetPositionAndMode(true);
                actual.ResetPositionAndMode(true);
                PredictedIdentityState legacyRead = default;
                CompactIdentityState compactRead = default;
                DeltaPacker<PredictedIdentityState>.Read(expected, baseline, ref legacyRead);
                DeltaPacker<CompactIdentityState>.Read(actual, baseline, ref compactRead);
                Check(legacyRead, compactRead);
            }
        }

        [Test]
        public void HistorySnapshotsUseSmallerStorageAndPreserveMetadataEncoding()
        {
            var compactHistory = History<FULL_STATE<CompactStateProbe>>.ForTickWindow(60);
            var legacyHistory = History<LegacyStateSnapshot>.ForTickWindow(60);
            Assert.That(compactHistory.BackingStoragePayloadBytes, Is.LessThan(legacyHistory.BackingStoragePayloadBytes));
            foreach (var metadata in Cases())
            {
                var state = new CompactStateProbe { a = 7, b = int.MinValue, e = 19, h = int.MaxValue };
                var legacy = new LegacyStateSnapshot { state = state, prediction = metadata };
                var compact = new FULL_STATE<CompactStateProbe> { state = state, prediction = metadata };
                compactHistory.Write(7, compact);
                Assert.That(compactHistory.TryGet(7, out var read), Is.True);
                Check(metadata, read.prediction);
                Assert.That(read.state.a, Is.EqualTo(state.a));
                Assert.That(read.state.b, Is.EqualTo(state.b));
                Assert.That(read.state.e, Is.EqualTo(state.e));
                Assert.That(read.state.h, Is.EqualTo(state.h));
                using var expected = new BitPacker();
                using var actual = new BitPacker();
                Packer<PredictedIdentityState>.Write(expected, legacy.prediction);
                Packer<CompactIdentityState>.Write(actual, read.prediction);
                Assert.That(new BitData(actual, 0, actual.positionInBits).Equals(
                    new BitData(expected, 0, expected.positionInBits)), Is.True);
            }
        }
    }
}
