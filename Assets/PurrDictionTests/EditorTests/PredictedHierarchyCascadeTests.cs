using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public class PredictedHierarchyCascadeTests
    {
        readonly List<GameObject> _cleanup = new ();

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _cleanup.Count; i++)
            {
                if (_cleanup[i])
                    Object.DestroyImmediate(_cleanup[i]);
            }

            _cleanup.Clear();
        }

        GameObject Track(GameObject go)
        {
            _cleanup.Add(go);
            return go;
        }

        GameObject BuildSimpleRig(string name, Vector3 position, bool withPredictedParent = false)
        {
            var root = Track(new GameObject(name));
            root.transform.position = position;
            root.AddComponent<PredictedGameObject>();

            if (withPredictedParent)
                root.AddComponent<PredictedParent>();

            return root;
        }

        [Test]
        public void DeletingRootCascadesIntoStateNestedInstances()
        {
            var hierarchy = CreateHierarchyWorld();

            var a = BuildSimpleRig("A", Vector3.zero);
            var b = BuildSimpleRig("B", new Vector3(2, 0, 0));
            var c = BuildSimpleRig("C", new Vector3(4, 0, 0));

            b.transform.SetParent(a.transform, true);
            c.transform.SetParent(b.transform, true);

            hierarchy.ReserveSceneObject(a, -1);
            hierarchy.ReserveSceneObject(b, -2);
            hierarchy.ReserveSceneObject(c, -3);
            hierarchy.RegisterReservedSceneObjects();

            Assert.That(hierarchy.TryGetId(a, out var aId), Is.True);
            Assert.That(hierarchy.TryGetId(b, out var bId), Is.True);
            Assert.That(hierarchy.TryGetId(c, out var cId), Is.True);

            var records = GetRecords(hierarchy);
            Assert.That(records[bId].parent, Is.Not.Null,
                "sanity: B must be state-parented under A through the authored link");
            Assert.That(records[bId].parent.Value.objectId, Is.EqualTo(aId));
            Assert.That(records[cId].parent, Is.Not.Null);
            Assert.That(records[cId].parent.Value.objectId, Is.EqualTo(bId));

            hierarchy.ApplyRemoteVisibilityDelete(aId);

            Assert.That(hierarchy.TryGetRootId(aId, out _), Is.False);
            Assert.That(hierarchy.TryGetRootId(bId, out _), Is.False,
                "a state-nested child instance must be deleted with its parent");
            Assert.That(hierarchy.TryGetGameObject(bId, out _), Is.False);
            Assert.That(hierarchy.TryGetRootId(cId, out _), Is.False,
                "the cascade must be transitive through state parent links");
            Assert.That(hierarchy.TryGetGameObject(cId, out _), Is.False);
        }

        [Test]
        public void DeletingMiddleOfChainSparesTheParent()
        {
            var hierarchy = CreateHierarchyWorld();

            var a = BuildSimpleRig("A", Vector3.zero);
            var b = BuildSimpleRig("B", new Vector3(2, 0, 0));
            var c = BuildSimpleRig("C", new Vector3(4, 0, 0));

            b.transform.SetParent(a.transform, true);
            c.transform.SetParent(b.transform, true);

            hierarchy.ReserveSceneObject(a, -1);
            hierarchy.ReserveSceneObject(b, -2);
            hierarchy.ReserveSceneObject(c, -3);
            hierarchy.RegisterReservedSceneObjects();

            Assert.That(hierarchy.TryGetId(a, out var aId), Is.True);
            Assert.That(hierarchy.TryGetId(b, out var bId), Is.True);
            Assert.That(hierarchy.TryGetId(c, out var cId), Is.True);

            hierarchy.ApplyRemoteVisibilityDelete(bId);

            Assert.That(hierarchy.TryGetRootId(aId, out _), Is.True,
                "deleting a child instance must never reach upward to its parent");
            Assert.That(hierarchy.TryGetGameObject(aId, out _), Is.True);
            Assert.That(hierarchy.TryGetRootId(bId, out _), Is.False);
            Assert.That(hierarchy.TryGetRootId(cId, out _), Is.False,
                "the cascade must follow state parent links below the deleted instance");
        }

        [Test]
        public void DeletingRootRescuesDecorationOnlyNestedInstances()
        {
            var hierarchy = CreateHierarchyWorld();

            var a = BuildSimpleRig("A", Vector3.zero);
            var b = BuildSimpleRig("B", new Vector3(2, 0, 0));

            hierarchy.ReserveSceneObject(a, -1);
            hierarchy.ReserveSceneObject(b, -2);
            hierarchy.RegisterReservedSceneObjects();

            Assert.That(hierarchy.TryGetId(a, out var aId), Is.True);
            Assert.That(hierarchy.TryGetId(b, out var bId), Is.True);

            b.transform.SetParent(a.transform, true);

            hierarchy.ApplyRemoteVisibilityDelete(aId);

            Assert.That(hierarchy.TryGetRootId(bId, out _), Is.True,
                "decoration-only nesting is client-local; the simulation must not delete it");
            Assert.That(hierarchy.TryGetGameObject(bId, out var bGo), Is.True);
            Assert.That(bGo.transform.parent, Is.Null,
                "the rescued instance must be detached from the deleted subtree");
            Assert.That(Vector3.Distance(bGo.transform.position, new Vector3(2, 0, 0)), Is.LessThan(1e-4f),
                "the rescue must preserve the world pose");
        }

        [Test]
        public void DeletingRuntimeParentCascadesIntoPredictedParentChild()
        {
            var hierarchy = CreateHierarchyWorld();

            var a = BuildSimpleRig("A", Vector3.zero);
            var b = BuildSimpleRig("B", new Vector3(2, 0, 0), withPredictedParent: true);

            hierarchy.ReserveSceneObject(a, -1);
            hierarchy.ReserveSceneObject(b, -2);
            hierarchy.RegisterReservedSceneObjects();

            Assert.That(hierarchy.TryGetId(a, out var aId), Is.True);
            Assert.That(hierarchy.TryGetId(b, out var bId), Is.True);

            b.transform.SetParent(a.transform, true);
            b.GetComponent<PredictedParent>().RefreshParentLink();

            hierarchy.ApplyRemoteVisibilityDelete(aId);

            Assert.That(hierarchy.TryGetRootId(bId, out _), Is.False,
                "a PredictedParent deviation is state; the cascade must follow it");
            Assert.That(hierarchy.TryGetGameObject(bId, out _), Is.False);
        }

        [Test]
        public void DeletingOldSpawnParentRewritesSurvivorRecords()
        {
            var hierarchy = CreateHierarchyWorld();

            var a = BuildSimpleRig("A", Vector3.zero);
            var b = BuildSimpleRig("B", new Vector3(2, 0, 0), withPredictedParent: true);
            b.transform.SetParent(a.transform, true);

            hierarchy.ReserveSceneObject(a, -1);
            hierarchy.ReserveSceneObject(b, -2);
            hierarchy.RegisterReservedSceneObjects();

            Assert.That(hierarchy.TryGetId(a, out var aId), Is.True);
            Assert.That(hierarchy.TryGetId(b, out var bId), Is.True);

            b.transform.SetParent(null, true);
            b.transform.position = new Vector3(5, 0, 0);
            b.GetComponent<PredictedParent>().RefreshParentLink();

            hierarchy.ApplyRemoteVisibilityDelete(aId);

            Assert.That(hierarchy.TryGetGameObject(bId, out var bGo), Is.True,
                "an instance reparented away through PredictedParent must survive its old spawn parent");

            var record = GetRecords(hierarchy)[bId];
            Assert.That(record.parent, Is.Null,
                "the survivor's record must stop naming the deleted spawn parent");
            Assert.That(Vector3.Distance(record.spawnPosition, bGo.transform.position), Is.LessThan(1e-4f),
                "the survivor's spawn pose must be rebased to world space");
        }

        [Test]
        public void DecorationParentSurvivesKillAndResurrect()
        {
            var hierarchy = CreateHierarchyWorld();

            var a = BuildSimpleRig("A", Vector3.zero);
            var b = BuildSimpleRig("B", new Vector3(2, 0, 0));

            hierarchy.ReserveSceneObject(a, -1);
            hierarchy.ReserveSceneObject(b, -2);
            hierarchy.RegisterReservedSceneObjects();

            Assert.That(hierarchy.TryGetId(a, out var aId), Is.True);
            Assert.That(hierarchy.TryGetId(b, out var bId), Is.True);

            b.transform.SetParent(a.transform, true);

            var fullState = BuildState(hierarchy, null);
            var withoutB = BuildState(hierarchy, record => !record.rootId.Equals(bId));

            try
            {
                InvokeSetUnityState(hierarchy, withoutB);
                Assert.That(hierarchy.TryGetGameObject(bId, out _), Is.False,
                    "sanity: applying a state without B must remove it");

                InvokeSetUnityState(hierarchy, fullState);
                Assert.That(hierarchy.TryGetGameObject(bId, out var bGo), Is.True,
                    "sanity: applying the full state must resurrect B");
                Assert.That(bGo.transform.parent, Is.EqualTo(a.transform),
                    "client-local decoration must be restored when a piece is resurrected");
                Assert.That(Vector3.Distance(bGo.transform.position, new Vector3(2, 0, 0)), Is.LessThan(1e-4f));
            }
            finally
            {
                fullState.Dispose();
                withoutB.Dispose();
            }
        }

        [Test]
        public void RescuedDecorationReattachesWhenParentResurrects()
        {
            var hierarchy = CreateHierarchyWorld();

            var a = BuildSimpleRig("A", Vector3.zero);
            var b = BuildSimpleRig("B", new Vector3(2, 0, 0));

            hierarchy.ReserveSceneObject(a, -1);
            hierarchy.ReserveSceneObject(b, -2);
            hierarchy.RegisterReservedSceneObjects();

            Assert.That(hierarchy.TryGetId(a, out var aId), Is.True);
            Assert.That(hierarchy.TryGetId(b, out var bId), Is.True);

            b.transform.SetParent(a.transform, true);

            var fullState = BuildState(hierarchy, null);

            try
            {
                hierarchy.ApplyRemoteVisibilityDelete(aId);
                Assert.That(hierarchy.TryGetGameObject(bId, out var bGo), Is.True);
                Assert.That(bGo.transform.parent, Is.Null,
                    "sanity: B must be rescued to world when its decoration parent dies");

                InvokeSetUnityState(hierarchy, fullState);
                Assert.That(hierarchy.TryGetGameObject(aId, out var aGo), Is.True,
                    "sanity: applying the full state must resurrect A");
                Assert.That(bGo.transform.parent, Is.EqualTo(aGo.transform),
                    "a rescued decoration must reattach when its parent resurrects");
            }
            finally
            {
                fullState.Dispose();
            }
        }

        private PredictedHierarchyState BuildState(PredictedHierarchy hierarchy, Func<InstanceDetails, bool> keep)
        {
            var spawned = GetField<List<InstanceDetails>>(typeof(PredictedHierarchy), hierarchy, "_spawnedPrefabs");
            var nextId = GetField<uint>(typeof(PredictedHierarchy), hierarchy, "_nextInstanceId");

            var list = DisposableList<InstanceDetails>.Create(spawned.Count);

            for (var i = 0; i < spawned.Count; i++)
            {
                if (keep == null || keep(spawned[i]))
                    list.Add(spawned[i]);
            }

            return new PredictedHierarchyState(list, DisposableList<PredictedObjectID>.Create(0), nextId);
        }

        private Dictionary<PredictedObjectID, InstanceDetails> GetRecords(PredictedHierarchy hierarchy)
        {
            return GetField<Dictionary<PredictedObjectID, InstanceDetails>>(
                typeof(PredictedHierarchy), hierarchy, "_recordsById");
        }

        private static void InvokeSetUnityState(PredictedHierarchy hierarchy, PredictedHierarchyState state)
        {
            var method = typeof(PredictedHierarchy).GetMethod("SetUnityState", InstanceFields);
            Assert.That(method, Is.Not.Null, "Missing PredictedHierarchy.SetUnityState");
            method.Invoke(hierarchy, new object[] { state });
        }

        private PredictedHierarchy CreateHierarchyWorld()
        {
            PurrNet.Utils.Hasher.PrepareType<PredictedHierarchyState>();
            PurrNet.Utils.Hasher.PrepareType<InstanceDetails>();
            PurrNet.Utils.Hasher.PrepareType<PredictedObjectID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedComponentID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedGameObjectState>();
            PurrNet.Utils.Hasher.PrepareType<PredictedParent.ParentState>();

            var networkObject = Track(new GameObject("NetworkManager"));
            var managerObject = Track(new GameObject("PredictionManager"));

            var networkManager = networkObject.AddComponent<NetworkManager>();
            var manager = CreateSpawnedPredictionManager(managerObject, networkManager);

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            PredictedHierarchy hierarchy;
            try
            {
                hierarchy = manager.RegisterSystem<PredictedHierarchy>();
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }

            SetField(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", hierarchy);
            return hierarchy;
        }

        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        private static PredictionManager CreateSpawnedPredictionManager(GameObject managerObject, NetworkManager networkManager)
        {
            var tickManager = new TickManager(20, networkManager, null, false);
            SetField(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);

            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", networkManager);
            SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
            manager.SetIsSpawned(true, false);
            return manager;
        }

        private static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }

        private static T GetField<T>(Type declaringType, object target, string fieldName)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            return (T)field.GetValue(target);
        }
    }
}
