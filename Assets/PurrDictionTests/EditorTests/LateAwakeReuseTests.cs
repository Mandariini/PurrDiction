using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// A prefab instance materialized for a given PredictedObjectID must always reflect its
    /// LateAwake side effects: either LateAwake ran on this exact GameObject for this id, or the
    /// GameObject was preserved from the same id's previous life. A pooled tree recycled for a
    /// different id (fuzzy drifted fallback) must be re-initialized, not silently reused.
    /// </summary>
    public sealed class LateAwakeReuseTests
    {
        readonly List<GameObject> _cleanup = new ();
        readonly List<Object> _assetCleanup = new ();
        PredictionManager _manager;

        [SetUp]
        public void SetUp()
        {
            LateAwakeCameraProbe.lateAwakeCalls = 0;
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;

            if (_manager)
            {
                var poolParent = GetField<GameObject>(typeof(PredictionManager), _manager, "_poolParent");
                if (poolParent)
                    Object.DestroyImmediate(poolParent);
                _manager = null;
            }

            for (var i = 0; i < _cleanup.Count; i++)
            {
                if (_cleanup[i])
                    Object.DestroyImmediate(_cleanup[i]);
            }

            for (var i = 0; i < _assetCleanup.Count; i++)
            {
                if (_assetCleanup[i])
                    Object.DestroyImmediate(_assetCleanup[i]);
            }

            _cleanup.Clear();
            _assetCleanup.Clear();
        }

        GameObject Track(GameObject go)
        {
            _cleanup.Add(go);
            return go;
        }

        [Test]
        public void SameIdRollbackChurnKeepsLateAwakeEffects()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterCameraPrefab(hierarchy);

            var owner = new PlayerID(new PackedULong(7), false);
            var spawnPos = new Vector3(1, 0, 0);

            var id = hierarchy.Create(0, spawnPos, Quaternion.identity, owner);
            Assert.That(id.HasValue, Is.True);
            Assert.That(LateAwakeCameraProbe.lateAwakeCalls, Is.EqualTo(1));

            Assert.That(hierarchy.TryGetGameObject(id, out var go), Is.True);
            Track(go);
            AssertLateAwakeConsistent(go, "initial create");

            RollBackToEmpty(hierarchy);

            var id2 = hierarchy.Create(0, spawnPos, Quaternion.identity, owner);
            Assert.That(id2, Is.EqualTo(id), "resim create must reuse the same POID");

            Assert.That(hierarchy.TryGetGameObject(id2, out var go2), Is.True);
            Track(go2);
            AssertLateAwakeConsistent(go2, "same-id resim create");
        }

        [Test]
        public void VerifiedApplyRecreateKeepsLateAwakeEffects()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterCameraPrefab(hierarchy);

            var owner = new PlayerID(new PackedULong(7), false);
            var spawnPos = new Vector3(1, 0, 0);

            var id = hierarchy.Create(0, spawnPos, Quaternion.identity, owner);
            Assert.That(id.HasValue, Is.True);
            Assert.That(hierarchy.TryGetGameObject(id, out var go), Is.True);
            Track(go);

            RollBackToEmpty(hierarchy);

            ApplyState(hierarchy, new[]
            {
                new InstanceDetails(0, 0, id.Value, spawnPos, Quaternion.identity, owner, null)
            });

            Assert.That(hierarchy.TryGetGameObject(id, out var reGo), Is.True);
            Track(reGo);
            AssertLateAwakeConsistent(reGo, "verified apply recreate");
        }

        [Test]
        public void DriftedPoolFallbackMustNotSkipLateAwake()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterCameraPrefab(hierarchy);

            var owned = new PlayerID(new PackedULong(7), false);
            var posA = new Vector3(0, 0, 0);
            var posB = new Vector3(50, 0, 0);

            var idA = hierarchy.Create(0, posA, Quaternion.identity);
            var idB = hierarchy.Create(0, posB, Quaternion.identity, owned);
            Assert.That(idA.HasValue, Is.True);
            Assert.That(idB.HasValue, Is.True);

            Assert.That(hierarchy.TryGetGameObject(idA, out var goA), Is.True);
            Assert.That(hierarchy.TryGetGameObject(idB, out var goB), Is.True);
            Track(goA);
            Track(goB);

            Assert.That(goA.transform.Find("Camera"), Is.Not.Null, "unowned instance keeps its camera");
            Assert.That(goB.transform.Find("Camera"), Is.Null, "owned instance loses its camera in LateAwake");

            RollBackToEmpty(hierarchy);

            var posB2 = posA + new Vector3(0.5f, 0, 0);
            ApplyState(hierarchy, new[]
            {
                new InstanceDetails(0, 0, idB.Value, posB2, Quaternion.identity, owned, null)
            });

            Assert.That(hierarchy.TryGetGameObject(idB, out var reGoB), Is.True);
            Track(reGoB);
            AssertLateAwakeConsistent(reGoB, "drifted fuzzy pool fallback");
        }

        void AssertLateAwakeConsistent(GameObject go, string context)
        {
            var probe = go.GetComponent<LateAwakeCameraProbe>();
            Assert.That(probe, Is.Not.Null);

            var camera = go.transform.Find("Camera");

            if (probe.owner.HasValue)
            {
                Assert.That(camera, Is.Null,
                    $"{context}: instance is owned but still has its camera; LateAwake side effects were lost");
            }
            else
            {
                Assert.That(camera, Is.Not.Null,
                    $"{context}: instance is unowned but its camera is missing; it inherited another id's LateAwake side effects");
            }
        }

        void RegisterCameraPrefab(PredictedHierarchy hierarchy)
        {
            var prefab = Track(new GameObject("PlayerPrefab"));
            prefab.AddComponent<LateAwakeCameraProbe>();

            var camera = new GameObject("Camera");
            camera.transform.SetParent(prefab.transform, false);

            var prefabsAsset = ScriptableObject.CreateInstance<PredictedPrefabs>();
            _assetCleanup.Add(prefabsAsset);
            prefabsAsset.prefabs.Add(new PredictedPrefab { prefab = prefab, pooled = false });

            _manager.predictedPrefabs = prefabsAsset;
        }

        void ApplyState(PredictedHierarchy hierarchy, InstanceDetails[] records)
        {
            uint maxId = 2;
            for (var i = 0; i < records.Length; i++)
            {
                var end = records[i].instanceId.instanceId.value + 1;
                if (end > maxId)
                    maxId = end;
            }

            var target = new PredictedHierarchyState(
                DisposableList<InstanceDetails>.Create(records.Length),
                DisposableList<PredictedObjectID>.Create(4),
                maxId);

            for (var i = 0; i < records.Length; i++)
                target.spawnedPrefabs.Add(records[i]);

            try
            {
                var method = typeof(PredictedHierarchy).GetMethod("SetUnityState", InstanceFields);
                Assert.That(method, Is.Not.Null, "Missing PredictedHierarchy.SetUnityState");
                method.Invoke(hierarchy, new object[] { target });
            }
            finally
            {
                target.Dispose();
            }
        }

        static void RollBackToEmpty(PredictedHierarchy hierarchy)
        {
            var rollbackTarget = new PredictedHierarchyState(
                DisposableList<InstanceDetails>.Create(4),
                DisposableList<PredictedObjectID>.Create(4),
                2);

            try
            {
                var method = typeof(PredictedHierarchy).GetMethod("SetUnityState", InstanceFields);
                Assert.That(method, Is.Not.Null, "Missing PredictedHierarchy.SetUnityState");
                method.Invoke(hierarchy, new object[] { rollbackTarget });
            }
            finally
            {
                rollbackTarget.Dispose();
            }
        }

        PredictedHierarchy CreateHierarchyWorld()
        {
            PurrCopy.Override<PredictedHierarchyState>();
            PurrNet.Utils.Hasher.PrepareType<PredictedHierarchyState>();
            PurrNet.Utils.Hasher.PrepareType<InstanceDetails>();
            PurrNet.Utils.Hasher.PrepareType<PredictedObjectID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedComponentID>();
            PurrNet.Utils.Hasher.PrepareType<EmptyState>();

            var networkObject = Track(new GameObject("NetworkManager"));
            var managerObject = Track(new GameObject("PredictionManager"));

            var networkManager = networkObject.AddComponent<NetworkManager>();
            _manager = CreateSpawnedPredictionManager(managerObject, networkManager);
            var hierarchy = _manager.RegisterSystem<PredictedHierarchy>();
            SetField(typeof(PredictionManager), _manager, "<hierarchy>k__BackingField", hierarchy);
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

    public sealed class LateAwakeCameraProbe : PredictedIdentity<EmptyState>
    {
        public static int lateAwakeCalls;

        protected override void LateAwake()
        {
            lateAwakeCalls++;

            if (owner.HasValue)
            {
                var camera = transform.Find("Camera");
                if (camera)
                    Object.DestroyImmediate(camera.gameObject);
            }
        }

        protected override EmptyState GetInitialState() => default;
    }
}
