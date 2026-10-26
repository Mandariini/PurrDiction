using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Local, non-networked 2D rigidbodies under a PurrDiction world: they freeze while a rollback
    /// replays, and bodies with interpolation enabled on their Rigidbody2D glide between prediction
    /// ticks at the same presented tick as predicted objects.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public class LocalPhysics2D : MonoBehaviour
    {
#if UNITY_PHYSICS_2D
        private struct Pose : System.IDisposable
        {
            public Vector2 position;
            public float rotation;

            public Pose(Rigidbody2D rb)
            {
                position = rb.position;
                rotation = rb.rotation;
            }

            public void Dispose() { }

            public static Pose Lerp(Pose from, Pose to, float t) => new()
            {
                position = Vector2.LerpUnclamped(from.position, to.position, t),
                rotation = from.rotation + Mathf.DeltaAngle(from.rotation, to.rotation) * t
            };
        }

        private const int ViewCapacity = 16;

        private PredictionManager _manager;
        private Rigidbody2D[] _rigidbodies;
        private UnityRigidbody2DState[] _state;
        private PredictedViewBuffer<Pose>[] _views;
        private bool[] _presented;
        private bool _anyPresented;

        private void Awake()
        {
            _rigidbodies = GetComponentsInChildren<Rigidbody2D>();
            _state = new UnityRigidbody2DState[_rigidbodies.Length];
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
                if (rb.interpolation == RigidbodyInterpolation2D.None)
                    continue;

                rb.interpolation = RigidbodyInterpolation2D.None;
                _views ??= new PredictedViewBuffer<Pose>[_rigidbodies.Length];
                _presented ??= new bool[_rigidbodies.Length];
                _views[i] = new PredictedViewBuffer<Pose>(Pose.Lerp, _manager.localTick, new Pose(rb), ViewCapacity);
            }
        }

        private void OnStartingToRollback()
        {
            RestoreSimulatedPoses();

            for(int i = 0; i < _rigidbodies.Length; i++)
            {
                var rb = _rigidbodies[i];

                _state[i] = new UnityRigidbody2DState(rb);

                rb.bodyType = RigidbodyType2D.Kinematic;

                // Switching to Kinematic preserves 2D velocities, unlike 3D rigidbodies.
#if UNITY_6000
                rb.linearVelocity = default;
#else
                rb.velocity = default;
#endif
                rb.angularVelocity = default;
            }
        }

        private void OnRollbackFinished()
        {
            for (int i = 0; i < _rigidbodies.Length; i++)
            {
                var state = _state[i];
                var rb = _rigidbodies[i];
                rb.bodyType = (RigidbodyType2D) state.bodyType;
                // Kinematic 2D bodies still need their velocities restored.
                if (rb.bodyType == RigidbodyType2D.Static)
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
                    SetTransformPose(rb.transform, rb.position, rb.rotation);
            }
        }

        private void LateUpdate()
        {
            if (_views == null || !_manager || !_manager.isClient)
                return;

            var viewTick = _manager.viewTick;
            for (int i = 0; i < _views.Length; i++)
            {
                var view = _views[i];
                var rb = _rigidbodies[i];
                if (view == null || !rb || rb.bodyType != RigidbodyType2D.Dynamic || !rb.IsAwake())
                    continue;

                var trs = rb.transform;
                Vector2 position = trs.position;
                float rotation = trs.eulerAngles.z;

                // Moved by hand since the last restore: start the view over from there.
                if (position != rb.position || Mathf.Abs(Mathf.DeltaAngle(rotation, rb.rotation)) > 0.01f)
                {
                    view.Teleport(_manager.localTick, new Pose { position = position, rotation = rotation });
                    continue;
                }

                var pose = view.Sample(viewTick);
                SetTransformPose(trs, pose.position, pose.rotation);
                _presented[i] = true;
                _anyPresented = true;
            }
        }

        private static void SetTransformPose(Transform trs, Vector2 position, float rotation)
        {
            var euler = trs.eulerAngles;
            trs.SetPositionAndRotation(
                new Vector3(position.x, position.y, trs.position.z),
                Quaternion.Euler(euler.x, euler.y, rotation));
        }
#endif
    }
}
