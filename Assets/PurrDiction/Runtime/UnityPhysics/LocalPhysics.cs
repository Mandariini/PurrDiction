using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Local, non-networked rigidbodies (debris, ragdolls) under a PurrDiction world: they freeze
    /// while a rollback replays, and bodies with interpolation enabled on their Rigidbody glide
    /// between prediction ticks at the same presented tick as predicted objects.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public class LocalPhysics : MonoBehaviour
    {
#if UNITY_PHYSICS_3D
        private struct Pose : System.IDisposable
        {
            public Vector3 position;
            public Quaternion rotation;

            public Pose(Rigidbody rb)
            {
                position = rb.position;
                rotation = rb.rotation;
            }

            public void Dispose() { }

            public static Pose Lerp(Pose from, Pose to, float t) => new()
            {
                position = Vector3.LerpUnclamped(from.position, to.position, t),
                rotation = Quaternion.SlerpUnclamped(from.rotation, to.rotation, t)
            };
        }

        private const int ViewCapacity = 16;

        private PredictionManager _manager;
        private Rigidbody[] _rigidbodies;
        private UnityRigidbodyState[] _state;
        private PredictedViewBuffer<Pose>[] _views;
        private bool[] _presented;
        private Pose[] _sampled;
        private bool[] _toPresent;
        private bool _anyPresented;

        private void Awake()
        {
            _rigidbodies = GetComponentsInChildren<Rigidbody>();
            _state = new UnityRigidbodyState[_rigidbodies.Length];
        }

        private void Start()
        {
            if (PredictionManager.TryGetInstance(gameObject.scene.handle, out var manager))
            {
                _manager = manager;
                _manager.onStartingToRollback += OnStartingToRollback;
                _manager.onRollbackFinished += OnRollbackFinished;
                _manager.onBeforePhysicsPass += RestoreSimulatedPoses;
                _manager.onAfterPhysicsPass += OnAfterPhysicsPass;
                TakeOverInterpolation();
            }
        }

        private void OnDestroy()
        {
            if (!_manager)
                return;

            _manager.onStartingToRollback -= OnStartingToRollback;
            _manager.onRollbackFinished -= OnRollbackFinished;
            _manager.onBeforePhysicsPass -= RestoreSimulatedPoses;
            _manager.onAfterPhysicsPass -= OnAfterPhysicsPass;
        }

        private void OnDisable() => RestoreSimulatedPoses();

        // Unity's own interpolation restarts whenever a body turns kinematic, which happens on every
        // rollback, so interpolated bodies are presented from the poses of the prediction ticks instead.
        private void TakeOverInterpolation()
        {
            for (int i = 0; i < _rigidbodies.Length; i++)
            {
                var rb = _rigidbodies[i];
                if (rb.interpolation == RigidbodyInterpolation.None)
                    continue;

                rb.interpolation = RigidbodyInterpolation.None;
                _views ??= new PredictedViewBuffer<Pose>[_rigidbodies.Length];
                _presented ??= new bool[_rigidbodies.Length];
                _views[i] = new PredictedViewBuffer<Pose>(Pose.Lerp, _manager.localTick, new Pose(rb), ViewCapacity);
            }
        }

        public void CacheCurrentState(bool overrideVelocities = false)
        {
            for (int i = 0; i < _rigidbodies.Length; i++)
            {
                if (overrideVelocities)
                     _state[i] = new UnityRigidbodyState(_rigidbodies[i]);
                else _state[i].isKinematic = _rigidbodies[i].isKinematic;
            }
        }

        private void OnStartingToRollback()
        {
            RestoreSimulatedPoses();

            for (int i = 0; i < _rigidbodies.Length; i++)
            {
                _state[i] = new UnityRigidbodyState(_rigidbodies[i]);
                _rigidbodies[i].isKinematic = true;
            }
        }

        private void OnRollbackFinished()
        {
            for (int i = 0; i < _rigidbodies.Length; i++)
            {
                var state = _state[i];
                var rb = _rigidbodies[i];
                rb.isKinematic = state.isKinematic;
                if (state.isKinematic)
                    continue;

#if UNITY_6000
                rb.linearVelocity = state.linearVelocity;
#else
                rb.velocity = state.linearVelocity;
#endif
                rb.angularVelocity = state.angularVelocity;
                if (state.isSleeping)
                    rb.Sleep();
                else rb.WakeUp();
            }
        }

        private void OnAfterPhysicsPass()
        {
            if (_views == null || _manager.isReplaying)
                return;

            // Predicted views stamp the state after simulating tick T as T + 1, once the tick completes.
            var tick = _manager.localTick + 1;
            for (int i = 0; i < _views.Length; i++)
            {
                if (_views[i] != null && _rigidbodies[i])
                    _views[i].Add(tick, new Pose(_rigidbodies[i]));
            }
        }

        // Runs before anything else in the frame, so ticks, rollbacks and SyncTransforms only ever
        // see the simulated poses; a presented pose synced into physics would teleport the body back.
        private void Update() => RestoreSimulatedPoses();

        private void RestoreSimulatedPoses()
        {
            if (!_anyPresented)
                return;

            _anyPresented = false;
            for (int i = 0; i < _presented.Length; i++)
            {
                if (!_presented[i])
                    continue;

                _presented[i] = false;
                var rb = _rigidbodies[i];
                if (rb)
                    rb.transform.SetPositionAndRotation(rb.position, rb.rotation);
            }
        }

        private void LateUpdate()
        {
            if (_views == null || !_manager || !_manager.isClient)
                return;

            var viewTick = _manager.viewTick;
            if (_presented == null || _presented.Length != _views.Length)
                _presented = new bool[_views.Length];
            if (_sampled == null || _sampled.Length != _views.Length)
                _sampled = new Pose[_views.Length];
            if (_toPresent == null || _toPresent.Length != _views.Length)
                _toPresent = new bool[_views.Length];

            // Check and sample every body before moving any transform: 
            // moving a parent transform also moves its child transforms
            for (int i = 0; i < _views.Length; i++)
            {
                _toPresent[i] = false;
                var view = _views[i];
                var rb = _rigidbodies[i];
                if (view == null || !rb || rb.isKinematic || rb.IsSleeping())
                    continue;

                var trs = rb.transform;
                trs.GetPositionAndRotation(out var position, out var rotation);

                // Moved by hand since the last restore: start the view over from there.
                if (position != rb.position || rotation != rb.rotation)
                {
                    view.Teleport(_manager.localTick, new Pose { position = position, rotation = rotation });
                    continue;
                }

                _sampled[i] = view.Sample(viewTick);
                _toPresent[i] = true;
            }

            for (int i = 0; i < _views.Length; i++)
            {
                if (!_toPresent[i])
                    continue;

                _rigidbodies[i].transform.SetPositionAndRotation(_sampled[i].position, _sampled[i].rotation);
                _presented[i] = true;
                _anyPresented = true;
            }
        }
#endif
    }
}
