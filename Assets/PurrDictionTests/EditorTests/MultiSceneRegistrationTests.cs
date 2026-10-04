using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet.Modules;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// Covers the opt-in multi-scene registration: a PredictionManager can claim PredictedIdentity
    /// scene objects from other loaded scenes (that have no manager of their own) so test scenes
    /// can hold predicted content without their own simulation world.
    /// </summary>
    public sealed class MultiSceneRegistrationTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string ManagerScenePath = "Assets/__MultiSceneRegistrationManager.unity";

        private readonly List<Object> _cleanup = new();
        private readonly List<Scene> _additionalScenes = new();
        private readonly List<string> _temporaryScenes = new();
        private Scene _managerScene;

        [SetUp]
        public void SetUp()
        {
            PurrNet.Utils.Hasher.PrepareType<PredictedHierarchyState>();
            PurrNet.Utils.Hasher.PrepareType<InstanceDetails>();
            PurrNet.Utils.Hasher.PrepareType<PredictedObjectID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedComponentID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedGameObjectState>();
            ClearSceneIdentityCache();

            // EditMode tests run in an untitled scene, and Unity refuses to create additive
            // scenes while the active scene is untitled. Start from a saved, empty scene.
            _managerScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SaveSceneAsset(_managerScene, ManagerScenePath);
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = _additionalScenes.Count - 1; i >= 0; i--)
            {
                var scene = _additionalScenes[i];
                if (scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
            _additionalScenes.Clear();

            for (var i = _cleanup.Count - 1; i >= 0; i--)
            {
                if (_cleanup[i])
                    Object.DestroyImmediate(_cleanup[i]);
            }
            _cleanup.Clear();

            if (_managerScene.IsValid() && _managerScene.isLoaded)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                _managerScene = default;
            }

            for (var i = _temporaryScenes.Count - 1; i >= 0; i--)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(_temporaryScenes[i]))
                    AssetDatabase.DeleteAsset(_temporaryScenes[i]);
            }
            _temporaryScenes.Clear();

            ClearSceneIdentityCache();
        }

        [Test]
        public void OtherSceneIdentityRegistersWhenEnabled()
        {
            var manager = CreateManager(registerOtherScenes: true);
            var scene = CreateAdditionalScene();
            var root = CreateRoot(scene, "CrossSceneIdentity");
            var identity = root.AddComponent<PredictedGameObject>();

            var hierarchy = Register(manager);

            Assert.That(identity.predictionManager, Is.SameAs(manager));
            Assert.That(hierarchy.TryGetId(root, out _), Is.True);
            CollectionAssert.Contains(manager.managedScenes, scene);
        }

        [Test]
        public void OtherSceneIdentitySkippedWhenDisabled()
        {
            var manager = CreateManager(registerOtherScenes: false);
            var scene = CreateAdditionalScene();
            var root = CreateRoot(scene, "CrossSceneIdentity");
            var identity = root.AddComponent<PredictedGameObject>();

            var hierarchy = Register(manager);

            Assert.That(identity.predictionManager, Is.Null);
            Assert.That(hierarchy.TryGetId(root, out _), Is.False);
            CollectionAssert.DoesNotContain(manager.managedScenes, scene);
        }

        [Test]
        public void SceneWithItsOwnManagerIsSkipped()
        {
            var manager = CreateManager(registerOtherScenes: true);
            var scene = CreateAdditionalScene();
            var root = CreateRoot(scene, "CrossSceneIdentity");
            var identity = root.AddComponent<PredictedGameObject>();

            var otherManager = CreateRoot(scene, "OtherPredictionManager");
            otherManager.AddComponent<PredictionManager>();

            var hierarchy = Register(manager);

            Assert.That(identity.predictionManager, Is.Null);
            Assert.That(hierarchy.TryGetId(root, out _), Is.False);
            CollectionAssert.DoesNotContain(manager.managedScenes, scene);
        }

        [Test]
        public void AdditionalScenesRegisterInPathOrder()
        {
            var manager = CreateManager(registerOtherScenes: true);

            var sceneB = CreateAdditionalScene();
            SaveSceneAsset(sceneB, "Assets/__MultiSceneRegistrationB.unity");
            var rootB = CreateRoot(sceneB, "IdentityB");
            rootB.AddComponent<PredictedGameObject>();

            var sceneA = CreateAdditionalScene();
            SaveSceneAsset(sceneA, "Assets/__MultiSceneRegistrationA.unity");
            var rootA = CreateRoot(sceneA, "IdentityA");
            rootA.AddComponent<PredictedGameObject>();

            var hierarchy = Register(manager);

            Assert.That(hierarchy.TryGetId(rootA, out var idA), Is.True);
            Assert.That(hierarchy.TryGetId(rootB, out var idB), Is.True);
            Assert.That(idA.instanceId.value, Is.LessThan(idB.instanceId.value),
                "scene objects must be reserved in scene path order regardless of creation order");
        }

        [Test]
        public void LateLoadedSceneWithPredictedIdentitiesWarns()
        {
            var manager = CreateManager(registerOtherScenes: true);
            Register(manager);
            Invoke(manager, "HookSceneLoads");

            var scene = CreateAdditionalScene();
            var root = CreateRoot(scene, "LateIdentity");
            root.AddComponent<PredictedGameObject>();

            LogAssert.Expect(LogType.Warning, new Regex("will not simulate"));
            Invoke(manager, "OnSceneLoadedAfterSpawn", scene, LoadSceneMode.Additive);

            Invoke(manager, "UnhookSceneLoads");
        }

        private PredictionManager CreateManager(bool registerOtherScenes)
        {
            var networkObject = Track(new GameObject("MultiScene NetworkManager"));
            var networkManager = networkObject.AddComponent<NetworkManager>();
            var rules = ScriptableObject.CreateInstance<NetworkRules>();
            _cleanup.Add(rules);
            SetField(typeof(NetworkManager), networkManager, "_networkRules", rules);

            var tickManager = new TickManager(20, networkManager, null, false);
            SetField(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);

            var managerObject = Track(new GameObject("MultiScene PredictionManager"));
            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", networkManager);
            SetField(typeof(PredictionManager), manager, "_registerOtherScenes", registerOtherScenes);
            manager.SetIsSpawned(true, false);

            // Edit-mode tests do not run Awake, which is what registers the manager by scene handle.
            Invoke(manager, "Awake");
            return manager;
        }

        private PredictedHierarchy Register(PredictionManager manager)
        {
            // Registering a PredictedHierarchy in edit mode emits a known packer-writer error
            // (the same one PredictedHierarchyPieceTests ignores for its passing cases).
            var previousIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;

            try
            {
                var hierarchy = manager.RegisterSystem<PredictedHierarchy>();
                SetField(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", hierarchy);

                Invoke(manager, "RegisterScenes");

                var queue = GetField<List<PredictedIdentity>>(manager, "_queue");
                var roots = new HashSet<GameObject>();
                var pid = -1;

                for (var i = 0; i < queue.Count; i++)
                {
                    var root = queue[i].GetRoot();
                    if (roots.Add(root))
                        hierarchy.ReserveSceneObject(root, pid--);
                }

                hierarchy.RegisterReservedSceneObjects();
                queue.Clear();
                return hierarchy;
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnoreFailingMessages;
            }
        }

        private Scene CreateAdditionalScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            EditorSceneManager.SetActiveScene(_managerScene);
            _additionalScenes.Add(scene);
            return scene;
        }

        private void SaveSceneAsset(Scene scene, string path)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path))
                AssetDatabase.DeleteAsset(path);

            EditorSceneManager.SaveScene(scene, path);
            _temporaryScenes.Add(path);
        }

        private GameObject CreateRoot(Scene scene, string name)
        {
            var go = Track(new GameObject(name));
            SceneManager.MoveGameObjectToScene(go, scene);
            return go;
        }

        private GameObject Track(GameObject go)
        {
            _cleanup.Add(go);
            return go;
        }

        private static void ClearSceneIdentityCache()
        {
            var cache = typeof(PurrNet.Prediction.SceneObjectsModule).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(cache, Is.Not.Null, "missing SceneObjectsModule._cache");
            ((Dictionary<Scene, List<PredictedIdentity>>)cache.GetValue(null)).Clear();
        }

        private static void Invoke(PredictionManager manager, string name, params object[] arguments)
        {
            var method = typeof(PredictionManager).GetMethod(name, InstanceFields);
            Assert.That(method, Is.Not.Null, $"missing method {name}");
            method.Invoke(manager, arguments);
        }

        private static void SetField(System.Type type, object target, string name, object value)
        {
            var field = type.GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"missing field {type.FullName}.{name}");
            field.SetValue(target, value);
        }

        private static T GetField<T>(PredictionManager manager, string name)
        {
            var field = typeof(PredictionManager).GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"missing field {name}");
            return (T)field.GetValue(manager);
        }
    }
}
