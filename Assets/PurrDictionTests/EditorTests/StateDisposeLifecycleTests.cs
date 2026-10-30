using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// User Dispose() only ever sees states the identity actually held. A state whose Dispose walks its lists
    /// (the common pattern) must survive first setup, where the identity's state is still the default struct.
    /// </summary>
    public sealed class StateDisposeLifecycleTests
    {
        const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        readonly List<GameObject> _cleanup = new ();
        readonly List<Object> _assetCleanup = new ();
        PredictionManager _manager;

        readonly List<string> _errors = new ();

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            PurrNet.Utils.Hasher.PrepareType(typeof(ListOwningState));
            // Copies must own their own list, as generated packers would make them.
            Packer<ListOwningState>.RegisterWriter((packer, state) =>
            {
                Packer<int>.Write(packer, state.items.isDisposed ? -1 : state.items.Count);
                if (!state.items.isDisposed)
                    foreach (var item in state.items)
                        Packer<int>.Write(packer, item);
            });
            Packer<ListOwningState>.RegisterReader((BitPacker packer, ref ListOwningState state) =>
            {
                int count = Packer<int>.Read(packer);
                state.items = count < 0 ? default : DisposableList<int>.Create();
                for (int i = 0; i < count; i++)
                    state.items.Add(Packer<int>.Read(packer));
            });
        }

        [SetUp]
        public void SetUp()
        {
            ListOwningState.disposeCalls = 0;
            _errors.Clear();
            Application.logMessageReceived += CaptureErrors;
        }

        void CaptureErrors(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error)
                _errors.Add(message);
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= CaptureErrors;
            if (_manager)
            {
                var poolParent = (GameObject)typeof(PredictionManager).GetField("_poolParent", InstanceFields)?.GetValue(_manager);
                if (poolParent)
                    Object.DestroyImmediate(poolParent);
                _manager = null;
            }

            foreach (var go in _cleanup)
                if (go)
                    Object.DestroyImmediate(go);
            foreach (var asset in _assetCleanup)
                if (asset)
                    Object.DestroyImmediate(asset);
            _cleanup.Clear();
            _assetCleanup.Clear();
        }

        [Test]
        public void FirstSetupKeepsTheInitialStateAndNeverDisposesTheDefault()
        {
            var probe = Spawn(CreateWorld());

            Assert.That(_errors, Is.Empty, "setup must not hand the default state to Dispose()");
            Assert.That(probe.currentState.items.isDisposed, Is.False, "setup must leave the initial state in place");
            Assert.That(probe.currentState.items.Count, Is.EqualTo(1));
        }

        [Test]
        public void ReleasingTwiceDisposesTheHeldStateOnly()
        {
            var probe = Spawn(CreateWorld());
            var held = probe.currentState.items;

            probe.ResetState();
            Assert.That(held.isDisposed, Is.True, "the held state is disposed on release");
            int disposes = ListOwningState.disposeCalls;

            probe.ResetState();
            Assert.That(ListOwningState.disposeCalls, Is.EqualTo(disposes), "an already released state is not disposed again");
            Assert.That(_errors, Is.Empty);
        }

        [Test]
        public void WritingTheFirstStateNeverDisposesTheDefaultBaseline()
        {
            // The baseline is default unless the identity overrides GetFirstStateBaseline. Disposing it threw from
            // the host's tick for every state whose Dispose walks its lists, so no client could ever join.
            var probe = Spawn(CreateWorld());
            int disposes = ListOwningState.disposeCalls;

            using var packer = BitPackerPool.Get();
            probe.RunWriteFirstState(1, packer);

            Assert.That(_errors, Is.Empty, "writing the first state must not hand the default baseline to Dispose()");
            Assert.That(ListOwningState.disposeCalls, Is.EqualTo(disposes), "the default baseline owns nothing to dispose");
            Assert.That(probe.currentState.items.isDisposed, Is.False, "the live state stays intact");
        }

        ListStateProbe Spawn(PredictedHierarchy hierarchy)
        {
            var id = hierarchy.Create(0, Vector3.zero, Quaternion.identity);
            Assert.That(id.HasValue, Is.True);
            Assert.That(hierarchy.TryGetGameObject(id, out var go), Is.True);
            _cleanup.Add(go);
            return go.GetComponent<ListStateProbe>();
        }

        PredictedHierarchy CreateWorld()
        {
            PurrCopy.Override<PredictedHierarchyState>();
            PurrNet.Utils.Hasher.PrepareType<PredictedHierarchyState>();
            PurrNet.Utils.Hasher.PrepareType<InstanceDetails>();
            PurrNet.Utils.Hasher.PrepareType<PredictedObjectID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedComponentID>();

            var networkObject = Track(new GameObject("NetworkManager"));
            var managerObject = Track(new GameObject("PredictionManager"));
            var networkManager = networkObject.AddComponent<NetworkManager>();

            var tickManager = new TickManager(20, networkManager, null, false);
            SetField(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);
            _manager = managerObject.AddComponent<PredictionManager>();
            SetField(typeof(NetworkIdentity), _manager, "<networkManager>k__BackingField", networkManager);
            SetField(typeof(PredictionManager), _manager, "<tickRate>k__BackingField", 20);
            _manager.SetIsSpawned(true, false);

            var hierarchy = _manager.RegisterSystem<PredictedHierarchy>();
            SetField(typeof(PredictionManager), _manager, "<hierarchy>k__BackingField", hierarchy);

            var prefab = Track(new GameObject("ListStatePrefab"));
            prefab.AddComponent<ListStateProbe>();
            var prefabs = ScriptableObject.CreateInstance<PredictedPrefabs>();
            _assetCleanup.Add(prefabs);
            prefabs.prefabs.Add(new PredictedPrefab { prefab = prefab, pooled = false });
            _manager.predictedPrefabs = prefabs;
            return hierarchy;
        }

        GameObject Track(GameObject go)
        {
            _cleanup.Add(go);
            return go;
        }

        static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }
    }

    public sealed class ListStateProbe : PredictedIdentity<ListOwningState>
    {
        protected override ListOwningState GetInitialState()
        {
            var items = DisposableList<int>.Create();
            items.Add(1);
            return new ListOwningState { items = items };
        }
    }

    public struct ListOwningState : IPredictedData<ListOwningState>
    {
        public static int disposeCalls;
        public DisposableList<int> items;

        public void Dispose()
        {
            disposeCalls++;
            // Walks the list like typical game states do; throws if handed a default (disposed) list.
            foreach (var _ in items) { }
            items.Dispose();
        }
    }
}
