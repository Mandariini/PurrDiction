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
        private int _maxRows;

        // A slot's bookkeeping sits together, so an operation reads one line of it.
        private struct SlotState
        {
            public ulong first;
            public ulong last;
            public ulong anchorTick;
            // Written entries of the range plus the anchor.
            public int entries;
            public bool any;
            public bool hasAnchor;
        }

        private SlotState[] _slots = Array.Empty<SlotState>();
        private T[] _anchorValue = Array.Empty<T>();
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

        // Owners with a longer window may share the table; ranges may then span its rows.
        internal void AllowWindow(int window)
            => _maxRows = Math.Max(_maxRows, RoundUpToPowerOfTwo(window + 2));

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
            _slots[slot].any = false;
            _slots[slot].hasAnchor = false;
            _slots[slot].entries = 0;
        }

        internal int rows => _rows;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal int Count(int slot) => _slots[slot].entries;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal ulong OldestTick(int slot)
            => _slots[slot].hasAnchor ? _slots[slot].anchorTick : _slots[slot].any ? _slots[slot].first : 0;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal ulong MostRecentTick(int slot)
            => _slots[slot].any ? _slots[slot].last : _slots[slot].hasAnchor ? _slots[slot].anchorTick : 0;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private int Index(ulong tick, int slot) => ((int)tick & _rowMask) * _stride + slot;

        // The entry written at exactly tick.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TryRead(int slot, ulong tick, out T value)
        {
            if (_slots[slot].any && tick >= _slots[slot].first && tick <= _slots[slot].last)
            {
                int index = Index(tick, slot);
                if (IsGapless(slot) || _written[index])
                {
                    value = _values[index];
                    return true;
                }
            }
            else if (_slots[slot].hasAnchor && tick == _slots[slot].anchorTick)
            {
                value = _anchorValue[slot];
                return true;
            }
            value = default;
            return false;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool Contains(int slot, ulong tick)
        {
            if (_slots[slot].any && tick >= _slots[slot].first && tick <= _slots[slot].last)
                return IsGapless(slot) || _written[Index(tick, slot)];
            return _slots[slot].hasAnchor && tick == _slots[slot].anchorTick;
        }

        // The newest entry, unless it lies ahead of maximumTick.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TryReadLatest(int slot, ulong maximumTick, out T value)
        {
            if (_slots[slot].any)
            {
                ulong last = _slots[slot].last;
                if (last <= maximumTick)
                {
                    value = _values[Index(last, slot)];
                    return true;
                }
            }
            else if (_slots[slot].hasAnchor && _slots[slot].anchorTick <= maximumTick)
            {
                value = _anchorValue[slot];
                return true;
            }
            value = default;
            return false;
        }

        // The entry at tick, or the newest one before it.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool ReadOrPrevious(int slot, ulong tick, out T value)
        {
            if (_slots[slot].any && tick >= _slots[slot].first)
            {
                ulong last = _slots[slot].last;
                value = _values[Index(tick < last ? tick : last, slot)];
                return true;
            }
            if (_slots[slot].hasAnchor && tick >= _slots[slot].anchorTick)
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
            if (_slots[slot].entries == 0)
            {
                value = default;
                distance = 0;
                return false;
            }

            // Extrapolation asks past the newest entry, which is then the closest: the anchor
            // always lies before the range.
            ref var state = ref _slots[slot];
            if (state.any && tick > state.last)
            {
                value = _values[Index(state.last, slot)];
                distance = tick - state.last;
                return true;
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
            if (_slots[slot].any && tick > _slots[slot].first)
            {
                previous = NewestWrittenAtOrBefore(slot, Math.Min(tick - 1, _slots[slot].last));
                return true;
            }
            if (_slots[slot].hasAnchor && _slots[slot].anchorTick < tick)
            {
                previous = _slots[slot].anchorTick;
                return true;
            }
            previous = 0;
            return false;
        }

        // The oldest entry after tick.
        private bool TryNextTick(int slot, ulong tick, out ulong next)
        {
            if (_slots[slot].hasAnchor && _slots[slot].anchorTick > tick)
            {
                next = _slots[slot].anchorTick;
                return true;
            }
            if (_slots[slot].any && tick < _slots[slot].last)
            {
                next = tick < _slots[slot].first ? _slots[slot].first : OldestWrittenAtOrAfter(slot, tick + 1);
                return true;
            }
            next = 0;
            return false;
        }

        /// <summary>
        /// Stores value at tick, keeping every other entry. Refused, without any change, when the
        /// range would span more rows than the window allows.
        /// </summary>
        // The usual write: the tick after the newest, still inside the rows.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TryAppendNext(int slot, ulong tick, in T value)
        {
            ref var state = ref _slots[slot];
            if (!state.any || tick != state.last + 1 || tick - state.first >= (ulong)_rows)
                return false;
            int next = Index(tick, slot);
            _values[next] = value;
            _written[next] = true;
            state.last = tick;
            state.entries++;
            return true;
        }

        internal bool TryWrite(int slot, ulong tick, in T value)
        {
            if (TryAppendNext(slot, tick, in value))
                return true;

            if (!_slots[slot].any)
            {
                if (_slots[slot].hasAnchor && tick <= _slots[slot].anchorTick)
                {
                    if (tick == _slots[slot].anchorTick)
                    {
                        _anchorValue[slot] = value;
                        return true;
                    }
                    // An entry before the anchor: the anchor becomes the end of a range.
                    ulong anchorTick = _slots[slot].anchorTick;
                    if (!Fits(tick, anchorTick))
                        return false;
                    var anchor = _anchorValue[slot];
                    _slots[slot].hasAnchor = false;
                    StartRange(slot, tick, value);
                    Append(slot, anchorTick, anchor);
                    _slots[slot].entries++;
                    return true;
                }
                StartRange(slot, tick, value);
                _slots[slot].entries++;
                return true;
            }

            ulong first = _slots[slot].first;
            ulong last = _slots[slot].last;
            if (tick > last)
            {
                if (first == last && !_slots[slot].hasAnchor && !Fits(first, tick))
                {
                    // A lone entry far behind: keep it as the anchor and start a new range. The
                    // owner's window pruning drops any older anchor first, so one is enough.
                    _slots[slot].anchorTick = last;
                    _anchorValue[slot] = _values[Index(last, slot)];
                    _slots[slot].hasAnchor = true;
                    StartRange(slot, tick, value);
                    _slots[slot].entries++;
                    return true;
                }
                if (!Fits(first, tick))
                    return false;
                Append(slot, tick, value);
                _slots[slot].entries++;
                return true;
            }

            if (tick >= first)
            {
                int index = Index(tick, slot);
                if (!_written[index])
                    _slots[slot].entries++;
                _values[index] = value;
                _written[index] = true;
                Propagate(slot, tick, last, value);
                return true;
            }

            if (_slots[slot].hasAnchor && tick <= _slots[slot].anchorTick)
            {
                if (tick == _slots[slot].anchorTick)
                {
                    _anchorValue[slot] = value;
                    return true;
                }
                // Before the anchor: spread the anchor into the range it precedes.
                if (!Fits(tick, last))
                    return false;
                EnsureSpan(last - tick + 1);
                var anchor = _anchorValue[slot];
                ulong anchorTick = _slots[slot].anchorTick;
                for (ulong filled = anchorTick + 1; filled < first; filled++)
                    Fill(Index(filled, slot), anchor);
                int anchorIndex = Index(anchorTick, slot);
                _values[anchorIndex] = anchor;
                _written[anchorIndex] = true;
                for (ulong filled = tick + 1; filled < anchorTick; filled++)
                    Fill(Index(filled, slot), value);
                _slots[slot].hasAnchor = false;
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
            _slots[slot].first = tick;
            _slots[slot].entries++;
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
            _slots[slot].first = tick;
            _slots[slot].last = tick;
            _slots[slot].any = true;
        }

        private void Append(int slot, ulong tick, in T value)
        {
            ulong last = _slots[slot].last;
            EnsureSpan(tick - _slots[slot].first + 1);
            if (tick > last + 1)
            {
                var previous = _values[Index(last, slot)];
                for (ulong filled = last + 1; filled < tick; filled++)
                    Fill(Index(filled, slot), previous);
            }
            int index = Index(tick, slot);
            _values[index] = value;
            _written[index] = true;
            _slots[slot].last = tick;
        }

        private void Fill(int index, in T value)
        {
            _values[index] = value;
            _written[index] = false;
        }

        internal bool Remove(int slot, ulong tick)
        {
            if (_slots[slot].hasAnchor && tick == _slots[slot].anchorTick)
            {
                _slots[slot].hasAnchor = false;
                _slots[slot].entries--;
                return true;
            }

            if (!_slots[slot].any || tick < _slots[slot].first || tick > _slots[slot].last)
                return false;
            int index = Index(tick, slot);
            if (!_written[index])
                return false;

            _slots[slot].entries--;
            ulong first = _slots[slot].first;
            ulong last = _slots[slot].last;
            if (first == last)
            {
                _slots[slot].any = false;
                return true;
            }
            if (tick == first)
            {
                _slots[slot].first = OldestWrittenAtOrAfter(slot, tick + 1);
                return true;
            }
            if (tick == last)
            {
                _slots[slot].last = NewestWrittenAtOrBefore(slot, tick - 1);
                return true;
            }

            // The removed tick and the copies after it now read the entry before it.
            _written[index] = false;
            var previous = _values[Index(tick - 1, slot)];
            _values[index] = previous;
            Propagate(slot, tick, last, previous);
            return true;
        }

        // Written entries of the range, without the anchor.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private int RangeEntries(int slot) => _slots[slot].entries - (_slots[slot].hasAnchor ? 1 : 0);

        // Every tick of the range was written, the usual case, so positions follow from ticks
        // without scanning rows that each sit a whole row apart.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool IsGapless(int slot) => (ulong)RangeEntries(slot) == _slots[slot].last - _slots[slot].first + 1;

        // Entries strictly before tick.
        internal int CountBefore(int slot, ulong tick)
        {
            int count = _slots[slot].hasAnchor && _slots[slot].anchorTick < tick ? 1 : 0;
            if (!_slots[slot].any || tick <= _slots[slot].first)
                return count;
            if (IsGapless(slot))
                return count + (int)(Math.Min(tick, _slots[slot].last + 1) - _slots[slot].first);
            ulong end = Math.Min(tick - 1, _slots[slot].last);
            for (ulong t = _slots[slot].first; t <= end; t++)
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
            int rangeEntries = RangeEntries(slot);
            if (_slots[slot].hasAnchor)
            {
                _slots[slot].hasAnchor = false;
                removed++;
            }
            if (removed < count && _slots[slot].any && (ulong)rangeEntries == _slots[slot].last - _slots[slot].first + 1)
            {
                int take = Math.Min(count - removed, rangeEntries);
                removed += take;
                if (take == rangeEntries)
                    _slots[slot].any = false;
                else
                    _slots[slot].first += (ulong)take;
            }
            while (removed < count && _slots[slot].any)
            {
                ulong first = _slots[slot].first;
                removed++;
                if (first == _slots[slot].last)
                {
                    _slots[slot].any = false;
                    break;
                }
                _slots[slot].first = OldestWrittenAtOrAfter(slot, first + 1);
            }
            _slots[slot].entries -= removed;
            return removed;
        }

        // Drops every entry after tick.
        internal void ClearFuture(int slot, ulong tick)
        {
            if (_slots[slot].any)
            {
                if (tick < _slots[slot].first)
                {
                    _slots[slot].entries -= RangeEntries(slot);
                    _slots[slot].any = false;
                }
                else if (tick < _slots[slot].last)
                {
                    if (IsGapless(slot))
                    {
                        _slots[slot].entries -= (int)(_slots[slot].last - tick);
                        _slots[slot].last = tick;
                    }
                    else
                    {
                        _slots[slot].entries -= CountRangeWritten(slot, tick + 1, _slots[slot].last);
                        _slots[slot].last = NewestWrittenAtOrBefore(slot, tick);
                    }
                }
            }
            if (_slots[slot].hasAnchor && tick < _slots[slot].anchorTick)
            {
                _slots[slot].hasAnchor = false;
                _slots[slot].entries--;
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
            if (_slots[slot].entries <= 1 || maximumEntries <= 0 || OldestTick(slot) >= baselineTick)
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
            if (_slots[slot].hasAnchor)
            {
                if (index == 0)
                    return _slots[slot].anchorTick;
                index--;
            }
            ulong last = _slots[slot].last;
            for (ulong t = _slots[slot].first; t <= last; t++)
            {
                if (_written[Index(t, slot)] && index-- == 0)
                    return t;
            }
            throw new IndexOutOfRangeException();
        }

        // Replaces the value of an existing entry.
        internal void Overwrite(int slot, ulong tick, in T value)
        {
            if (_slots[slot].hasAnchor && tick == _slots[slot].anchorTick)
            {
                _anchorValue[slot] = value;
                return;
            }
            _values[Index(tick, slot)] = value;
            Propagate(slot, tick, _slots[slot].last, value);
        }

        // Copies every entry, oldest first, for an owner moving them elsewhere.
        internal void CopyEntries(int slot, List<(ulong tick, T value)> into)
        {
            if (_slots[slot].hasAnchor)
                into.Add((_slots[slot].anchorTick, _anchorValue[slot]));
            if (!_slots[slot].any)
                return;
            ulong last = _slots[slot].last;
            for (ulong t = _slots[slot].first; t <= last; t++)
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
            Array.Resize(ref _slots, stride);
            Array.Resize(ref _anchorValue, stride);
            Rebuild(_rows, stride);
        }

        private void Rebuild(int rows, int stride)
        {
            var values = new T[rows * stride];
            var written = new bool[rows * stride];
            int mask = rows - 1;
            for (int slot = 0; slot < _stride && slot < stride; slot++)
            {
                if (!_slots[slot].any)
                    continue;
                for (ulong tick = _slots[slot].first; tick <= _slots[slot].last; tick++)
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
