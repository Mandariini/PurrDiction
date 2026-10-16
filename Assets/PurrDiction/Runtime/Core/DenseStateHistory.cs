using System;
using System.Collections.Generic;
using Unity.IL2CPP.CompilerServices;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Histories of every identity of one value type in one world, one row per tick, so a pass
    /// over the identities reads and writes consecutive memory instead of one ring each.
    /// A slot covers every tick of its range: a tick that was not written holds a copy of the
    /// written entry before it, which is what a sparse history returns for that tick, and a
    /// written flag keeps the written ticks apart so every operation matches a sparse history
    /// exactly. A single older entry far behind the range (an initial state) is kept as the
    /// slot's anchor instead of being spread over thousands of rows. Rows are copied bitwise and
    /// never disposed, so only plain value types qualify.
    /// </summary>
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    internal sealed class DenseStateHistory<T> where T : struct
    {
        private T[] _values = Array.Empty<T>();
        private bool[] _written = Array.Empty<bool>();
        private int _rows;
        private int _rowMask;
        private int _stride;
        private readonly int _maxRows;

        private ulong[] _first = Array.Empty<ulong>();
        private ulong[] _last = Array.Empty<ulong>();
        private bool[] _any = Array.Empty<bool>();
        private ulong[] _anchorTick = Array.Empty<ulong>();
        private T[] _anchorValue = Array.Empty<T>();
        private bool[] _hasAnchor = Array.Empty<bool>();
        // Written entries of the range plus the anchor.
        private int[] _entries = Array.Empty<int>();
        private readonly Stack<int> _freeSlots = new ();
        private int _slotCount;

        /// <param name="initialRows">Rows to start with; grown as ranges widen.</param>
        /// <param name="window">Ticks the owners' pruning retains. A range never spans more rows
        /// than this allows; a write that would is refused so its owner can keep it elsewhere.</param>
        internal DenseStateHistory(int initialRows, int window)
        {
            _rows = RoundUpToPowerOfTwo(Math.Max(4, initialRows));
            _rowMask = _rows - 1;
            _maxRows = RoundUpToPowerOfTwo(Math.Max(_rows, window + 2));
        }

        internal int AllocateSlot()
        {
            int slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _slotCount++;
            if (slot >= _stride)
                GrowSlots(Math.Max(16, _stride * 2));
            Clear(slot);
            return slot;
        }

        internal void ReleaseSlot(int slot)
        {
            Clear(slot);
            _freeSlots.Push(slot);
        }

        internal void Clear(int slot)
        {
            _any[slot] = false;
            _hasAnchor[slot] = false;
            _entries[slot] = 0;
        }

        internal int rows => _rows;

        internal int Count(int slot) => _entries[slot];

        internal ulong OldestTick(int slot)
            => _hasAnchor[slot] ? _anchorTick[slot] : _any[slot] ? _first[slot] : 0;

        internal ulong MostRecentTick(int slot)
            => _any[slot] ? _last[slot] : _hasAnchor[slot] ? _anchorTick[slot] : 0;

        private int Index(ulong tick, int slot) => ((int)tick & _rowMask) * _stride + slot;

        // The entry written at exactly tick.
        internal bool TryRead(int slot, ulong tick, out T value)
        {
            if (_any[slot] && tick >= _first[slot] && tick <= _last[slot])
            {
                int index = Index(tick, slot);
                if (_written[index])
                {
                    value = _values[index];
                    return true;
                }
            }
            else if (_hasAnchor[slot] && tick == _anchorTick[slot])
            {
                value = _anchorValue[slot];
                return true;
            }
            value = default;
            return false;
        }

        internal bool Contains(int slot, ulong tick)
        {
            if (_any[slot] && tick >= _first[slot] && tick <= _last[slot])
                return _written[Index(tick, slot)];
            return _hasAnchor[slot] && tick == _anchorTick[slot];
        }

        // The newest entry, unless it lies ahead of maximumTick.
        internal bool TryReadLatest(int slot, ulong maximumTick, out T value)
        {
            if (_any[slot])
            {
                ulong last = _last[slot];
                if (last <= maximumTick)
                {
                    value = _values[Index(last, slot)];
                    return true;
                }
            }
            else if (_hasAnchor[slot] && _anchorTick[slot] <= maximumTick)
            {
                value = _anchorValue[slot];
                return true;
            }
            value = default;
            return false;
        }

        // The entry at tick, or the newest one before it.
        internal bool ReadOrPrevious(int slot, ulong tick, out T value)
        {
            if (_any[slot] && tick >= _first[slot])
            {
                ulong last = _last[slot];
                value = _values[Index(tick < last ? tick : last, slot)];
                return true;
            }
            if (_hasAnchor[slot] && tick >= _anchorTick[slot])
            {
                value = _anchorValue[slot];
                return true;
            }
            value = default;
            return false;
        }

        // The entry nearest to tick; between two equally near entries the later one wins.
        internal bool TryGetClosest(int slot, ulong tick, out T value, out ulong distance)
        {
            if (_entries[slot] == 0)
            {
                value = default;
                distance = 0;
                return false;
            }

            if (TryRead(slot, tick, out value))
            {
                distance = 0;
                return true;
            }

            bool hasPrevious = TryPreviousTick(slot, tick, out ulong previous);
            bool hasNext = TryNextTick(slot, tick, out ulong next);
            ulong chosen = hasPrevious && (!hasNext || tick - previous < next - tick) ? previous : next;
            TryRead(slot, chosen, out value);
            distance = chosen > tick ? chosen - tick : tick - chosen;
            return true;
        }

        // The newest entry before tick.
        private bool TryPreviousTick(int slot, ulong tick, out ulong previous)
        {
            if (_any[slot] && tick > _first[slot])
            {
                previous = NewestWrittenAtOrBefore(slot, Math.Min(tick - 1, _last[slot]));
                return true;
            }
            if (_hasAnchor[slot] && _anchorTick[slot] < tick)
            {
                previous = _anchorTick[slot];
                return true;
            }
            previous = 0;
            return false;
        }

        // The oldest entry after tick.
        private bool TryNextTick(int slot, ulong tick, out ulong next)
        {
            if (_hasAnchor[slot] && _anchorTick[slot] > tick)
            {
                next = _anchorTick[slot];
                return true;
            }
            if (_any[slot] && tick < _last[slot])
            {
                next = tick < _first[slot] ? _first[slot] : OldestWrittenAtOrAfter(slot, tick + 1);
                return true;
            }
            next = 0;
            return false;
        }

        /// <summary>
        /// Stores value at tick, keeping every other entry. Refused, without any change, when the
        /// range would span more rows than the window allows.
        /// </summary>
        internal bool TryWrite(int slot, ulong tick, in T value)
        {
            if (!_any[slot])
            {
                if (_hasAnchor[slot] && tick <= _anchorTick[slot])
                {
                    if (tick == _anchorTick[slot])
                    {
                        _anchorValue[slot] = value;
                        return true;
                    }
                    // An entry before the anchor: the anchor becomes the end of a range.
                    ulong anchorTick = _anchorTick[slot];
                    if (!Fits(tick, anchorTick))
                        return false;
                    var anchor = _anchorValue[slot];
                    _hasAnchor[slot] = false;
                    StartRange(slot, tick, value);
                    Append(slot, anchorTick, anchor);
                    _entries[slot]++;
                    return true;
                }
                StartRange(slot, tick, value);
                _entries[slot]++;
                return true;
            }

            ulong first = _first[slot];
            ulong last = _last[slot];
            if (tick > last)
            {
                if (first == last && !_hasAnchor[slot] && !Fits(first, tick))
                {
                    // A lone entry far behind: keep it as the anchor and start a new range. The
                    // owner's window pruning drops any older anchor first, so one is enough.
                    _anchorTick[slot] = last;
                    _anchorValue[slot] = _values[Index(last, slot)];
                    _hasAnchor[slot] = true;
                    StartRange(slot, tick, value);
                    _entries[slot]++;
                    return true;
                }
                if (!Fits(first, tick))
                    return false;
                Append(slot, tick, value);
                _entries[slot]++;
                return true;
            }

            if (tick >= first)
            {
                int index = Index(tick, slot);
                if (!_written[index])
                    _entries[slot]++;
                _values[index] = value;
                _written[index] = true;
                Propagate(slot, tick, last, value);
                return true;
            }

            if (_hasAnchor[slot] && tick <= _anchorTick[slot])
            {
                if (tick == _anchorTick[slot])
                {
                    _anchorValue[slot] = value;
                    return true;
                }
                // Before the anchor: spread the anchor into the range it precedes.
                if (!Fits(tick, last))
                    return false;
                EnsureSpan(last - tick + 1);
                var anchor = _anchorValue[slot];
                ulong anchorTick = _anchorTick[slot];
                for (ulong filled = anchorTick + 1; filled < first; filled++)
                    Fill(Index(filled, slot), anchor);
                int anchorIndex = Index(anchorTick, slot);
                _values[anchorIndex] = anchor;
                _written[anchorIndex] = true;
                for (ulong filled = tick + 1; filled < anchorTick; filled++)
                    Fill(Index(filled, slot), value);
                _hasAnchor[slot] = false;
            }
            else
            {
                if (!Fits(tick, last))
                    return false;
                EnsureSpan(last - tick + 1);
                for (ulong filled = tick + 1; filled < first; filled++)
                    Fill(Index(filled, slot), value);
            }
            int written = Index(tick, slot);
            _values[written] = value;
            _written[written] = true;
            _first[slot] = tick;
            _entries[slot]++;
            return true;
        }

        private bool Fits(ulong low, ulong high) => high - low < (ulong)_maxRows;

        // The ticks after tick that were never written read its entry.
        private void Propagate(int slot, ulong tick, ulong last, in T value)
        {
            for (ulong next = tick + 1; next <= last; next++)
            {
                int nextIndex = Index(next, slot);
                if (_written[nextIndex])
                    break;
                _values[nextIndex] = value;
            }
        }

        private void StartRange(int slot, ulong tick, in T value)
        {
            int index = Index(tick, slot);
            _values[index] = value;
            _written[index] = true;
            _first[slot] = tick;
            _last[slot] = tick;
            _any[slot] = true;
        }

        private void Append(int slot, ulong tick, in T value)
        {
            ulong last = _last[slot];
            EnsureSpan(tick - _first[slot] + 1);
            var previous = _values[Index(last, slot)];
            for (ulong filled = last + 1; filled < tick; filled++)
                Fill(Index(filled, slot), previous);
            int index = Index(tick, slot);
            _values[index] = value;
            _written[index] = true;
            _last[slot] = tick;
        }

        private void Fill(int index, in T value)
        {
            _values[index] = value;
            _written[index] = false;
        }

        internal bool Remove(int slot, ulong tick)
        {
            if (_hasAnchor[slot] && tick == _anchorTick[slot])
            {
                _hasAnchor[slot] = false;
                _entries[slot]--;
                return true;
            }

            if (!_any[slot] || tick < _first[slot] || tick > _last[slot])
                return false;
            int index = Index(tick, slot);
            if (!_written[index])
                return false;

            _entries[slot]--;
            ulong first = _first[slot];
            ulong last = _last[slot];
            if (first == last)
            {
                _any[slot] = false;
                return true;
            }
            if (tick == first)
            {
                _first[slot] = OldestWrittenAtOrAfter(slot, tick + 1);
                return true;
            }
            if (tick == last)
            {
                _last[slot] = NewestWrittenAtOrBefore(slot, tick - 1);
                return true;
            }

            // The removed tick and the copies after it now read the entry before it.
            _written[index] = false;
            var previous = _values[Index(tick - 1, slot)];
            _values[index] = previous;
            Propagate(slot, tick, last, previous);
            return true;
        }

        // Entries strictly before tick.
        internal int CountBefore(int slot, ulong tick)
        {
            int count = _hasAnchor[slot] && _anchorTick[slot] < tick ? 1 : 0;
            if (!_any[slot] || tick <= _first[slot])
                return count;
            ulong end = Math.Min(tick - 1, _last[slot]);
            for (ulong t = _first[slot]; t <= end; t++)
            {
                if (_written[Index(t, slot)])
                    count++;
            }
            return count;
        }

        // Drops the oldest entries, as a sparse history drops its front.
        internal int RemoveOldest(int slot, int count)
        {
            int removed = 0;
            if (count <= 0)
                return 0;
            if (_hasAnchor[slot])
            {
                _hasAnchor[slot] = false;
                removed++;
            }
            while (removed < count && _any[slot])
            {
                ulong first = _first[slot];
                removed++;
                if (first == _last[slot])
                {
                    _any[slot] = false;
                    break;
                }
                _first[slot] = OldestWrittenAtOrAfter(slot, first + 1);
            }
            _entries[slot] -= removed;
            return removed;
        }

        // Drops every entry after tick.
        internal void ClearFuture(int slot, ulong tick)
        {
            if (_any[slot])
            {
                if (tick < _first[slot])
                {
                    _entries[slot] -= CountRangeWritten(slot, _first[slot], _last[slot]);
                    _any[slot] = false;
                }
                else if (tick < _last[slot])
                {
                    _entries[slot] -= CountRangeWritten(slot, tick + 1, _last[slot]);
                    _last[slot] = NewestWrittenAtOrBefore(slot, tick);
                }
            }
            if (_hasAnchor[slot] && tick < _anchorTick[slot])
            {
                _hasAnchor[slot] = false;
                _entries[slot]--;
            }
        }

        private int CountRangeWritten(int slot, ulong from, ulong to)
        {
            int count = 0;
            for (ulong t = from; t <= to; t++)
            {
                if (_written[Index(t, slot)])
                    count++;
            }
            return count;
        }

        // Drops entries before baselineTick, keeping the newest one at or before it as an anchor.
        // Removes at most maximumEntries, oldest first, and returns how many it removed.
        internal int PruneBeforeBaseline(int slot, ulong baselineTick, int maximumEntries = int.MaxValue)
        {
            if (_entries[slot] <= 1 || maximumEntries <= 0 || OldestTick(slot) >= baselineTick)
                return 0;
            int before = CountBefore(slot, baselineTick);
            // The newest entry before the baseline stays unless the baseline itself is an entry.
            int remove = Contains(slot, baselineTick) ? before : Math.Max(0, before - 1);
            return RemoveOldest(slot, Math.Min(remove, maximumEntries));
        }

        // The first tick of a range is always written, so the scan stops inside it.
        private ulong NewestWrittenAtOrBefore(int slot, ulong tick)
        {
            while (!_written[Index(tick, slot)])
                tick--;
            return tick;
        }

        // The last tick of a range is always written, so the scan stops inside it.
        private ulong OldestWrittenAtOrAfter(int slot, ulong tick)
        {
            while (!_written[Index(tick, slot)])
                tick++;
            return tick;
        }

        // The tick of the entry at position index, oldest first.
        internal ulong EntryTick(int slot, int index)
        {
            if (_hasAnchor[slot])
            {
                if (index == 0)
                    return _anchorTick[slot];
                index--;
            }
            ulong last = _last[slot];
            for (ulong t = _first[slot]; t <= last; t++)
            {
                if (_written[Index(t, slot)] && index-- == 0)
                    return t;
            }
            throw new IndexOutOfRangeException();
        }

        // Replaces the value of an existing entry.
        internal void Overwrite(int slot, ulong tick, in T value)
        {
            if (_hasAnchor[slot] && tick == _anchorTick[slot])
            {
                _anchorValue[slot] = value;
                return;
            }
            _values[Index(tick, slot)] = value;
            Propagate(slot, tick, _last[slot], value);
        }

        // Copies every entry, oldest first, for an owner moving them elsewhere.
        internal void CopyEntries(int slot, List<(ulong tick, T value)> into)
        {
            if (_hasAnchor[slot])
                into.Add((_anchorTick[slot], _anchorValue[slot]));
            if (!_any[slot])
                return;
            ulong last = _last[slot];
            for (ulong t = _first[slot]; t <= last; t++)
            {
                int index = Index(t, slot);
                if (_written[index])
                    into.Add((t, _values[index]));
            }
        }

        private void EnsureSpan(ulong span)
        {
            if (span <= (ulong)_rows)
                return;
            int rows = _rows;
            while ((ulong)rows < span)
                rows *= 2;
            Rebuild(rows, _stride);
        }

        private void GrowSlots(int stride)
        {
            Array.Resize(ref _first, stride);
            Array.Resize(ref _last, stride);
            Array.Resize(ref _any, stride);
            Array.Resize(ref _anchorTick, stride);
            Array.Resize(ref _anchorValue, stride);
            Array.Resize(ref _hasAnchor, stride);
            Array.Resize(ref _entries, stride);
            Rebuild(_rows, stride);
        }

        private void Rebuild(int rows, int stride)
        {
            var values = new T[rows * stride];
            var written = new bool[rows * stride];
            int mask = rows - 1;
            for (int slot = 0; slot < _stride && slot < stride; slot++)
            {
                if (!_any[slot])
                    continue;
                for (ulong tick = _first[slot]; tick <= _last[slot]; tick++)
                {
                    int target = ((int)tick & mask) * stride + slot;
                    int source = Index(tick, slot);
                    values[target] = _values[source];
                    written[target] = _written[source];
                }
            }
            _values = values;
            _written = written;
            _rows = rows;
            _rowMask = mask;
            _stride = stride;
        }

        private static int RoundUpToPowerOfTwo(int value)
        {
            int result = 1;
            while (result < value)
                result <<= 1;
            return result;
        }
    }
}
