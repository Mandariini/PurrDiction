using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public class PredictionOwnershipTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp]
        public void SetUp()
        {
            TrackedDisposable.Reset();
        }

        [Test]
        public void SparseHistoryPrunesByTickAgeAndKeepsRollbackAnchor()
        {
            var history = new History<TrackedDisposable>(10);
            history.Write(1, new TrackedDisposable(1));
            history.Write(20, new TrackedDisposable(2));
            history.Write(95, new TrackedDisposable(3));

            history.PruneByTickWindow(100);

            Assert.That(history.Count, Is.EqualTo(2));
            Assert.That(history.OldestTick, Is.EqualTo(20));
            Assert.That(history.ReadOrPrevious(90, out var anchor), Is.True);
            Assert.That(anchor.id, Is.EqualTo(2));
            Assert.That(TrackedDisposable.disposeCount, Is.EqualTo(1));

            history.PruneByTickWindow(120);

            Assert.That(history.Count, Is.EqualTo(1));
            Assert.That(history.OldestTick, Is.EqualTo(95));
            Assert.That(TrackedDisposable.disposeCount, Is.EqualTo(2));

            history.Clear();
            Assert.That(TrackedDisposable.disposeCount, Is.EqualTo(3));
        }

        [Test]
        public void StatefulInputIdentityReleasesEveryOwnedInputWhenPooled()
        {
            var gameObject = new GameObject(nameof(StatefulInputIdentityReleasesEveryOwnedInputWhenPooled));
            try
            {
                var identity = gameObject.AddComponent<StatefulInputProbe>();
                var history = SeedInputStorage(identity, typeof(PredictedIdentity<TrackedInput, EmptyState>));

                identity.ReleasePredictionStateForPool();

                Assert.That(history.Count, Is.Zero);
                Assert.That(TrackedInput.disposeCount, Is.EqualTo(6));

                identity.ReleasePredictionStateForPool();
                Assert.That(TrackedInput.disposeCount, Is.EqualTo(6));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DeterministicInputIdentityReleasesEveryOwnedInputWhenPooled()
        {
            var gameObject = new GameObject(nameof(DeterministicInputIdentityReleasesEveryOwnedInputWhenPooled));
            try
            {
                var identity = gameObject.AddComponent<DeterministicInputProbe>();
                var history = SeedInputStorage(identity, typeof(DeterministicIdentity<TrackedInput, EmptyState>));

                identity.ReleasePredictionStateForPool();

                Assert.That(history.Count, Is.Zero);
                Assert.That(TrackedInput.disposeCount, Is.EqualTo(6));

                identity.ReleasePredictionStateForPool();
                Assert.That(TrackedInput.disposeCount, Is.EqualTo(6));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

#if UNITY_PHYSICS_3D
        [Test]
        public void ProjectileInterpolationDoesNotAllocateDisposableViewState()
        {
            var gameObject = new GameObject(nameof(ProjectileInterpolationDoesNotAllocateDisposableViewState));
            try
            {
                var projectile = gameObject.AddComponent<ProjectileInterpolationProbe>();
                var result = projectile.InterpolateForTest(default, default, 0.5f);

                Assert.That(result.overlappingTriggers.isDisposed, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
#endif

        [Test]
        public void ReparentingIdentityRefreshesNearestPredictionPolicyScope()
        {
            var managerObject = new GameObject("PredictionManager");
            var firstParent = new GameObject("FirstScope");
            var secondParent = new GameObject("SecondScope");
            var identityObject = new GameObject("PolicyProbe");

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var firstScope = firstParent.AddComponent<PredictionPolicyScope>();
                var secondScope = secondParent.AddComponent<PredictionPolicyScope>();
                firstScope.configuredPredictionPolicy = PredictionPolicy.ServerRelay;
                secondScope.configuredPredictionPolicy = PredictionPolicy.FullPrediction;

                identityObject.transform.SetParent(firstParent.transform);
                var identity = identityObject.AddComponent<PolicyProbe>();
                identity.AttachForTest(manager);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                identityObject.transform.SetParent(secondParent.transform);
                identity.RefreshParentScopeForTest();
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                identity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(firstParent);
                UnityEngine.Object.DestroyImmediate(secondParent);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        private static History<TrackedInput> SeedInputStorage(Component identity, Type identityBaseType)
        {
            var history = new History<TrackedInput>(8);
            history.Write(1, new TrackedInput(1));
            history.Write(2, new TrackedInput(2));

            SetField(identityBaseType, identity, "_inputHistory", history);
            SetField(identityBaseType, identity, "_currentInput", new TrackedInput(3));
            SetField(identityBaseType, identity, "_nextInput", new TrackedInput(4));
            SetField(identityBaseType, identity, "_lastInput", (TrackedInput?)new TrackedInput(5));
            SetField(identityBaseType, identity, "_queuedInput", (TrackedInput?)new TrackedInput(6));
            return history;
        }

        private static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }

        private readonly struct TrackedDisposable : IDisposable
        {
            public static int disposeCount;
            public readonly int id;

            public TrackedDisposable(int id)
            {
                this.id = id;
            }

            public static void Reset()
            {
                disposeCount = 0;
                TrackedInput.disposeCount = 0;
            }

            public void Dispose()
            {
                if (id != 0)
                    disposeCount++;
            }
        }
    }

    public struct TrackedInput : IPredictedData
    {
        public static int disposeCount;
        public int id;

        public TrackedInput(int id)
        {
            this.id = id;
        }

        public void Dispose()
        {
            if (id != 0)
                disposeCount++;
        }
    }

    public struct EmptyState : IPredictedData<EmptyState>
    {
        public void Dispose() { }
    }

    public sealed class StatefulInputProbe : PredictedIdentity<TrackedInput, EmptyState>
    {
        protected override void Simulate(TrackedInput input, ref EmptyState state, float delta) { }
    }

    public sealed class DeterministicInputProbe : DeterministicIdentity<TrackedInput, EmptyState>
    {
        protected override void Simulate(TrackedInput input, ref EmptyState state, sfloat delta) { }
    }

#if UNITY_PHYSICS_3D
    public sealed class ProjectileInterpolationProbe : PredictedProjectile3D
    {
        public ProjectileState3D InterpolateForTest(ProjectileState3D from, ProjectileState3D to, float t)
            => base.Interpolate(from, to, t);
    }
#endif

    public sealed class PolicyProbe : PredictedIdentity<EmptyState>
    {
        public void AttachForTest(PredictionManager manager)
        {
            predictionManager = manager;
            RefreshResolvedPredictionPolicy();
        }

        public void DetachForTest()
        {
            predictionManager = null;
        }

        public void RefreshParentScopeForTest()
        {
            base.OnTransformParentChanged();
        }
    }
}
