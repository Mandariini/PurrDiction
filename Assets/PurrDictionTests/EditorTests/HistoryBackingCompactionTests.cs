using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using PurrNet.Prediction;

namespace PurrNet.Prediction.Tests.Editor
{
    // Artifact-only: these tests exercise storage and ownership without Unity APIs.
    public sealed class HistoryBackingCompactionTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        sealed class Ticket { public int Disposals; }
        struct Value : IDisposable
        {
            public Ticket Ticket;
            public void Dispose() { if (Ticket != null) Ticket.Disposals++; }
        }

        [StructLayout(LayoutKind.Sequential, Size = 3760)]
        struct LargeValue : IDisposable
        {
            public int Number;
            public void Dispose() { }
        }

        static int Backing<T>(History<T> history) where T : struct, IDisposable =>
            ((Array)typeof(History<T>).GetField("m_values", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(history)).Length;

        static Ticket Write(History<Value> history, List<Ticket> allocated, ulong tick)
        {
            var ticket = new Ticket();
            allocated.Add(ticket);
            history.Write(tick, new Value { Ticket = ticket });
            return ticket;
        }

        static void Check(History<Value> history, SortedDictionary<ulong, Ticket> expected, List<Ticket> allocated)
        {
            Assert.That(history.Count, Is.EqualTo(expected.Count));
            var live = new HashSet<Ticket>(expected.Values);
            int index = 0;
            foreach (var pair in expected)
            {
                Assert.That(history.GetEntryTick(index), Is.EqualTo(pair.Key));
                Assert.That(history[index].Ticket, Is.SameAs(pair.Value));
                Assert.That(history.Read(pair.Key, out var read), Is.True);
                Assert.That(read.Ticket, Is.SameAs(pair.Value));
                index++;
            }
            foreach (var ticket in allocated)
                Assert.That(ticket.Disposals, Is.EqualTo(live.Contains(ticket) ? 0 : 1));

            var queries = new HashSet<ulong> { 0, ulong.MaxValue };
            foreach (ulong tick in expected.Keys)
            {
                queries.Add(tick);
                if (tick > 0) queries.Add(tick - 1);
                if (tick < ulong.MaxValue) queries.Add(tick + 1);
            }
            foreach (ulong tick in queries)
            {
                Assert.That(history.HasAtOrBefore(tick),
                    Is.EqualTo(expected.Keys.Any(entryTick => entryTick <= tick)),
                    $"Baseline availability at {tick}");
                bool canReadLatest = expected.Count > 0 && expected.Last().Key <= tick;
                Assert.That(history.TryReadLatest(tick, out var latest), Is.EqualTo(canReadLatest),
                    $"Newest entry at {tick}");
                Assert.That(latest.Ticket, Is.SameAs(canReadLatest ? expected.Last().Value : null));
            }
        }

        [Test]
        public void NewestReadRejectsFutureEntriesAndReturnsAnIndependentValueCopy()
        {
            var history = new History<Value>();
            var first = new Ticket();
            var last = new Ticket();
            history.Write(3, new Value { Ticket = first });
            history.Write(7, new Value { Ticket = last });
            Assert.That(history.TryReadLatest(6, out var value), Is.False,
                "coalescing must not substitute an older entry when a future one exists");
            Assert.That(value.Ticket, Is.Null);
            Assert.That(history.TryReadLatest(7, out value), Is.True);
            value.Ticket = null;
            Assert.That(history[1].Ticket, Is.SameAs(last));
            Assert.That(first.Disposals + last.Disposals, Is.Zero);
            history.ClearFuture(6);
            Assert.That(history.TryReadLatest(6, out value), Is.True);
            Assert.That(value.Ticket, Is.SameAs(first));
            Assert.That(last.Disposals, Is.EqualTo(1));
            history.Clear();
            Assert.That(history.TryReadLatest(ulong.MaxValue, out value), Is.False);
            Assert.That(value.Ticket, Is.Null);
            Assert.That(first.Disposals, Is.EqualTo(1));
        }

        [Test]
        public void TickWindowRetainsEveryTickWithoutGrowingItsBackingArrays()
        {
            var history = History<LargeValue>.ForTickWindow(600);
            for (ulong tick = 0; tick < 1800; tick++)
            {
                history.PruneByTickWindow(tick);
                history.Write(tick, new LargeValue { Number = (int)tick });
                Assert.That(Backing(history), Is.EqualTo(602));
                ulong cutoff = tick > 600 ? tick - 600 : 0;
                Assert.That(history.Read(cutoff, out var oldest), Is.True);
                Assert.That(oldest.Number, Is.EqualTo((int)cutoff));
                Assert.That(history.MostRecentTick, Is.EqualTo(tick));
            }
            history.ClearPast(1790);
            Assert.That(history.TryCompactBackingStorage(65536), Is.True);
            history.RestoreEagerBackingStorage();
            Assert.That(Backing(history), Is.EqualTo(602));
            Assert.That(history.Read(1790, out var retained), Is.True);
            Assert.That(retained.Number, Is.EqualTo(1790));
            history.Clear();
        }

        [Test]
        public void TickWindowStorageStillSupportsUnprunedOutOfOrderWritesAndDisposal()
        {
            var history = History<Value>.ForTickWindow(600);
            var allocated = new List<Ticket>();
            var expected = new SortedDictionary<ulong, Ticket>();
            for (ulong tick = 0; tick < 880; tick += 2)
                expected[tick] = Write(history, allocated, tick);
            for (ulong tick = 1; tick < 400; tick += 2)
                expected[tick] = Write(history, allocated, tick);
            Assert.That(Backing(history), Is.EqualTo(901));
            Check(history, expected, allocated);
            history.Clear(); expected.Clear(); Check(history, expected, allocated);
        }

        [Test]
        public void WrappedCompactionAndEagerRebindPreserveEveryTickValueAndOwnership()
        {
            var history = new History<Value>(600);
            Assert.That(Backing(history), Is.EqualTo(901), "Live histories retain eager allocation.");
            var allocated = new List<Ticket>();
            var expected = new SortedDictionary<ulong, Ticket>();
            for (ulong tick = 0; tick < 899; tick++) expected[tick] = Write(history, allocated, tick);
            history.ClearPast(880);
            foreach (var tick in expected.Keys.Where(tick => tick < 880).ToArray()) expected.Remove(tick);
            for (ulong tick = 899; tick <= 940; tick++) expected[tick] = Write(history, allocated, tick);
            Assert.That(history.TryCompactBackingStorage(65536), Is.True);
            Assert.That(Backing(history), Is.EqualTo(61));
            Check(history, expected, allocated);
            Assert.That(history.ReadOrPrevious(941, out var anchor), Is.True);
            Assert.That(anchor.Ticket, Is.SameAs(expected[940]));
            history.RestoreEagerBackingStorage();
            Assert.That(Backing(history), Is.EqualTo(901));
            Check(history, expected, allocated);
            for (ulong tick = 941; tick <= 980; tick++) expected[tick] = Write(history, allocated, tick);
            Check(history, expected, allocated);
            history.Clear(); expected.Clear(); Check(history, expected, allocated);
        }

        [Test]
        public void CompactRingCanWrapGrowInsertReplaceAndRemoveBeforeRebind()
        {
            var history = new History<Value>(600);
            var allocated = new List<Ticket>();
            var expected = new SortedDictionary<ulong, Ticket>();
            for (ulong tick = 0; tick < 32; tick += 2) expected[tick] = Write(history, allocated, tick);
            history.TryCompactBackingStorage(65536);
            Assert.That(Backing(history), Is.EqualTo(16));
            history.ClearPast(12);
            foreach (var tick in expected.Keys.Where(tick => tick < 12).ToArray()) expected.Remove(tick);
            for (ulong tick = 32; tick <= 42; tick += 2) expected[tick] = Write(history, allocated, tick);
            expected[25] = Write(history, allocated, 25); // Full wrapped ring, out-of-order growth.
            Assert.That(Backing(history), Is.EqualTo(32));
            expected[20] = Write(history, allocated, 20);
            history.Remove(26); expected.Remove(26);
            Check(history, expected, allocated);
            Assert.That(history.ReadOrPrevious(27, out var previous), Is.True);
            Assert.That(previous.Ticket, Is.SameAs(expected[25]));
            history.Clear(); expected.Clear(); Check(history, expected, allocated);
        }

        [Test]
        public void CompactNearFullHistoryNeverGrowsBeyondOriginalCeiling()
        {
            var history = new History<Value>(600);
            var allocated = new List<Ticket>();
            var expected = new SortedDictionary<ulong, Ticket>();
            for (ulong tick = 0; tick < 899; tick++) expected[tick] = Write(history, allocated, tick);
            Assert.That(history.TryCompactBackingStorage(int.MaxValue), Is.True);
            Assert.That(Backing(history), Is.EqualTo(899));
            expected[899] = Write(history, allocated, 899);
            foreach (var tick in expected.Keys.Take(300).ToArray()) expected.Remove(tick);
            Assert.That(Backing(history), Is.EqualTo(901));
            Assert.That(history.Count, Is.EqualTo(600));
            Check(history, expected, allocated);
            history.Clear(); expected.Clear(); Check(history, expected, allocated);
        }

        [Test]
        public void PayloadBudgetRejectsLargePawnCopiesWithoutChangingEntries()
        {
            var history = new History<LargeValue>(600);
            for (ulong tick = 0; tick < 41; tick++) history.Write(tick, new LargeValue { Number = (int)tick });
            Assert.That(history.TryCompactBackingStorage(65536), Is.False);
            Assert.That(Backing(history), Is.EqualTo(901));
            Assert.That(history.Count, Is.EqualTo(41));
            for (ulong tick = 0; tick < 41; tick++)
            {
                Assert.That(history.Read(tick, out var value), Is.True);
                Assert.That(value.Number, Is.EqualTo((int)tick));
            }
            Assert.That(history.TryCompactBackingStorage(41 * (3760 + 8)), Is.True);
            Assert.That(Backing(history), Is.EqualTo(41));
            history.Clear();
        }

        [Test]
        public void SparsePartialPruningRetainsTheRequiredAnchorAndAllLaterEntries()
        {
            var history = new History<Value>(600);
            var allocated = new List<Ticket>();
            var expected = new SortedDictionary<ulong, Ticket>();
            foreach (ulong tick in new ulong[] { 1, 5, 10, 20, 40, 80, 120 }) expected[tick] = Write(history, allocated, tick);
            Assert.That(history.PruneBeforeBaseline(75, 2), Is.EqualTo(2));
            expected.Remove(1); expected.Remove(5);
            Check(history, expected, allocated);
            Assert.That(history.PruneBeforeBaseline(75), Is.EqualTo(2));
            expected.Remove(10); expected.Remove(20);
            Check(history, expected, allocated);
            history.TryCompactBackingStorage(65536);
            Assert.That(history.ReadOrPrevious(75, out var value), Is.True);
            Assert.That(value.Ticket, Is.SameAs(expected[40]));
            Assert.That(history.ReadOrPrevious(80, out value), Is.True);
            Assert.That(value.Ticket, Is.SameAs(expected[80]));
            Assert.That(history.PruneBeforeBaseline(0), Is.Zero, "A regressed cutoff cannot prune any required value.");
            Assert.That(history.PruneBeforeBaseline(ulong.MaxValue), Is.EqualTo(2));
            expected.Remove(40); expected.Remove(80);
            Check(history, expected, allocated);
            Assert.That(history.PruneBeforeBaseline(ulong.MaxValue), Is.Zero);
            history.Clear(); expected.Clear(); Check(history, expected, allocated);
        }

        [TestCase(1, 1)]
        [TestCase(600, 1)]
        public void EmptyCompactionAndRepeatedRestoreRespectBoundedCeilings(int capacity, int compactCapacity)
        {
            var history = new History<Value>(capacity);
            int eager = Backing(history);
            history.TryCompactBackingStorage(65536);
            Assert.That(Backing(history), Is.EqualTo(compactCapacity));
            Assert.That(history.TryCompactBackingStorage(65536), Is.False);
            history.RestoreEagerBackingStorage();
            history.RestoreEagerBackingStorage();
            Assert.That(Backing(history), Is.EqualTo(eager));
            Assert.That(history.Capacity, Is.EqualTo(capacity));
            Assert.That(history.Count, Is.Zero);
        }

        [Test]
        public void ExpiredAndFilledGapsRestoreDirectIndexingWithoutChangingOwnership()
        {
            var history = new History<Value>(600);
            var allocated = new List<Ticket>();
            var expected = new SortedDictionary<ulong, Ticket>();
            bool Direct() => (bool)typeof(History<Value>).GetField("m_contiguous", Fields).GetValue(history);
            foreach (ulong tick in new ulong[] { 0, 10, 11, 12, 13, 14, 20 })
                expected[tick] = Write(history, allocated, tick);
            Assert.That(Direct(), Is.False);
            history.TryCompactBackingStorage(65536);
            history.ClearPast(10); expected.Remove(0);
            Assert.That(Direct(), Is.False, "the remaining tail still has a gap");
            history.ClearFuture(14); expected.Remove(20);
            Assert.That(Direct(), Is.True);
            Check(history, expected, allocated);
            history.Remove(12); expected.Remove(12);
            Assert.That(Direct(), Is.False);
            expected[12] = Write(history, allocated, 12);
            Assert.That(Direct(), Is.True);
            expected[ulong.MaxValue] = Write(history, allocated, ulong.MaxValue);
            Assert.That(Direct(), Is.False);
            history.Remove(ulong.MaxValue); expected.Remove(ulong.MaxValue);
            Assert.That(Direct(), Is.True);
            history.Remove(10); expected.Remove(10);
            Assert.That(Direct(), Is.True);
            Check(history, expected, allocated);
            Assert.That(history.Find(10, out int before), Is.False);
            Assert.That(before, Is.Zero);
            Assert.That(history.Find(15, out int after), Is.False);
            Assert.That(after, Is.EqualTo(expected.Count));
            history.Clear(); expected.Clear(); Check(history, expected, allocated);
        }

        [Test]
        public void StartupGapExpirationRestoresDirectIndexingInAWrappedTickWindow()
        {
            var history = History<Value>.ForTickWindow(10);
            var allocated = new List<Ticket>();
            var expected = new SortedDictionary<ulong, Ticket>();
            expected[0] = Write(history, allocated, 0);
            for (ulong tick = 100; tick < 150; tick++)
            {
                history.PruneByTickWindow(tick);
                foreach (var old in expected.Keys.Where(old => old < tick - 10).ToArray())
                {
                    // Sparse pruning retains the newest preceding anchor until a
                    // newer entry reaches the cutoff.
                    if (expected.Keys.Any(next => next > old && next <= tick - 10)) expected.Remove(old);
                }
                expected[tick] = Write(history, allocated, tick);
                Check(history, expected, allocated);
                if (tick >= 111)
                    Assert.That(typeof(History<Value>).GetField("m_contiguous", Fields).GetValue(history), Is.True);
            }
            history.Clear(); expected.Clear(); Check(history, expected, allocated);
        }

        [Test]
        public void EmptyAndSingletonStorageCanWrapAndGrowWithoutRebind()
        {
            var history = new History<Value>(600);
            var allocated = new List<Ticket>();
            var expected = new SortedDictionary<ulong, Ticket>();
            history.TryCompactBackingStorage(65536);
            Assert.That(Backing(history), Is.EqualTo(1));
            expected[10] = Write(history, allocated, 10);
            history.ClearPast(10, true); expected.Clear();
            expected[20] = Write(history, allocated, 20);
            expected[10] = Write(history, allocated, 10); // Insert before the sole entry and grow.
            Assert.That(Backing(history), Is.EqualTo(2));
            history.ClearPast(20); expected.Remove(10);
            expected[30] = Write(history, allocated, 30); // Wrap in the two-slot ring.
            expected[25] = Write(history, allocated, 25); // Grow and copy both segments.
            Assert.That(Backing(history), Is.EqualTo(4));
            Check(history, expected, allocated);
            history.Clear(); expected.Clear(); Check(history, expected, allocated);
        }
    }
}
