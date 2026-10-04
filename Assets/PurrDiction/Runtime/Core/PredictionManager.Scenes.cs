using System;
using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Pooling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        [Header("Scenes")]
        [Tooltip("Also register PredictedIdentity scene objects from every loaded scene that has no PredictionManager of its own, in addition to this manager's own scene. " +
                 "Every peer must have the same scenes loaded before this manager spawns; scenes loaded later are never registered. " +
                 "Enable on exactly one manager per session.")]
        [SerializeField] private bool _registerOtherScenes;

        private readonly List<Scene> _managedScenesScratch = new();
        private Scene[] _managedScenes = Array.Empty<Scene>();
        private bool _sceneLoadHookSubscribed;

        /// <summary>
        /// The scenes this manager owns: its own scene first, then every registered scene that
        /// has no PredictionManager of its own when <see cref="_registerOtherScenes"/> is enabled.
        /// Scene object instance ids are assigned in this order, so every peer must resolve the
        /// same list for the ids to agree.
        /// </summary>
        public IReadOnlyList<Scene> managedScenes => _managedScenes;

        /// <summary>
        /// Collects this manager's scene, then (when <see cref="_registerOtherScenes"/> is enabled)
        /// every other loaded scene without a PredictionManager, in a path-ordered,
        /// peer-independent order, and queues their PredictedIdentity scene objects for reservation.
        /// </summary>
        private void RegisterScenes()
        {
            CollectManagedScenes(_managedScenesScratch);

            var identities = ListPool<PredictedIdentity>.Instantiate();

            for (var s = 0; s < _managedScenesScratch.Count; s++)
            {
                var scene = _managedScenesScratch[s];
                identities.Clear();

#if HAS_DISCOVERY_RULE
                SceneObjectsModule.GetScenePredictedIdentities(scene, identities, networkManager.networkRules.ShouldIncludeInstantiatedSceneObjects());
#else
                SceneObjectsModule.GetScenePredictedIdentities(scene, identities);
#endif

                for (var i = 0; i < identities.Count; i++)
                    _queue.Add(identities[i]);
            }

            ListPool<PredictedIdentity>.Destroy(identities);

            _managedScenes = _managedScenesScratch.ToArray();

            WarnAboutPhysicsSceneMismatch();
        }

        private void CollectManagedScenes(List<Scene> results)
        {
            results.Clear();
            results.Add(gameObject.scene);

            if (!_registerOtherScenes || !IsPrimarySceneRegistrar())
                return;

            var managerHandle = gameObject.scene.handle;

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);

                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                if (scene.handle == managerHandle)
                    continue;

                if (HasPredictionManager(scene))
                    continue;

                results.Add(scene);
            }

            if (results.Count > 2)
                results.Sort(1, results.Count - 1, SceneOrderComparer.instance);

            WarnAboutDuplicateSceneOrder(results);
        }

        /// <summary>
        /// When several managers enable <see cref="_registerOtherScenes"/>, only the one whose
        /// scene sorts first claims manager-less scenes. The choice is path based so every peer
        /// elects the same manager.
        /// </summary>
        private bool IsPrimarySceneRegistrar()
        {
            PredictionManager primary = null;

            foreach (var other in _instances.Values)
            {
                if (!other || !other._registerOtherScenes)
                    continue;

                if (primary == null || CompareManagers(other, primary) < 0)
                    primary = other;
            }

            return ReferenceEquals(primary, this);
        }

        private static int CompareManagers(PredictionManager a, PredictionManager b)
        {
            var sceneA = a.gameObject.scene;
            var sceneB = b.gameObject.scene;

            int byPath = string.CompareOrdinal(sceneA.path, sceneB.path);
            if (byPath != 0)
                return byPath;

            int byName = string.CompareOrdinal(sceneA.name, sceneB.name);
            if (byName != 0)
                return byName;

            return 0;
        }

        private static bool HasPredictionManager(Scene scene)
        {
            var roots = scene.GetRootGameObjects();

            for (var i = 0; i < roots.Length; i++)
            {
                if (!roots[i])
                    continue;

                if (roots[i].GetComponentInChildren<PredictionManager>(true))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="scene"/> belongs to this manager. Identities in managed
        /// scenes are expected to be discovered during registration.
        /// </summary>
        private bool IsManagedScene(Scene scene)
        {
            for (var i = 0; i < _managedScenes.Length; i++)
            {
                if (_managedScenes[i].handle == scene.handle)
                    return true;
            }

            return false;
        }

        private void WarnAboutPhysicsSceneMismatch()
        {
#if UNITY_PHYSICS_3D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
            {
                var managerScene = gameObject.scene.GetPhysicsScene();

                for (var i = 1; i < _managedScenes.Length; i++)
                {
                    var scene = _managedScenes[i];
                    if (!scene.IsValid() || !scene.isLoaded)
                        continue;

                    if (scene.GetPhysicsScene() == managerScene)
                        continue;

                    PurrLogger.LogWarning(
                        $"Scene '{scene.name}' has its own local physics scene; predicted 3D physics in it will not advance under PredictionManager '{name}'. " +
                        "Load additional scenes with LocalPhysicsMode.None so they share the manager's physics scene.",
                        this);
                }
            }
#endif
#if UNITY_PHYSICS_2D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0)
            {
                var managerScene = gameObject.scene.GetPhysicsScene2D();

                for (var i = 1; i < _managedScenes.Length; i++)
                {
                    var scene = _managedScenes[i];
                    if (!scene.IsValid() || !scene.isLoaded)
                        continue;

                    if (scene.GetPhysicsScene2D() == managerScene)
                        continue;

                    PurrLogger.LogWarning(
                        $"Scene '{scene.name}' has its own local physics scene; predicted 2D physics in it will not advance under PredictionManager '{name}'. " +
                        "Load additional scenes with LocalPhysicsMode.None so they share the manager's physics scene.",
                        this);
                }
            }
#endif
        }

        private static void WarnAboutDuplicateSceneOrder(List<Scene> results)
        {
            for (var i = 2; i < results.Count; i++)
            {
                var a = results[i - 1];
                var b = results[i];

                if (a.path != b.path || a.name != b.name)
                    continue;

                PurrLogger.LogWarning(
                    $"PredictionManager registered two loaded scenes named '{b.name}' ('{b.path}'); instance ids for their scene objects may differ between peers. " +
                    "Close duplicate copies of a test scene before entering play.");
                return;
            }
        }

        private void HookSceneLoads()
        {
            if (_sceneLoadHookSubscribed)
                return;

            _sceneLoadHookSubscribed = true;
            SceneManager.sceneLoaded += OnSceneLoadedAfterSpawn;
        }

        private void UnhookSceneLoads()
        {
            if (!_sceneLoadHookSubscribed)
                return;

            _sceneLoadHookSubscribed = false;
            SceneManager.sceneLoaded -= OnSceneLoadedAfterSpawn;
        }

        private void OnSceneLoadedAfterSpawn(Scene scene, LoadSceneMode mode)
        {
            if (!_registerOtherScenes || !isSpawned)
                return;

            if (scene.handle == gameObject.scene.handle || IsManagedScene(scene) || HasPredictionManager(scene))
                return;

            if (!SceneHasPredictedIdentities(scene))
                return;

            PurrLogger.LogWarning(
                $"PredictedIdentity scene objects in scene '{scene.name}' will not simulate: other scenes are only registered while the PredictionManager spawns. " +
                "Load test scenes before entering play or connecting, or create these objects with PredictionManager.hierarchy.Create.",
                this);
        }

        private static bool SceneHasPredictedIdentities(Scene scene)
        {
            var roots = scene.GetRootGameObjects();

            for (var i = 0; i < roots.Length; i++)
            {
                if (!roots[i])
                    continue;

                if (roots[i].GetComponentInChildren<PredictedIdentity>(true))
                    return true;
            }

            return false;
        }

        private sealed class SceneOrderComparer : IComparer<Scene>
        {
            public static readonly SceneOrderComparer instance = new();

            public int Compare(Scene a, Scene b)
            {
                int byPath = string.CompareOrdinal(a.path, b.path);
                if (byPath != 0)
                    return byPath;

                int byName = string.CompareOrdinal(a.name, b.name);
                if (byName != 0)
                    return byName;

                return a.buildIndex.CompareTo(b.buildIndex);
            }
        }
    }
}
