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
    /// Re-running a deterministic spawn during rollback replay reuses the same POIDs, so the
    /// instance count must stay bounded even when the replayed spawn positions drift (the drift
    /// only invalidates the pool's exact-id claim, it does not make the spawns new objects).
    /// Growth here means every reconcile leaks prefab clones until ClearOld reaps them.
    /// </summary>
    public sealed class ReplayDriftChurnTests
    {
        readonly List<GameObject> _cleanup = new ();
        readonly List<Object> _assetCleanup = new ();
        PredictionManager _manager;

        [SetUp]
        public void SetUp()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;

            foreach (var clone in FindPrefabClones())
                Object.DestroyImmediate(clone);

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

        const string PrefabName = "ChurnPlayerPrefab";
        const int SpawnCount = 3;
        const int ReplayRounds = 5;

        [Test, Ignore("Documents the replay drift pool-churn leak; unignore when the drift fallback stops stranding trees.")]
        public void ReplayedSpawnsWithDriftingPositionsDoNotAccumulateClones()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterPrefab(hierarchy);

            for (var round = 0; round < ReplayRounds; round++)
            {
                var ids = new PredictedObjectID?[SpawnCount];

                for (var k = 0; k < SpawnCount; k++)
                {
                    var pos = new Vector3((round * SpawnCount + k) * 10f, 0, 0);
                    ids[k] = hierarchy.Create(0, pos, Quaternion.identity);
                    Assert.That(ids[k].HasValue, Is.True, $"round {round} spawn {k} failed");
                }

                for (var k = 0; k < SpawnCount; k++)
                {
                    Assert.That(ids[k].Value.instanceId.value, Is.EqualTo((uint)(2 + k)),
                        $"round {round}: replayed create must reuse the same POID block");
                }

                int liveCount = 0;
                foreach (var clone in FindPrefabClones())
                {
                    if (clone.activeInHierarchy)
                        liveCount++;
                }

                Assert.That(liveCount, Is.EqualTo(SpawnCount),
                    $"round {round}: live instance count must match the spawn count");

                RollBackToEmpty(hierarchy);
            }

            int totalClones = FindPrefabClones().Count;
            Assert.That(totalClones, Is.LessThanOrEqualTo(SpawnCount + 1),
                $"{ReplayRounds} replays of the same {SpawnCount} spawns left {totalClones} prefab clones " +
                "in the scene; every reconcile leaks instances until ClearOld reaps them");
        }

        static List<GameObject> FindPrefabClones()
        {
            var result = new List<GameObject>();
            var all = Resources.FindObjectsOfTypeAll<GameObject>();

            for (var i = 0; i < all.Length; i++)
            {
                var go = all[i];

                if (!go || !go.name.StartsWith(PrefabName, StringComparison.Ordinal))
                    continue;

                if (go.name == PrefabName)
                    continue;

                if (!go.scene.IsValid())
                    continue;

                result.Add(go);
            }

            return result;
        }

        void RegisterPrefab(PredictedHierarchy hierarchy)
        {
            var prefab = Track(new GameObject(PrefabName));
            prefab.AddComponent<PredictedGameObject>();

            var prefabsAsset = ScriptableObject.CreateInstance<PredictedPrefabs>();
            _assetCleanup.Add(prefabsAsset);
            prefabsAsset.prefabs.Add(new PredictedPrefab { prefab = prefab, pooled = false });

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
            PurrCopy.Override<PredictedHierarchyState>();
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
