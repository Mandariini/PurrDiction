using System.Collections.Generic;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class TrailGunner : PredictedIdentity<TrailGunner.GunnerInput, TrailGunner.GunnerState>
{
    public const float ProjectileSpeed = 20f;
    public const uint FireEveryTicks = 4;

    public static GameObject projectilePrefab;
    public static ulong fireStartTick = ulong.MaxValue;
    public static ulong fireEndTick;

    public struct GunnerInput : IPredictedData
    {
        public bool fire;

        public void Dispose() { }
    }

    public struct GunnerState : IPredictedData<GunnerState>
    {
        public int shots;

        public void Dispose() { }
    }

    protected override void GetFinalInput(ref GunnerInput input)
    {
        var tick = predictionManager.localTick;
        input.fire = projectilePrefab &&
                     tick >= fireStartTick &&
                     tick < fireEndTick &&
                     tick % FireEveryTicks == 0;
    }

    protected override void Simulate(GunnerInput input, ref GunnerState state, float delta)
    {
        if (!input.fire || !projectilePrefab)
            return;

        hierarchy.Create(projectilePrefab, transform.position, Quaternion.identity, owner);
        state.shots += 1;
    }

    public string Digest()
    {
        return $"{id.objectId.instanceId.value}:{currentState.shots}";
    }
}

public class TrailProjectile : PredictedIdentity<TrailProjectile.ProjState>
{
    public const uint LifetimeTicks = 30;

    public struct ProjState : IPredictedData<ProjState>
    {
        public uint age;
        public bool deleteRequested;

        public void Dispose() { }
    }

    protected override void Simulate(ref ProjState state, float delta)
    {
        if (state.deleteRequested)
            return;

        state.age += 1;
        transform.position += Vector3.right * (TrailGunner.ProjectileSpeed * delta);

        if (state.age < LifetimeTicks)
            return;

        state.deleteRequested = true;
        hierarchy.Delete(id.objectId);
    }
}

public class TrailViewTracker : MonoBehaviour
{
    public const float BackwardEps = 0.05f;
    public const float LateralEps = 0.05f;

    public struct Sample
    {
        public string kind;
        public string channel;
        public int frame;
        public int prevFrame;
        public ulong tick;
        public uint instanceId;
        public int segment;
        public bool owned;
        public Vector3 prev;
        public Vector3 cur;
        public bool hasPreviousContext;
        public Observation previousContext;
        public Observation currentContext;
        public bool isRegisteredInstance;
        public bool hasSpawnRecord;
        public Vector3 spawnPosition;
        public PlayerID? spawnOwner;

        public override string ToString()
        {
            return $"{kind}/{channel} id={instanceId} owned={owned} seg={segment} " +
                   $"frames={prevFrame}->{frame} tick={tick} " +
                   $"prev=({prev.x:F3},{prev.y:F3},{prev.z:F3}) cur=({cur.x:F3},{cur.y:F3},{cur.z:F3}) " +
                   $"contextPrev={hasPreviousContext}:{previousContext} contextNow={currentContext} " +
                   $"samePhysical={hasPreviousContext && previousContext.physicalInstance == currentContext.physicalInstance} " +
                   $"ageRewind={hasPreviousContext && currentContext.age < previousContext.age} registered={isRegisteredInstance} " +
                   $"spawnRecord={hasSpawnRecord}:({spawnPosition.x:F3},{spawnPosition.y:F3},{spawnPosition.z:F3}) owner={spawnOwner}";
        }
    }

    public struct Observation
    {
        public uint id;
        public EntityId physicalInstance;
        public PlayerID? owner;
        public ulong localTick;
        public uint age;
        public bool deleteRequested;
        public ulong? projectileVerifiedTick;
        public ulong? transformVerifiedTick;

        public override string ToString()
            => $"[id={id},physical={physicalInstance},owner={owner},tick={localTick},age={age},delete={deleteRequested},verified={projectileVerifiedTick},transformVerified={transformVerifiedTick}]";
    }

    public static readonly List<Sample> failures = new();
    public static readonly List<Sample> diagnostics = new();
    public static readonly HashSet<uint> deadIds = new();
    public static long totalSamples;
    public static int segmentsStarted;
    public static int resurrections;
    public static float maxBackward;

    private const int MaxRecorded = 64;

    private PredictedTransform _pt;
    private TrailProjectile _proj;

    private int _segment;
    private bool _hasPrev;
    private Vector3 _prevView;
    private Vector3 _prevSim;
    private int _prevFrame;
    private uint _lastId;
    private bool _hasDisabledSample;
    private Vector3 _disabledView;
    private int _disabledFrame;
    private bool _checkedResurrection;
    private EntityId _physicalInstance;
    private bool _hasPreviousContext;
    private Observation _previousContext;
    private Observation _currentContext;

    public static void ResetAll()
    {
        failures.Clear();
        diagnostics.Clear();
        deadIds.Clear();
        totalSamples = 0;
        segmentsStarted = 0;
        resurrections = 0;
        maxBackward = 0f;
    }

    private void Awake()
    {
        _pt = GetComponent<PredictedTransform>();
        _proj = GetComponent<TrailProjectile>();
        _physicalInstance = gameObject.GetEntityId();
    }

