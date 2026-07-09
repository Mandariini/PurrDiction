using System;
using System.Collections.Generic;
using PurrNet.Modules;
using PurrNet.Packing;
using Unity.Profiling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract partial class PredictedIdentity : MonoBehaviour
    {
        public ulong? lastVerifiedTick { get; internal set; }

        public virtual string GetExtraString()
        {
            return string.Empty;
        }

        protected readonly ProfilerMarker simulateMarker;

        private static readonly Dictionary<Type, ProfilerMarker> _simulateMarkers = new ();

        protected PredictedIdentity()
        {
            var type = GetType();
            if (!_simulateMarkers.TryGetValue(type, out simulateMarker))
            {
                simulateMarker = new ProfilerMarker($"{type.Name}.Simulate");
                _simulateMarkers.Add(type, simulateMarker);
            }
        }

        public PredictionManager predictionManager { get; protected set; }

        /// <summary>
        /// Represents the identifier of the owner associated with this object.
        /// Used to track ownership, enabling control over inputs.
        /// </summary>
        public PlayerID? owner;

        /// <summary>
        /// The unique identifier for this object.
        /// Can be used to identify the object across the network.
        /// </summary>
        public PredictedComponentID id;

        internal bool isFreshSpawn = true;
        internal bool preservesStateOnSetup { get; private set; }
        private bool _simulateSoftCorrectionDuringReplay;
        private bool _skipReplaySpawnInitialization;

        public virtual bool hasInput => false;

        internal virtual bool isEventHandler => false;

        public virtual bool controlsTransformPolicy => false;

        [Header("Predicted Identity")]
        [SerializeField, Tooltip("Use the nearest PredictionPolicyScope by default, or explicitly override it for this identity.")]
        private PredictionPolicySource _predictionPolicySource = PredictionPolicySource.UseScope;

        [SerializeField, Tooltip("Local prediction policy used when this identity overrides its scope, or when no PredictionPolicyScope is found.")]
        private PredictionPolicy _predictionPolicy = PredictionPolicy.FullPrediction;

        /// <summary>
        /// How this identity participates in client-side prediction and reconciliation.
        /// See <see cref="PredictionPolicy"/> for the semantics of each mode.
        /// </summary>
        public PredictionPolicy predictionPolicy { get; private set; }

        /// <summary>
        /// The configured (serialized) policy, re-applied on every registration including pooled reuse.
        /// Setting this on a spawned identity applies the change immediately via <see cref="SetPredictionPolicy"/>.
        /// </summary>
        public PredictionPolicy configuredPredictionPolicy
        {
            get => _predictionPolicy;
            set
            {
                _predictionPolicy = NormalizePredictionPolicy(value, predictionManager);
                if (predictionManager && UsesConfiguredPredictionPolicy())
                    SetPredictionPolicy(ResolvePredictionPolicy());
            }
        }

        public PredictionPolicySource predictionPolicySource
        {
            get => _predictionPolicySource;
            set
            {
                if (_predictionPolicySource == value)
                    return;

                _predictionPolicySource = value;
                RefreshResolvedPredictionPolicy();
            }
        }

        internal bool OverridesPredictionPolicyScope()
            => _predictionPolicySource == PredictionPolicySource.OverrideScope;

        private bool UsesConfiguredPredictionPolicy()
            => OverridesPredictionPolicyScope() || !TryGetPredictionPolicyScope(out _);

        public bool TryGetPredictionPolicyScope(out PredictionPolicyScope scope)
        {
            var current = transform;
            while (current)
            {
                if (current.TryGetComponent(out scope))
                    return true;

                current = current.parent;
            }

            scope = null;
            return false;
        }

        protected virtual PredictionPolicy ResolvePredictionPolicy()
        {
            if (!OverridesPredictionPolicyScope() && TryGetPredictionPolicyScope(out var scope))
                return NormalizePredictionPolicy(scope.ResolvePolicy(), false);

            return NormalizePredictionPolicy(_predictionPolicy, false);
        }

        internal PredictionPolicy ResolveDelegatedPredictionPolicy()
            => predictionManager && !isFreshSpawn ? predictionPolicy : ResolvePredictionPolicy();

        internal void RefreshResolvedPredictionPolicy()
        {
            if (predictionManager)
                SetPredictionPolicy(ResolvePredictionPolicy());
        }

        protected virtual void OnTransformParentChanged()
        {
            RefreshResolvedPredictionPolicy();
        }

        /// <summary>
        /// Changes the prediction policy at runtime. Deterministic identities support
        /// <see cref="PredictionPolicy.FullPrediction"/>, <see cref="PredictionPolicy.ServerRelay"/>,
        /// and <see cref="PredictionPolicy.PredictedIfOwned"/>. Switching mid-game is safest at
        /// ownership changes; the next reconcile realigns the identity with its new timeline.
        /// </summary>
        public void SetPredictionPolicy(PredictionPolicy policy)
        {
            policy = NormalizePredictionPolicy(policy, true);

            if (predictionPolicy == policy)
                return;

            var oldPolicy = predictionPolicy;
            predictionPolicy = policy;
            OnPredictionPolicyChanged(oldPolicy, policy);
        }

        private PredictionPolicy NormalizePredictionPolicy(PredictionPolicy policy, bool log)
        {
            if (!isDeterministic || policy != PredictionPolicy.SoftCorrection)
                return policy;

            if (log)
            {
                Logging.PurrLogger.LogError(
                    $"Deterministic identities do not support {nameof(PredictionPolicy.SoftCorrection)} because they do not receive authoritative state deltas to correct against.",
                    this);
            }

            return PredictionPolicy.FullPrediction;
        }

        protected virtual void OnPredictionPolicyChanged(PredictionPolicy oldPolicy, PredictionPolicy newPolicy) { }

        protected void SyncControlledTransformPolicy(PredictionPolicy policy)
        {
            if (!controlsTransformPolicy)
                return;

            if (TryGetComponent(out PredictedTransform predictedTransform) && predictedTransform != this)
                predictedTransform.SetPredictionPolicy(policy);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal PredictionPolicy EffectivePolicy()
        {
            if (predictionPolicy != PredictionPolicy.PredictedIfOwned)
                return predictionPolicy;
            return IsOwner() ? PredictionPolicy.FullPrediction : PredictionPolicy.ServerRelay;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool UsesSoftCorrectionTimeline()
            => predictionPolicy == PredictionPolicy.SoftCorrection;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool UsesServerRelayTimeline()
            => EffectivePolicy() == PredictionPolicy.ServerRelay;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool UsesFullPredictionTimeline()
            => EffectivePolicy() == PredictionPolicy.FullPrediction;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TracksEffectivePolicyChanges()
            => predictionPolicy == PredictionPolicy.PredictedIfOwned;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool AccumulatesRollbackInterpolationError()
            => UsesFullPredictionTimeline();

        internal bool IsSoftCorrectionReplaySimulating()
            => _simulateSoftCorrectionDuringReplay;

        internal bool SkipsReplaySpawnInitialization()
            => _skipReplaySpawnInitialization;

        public bool shouldSkipReplaySpawnInitialization => _skipReplaySpawnInitialization;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool SkipsCurrentSimulationPhase()
        {
            var manager = predictionManager;
            if (manager.cachedIsServer)
                return false;

            if (UsesFullPredictionTimeline())
                return false;

            if (UsesSoftCorrectionTimeline())
                return manager.isReplaying && !_simulateSoftCorrectionDuringReplay;

            return !(manager.isReplaying && manager.isVerified);
        }

        internal virtual void OnReplayStart() { }

        internal virtual void OnReplayEnd() { }

        internal virtual void SyncEffectivePolicySideEffects() { }

        [UsedByIL]
        public bool IsSimulating()
        {
            return predictionManager.isSimulating;
        }

        public virtual void OnPreSetup() {  }

        internal virtual void OnPrepareSimulationInputs(ulong tick, float delta) {  }

        public virtual void ResetState()
        {
            isServer = false;
            isFreshSpawn = true;
            preservesStateOnSetup = false;
            _simulateSoftCorrectionDuringReplay = false;
            _skipReplaySpawnInitialization = false;
            owner = null;
            id = default;
            ResetModulesForPool();
            OnRemovedFromPool();
        }

        internal void SetPreserveStateOnSetup(bool preserve)
        {
            preservesStateOnSetup = preserve;
        }

        internal void SetSoftCorrectionReplaySimulation(bool simulate)
        {
            _simulateSoftCorrectionDuringReplay = simulate;
        }

        internal void SetSkipReplaySpawnInitialization(bool skip)
        {
            _skipReplaySpawnInitialization = skip;
        }

        internal void SetOwner(PlayerID? player, bool syncPolicySideEffects = true)
        {
            owner = player;
            OnOwnerAssigned(player);

            if (syncPolicySideEffects)
                SyncEffectivePolicySideEffects();
        }

        protected virtual void OnOwnerAssigned(PlayerID? player) { }

        internal void TriggerOnRemovedFromPool()
        {
            OnRemovedFromPool();
        }

        protected virtual void OnRemovedFromPool() {}

        protected virtual void OnAddedToPool() {}

        protected virtual void LateAwake() {}

        protected virtual void Destroyed() {}

        private bool _destroyedFired;

        internal void TriggerDestroyedEvent()
        {
            if (_destroyedFired)
                return;

            _destroyedFired = true;
            Destroyed();
            TriggerModuleDestroyedEvents();
        }

        public bool isServer { get; private set; }

        public SceneID sceneId { get; private set; }

        internal virtual void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            isServer = manager.isServer;
            this.id = id;
            _destroyedFired = false;
            predictionManager = world;
            sceneId = world.sceneId;
            SetOwner(owner, false);
            SetPredictionPolicy(ResolvePredictionPolicy());

            if (!isFreshSpawn)
            {
                if (preservesStateOnSetup)
                    ModuleSetup(world);
                else ResetModulesForReuse(world);
                return;
            }

            isFreshSpawn = false;

            BeginInitialModuleSetup();
            try
            {
                ModuleSetup(world);
                LateAwake();
            }
            finally
            {
                EndInitialModuleSetup();
            }
        }

        protected virtual void OnDestroy()
        {
            TriggerDestroyedEvent();
            TearDownAllModules();

            if (predictionManager)
                predictionManager.UnregisterInstance(this);
        }

        public bool isOwner => IsOwner();

        public bool isController
        {
            get
            {
                if (!predictionManager)
                    return false;

                var player = predictionManager.isSpawned ? predictionManager.localPlayer ?? default : default;
                return IsOwner(player, predictionManager.cachedIsServer);
            }
        }

        public bool IsOwner()
        {
            if (predictionManager && predictionManager.isSpawned && owner == predictionManager.localPlayer)
                return true;
            return false;
        }

        public bool IsOwner(PlayerID player)
        {
            return owner == player;
        }

        public bool IsOwner(PlayerID? player)
        {
            return owner == player;
        }

        public bool IsOwner(PlayerID player, bool asServer)
        {
            if (owner.HasValue)
            {
                if (owner.Value.isBot)
                    return asServer;
                return owner == player;
            }
            return asServer;
        }

        internal abstract void SimulateTick(ulong tick, float delta);

        internal abstract void LateSimulateTick(float delta);

        public virtual void PostSimulate() {}

        internal abstract void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate);

        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        public abstract void UpdateRollbackInterpolationState(float delta, bool accumulateError);

        public abstract void ResetInterpolation();

        private PlayerID? _lastOwner;

        public virtual bool isDeterministic => false;

        /// <summary>
        /// Called once when owner changes
        /// This is meant to be used for view/visuals only and not part of the simulation
        /// </summary>
        public virtual void OnViewOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner) { }

        internal virtual void UpdateView(float deltaTime)
        {
            if (owner != _lastOwner)
            {
                OnViewOwnerChanged(_lastOwner, owner);
                _lastOwner = owner;
            }
        }

        internal virtual void LateUpdateView(float deltaTime) { }

        internal abstract void GetLatestUnityState();

        internal abstract void WriteFirstState(ulong tick, BitPacker packer);

        internal abstract bool WriteCurrentState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule);

        internal abstract void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable);

        internal abstract void ReadFirstState(ulong tick, BitPacker packer);

        internal abstract void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule);

        internal abstract void ReadInput(ulong tick, PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable);

        internal abstract void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable);

        public GameObject GetRoot()
        {
            var current = transform;

            while (current.parent != null)
            {
                if (current.parent.GetComponent<PredictedIdentity>() == null)
                    break;

                current = current.parent;
            }

            return current.gameObject;
        }

        internal void TriggerOnPooledEvent()
        {
            ReleasePredictionStateForPool();
            OnAddedToPool();
        }

        internal virtual void ReleasePredictionStateForPool()
        {
            ReleaseModuleStateForPool();
        }

        public abstract void WriteFirstInput(ulong localTick, BitPacker packer);

        public abstract void ReadFirstInput(ulong localTick, BitPacker packer);

        internal abstract void ClearFuture(ulong stateTick);
    }
}
