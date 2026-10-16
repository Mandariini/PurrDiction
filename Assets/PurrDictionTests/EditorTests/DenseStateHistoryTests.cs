using System;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class DenseStateHistoryTests
    {
        private struct Value : IDisposable
        {
            public int id;
            public void Dispose() { }
        }

        [Test]
        public void EveryLookupMatchesTheSparseHistory([Range(1, 8)] int seed)
        {
            var random = new Random(seed);
            var dense = new DenseStateHistory<Value>(8, 600);
            int slot = dense.AllocateSlot();
            // A neighbouring slot shares the rows and must never leak into the probed one.
            int neighbour = dense.AllocateSlot();
            var sparse = new History<Value>();
            ulong floor = 0;
            ulong head = 0;
            int nextId = 1;

            for (int step = 0; step < 4000; step++)
            {
                int op = random.Next(100);
                if (op < 55)
                {
                    // Mostly the next tick, sometimes a gap, an overwrite, an earlier tick or a far jump.
                    ulong tick = head + 1;
                    int shape = random.Next(20);
                    if (shape == 0) tick = head + (ulong)random.Next(2, 8);
                    else if (shape == 1 && head > floor) tick = floor + (ulong)random.Next(0, (int)(head - floor) + 1);
                    else if (shape == 2) tick = head + 2000 + (ulong)random.Next(0, 2000);
                    else if (shape == 3 && floor > 60) tick = floor - (ulong)random.Next(1, 60);
                    var value = new Value { id = nextId++ };
                    dense.Write(slot, tick, in value);
                    sparse.Write(tick, value);
                    dense.Write(neighbour, tick, new Value { id = -1 });
                    if (tick > head)
                        head = tick;
                }
                else if (op < 65)
                {
                    ulong tick = floor + (ulong)random.Next(0, (int)Math.Min(head - floor + 2, 40));
                    dense.ClearFuture(slot, tick);
                    sparse.ClearFuture(tick);
                    head = sparse.Count == 0 ? tick : sparse.MostRecentTick;
                }
                else if (op < 72)
                {
                    ulong baseline = floor + (ulong)random.Next(0, (int)Math.Min(head - floor + 4, 30));
                    dense.PruneBeforeBaseline(slot, baseline);
                    sparse.PruneBeforeBaseline(baseline);
                    if (baseline > floor)
                        floor = baseline;
                }
                else if (op < 75)
                {
                    dense.Clear(slot);
                    sparse.Clear();
                    floor = head;
                }

                Assert.That(dense.Count(slot) > 0, Is.EqualTo(sparse.Count > 0), $"step {step}: emptiness");
                Assert.That(dense.Count(slot) > 1, Is.EqualTo(sparse.Count > 1), $"step {step}: more than one entry");

                // Pruning keeps the newest written entry at or before its baseline, so lookups
                // below the floor match too.
                ulong low = floor > 80 ? floor - 80 : 0;
                ulong probe = low + (ulong)random.Next(0, (int)Math.Min(head - low + 3, 4000));
                bool found = sparse.ReadOrPrevious(probe, out var expected);
                Assert.That(dense.ReadOrPrevious(slot, probe, out var actual), Is.EqualTo(found),
                    $"step {step}: ReadOrPrevious({probe}) presence");
                if (found)
                    Assert.That(actual.id, Is.EqualTo(expected.id), $"step {step}: ReadOrPrevious({probe})");

                ulong limit = low + (ulong)random.Next(0, (int)Math.Min(head - low + 3, 4000));
                bool latest = sparse.TryReadLatest(limit, out var expectedLatest);
                Assert.That(dense.TryReadLatest(slot, limit, out var actualLatest), Is.EqualTo(latest),
                    $"step {step}: TryReadLatest({limit}) presence");
                if (latest)
                    Assert.That(actualLatest.id, Is.EqualTo(expectedLatest.id), $"step {step}: TryReadLatest({limit})");
            }
        }

        [Test]
        public void AnInitialEntryFarBehindStaysReadableWithoutWideningTheRows()
        {
            var dense = new DenseStateHistory<Value>(8, 600);
            int slot = dense.AllocateSlot();
            dense.Write(slot, 0, new Value { id = 1 });
            dense.Write(slot, 5000, new Value { id = 2 });
            dense.Write(slot, 5001, new Value { id = 3 });

            Assert.That(dense.ReadOrPrevious(slot, 2500, out var initial), Is.True);
            Assert.That(initial.id, Is.EqualTo(1), "ticks before the first save read the initial state");
            Assert.That(dense.ReadOrPrevious(slot, 5000, out var saved), Is.True);
            Assert.That(saved.id, Is.EqualTo(2));
            Assert.That(dense.Count(slot), Is.EqualTo(3), "the initial state is one anchor, not 5000 rows");

            dense.PruneBeforeBaseline(slot, 5001);
            Assert.That(dense.ReadOrPrevious(slot, 2500, out _), Is.False, "pruning past the range drops the anchor");
            Assert.That(dense.ReadOrPrevious(slot, 5001, out var latest), Is.True);
            Assert.That(latest.id, Is.EqualTo(3));
        }
    }
}
