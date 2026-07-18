using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedHierarchy : PredictedIdentity<PredictedHierarchyState>
    {
        readonly List<InstanceDetails> _spawnedPrefabs = new ();
        readonly Dictionary<PredictedObjectID, InstanceDetails> _recordsById = new ();
        readonly Dictionary<PredictedObjectID, GameObject> _instanceMap = new ();
        readonly Dictionary<GameObject, PredictedObjectID> _goToId = new ();
        readonly HashSet<PredictedObjectID> _isSceneObject = new ();
        readonly Dictionary<PredictedObjectID, PredictedComponentID?> _runtimeParents = new ();
        readonly Dictionary<PredictedObjectID, PredictedComponentID?> _desiredParentsScratch = new ();
        readonly List<PredictedObjectID> _reservedSceneObjects = new ();
        readonly Dictionary<int, PiecePrototype> _prototypes = new ();
        readonly PredictedPiecePool _pool = new ();

        readonly Dictionary<PredictedObjectID, int> _targetIdsScratch = new ();
        readonly List<InstanceDetails> _removalScratch = new ();
        readonly HashSet<PredictedObjectID> _removalSetScratch = new ();
        readonly Dictionary<PredictedObjectID, List<InstanceDetails>> _additionGroups = new ();
        readonly List<PredictedObjectID> _additionGroupOrder = new ();
        readonly Dictionary<uint, GameObject> _pieceGoScratch = new ();
        readonly List<PooledPiece> _takenPiecesScratch = new ();
        readonly HashSet<uint> _individuallyTakenScratch = new ();
        readonly List<InstanceDetails> _donorNeededScratch = new ();
        readonly List<PooledPiece> _memberPiecesScratch = new ();
        readonly List<InstanceDetails> _recordBuildScratch = new ();
        readonly List<GameObject> _collectScratch = new ();

        private uint _nextInstanceId = 2;

        protected override PredictedHierarchyState GetInitialState()
        {
            var state = new PredictedHierarchyState(
                DisposableList<InstanceDetails>.Create(16),
                DisposableList<PredictedObjectID>.Create(16),
                DisposableList<InstanceParent>.Create(16),
                _nextInstanceId);
            return state;
        }

        protected override void GetUnityState(ref PredictedHierarchyState state)
        {
            int count = _spawnedPrefabs.Count;
            state.spawnedPrefabs.Clear();

            if (state.spawnedPrefabs.list.Capacity < count)
                state.spawnedPrefabs.list.Capacity = count;

            for (var i = 0; i < count; i++)
                state.spawnedPrefabs.Add(_spawnedPrefabs[i]);

            state.parents.Clear();

            for (var i = 0; i < count; i++)
            {
                var instanceId = _spawnedPrefabs[i].instanceId;
                if (_runtimeParents.TryGetValue(instanceId, out var parentLink))
                    state.parents.Add(new InstanceParent(instanceId, parentLink));
            }

            state.nextInstanceId = _nextInstanceId;
        }

        private bool _isRollingBack = false;

        protected override void SetUnityState(PredictedHierarchyState state)
        {
            _isRollingBack = true;

            var target = state.spawnedPrefabs;

            _targetIdsScratch.Clear();
            for (var i = 0; i < target.Count; i++)
                _targetIdsScratch[target[i].instanceId] = i;

            _removalScratch.Clear();
            _removalSetScratch.Clear();

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];
                if (!_targetIdsScratch.ContainsKey(record.instanceId))
                {
                    _removalScratch.Add(record);
                    _removalSetScratch.Add(record.instanceId);
                }
            }

            if (_removalScratch.Count > 0)
                RemovePieceSet(_removalScratch, _removalSetScratch, true, false, false);

            _additionGroups.Clear();
            _additionGroupOrder.Clear();

            for (var i = 0; i < target.Count; i++)
            {
                var record = target[i];

                if (_instanceMap.ContainsKey(record.instanceId))
                    continue;

                var rootId = record.rootId;

                if (!_additionGroups.TryGetValue(rootId, out var list))
                {
                    list = ListPool<InstanceDetails>.Instantiate();
                    _additionGroups[rootId] = list;
                    _additionGroupOrder.Add(rootId);
                }

                list.Add(record);
            }

            for (var g = 0; g < _additionGroupOrder.Count; g++)
            {
                var rootId = _additionGroupOrder[g];
                var records = _additionGroups[rootId];
                records.Sort((a, b) => a.pieceIndex.value.CompareTo(b.pieceIndex.value));

                var prefabId = records[0].prefabId;
                var proto = GetPrototype(prefabId);

                if (proto == null)
                {
                    PurrLogger.LogError($"Mismatch: no prototype for prefab {prefabId}; cannot recreate instance {rootId}.");
                    ListPool<InstanceDetails>.Destroy(records);
                    continue;
                }

                bool liveAny = false;
                for (var k = 0; k < proto.pieceCount && !liveAny; k++)
                    liveAny = _instanceMap.ContainsKey(new PredictedObjectID(rootId.instanceId.value + (uint)k));

                if (!liveAny)
                {
                    CreateWholeInstance(prefabId, proto, records);
                }
                else
                {
                    for (var r = 0; r < records.Count; r++)
                        ResurrectPiece(records[r], proto);
                }

                ListPool<InstanceDetails>.Destroy(records);
            }

            _spawnedPrefabs.Clear();
            _recordsById.Clear();

            for (var i = 0; i < target.Count; i++)
            {
                var record = target[i];
                _spawnedPrefabs.Add(record);
                _recordsById[record.instanceId] = record;
            }

            _nextInstanceId = state.nextInstanceId;

            ApplyParents(state.parents);

            _isRollingBack = false;
        }

        private void ApplyParents(DisposableList<InstanceParent> parents)
        {
            _desiredParentsScratch.Clear();

            if (!parents.isDisposed)
            {
                for (var i = 0; i < parents.Count; i++)
                {
                    var link = parents[i];
                    _desiredParentsScratch[link.child] = link.parent;
                }
            }

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];
                var instanceId = record.instanceId;

                if (!_instanceMap.TryGetValue(instanceId, out var go) || !go)
                    continue;

                bool hasDesired = _desiredParentsScratch.TryGetValue(instanceId, out var desired);
                bool hasCurrent = _runtimeParents.TryGetValue(instanceId, out var current);

                if (hasDesired == hasCurrent && (!hasDesired || SameAttach(desired, current)))
                    continue;

                if (hasDesired)
                {
                    if (desired.HasValue && predictionManager.TryGetIdentity(desired.Value, out var parentIdentity) && parentIdentity)
                        go.transform.SetParent(parentIdentity.transform, true);
                    else
                        go.transform.SetParent(null, true);

                    _runtimeParents[instanceId] = desired;
                }
                else
                {
                    var def = GetDefaultParent(record);

                    if (def.HasValue && predictionManager.TryGetIdentity(def.Value, out var defaultIdentity) && defaultIdentity)
                    {
                        var proto = GetPrototype(record.prefabId);
                        var path = proto != null && record.pieceIndex.value > 0
                            ? proto.pieces[record.pieceIndex.value].inverseSiblingPath
                            : null;
                        PiecePrototype.AttachAtPath(defaultIdentity.transform, go.transform, path, true);
                    }
                    else
                    {
                        go.transform.SetParent(null, true);
                    }

                    _runtimeParents.Remove(instanceId);
                }
            }
        }

        static bool SameAttach(PredictedComponentID? a, PredictedComponentID? b)
        {
            if (a.HasValue != b.HasValue)
                return false;
            return !a.HasValue || a.Value.objectId.Equals(b.Value.objectId);
        }

        private PredictedComponentID? GetDefaultParent(in InstanceDetails record)
        {
            if (record.pieceIndex.value > 0)
            {
                var proto = GetPrototype(record.prefabId);

                if (proto == null)
                    return null;

                int parentPieceIndex = proto.pieces[record.pieceIndex.value].parentPieceIndex;
                var parentId = new PredictedObjectID(record.rootId.instanceId.value + (uint)parentPieceIndex);
                return new PredictedComponentID(parentId, 0);
            }

            if (record.parent.HasValue)
                return new PredictedComponentID(record.parent.Value.objectId, 0);

            return null;
        }

        internal PiecePrototype GetPrototype(int prefabId)
        {
            if (_prototypes.TryGetValue(prefabId, out var proto))
                return proto;

            if (prefabId < 0)
                return null;

            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                return null;

            proto = PiecePrototype.Build(prefab);
            _prototypes[prefabId] = proto;
            return proto;
        }

        public PredictedObjectID? Create(int prefabId, PlayerID? owner = null)
        {
            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                return default;

            return Create(prefab, owner);
        }

        public PredictedObjectID? Create(GameObject prefab, Vector3 position, Quaternion rotation, PlayerID? owner = null)
        {
            if (!predictionManager.TryGetPrefab(prefab, out var pid))
                return default;

            return Create(pid, position, rotation, owner);
        }

        public PredictedObjectID? Create(int prefabId, Vector3 position, Quaternion rotation, PlayerID? owner = null)
        {
            return CreateInstance(prefabId, position, rotation, owner, null);
        }

        /// <summary>
        /// Spawns a prefab parented under the transform of the given predicted component.
        /// The position and rotation are local to that parent. The parent link is part of
        /// predicted state: rollbacks, replays and late joins restore it automatically.
        /// </summary>
        public PredictedObjectID? CreateChild(int prefabId, Vector3 localPosition, Quaternion localRotation, PredictedComponentID parent, PlayerID? owner = null)
        {
            return CreateInstance(prefabId, localPosition, localRotation, owner, parent);
        }

        /// <summary>
        /// Spawns a prefab parented under the transform of the given predicted component.
        /// The position and rotation are local to that parent. The parent link is part of
        /// predicted state: rollbacks, replays and late joins restore it automatically.
        /// </summary>
        public PredictedObjectID? CreateChild(GameObject prefab, Vector3 localPosition, Quaternion localRotation, PredictedComponentID parent, PlayerID? owner = null)
        {
            if (!predictionManager.TryGetPrefab(prefab, out var pid))
                return default;

            return CreateChild(pid, localPosition, localRotation, parent, owner);
        }

        /// <summary>
        /// Spawns a prefab parented under the given predicted identity.
        /// The position and rotation are local to that parent. The parent link is part of
        /// predicted state: rollbacks, replays and late joins restore it automatically.
        /// </summary>
        public PredictedObjectID? CreateChild(GameObject prefab, Vector3 localPosition, Quaternion localRotation, PredictedIdentity parent, PlayerID? owner = null)
        {
            if (!parent)
                return Create(prefab, localPosition, localRotation, owner);

            return CreateChild(prefab, localPosition, localRotation, parent.id, owner);
        }

        private PredictedObjectID? CreateInstance(int prefabId, Vector3 position, Quaternion rotation, PlayerID? owner, PredictedComponentID? parent)
        {
            var proto = GetPrototype(prefabId);

            if (proto == null)
            {
                PurrLogger.LogError($"Failed to get prefab {prefabId}");
                return default;
            }

            uint baseId = _nextInstanceId;
            _nextInstanceId += (uint)proto.pieceCount;

            _recordBuildScratch.Clear();
            var rootId = new PredictedObjectID(baseId);
            _recordBuildScratch.Add(new InstanceDetails(prefabId, 0, rootId, position, rotation, owner, parent));

            for (var k = 1; k < proto.pieceCount; k++)
                _recordBuildScratch.Add(new InstanceDetails(prefabId, (uint)k, new PredictedObjectID(baseId + (uint)k), Vector3.zero, Quaternion.identity, null, null));

            var rootGo = CreateWholeInstance(prefabId, proto, _recordBuildScratch);

            if (!rootGo)
                return default;

            for (var k = 0; k < _recordBuildScratch.Count; k++)
            {
                var record = _recordBuildScratch[k];
                _spawnedPrefabs.Add(record);
                _recordsById[record.instanceId] = record;
            }

            NotifyInstanceParentChanged(rootGo);

            if (!_isRollingBack && !predictionManager.isSimulating)
            {
                ref var state = ref currentState;
                GetUnityState(ref state);
            }

            return rootId;
        }

        private GameObject CreateWholeInstance(int prefabId, PiecePrototype proto, List<InstanceDetails> records)
        {
            _pieceGoScratch.Clear();
            _takenPiecesScratch.Clear();
            _individuallyTakenScratch.Clear();
            _donorNeededScratch.Clear();

            bool hasRoot = records[0].isRootRecord;
            var rootRecord = records[0];
            var rootId = rootRecord.rootId;

            bool reset = false;
            bool removedFromPoolEvent = false;
            GameObject rootGo = null;

            Transform parentTrs = null;

            if (hasRoot && rootRecord.parent.HasValue)
            {
                if (predictionManager.TryGetIdentity(rootRecord.parent.Value, out var parentIdentity) && parentIdentity)
                    parentTrs = parentIdentity.transform;
                else
                    PurrLogger.LogError($"Failed to resolve spawn parent {rootRecord.parent.Value} for prefab {prefabId}; spawning unparented.");
            }

            if (hasRoot)
            {
                if (!_pool.TryTakeTree(rootRecord.instanceId, rootRecord.spawnPosition, prefabId >= 0, _takenPiecesScratch, out rootGo, out var drifted))
                {
                    if (drifted || prefabId < 0)
                        _pool.TryTakeNearestCompleteTree(prefabId, rootRecord.spawnPosition, _takenPiecesScratch, out rootGo);
                }

                if (rootGo)
                {
                    if (!PreservesSoftCorrectionRootPose(rootGo, rootRecord.instanceId))
                        ApplySpawnPose(rootGo.transform, parentTrs, rootRecord.spawnPosition, rootRecord.spawnRotation);
                    else if (parentTrs)
                        rootGo.transform.SetParent(parentTrs, true);

                    for (var i = 0; i < _takenPiecesScratch.Count; i++)
                        _pieceGoScratch[_takenPiecesScratch[i].pieceIndex] = _takenPiecesScratch[i].gameObject;
                }
                else
                {
                    if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                    {
                        PurrLogger.LogError($"Failed to get prefab {prefabId}");
                        return null;
                    }

                    var worldPosition = parentTrs ? parentTrs.TransformPoint(rootRecord.spawnPosition) : rootRecord.spawnPosition;
                    var worldRotation = parentTrs ? parentTrs.rotation * rootRecord.spawnRotation : rootRecord.spawnRotation;

                    rootGo = predictionManager.InternalCreate(prefab, worldPosition, worldRotation, out var fromPool);
                    reset = fromPool;
                    removedFromPoolEvent = fromPool;

                    if (parentTrs)
                        ApplySpawnPose(rootGo.transform, parentTrs, rootRecord.spawnPosition, rootRecord.spawnRotation);

                    if (!proto.TryCollectInstancePieces(rootGo, _collectScratch))
                    {
                        UnityProxy.DestroyImmediateDirectly(rootGo);
                        return null;
                    }

                    for (var k = 0; k < _collectScratch.Count; k++)
                        _pieceGoScratch[(uint)k] = _collectScratch[k];
                }
            }

            for (var r = 0; r < records.Count; r++)
            {
                var record = records[r];
                uint k = record.pieceIndex.value;

                if (_pieceGoScratch.ContainsKey(k))
                    continue;

                if (_pool.TryTakePiece(record.instanceId, out var pieceGo))
                {
                    _pieceGoScratch[k] = pieceGo;
                    _individuallyTakenScratch.Add(k);
                }
                else
                {
                    _donorNeededScratch.Add(record);
                }
            }

            if (_donorNeededScratch.Count > 0)
                ExtractFromDonor(prefabId, proto, _donorNeededScratch, _pieceGoScratch, _individuallyTakenScratch);

            for (var r = 0; r < records.Count; r++)
            {
                var record = records[r];
                uint k = record.pieceIndex.value;

                if (!_individuallyTakenScratch.Contains(k) || !_pieceGoScratch.TryGetValue(k, out var pieceGo) || !pieceGo)
                    continue;

                var pp = proto.pieces[k];
                Transform attach = null;

                if (pp.parentPieceIndex >= 0 && _pieceGoScratch.TryGetValue((uint)pp.parentPieceIndex, out var parentGo) && parentGo)
                    attach = parentGo.transform;
                else if (rootGo)
                    attach = rootGo.transform;

                if (attach)
                {
                    PiecePrototype.AttachAtPath(attach, pieceGo.transform, pp.inverseSiblingPath, false);
                    pieceGo.transform.localPosition = pp.localPosition;
                    pieceGo.transform.localRotation = pp.localRotation;
                    pieceGo.transform.localScale = pp.localScale;
                }
                else
                {
                    pieceGo.transform.SetParent(null, false);
                    pieceGo.transform.SetPositionAndRotation(record.spawnPosition, record.spawnRotation);
                }

                pieceGo.SetActive(pp.activeSelf);
            }

            for (var k = proto.pieceCount - 1; k >= 0; k--)
            {
                uint pieceIndex = (uint)k;

                if (!_pieceGoScratch.TryGetValue(pieceIndex, out var extraGo) || !extraGo)
                    continue;

                bool wanted = false;
                for (var r = 0; r < records.Count && !wanted; r++)
                    wanted = records[r].pieceIndex.value == pieceIndex;

                if (wanted)
                    continue;

                var extraId = new PredictedObjectID(rootId.instanceId.value + pieceIndex);
                extraGo.transform.SetParent(null, true);
                extraGo.SetActive(false);
                _pool.PutPiece(prefabId, extraId, pieceIndex, extraGo, predictionManager.localTick);
                _pieceGoScratch.Remove(pieceIndex);
            }

            var instanceOwner = rootRecord.owner;

            for (var r = 0; r < records.Count; r++)
            {
                var record = records[r];

                if (!_pieceGoScratch.TryGetValue(record.pieceIndex.value, out var pieceGo) || !pieceGo)
                {
                    PurrLogger.LogError($"Mismatch: failed to materialize piece {record.instanceId} of prefab {prefabId}.");
                    continue;
                }

                if (_instanceMap.Remove(record.instanceId, out var other))
                    PurrLogger.LogError($"Duplicate instance ID {record.instanceId} for prefab {prefabId}. Existing GameObject: `{other.name}`, New GameObject: `{pieceGo.name}`", other);

                _instanceMap[record.instanceId] = pieceGo;
                _goToId[pieceGo] = record.instanceId;

                predictionManager.RegisterInstance(pieceGo, record.instanceId, instanceOwner, reset, removedFromPoolEvent);
            }

            if (rootGo && !rootGo.activeSelf)
                rootGo.SetActive(true);

            if (!rootGo && records.Count > 0 && _pieceGoScratch.TryGetValue(records[0].pieceIndex.value, out var firstGo))
                return firstGo;

            return rootGo;
        }

        private void ExtractFromDonor(int prefabId, PiecePrototype proto, List<InstanceDetails> needed,
            Dictionary<uint, GameObject> pieceGos, HashSet<uint> individuallyTaken)
        {
            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
            {
                PurrLogger.LogError($"Cannot rebuild {needed.Count} piece(s) of prefab {prefabId}: no prefab asset to instantiate (scene pieces cannot be rebuilt once destroyed).");
                return;
            }

            var donor = UnityProxy.InstantiateDirectly(prefab, Vector3.zero, Quaternion.identity, gameObject.scene);

            if (!proto.TryCollectInstancePieces(donor, _collectScratch))
            {
                UnityProxy.DestroyImmediateDirectly(donor);
                return;
            }

            var donorPieces = ListPool<GameObject>.Instantiate();
            donorPieces.AddRange(_collectScratch);

            for (var k = donorPieces.Count - 1; k >= 1; k--)
                donorPieces[k].transform.SetParent(null, false);

            for (var k = 0; k < donorPieces.Count; k++)
            {
                bool isNeeded = false;
                for (var n = 0; n < needed.Count && !isNeeded; n++)
                    isNeeded = needed[n].pieceIndex.value == (uint)k;

                if (isNeeded)
                {
                    pieceGos[(uint)k] = donorPieces[k];
                    individuallyTaken.Add((uint)k);
                }
                else
                {
                    UnityProxy.DestroyImmediateDirectly(donorPieces[k]);
                }
            }

            ListPool<GameObject>.Destroy(donorPieces);
        }

        private void ResurrectPiece(InstanceDetails record, PiecePrototype proto)
        {
            uint k = record.pieceIndex.value;

            if (k >= proto.pieceCount)
            {
                PurrLogger.LogError($"Mismatch: piece index {k} out of range for prefab {record.prefabId}.");
                return;
            }

            if (!_pool.TryTakePiece(record.instanceId, out var pieceGo))
            {
                _donorNeededScratch.Clear();
                _donorNeededScratch.Add(record);
                _pieceGoScratch.Clear();
                _individuallyTakenScratch.Clear();
                ExtractFromDonor(record.prefabId, proto, _donorNeededScratch, _pieceGoScratch, _individuallyTakenScratch);
                _pieceGoScratch.TryGetValue(k, out pieceGo);
            }

            if (!pieceGo)
            {
                PurrLogger.LogError($"Mismatch: failed to resurrect piece {record.instanceId} of prefab {record.prefabId}.");
                return;
            }

            var pp = proto.pieces[k];
            var defaultParentId = new PredictedObjectID(record.rootId.instanceId.value + (uint)pp.parentPieceIndex);

            if (pp.parentPieceIndex >= 0 && _instanceMap.TryGetValue(defaultParentId, out var parentGo) && parentGo)
            {
                PiecePrototype.AttachAtPath(parentGo.transform, pieceGo.transform, pp.inverseSiblingPath, false);
                pieceGo.transform.localPosition = pp.localPosition;
                pieceGo.transform.localRotation = pp.localRotation;
                pieceGo.transform.localScale = pp.localScale;
            }
            else
            {
                pieceGo.transform.SetParent(null, false);
                pieceGo.transform.SetPositionAndRotation(record.spawnPosition, record.spawnRotation);
            }

            pieceGo.SetActive(pp.activeSelf);

            PlayerID? owner = record.owner;
            if (_recordsById.TryGetValue(record.rootId, out var rootRecord))
                owner = rootRecord.owner;

            if (_instanceMap.Remove(record.instanceId, out var other))
                PurrLogger.LogError($"Duplicate instance ID {record.instanceId}. Existing GameObject: `{other.name}`, New GameObject: `{pieceGo.name}`", other);

            _instanceMap[record.instanceId] = pieceGo;
            _goToId[pieceGo] = record.instanceId;

            predictionManager.RegisterInstance(pieceGo, record.instanceId, owner, false, false);
        }

        private static void ApplySpawnPose(Transform trs, Transform parent, Vector3 position, Quaternion rotation)
        {
            if (parent)
            {
                trs.SetParent(parent, false);
                trs.localPosition = position;
                trs.localRotation = rotation;
            }
            else
            {
                trs.SetPositionAndRotation(position, rotation);
            }
        }

        internal void NotifyInstanceParentChanged(GameObject go)
        {
            if (!_goToId.TryGetValue(go, out var instanceId))
                return;

            if (!_recordsById.TryGetValue(instanceId, out var record))
                return;

            PredictedComponentID? resolved = TryResolveParentLink(go, out var parentLink)
                ? parentLink
                : (PredictedComponentID?)null;

            var def = GetDefaultParent(record);

            if (SameAttach(resolved, def))
                _runtimeParents.Remove(instanceId);
            else
                _runtimeParents[instanceId] = resolved;

            RefreshDescendantPolicies(go);
        }

        private void RefreshDescendantPolicies(GameObject go)
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, identities);

            for (var i = 0; i < identities.Count; i++)
                identities[i].RefreshResolvedPredictionPolicy();

            ListPool<PredictedIdentity>.Destroy(identities);
        }

        private bool TryResolveParentLink(GameObject go, out PredictedComponentID parent)
        {
            var current = go.transform.parent;

            while (current != null)
            {
                if (current.TryGetComponent(out PredictedIdentity identity) &&
                    ReferenceEquals(identity.predictionManager, predictionManager))
                {
                    parent = new PredictedComponentID(identity.id.objectId, 0);
                    return true;
                }

                current = current.parent;
            }

            parent = default;
            return false;
        }

        private bool PreservesSoftCorrectionRootPose(GameObject instance, PredictedObjectID instanceId)
        {
            if (!predictionManager.isReplaying || !instance)
                return false;

            return instance.TryGetComponent(out PredictedTransform predictedTransform) &&
                   predictedTransform.id.objectId.Equals(instanceId) &&
                   predictedTransform.previousRegisteredPredictionPolicy == PredictionPolicy.SoftCorrection &&
                   predictedTransform.ResolvePredictionPolicyForSetup() == PredictionPolicy.SoftCorrection;
        }

        protected override void Simulate(ref PredictedHierarchyState state, float delta)
        {
            for (var o = 0; o < state.toDelete.Count; o++)
                DeleteNow(state.toDelete[o]);
            state.toDelete.Clear();
        }

        private void LateUpdate()
        {
            _pool.ClearOld(predictionManager);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            ClearPool();
        }

        private void ClearPool()
        {
            _pool.Clear(predictionManager);
        }

        readonly List<(GameObject root, int pid)> _pendingSceneReservations = new ();
        readonly HashSet<Transform> _sceneBoundariesScratch = new ();

        internal void ReserveSceneObject(GameObject root, int pid)
        {
            _pendingSceneReservations.Add((root, pid));
        }

        internal void RegisterReservedSceneObjects()
        {
            _sceneBoundariesScratch.Clear();

            for (var i = 0; i < _pendingSceneReservations.Count; i++)
            {
                var root = _pendingSceneReservations[i].root;
                if (root)
                    _sceneBoundariesScratch.Add(root.transform);
            }

            for (var i = 0; i < _pendingSceneReservations.Count; i++)
            {
                var (root, pid) = _pendingSceneReservations[i];

                if (!root)
                    continue;

                var proto = PiecePrototype.Build(root, _sceneBoundariesScratch);

                if (proto == null)
                    continue;

                _prototypes[pid] = proto;

                if (!proto.TryCollectInstancePieces(root, _collectScratch, _sceneBoundariesScratch))
                    continue;

                uint baseId = _nextInstanceId;
                _nextInstanceId += (uint)proto.pieceCount;

                var rootId = new PredictedObjectID(baseId);
                root.transform.GetPositionAndRotation(out var rootPos, out var rootRot);

                for (var k = 0; k < proto.pieceCount; k++)
                {
                    var pieceId = new PredictedObjectID(baseId + (uint)k);
                    var pieceGo = _collectScratch[k];

                    var record = k == 0
                        ? new InstanceDetails(pid, 0, pieceId, rootPos, rootRot, null, null)
                        : new InstanceDetails(pid, (uint)k, pieceId, Vector3.zero, Quaternion.identity, null, null);

                    _isSceneObject.Add(pieceId);
                    _instanceMap.Add(pieceId, pieceGo);
                    _goToId.Add(pieceGo, pieceId);
                    _spawnedPrefabs.Add(record);
                    _recordsById[pieceId] = record;

                    predictionManager.RegisterInstance(pieceGo, pieceId, null, false, false);
                }

                _reservedSceneObjects.Add(rootId);
            }

            for (var i = 0; i < _reservedSceneObjects.Count; i++)
            {
                var rootId = _reservedSceneObjects[i];
                if (_instanceMap.TryGetValue(rootId, out var root) && root)
                    NotifyInstanceParentChanged(root);
            }

            _reservedSceneObjects.Clear();
            _pendingSceneReservations.Clear();
        }

        public PredictedObjectID? Create(GameObject prefab, PlayerID? owner = null)
        {
            var trs = prefab.transform;
            trs.GetPositionAndRotation(out var position, out var rotation);

            if (!predictionManager.TryGetPrefab(prefab, out var pid))
                return default;

            return Create(pid, position, rotation, owner);
        }

        public bool TryCreate(int prefabId, out PredictedObjectID id, PlayerID? owner = null)
        {
            var result = Create(prefabId, owner);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }

        public bool TryCreate(GameObject prefab, Vector3 position, Quaternion rotation, out PredictedObjectID id, PlayerID? owner = null)
        {
            var result = Create(prefab, position, rotation, owner);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }

        public bool TryCreate(GameObject prefab, out PredictedObjectID id, PlayerID? owner = null)
        {
            var result = Create(prefab, owner);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }

        public bool TryCreateAndGet<T>(int prefabId, out T component, PlayerID? owner = null) where T : Component
        {
            var objId = Create(prefabId, owner);
            return TryGetComponent(objId, out component);
        }

        public bool TryCreateAndGet<T>(GameObject prefab, Vector3 position, Quaternion rotation, out T component, PlayerID? owner = null) where T : Component
        {
            var objId = Create(prefab, position, rotation, owner);
            return TryGetComponent(objId, out component);
        }

        public bool TryCreateAndGet<T>(GameObject prefab, out T component, PlayerID? owner = null) where T : Component
        {
            var objId = Create(prefab, owner);
            return TryGetComponent(objId, out component);
        }

        public GameObject GetGameObject(PredictedObjectID? id)
        {
            if (!id.HasValue)
                return null;

            return _instanceMap.GetValueOrDefault(id.Value);
        }

        public T GetComponent<T>(PredictedObjectID? id)
        {
            if (!id.HasValue)
                return default;

            return GetComponent<T>(id.Value);
        }

        public T GetComponent<T>(PredictedObjectID id)
        {
            var go = _instanceMap.GetValueOrDefault(id);
            if (!go) return default;
            return go.GetComponent<T>();
        }

        public bool TryGetComponent<T>(PredictedObjectID id, out T go)
        {
            go = GetComponent<T>(id);
            return go != null;
        }

        public bool TryGetComponent<T>(PredictedObjectID? id, out T go)
        {
            go = GetComponent<T>(id);
            return go != null;
        }

        public bool TryGetId(GameObject go, out PredictedObjectID id)
        {
            if (!_goToId.TryGetValue(go, out id))
                return false;

            return true;
        }

        /// <summary>
        /// Resolves the root piece id of the spawn instance the given piece belongs to.
        /// </summary>
        public bool TryGetRootId(PredictedObjectID pieceId, out PredictedObjectID rootId)
        {
            if (_recordsById.TryGetValue(pieceId, out var record))
            {
                rootId = record.rootId;
                return true;
            }

            rootId = default;
            return false;
        }

        public bool TryGetGameObject(PredictedObjectID? id, out GameObject go)
        {
            if (!id.HasValue)
            {
                go = null;
                return false;
            }

            return _instanceMap.TryGetValue(id.Value, out go);
        }

        private void DeleteNow(PredictedObjectID id)
        {
            if (!_recordsById.TryGetValue(id, out var record))
                return;

            if (!_instanceMap.TryGetValue(id, out var instance) || !instance)
                return;

            _removalScratch.Clear();
            _removalSetScratch.Clear();
            CollectCascade(instance.transform, record.rootId);

            var isVerified = predictionManager.isVerified;
            bool canPool = record.prefabId.value < 0 || !isVerified;

            RemovePieceSet(_removalScratch, _removalSetScratch, canPool, true, true);

            if (record.isRootRecord)
                PromoteOrphans(record);
        }

        private void CollectCascade(Transform current, PredictedObjectID rootId)
        {
            if (_goToId.TryGetValue(current.gameObject, out var pieceId))
            {
                if (!_recordsById.TryGetValue(pieceId, out var pieceRecord) || !pieceRecord.rootId.Equals(rootId))
                    return;

                _removalScratch.Add(pieceRecord);
                _removalSetScratch.Add(pieceId);
            }

            int childCount = current.childCount;
            for (var i = 0; i < childCount; i++)
                CollectCascade(current.GetChild(i), rootId);
        }

        private void PromoteOrphans(InstanceDetails rootRecord)
        {
            var rootId = rootRecord.rootId;

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];

                if (!record.rootId.Equals(rootId) || record.instanceId.Equals(rootRecord.instanceId))
                    continue;

                if (!_instanceMap.TryGetValue(record.instanceId, out var go) || !go)
                    continue;

                go.transform.GetPositionAndRotation(out var pos, out var rot);
                var promoted = new InstanceDetails(record.prefabId, record.pieceIndex.value, record.instanceId, pos, rot, rootRecord.owner, record.parent);

                _spawnedPrefabs[i] = promoted;
                _recordsById[record.instanceId] = promoted;
            }
        }

        private void RemovePieceSet(List<InstanceDetails> records, HashSet<PredictedObjectID> memberSet,
            bool canPool, bool triggerDestroyEvent, bool removeRecords)
        {
            bool progress = true;

            while (progress)
            {
                progress = false;

                for (var i = 0; i < records.Count; i++)
                {
                    var record = records[i];

                    if (!memberSet.Contains(record.instanceId))
                        continue;

                    if (!_instanceMap.TryGetValue(record.instanceId, out var go) || !go)
                    {
                        _instanceMap.Remove(record.instanceId);
                        _runtimeParents.Remove(record.instanceId);
                        if (removeRecords)
                            RemoveRecord(record.instanceId);
                        memberSet.Remove(record.instanceId);
                        progress = true;
                        continue;
                    }

                    bool isTopmost = true;
                    var ancestor = go.transform.parent;

                    while (ancestor != null)
                    {
                        if (_goToId.TryGetValue(ancestor.gameObject, out var ancestorId) && memberSet.Contains(ancestorId))
                        {
                            isTopmost = false;
                            break;
                        }

                        ancestor = ancestor.parent;
                    }

                    if (!isTopmost)
                        continue;

                    RemoveSubtree(record, go, memberSet, canPool, triggerDestroyEvent, removeRecords);
                    progress = true;
                }
            }
        }

        private void RemoveSubtree(InstanceDetails topRecord, GameObject topGo, HashSet<PredictedObjectID> memberSet,
            bool canPool, bool triggerDestroyEvent, bool removeRecords)
        {
            _memberPiecesScratch.Clear();
            RescueAndCollect(topGo.transform, memberSet, _memberPiecesScratch);

            for (var i = 0; i < _memberPiecesScratch.Count; i++)
            {
                var piece = _memberPiecesScratch[i];

                predictionManager.UnregisterInstance(piece.gameObject, false, triggerDestroyEvent);

                _instanceMap.Remove(piece.id);
                _goToId.Remove(piece.gameObject);
                _runtimeParents.Remove(piece.id);
                memberSet.Remove(piece.id);

                if (removeRecords)
                    RemoveRecord(piece.id);
            }

            var proto = GetPrototype(topRecord.prefabId);
            bool isComplete = topRecord.isRootRecord && proto != null && _memberPiecesScratch.Count == proto.pieceCount;

            if (canPool)
            {
                bool detach = !topRecord.isRootRecord || TryResolveParentLink(topGo, out _);

                if (detach && topGo.transform.parent != null)
                    topGo.transform.SetParent(null, true);

                _pool.PutTree(topRecord.prefabId, topRecord.instanceId, topRecord.spawnPosition, topGo,
                    _memberPiecesScratch, predictionManager.localTick, isComplete);

                topGo.SetActive(false);
            }
            else
            {
                if (isComplete)
                    predictionManager.InternalDelete(topRecord.prefabId, topGo);
                else
                    UnityProxy.DestroyImmediateDirectly(topGo);
            }
        }

        private void RescueAndCollect(Transform current, HashSet<PredictedObjectID> memberSet, List<PooledPiece> members)
        {
            if (_goToId.TryGetValue(current.gameObject, out var pieceId))
            {
                if (!memberSet.Contains(pieceId))
                {
                    if (current.parent != null)
                        current.SetParent(null, true);
                    return;
                }

                uint pieceIndex = _recordsById.TryGetValue(pieceId, out var record) ? record.pieceIndex.value : 0;
                members.Add(new PooledPiece(pieceId, pieceIndex, current.gameObject));
            }

            for (var i = current.childCount - 1; i >= 0; i--)
                RescueAndCollect(current.GetChild(i), memberSet, members);
        }

        private void RemoveRecord(PredictedObjectID id)
        {
            if (!_recordsById.Remove(id))
                return;

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                if (_spawnedPrefabs[i].instanceId.Equals(id))
                {
                    _spawnedPrefabs.RemoveAt(i);
                    return;
                }
            }
        }

        public void Delete(GameObject go)
        {
            if (!go)
                return;

            EnqueueTopmostPieces(go.transform);
        }

        private void EnqueueTopmostPieces(Transform trs)
        {
            if (_goToId.TryGetValue(trs.gameObject, out var poid))
            {
                currentState.toDelete.Add(poid);
                return;
            }

            int children = trs.childCount;

            for (int i = 0; i < children; i++)
                EnqueueTopmostPieces(trs.GetChild(i));
        }

        public void Delete(PredictedIdentity pid)
        {
            if (pid)
                Delete(pid.gameObject);
        }

        public void Delete(PredictedObjectID? id)
        {
            if (id.TryGetGameObject(predictionManager, out var go))
                Delete(go);
        }

        public void Cleanup()
        {
            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];
                if (!_instanceMap.TryGetValue(record.instanceId, out var go) || !go)
                    continue;

                if (_isSceneObject.Contains(record.instanceId))
                {
                    predictionManager.UnregisterInstance(go, true, true);
                    continue;
                }

                predictionManager.UnregisterInstance(go, false, true);
            }

            for (var i = 0; i < _spawnedPrefabs.Count; i++)
            {
                var record = _spawnedPrefabs[i];

                if (_isSceneObject.Contains(record.instanceId))
                    continue;

                if (!_instanceMap.TryGetValue(record.instanceId, out var go) || !go)
                    continue;

                UnityProxy.DestroyImmediateDirectly(go);
            }

            _instanceMap.Clear();
            _goToId.Clear();
            _spawnedPrefabs.Clear();
            _recordsById.Clear();
            _isSceneObject.Clear();
            _runtimeParents.Clear();
            _reservedSceneObjects.Clear();
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }
    }
}
