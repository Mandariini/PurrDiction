using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class HierarchyBaselineCachingTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void SharedAndDivergentBaselinesKeepTheirOwnTopologyAndExpiredBaselinesAreInvalidated()
        {
            var managerObject = new GameObject("Baseline cache manager");
            var hierarchyObject = new GameObject("Baseline cache hierarchy");
            var history = new History<FULL_STATE<PredictedHierarchyState>>(600);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                Set(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", hierarchy);
                Set(typeof(PredictedIdentity<PredictedHierarchyState>), hierarchy, "_verifiedHistory", history);
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", 20UL);
                history.Write(10, Topology(2));
                history.Write(11, Topology(3));

                var first = Get(manager, 10);
                Assert.That(Get(manager, 10), Is.SameAs(first), "receivers with the same ACK share the scan");
                var second = Get(manager, 11);
                Assert.That(second, Is.Not.SameAs(first));
                Assert.That(Roots(first), Is.EquivalentTo(new[] { new PredictedObjectID(2) }));
                Assert.That(Roots(second), Is.EquivalentTo(new[] { new PredictedObjectID(3) }));

                history.ClearPast(11);
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", 21UL);
                var expired = Get(manager, 10);
                Assert.That(expired.GetType().GetField("hasBaseline").GetValue(expired), Is.False);
                Assert.That(Roots(expired), Is.Empty, "a stalled ACK must not reuse a pruned baseline");
                Assert.That(Roots(Get(manager, 11)), Is.EquivalentTo(new[] { new PredictedObjectID(3) }));
            }
            finally
            {
                history.Clear();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void CachedMembershipSurvivesStableTicksAndRejectsAReusedPooledList()
        {
            var managerObject = new GameObject("Stable baseline manager");
            var hierarchyObject = new GameObject("Stable baseline hierarchy");
            var history = new History<FULL_STATE<PredictedHierarchyState>>(600);
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                Set(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", hierarchy);
                Set(typeof(PredictedIdentity<PredictedHierarchyState>), hierarchy, "_verifiedHistory", history);
                var original = Topology(2);
                var originalList = original.state.spawnedPrefabs.list;
                history.Write(10, original);
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", 20UL);
                var first = Get(manager, 10);
                for (ulong tick = 21; tick <= 30; tick++)
                {
                    Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", tick);
                    Assert.That(Roots(Get(manager, 10)), Is.EquivalentTo(new[] { new PredictedObjectID(2) }));
                }

                history.Clear();
                Assert.That(original.state.spawnedPrefabs.isDisposed, Is.True,
                    "the membership cache must not extend ownership of a historical snapshot");
                var replacement = Topology(3, originalList.Capacity);
                Assert.That(replacement.state.spawnedPrefabs.list, Is.SameAs(originalList),
                    "exercise pool reuse with identical list identity and count");
                history.Write(12, replacement);
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", 31UL);
                var refreshed = Get(manager, 12);
                Assert.That(refreshed, Is.SameAs(first));
                Assert.That(Roots(refreshed), Is.EquivalentTo(new[] { new PredictedObjectID(3) }));

                history.Clear();
                history.Write(13, default);
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", 32UL);
                Assert.That(Roots(Get(manager, 13)), Is.Empty, "an empty topology clears previous membership");
            }
            finally
            {
                history.Clear();
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        static FULL_STATE<PredictedHierarchyState> Topology(uint id, int capacity = 1)
        {
            var records = DisposableList<InstanceDetails>.Create(capacity);
            records.Add(new InstanceDetails(0, new PredictedObjectID(id), Vector3.zero, Quaternion.identity, null));
            return new FULL_STATE<PredictedHierarchyState>
            {
                state = new PredictedHierarchyState(records, DisposableList<PredictedObjectID>.Create(0), id + 1)
            };
        }

        static object Get(PredictionManager manager, ulong tick) =>
            typeof(PredictionManager).GetMethod("GetHierarchyBaselineScratch", Fields).Invoke(manager, new object[] { tick });

        static PredictionObjectSet Roots(object scratch) =>
            (PredictionObjectSet)scratch.GetType().GetField("roots").GetValue(scratch);

        static void Set(Type type, object target, string name, object value) =>
            type.GetField(name, Fields).SetValue(target, value);
    }
}
