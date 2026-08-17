using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class RigidbodyForceMathTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private static PredictedRigidbody CreateBody(
            GameObject bodyObject,
            out Rigidbody rigidbody)
        {
            rigidbody = bodyObject.AddComponent<Rigidbody>();
            rigidbody.useGravity = false;
            rigidbody.maxAngularVelocity = 1000f;

            var predicted = bodyObject.AddComponent<PredictedRigidbody>();
            var field = typeof(PredictedRigidbody).GetField("_rigidbody", InstanceFields);
            Assert.That(field, Is.Not.Null);
            field.SetValue(predicted, rigidbody);
            return predicted;
        }

        [Test]
        public void TorqueImpulseDividesByThePrincipalInertiaNotMass()
        {
            var bodyObject = new GameObject("Tensor torque body");
            try
            {
                var predicted = CreateBody(bodyObject, out var rigidbody);
                rigidbody.mass = 3f;
                rigidbody.inertiaTensor = new Vector3(2f, 4f, 8f);
                rigidbody.inertiaTensorRotation = Quaternion.identity;

                predicted.AddTorque(new Vector3(0f, 6f, 0f), ForceMode.Impulse);

                var omega = rigidbody.angularVelocity;
                Assert.That(omega.x, Is.EqualTo(0f).Within(1e-4f));
                Assert.That(omega.y, Is.EqualTo(6f / 4f).Within(1e-4f),
                    "torque must divide by the y principal inertia, not mass");
                Assert.That(omega.z, Is.EqualTo(0f).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(bodyObject);
            }
        }

        [Test]
        public void TorqueFollowsTheRotatedInertiaTensorFrame()
        {
            var bodyObject = new GameObject("Rotated tensor body");
            try
            {
                var predicted = CreateBody(bodyObject, out var rigidbody);
                bodyObject.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                Physics.SyncTransforms();
                rigidbody.inertiaTensor = new Vector3(2f, 4f, 8f);
                rigidbody.inertiaTensorRotation = Quaternion.identity;

                predicted.AddTorque(new Vector3(16f, 0f, 0f), ForceMode.Impulse);

                var omega = rigidbody.angularVelocity;
                Assert.That(omega.x, Is.EqualTo(16f / 8f).Within(1e-3f),
                    "world-x torque on a 90-degree-yawed body acts through the z principal inertia");
                Assert.That(omega.y, Is.EqualTo(0f).Within(1e-3f));
                Assert.That(omega.z, Is.EqualTo(0f).Within(1e-3f));
            }
            finally
            {
                Object.DestroyImmediate(bodyObject);
            }
        }

        [Test]
        public void ZeroInertiaComponentAbsorbsTorqueOnThatAxis()
        {
            var bodyObject = new GameObject("Locked axis body");
            try
            {
                var predicted = CreateBody(bodyObject, out var rigidbody);
                rigidbody.inertiaTensor = new Vector3(2f, 0f, 8f);
                rigidbody.inertiaTensorRotation = Quaternion.identity;

                predicted.AddTorque(new Vector3(0f, 5f, 0f), ForceMode.Impulse);

                Assert.That(rigidbody.angularVelocity.magnitude, Is.EqualTo(0f).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(bodyObject);
            }
        }

        [Test]
        public void RelativeTorqueIgnoresTransformScale()
        {
            var bodyObject = new GameObject("Scaled relative torque body");
            try
            {
                var predicted = CreateBody(bodyObject, out var rigidbody);
                bodyObject.transform.localScale = new Vector3(2f, 2f, 2f);
                rigidbody.inertiaTensor = Vector3.one;
                rigidbody.inertiaTensorRotation = Quaternion.identity;

                predicted.AddRelativeTorque(new Vector3(0f, 0f, 3f), ForceMode.Impulse);

                Assert.That(rigidbody.angularVelocity.z, Is.EqualTo(3f).Within(1e-4f),
                    "local torque must rotate without picking up lossy scale");
            }
            finally
            {
                Object.DestroyImmediate(bodyObject);
            }
        }

        private static PredictedRigidbody2D CreateBody2D(
            GameObject bodyObject,
            out Rigidbody2D rigidbody)
        {
            rigidbody = bodyObject.AddComponent<Rigidbody2D>();
            rigidbody.gravityScale = 0f;
            rigidbody.useAutoMass = false;

            var predicted = bodyObject.AddComponent<PredictedRigidbody2D>();
            var field = typeof(PredictedRigidbody2D).GetField("_rigidbody", InstanceFields);
            Assert.That(field, Is.Not.Null);
            field.SetValue(predicted, rigidbody);
            return predicted;
        }

        [Test]
        public void Torque2DImpulseDividesByInertiaInDegrees()
        {
            var bodyObject = new GameObject("Tensor torque body 2d");
            try
            {
                var predicted = CreateBody2D(bodyObject, out var rigidbody);
                rigidbody.mass = 3f;
                rigidbody.inertia = 5f;

                predicted.AddTorque(10f, ForceMode2D.Impulse);

                Assert.That(
                    rigidbody.angularVelocity,
                    Is.EqualTo(10f / 5f * Mathf.Rad2Deg).Within(1e-2f),
                    "2D torque must divide by the moment of inertia and convert to degrees");
            }
            finally
            {
                Object.DestroyImmediate(bodyObject);
            }
        }

        [Test]
        public void ForceAtPosition2DUsesTheCrossProductTorque()
        {
            var bodyObject = new GameObject("Cross torque body 2d");
            try
            {
                var predicted = CreateBody2D(bodyObject, out var rigidbody);
                rigidbody.mass = 1f;
                rigidbody.inertia = 4f;

                predicted.AddForceAtPosition(
                    new Vector2(0f, 8f),
                    rigidbody.worldCenterOfMass + new Vector2(2f, 0f),
                    ForceMode2D.Impulse);

                Assert.That(
                    rigidbody.angularVelocity,
                    Is.EqualTo(2f * 8f / 4f * Mathf.Rad2Deg).Within(1e-2f),
                    "an off-center +y force to the right of the center must spin counter-clockwise by r x F");
                Assert.That(rigidbody.linearVelocity.y, Is.EqualTo(8f).Within(1e-3f));
            }
            finally
            {
                Object.DestroyImmediate(bodyObject);
            }
        }
    }
}
