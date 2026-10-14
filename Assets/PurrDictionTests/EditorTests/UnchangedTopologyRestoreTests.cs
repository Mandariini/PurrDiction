using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class UnchangedTopologyRestoreTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void EqualTopologyPreservesTheDependencyCacheButResetsEntrantsAndRestoresTheNextId()
        {
            var hierarchyObject = new GameObject("Unchanged topology");
            var piece = new GameObject("Existing piece");
            var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
            var record = new InstanceDetails(0, new PredictedObjectID(2), Vector3.zero, Quaternion.identity, null);
            var records = DisposableList<InstanceDetails>.Create(1);
            records.Add(record);
            var state = new PredictedHierarchyState(records, DisposableList<PredictedObjectID>.Create(0), 99);
            try
            {
                Field<List<InstanceDetails>>(hierarchy, "_spawnedPrefabs").Add(record);
                Field<Dictionary<PredictedObjectID, InstanceDetails>>(hierarchy, "_recordsById").Add(record.instanceId, record);
                Field<Dictionary<PredictedObjectID, GameObject>>(hierarchy, "_instanceMap").Add(record.instanceId, piece);
                Field<HashSet<PredictedObjectID>>(hierarchy, "_verifiedApplyEntrants").Add(record.instanceId);
                typeof(PredictedHierarchy).GetField("_visibilityDependencyCacheDirty", Fields).SetValue(hierarchy, false);

                hierarchy.ApplyHistoricalTopology(state);
                Assert.That(Field<bool>(hierarchy, "_visibilityDependencyCacheDirty"), Is.False);
                Assert.That(hierarchy.WasMaterializedByVerifiedApply(record.instanceId), Is.False);
                Assert.That(Field<uint>(hierarchy, "_nextInstanceId"), Is.EqualTo(99));
                Assert.That(Field<bool>(hierarchy, "_isRollingBack"), Is.False);
                Assert.That(Field<Dictionary<PredictedObjectID, GameObject>>(hierarchy, "_instanceMap")[record.instanceId], Is.SameAs(piece));
            }
            finally
            {
                state.Dispose();
                hierarchy.ResetState();
                Object.DestroyImmediate(piece);
                Object.DestroyImmediate(hierarchyObject);
            }
        }

        [Test]
        public void ChangedTopologyUsesTheRestorePathAndInvalidatesDependencies()
        {
            var hierarchyObject = new GameObject("Changed topology");
            var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
            var old = new InstanceDetails(0, new PredictedObjectID(2), Vector3.zero, Quaternion.identity, null);
            var state = new PredictedHierarchyState(DisposableList<InstanceDetails>.Create(0),
                DisposableList<PredictedObjectID>.Create(0), 3);
            try
            {
                Field<List<InstanceDetails>>(hierarchy, "_spawnedPrefabs").Add(old);
                Field<Dictionary<PredictedObjectID, InstanceDetails>>(hierarchy, "_recordsById").Add(old.instanceId, old);
                typeof(PredictedHierarchy).GetField("_visibilityDependencyCacheDirty", Fields).SetValue(hierarchy, false);
                hierarchy.ApplyHistoricalTopology(state);
                Assert.That(Field<bool>(hierarchy, "_visibilityDependencyCacheDirty"), Is.True);
                Assert.That(Field<List<InstanceDetails>>(hierarchy, "_spawnedPrefabs"), Is.Empty);
                Assert.That(Field<Dictionary<PredictedObjectID, InstanceDetails>>(hierarchy, "_recordsById"), Is.Empty);
            }
            finally
            {
                state.Dispose();
                hierarchy.ResetState();
                Object.DestroyImmediate(hierarchyObject);
            }
        }

        private static T Field<T>(PredictedHierarchy hierarchy, string name)
            => (T)typeof(PredictedHierarchy).GetField(name, Fields).GetValue(hierarchy);
    }
}
