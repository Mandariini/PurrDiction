using System;
using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    public sealed class History<T> where T : struct, IDisposable
    {
        // A tick and its value share one slot, so each lookup touches a single cache line.
        struct Slot
        {
            public ulong tick;
            public T value;
        }

        Slot[] m_values;

        int m_head;

        int m_count;

        readonly int m_maxCount;

        readonly int m_limitToCut;

        readonly int m_eagerCapacity;

        readonly int m_growthBoundary;

        bool m_contiguous = true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int Phys(int index)
        {
            int phys = m_head + index;
            if (phys >= m_values.Length)
                phys -= m_values.Length;
            return phys;
        }

        /// <summary>
        /// Access values directly by index
        /// </summary>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= m_count)
                {
                    throw new IndexOutOfRangeException($"Index {index} is out of range. Only from 0 to {m_count - 1} is valid.");
                }
                return m_values[Phys(index)].value;
            }
            set
            {
                if (index < 0 || index >= m_count)
                {
                    throw new IndexOutOfRangeException($"Index {index} is out of range. Only from 0 to {m_count - 1} is valid.");
                }
                m_values[Phys(index)].value = value;
            }
        }

        /// <summary>
        /// Number of entries
        /// </summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_count;
        }

        public int Capacity => m_maxCount;

        /// <summary>
        /// Value of the most recent received tick
        /// </summary>
        public ulong MostRecentTick
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_count == 0 ? 0 : m_values[Phys(m_count - 1)].tick;
        }

        // Read only the newest entry, and reject it if it is ahead of the caller.
        // Keep the ordinary value copy so equality callbacks cannot change history slots.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadLatest(ulong maximumTick, out T result)
        {
            if (m_count > 0)
            {
                int phys = Phys(m_count - 1);
                if (m_values[phys].tick <= maximumTick)
                {
                    result = m_values[phys].value;
                    return true;
                }
            }
            result = default;
            return false;
        }

        /// <summary>
        /// Oldest tick we have in the set (this will vary as older entries are purged)
        /// </summary>
        public ulong OldestTick
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_count == 0 ? 0 : m_values[m_head].tick;
        }

        // Availability checks need neither a search nor a copy of the retained value.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool HasAtOrBefore(ulong tick) => m_count > 0 && m_values[m_head].tick <= tick;

        /// <summary>
        /// Gets the tick value for the index in the internal collection
        /// </summary>
        /// <returns>Tick number</returns>
        public ulong GetEntryTick(int index)
        {
            if (index < 0 || index >= m_count)
            {
                throw new IndexOutOfRangeException($"Index {index} is out of range. Only from 0 to {m_count - 1} is valid.");
            }
            return m_values[Phys(index)].tick;
        }

        /// <summary>
        /// Creates a new history collection
        /// </summary>
        /// <param name="maxEntries">Number of entries you can have at once reliably,
        /// anything past this number can be cleaned at any time without your input. -1 means no limit thus no cleaning.</param>
        public History(int maxEntries = -1) : this(maxEntries, -1) { }

        // A tick window holds its cutoff anchor, the ticks inside the window and the
        // next write. Keep the usual entry-count ceiling for out-of-order callers.
        internal static History<T> ForTickWindow(int maxEntries)
            => new History<T>(maxEntries, maxEntries > 0 ? maxEntries + 2 : -1);

        internal static History<T> ForTickWindow(int maxEntries, int initialWindow)
            => new History<T>(maxEntries, maxEntries > 0 ? Math.Min(maxEntries, initialWindow) + 2 : -1);

        internal static History<T> ForTickWindow(int maxEntries, int initialWindow, int growthWindow)
            => new History<T>(maxEntries,
                maxEntries > 0 ? Math.Min(maxEntries, initialWindow) + 2 : -1,
                maxEntries > 0 ? Math.Min(maxEntries, growthWindow) + 2 : 0);

        private History(int maxEntries, int initialCapacity, int growthBoundary = 0)
        {
            m_maxCount = maxEntries;

            if (m_maxCount > 0)
            {
                m_limitToCut = Math.Max(m_maxCount + 10, m_maxCount + m_maxCount / 2);
                m_eagerCapacity = initialCapacity > 0 ? Math.Min(initialCapacity, m_limitToCut + 1) : m_limitToCut + 1;
                m_growthBoundary = Math.Min(growthBoundary, m_limitToCut + 1);
                m_values = new Slot[m_eagerCapacity];
            }
            else
            {
                m_values = new Slot[16];
            }
        }

        void Grow()
        {
            int newCapacity = m_values.Length * 2;
            if (m_maxCount > 0)
            {
                newCapacity = Math.Min(newCapacity, m_limitToCut + 1);
                int tickWindowCapacity = m_maxCount + 2;
                if (m_eagerCapacity < tickWindowCapacity && m_values.Length < tickWindowCapacity)
                    newCapacity = Math.Min(newCapacity, tickWindowCapacity);
                // Reach the protocol window without doubling past it. Stalled local
                // timelines can still grow beyond this boundary to their full limit.
                if (m_values.Length < m_growthBoundary)
                    newCapacity = Math.Min(newCapacity, m_growthBoundary);
            }
            ResizeBackingStorage(newCapacity);
        }

        // Compaction must preserve every baseline and value without disposing ownership.
        internal bool TryCompactBackingStorage(int maximumPayloadBytes)
        {
            if (m_maxCount <= 0)
                return false;
            int newCapacity = Math.Max(1, m_count);
            if (newCapacity >= m_values.Length ||
                (long)newCapacity * (Unsafe.SizeOf<T>() + sizeof(ulong)) > maximumPayloadBytes)
                return false;
            ResizeBackingStorage(newCapacity);
            return true;
        }

        // Keep the baseline's latest preceding entry as an anchor, even with a removal budget.
        internal int PruneBeforeBaseline(ulong baselineTick, int maximumEntriesToRemove = int.MaxValue)
        {
            // An oldest entry at or after the baseline is already the anchor.
            if (m_count <= 1 || maximumEntriesToRemove <= 0 || m_values[m_head].tick >= baselineTick)
                return 0;
            bool found = Find(baselineTick, out int index);
            int firstIndexToKeep = found ? index : Math.Max(0, index - 1);
            int removeCount = Math.Min(firstIndexToKeep, maximumEntriesToRemove);
            if (removeCount > 0)
                RemoveFront(removeCount);
            return removeCount;
        }

        // Restore capacity before Setup to avoid deferring growth into later simulation.
        internal long RestoreEagerBackingStorage(int minimumCapacity = 0)
        {
            int capacity = Math.Min(m_limitToCut + 1, Math.Max(m_eagerCapacity, minimumCapacity));
            if (m_maxCount <= 0 || m_values.Length >= capacity)
                return 0;
            ResizeBackingStorage(Math.Max(m_count, capacity));
            return BackingStoragePayloadBytes;
        }

        internal bool HasEagerBackingStorage => m_maxCount > 0 && m_values.Length >= m_eagerCapacity;

        internal long BackingStoragePayloadBytes =>
            (long)m_values.Length * (Unsafe.SizeOf<T>() + sizeof(ulong));

        private void ResizeBackingStorage(int newCapacity)
        {
            var newValues = new Slot[newCapacity];

            int firstChunk = Math.Min(m_count, m_values.Length - m_head);
            Array.Copy(m_values, m_head, newValues, 0, firstChunk);

            int remaining = m_count - firstChunk;
            if (remaining > 0)
                Array.Copy(m_values, 0, newValues, firstChunk, remaining);

            m_values = newValues;
            m_head = 0;
        }

        /// <summary>
        /// Writes history, adds an entry to the collection.
        /// </summary>
        /// <param name="tick">Which tick did this happen in.</param>
        /// <param name="data">What is the state/data of the tick.</param>
        public void Write(ulong tick, in T data)
        {
            if (m_count == 0 || tick > m_values[Phys(m_count - 1)].tick)
            {
                if (m_count > 0 && tick != m_values[Phys(m_count - 1)].tick + 1)
                    m_contiguous = false;

                if (m_count == m_values.Length)
                    Grow();

                m_values[Phys(m_count)] = new Slot { tick = tick, value = data };
                m_count++;

                if (m_maxCount > 0)
                    TryToDownsize();
                return;
            }

            if (Find(tick, out var index))
            {
                int phys = Phys(index);
                m_values[phys].value.Dispose();
                m_values[phys].value = data;
                return;
            }

            InsertAt(index, tick, data);
            RefreshContiguity();

            if (m_maxCount > 0)
                TryToDownsize();
        }

        void InsertAt(int index, ulong tick, in T data)
        {
            if (m_count == m_values.Length)
                Grow();

            for (int i = m_count; i > index; i--)
            {
                int dst = Phys(i);
                int src = Phys(i - 1);
                m_values[dst] = m_values[src];
            }

            m_values[Phys(index)] = new Slot { tick = tick, value = data };
            m_count++;
        }

        private void TryToDownsize()
        {
            if (m_count < m_limitToCut)
            {
                return;
            }

            RemoveFront(m_count - m_maxCount);
        }

        // Sorted unique ticks fill the entire interval exactly when its span equals
        // the entry count. A sparse startup or replay gap need not disable direct
        // indexing after that gap has expired or been filled.
        private void RefreshContiguity()
        {
            m_contiguous = m_count <= 1 ||
                m_values[Phys(m_count - 1)].tick - m_values[m_head].tick == (ulong)(m_count - 1);
        }

        private void RemoveFront(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int phys = Phys(i);
                m_values[phys].value.Dispose();
                m_values[phys] = default;
            }

            m_head = Phys(count);
            m_count -= count;

            if (!m_contiguous)
                RefreshContiguity();
        }

        private void RemoveBack(int count)
        {
            for (int i = m_count - count; i < m_count; i++)
            {
                int phys = Phys(i);
                m_values[phys].value.Dispose();
                m_values[phys] = default;
            }

            m_count -= count;

            if (!m_contiguous)
                RefreshContiguity();
        }

        /// <summary>
        /// Removes entries older than the configured history window while retaining the
        /// latest entry at or before the cutoff as an anchor for sparse history reads.
        /// </summary>
        public void PruneByTickWindow(ulong currentTick)
        {
            if (m_maxCount <= 0 || m_count <= 1)
                return;

            ulong window = (ulong)m_maxCount;
            if (currentTick <= window)
                return;

            PruneBeforeBaseline(currentTick - window);
        }

        /// <summary>
        /// Clear all the data before 'tick'
        /// </summary>
        /// <param name="tick">Clear everything before this</param>
        /// <param name="inclusive">Include the tick number in the clear</param>
        public void ClearPast(ulong tick, bool inclusive = false)
        {
            if (Find(tick, out var index) && inclusive)
            {
                index += 1;
            }

            RemoveFront(index);
        }

        /// <summary>
        /// Clear all the data after 'tick'
        /// </summary>
        /// <param name="tick">Clear everything after this</param>
        /// <param name="inclusive">Include the tick number in the clear</param>
        public void ClearFuture(ulong tick, bool inclusive = false)
        {
            if (Find(tick, out var index) && !inclusive)
            {
                index += 1;
            }

            RemoveBack(m_count - index);
        }

        public void Clear()
        {
            RemoveFront(m_count);
            m_head = 0;
            m_contiguous = true;
        }

        /// <summary>
        /// Returns if possible the data at the specified tick number.
        /// </summary>
        /// <param name="tick">The tick you are searching for.</param>
        /// <param name="result">The data stored or default if not found.</param>
        public bool Read(ulong tick, out T result)
        {
            if (Find(tick, out var index))
            {
                result = m_values[Phys(index)].value;
                return true;
            }

            result = default;
            return false;
        }

        public T ReadOrDefault(ulong tick)
        {
            return Find(tick, out var index) ? m_values[Phys(index)].value : default;
        }

        public bool TryGet(ulong tick, out T result)
        {
            return Read(tick, out result);
        }

        // Membership tests need neither the slot nor a copy of its value.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Contains(ulong tick) => Find(tick, out _);

        /// <summary>
        /// Returns the data at the specified tick, or if no exact match exists,
        /// returns the closest previous entry (the most recent entry before the tick).
        /// </summary>
        /// <param name="tick">The tick you are searching for.</param>
        /// <param name="result">The data stored or default if not found.</param>
        /// <returns>True if an exact or previous entry was found.</returns>
        public bool ReadOrPrevious(ulong tick, out T result)
        {
            if (Find(tick, out var index))
            {
                result = m_values[Phys(index)].value;
                return true;
            }

            // index is the insertion point; index-1 is the closest previous entry
            if (index > 0)
            {
                result = m_values[Phys(index - 1)].value;
                return true;
            }

            result = default;
            return false;
        }

        public bool TryGetClosest(ulong tick, out T result)
        {
            return TryGetClosest(tick, out result, out _);
        }

        public bool TryGetClosest(ulong tick, out T result, out ulong tickDifference)
        {
            if (m_count == 0)
            {
                result = default;
                tickDifference = 0;
                return false;
            }

            if (Find(tick, out var index))
            {
                result = m_values[Phys(index)].value;
                tickDifference = 0;
                return true;
            }

            if (index == m_count || (index > 0 && tick - m_values[Phys(index - 1)].tick < m_values[Phys(index)].tick - tick))
                index -= 1;

            result = m_values[Phys(index)].value;
            var resultTick = m_values[Phys(index)].tick;
            tickDifference = resultTick > tick ? resultTick - tick : tick - resultTick;
            return true;
        }

        /// <summary>
        /// Does a binary search on the internal entries.
        /// </summary>
        /// <param name="tick">The tick you are looking for.</param>
        /// <param name="result">The index that is either your result or the closest index, this can change when Writing new data.</param>
        /// <returns>Returns if a match was found or not.</returns>
        public bool Find(ulong tick, out int result)
        {
            if (m_count == 0)
            {
                result = 0;
                return false;
            }

            ulong first = m_values[m_head].tick;

            if (tick < first)
            {
                result = 0;
                return false;
            }

            if (m_contiguous)
            {
                ulong offset = tick - first;

                if (offset < (ulong)m_count)
                {
                    result = (int)offset;
                    return true;
                }

                result = m_count;
                return false;
            }

            // Delta baselines usually address one of the newest sparse entries.
            // Also resolve insertion points beyond or between those entries directly.
            int lastIndex = m_count - 1;
            ulong last = m_values[Phys(lastIndex)].tick;
            if (tick >= last)
            {
                result = tick == last ? lastIndex : m_count;
                return tick == last;
            }
            if (lastIndex > 0)
            {
                ulong previous = m_values[Phys(lastIndex - 1)].tick;
                if (tick >= previous)
                {
                    result = tick == previous ? lastIndex - 1 : lastIndex;
                    return tick == previous;
                }
            }

            int min = 0;
            int max = lastIndex - 2;

            while (min <= max)
            {
                int mid = (min + max) / 2;
                ulong midTick = m_values[Phys(mid)].tick;

                if (tick == midTick)
                {
                    result = mid;
                    return true;
                }

                if (tick < midTick)
                {
                    max = mid - 1;
                }
                else
                {
                    min = mid + 1;
                }
            }

            result = min;
            return false;
        }

        /// <summary>
        /// Throw any cached data at the garbage collector
        /// </summary>
        public void TrimExcessMemory()
        {
            if (m_maxCount > 0 || m_count > m_values.Length / 2 || m_values.Length <= 16)
                return;

            ResizeBackingStorage(Math.Max(16, m_count));
        }

        public void Remove(ulong tick)
        {
            if (!Find(tick, out var index))
                return;

            int phys = Phys(index);
            m_values[phys].value.Dispose();

            for (int i = index; i < m_count - 1; i++)
            {
                int dst = Phys(i);
                int src = Phys(i + 1);
                m_values[dst] = m_values[src];
            }

            m_values[Phys(m_count - 1)] = default;
            m_count--;

            RefreshContiguity();
        }
    }
}
