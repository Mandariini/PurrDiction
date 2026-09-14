using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction.StateMachine
{
    /// <summary>
    /// A replicated state machine. <see cref="SetState(IPredictedStateNodeBase)"/>/<see cref="Next"/>/
    /// <see cref="Previous"/> only write <see cref="SMState.wantedState"/> into the predicted state;
    /// the actual transition (Exit old node -> Enter new node) is applied deterministically when
    /// this identity's <see cref="Simulate"/> runs. Because <see cref="SMState.wantedState"/> and
    /// <see cref="SMState.stateIndex"/> are replicated, a request made on tick N applies to the same
    /// tick on every simulator (server, owner prediction and verified replay all run the same code).
    ///
    /// The view layer is driven separately: <see cref="UpdateView"/> compares (stateIndex, transition)
    /// on both the predicted/interpolated and the verified timeline and fires
    /// <see cref="IPredictedStateNodeBase.ViewEnter"/> / <see cref="IPredictedStateNodeBase.ViewExit"/>
    /// accordingly. Re-entering the current state (TryResetState / interrupt-self semantics) bumps
    /// <see cref="SMState.transition"/>, which is what lets the view restart a clip for that re-entry.
    ///
    /// Fixes applied on top of the original PurrDiction implementation (kept for reference):
    /// 1. Both transition sources now share one code path and identical bookkeeping: the wantedState
    ///    branch used to skip updating _previousStateNode/_currentStateNode/_nextStateNode and firing
    ///    onStateChanged like the external-change branch did. Bookkeeping now also happens BEFORE
    ///    Enter() so a node can query the machine (currentStateNode, ...) consistently from its own
    ///    Enter().
    /// 2. An external stateIndex change (authoritative/verified state applied directly, no wantedState)
    ///    used to call Enter() without first calling Exit() on the node that was entered. It now exits
    ///    the node tracked by lastEnteredStateIndex before entering the new one, and reports the true
    ///    previous node to onStateChanged.
    /// 3. Next()/Previous() are now safe before the machine's first transition (stateIndex == -1);
    ///    they navigate relative to the default state (or index 0). SetState(int) also validates its
    ///    index instead of crashing, and an invalid wantedState is consumed with an error instead of
    ///    being left stuck or indexing out of bounds.
    ///
    /// TIMING - WHEN DOES A REQUEST APPLY? (deterministic: identical on every simulator)
    /// SetState/TrySetState/TryResetState/Next/Previous/ForceSetState only write wantedState - a
    /// transition is NEVER applied synchronously at the call site. Immediately after a request,
    /// currentStateNode still returns the OLD state; use the bool returned by Try* to know whether
    /// the request was accepted, or read SMState.wantedState for what is pending.
    ///
    /// The transition (Exit old -> Enter new) is applied while this machine's Simulate runs:
    /// - Request made by code simulating BEFORE this machine (player, controller, modules): applied
    ///   at the START of the same tick's Simulate; the new state's StateSimulate runs that tick too.
    /// - Request made from INSIDE this machine (a node's Enter() or StateSimulate()): flushed at the
    ///   END of the same tick's Simulate. The new state's Enter() runs that tick, but its first
    ///   StateSimulate() runs on the next tick.
    /// - Request made by code simulating AFTER this machine (a state node's own PredictedIdentity
    ///   Simulate - the TestNode_Input pattern): applies on the next tick.
    ///
    /// The end-of-Simulate flush also lets a state chain transitions within one tick (e.g. enter a
    /// state that immediately requests the next one - no-clip fall-throughs). The chain is capped
    /// at state count + 1; a cyclic chain is broken deterministically by dropping the leftover
    /// request (it can simply be requested again on a later tick).
    ///
    /// currentStateNode is safe to read once this machine's Simulate has run for that tick (or from
    /// inside the entered node's own Enter(), where the bookkeeping is already updated before the
    /// call). Same-state re-entry does not change currentStateNode - SMState.transition is what
    /// tells you a re-entry happened.
    /// </summary>
    [AddComponentMenu("PurrDiction/Predicted State machine")]
    public class PredictedStateMachine : PredictedIdentity<PredictedStateMachine.SMState>
    {
        [SerializeField, Min(-1)] private int _defaultStateIndex = 0;

        [SerializeField] private List<SerializableInterface<IPredictedStateNodeBase>> _wrappedStates =
            new List<SerializableInterface<IPredictedStateNodeBase>>();
        private List<IPredictedStateNodeBase> _states;
        public IReadOnlyList<IPredictedStateNodeBase> states => _states;
        public event StateChangedDelegate onStateChanged;
        public delegate void StateChangedDelegate(IPredictedStateNodeBase previousState, IPredictedStateNodeBase newState);

        public IPredictedStateNodeBase currentStateNode
        {
            get
            {
                if (_states == null || currentState.stateIndex < 0 || currentState.stateIndex >= _states.Count)
                    return null;

                return _states[currentState.stateIndex];
            }
        }

        public IPredictedStateNodeBase _previousStateNode;
        public IPredictedStateNodeBase _nextStateNode;
        public IPredictedStateNodeBase _currentStateNode;

        private int _previousViewStateIndex = -1;
        private int _previousVerifiedViewStateIndex = -1;

        private void Awake()
        {
            _states = new List<IPredictedStateNodeBase>(_wrappedStates.Count);
            for (var i = 0; i < _wrappedStates.Count; i++)
                _states.Add(_wrappedStates[i].Value);
            var defaultStateIndex = GetValidDefaultStateIndex();
            _currentStateNode = defaultStateIndex > -1 ? _states[defaultStateIndex] : null;

            for (var i = 0; i < _states.Count; i++)
            {
                if (_states[i] == null)
                    continue;
                var state = _states[i];
                state.Setup(this);
            }
        }

        private int GetValidDefaultStateIndex()
        {
            if (_defaultStateIndex < 0)
                return -1;

            if (_states != null)
            {
                if (_defaultStateIndex >= _states.Count)
                    return -1;

                return _states[_defaultStateIndex] != null ? _defaultStateIndex : -1;
            }

            if (_defaultStateIndex >= _wrappedStates.Count)
                return -1;

            return _wrappedStates[_defaultStateIndex]?.Value != null ? _defaultStateIndex : -1;
        }

        private uint _prevViewTransition;
        private uint _prevVerifiedViewTransition;
        protected override void UpdateView(SMState viewState, SMState? verified)
        {
            if (verified.HasValue)
            {
                if (verified.Value.stateIndex != _previousVerifiedViewStateIndex || verified.Value.transition != _prevVerifiedViewTransition)
                {
                    if (_previousVerifiedViewStateIndex > -1 && _states[_previousVerifiedViewStateIndex] != null)
                        _states[_previousVerifiedViewStateIndex].ViewExit(true);
                    _previousVerifiedViewStateIndex = verified.Value.stateIndex;
                    _prevVerifiedViewTransition = verified.Value.transition;
                    if (_previousVerifiedViewStateIndex > -1 && _states[_previousVerifiedViewStateIndex] != null)
                        _states[_previousVerifiedViewStateIndex].ViewEnter(true);
                }
            }

            if (viewState.stateIndex != _previousViewStateIndex || viewState.transition != _prevViewTransition)
            {
                if (_previousViewStateIndex > -1 && _states[_previousViewStateIndex] != null)
                    _states[_previousViewStateIndex].ViewExit(false);
                _previousViewStateIndex = viewState.stateIndex;
                _prevViewTransition = viewState.transition;
                if (_previousViewStateIndex > -1 && _states[_previousViewStateIndex] != null)
                    _states[_previousViewStateIndex].ViewEnter(false);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void Simulate(ref SMState state, float delta)
        {
            base.Simulate(ref state, delta);
            if (_states.Count == 0) return;

            int requestedIndex = state.wantedState;
            if (requestedIndex != -1)
            {
                // The request is consumed every tick, even when it has to be dropped.
                state.wantedState = -1;

                if (requestedIndex < 0 || requestedIndex >= _states.Count || _states[requestedIndex] == null)
                {
                    PurrLogger.LogError(
                        $"Can't switch state: wanted state index {requestedIndex} is invalid " +
                        $"for a machine with {_states.Count} states.", this);
                    requestedIndex = -1;
                }
            }

            // A stateIndex that changed without a wantedState (authoritative/verified state
            // applied directly) is also a transition: exit the node that is currently entered,
            // which is tracked by lastEnteredStateIndex - not stateIndex.
            bool externalChange = requestedIndex == -1 &&
                                  state.stateIndex > -1 &&
                                  state.stateIndex != state.lastEnteredStateIndex;

            if (requestedIndex != -1)
                ApplyTransition(ref state, requestedIndex, isRequested: true);
            else if (externalChange)
                ApplyTransition(ref state, state.stateIndex, isRequested: false);

            if (state.stateIndex > -1 && _states[state.stateIndex] != null)
                _states[state.stateIndex].StateSimulate(delta);

            // Same-tick flush: requests made by the code above - a node's Enter() or
            // StateSimulate() - are applied at the end of this very Simulate, so a transition
            // lands on the tick it was requested no matter where the request came from (before
            // this machine simulated, or from within it).
            //
            // Enter() may itself request another state (skip chains): loop until stable. Each
            // Enter() has fully returned before the next iteration applies its transition, so
            // there is no re-entrancy hazard. A request cycle would loop forever, hence the
            // chain guard: if hit, the leftover request is dropped and simply re-requested on
            // a later tick (deterministic on all simulators).
            int chainGuard = _states.Count + 1;
            while (state.wantedState != -1 && chainGuard-- > 0)
            {
                int queuedIndex = state.wantedState;
                state.wantedState = -1;

                if (queuedIndex < 0 || queuedIndex >= _states.Count || _states[queuedIndex] == null)
                {
                    PurrLogger.LogError(
                        $"Can't switch state: wanted state index {queuedIndex} is invalid " +
                        $"for a machine with {_states.Count} states.", this);
                    continue;
                }

                ApplyTransition(ref state, queuedIndex, isRequested: true);
            }

            if (state.wantedState != -1)
            {
                PurrLogger.LogWarning(
                    $"State change chain exceeded {_states.Count + 1} transitions in one tick on " +
                    $"machine {name}; dropping the leftover request.", this);
                state.wantedState = -1;
            }

            // Safety net: keep the cached node pointers in sync if the replicated stateIndex
            // was rewritten by code outside the FSM (should no longer be possible, but harmless).
            if (state.stateIndex > -1 && !ReferenceEquals(_currentStateNode, _states[state.stateIndex]))
            {
                _previousStateNode = _currentStateNode;
                _currentStateNode = _states[state.stateIndex];
                _nextStateNode = _states[(state.stateIndex + 1) % _states.Count];
            }
        }

        /// <summary>
        /// Exits the currently entered node (if any) and enters <paramref name="targetIndex"/>.
        /// Updates the replicated state (stateIndex, lastEnteredStateIndex, transition) and the
        /// cached node pointers BEFORE Enter() so a node can query the machine consistently from
        /// inside its own Enter().
        /// </summary>
        private void ApplyTransition(ref SMState state, int targetIndex, bool isRequested)
        {
            // On an explicit request we always exit first, even when re-entering the same state
            // (TryResetState / interrupt-self semantics). On an external change we exit the node
            // whose Enter() ran most recently (tracked by lastEnteredStateIndex - not stateIndex).
            int enteredIndex = isRequested ? state.stateIndex : state.lastEnteredStateIndex;
            IPredictedStateNodeBase oldState = null;
            if (enteredIndex > -1 && _states[enteredIndex] != null)
            {
                oldState = _states[enteredIndex];
                oldState.Exit();
            }

            var newState = _states[targetIndex];

            state.stateIndex = targetIndex;
            state.lastEnteredStateIndex = targetIndex;
            if (isRequested)
                state.transition++;

            _previousStateNode = oldState;
            _currentStateNode = newState;
            _nextStateNode = _states[(targetIndex + 1) % _states.Count];
            newState.Enter();
            onStateChanged?.Invoke(oldState, newState);
        }

        protected override SMState GetInitialState()
        {
            var defaultStateIndex = GetValidDefaultStateIndex();
            var state = new SMState()
            {
                wantedState = defaultStateIndex,
                stateIndex = -1,
                lastEnteredStateIndex = -1
            };

            return state;
        }

        // Resolves the state to navigate relative to before the machine has entered its first
        // state (stateIndex == -1): the default state, or index 0 if there is no default.
        private int GetNavigationBaseIndex()
        {
            if (currentState.stateIndex > -1)
                return currentState.stateIndex;

            var defaultStateIndex = GetValidDefaultStateIndex();
            if (defaultStateIndex > -1)
                return defaultStateIndex;

            return _states.Count > 0 ? 0 : -1;
        }

        /// <summary>
        /// Steps to the next state in the list. Safe before the first transition: navigates relative
        /// to the default state (or index 0). The transition applies on the same tick when requested
        /// from simulation code.
        /// </summary>
        public virtual void Next()
        {
            if (_states.Count == 0) return;
            var baseIndex = GetNavigationBaseIndex();
            if (baseIndex < 0) return;
            SetState((baseIndex + 1) % _states.Count);
        }

        /// <summary>
        /// Steps to the previous state in the list. Safe before the first transition (see <see cref="Next"/>).
        /// </summary>
        public virtual void Previous()
        {
            if (_states.Count == 0) return;
            var baseIndex = GetNavigationBaseIndex();
            if (baseIndex < 0) return;
            SetState((baseIndex - 1 + _states.Count) % _states.Count);
        }

        /// <summary>
        /// Requests a transition by state index. Re-entering the CURRENT state is allowed (Exit then
        /// Enter again, transition++) and is the TryResetState / interrupt-self semantic. The request
        /// is applied within the same simulation tick (see class remarks) - call this from simulation
        /// code only.
        /// </summary>
        public void SetState(int stateIndex)
        {
            if (stateIndex < 0 || stateIndex >= _states.Count || _states[stateIndex] == null)
            {
                PurrLogger.LogError(
                    $"Can't switch state: index {stateIndex} is invalid for a machine with " +
                    $"{_states.Count} states.", this);
                return;
            }

            SetWantedStateIndex(stateIndex);
        }

        public void SetState(IPredictedStateNodeBase state)
        {
            var index = _states.IndexOf(state);
            if (index == -1)
            {
                PurrLogger.LogError($"Can't switch state. Either state ({state}) is invalid, or doesn't exist in states list!", this);
                return;
            }

            SetState(index);
        }

        public int GetStateId(IPredictedStateNodeBase state)
        {
            return _states.IndexOf(state);
        }

        private void SetWantedStateIndex(int index)
        {
            var copy = currentState;
            copy.wantedState = index;
            currentState = copy;
        }

        public struct SMState : IPredictedData<SMState>
        {
            public int wantedState;
            public int stateIndex;
            public uint transition;

            public int lastEnteredStateIndex;

            public void Dispose() { }
        }
    }

    [System.Serializable]
    public class SerializableInterface<T> where T : class
    {
        [SerializeField] internal Object _object;

        public T Value
        {
            get
            {
                if (_object is GameObject go)
                {
                    return go.GetComponent<T>();
                }
                return _object as T;
            }
            set => _object = value as Object;
        }
    }
}
