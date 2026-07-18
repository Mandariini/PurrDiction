using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    internal readonly struct PooledPiece
    {
        public readonly PredictedObjectID id;
        public readonly uint pieceIndex;
        public readonly GameObject gameObject;

        public PooledPiece(PredictedObjectID id, uint pieceIndex, GameObject gameObject)
        {
            this.id = id;
            this.pieceIndex = pieceIndex;
            this.gameObject = gameObject;
        }
    }

    /// <summary>
    /// Holds recently deleted pieces (single or assembled subtrees) keyed by their exact
    /// piece ids so rollbacks and replays can resurrect the very same GameObjects.
    /// Entries older than the rollback window are destroyed for real, except scene pieces
    /// which have no asset to rebuild from and are kept until cleanup.
    /// </summary>
    internal sealed class PredictedPiecePool
    {
        sealed class Entry
        {
            public GameObject rootGo;
            public PredictedObjectID rootPieceId;
            public PackedInt prefabId;
            public ulong addedTick;
            public Vector3 rootSpawnPosition;
            public bool isComplete;
            public readonly List<PooledPiece> pieces = new ();
        }

        readonly Dictionary<PredictedObjectID, Entry> _byPieceId = new ();
        readonly List<Entry> _entries = new ();
        readonly HashSet<GameObject> _entryPieceScratch = new ();

        public void PutTree(PackedInt prefabId, PredictedObjectID rootPieceId, Vector3 rootSpawnPosition,
            GameObject rootGo, List<PooledPiece> pieces, ulong tick, bool isComplete)
        {
            var entry = new Entry
            {
                rootGo = rootGo,
                rootPieceId = rootPieceId,
                prefabId = prefabId,
                addedTick = tick,
                rootSpawnPosition = rootSpawnPosition,
                isComplete = isComplete
            };

            for (var i = 0; i < pieces.Count; i++)
            {
                entry.pieces.Add(pieces[i]);
                _byPieceId[pieces[i].id] = entry;
            }

            _entries.Add(entry);
        }

        public void PutPiece(PackedInt prefabId, PredictedObjectID pieceId, uint pieceIndex, GameObject go, ulong tick)
        {
            var single = new List<PooledPiece> { new PooledPiece(pieceId, pieceIndex, go) };
            PutTree(prefabId, pieceId, go.transform.position, go, single, tick, false);
        }

        public bool Contains(PredictedObjectID pieceId)
        {
            return _byPieceId.ContainsKey(pieceId);
        }

        /// <summary>
        /// Takes the whole entry rooted at the given root piece id. When the entry exists but its
        /// spawn position drifted too far, it is left in place and foundButDrifted is set so the
        /// caller can fall back to a fuzzy complete-tree take.
        /// </summary>
        public bool TryTakeTree(PredictedObjectID rootPieceId, Vector3 expectedSpawnPosition, bool checkDrift,
            List<PooledPiece> resultPieces, out GameObject rootGo, out bool foundButDrifted)
        {
            foundButDrifted = false;

            if (!_byPieceId.TryGetValue(rootPieceId, out var entry) || entry.rootPieceId.Equals(rootPieceId) == false)
            {
                rootGo = null;
                return false;
            }

            if (checkDrift && Vector3.Distance(entry.rootSpawnPosition, expectedSpawnPosition) > 0.1f)
            {
                rootGo = null;
                foundButDrifted = true;
                return false;
            }

            RemoveEntry(entry);
            resultPieces.AddRange(entry.pieces);
            rootGo = entry.rootGo;
            return true;
        }

        /// <summary>
        /// Takes the complete tree of the given prefab whose spawn position is nearest to the
        /// requested one. Used to reuse a mispredicted spawn's instance under a different id.
        /// </summary>
        public bool TryTakeNearestCompleteTree(PackedInt prefabId, Vector3 spawnPosition,
            List<PooledPiece> resultPieces, out GameObject rootGo)
        {
            Entry closest = null;
            float closestError = float.MaxValue;

            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];

                if (!entry.isComplete || entry.prefabId != prefabId)
                    continue;

                float posError = Vector3.Distance(entry.rootSpawnPosition, spawnPosition);

                if (posError < closestError)
                {
                    closestError = posError;
                    closest = entry;
                }
            }

            if (closest == null)
            {
                rootGo = null;
                return false;
            }

            RemoveEntry(closest);
            resultPieces.AddRange(closest.pieces);
            rootGo = closest.rootGo;
            return true;
        }

        /// <summary>
        /// Takes a single piece out of the pool, splitting its entry when the piece sits inside
        /// a pooled subtree: pooled pieces below it become their own entries and stay pooled.
        /// </summary>
        public bool TryTakePiece(PredictedObjectID pieceId, out GameObject go)
        {
            if (!_byPieceId.TryGetValue(pieceId, out var entry))
            {
                go = null;
                return false;
            }

            int pieceIdx = -1;
            for (var i = 0; i < entry.pieces.Count; i++)
            {
                if (entry.pieces[i].id.Equals(pieceId))
                {
                    pieceIdx = i;
                    break;
                }
            }

            if (pieceIdx == -1)
            {
                go = null;
                return false;
            }

            var piece = entry.pieces[pieceIdx];
            go = piece.gameObject;

            if (!go)
            {
                entry.pieces.RemoveAt(pieceIdx);
                _byPieceId.Remove(pieceId);
                go = null;
                return false;
            }

            SplitOffChildSubtrees(entry, go);

            entry.pieces.RemoveAt(pieceIdx);
            _byPieceId.Remove(pieceId);
            entry.isComplete = false;

            if (go.transform.parent != null)
                go.transform.SetParent(null, false);

            if (entry.pieces.Count == 0)
                _entries.Remove(entry);
            else if (go == entry.rootGo)
            {
                var newRoot = entry.pieces[0];
                entry.rootGo = newRoot.gameObject;
                entry.rootPieceId = newRoot.id;
            }

            return true;
        }

        void SplitOffChildSubtrees(Entry entry, GameObject piece)
        {
            _entryPieceScratch.Clear();
            for (var i = 0; i < entry.pieces.Count; i++)
            {
                var go = entry.pieces[i].gameObject;
                if (go && go != piece)
                    _entryPieceScratch.Add(go);
            }

            var subtreeRoots = ListPool<Transform>.Instantiate();
            CollectTopmostEntryPieces(piece.transform, subtreeRoots);

            for (var i = 0; i < subtreeRoots.Count; i++)
            {
                var subRoot = subtreeRoots[i];
                subRoot.SetParent(null, false);

                var subEntry = new Entry
                {
                    rootGo = subRoot.gameObject,
                    prefabId = entry.prefabId,
                    addedTick = entry.addedTick,
                    rootSpawnPosition = subRoot.position,
                    isComplete = false
                };

                MovePiecesInSubtree(entry, subEntry, subRoot);
                subEntry.rootPieceId = subEntry.pieces.Count > 0 ? subEntry.pieces[0].id : default;
                _entries.Add(subEntry);
            }

            ListPool<Transform>.Destroy(subtreeRoots);
        }

        void CollectTopmostEntryPieces(Transform current, List<Transform> results)
        {
            int childCount = current.childCount;
            for (var i = 0; i < childCount; i++)
            {
                var child = current.GetChild(i);

                if (_entryPieceScratch.Contains(child.gameObject))
                    results.Add(child);
                else
                    CollectTopmostEntryPieces(child, results);
            }
        }

        void MovePiecesInSubtree(Entry from, Entry to, Transform subRoot)
        {
            for (var i = from.pieces.Count - 1; i >= 0; i--)
            {
                var candidate = from.pieces[i];
                if (!candidate.gameObject)
                    continue;

                var t = candidate.gameObject.transform;
                if (t == subRoot || t.IsChildOf(subRoot))
                {
                    from.pieces.RemoveAt(i);
                    to.pieces.Add(candidate);
                    _byPieceId[candidate.id] = to;
                }
            }

            to.pieces.Sort((a, b) => a.pieceIndex.CompareTo(b.pieceIndex));
        }

        void RemoveEntry(Entry entry)
        {
            for (var i = 0; i < entry.pieces.Count; i++)
                _byPieceId.Remove(entry.pieces[i].id);
            _entries.Remove(entry);
        }

        public void ClearOld(PredictionManager predictionManager)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];

                if (entry.prefabId.value < 0)
                    continue;

                var delta = predictionManager.localTick - entry.addedTick;

                if (delta <= (uint)predictionManager.tickRate * 2)
                    continue;

                DestroyEntry(predictionManager, entry);
            }
        }

        public void Clear(PredictionManager predictionManager)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
                DestroyEntry(predictionManager, _entries[i]);
        }

        void DestroyEntry(PredictionManager predictionManager, Entry entry)
        {
            for (var i = 0; i < entry.pieces.Count; i++)
                _byPieceId.Remove(entry.pieces[i].id);

            _entries.Remove(entry);

            if (!entry.rootGo)
                return;

            if (entry.isComplete)
                predictionManager.InternalDelete(entry.prefabId, entry.rootGo);
            else
                UnityProxy.DestroyImmediateDirectly(entry.rootGo);
        }
    }
}
