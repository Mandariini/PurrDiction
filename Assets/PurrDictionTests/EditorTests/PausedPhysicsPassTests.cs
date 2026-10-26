using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public class PausedPhysicsPassTests
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _worldObject;
        private PredictionManager _world;
        private PredictedTime _time;
#if UNITY_PHYSICS_3D
        private SimulationMode _simulationMode3D;
        private Rigidbody _body3D;
#endif
#if UNITY_PHYSICS_2D
        private SimulationMode2D _simulationMode2D;
        private Rigidbody2D _body2D;
#endif

        [SetUp]
        public void SetUp()
        {
            _worldObject = new GameObject("Paused physics world");
            _world = _worldObject.AddComponent<PredictionManager>();
            typeof(PredictionManager).GetField("<tickDelta>k__BackingField", Flags).SetValue(_world, 1f / 20f);
            _time = _worldObject.AddComponent<PredictedTime>();
            typeof(PredictionManager).GetField("<time>k__BackingField", Flags).SetValue(_world, _time);

            PredictionPhysicsProvider providers = default;
#if UNITY_PHYSICS_3D
            _simulationMode3D = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            _body3D = new GameObject("Paused body 3D").AddComponent<Rigidbody>();
            _body3D.useGravity = false;
            providers |= PredictionPhysicsProvider.UnityPhysics3D;
#endif
#if UNITY_PHYSICS_2D
            _simulationMode2D = Physics2D.simulationMode;
            Physics2D.simulationMode = SimulationMode2D.Script;
            _body2D = new GameObject("Paused body 2D").AddComponent<Rigidbody2D>();
            _body2D.gravityScale = 0f;
            providers |= PredictionPhysicsProvider.UnityPhysics2D;
#endif
            typeof(PredictionManager).GetField("_physicsProvider", Flags).SetValue(_world, providers);
        }

        [TearDown]
        public void TearDown()
        {
#if UNITY_PHYSICS_3D
            Object.DestroyImmediate(_body3D.gameObject);
            Physics.simulationMode = _simulationMode3D;
#endif
#if UNITY_PHYSICS_2D
            Object.DestroyImmediate(_body2D.gameObject);
            Physics2D.simulationMode = _simulationMode2D;
#endif
            Object.DestroyImmediate(_worldObject);
        }

        [Test]
        public void ZeroTimeScaleSkipsPhysicsStepWithoutLogging()
        {
            int before = 0, after = 0;
            _world.onBeforePhysicsPass += () => before++;
            _world.onAfterPhysicsPass += () => after++;
            _time.currentState.timeScale = 0f;
            ResetBodies();

            PhysicsPass();

            LogAssert.NoUnexpectedReceived();
            Assert.That(before, Is.EqualTo(1));
            Assert.That(after, Is.EqualTo(1));
#if UNITY_PHYSICS_3D
            Assert.That(_body3D.position, Is.EqualTo(Vector3.zero));
#endif
#if UNITY_PHYSICS_2D
            Assert.That(_body2D.position, Is.EqualTo(Vector2.zero));
#endif
        }

        [Test]
        public void UnitTimeScaleStillStepsPhysics()
        {
            _time.currentState.timeScale = 1f;
            ResetBodies();

            PhysicsPass();

#if UNITY_PHYSICS_3D
            Assert.That(_body3D.position.x, Is.GreaterThan(0f));
#endif
#if UNITY_PHYSICS_2D
            Assert.That(_body2D.position.x, Is.GreaterThan(0f));
#endif
        }

        private void ResetBodies()
        {
#if UNITY_PHYSICS_3D
            _body3D.position = Vector3.zero;
            _body3D.linearVelocity = Vector3.right;
#endif
#if UNITY_PHYSICS_2D
            _body2D.position = Vector2.zero;
            _body2D.linearVelocity = Vector2.right;
#endif
        }

        private void PhysicsPass()
        {
            var method = typeof(PredictionManager).GetMethod("DoPhysicsPass", Flags);
            var scope = Activator.CreateInstance(method.GetParameters()[0].ParameterType);
            method.Invoke(_world, new[] { scope });
        }
    }
}
