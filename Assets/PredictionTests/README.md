# Prediction Tests

Multi-process end-to-end tests for the prediction pipeline, modeled after PurrNet's `PlayModeTests`. Each process loads `Bootstrap.unity`, connects, then runs the scenario sequence in lockstep (server drives, clients ack). Results are written as JSON via `-results`.

## Scenarios

| Scenario | What it guards |
|---|---|
| `PredictionBootstrap` | Connection + PredictionManager spawn/tick on every peer |
| `BounceScenario` | Verified-gated physics events fire exactly once per physical event: a predicted rigidbody bounces and every peer's `isVerified`-gated, tick-deduped collision counter must equal the server's (repro for the multi-fire report) |
| `DeterministicAlignmentScenario` | Deterministic identities stay tick-aligned with synced state across the join seam; timed deterministic spawns produce identical instance ids everywhere (PurrNet v1.20.0-beta.160 regression class) |
| `PredictedPawnScenario` | Input round-trip, per-player owned identities, input-driven hierarchy spawns converge |
| `ReconnectScenario` | Disconnect/reconnect mid-simulation: rejoined client re-syncs and stays converged through new deterministic spawns |
| `ProjectileChainScenario` | Predicted projectile bursts create predicted muzzle and hit effects under tiny prefab pools; list-backed projectile/module state stresses rollback reuse |
| `StaticModuleReuseScenario` | Tick-pooled predicted identities rerun static module setup on reuse and reset list-backed module state |
| `DynamicModuleShapeScenario` | Reused predicted identities start with no stale dynamic modules, then add/remove different dynamic module shapes |
| `ProjectileChainReconnectScenario` | A client reconnects during an active projectile/VFX burst, stressing full-sync while dynamic modules and pooled effects churn |
| `ServerRelayScenario` | ServerRelay bodies remain kinematic on clients and only execute verified ticks |
| `SoftCorrectionScenario` | 3D soft-corrected bodies tolerate local divergence and converge without replay simulation |
| `SoftCorrection2DScenario` | 2D soft-corrected bodies follow the same convergence and replay guarantees |
| `OwnedRelayScenario` | PredictedIfOwned resolves to live prediction for the owner and server relay for non-owners |
| `SoftCorrectionPoolReuseScenario` | A scoped soft-correction object preserves its policy through replay pooling, while a completed pooled lifetime cannot leak pose-correction accumulators into the next object |
| `GenericSoftCorrectionScenario` | An opted-in generic state consumes verified deltas and converges without rollback simulation |
| `ReplayPolicyTransitionScenario` | Entering SoftCorrection during reconcile freezes the body before the replay physics pass |
| `DeterministicGauntletScenario` | Input-driven deterministic logic and `PredictedRandom` survive latency/jitter/loss byte-exactly: a scene-authored `DeterministicIdentity<INPUT,STATE>` accumulates an RNG stream and server-generated inputs for 200 ticks; every peer must land on the identical steps/seed/accumulator/input-sum. Canary for input-redundancy-window overruns and deterministic timeline shifts |

All scenarios fail on unexpected Unity `Error`, `Assert`, or `Exception` logs during the active scenario. They run with simulated latency (40–80ms by default, configurable on the `Bootstrap` object or via `-latencyMin`/`-latencyMax`; `-latencyMax 0` disables) so rollback depth resembles real conditions instead of a clean localhost.

Convergence is asserted by exchanging a world digest (deterministic counter delta vs `time.tick`, hierarchy instance list, `nextInstanceId`, pawn states) — clients report theirs, the server fails on any mismatch.

## Running in the editor

Open `Bootstrap.unity` **in the main editor and in every clone**, then enter play mode in the main editor first and in the clones within the connection timeout (30s). The main editor runs as Host (configurable on the `Bootstrap` object); ParrelSync/MPPM clones auto-detect and join as clients. Default expected connections: 2 (host + one clone) — raise `Editor Expected Connections` when using more clones.

## Running standalone

Build `StandaloneLinux64`/`StandaloneWindows64` with this scene first, then:

```
PurrDictionTests -batchmode -nographics -role host -count 3 -results host.json -logFile host.log
PurrDictionTests -batchmode -nographics -role client -count 3 -results client-1.json -logFile client-1.log
PurrDictionTests -batchmode -nographics -role client -count 3 -results client-2.json -logFile client-2.log
```

Optional args: `-port`, `-serverHost`, `-connectTimeout`. Exit code is non-zero if any scenario fails. CI runs this via `.github/workflows/prediction-tests.yml` (server and host matrix, IL2CPP).

Policy regression scenarios are included in the normal suite. Pass `-policyRegressionScenariosOnly`
to run just the bootstrap and the three focused policy scenarios.

## Server load and visibility benchmark

Pass `-serverLoadBenchmark` to run only the bootstrap plus `ServerLoadBenchmarkScenario`.
The server spawns `-benchObjects` movers (default 200), settles, then samples acknowledgement
lag and frame-write profiler markers for `-benchSeconds` (default 20). Visibility markers cover
per-player preparation, event commits, and hierarchy/3D/2D projection. Direct player runs support:

