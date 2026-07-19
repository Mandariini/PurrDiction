using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class TickAgreementShooter : PredictedIdentity<TickAgreementShooter.ShotInput, TickAgreementShooter.ShooterState>
{
    public const int TargetShots = 3;
    public const int ShotSpacing = 40;

    public static GameObject bulletPrefab;

    public struct ShotInput : IPredictedData
    {
        public bool shoot;

        public void Dispose() { }
    }

    public struct ShooterState : IPredictedData<ShooterState>
    {
        public DisposableList<uint> shotTicks;
        public int shots;
        public uint cooldown;

        public void Dispose()
        {
            shotTicks.Dispose();
        }
    }

    private bool _armed;

    public readonly List<uint> firstPredictedTicks = new ();

    public bool isDone => currentState.shots >= TargetShots;

    public void Arm() => _armed = true;

    protected override void GetFinalInput(ref ShotInput input)
    {
        input.shoot = _armed && currentState.shots < TargetShots &&
                      predictionManager.localTick % ShotSpacing == 0;
    }

    protected override void Simulate(ShotInput input, ref ShooterState state, float delta)
    {
        if (state.cooldown > 0)
            state.cooldown--;

        if (!input.shoot || state.cooldown > 0 || state.shots >= TargetShots)
            return;

        uint tick = (uint)predictionManager.time.tick;

        if (state.shotTicks.isDisposed)
            state.shotTicks = DisposableList<uint>.Create(TargetShots);

        state.shotTicks.Add(tick);
        state.shots++;
        state.cooldown = ShotSpacing / 2;

        if (bulletPrefab)
            predictionManager.hierarchy.Create(bulletPrefab, transform.position + new Vector3(state.shots, 0f, 0f), Quaternion.identity);

        if (!predictionManager.isReplaying && !predictionManager.isCatchingUpFrames &&
            firstPredictedTicks.Count < state.shots)
        {
            firstPredictedTicks.Add(tick);
        }
    }

    public string TickDigest()
    {
        var sb = new StringBuilder();
        sb.Append("shots=").Append(currentState.shots).Append(":ticks=");

        if (!currentState.shotTicks.isDisposed)
        {
            for (var i = 0; i < currentState.shotTicks.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(currentState.shotTicks[i]);
            }
        }

        return sb.ToString();
    }
}

public class TickAgreementScenario : Scenario
{
    private const int DigestChannel = 1500;
    private const float Timeout = 120f;
    private const float SettleSeconds = 3f;

    private static GameObject shooterPrefab;
    private static GameObject bulletPrefab;
    private int _shooterPrefabId;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        if (shooterPrefab)
            return;

        bulletPrefab = PredictionTestUtils.CreatePrefab<PredictedMarker>("TickAgreementBullet");
        PredictionTestUtils.RegisterPrefab(ctx, bulletPrefab);
        TickAgreementShooter.bulletPrefab = bulletPrefab;

        shooterPrefab = PredictionTestUtils.CreatePrefab<TickAgreementShooter>("TickAgreementShooter");
        PredictionTestUtils.RegisterPrefab(ctx, shooterPrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(shooterPrefab, out _shooterPrefabId);

        if (ctx.isServer)
        {
            var players = ctx.networkManager.players;

            for (var i = 0; i < players.Count; i++)
            {
                if (players[i].isServer)
                    continue;

                pm.hierarchy.Create(shooterPrefab, new Vector3(200f + i * 5f, 0f, 0f), Quaternion.identity, players[i]);
            }
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => CountShooters(pm) >= ctx.externalClientCount + (ctx.role == NetworkRole.Host ? 1 : 0),
                Timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"shooters never spawned: {CountShooters(pm)}");
        }

        var mine = FindOwnShooter(ctx);
        mine?.Arm();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => AllShootersDone(pm),
                Timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"shooters never finished: {DescribeShooters(pm)}");
        }

        await UniTask.WaitForSeconds(SettleSeconds, cancellationToken: ctx.cancellationToken);

        if (mine != null)
        {
            var verified = mine.currentState.shotTicks;
            int verifiedCount = verified.isDisposed ? 0 : verified.Count;

            if (verifiedCount != TickAgreementShooter.TargetShots ||
                mine.firstPredictedTicks.Count != TickAgreementShooter.TargetShots)
            {
                return ScenarioResult.Fail(
                    $"shot count mismatch: predicted {mine.firstPredictedTicks.Count}, verified {verifiedCount}");
            }

            for (var i = 0; i < verifiedCount; i++)
            {
                if (mine.firstPredictedTicks[i] != verified[i])
                {
                    return ScenarioResult.Fail(
                        $"tick disagreement on shot {i}: owner predicted tick {mine.firstPredictedTicks[i]} " +
                        $"but the verified timeline says {verified[i]} " +
                        $"(predicted [{string.Join(",", mine.firstPredictedTicks)}] vs verified [{VerifiedList(mine)}])");
                }
            }
        }

        var sb = new StringBuilder();
        var counter = UnityEngine.Object.FindFirstObjectByType<DeterministicTickCounter>();
        sb.Append(PredictionTestUtils.WorldDigest(ctx, counter));
        PredictionTestUtils.AppendIdentities<TickAgreementShooter>(pm, _shooterPrefabId, sb, shooter => shooter.TickDigest());
        return await DigestExchange.Compare(ctx, DigestChannel, sb.ToString(), 30f);
    }

    private static string VerifiedList(TickAgreementShooter shooter)
    {
        var verified = shooter.currentState.shotTicks;
        if (verified.isDisposed)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < verified.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(verified[i]);
        }
        return sb.ToString();
    }

    private int CountShooters(PredictionManager pm)
    {
        int count = 0;
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            if (state.spawnedPrefabs[i].prefabId == _shooterPrefabId && state.spawnedPrefabs[i].isRootRecord)
                count++;
        }

        return count;
    }

    private bool AllShootersDone(PredictionManager pm)
    {
        bool any = false;
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != _shooterPrefabId || !record.isRootRecord)
                continue;

            any = true;
            if (!record.instanceId.TryGetComponent<TickAgreementShooter>(pm, out var shooter) || !shooter.isDone)
                return false;
        }

        return any;
    }

    private string DescribeShooters(PredictionManager pm)
    {
        var sb = new StringBuilder();
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != _shooterPrefabId || !record.isRootRecord)
                continue;

            if (record.instanceId.TryGetComponent<TickAgreementShooter>(pm, out var shooter))
                sb.Append('[').Append(shooter.TickDigest()).Append(']');
        }

        return sb.ToString();
    }

    private TickAgreementShooter FindOwnShooter(ScenarioContext ctx)
    {
        var manager = ctx.networkManager;

        if (!manager.isLocalPlayerReady)
            return null;

        var localPlayer = manager.localPlayer;
        var pm = ctx.predictionManager;
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != _shooterPrefabId || !record.isRootRecord)
                continue;

            if (record.owner.HasValue && record.owner.Value == localPlayer &&
                record.instanceId.TryGetComponent<TickAgreementShooter>(pm, out var shooter))
            {
                return shooter;
            }
        }

        return null;
    }
}
