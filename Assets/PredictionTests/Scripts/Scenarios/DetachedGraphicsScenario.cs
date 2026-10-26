using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public static class DetachedGraphicsSignals
{
    private static readonly ScenarioStateLedger<uint> Instances = new();

    [ObserversRpc(runLocally: true, bufferLast: true)]
    public static void AnnounceInstance(int scenarioIndex, int round, uint instanceId)
        => Instances.TryAdd(scenarioIndex, round, instanceId);

    public static bool TryGetInstance(int scenarioIndex, int round, out PredictedObjectID instanceId)
    {
        bool found = Instances.TryGet(scenarioIndex, round, out var raw);
        instanceId = new PredictedObjectID(raw);
        return found;
    }
}

/// <summary>
/// A PredictedTransform with "Unparent Graphics" moves its graphics out of the predicted root, so
/// they no longer follow the root's active state. Deleting the root with hierarchy.Delete must still
/// hide them on every peer, for a scene-placed object and a runtime instance alike, and a pooled
/// instance reused by a later Create must show its graphics again.
/// </summary>
public sealed class DetachedGraphicsScenario : Scenario
{
    public const string ScenePropName = "DetachedGraphicsSceneProp";

    private const float Timeout = 30f;
    private const int BarrierReady = 1;
    private const int BarrierDeleted = 2;
    private const int BarrierRecreate = 3;
    private const int RoundFirst = 0;
    private const int RoundReused = 1;

    private GameObject _prefab;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _prefab = PredictionTestUtils.CreatePrefab<PredictedTransform>("DetachedGraphicsPrefab");
        var graphics = new GameObject("DetachedGraphicsPrefabGraphics");
        graphics.transform.SetParent(_prefab.transform, false);
        ConfigureUnparentedGraphics(_prefab.GetComponent<PredictedTransform>(), graphics.transform);
        PredictionTestUtils.RegisterPrefab(ctx, _prefab, pooled: true);
    }

    private static void ConfigureUnparentedGraphics(PredictedTransform target, Transform graphics)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(PredictedTransform).GetField("_graphics", flags)!.SetValue(target, graphics);
        typeof(PredictedTransform).GetField("_unparentGraphics", flags)!.SetValue(target, true);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        var prop = FindSceneProp();
        if (!prop)
            return ScenarioResult.Fail($"no {ScenePropName} in the scene");

        var propGraphics = prop.graphics;
        if (!propGraphics || propGraphics.IsChildOf(prop.transform))
            return ScenarioResult.Fail("the scene prop's graphics were not unparented");

        await ScenarioBarrier.Wait(ctx, BarrierReady, Timeout);

        if (ctx.isServer)
        {
            var created = world.hierarchy.Create(_prefab, new Vector3(-30, 0, 0), Quaternion.identity);
            if (!created.HasValue)
                return ScenarioResult.Fail("runtime instance creation failed");
            DetachedGraphicsSignals.AnnounceInstance(ctx.scenarioIndex, RoundFirst, created.Value.instanceId.value);
        }

        var (instance, failure) = await AwaitVisibleInstance(ctx, RoundFirst);
        if (failure != null)
            return ScenarioResult.Fail(failure);

        var instanceGraphics = instance.graphics;

        await ScenarioBarrier.Wait(ctx, BarrierDeleted, Timeout);

        if (ctx.isServer)
        {
            world.hierarchy.Delete(instance.gameObject);
            world.hierarchy.Delete(prop.gameObject);
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => !IsLive(prop.gameObject) && !IsLive(instance.gameObject), Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("the deletes never reached this peer");
        }

        // Let any reconcile that touched the deleted roots settle before judging the graphics.
        await UniTask.DelayFrame(3, cancellationToken: ctx.cancellationToken);

        var failures = new List<string>();
        CheckHidden(propGraphics, "scene prop graphics right after delete", failures);
        CheckHidden(instanceGraphics, "runtime instance graphics right after delete", failures);

        // The hierarchy keeps deleted pieces for two seconds of rollback history; scene objects stay forever.
        await UniTask.Delay(TimeSpan.FromSeconds(3), cancellationToken: ctx.cancellationToken);

        CheckHidden(propGraphics, "scene prop graphics after the rollback window", failures);
        CheckHidden(instanceGraphics, "runtime instance graphics after the rollback window", failures);

        await ScenarioBarrier.Wait(ctx, BarrierRecreate, Timeout);

        if (ctx.isServer)
        {
            var created = world.hierarchy.Create(_prefab, new Vector3(-35, 0, 0), Quaternion.identity);
            if (!created.HasValue)
                return ScenarioResult.Fail("reused instance creation failed");
            DetachedGraphicsSignals.AnnounceInstance(ctx.scenarioIndex, RoundReused, created.Value.instanceId.value);
        }

        var (reused, reuseFailure) = await AwaitVisibleInstance(ctx, RoundReused);
        if (reuseFailure != null)
            failures.Add(reuseFailure);

        if (failures.Count > 0)
            return ScenarioResult.Fail(string.Join("; ", failures));

        return ScenarioResult.Ok($"reused pooled object: {reused.gameObject == instance.gameObject}");
    }

    private async UniTask<(PredictedTransform, string)> AwaitVisibleInstance(ScenarioContext ctx, int round)
    {
        var world = ctx.predictionManager;
        PredictedTransform instance = null;

        try
        {
            await UniTaskUtils.WaitWithTimeout(() =>
            {
                if (!DetachedGraphicsSignals.TryGetInstance(ctx.scenarioIndex, round, out var id) ||
                    !world.hierarchy.TryGetGameObject(id, out var go) || !IsLive(go))
                    return false;
                instance = go.GetComponent<PredictedTransform>();
                return instance && instance.graphics;
            }, Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return (null, $"round {round}: the runtime instance never appeared");
        }

        // Graphics follow the root on the frame after it comes back.
        await UniTask.DelayFrame(3, cancellationToken: ctx.cancellationToken);

        if (!instance || !IsLive(instance.gameObject))
            return (null, $"round {round}: the runtime instance was removed again");
        if (!instance.graphics.gameObject.activeInHierarchy)
            return (null, $"round {round}: a live instance has hidden graphics");
        if (instance.graphics.IsChildOf(instance.transform))
            return (null, $"round {round}: the instance graphics were not unparented");
        return (instance, null);
    }

    private static bool IsLive(GameObject go) => go && go.activeInHierarchy;

    private static void CheckHidden(Transform graphics, string what, List<string> failures)
    {
        if (graphics && graphics.gameObject.activeInHierarchy)
            failures.Add($"{what} are still visible");
    }

    private static PredictedTransform FindSceneProp()
    {
        var all = FindObjectsByType<PredictedTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i].name == ScenePropName)
                return all[i];
        }

        return null;
    }
}