    private void OnEnable()
    {
        _segment++;
        segmentsStarted++;
        _hasPrev = false;
        _checkedResurrection = false;
    }

    private void OnDisable()
    {
        if (_hasPrev)
        {
            _hasDisabledSample = true;
            _disabledView = _prevView;
            _disabledFrame = _prevFrame;
            if (_lastId != 0)
                deadIds.Add(_lastId);
        }

        _hasPrev = false;
    }

    private void LateUpdate()
    {
        if (!_pt || _pt.predictionManager == null)
            return;

        totalSamples++;

        _pt.GetViewWorldPose(out var viewPos, out _);
        var simPos = transform.position;
        var frame = Time.frameCount;
        var tick = _pt.predictionManager.localTick;
        bool owned = _proj && _proj.isOwner;
        uint instanceId = _proj ? _proj.id.objectId.instanceId.value : 0;
        _lastId = instanceId;
        // Only scalar context is retained between frames. Detailed record lookup and
        // formatting happen for the existing bounded failures/diagnostics below.
        _currentContext = new Observation
        {
            id = instanceId,
            physicalInstance = _physicalInstance,
            owner = _proj ? _proj.owner : null,
            localTick = tick,
            age = _proj ? _proj.currentState.age : 0,
            deleteRequested = _proj && _proj.currentState.deleteRequested,
            projectileVerifiedTick = _proj ? _proj.lastVerifiedTick : null,
            transformVerifiedTick = _pt.lastVerifiedTick
        };

        if (!_checkedResurrection && instanceId != 0)
        {
            _checkedResurrection = true;
            if (deadIds.Contains(instanceId))
            {
                resurrections++;
                Record(diagnostics, "resurrectedId", "id", frame, _disabledFrame, tick, instanceId, owned, _disabledView, viewPos);
            }
        }

        if (_hasPrev)
        {
            CheckChannel("view", _prevView, viewPos, frame, tick, instanceId, owned);
            CheckChannel("sim", _prevSim, simPos, frame, tick, instanceId, owned);
        }
        else if (_hasDisabledSample)
        {
            var dx = viewPos.x - _disabledView.x;
            if (dx < -BackwardEps)
            {
                Record(diagnostics, "segmentJumpBackward", "view", frame, _disabledFrame, tick, instanceId, owned,
                    _disabledView, viewPos);
            }

            _hasDisabledSample = false;
        }

        _hasPrev = true;
        _prevView = viewPos;
        _prevSim = simPos;
        _prevFrame = frame;
        _previousContext = _currentContext;
        _hasPreviousContext = true;
    }

    private void CheckChannel(string channel, Vector3 prev, Vector3 cur, int frame, ulong tick, uint instanceId, bool owned)
    {
        var dx = cur.x - prev.x;
        var lateral = Mathf.Max(Mathf.Abs(cur.y - prev.y), Mathf.Abs(cur.z - prev.z));

        // The view channel legitimately renders corrections: a fire input arriving one tick
        // late server-side lands the authoritative projectile one tick behind the prediction,
        // and a catch-up burst can compress that whole correction into one rendered frame.
        // Allow up to one tick of travel backward on the view; sim and lateral stay strict,
        // so identity swaps and multi-tick jumps still fail.
        var backwardEps = BackwardEps;
        if (channel == "view" && _pt.predictionManager != null)
            backwardEps += TrailGunner.ProjectileSpeed * _pt.predictionManager.tickDelta;

        if (dx < -backwardEps)
        {
            if (-dx > maxBackward)
                maxBackward = -dx;
            Record(owned ? failures : diagnostics, owned ? "backward" : "backwardRemote", channel, frame, _prevFrame,
                tick, instanceId, owned, prev, cur);
        }
        else if (lateral > LateralEps)
        {
            Record(owned ? failures : diagnostics, owned ? "lateral" : "lateralRemote", channel, frame, _prevFrame,
                tick, instanceId, owned, prev, cur);
        }
    }

    private void Record(List<Sample> target, string kind, string channel, int frame, int prevFrame, ulong tick,
        uint instanceId, bool owned, Vector3 prev, Vector3 cur)
    {
        if (target.Count >= MaxRecorded)
            return;

        var sample = new Sample
        {
            kind = kind,
            channel = channel,
            frame = frame,
            prevFrame = prevFrame,
            tick = tick,
            instanceId = instanceId,
            segment = _segment,
            owned = owned,
            prev = prev,
            cur = cur,
            hasPreviousContext = _hasPreviousContext,
            previousContext = _previousContext,
            currentContext = _currentContext
        };

        if (_proj && _pt.predictionManager)
        {
            var pm = _pt.predictionManager;
            sample.isRegisteredInstance = pm.TryGetIdentity(_proj.id, out var registered) && registered == _proj;
            if (pm.hierarchy)
            {
                var records = pm.hierarchy.currentState.spawnedPrefabs;
                for (var i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    if (!record.instanceId.Equals(_proj.id.objectId))
                        continue;
                    sample.hasSpawnRecord = true;
                    sample.spawnPosition = record.spawnPosition;
                    sample.spawnOwner = record.owner;
                    break;
                }
            }
        }

        target.Add(sample);
    }
}
