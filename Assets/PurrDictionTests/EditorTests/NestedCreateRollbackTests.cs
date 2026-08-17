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
    /// <summary>
    /// Two separate roots created in the same tick, with B Unity-nested under A, must survive a
    /// rollback removal without cross-binding their ids. The batch removal path pools everything
    /// reachable from the topmost member, so B's GameObject can end up inside A's pool entry and
    /// the resim create for A then registers B's GameObject under A's POID.
    /// </summary>
    public sealed class NestedCreateRollbackTests
    {
        readonly List<GameObject> _cleanup = new ();
        readonly List<Object> _assetCleanup = new ();
        PredictionManager _manager;

        [TearDown]
        public void TearDown()
        {
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
        public void RollbackOfSameTickNestedCreatesKeepsPoidBindings()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterTwoPrefabs(hierarchy);

            var spawnPos = new Vector3(1, 0, 0);
            var idA = hierarchy.Create(0, spawnPos, Quaternion.identity);
            Assert.That(idA.HasValue, Is.True);

            var idB = hierarchy.Create(1, Vector3.zero, Quaternion.identity, new PredictedComponentID(idA.Value, 0));
            Assert.That(idB.HasValue, Is.True);

            AssertNestedPair(hierarchy, idA.Value, idB.Value);

            RollBackToEmpty(hierarchy);

            Assert.That(hierarchy.TryGetGameObject(idA, out _), Is.False, "rollback must remove A");
            Assert.That(hierarchy.TryGetGameObject(idB, out _), Is.False, "rollback must remove B");

            AssertResimRebindsCorrectly(hierarchy, idA.Value, idB.Value, spawnPos, withStateParent: true);
        }

        [Test]
        public void RollbackOfSameTickCreatesNestedByRawSetParentKeepsPoidBindings()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterTwoPrefabs(hierarchy);

            var spawnPos = new Vector3(1, 0, 0);
            var idA = hierarchy.Create(0, spawnPos, Quaternion.identity);
            var idB = hierarchy.Create(1, Vector3.zero, Quaternion.identity);
            Assert.That(idA.HasValue, Is.True);
            Assert.That(idB.HasValue, Is.True);

            Assert.That(hierarchy.TryGetGameObject(idA, out var goA), Is.True);
            Assert.That(hierarchy.TryGetGameObject(idB, out var goB), Is.True);
            Track(goA);
            Track(goB);

            UnityEngine.TestTools.LogAssert.Expect(
                LogType.Warning,
                new System.Text.RegularExpressions.Regex("reparented"));
            goB.transform.SetParent(goA.transform, true);
            hierarchy.NotifyInstanceParentChanged(goB);

            AssertNestedPair(hierarchy, idA.Value, idB.Value);

            RollBackToEmpty(hierarchy);

            Assert.That(hierarchy.TryGetGameObject(idA, out _), Is.False, "rollback must remove A");
            Assert.That(hierarchy.TryGetGameObject(idB, out _), Is.False, "rollback must remove B");

            AssertResimRebindsCorrectly(hierarchy, idA.Value, idB.Value, spawnPos, withStateParent: false);
        }

        void AssertNestedPair(PredictedHierarchy hierarchy, PredictedObjectID idA, PredictedObjectID idB)
        {
            Assert.That(hierarchy.TryGetGameObject(idA, out var goA), Is.True);
            Assert.That(hierarchy.TryGetGameObject(idB, out var goB), Is.True);
            Track(goA);
            Track(goB);

            Assert.That(goB.transform.IsChildOf(goA.transform), Is.True,
                "precondition: B's GameObject is Unity-nested under A's");
            Assert.That(goA.GetComponent<BoxCollider>(), Is.Not.Null, "A resolves to A's prefab");
            Assert.That(goB.GetComponent<SphereCollider>(), Is.Not.Null, "B resolves to B's prefab");
        }

        void AssertResimRebindsCorrectly(PredictedHierarchy hierarchy, PredictedObjectID idA, PredictedObjectID idB,
            Vector3 spawnPos, bool withStateParent)
        {
            var idA2 = hierarchy.Create(0, spawnPos, Quaternion.identity);
            Assert.That(idA2, Is.EqualTo(idA), "the resim create must reuse A's POID");

            Assert.That(hierarchy.TryGetGameObject(idA2, out var resimGoA), Is.True);
            Track(resimGoA);

            Assert.That(resimGoA.GetComponent<SphereCollider>(), Is.Null,
                "A's POID must not resolve to B's pooled GameObject after a batch rollback removal of the nested pair");
            Assert.That(resimGoA.GetComponent<BoxCollider>(), Is.Not.Null,
                "A's POID must resolve to an instance of A's prefab");

            var idB2 = withStateParent
                ? hierarchy.Create(1, Vector3.zero, Quaternion.identity, new PredictedComponentID(idA2.Value, 0))
                : hierarchy.Create(1, Vector3.zero, Quaternion.identity);
            Assert.That(idB2, Is.EqualTo(idB), "the resim create must reuse B's POID");

            Assert.That(hierarchy.TryGetGameObject(idB2, out var resimGoB), Is.True);
            Track(resimGoB);

            Assert.That(resimGoB, Is.Not.EqualTo(resimGoA), "A and B must stay distinct GameObjects");
            Assert.That(resimGoB.GetComponent<SphereCollider>(), Is.Not.Null,
                "B's POID must resolve to an instance of B's prefab");
        }

        void RegisterTwoPrefabs(PredictedHierarchy hierarchy)
        {
            var prefabA = Track(new GameObject("PrefabA"));
            prefabA.AddComponent<PredictedGameObject>();
            prefabA.AddComponent<BoxCollider>();

            var prefabB = Track(new GameObject("PrefabB"));
            prefabB.AddComponent<PredictedGameObject>();
            prefabB.AddComponent<SphereCollider>();

            var prefabsAsset = ScriptableObject.CreateInstance<PredictedPrefabs>();
            _assetCleanup.Add(prefabsAsset);
            prefabsAsset.prefabs.Add(new PredictedPrefab { prefab = prefabA, pooled = false });
            prefabsAsset.prefabs.Add(new PredictedPrefab { prefab = prefabB, pooled = false });

            _manager.predictedPrefabs = prefabsAsset;
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
            PurrNet.Utils.Hasher.PrepareType<PredictedHierarchyState>();
            PurrNet.Utils.Hasher.PrepareType<InstanceDetails>();
            PurrNet.Utils.Hasher.PrepareType<PredictedObjectID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedComponentID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedGameObjectState>();

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
}
