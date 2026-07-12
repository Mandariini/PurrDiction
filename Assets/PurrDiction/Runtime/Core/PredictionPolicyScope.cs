using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    [DisallowMultipleComponent]
    [AddComponentMenu("PurrDiction/Prediction Policy Scope")]
    public sealed class PredictionPolicyScope : MonoBehaviour
    {
        [SerializeField] private bool _inheritFromParent;
        [SerializeField] private PredictionPolicy _predictionPolicy = PredictionPolicy.FullPrediction;

        public bool inheritFromParent
        {
            get => _inheritFromParent;
            set
            {
                if (_inheritFromParent == value)
                    return;

                _inheritFromParent = value;
                ApplyToIdentities();
            }
        }

        public PredictionPolicy configuredPredictionPolicy
        {
            get => _predictionPolicy;
            set => SetPredictionPolicy(value);
        }

        public void SetPredictionPolicy(PredictionPolicy policy)
        {
            if (_predictionPolicy == policy)
                return;

            _predictionPolicy = policy;
            ApplyToIdentities();
        }

        public PredictionPolicy ResolvePolicy()
        {
            if (_inheritFromParent && TryGetParentScope(out var parent))
                return parent.ResolvePolicy();

            return _predictionPolicy;
        }

        internal PredictionPolicy ResolvePolicyForSetup()
        {
            if (_inheritFromParent && TryGetEnabledParentScope(out var parent))
                return parent.ResolvePolicyForSetup();

            return _predictionPolicy;
        }

        private bool TryGetEnabledParentScope(out PredictionPolicyScope scope)
        {
            var current = transform.parent;
            while (current)
            {
                if (current.TryGetComponent(out scope) && scope.enabled)
                    return true;

                current = current.parent;
            }

            scope = null;
            return false;
        }

        public bool TryGetParentScope(out PredictionPolicyScope scope)
        {
            var current = transform.parent;
            while (current)
            {
                if (current.TryGetComponent(out scope) && scope.isActiveAndEnabled)
                    return true;

                current = current.parent;
            }

            scope = null;
            return false;
        }

        public void ApplyToIdentities()
        {
            if (!isActiveAndEnabled)
                return;

            var identities = ListPool<PredictedIdentity>.Instantiate();
            try
            {
                GetComponentsInChildren(true, identities);
                for (var i = 0; i < identities.Count; i++)
                {
                    var identity = identities[i];
                    if (!identity || identity.OverridesPredictionPolicyScope())
                        continue;
                    if (!identity.TryGetPredictionPolicyScope(out var scope) || scope != this)
                        continue;

                    identity.RefreshResolvedPredictionPolicy();
                }
            }
            finally
            {
                ListPool<PredictedIdentity>.Destroy(identities);
            }

            var scopes = ListPool<PredictionPolicyScope>.Instantiate();
            try
            {
                GetComponentsInChildren(true, scopes);
                for (var i = 0; i < scopes.Count; i++)
                {
                    var scope = scopes[i];
                    if (!scope || scope == this || !scope._inheritFromParent)
                        continue;
                    if (!scope.TryGetParentScope(out var parent) || parent != this)
                        continue;

                    scope.ApplyToIdentities();
                }
            }
            finally
            {
                ListPool<PredictionPolicyScope>.Destroy(scopes);
            }
        }

        private void RefreshDescendantIdentities()
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();
            try
            {
                GetComponentsInChildren(true, identities);
                for (var i = 0; i < identities.Count; i++)
                {
                    var identity = identities[i];
                    if (identity && !identity.OverridesPredictionPolicyScope())
                        identity.RefreshResolvedPredictionPolicy();
                }
            }
            finally
            {
                ListPool<PredictedIdentity>.Destroy(identities);
            }
        }

        private void OnEnable()
        {
            ApplyToIdentities();
        }

        private void OnDisable()
        {
            // Deactivating a pooled hierarchy must not transiently replace the applied
            // policy before PredictionManager unregisters its identities. In particular,
            // SoftCorrection state is intentionally preserved for same-ID replay reuse.
            // Disabling or removing the scope component on an active hierarchy still
            // needs to make descendants fall back to their next policy source.
            if (!gameObject.activeInHierarchy)
                return;

            RefreshDescendantIdentities();
        }

        private void OnTransformParentChanged()
        {
            ApplyToIdentities();
        }

        private void OnTransformChildrenChanged()
        {
            ApplyToIdentities();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying)
                ApplyToIdentities();
        }
#endif
    }
}
