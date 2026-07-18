using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public class PredictedHierarchyPieceTests
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

        GameObject BuildCompoundRig()
        {
            var root = Track(new GameObject("root"));
            root.AddComponent<PredictedGameObject>();

            var plainFolder = new GameObject("folder");
            plainFolder.transform.SetParent(root.transform);

            var pieceA = new GameObject("pieceA");
            pieceA.AddComponent<PredictedGameObject>();
            pieceA.transform.SetParent(plainFolder.transform);

            var pieceB = new GameObject("pieceB");
            pieceB.AddComponent<PredictedGameObject>();
            pieceB.transform.SetParent(root.transform);

            var pieceAChild = new GameObject("pieceAChild");
            pieceAChild.AddComponent<PredictedGameObject>();
            pieceAChild.transform.SetParent(pieceA.transform);

            return root;
        }

        [Test]
        public void PrototypeTraversalMatchesGetComponentsInChildrenOrder()
        {
            var root = BuildCompoundRig();

            var proto = PiecePrototype.Build(root);
            Assert.That(proto, Is.Not.Null);
            Assert.That(proto.pieceCount, Is.EqualTo(4));

            var collected = new List<GameObject>();
            Assert.That(proto.TryCollectInstancePieces(root, collected), Is.True);

            var identities = new List<PredictedIdentity>();
            root.GetComponentsInChildren(true, identities);

            var identityGos = new List<GameObject>();
            for (var i = 0; i < identities.Count; i++)
            {
                if (!identityGos.Contains(identities[i].gameObject))
                    identityGos.Add(identities[i].gameObject);
            }

            Assert.That(collected, Is.EqualTo(identityGos),
                "pieceIndex traversal must match the GameObject order of GetComponentsInChildren");
        }

        [Test]
        public void PrototypeRecordsParentPiecesAndPathsThroughPlainTransforms()
        {
            var root = BuildCompoundRig();
            var proto = PiecePrototype.Build(root);

            Assert.That(proto.pieces[0].parentPieceIndex, Is.EqualTo(-1));
            Assert.That(proto.pieces[1].parentPieceIndex, Is.EqualTo(0), "pieceA hangs off the root through a plain folder");
            Assert.That(proto.pieces[1].inverseSiblingPath.Length, Is.EqualTo(2), "pieceA is two levels below its parent piece");
            Assert.That(proto.pieces[2].parentPieceIndex, Is.EqualTo(1), "pieceAChild's nearest identity ancestor is pieceA");
            Assert.That(proto.pieces[2].inverseSiblingPath.Length, Is.EqualTo(1));
            Assert.That(proto.pieces[3].parentPieceIndex, Is.EqualTo(0));
        }

        [Test]
        public void PrototypeRejectsIdentitylessRoot()
        {
            var root = Track(new GameObject("bare"));
            var child = new GameObject("child");
            child.AddComponent<PredictedGameObject>();
            child.transform.SetParent(root.transform);

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            var proto = PiecePrototype.Build(root);
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

            Assert.That(proto, Is.Null);
        }

        [Test]
        public void PrototypeBoundariesStopDescent()
        {
            var root = BuildCompoundRig();
            var nestedRoot = root.transform.GetChild(0).GetChild(0);

            var boundaries = new HashSet<Transform> { nestedRoot };
            var proto = PiecePrototype.Build(root, boundaries);

            Assert.That(proto.pieceCount, Is.EqualTo(2), "pieceA and its child sit behind the boundary");
        }

        [Test]
        public void AttachAtPathRestoresAuthoredLocation()
        {
            var root = BuildCompoundRig();
            var proto = PiecePrototype.Build(root);

            var collected = new List<GameObject>();
            proto.TryCollectInstancePieces(root, collected);

            var pieceA = collected[1];
            var originalParent = pieceA.transform.parent;
            var originalSibling = pieceA.transform.GetSiblingIndex();

            pieceA.transform.SetParent(null);

            PiecePrototype.AttachAtPath(root.transform, pieceA.transform, proto.pieces[1].inverseSiblingPath, false);

            Assert.That(pieceA.transform.parent, Is.EqualTo(originalParent));
            Assert.That(pieceA.transform.GetSiblingIndex(), Is.EqualTo(originalSibling));
        }

        [Test]
        public void RootIdIsDerivedFromPieceIndex()
        {
            var record = new InstanceDetails(3, 2, new PredictedObjectID(12), Vector3.zero, Quaternion.identity, null, null);

            Assert.That(record.rootId, Is.EqualTo(new PredictedObjectID(10)));
            Assert.That(record.isRootRecord, Is.False);

            var rootRecord = new InstanceDetails(3, 0, new PredictedObjectID(10), Vector3.one, Quaternion.identity, null, null);
            Assert.That(rootRecord.rootId, Is.EqualTo(new PredictedObjectID(10)));
            Assert.That(rootRecord.isRootRecord, Is.True);
        }

        [Test]
        public void PoolPreciseTreeTakeMatchesExactRootAndRejectsDrift()
        {
            var pool = new PredictedPiecePool();
            var tree = Track(new GameObject("tree"));

            var pieces = new List<PooledPiece> { new PooledPiece(new PredictedObjectID(10), 0, tree) };
            pool.PutTree(1, new PredictedObjectID(10), Vector3.zero, tree, pieces, 0, true);

            var taken = new List<PooledPiece>();

            Assert.That(pool.TryTakeTree(new PredictedObjectID(11), 1, Vector3.zero, true, taken, out _, out _), Is.False);

            Assert.That(pool.TryTakeTree(new PredictedObjectID(10), 1, new Vector3(5, 0, 0), true, taken, out _, out var drifted), Is.False);
            Assert.That(drifted, Is.True, "an exact id at a drifted spawn position must report the drift");

            Assert.That(pool.TryTakeTree(new PredictedObjectID(10), 1, Vector3.zero, true, taken, out var rootGo, out _), Is.True);
            Assert.That(rootGo, Is.EqualTo(tree));
            Assert.That(taken.Count, Is.EqualTo(1));

            Assert.That(pool.TryTakeTree(new PredictedObjectID(10), 1, Vector3.zero, true, taken, out _, out _), Is.False,
                "a taken entry must leave the pool");
        }

        [Test]
        public void PoolPieceExtractionSplitsPooledSubtrees()
        {
            var pool = new PredictedPiecePool();

            var root = Track(new GameObject("root"));
            var mid = Track(new GameObject("mid"));
            var leaf = Track(new GameObject("leaf"));
            mid.transform.SetParent(root.transform);
            leaf.transform.SetParent(mid.transform);

            var pieces = new List<PooledPiece>
            {
                new PooledPiece(new PredictedObjectID(20), 0, root),
                new PooledPiece(new PredictedObjectID(21), 1, mid),
                new PooledPiece(new PredictedObjectID(22), 2, leaf)
            };

            pool.PutTree(1, new PredictedObjectID(20), Vector3.zero, root, pieces, 0, true);

            Assert.That(pool.TryTakePiece(new PredictedObjectID(21), 1, out var takenMid), Is.True);
            Assert.That(takenMid, Is.EqualTo(mid));
            Assert.That(takenMid.transform.parent, Is.Null, "the taken piece must leave the pooled tree");
            Assert.That(leaf.transform.parent, Is.Null, "the leaf stays pooled as its own entry, detached from the taken piece");

            Assert.That(pool.Contains(new PredictedObjectID(22)), Is.True);
            Assert.That(pool.Contains(new PredictedObjectID(21)), Is.False);
            Assert.That(pool.Contains(new PredictedObjectID(20)), Is.True);

            Assert.That(pool.TryTakePiece(new PredictedObjectID(22), 1, out var takenLeaf), Is.True);
            Assert.That(takenLeaf, Is.EqualTo(leaf));
        }

        [Test]
        public void DirectDestroyOfAPieceDegradesToAHardDelete()
        {
            var networkObject = Track(new GameObject("NetworkManager"));
            var managerObject = Track(new GameObject("PredictionManager"));

            var networkManager = networkObject.AddComponent<NetworkManager>();
            var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
            var hierarchy = manager.RegisterSystem<PredictedHierarchy>();
            SetField(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", hierarchy);

            var rigRoot = BuildCompoundRig();
            hierarchy.ReserveSceneObject(rigRoot, -1);
            hierarchy.RegisterReservedSceneObjects();

            var pieceAId = new PredictedObjectID(3);
            var pieceAChildId = new PredictedObjectID(4);
            var pieceBId = new PredictedObjectID(5);

            Assert.That(hierarchy.TryGetGameObject(pieceAId, out var pieceAGo), Is.True);

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            Object.DestroyImmediate(pieceAGo);
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

            Assert.That(hierarchy.TryGetGameObject(pieceAId, out _), Is.False,
                "the destroyed piece must leave the instance map");
            Assert.That(hierarchy.TryGetRootId(pieceAId, out _), Is.False,
                "the destroyed piece's record must be removed");
            Assert.That(hierarchy.TryGetRootId(pieceAChildId, out _), Is.False,
                "pieces destroyed with their parent must be removed too");
            Assert.That(hierarchy.TryGetRootId(pieceBId, out _), Is.True,
                "unrelated pieces of the same instance must survive");
            Assert.That(hierarchy.TryGetGameObject(new PredictedObjectID(2), out _), Is.True);
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

        [Test]
        public void PoolNearestFallbackOnlyReturnsCompleteTrees()
        {
            var pool = new PredictedPiecePool();

            var partial = Track(new GameObject("partial"));
            pool.PutTree(1, new PredictedObjectID(30), Vector3.zero, partial,
                new List<PooledPiece> { new PooledPiece(new PredictedObjectID(30), 0, partial) }, 0, false);

            var taken = new List<PooledPiece>();
            Assert.That(pool.TryTakeNearestCompleteTree(1, Vector3.zero, taken, out _), Is.False,
                "incomplete trees must never satisfy the fuzzy fallback");

            var complete = Track(new GameObject("complete"));
            pool.PutTree(1, new PredictedObjectID(40), new Vector3(1, 0, 0), complete,
                new List<PooledPiece> { new PooledPiece(new PredictedObjectID(40), 0, complete) }, 0, true);

            Assert.That(pool.TryTakeNearestCompleteTree(1, Vector3.zero, taken, out var rootGo), Is.True);
            Assert.That(rootGo, Is.EqualTo(complete));
        }
    }
}
