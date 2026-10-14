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

        static FULL_STATE<PredictedHierarchyState> Topology(uint id)
        {
            var records = DisposableList<InstanceDetails>.Create(1);
            records.Add(new InstanceDetails(0, new PredictedObjectID(id), Vector3.zero, Quaternion.identity, null));
            return new FULL_STATE<PredictedHierarchyState>
            {
                state = new PredictedHierarchyState(records, DisposableList<PredictedObjectID>.Create(0), id + 1)
            };
        }

        static object Get(PredictionManager manager, ulong tick) =>
            typeof(PredictionManager).GetMethod("GetHierarchyBaselineScratch", Fields).Invoke(manager, new object[] { tick });

        static HashSet<PredictedObjectID> Roots(object scratch) =>
            (HashSet<PredictedObjectID>)scratch.GetType().GetField("roots").GetValue(scratch);

        static void Set(Type type, object target, string name, object value) =>
            type.GetField(name, Fields).SetValue(target, value);
    }
}
