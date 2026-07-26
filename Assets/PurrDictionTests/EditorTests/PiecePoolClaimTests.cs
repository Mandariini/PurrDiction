using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// A pooled entry claims every piece id it holds. When a create for one of those ids is
    /// satisfied from somewhere else - the fuzzy fallback returning a different tree, or a fresh
    /// instantiate - the claim must not survive, or the pool will later hand out a stale
    /// GameObject for an id that is already live.
    /// </summary>
    public sealed class PiecePoolClaimTests
    {
        private readonly List<GameObject> _tracked = new();

        [TearDown]
        public void Cleanup()
        {
            for (var i = 0; i < _tracked.Count; i++)
            {
                if (_tracked[i])
                    Object.DestroyImmediate(_tracked[i]);
            }
            _tracked.Clear();
        }

        private GameObject Track(GameObject go)
        {
            _tracked.Add(go);
            return go;
        }

        private static List<PooledPiece> Single(PredictedObjectID id, GameObject go)
            => new() { new PooledPiece(id, 0, go) };

        [Test]
        public void ReleasingAClaimAfterAFallbackServedADifferentTree()
        {
            var pool = new PredictedPiecePool();

            var exact = Track(new GameObject("exact"));
            pool.PutTree(1, new PredictedObjectID(50), Vector3.zero, exact,
                Single(new PredictedObjectID(50), exact), 0, true);

            var nearer = Track(new GameObject("nearer"));
            pool.PutTree(1, new PredictedObjectID(60), new Vector3(5, 0, 0), nearer,
                Single(new PredictedObjectID(60), nearer), 0, true);

            var taken = new List<PooledPiece>();
            var respawnPosition = new Vector3(5, 0, 0);

            Assert.That(
                pool.TryTakeTree(new PredictedObjectID(50), 1, respawnPosition, true, taken, out _, out var drifted),
                Is.False);
            Assert.That(drifted, Is.True);

            taken.Clear();
            Assert.That(
                pool.TryTakeNearestCompleteTree(1, respawnPosition, taken, out var served),
                Is.True);
            Assert.That(served, Is.EqualTo(nearer),
                "the fallback served the create from a different pooled tree");

            // Id 50 is now live as `nearer`, which is what the hierarchy registers it as.
            pool.ReleaseClaim(new PredictedObjectID(50));

            Assert.That(pool.Contains(new PredictedObjectID(50)), Is.False,
                "the pool must not still claim an id that is live elsewhere");
            Assert.That(pool.TryTakePiece(new PredictedObjectID(50), 1, out _), Is.False,
                "a released id must never yield a stale GameObject");

            // The bypassed tree keeps its GameObject so it is still reusable and still torn
            // down with its entry - releasing a claim must not leak it.
            taken.Clear();
            Assert.That(pool.TryTakeNearestCompleteTree(1, Vector3.zero, taken, out var reused), Is.True);
            Assert.That(reused, Is.EqualTo(exact),
                "releasing the id claim must not orphan the pooled GameObject");
        }

        [Test]
        public void ReleasingANonRootClaimAfterAFreshInstantiate()
        {
            var pool = new PredictedPiecePool();

            var root = Track(new GameObject("root"));
            var child = Track(new GameObject("child"));
            child.transform.SetParent(root.transform);

            var pieces = new List<PooledPiece>
            {
                new PooledPiece(new PredictedObjectID(70), 0, root),
                new PooledPiece(new PredictedObjectID(71), 1, child)
            };
            pool.PutTree(1, new PredictedObjectID(70), Vector3.zero, root, pieces, 0, true);

            var taken = new List<PooledPiece>();

            // A create whose root record is the child id: the pool maps 71, but not as a root,
            // so the exact take fails without reporting drift and the caller falls through to a
            // fresh instantiate.
            Assert.That(
                pool.TryTakeTree(new PredictedObjectID(71), 1, Vector3.zero, true, taken, out _, out var drifted),
                Is.False);
            Assert.That(drifted, Is.False,
                "a non-root claim must not be reported as drift, so there is no fuzzy fallback");

            // The caller instantiates fresh and registers 71, so the claim must be relinquished.
            pool.ReleaseClaim(new PredictedObjectID(71));

            Assert.That(pool.Contains(new PredictedObjectID(71)), Is.False);
            Assert.That(pool.TryTakePiece(new PredictedObjectID(71), 1, out _), Is.False,
                "a released non-root id must never yield the pooled child");

            Assert.That(pool.Contains(new PredictedObjectID(70)), Is.True,
                "releasing one piece must not disturb the rest of the entry");
            Assert.That(pool.TryTakePiece(new PredictedObjectID(70), 1, out var takenRoot), Is.True);
            Assert.That(takenRoot, Is.EqualTo(root));
        }
    }
}
