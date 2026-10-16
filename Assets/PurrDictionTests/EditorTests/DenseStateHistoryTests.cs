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

        private static History<Value> Ring(int maxEntries)
            => maxEntries > 0 ? History<Value>.ForTickWindow(maxEntries, 8) : new History<Value>();

        private static History<Value> InTable(DenseStateHistory<Value> table, int maxEntries)
            => History<Value>.InTable(table, maxEntries, 8);

        [Test]
        public void EveryOperationMatchesTheRingHistory(
            [Values(-1, 40, 600)] int maxEntries,
            [Range(1, 6)] int seed)
        {
            var random = new Random(seed * 7919 + maxEntries);
            int window = maxEntries > 0 ? maxEntries : 600;
            var table = new DenseStateHistory<Value>(8, window);
            var ring = Ring(maxEntries);
            var dense = InTable(table, maxEntries);
            // A neighbouring slot shares the rows and must never leak into the probed one.
            var neighbour = InTable(table, maxEntries);
            ulong head = 0;
            int nextId = 1;
            int stepsInTable = 0;
            int moves = 0;

            for (int step = 0; step < 3000; step++)
            {
                int op = random.Next(100);
                if (op < 55)
                {
                    ulong tick = head + 1;
                    int shape = random.Next(24);
                    ulong oldest = ring.Count > 0 ? ring.OldestTick : head;
                    if (shape == 0) tick = head + (ulong)random.Next(2, 8);
                    else if (shape == 1 && head > oldest) tick = oldest + (ulong)random.Next(0, (int)Math.Min(head - oldest + 1, 2000));
                    else if (shape == 2) tick = head + 1000 + (ulong)random.Next(0, 3000);
                    else if (shape == 3 && oldest > 3) tick = oldest - (ulong)random.Next(1, (int)Math.Min(oldest, 80));
                    else if (shape == 4 && ring.Count > 0) tick = ring.GetEntryTick(random.Next(ring.Count));
                    var value = new Value { id = nextId++ };
                    ring.Write(tick, value);
                    dense.Write(tick, value);
                    neighbour.Write(tick, new Value { id = -1 });
                    if (tick > head)
                        head = tick;
                }
                else if (op < 62)
                {
                    ulong tick = Probe(random, ring, head);
                    ring.Remove(tick);
                    dense.Remove(tick);
                }
                else if (op < 69)
                {
                    ulong tick = Probe(random, ring, head);
                    bool inclusive = random.Next(2) == 0;
                    ring.ClearFuture(tick, inclusive);
                    dense.ClearFuture(tick, inclusive);
                }
                else if (op < 74)
                {
                    ulong tick = Probe(random, ring, head);
                    bool inclusive = random.Next(2) == 0;
                    ring.ClearPast(tick, inclusive);
                    dense.ClearPast(tick, inclusive);
                }
                else if (op < 84)
                {
                    ulong baseline = Probe(random, ring, head);
                    int budget = random.Next(3) == 0 ? random.Next(0, 4) : int.MaxValue;
                    Assert.That(dense.PruneBeforeBaseline(baseline, budget),
                        Is.EqualTo(ring.PruneBeforeBaseline(baseline, budget)), $"step {step}: pruned count");
                }
                else if (op < 90)
                {
                    ulong current = head + (ulong)random.Next(0, 50);
                    ring.PruneByTickWindow(current);
                    dense.PruneByTickWindow(current);
                }
                else if (op < 95 && ring.Count > 0)
                {
                    int index = random.Next(ring.Count);
                    var value = new Value { id = nextId++ };
                    ring[index] = value;
                    dense[index] = value;
                }
                else if (op < 97)
                {
                    ring.Clear();
                    dense.Clear();
                }

                AssertSame(ring, dense, random, head, step);
                if (dense.isInTable)
                {
                    stepsInTable++;
                }
                else
                {
                    // It left the table for a range too wide; start both over so the table keeps being tested.
                    moves++;
                    ring = Ring(maxEntries);
                    dense = InTable(table, maxEntries);
                }
            }

            Assert.That(stepsInTable, Is.GreaterThan(1500), "the table path is exercised, not only the ring it falls back to");
            Assert.That(moves, Is.GreaterThan(0), "ranges wider than the window leave the table");
            for (int i = 0; i < neighbour.Count; i++)
                Assert.That(neighbour[i].id, Is.EqualTo(-1));
        }

        private static ulong Probe(Random random, History<Value> ring, ulong head)
        {
            ulong low = ring.Count > 0 ? ring.OldestTick : head;
            low = low > 20 ? low - 20 : 0;
            return low + (ulong)random.Next(0, (int)Math.Min(head - low + 20, 6000));
        }

        private static void AssertSame(History<Value> ring, History<Value> dense, Random random, ulong head, int step)
        {
            Assert.That(dense.Count, Is.EqualTo(ring.Count), $"step {step}: count");
            Assert.That(dense.OldestTick, Is.EqualTo(ring.OldestTick), $"step {step}: oldest");
            Assert.That(dense.MostRecentTick, Is.EqualTo(ring.MostRecentTick), $"step {step}: newest");

            for (int i = 0; i < ring.Count; i++)
            {
                Assert.That(dense.GetEntryTick(i), Is.EqualTo(ring.GetEntryTick(i)), $"step {step}: tick of entry {i}");
                Assert.That(dense[i].id, Is.EqualTo(ring[i].id), $"step {step}: value of entry {i}");
            }

            for (int p = 0; p < 6; p++)
            {
                ulong tick = Probe(random, ring, head);
                Assert.That(dense.Read(tick, out var d), Is.EqualTo(ring.Read(tick, out var r)), $"step {step}: Read({tick})");
                Assert.That(d.id, Is.EqualTo(r.id), $"step {step}: Read({tick}) value");
                Assert.That(dense.ReadOrPrevious(tick, out d), Is.EqualTo(ring.ReadOrPrevious(tick, out r)),
                    $"step {step}: ReadOrPrevious({tick})");
                Assert.That(d.id, Is.EqualTo(r.id), $"step {step}: ReadOrPrevious({tick}) value");
                Assert.That(dense.TryReadLatest(tick, out d), Is.EqualTo(ring.TryReadLatest(tick, out r)),
                    $"step {step}: TryReadLatest({tick})");
                Assert.That(d.id, Is.EqualTo(r.id), $"step {step}: TryReadLatest({tick}) value");
                Assert.That(dense.TryGetClosest(tick, out d, out var dDistance),
                    Is.EqualTo(ring.TryGetClosest(tick, out r, out var rDistance)), $"step {step}: TryGetClosest({tick})");
                Assert.That(d.id, Is.EqualTo(r.id), $"step {step}: TryGetClosest({tick}) value");
                Assert.That(dDistance, Is.EqualTo(rDistance), $"step {step}: TryGetClosest({tick}) distance");
                Assert.That(dense.Find(tick, out int dIndex), Is.EqualTo(ring.Find(tick, out int rIndex)), $"step {step}: Find({tick})");
                Assert.That(dIndex, Is.EqualTo(rIndex), $"step {step}: Find({tick}) index");
                Assert.That(dense.Contains(tick), Is.EqualTo(ring.Contains(tick)), $"step {step}: Contains({tick})");
                Assert.That(dense.HasAtOrBefore(tick), Is.EqualTo(ring.HasAtOrBefore(tick)), $"step {step}: HasAtOrBefore({tick})");
            }
        }

        [Test]
        public void AnInitialEntryFarBehindStaysInTheTableAsItsAnchor()
        {
            var table = new DenseStateHistory<Value>(8, 600);
            var history = InTable(table, 600);
            history.Write(0, new Value { id = 1 });
            history.Write(5000, new Value { id = 2 });
            history.Write(5001, new Value { id = 3 });

            Assert.That(history.isInTable, Is.True);
            Assert.That(history.ReadOrPrevious(2500, out var initial), Is.True);
            Assert.That(initial.id, Is.EqualTo(1), "ticks before the first save read the initial state");
            Assert.That(history.Count, Is.EqualTo(3));
            Assert.That(table.rows, Is.LessThanOrEqualTo(8), "the initial state is one anchor, not 5000 rows");

            history.PruneBeforeBaseline(5001);
            Assert.That(history.ReadOrPrevious(2500, out _), Is.False, "pruning past the range drops the anchor");
            Assert.That(history.ReadOrPrevious(5001, out var latest), Is.True);
            Assert.That(latest.id, Is.EqualTo(3));
        }

        [Test]
        public void ARangeWiderThanTheWindowMovesIntoItsOwnRing()
        {
            var table = new DenseStateHistory<Value>(8, 60);
            var history = InTable(table, 60);
            var other = InTable(table, 60);
            history.Write(10, new Value { id = 1 });
            history.Write(11, new Value { id = 2 });
            other.Write(11, new Value { id = 9 });

            history.Write(500, new Value { id = 3 });

            Assert.That(history.isInTable, Is.False);
            Assert.That(history.Count, Is.EqualTo(3));
            Assert.That(history.ReadOrPrevious(200, out var kept), Is.True);
            Assert.That(kept.id, Is.EqualTo(2));
            Assert.That(table.rows, Is.LessThanOrEqualTo(64), "the table did not widen for one slot");
            Assert.That(other.Read(11, out var neighbour) && neighbour.id == 9, Is.True);
        }
    }
}
