using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

public class DeterministicGauntletScenario : Scenario
{
    private const int DigestChannel = 1600;
    private const float Timeout = 90f;
    private const float SettleSeconds = 3f;

    private DeterministicGauntlet _gauntlet;

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        _gauntlet = FindFirstObjectByType<DeterministicGauntlet>();
        if (_gauntlet)
            _gauntlet.ScheduleStart(startTick);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        if (!_gauntlet)
            return ScenarioResult.Fail("no DeterministicGauntlet in the scene");

        try
        {
            await UniTaskUtils.WaitWithTimeout(() => _gauntlet.isDone, Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"gauntlet never finished: {_gauntlet.StateDigest()}");
        }

        await UniTask.WaitForSeconds(SettleSeconds, cancellationToken: ctx.cancellationToken);
        await PredictionTestUtils.AlignDigestTick(ctx, DigestChannel, Timeout);

        var counter = FindFirstObjectByType<DeterministicTickCounter>();
        var digest = PredictionTestUtils.WorldDigest(ctx, counter) + "|gauntlet=" + _gauntlet.StateDigest();
        return await DigestExchange.Compare(ctx, DigestChannel, digest, 30f);
    }
}
