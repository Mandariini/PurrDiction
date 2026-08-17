using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomPropertyDrawer(typeof(PredictionPolicy))]
    public class PredictionPolicyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyPath != "_predictionPolicy" ||
                property.serializedObject.isEditingMultipleObjects)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            var target = property.serializedObject.targetObject;
            var identity = target as PredictedIdentity;

            if (identity && Application.isPlaying)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUI.EnumPopup(position,
                        new GUIContent(label.text, "Locked during play mode. Change it through configuredPredictionPolicy, SetPredictionPolicy, or SetPredictionPolicyOverride."),
                        identity.predictionPolicy);
                }
                return;
            }

            if (identity is PredictedTransform predictedTransform &&
                predictedTransform.TryGetTransformPolicyOwner(out var policyOwner))
            {
                DrawDelegated(position, label, policyOwner,
                    policyOwner.GetResolvedPredictionPolicy(),
                    $"Controlled by {policyOwner.GetType().Name} on this GameObject. Pose and velocity for one physics body must use the same prediction policy.");
                return;
            }

            if (identity && UsesScope(identity, property) && identity.TryGetPredictionPolicyScope(out var scope))
            {
                var provider = ResolvePolicyProvider(scope);
                DrawDelegated(position, label, provider,
                    scope.ResolvePolicy(identity),
                    "Controlled by a PredictionPolicyScope. Set Prediction Policy Source to Override Scope to configure this identity independently.");
                return;
            }

            if (target is PredictionPolicyScope policyScope &&
                UsesParentScope(policyScope, property) &&
                policyScope.TryGetParentScope(out var parentScope))
            {
                var provider = ResolvePolicyProvider(parentScope);
                DrawDelegated(position, label, provider, parentScope.ResolvePolicy(),
                    "Inherited from a parent PredictionPolicyScope. Disable Inherit From Parent to configure this scope independently.");
                return;
            }

            if (identity && (identity.isDeterministic || !identity.supportsSoftCorrection))
            {
                DrawWithAutomaticRelayFallback(position, property, label, identity.isDeterministic);
                return;
            }

            EditorGUI.PropertyField(position, property, label);
        }

        private static bool UsesScope(PredictedIdentity identity, SerializedProperty property)
        {
            var source = property.serializedObject.FindProperty("_predictionPolicySource");
            if (source == null)
                return identity.predictionPolicySource != PredictionPolicySource.OverrideScope;

            return source.enumValueIndex != (int)PredictionPolicySource.OverrideScope;
        }

        private static bool UsesParentScope(PredictionPolicyScope scope, SerializedProperty property)
        {
            var inherit = property.serializedObject.FindProperty("_inheritFromParent");
            return inherit == null ? scope.inheritFromParent : inherit.boolValue;
        }

        private static PredictionPolicyScope ResolvePolicyProvider(PredictionPolicyScope scope)
        {
            var provider = scope;
            while (provider && provider.inheritFromParent && provider.TryGetParentScope(out var parent))
                provider = parent;

            return provider;
        }

        private static void DrawWithAutomaticRelayFallback(
            Rect position,
            SerializedProperty property,
            GUIContent label,
            bool deterministic)
        {
            var tooltip = deterministic
                ? "Deterministic identities do not receive authoritative state deltas for correction. SoftCorrection behaves as ServerRelay, while PredictedIfOwnedWithSoftFallback predicts when owned and relays otherwise."
                : "This identity does not implement verified-state correction. SoftCorrection behaves as ServerRelay, while PredictedIfOwnedWithSoftFallback predicts when owned and relays otherwise. Override supportsSoftCorrection and OnVerifiedStateReceived to opt into soft correction.";
            var policyLabel = new GUIContent(label.text, tooltip);
            EditorGUI.PropertyField(position, property, policyLabel);
        }

        private static void DrawDelegated(
            Rect position,
            GUIContent label,
            Object provider,
            PredictionPolicy policy,
            string tooltip)
        {
            var fieldRect = EditorGUI.PrefixLabel(position, label);
            var icon = provider
                ? EditorGUIUtility.ObjectContent(provider, provider.GetType()).image
                : null;
            var providerName = provider ? provider.name : "Missing Source";
            var value = new GUIContent(
                $"{providerName} ({ObjectNames.NicifyVariableName(policy.ToString())})",
                icon,
                tooltip);

            using (new EditorGUI.DisabledScope(true))
                GUI.Button(fieldRect, value, EditorStyles.objectField);

            if (provider && GUI.Button(fieldRect, GUIContent.none, GUIStyle.none))
                EditorGUIUtility.PingObject(provider);
        }
    }

    [CustomPropertyDrawer(typeof(PredictionPolicySource))]
    public class PredictionPolicySourceDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            bool locked = Application.isPlaying && property.serializedObject.targetObject is PredictedIdentity;
            var content = locked
                ? new GUIContent(label.text, "Locked during play mode. Change the source through predictionPolicySource, SetPredictionPolicyOverride, or UsePredictionPolicyScope.")
                : label;

            using (new EditorGUI.DisabledScope(locked))
                EditorGUI.PropertyField(position, property, content);
        }
    }
}
