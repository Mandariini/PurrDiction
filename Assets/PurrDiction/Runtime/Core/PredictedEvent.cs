using System;
using System.Collections.Generic;

namespace PurrNet.Prediction
{
    public enum PredictedEventMode
    {
        /// <summary>Predict on the owner; deliver other or previously unpredicted occurrences when verified.</summary>
        Auto,
        /// <summary>Present the first local occurrence, including speculative replay, without repeating it on replay or verification.</summary>
        View
    }

    public abstract class PredictedDelegate<TDelegate> where TDelegate : Delegate
    {
        private readonly PredictionManager _world;
        private readonly PredictedIdentity _identity;
        private readonly PredictedEventMode _mode;
        private Dictionary<ulong, int> _delivered;
        private List<ulong> _expired;
        private PredictedComponentID _identityId;
        private uint _lastPass;
        private ulong _lastTick;
        private int _callsThisPass;
        private bool _hasPass;
        private ulong _oldestRetainedTick;
        private ulong _lastPruneTick;

        protected TDelegate onInvoke;

        protected PredictedDelegate(PredictionManager world, PredictedIdentity identity)
            : this(world, identity, PredictedEventMode.Auto) { }

        protected PredictedDelegate(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
        {
            if (mode != PredictedEventMode.Auto && mode != PredictedEventMode.View)
                throw new ArgumentOutOfRangeException(nameof(mode));
            _world = world;
            _identity = identity;
            _identityId = identity.id;
            _mode = mode;
        }

        public void AddListener(TDelegate action)
        {
            onInvoke = (TDelegate) Delegate.Combine(onInvoke, action);
        }

        public void RemoveListener(TDelegate action)
        {
            onInvoke = (TDelegate) Delegate.Remove(onInvoke, action);
        }

        public void RemoveAllListeners()
        {
            onInvoke = null;
        }

        /// <summary>Clear occurrence history when deliberately reusing an event for a new simulation lifetime.</summary>
        public void ResetDeliveryHistory()
        {
            _delivered?.Clear();
            _expired?.Clear();
            _hasPass = false;
            _oldestRetainedTick = 0;
            _lastPruneTick = 0;
            _identityId = _identity.id;
        }

        protected bool ShouldInvoke()
        {
            if (_world.isCatchingUpFrames)
            {
                return false;
            }

            if (_world.cachedIsServer)
            {
                return true;
            }

            if (_mode == PredictedEventMode.Auto && !_world.isVerified &&
                (!_identity.IsOwner() || _world.isReplaying))
                return false;

            if (!_identityId.Equals(_identity.id))
                ResetDeliveryHistory();

            ulong tick = _world.localTickInContext;
            PruneDeliveryHistory();
            if (tick < _oldestRetainedTick)
                return false;

            // spawnPass advances for every forward, verified and speculative simulation pass.
            // Preserve multiple calls in a tick, matching occurrences by their invocation order.
            if (!_hasPass || _lastPass != _world.spawnPass || _lastTick != tick)
            {
                _hasPass = true;
                _lastPass = _world.spawnPass;
                _lastTick = tick;
                _callsThisPass = 0;
            }
            int occurrence = ++_callsThisPass;
            _delivered ??= new Dictionary<ulong, int>();
            if (_delivered.TryGetValue(tick, out int delivered) && occurrence <= delivered)
                return false;

            _delivered[tick] = occurrence;
            return true;
        }

        private void PruneDeliveryHistory()
        {
            ulong window = _world.verifiedHistoryWindowTicks;
            ulong floor = _world.localTick > window ? _world.localTick - window : 0;
            if (floor > _oldestRetainedTick)
                _oldestRetainedTick = floor;

            // Prune at most once per simulated second. Expired occurrences remain suppressed
            // by the floor even after their entries have been reclaimed.
            if (_delivered == null || floor <= _lastPruneTick ||
                floor - _lastPruneTick < (ulong)Math.Max(1, _world.tickRate))
                return;

            _lastPruneTick = floor;
            _expired ??= new List<ulong>();
            foreach (var entry in _delivered)
                if (entry.Key < floor) _expired.Add(entry.Key);
            foreach (ulong expired in _expired)
                _delivered.Remove(expired);
            _expired.Clear();
        }
    }

    public sealed class PredictedEvent : PredictedDelegate<Action>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke()
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke();
            }
        }
    }

    public sealed class PredictedEvent<T> : PredictedDelegate<Action<T>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T value)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(value);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2> : PredictedDelegate<Action<T1, T2>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1, T2 arg2)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3> : PredictedDelegate<Action<T1, T2, T3>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4> : PredictedDelegate<Action<T1, T2, T3, T4>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5> : PredictedDelegate<Action<T1, T2, T3, T4, T5>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12,
            T13 arg13)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12,
            T13 arg13,
            T14 arg14)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12,
            T13 arg13,
            T14 arg14,
            T15 arg15)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public PredictedEvent(PredictionManager world, PredictedIdentity identity, PredictedEventMode mode)
            : base(world, identity, mode) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12,
            T13 arg13,
            T14 arg14,
            T15 arg15,
            T16 arg16)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15, arg16);
            }
        }
    }
}
