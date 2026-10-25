using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Prediction.StateMachine;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// A pooled state machine keeps its node components, but not the life that was running in
    /// them. Reset must therefore exit the node which was actually entered, restore the cached
    /// pointers to the default state, and rewind the view latch so the next life enters its view
    /// state even when it starts in the same (stateIndex, transition) pair as the previous one.
    /// </summary>
    public sealed class PredictedStateMachinePoolingTests
    {
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        private GameObject _machineGo;
        private PredictedStateMachine _machine;
        private FakePredictedStateNode _node;

        [SetUp]
        public void SetUp()
        {
            _machineGo = new GameObject("Predicted state machine");
            _node = ScriptableObject.CreateInstance<FakePredictedStateNode>();
            _machine = _machineGo.AddComponent<PredictedStateMachine>();

            var wrapped = new List<SerializableInterface<IPredictedStateNodeBase>>
            {
                new SerializableInterface<IPredictedStateNodeBase> { Value = _node }
            };
            SetField(_machine, "_wrappedStates", wrapped);

            // Edit mode does not run Awake for a plain MonoBehaviour.
            typeof(PredictedStateMachine)
                .GetMethod("Awake", InstancePrivate)
                .Invoke(_machine, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (_machineGo)
                Object.DestroyImmediate(_machineGo);
            if (_node)
                Object.DestroyImmediate(_node);
        }

        [Test]
        public void ResetStateExitsTheEnteredNodeAndClearsLocalBookkeeping()
        {
            var state = EnterDefaultState();

            InvokeUpdateView(state);
            Assert.That(_node.viewEnterCount, Is.EqualTo(1));
            Assert.That(GetField<int>(_machine, "_previousViewStateIndex"), Is.EqualTo(0));

            _machine.ResetState();

            Assert.That(_node.exitCount, Is.EqualTo(1), "the pooled node must be exited");
            Assert.That(GetField<int>(_machine, "_enteredStateIndex"), Is.EqualTo(-1));
            Assert.That(_machine._previousStateNode, Is.Null);
            Assert.That(_machine._nextStateNode, Is.Null);
            Assert.That(_machine._currentStateNode, Is.SameAs(_node),
                "the cached pointer returns to the default state until the next life enters it");
            Assert.That(GetField<int>(_machine, "_previousViewStateIndex"), Is.EqualTo(-1));
            Assert.That(GetField<uint>(_machine, "_prevViewTransition"), Is.EqualTo(0u));
            Assert.That(GetField<int>(_machine, "_previousVerifiedViewStateIndex"), Is.EqualTo(-1));
            Assert.That(GetField<uint>(_machine, "_prevVerifiedViewTransition"), Is.EqualTo(0u));

            // ResetState and pool release may both run for the same instance; the node must be
            // exited exactly once.
            _machine.ResetState();
            Assert.That(_node.exitCount, Is.EqualTo(1));
        }

        [Test]
        public void ViewEnterFiresAgainWhenTheNextLifeStartsInTheSameState()
        {
            var state = EnterDefaultState();

            InvokeUpdateView(state);
            Assert.That(_node.viewEnterCount, Is.EqualTo(1));

            _machine.ResetState();
            Assert.That(_node.viewExitCount, Is.EqualTo(0));

            // Same stateIndex/transition as the previous life. Without rewinding the view latch
            // this is swallowed as "no change" and the new life never enters its view state.
            InvokeUpdateView(state);
            Assert.That(_node.viewEnterCount, Is.EqualTo(2));
        }

        [Test]
        public void ReleasingForPoolExitsTheEnteredNodeAndClearsLocalBookkeeping()
        {
            EnterDefaultState();

            _machine.ReleasePredictionStateForPool();

            Assert.That(_node.exitCount, Is.EqualTo(1));
            Assert.That(GetField<int>(_machine, "_enteredStateIndex"), Is.EqualTo(-1));
            Assert.That(_machine._previousStateNode, Is.Null);
            Assert.That(_machine._nextStateNode, Is.Null);
            Assert.That(_machine._currentStateNode, Is.SameAs(_node));
            Assert.That(GetField<int>(_machine, "_previousViewStateIndex"), Is.EqualTo(-1));
        }

        private PredictedStateMachine.SMState EnterDefaultState()
        {
            var state = new PredictedStateMachine.SMState
            {
                wantedState = -1,
                stateIndex = -1,
                lastEnteredStateIndex = -1
            };
            var applyTransition = typeof(PredictedStateMachine)
                .GetMethod("ApplyTransition", InstancePrivate);
            var args = new object[] { state, 0, true };
            applyTransition.Invoke(_machine, args);
            state = (PredictedStateMachine.SMState)args[0];

            Assert.That(_node.enterCount, Is.EqualTo(1));
            Assert.That(state.stateIndex, Is.EqualTo(0));
            Assert.That(state.lastEnteredStateIndex, Is.EqualTo(0));
            return state;
        }

        private void InvokeUpdateView(PredictedStateMachine.SMState state)
        {
            var updateView = typeof(PredictedStateMachine).GetMethod(
                "UpdateView", InstancePrivate, null,
                new[] { typeof(PredictedStateMachine.SMState), typeof(PredictedStateMachine.SMState?) },
                null);
            updateView.Invoke(_machine, new object[] { state, null });
        }

        private static T GetField<T>(object target, string name)
            => (T)target.GetType().GetField(name, InstancePrivate).GetValue(target);

        private static void SetField(object target, string name, object value)
            => target.GetType().GetField(name, InstancePrivate).SetValue(target, value);
    }
}
