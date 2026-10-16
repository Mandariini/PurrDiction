using System;
using System.Collections.Generic;
using Unity.IL2CPP.CompilerServices;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Predicted states of every identity of one state type in one world, one row per tick, so a
    /// pass over the identities reads and writes consecutive memory instead of one ring each.
    /// A slot covers every tick of its range: a tick that was not written holds a copy of the one
    /// before it, which is what a sparse history returns for that tick, and a written flag keeps
    /// the written ticks apart so pruning and truncation match a sparse history exactly. A single older entry far
    /// behind the range (an initial state) is kept as the slot's anchor instead of being spread
    /// over thousands of rows. Only for states without references, whose copies need no disposal.
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
        private readonly Stack<int> _freeSlots = new ();
        private int _slotCount;

        /// <param name="initialRows">Rows to start with; grown as ranges widen.</param>
        /// <param name="window">Ticks the owners' pruning retains, so a range never needs more rows.</param>
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
        }

        internal int rows => _rows;

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

        internal int Count(int slot)
            => (_any[slot] ? (int)(_last[slot] - _first[slot] + 1) : 0) + (_hasAnchor[slot] ? 1 : 0);

        internal ulong OldestTick(int slot)
            => _hasAnchor[slot] ? _anchorTick[slot] : _any[slot] ? _first[slot] : 0;

        private int Index(ulong tick, int slot) => ((int)tick & _rowMask) * _stride + slot;

        internal void Write(int slot, ulong tick, in T value)
        {
            if (!_any[slot])
            {
                if (_hasAnchor[slot] && tick <= _anchorTick[slot])
                {
                    if (tick == _anchorTick[slot])
                    {
                        _anchorValue[slot] = value;
                        return;
                    }
                    // An entry before the anchor: the anchor becomes the end of a range.
                    var anchor = _anchorValue[slot];
                    ulong anchorTick = _anchorTick[slot];
                    _hasAnchor[slot] = false;
                    StartRange(slot, tick, value);
                    Append(slot, anchorTick, anchor);
                    return;
                }
                StartRange(slot, tick, value);
                return;
            }

            ulong first = _first[slot];
            ulong last = _last[slot];
            if (tick > last)
            {
                if (first == last && !_hasAnchor[slot] && tick - first + 1 > (ulong)_maxRows)
                {
                    // A lone entry far behind: keep it as the anchor and start a new range. The
                    // owner's window pruning drops any older anchor first, so one is enough.
                    _anchorTick[slot] = last;
                    _anchorValue[slot] = _values[Index(last, slot)];
                    _hasAnchor[slot] = true;
                    StartRange(slot, tick, value);
                    return;
                }
                Append(slot, tick, value);
                return;
            }

            if (tick >= first)
            {
                // The ticks after it that were never written read this entry now.
                int index = Index(tick, slot);
                _values[index] = value;
                _written[index] = true;
                for (ulong next = tick + 1; next <= last; next++)
                {
                    int nextIndex = Index(next, slot);
                    if (_written[nextIndex])
                        break;
                    _values[nextIndex] = value;
                }
                return;
            }

            if (_hasAnchor[slot] && tick <= _anchorTick[slot])
            {
                if (tick == _anchorTick[slot])
                {
                    _anchorValue[slot] = value;
                    return;
                }
                // Before the anchor: spread the anchor into the range it precedes.
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
                EnsureSpan(last - tick + 1);
                for (ulong filled = tick + 1; filled < first; filled++)
                    Fill(Index(filled, slot), value);
            }
            int written = Index(tick, slot);
            _values[written] = value;
            _written[written] = true;
            _first[slot] = tick;
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

        // Drops every entry after tick.
        internal void ClearFuture(int slot, ulong tick)
        {
            if (_any[slot])
            {
                if (tick < _first[slot])
                    _any[slot] = false;
                else if (tick < _last[slot])
                    _last[slot] = NewestWrittenAtOrBefore(slot, tick);
            }
            if (_hasAnchor[slot] && tick < _anchorTick[slot])
                _hasAnchor[slot] = false;
        }

        // Drops entries before baselineTick, keeping the newest one at or before it as an anchor.
        internal void PruneBeforeBaseline(int slot, ulong baselineTick)
        {
            if (Count(slot) <= 1 || OldestTick(slot) >= baselineTick)
                return;
            if (!_any[slot] || baselineTick < _first[slot])
                return;
            _hasAnchor[slot] = false;
            ulong last = _last[slot];
            _first[slot] = NewestWrittenAtOrBefore(slot, baselineTick < last ? baselineTick : last);
        }

        // The first tick of a range is always written, so the scan stops inside it.
        private ulong NewestWrittenAtOrBefore(int slot, ulong tick)
        {
            while (!_written[Index(tick, slot)])
                tick--;
            return tick;
        }

        // Keeps the window before tick plus the newest entry before that, like History.PruneByTickWindow.
        internal void PruneByTickWindow(int slot, ulong tick, ulong window)
        {
            if (window == 0 || tick <= window)
                return;
            PruneBeforeBaseline(slot, tick - window);
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
