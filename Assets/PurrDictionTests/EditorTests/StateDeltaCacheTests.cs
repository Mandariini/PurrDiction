using NUnit.Framework;
using PurrNet.Packing;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class StateDeltaCacheTests
    {
        private struct State { public int value; }

        [OneTimeSetUp]
        public void RegisterPackers() => NetworkManager.CallAllRegisters();

        [Test]
        public void UnalignedSuffixIsCopiedExactlyAndDoesNotBorrowTheSourceBuffer()
        {
            using var cache = new StateDeltaCache<State>();
            using var source = BitPackerPool.Get();
            var baseline = new State { value = 3 };
            var current = new State { value = 97 };
            Packer<bool>.Write(source, true);
            Packer<int>.Write(source, current.value - baseline.value);
            cache.Store(20, 10, 1, source, baseline, current);
            source.ResetPositionAndMode(false);
            Packer<int>.Write(source, -123);
            using var destination = BitPackerPool.Get();
            Packer<bool>.Write(destination, false);
            Assert.That(cache.TryWrite(20, 10, destination, ref baseline, ref current), Is.True);
            destination.ResetPositionAndMode(true);
            Assert.That(Packer<bool>.Read(destination), Is.False);
            Assert.That(Packer<int>.Read(destination), Is.EqualTo(94));
        }

        [Test]
        public void MutationPositionNewTickAndResetInvalidateReuse()
        {
            using var cache = new StateDeltaCache<State>();
            using var source = BitPackerPool.Get();
            var baseline = new State { value = 3 };
            var current = new State { value = 97 };
            Packer<int>.Write(source, 94);
            cache.Store(20, 10, 0, source, baseline, current);
            using var destination = BitPackerPool.Get();
            current.value++;
            Assert.That(cache.TryWrite(20, 10, destination, ref baseline, ref current), Is.False);
            current.value--;
            baseline.value++;
            Assert.That(cache.TryWrite(20, 10, destination, ref baseline, ref current), Is.False);
            baseline.value--;
            Packer<bool>.Write(destination, true);
            Assert.That(cache.TryWrite(20, 10, destination, ref baseline, ref current), Is.False);
            destination.ResetPositionAndMode(false);
            Assert.That(cache.TryWrite(21, 10, destination, ref baseline, ref current), Is.False);
            cache.Store(21, 10, 0, source, baseline, current);
            cache.Reset();
            Assert.That(cache.TryWrite(21, 10, destination, ref baseline, ref current), Is.False);
        }

        [Test]
        public void MultipleBaselinesRemainIndependentAndCapacityIsBounded()
        {
            using var cache = new StateDeltaCache<State>();
            using var bits = BitPackerPool.Get();
            var current = new State { value = 100 };
            for (int i = 0; i < 5; i++)
            {
                bits.ResetPositionAndMode(false);
                Packer<int>.Write(bits, 100 - i);
                cache.Store(20, (ulong)(10 + i), 0, bits, new State { value = i }, current);
            }
            var evicted = new State { value = 0 };
            bits.ResetPositionAndMode(false);
            Assert.That(cache.TryWrite(20, 10, bits, ref evicted, ref current), Is.False);
            for (int i = 1; i < 5; i++)
            {
                bits.ResetPositionAndMode(false);
                var baseline = new State { value = i };
                Assert.That(cache.TryWrite(20, (ulong)(10 + i), bits, ref baseline, ref current), Is.True);
                bits.ResetPositionAndMode(true);
                Assert.That(Packer<int>.Read(bits), Is.EqualTo(100 - i));
            }
        }
    }
}
