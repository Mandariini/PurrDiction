using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>Records whether a dependency was injected before its predicted setup ran.</summary>
public sealed class PreSpawnProbe : PredictedIdentity<MixedNetworkProbeState>
{
    public bool injected;
    public bool injectedAtPreSetup;

    public override void OnPreSetup()
    {
        base.OnPreSetup();
        injectedAtPreSetup = injected;
    }

    protected override void Simulate(ref MixedNetworkProbeState state, float delta) => state.ticks++;
}

public static class PreSpawnHookSignals
{
    private static readonly ScenarioStateLedger<uint> Instances = new();

    [ObserversRpc(runLocally: true, bufferLast: true)]
    public static void Announce(int scenarioIndex, uint instanceId) => Instances.TryAdd(scenarioIndex, 0, instanceId);

    public static bool TryGet(int scenarioIndex, out PredictedObjectID id)
    {
        bool found = Instances.TryGet(scenarioIndex, 0, out var raw);
        id = new PredictedObjectID(raw);
        return found;
    }
}

/// <summary>
/// PredictedHierarchy.onPreSpawn lets dependency injection reach predicted objects before their
/// setup, like HierarchyV2.onPreSpawn does for network identities: prefab instances on every peer
/// and scene objects at registration (report: no pre-spawn hook for PurrDiction).
/// </summary>
public sealed class PreSpawnHookScenario : Scenario
{
    private const float Timeout = 30f;

    private static readonly HashSet<string> SceneObjectEvents = new();
    private GameObject _prefab;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _prefab = PredictionTestUtils.CreatePrefab<PreSpawnProbe>("PreSpawnProbePrefab");
        PredictionTestUtils.RegisterPrefab(ctx, _prefab);
        PredictedHierarchy.onPreSpawn += Inject;
    }

    private static void Inject(PredictedPreSpawn spawn)
    {
        if (spawn.isSceneObject)
            SceneObjectEvents.Add(spawn.gameObject.name);

        foreach (var probe in spawn.gameObject.GetComponentsInChildren<PreSpawnProbe>(true))
            probe.injected = true;
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;

        if (!SceneObjectEvents.Contains(DetachedGraphicsScenario.ScenePropName))
            return ScenarioResult.Fail($"no scene-object pre-spawn for {DetachedGraphicsScenario.ScenePropName}; saw [{string.Join(", ", SceneObjectEvents)}]");

        if (ctx.isServer)
        {
            var created = world.hierarchy.Create(_prefab, new Vector3(70, 0, 0), Quaternion.identity);
            if (!created.HasValue)
                return ScenarioResult.Fail("probe creation failed");
            PreSpawnHookSignals.Announce(ctx.scenarioIndex, created.Value.instanceId.value);
        }

        PreSpawnProbe probe = null;
        try
        {
            await UniTaskUtils.WaitWithTimeout(() =>
            {
                if (!PreSpawnHookSignals.TryGet(ctx.scenarioIndex, out var id) ||
                    !world.hierarchy.TryGetGameObject(id, out var go) || !go)
                    return false;
                probe = go.GetComponent<PreSpawnProbe>();
                return probe;
            }, Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("the probe instance never appeared");
        }

        if (!probe.injected)
            return ScenarioResult.Fail("onPreSpawn never reached the prefab instance");
        if (!probe.injectedAtPreSetup)
            return ScenarioResult.Fail("onPreSpawn ran after the instance's OnPreSetup");

        return ScenarioResult.Ok();
    }
}
