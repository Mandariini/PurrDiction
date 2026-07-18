using System;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class PieceLifecycleScenario : Scenario
{
    private const int DigestChannel = 1300;
    private const float Timeout = 90f;
    private const float SettleSeconds = 3f;

    internal static GameObject rigPrefab;
    internal static int rigPrefabId = int.MinValue;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        if (rigPrefab)
            return;

        rigPrefab = BuildRigTemplate();
        PredictionTestUtils.RegisterPrefab(ctx, rigPrefab);
    }

    private static GameObject BuildRigTemplate()
    {
        var root = new GameObject("PieceRig");
        root.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(root);
        var rig = root.AddComponent<PieceRigRoot>();

        var armA = new GameObject("armA");
        armA.transform.SetParent(root.transform);
        rig.armA = armA.AddComponent<PieceProbe>();

        var armB = new GameObject("armB");
        armB.transform.SetParent(root.transform);
        rig.armB = armB.AddComponent<PieceProbe>();

        var tipB = new GameObject("tipB");
        tipB.transform.SetParent(armB.transform);
        tipB.AddComponent<PieceProbe>();

        var armC = new GameObject("armC");
        armC.transform.SetParent(root.transform);
        rig.armC = armC.AddComponent<PieceProbe>();
        armC.AddComponent<PredictedParent>();

        return root;
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(rigPrefab, out rigPrefabId);

        if (ctx.isServer)
        {
            if (!pm.hierarchy.Create(rigPrefab, new Vector3(100f, 0f, 0f), Quaternion.identity).HasValue)
                return ScenarioResult.Fail("failed to create first piece rig");
            if (!pm.hierarchy.Create(rigPrefab, new Vector3(120f, 0f, 0f), Quaternion.identity).HasValue)
                return ScenarioResult.Fail("failed to create second piece rig");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => EndShapeReached(pm) && ProbesStable(pm),
                Timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"piece lifecycle timeout: {DescribeShape(pm)} probesStable={ProbesStable(pm)}");
        }

        await UniTask.WaitForSeconds(SettleSeconds, cancellationToken: ctx.cancellationToken);

        var refFailure = CheckSerializedRefs(pm);
        if (refFailure != null)
            return ScenarioResult.Fail(refFailure);

        return await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(ctx), 30f);
    }

    internal static bool EndShapeReached(PredictionManager pm)
    {
        int roots = 0, pieces = 0;
        bool actorDone = false;

        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != rigPrefabId)
                continue;

            pieces++;

            if (!record.isRootRecord)
                continue;

            roots++;

            if (record.instanceId.TryGetComponent<PieceRigRoot>(pm, out var rig))
                actorDone = rig.currentState.step >= 3;
        }

        return roots == 1 && pieces == 4 && actorDone;
    }

    internal static string DescribeShape(PredictionManager pm)
    {
        int roots = 0, pieces = 0;
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != rigPrefabId)
                continue;

            pieces++;
            if (record.isRootRecord)
                roots++;
        }

        return $"roots={roots}/1 pieces={pieces}/4";
    }

    internal static string CheckSerializedRefs(PredictionManager pm)
    {
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != rigPrefabId || !record.isRootRecord)
                continue;

            if (!record.instanceId.TryGetComponent<PieceRigRoot>(pm, out var rig))
                return "surviving rig root has no PieceRigRoot component";

            if (!rig.armA)
                return "serialized reference to the surviving armA piece broke";

            if (!rig.armA.gameObject.activeInHierarchy)
                return "surviving armA piece is inactive";

            if (rig.armB)
                return "serialized reference to the deleted armB piece still resolves after the pool window";

            if (!rig.armC)
                return "serialized reference to the surviving armC piece broke";

            return null;
        }

        return "no surviving rig root found";
    }

    internal static string BuildDigest(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var counter = UnityEngine.Object.FindFirstObjectByType<DeterministicTickCounter>();

        var sb = new StringBuilder();
        sb.Append(PredictionTestUtils.WorldDigest(ctx, counter));
        PredictionTestUtils.AppendIdentities<PieceProbe>(pm, rigPrefabId, sb,
            probe => probe.currentState.ticksAlive.ToString());
        return sb.ToString();
    }

    internal static bool ProbesStable(PredictionManager pm)
    {
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != rigPrefabId)
                continue;

            if (record.instanceId.TryGetComponent<PieceProbe>(pm, out var probe) &&
                probe.currentState.ticksAlive < PieceProbe.TickCap)
                return false;
        }

        return true;
    }
}
