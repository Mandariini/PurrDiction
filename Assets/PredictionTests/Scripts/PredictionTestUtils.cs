using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet.Prediction;
using UnityEngine;

public static class PredictionTestUtils
{
    /// <summary>
    /// Waits until the deterministic timed spawner is not about to fire. Peers digest their
    /// own predicted head, which runs ahead of the server by the input lead; a digest taken
    /// while a deterministic spawn lands inside that window sees instances the server has not
    /// simulated yet and reports a false divergence. Two seconds of headroom comfortably
    /// covers the lead at the highest simulated latencies.
    /// </summary>
    public static async UniTask WaitForDeterministicQuietWindow(ScenarioContext ctx, float timeoutSeconds)
    {
        var spawner = UnityEngine.Object.FindFirstObjectByType<DeterministicTimedSpawner>();
        if (!spawner)
            return;

        await UniTaskUtils.WaitWithTimeout(
            () => spawner.spawnedCount >= spawner.totalSpawns || spawner.secondsUntilNextSpawn > 2f,
            timeoutSeconds,
            ctx.cancellationToken);
    }

    public static void RegisterPrefab(ScenarioContext ctx, GameObject prefab, bool pooled = false, int warmupCount = 0)
    {
        ctx.predictionManager.predictedPrefabs.prefabs.Add(new PredictedPrefab
        {
            prefab = prefab,
            pooled = pooled,
            warmupCount = warmupCount
        });

        if (pooled)
            ctx.predictionManager.predictedPrefabs = ctx.predictionManager.predictedPrefabs;
    }

    public static GameObject CreatePrefab<T>(string name) where T : PredictedIdentity
    {
        var go = new GameObject(name);
        go.SetActive(false);
        go.AddComponent<T>();
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go;
    }

    public static int CountInstances(PredictionManager pm, int prefabId)
    {
        ref var state = ref pm.hierarchy.currentState;
        int count = 0;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            if (state.spawnedPrefabs[i].prefabId == prefabId)
                count++;
        }
        return count;
    }

    public static long CounterDelta(DeterministicTickCounter counter, PredictionManager pm)
    {
        return (long)counter.currentState.count - (long)pm.time.tick;
    }

    /// <summary>
    /// Stable digest of the predicted world: deterministic counter alignment, hierarchy
    /// instance list, nextInstanceId and per-pawn state. Equal across peers once the
    /// simulation is quiesced; any one-tick deterministic skew or instance-id drift
    /// shows up as a mismatch.
    /// </summary>
    public static string WorldDigest(ScenarioContext ctx, DeterministicTickCounter counter)
    {
        var pm = ctx.predictionManager;
        var sb = new StringBuilder();

        if (counter)
            sb.Append($"counterDelta={CounterDelta(counter, pm)};");

        ref var state = ref pm.hierarchy.currentState;
        sb.Append($"next={state.nextInstanceId};count={state.spawnedPrefabs.Count};");

        var entries = new List<InstanceDetails>();
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
            entries.Add(state.spawnedPrefabs[i]);
        entries.Sort((a, b) => a.instanceId.instanceId.value.CompareTo(b.instanceId.instanceId.value));

        foreach (var details in entries)
        {
            ulong owner = details.owner.HasValue ? details.owner.Value.id.value : 0;
            sb.Append($"[{(int)details.prefabId}:{details.pieceIndex.value}:{details.instanceId.instanceId.value}:{owner}");

            if (details.instanceId.TryGetComponent<PawnIdentity>(pm, out var pawn))
                sb.Append($":sum={pawn.currentState.sum}:proj={pawn.currentState.projectiles}");

            sb.Append(']');
        }

        bool any = false;
        foreach (var details in entries)
        {
            if (!details.instanceId.TryGetComponent<PredictedParent>(pm, out var carrier))
                continue;

            if (!any)
            {
                sb.Append("|parents=");
                any = true;
            }

            var link = carrier.currentState.parent;
            sb.Append('[').Append(details.instanceId.instanceId.value).Append("->");
            sb.Append(link.HasValue ? link.Value.objectId.instanceId.value.ToString() : "root");
            sb.Append(']');
        }

        return sb.ToString();
    }

    public static void AppendIdentities<T>(PredictionManager pm, int prefabId, StringBuilder sb, Func<T, string> digest)
        where T : Component
    {
        ref var state = ref pm.hierarchy.currentState;
        var entries = new List<InstanceDetails>();
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId == prefabId)
                entries.Add(details);
        }

        entries.Sort((a, b) => a.instanceId.instanceId.value.CompareTo(b.instanceId.instanceId.value));

        sb.Append('|').Append(typeof(T).Name).Append('=');
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].instanceId.TryGetComponent<T>(pm, out var instance))
                sb.Append('[').Append(digest(instance)).Append(']');
            else
                sb.Append("[missing:").Append(entries[i].instanceId.instanceId.value).Append(']');
        }
    }
}