- `-benchVisibilityMode none|static|churn|acquire-churn` (`none` is the default-visible baseline).
- `-benchVisibilityPercent` (default 25).
- `-benchVisibilityChurnPercent` (default 10) and `-benchVisibilityChurnTicks` (default 30).
- `-benchDeleteChurn <perSecond>` deletes and respawns that many movers per second during the
  timed window (steady total population, real `hierarchy.Delete` plus prefab respawn), exercising
  the per-frame delete tombstone section. Requires the default `-benchVisibilityMode none`.

`static` issues one-time `HideFrom` calls for the hidden complement. `churn` keeps the configured
visible fraction and issues only `HideFrom`/`ShowTo` changes at epoch boundaries. `acquire-churn`
hides the benchmark cohort once, holds `AcquireVisibility` handles for the visible window, and swaps
only the entering/leaving leases. Churn at the 0%/100% edges is capped to an achievable value and
reported as capped churn. An exact per-client RPC barrier prevents timing from starting with initial
state still in flight, including when a client expects zero visible movers. After sampling and timed
transport counters stop, every client also validates the exact sorted visible-root signature before
the scenario can pass.

Use `Tools/PurrDiction/Analysis/Run Server Load Latency Sweep` for the unchanged latency baseline.
`Tools/PurrDiction/Analysis/Run Server Load Visibility Sweep` runs a four-case
`none|static|churn|acquire-churn` preset at 0 ms below
`Builds/ServerLoadBenchmark/VisibilityPreset`. Use
`-executeMethod PurrNet.Prediction.Benchmarks.Editor.ServerLoadBenchmarkRunner.RunFromCommandLine`
to build once and run a matrix. With no new arguments, its workload is unchanged: 3 clients, 200
objects, visibility `none`, and latencies `0,50,100,200`. Opt-in CSV dimensions are
`-slbClientCounts`, `-slbObjectCounts`, `-slbVisibilityModes`, and `-slbLatencies`; legacy scalar
`-slbClients` and `-slbObjects` remain supported. Visibility settings use
`-slbVisibilityPercent`, `-slbVisibilityChurnPercent`, and `-slbVisibilityChurnTicks`.
`-slbVisibilityModes` also accepts `deletechurn`, which runs the delete-churn workload (visibility
left fully open) at the `-slbDeleteChurn` rate (default 10 per second).

The Cartesian product is capped at 32 runs by default so an accidental matrix does not monopolize CI;
raise that deliberate guard with `-slbMaxRuns`. For example:

```text
Unity.exe -batchmode -nographics -projectPath <project> -executeMethod PurrNet.Prediction.Benchmarks.Editor.ServerLoadBenchmarkRunner.RunFromCommandLine -slbClientCounts "1,4" -slbObjectCounts "200,1000" -slbVisibilityModes "none,static,churn,acquire-churn" -slbLatencies "0,100" -slbVisibilityPercent 25 -slbVisibilityChurnPercent 10 -slbVisibilityChurnTicks 30 -slbMaxRuns 32 -logFile server-load-runner.log
```

Results land in `Builds/ServerLoadBenchmark/server-load-sweep.{md,json}` with one artifact directory
per case. Reports include acknowledgement lag, visibility mutations/acquisitions/releases/active
handles/epochs, final-signature validation counts, every sampled marker (including
`CommitVisibilityChanges`), timed-window host bytes, and separately labeled whole-scenario host
bytes. Delivery-cadence columns cover the timed window: reliable frames (per-client frames that
took the reliable recovery path), full frames (non-delta frames), the per-case delete-churn rate
with delete/respawn counts, and the client-averaged `framesPerSecond` (server-frame apply passes
per second on each pure client). Other runner controls include `-slbSeconds`, `-slbInputEvery`,
`-slbPacketLoss`, `-slbSkipBuild`, and `-slbPlayer`.

## Visibility microbenchmarks

Use `Tools/PurrDiction/Analysis/Run Visibility Benchmarks` to measure the per-player visibility
hot paths without adding machine-dependent timing assertions to the test suite. The runner covers
`HideFrom`, `ShowTo`, `AcquireVisibility`, and handle-disposal event submission; idempotent and sparse
timeline mutations; initial and stable ACK pruning; hierarchy projection (including deletes and a
16-player batch); baseline-root membership; addressed record encoding/decoding; and 3D/2D
physics-event projection. It reports median/min/max time, steady-state allocation, normalized time
per source record, output counts, and encoded bit counts to
`Temp/PurrDictionVisibilityBenchmarks/prediction-visibility-benchmarks.{json,md}`.

For batch-mode comparisons, run:

```text
Unity.exe -batchmode -nographics -projectPath <project> -executeMethod PurrNet.Prediction.Benchmarks.Editor.PredictionVisibilityBenchmarkRunner.RunFromCommandLine -purrdictionVisibilityBenchmarkOutput Temp/PurrDictionVisibilityBenchmarks -logFile Temp/PurrDictionVisibilityBenchmarks/benchmark.log
```

Treat results as comparative data from the same machine and build configuration; these benchmarks
are intentionally opt-in and non-gating.
