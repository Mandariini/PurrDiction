using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

public static class PieceReconnectSignals
{
    public static ulong victimId;
    public static bool victimReceived;
    public static bool victimRejoined;
    public static bool cycleComplete;

    [ObserversRpc(runLocally: true)]
    public static void BroadcastVictim(ulong playerId)
    {
        victimId = playerId;
        victimReceived = true;
    }

    [ServerRpc(requireOwnership: false)]
    public static void ReportVictimRejoined()
    {
        victimRejoined = true;
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastCycleComplete()
    {
        cycleComplete = true;
    }
}

public class PieceReconnectScenario : Scenario
{
    private const int DigestChannel = 1400;
    private const float Timeout = 90f;
    private const float ReconnectTimeout = 30f;
    private const float StayDisconnectedSeconds = 1f;
    private const float SettleSeconds = 3f;

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var choreography = ctx.isServer
            ? await RunAsServer(ctx)
            : await RunAsClient(ctx);

        if (!choreography.success)
            return choreography;

        return await FinishAndCompare(ctx);
    }

    private async UniTask<ScenarioResult> RunAsServer(ScenarioContext ctx)
    {
        var victim = PickVictim(ctx);
        if (!victim.HasValue)
            return ScenarioResult.Fail("no eligible client to disconnect");

        var victimId = victim.Value.id.value;
        PieceReconnectSignals.victimRejoined = false;
        PieceReconnectSignals.BroadcastVictim(victimId);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => PieceReconnectSignals.victimRejoined,
                ReconnectTimeout + StayDisconnectedSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"victim {victimId} never reported back after the disconnect/reconnect cycle");
        }

        PieceReconnectSignals.BroadcastCycleComplete();
        return ScenarioResult.Ok();
    }

    private async UniTask<ScenarioResult> RunAsClient(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => PieceReconnectSignals.victimReceived,
                ReconnectTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("victim broadcast never arrived");
        }

        var manager = ctx.networkManager;
        bool isVictim = ctx.role == NetworkRole.Client
                        && manager.isLocalPlayerReady
                        && manager.localPlayer.id.value == PieceReconnectSignals.victimId;

        if (isVictim)
        {
            manager.StopClient();

            await UniTaskUtils.WaitWithTimeout(
                () => manager.clientState == ConnectionState.Disconnected,
                ReconnectTimeout,
                ctx.cancellationToken);

            await UniTask.WaitForSeconds(StayDisconnectedSeconds, cancellationToken: ctx.cancellationToken);

            manager.StartClient();

            await UniTaskUtils.WaitWithTimeout(
                () => manager.isClient && manager.isLocalPlayerReady,
                ReconnectTimeout,
                ctx.cancellationToken);

            var pm = ctx.predictionManager;
            await UniTaskUtils.WaitWithTimeout(
                () => pm && pm.isSpawned,
                ReconnectTimeout,
                ctx.cancellationToken);

            var startTick = pm.localTick;
            await UniTaskUtils.WaitWithTimeout(
                () => pm.localTick > startTick + 5,
                ReconnectTimeout,
                ctx.cancellationToken);

            PieceReconnectSignals.ReportVictimRejoined();
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => PieceReconnectSignals.cycleComplete,
                ReconnectTimeout + StayDisconnectedSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("cycle-complete broadcast never arrived");
        }

        return ScenarioResult.Ok();
    }

    private async UniTask<ScenarioResult> FinishAndCompare(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(PawnIdentity.pawnPrefab, out var pawnPrefabId);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => PieceLifecycleScenario.EndShapeReached(pm) && PieceLifecycleScenario.ProbesStable(pm),
                Timeout,
                ctx.cancellationToken);

            await UniTaskUtils.WaitWithTimeout(
                () => PredictionTestUtils.CountInstances(pm, pawnPrefabId) >= ctx.expectedConnections,
                Timeout,
                ctx.cancellationToken);

            await UniTaskUtils.WaitWithTimeout(
                () => PawnIdentity.AllStable(pm, pawnPrefabId),
                Timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"decomposed shape never re-stabilized: {PieceLifecycleScenario.DescribeShape(pm)} " +
                $"pawns={PredictionTestUtils.CountInstances(pm, pawnPrefabId)}/{ctx.expectedConnections} pawnsStable={PawnIdentity.AllStable(pm, pawnPrefabId)}");
        }

        await UniTask.WaitForSeconds(SettleSeconds, cancellationToken: ctx.cancellationToken);

        var refFailure = PieceLifecycleScenario.CheckSerializedRefs(pm);
        if (refFailure != null)
            return ScenarioResult.Fail($"after rebuild: {refFailure}");

        return await DigestExchange.Compare(ctx, DigestChannel, PieceLifecycleScenario.BuildDigest(ctx), 30f);
    }

    private static PlayerID? PickVictim(ScenarioContext ctx)
    {
        var manager = ctx.networkManager;
        var hostLocal = manager.isLocalPlayerReady && ctx.role == NetworkRole.Host
            ? manager.localPlayer
            : (PlayerID?)null;

        PlayerID? best = null;
        var players = manager.players;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p.isServer) continue;
            if (hostLocal.HasValue && hostLocal.Value == p) continue;
            if (!best.HasValue || p.id.value < best.Value.id.value)
                best = p;
        }
        return best;
    }
}
