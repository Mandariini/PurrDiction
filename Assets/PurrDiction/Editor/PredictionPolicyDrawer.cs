using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomPropertyDrawer(typeof(PredictionPolicy))]
    public class PredictionPolicyDrawer : PropertyDrawer
    {
        private static readonly PredictionPolicy[] _deterministicPolicies =
        {
            PredictionPolicy.FullPrediction,
            PredictionPolicy.ServerRelay,
            PredictionPolicy.PredictedIfOwned
        };

        private static readonly GUIContent[] _deterministicPolicyLabels =
        {
            new("Full Prediction"),
            new("Server Relay"),
            new("Predicted If Owned")
        };

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var identity = property.serializedObject.targetObject as PredictedIdentity;

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
                DrawDelegated(position, label, policyOwner.GetResolvedPredictionPolicy(),
                    $"Controlled by {policyOwner.GetType().Name} on this GameObject. Pose and velocity for one physics body must use the same prediction policy.");
                return;
            }

            if (identity && UsesScope(identity, property) && identity.TryGetPredictionPolicyScope(out var scope))
            {
                DrawDelegated(position, label, ResolveDisplayPolicy(identity, scope.ResolvePolicy()),
                    "Controlled by the nearest active PredictionPolicyScope. Set Prediction Policy Source to Override Scope to configure this identity independently.");
                return;
            }

            if (identity && identity.isDeterministic)
            {
                DrawDeterministic(position, property, label);
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

        private static PredictionPolicy ResolveDisplayPolicy(PredictedIdentity identity, PredictionPolicy policy)
        {
            if (identity && identity.isDeterministic && policy == PredictionPolicy.SoftCorrection)
                return PredictionPolicy.FullPrediction;

            return policy;
        }

        private static void DrawDeterministic(Rect position, SerializedProperty property, GUIContent label)
        {
            var policy = (PredictionPolicy)property.enumValueIndex;
            int index = DeterministicIndexOf(policy);
            var deterministicLabel = new GUIContent(label.text,
                "Deterministic identities do not send per-tick state. FullPrediction predicts and replays locally; ServerRelay simulates only verified ticks from deterministic history; PredictedIfOwned switches between those modes by owner. SoftCorrection is unavailable because it needs authoritative state deltas.");

            int selected = EditorGUI.Popup(position, deterministicLabel, index, _deterministicPolicyLabels);
            property.enumValueIndex = (int)_deterministicPolicies[selected];
        }

        private static int DeterministicIndexOf(PredictionPolicy policy)
        {
            for (int i = 0; i < _deterministicPolicies.Length; i++)
            {
                if (_deterministicPolicies[i] == policy)
                    return i;
            }

            return 0;
        }

        private static void DrawDelegated(Rect position, GUIContent label, PredictionPolicy policy, string tooltip)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.EnumPopup(position,
                    new GUIContent(label.text, tooltip),
                    policy);
            }
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
