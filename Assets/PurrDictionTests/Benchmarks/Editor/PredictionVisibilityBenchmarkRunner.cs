using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PurrNet.Prediction.Benchmarks.Editor
{
    /// <summary>
    /// Opt-in microbenchmarks for the per-player visibility replication path.
    /// These deliberately have no NUnit timing assertions: benchmark results vary by
    /// machine and should be compared as reports, not used as correctness gates.
    /// </summary>
    public static class PredictionVisibilityBenchmarkRunner
    {
        const int SampleCount = 7;
        const int WarmupIterations = 4;
        const string DefaultOutputDirectory = "Temp/PurrDictionVisibilityBenchmarks";

        static bool _allocationCounterSupported;
        static byte[] _allocationProbe;

        [MenuItem("Tools/PurrDiction/Analysis/Run Visibility Benchmarks", false, -85)]
        public static void RunFromMenu()
        {
            var report = Run();
            EditorUtility.RevealInFinder(report.markdownPath);
        }

        public static void RunFromCommandLine()
        {
            try
            {
                Run();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        public static VisibilityBenchmarkReport Run()
        {
            NetworkManager.LoadOrGenerateHashes();

            string outputDirectory =
                GetArgument("-purrdictionVisibilityBenchmarkOutput") ??
                DefaultOutputDirectory;
            Directory.CreateDirectory(outputDirectory);

            var report = new VisibilityBenchmarkReport
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                projectPath = Directory.GetCurrentDirectory(),
                outputDirectory = Path.GetFullPath(outputDirectory)
            };

            _allocationCounterSupported = CalibrateAllocationCounter();
            if (!_allocationCounterSupported)
            {
                report.warnings.Add(
                    "This Unity runtime does not expose a working per-thread allocation " +
                    "counter; B/op is reported as n/a.");
            }

            report.jsonPath = Path.GetFullPath(
                Path.Combine(outputDirectory, "prediction-visibility-benchmarks.json"));
            report.markdownPath = Path.GetFullPath(
                Path.Combine(outputDirectory, "prediction-visibility-benchmarks.md"));

            AddTimelineBenchmarks(report);
            WriteReports(report);

            AddHierarchyBenchmarks(report);
            WriteReports(report);

            AddBaselineMembershipBenchmarks(report);
            WriteReports(report);

            AddAddressedRecordBenchmarks(report);
            WriteReports(report);

            AddPhysicsBenchmarks(report);
            WriteReports(report);

            Debug.Log(
                $"PurrDiction visibility benchmarks wrote:\n" +
                $"{report.jsonPath}\n{report.markdownPath}");
            return report;
        }

        static void AddTimelineBenchmarks(VisibilityBenchmarkReport report)
        {
            AddTimelineBenchmark(report, 4096, 1, toggle: false, 256);
            AddTimelineBenchmark(report, 4096, 1, toggle: true, 128);
            AddTimelineBenchmark(report, 4096, 10, toggle: true, 64);
            AddTimelinePruneBenchmark(
                report, 4096, 25, restoreDefault: false, prePruneStable: false);
            AddTimelinePruneBenchmark(
                report, 4096, 25, restoreDefault: false, prePruneStable: true);
            AddTimelinePruneBenchmark(
                report, 4096, 25, restoreDefault: true, prePruneStable: false);
            AddManagerEventBenchmarks(report);
        }

        static void AddTimelineBenchmark(
            VisibilityBenchmarkReport report,
            int roots,
            int mutationPercent,
            bool toggle,
            int iterations)
        {
            int affectedRoots = Math.Max(1, roots * mutationPercent / 100);
            var specification = new BenchmarkSpecification
            {
                category = "Timeline",
                name = toggle
                    ? $"SetVisibleAndPrune.Toggle{mutationPercent}Percent"
                    : $"SetVisible.Idempotent{mutationPercent}Percent",
                roots = roots,
                visiblePercent = toggle ? 100 - mutationPercent : 100,
                churnPercent = toggle ? mutationPercent : 0,
                iterations = iterations,
                sourceRecords = affectedRoots,
                note = "Touches only roots supplied by visibility events."
            };

            report.operations.Add(Measure(
                specification,
                () => TimelineContext.Create(roots, affectedRoots, toggle),
                (context, _) => context.Step(),
                context => new BenchmarkObservation(
                    context.timeline.currentExceptionCount,
                    -1),
                context => context.Dispose()));
        }

        static void AddTimelinePruneBenchmark(
            VisibilityBenchmarkReport report,
            int roots,
            int hiddenPercent,
            bool restoreDefault,
            bool prePruneStable)
        {
            int affectedRoots = roots * hiddenPercent / 100;
            var specification = new BenchmarkSpecification
            {
                category = "Timeline",
                name = restoreDefault
                    ? "PruneThrough.DropRestoredDefaults"
                    : prePruneStable
                        ? "PruneThrough.StableHiddenExceptions"
                        : "PruneThrough.AnchorHiddenExceptions",
                roots = roots,
                visiblePercent = 100 - hiddenPercent,
                iterations = prePruneStable ? 256 : 1,
                sourceRecords = prePruneStable ? 0 : affectedRoots,
                note = prePruneStable
                    ? "Measures advancing ACKs after hidden exceptions reached a stable anchor."
                    : null
            };

            report.operations.Add(Measure(
                specification,
                () => TimelinePruneContext.Create(
                    affectedRoots,
                    restoreDefault,
                    prePruneStable),
                (context, _) => context.Prune(),
                context => new BenchmarkObservation(
                    context.timeline.trackedRootCount,
                    -1),
                context => context.Dispose()));
        }

        static void AddManagerEventBenchmarks(VisibilityBenchmarkReport report)
        {
            const int roots = 4096;
            const int affectedRoots = 40;
            const int iterations = 128;

            AddManagerEventBenchmark(
                report,
                "HideFrom.Batch1Percent",
                ManagerVisibilityEventOperation.HideFrom,
                roots,
                affectedRoots,
                iterations,
                visiblePercent: 100,
                "Times only event submission; frame-boundary commits run in the untimed reset.");
            AddManagerEventBenchmark(
                report,
                "ShowTo.Batch1Percent",
                ManagerVisibilityEventOperation.ShowTo,
                roots,
                affectedRoots,
                iterations,
                visiblePercent: 99,
                "Starts with the affected roots hidden and times only event submission.");
            AddManagerEventBenchmark(
                report,
                "AcquireVisibility.Batch1Percent",
                ManagerVisibilityEventOperation.Acquire,
                roots,
                affectedRoots,
                iterations,
                visiblePercent: 99,
                "Includes visibility-handle and token bookkeeping allocations.");
            AddManagerEventBenchmark(
                report,
                "VisibilityHandle.Dispose.Batch1Percent",
                ManagerVisibilityEventOperation.Dispose,
                roots,
                affectedRoots,
                iterations,
                visiblePercent: 100,
                "Starts with acquired hidden roots and times final-handle disposal.");
        }

        static void AddManagerEventBenchmark(
            VisibilityBenchmarkReport report,
            string name,
            ManagerVisibilityEventOperation operation,
            int roots,
            int affectedRoots,
            int iterations,
            int visiblePercent,
            string note)
        {
            var specification = new BenchmarkSpecification
            {
                category = "Manager events",
                name = name,
                roots = roots,
                visiblePercent = visiblePercent,
                churnPercent = 1,
                iterations = iterations,
                sourceRecords = affectedRoots,
                note = note
            };

            report.operations.Add(MeasureWithUntimedReset(
                specification,
                () => ManagerVisibilityEventContext.Create(
                    roots,
                    affectedRoots,
                    operation),
                context => context.Step(),
                context => context.ResetAfterStep(),
                context => new BenchmarkObservation(
                    context.lastOperationCount,
                    -1),
                context => context.Dispose()));
        }

        static void AddHierarchyBenchmarks(VisibilityBenchmarkReport report)
        {
            AddHierarchyBenchmark(report, 1024, 4, 100, 0, 64);
            AddHierarchyPassThroughBenchmark(report, 1024, 4, 256);
            AddHierarchyBenchmark(report, 1024, 4, 25, 0, 64);
            AddHierarchyBenchmark(report, 1024, 4, 25, 1, 64);
            AddHierarchyBenchmark(report, 1024, 4, 25, 8, 64);
            AddHierarchyBenchmark(report, 1024, 4, 25, 128, 32);

            var specification = new BenchmarkSpecification
            {
                category = "Hierarchy",
                name = "BuildProjection.16Players.StaggeredQuarters",
                roots = 1024,
                piecesPerRoot = 4,
                players = 16,
                visiblePercent = 25,
                iterations = 8,
                sourceRecords = 4096,
                note = "Builds and disposes one projection per player."
            };

            report.operations.Add(Measure(
                specification,
                () => MultiPlayerHierarchyContext.Create(1024, 4, 16),
                (context, _) => context.ProjectAll(),
                context => new BenchmarkObservation(context.lastOutputCount, -1),
                context => context.Dispose()));
        }

        static void AddHierarchyPassThroughBenchmark(
            VisibilityBenchmarkReport report,
            int roots,
            int piecesPerRoot,
            int iterations)
        {
            var specification = new BenchmarkSpecification
            {
                category = "Hierarchy",
                name = "SelectGlobal.PassThroughDefaultVisible",
                roots = roots,
                piecesPerRoot = piecesPerRoot,
                visiblePercent = 100,
                iterations = iterations,
                sourceRecords = roots * piecesPerRoot,
                note =
                    "Exercises the default-visible/no-pending-delete selection branch " +
                    "that reuses the global hierarchy instead of building a projection."
            };

            report.operations.Add(Measure(
                specification,
                () => HierarchyPassThroughContext.Create(roots, piecesPerRoot),
                (context, _) => context.Select(),
                context => new BenchmarkObservation(context.lastOutputCount, -1),
                context => context.Dispose()));
        }

        static void AddHierarchyBenchmark(
            VisibilityBenchmarkReport report,
            int roots,
            int piecesPerRoot,
            int visiblePercent,
            int deletes,
            int iterations)
        {
            var specification = new BenchmarkSpecification
            {
                category = "Hierarchy",
                name = deletes == 0
                    ? "BuildProjection"
                    : $"BuildProjection.With{deletes}Deletes",
                roots = roots,
                piecesPerRoot = piecesPerRoot,
                visiblePercent = visiblePercent,
                deletes = deletes,
                iterations = iterations,
                sourceRecords = roots * piecesPerRoot
            };

            report.operations.Add(Measure(
                specification,
                () => HierarchyProjectionContext.Create(
                    roots,
                    piecesPerRoot,
                    visiblePercent,
                    deletes),
                (context, _) => context.Project(),
                context => new BenchmarkObservation(context.lastOutputCount, -1),
                context => context.Dispose()));
        }

        static void AddBaselineMembershipBenchmarks(VisibilityBenchmarkReport report)
        {
            AddBaselineMembershipPair(report, 256, 4, 64);
            AddBaselineMembershipPair(report, 1024, 4, 8);
            AddBaselineMembershipPair(report, 4096, 4, 1);
        }

        static void AddBaselineMembershipPair(
            VisibilityBenchmarkReport report,
            int roots,
            int piecesPerRoot,
            int iterations)
        {
            var linearSpecification = new BenchmarkSpecification
            {
                category = "Baseline membership",
                name = "LinearPerSystem.Reference",
                roots = roots,
                piecesPerRoot = piecesPerRoot,
                iterations = iterations,
                sourceRecords = roots * piecesPerRoot,
                note = "Reference for the original per-system StateContainsRoot path."
            };

            report.operations.Add(Measure(
                linearSpecification,
                () => BaselineMembershipContext.Create(roots, piecesPerRoot),
                (context, _) => context.CheckLinear(),
                context => new BenchmarkObservation(context.lastMatches, -1),
                context => context.Dispose()));

            var indexedSpecification = new BenchmarkSpecification
            {
                category = "Baseline membership",
                name = "BuildRootIndexThenLookup",
                roots = roots,
                piecesPerRoot = piecesPerRoot,
                iterations = iterations,
                sourceRecords = roots * piecesPerRoot,
                note = "Includes rebuilding the root HashSet once per operation."
            };

            report.operations.Add(Measure(
                indexedSpecification,
                () => BaselineMembershipContext.Create(roots, piecesPerRoot),
                (context, _) => context.CheckIndexed(),
                context => new BenchmarkObservation(context.lastMatches, -1),
                context => context.Dispose()));
        }

        static void AddAddressedRecordBenchmarks(VisibilityBenchmarkReport report)
        {
            AddAddressedRecordSet(report, 64, 13, 256);
            AddAddressedRecordSet(report, 1024, 256, 16);
            AddAddressedStateOmissionBenchmark(report, 4096, 256, 0, 128);
            AddAddressedStateOmissionBenchmark(report, 4096, 256, 1, 64);
            AddAddressedStateOmissionBenchmark(report, 4096, 256, 100, 8);
        }

        static void AddAddressedStateOmissionBenchmark(
            VisibilityBenchmarkReport report,
            int candidateCount,
            int payloadBits,
            int changedPercent,
            int iterations)
        {
            var specification = new BenchmarkSpecification
            {
                category = "Addressed state omission",
                name = $"Write.{changedPercent}PercentChanged",
                addressedRecords = candidateCount,
                payloadBits = payloadBits,
                iterations = iterations,
                sourceRecords = candidateCount,
                note =
                    "Scans every candidate, buffers only changed addressed records, " +
                    "then writes the reduced count and copies the buffered bits."
            };

            report.operations.Add(Measure(
                specification,
                () => AddressedStateOmissionContext.Create(
                    candidateCount,
                    payloadBits,
                    changedPercent),
                (context, _) => context.WriteBatch(),
                context => new BenchmarkObservation(
                    context.lastOutputCount,
                    context.serializedBits),
                context => context.Dispose()));
        }

        static void AddAddressedRecordSet(
            VisibilityBenchmarkReport report,
            int recordCount,
            int payloadBits,
            int iterations)
        {
            var writeSpecification = new BenchmarkSpecification
            {
                category = "Addressed records",
                name = "Write.SparseIds",
                addressedRecords = recordCount,
                payloadBits = payloadBits,
                iterations = iterations,
                sourceRecords = recordCount
            };

            report.operations.Add(Measure(
                writeSpecification,
                () => AddressedWriteContext.Create(recordCount, payloadBits),
                (context, _) => context.WriteBatch(),
                context => new BenchmarkObservation(recordCount, context.serializedBits),
                context => context.Dispose()));

            AddAddressedReadBenchmark(
                report,
                recordCount,
                payloadBits,
                iterations,
                skipPayload: false);
            AddAddressedReadBenchmark(
                report,
                recordCount,
                payloadBits,
                iterations,
                skipPayload: true);
        }

        static void AddAddressedReadBenchmark(
            VisibilityBenchmarkReport report,
            int recordCount,
            int payloadBits,
            int iterations,
            bool skipPayload)
        {
            var specification = new BenchmarkSpecification
            {
                category = "Addressed records",
                name = skipPayload ? "Read.SkipUnknown" : "Read.ConsumeKnown",
                addressedRecords = recordCount,
                payloadBits = payloadBits,
                iterations = iterations,
                sourceRecords = recordCount
            };

            report.operations.Add(Measure(
                specification,
                () => AddressedReadContext.Create(
                    recordCount,
                    payloadBits,
                    skipPayload),
                (context, _) => context.ReadBatch(),
                context => new BenchmarkObservation(
                    context.lastRecordCount,
                    context.serializedBits),
                context => context.Dispose()));
        }

        static void AddPhysicsBenchmarks(VisibilityBenchmarkReport report)
        {
#if UNITY_PHYSICS_3D
            AddPhysics3DBenchmark(report, 2048, 0, 64);
            AddPhysics3DBenchmark(report, 2048, 25, 64);
            AddPhysics3DBenchmark(report, 2048, 100, 64);
#else
            report.warnings.Add(
                "UNITY_PHYSICS_3D is not defined; skipped 3D physics visibility projection.");
#endif

#if UNITY_PHYSICS_2D
            AddPhysics2DBenchmark(report, 2048, 0, 64);
            AddPhysics2DBenchmark(report, 2048, 25, 64);
            AddPhysics2DBenchmark(report, 2048, 100, 64);
#else
            report.warnings.Add(
                "UNITY_PHYSICS_2D is not defined; skipped 2D physics visibility projection.");
#endif
        }

#if UNITY_PHYSICS_3D
        static void AddPhysics3DBenchmark(
            VisibilityBenchmarkReport report,
            int events,
            int retainedPercent,
            int iterations)
        {
            var specification = new BenchmarkSpecification
            {
                category = "Physics 3D",
                name = "ProjectEvents",
                visiblePercent = retainedPercent,
                physicsEvents = events,
                iterations = iterations,
                sourceRecords = events,
                note = "Events contain zero contact points."
            };

            report.operations.Add(Measure(
                specification,
                () => Physics3DProjectionContext.Create(events, retainedPercent),
                (context, _) => context.Project(),
                context => new BenchmarkObservation(context.lastOutputCount, -1),
                context => context.Dispose()));
        }
#endif

#if UNITY_PHYSICS_2D
        static void AddPhysics2DBenchmark(
            VisibilityBenchmarkReport report,
            int events,
            int retainedPercent,
            int iterations)
        {
            var specification = new BenchmarkSpecification
            {
                category = "Physics 2D",
                name = "ProjectEvents",
                visiblePercent = retainedPercent,
                physicsEvents = events,
                iterations = iterations,
                sourceRecords = events,
                note = "Events contain zero contact points."
            };

            report.operations.Add(Measure(
                specification,
                () => Physics2DProjectionContext.Create(events, retainedPercent),
                (context, _) => context.Project(),
                context => new BenchmarkObservation(context.lastOutputCount, -1),
                context => context.Dispose()));
        }
#endif

        static VisibilityOperationResult Measure<TContext>(
            BenchmarkSpecification specification,
            Func<TContext> setup,
            Action<TContext, int> body,
            Func<TContext, BenchmarkObservation> observe,
            Action<TContext> cleanup)
            where TContext : class
        {
            Debug.Log(
                $"PurrDiction visibility benchmark: starting " +
                $"{specification.category}.{specification.name}.");

            TContext warmupContext = null;
            try
            {
                warmupContext = setup();
                int warmups = Math.Min(specification.iterations, WarmupIterations);
                for (var i = 0; i < warmups; i++)
                    body(warmupContext, i);
                observe(warmupContext);
            }
            finally
            {
                if (warmupContext != null)
                    cleanup(warmupContext);
            }

            var timings = new double[SampleCount];
            var allocations = new double[SampleCount];
            var observation = default(BenchmarkObservation);

            for (var sample = 0; sample < SampleCount; sample++)
            {
                TContext context = null;
                try
                {
                    context = setup();

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    long allocatedBefore = _allocationCounterSupported
                        ? GC.GetAllocatedBytesForCurrentThread()
                        : 0;
                    long started = Stopwatch.GetTimestamp();

                    for (var i = 0; i < specification.iterations; i++)
                        body(context, i);

                    long elapsed = Stopwatch.GetTimestamp() - started;
                    long allocatedAfter = _allocationCounterSupported
                        ? GC.GetAllocatedBytesForCurrentThread()
                        : 0;

                    timings[sample] =
                        elapsed * 1_000_000_000.0 /
                        Stopwatch.Frequency /
                        specification.iterations;
                    allocations[sample] = _allocationCounterSupported
                        ? (allocatedAfter - allocatedBefore) /
                          (double)specification.iterations
                        : -1;
                    observation = observe(context);
                }
                finally
                {
                    if (context != null)
                        cleanup(context);
                }
            }

            Array.Sort(timings);
            Array.Sort(allocations);

            double medianNanoseconds = timings[SampleCount / 2];
            var result = new VisibilityOperationResult
            {
                category = specification.category,
                name = specification.name,
                roots = specification.roots,
                piecesPerRoot = specification.piecesPerRoot,
                players = specification.players,
                visiblePercent = specification.visiblePercent,
                churnPercent = specification.churnPercent,
                deletes = specification.deletes,
                addressedRecords = specification.addressedRecords,
                payloadBits = specification.payloadBits,
                physicsEvents = specification.physicsEvents,
                sourceRecords = specification.sourceRecords,
                iterations = specification.iterations,
                samples = SampleCount,
                minNanoseconds = timings[0],
                medianNanoseconds = medianNanoseconds,
                maxNanoseconds = timings[SampleCount - 1],
                medianAllocatedBytes = allocations[SampleCount / 2],
                nanosecondsPerSourceRecord = specification.sourceRecords > 0
                    ? medianNanoseconds / specification.sourceRecords
                    : -1,
                outputCount = observation.outputCount,
                serializedBits = observation.serializedBits,
                note = specification.note
            };

            Debug.Log(
                $"PurrDiction visibility benchmark: finished " +
                $"{result.category}.{result.name} = " +
                $"{FormatNumber(result.medianNanoseconds)} ns/op, " +
                $"{FormatAllocatedBytes(result.medianAllocatedBytes)} B/op.");
            return result;
        }

        static VisibilityOperationResult MeasureWithUntimedReset<TContext>(
            BenchmarkSpecification specification,
            Func<TContext> setup,
            Action<TContext> body,
            Action<TContext> reset,
            Func<TContext, BenchmarkObservation> observe,
            Action<TContext> cleanup)
            where TContext : class
        {
            Debug.Log(
                $"PurrDiction visibility benchmark: starting " +
                $"{specification.category}.{specification.name}.");

            TContext warmupContext = null;
            try
            {
                warmupContext = setup();
                int warmups = Math.Min(specification.iterations, WarmupIterations);
                for (var i = 0; i < warmups; i++)
                {
                    body(warmupContext);
                    reset(warmupContext);
                }

                observe(warmupContext);
            }
            finally
            {
                if (warmupContext != null)
                    cleanup(warmupContext);
            }

            var timings = new double[SampleCount];
            var allocations = new double[SampleCount];
            var observation = default(BenchmarkObservation);

            for (var sample = 0; sample < SampleCount; sample++)
            {
                TContext context = null;
                try
                {
                    context = setup();

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    long elapsed = 0;
                    long allocated = 0;
                    for (var i = 0; i < specification.iterations; i++)
                    {
                        long allocatedBefore = _allocationCounterSupported
                            ? GC.GetAllocatedBytesForCurrentThread()
                            : 0;
                        long started = Stopwatch.GetTimestamp();

                        body(context);

                        elapsed += Stopwatch.GetTimestamp() - started;
                        if (_allocationCounterSupported)
                        {
                            allocated +=
                                GC.GetAllocatedBytesForCurrentThread() -
                                allocatedBefore;
                        }
                        reset(context);
                    }

                    timings[sample] =
                        elapsed * 1_000_000_000.0 /
                        Stopwatch.Frequency /
                        specification.iterations;
                    allocations[sample] = _allocationCounterSupported
                        ? allocated / (double)specification.iterations
                        : -1;
                    observation = observe(context);
                }
                finally
                {
                    if (context != null)
                        cleanup(context);
                }
            }

            Array.Sort(timings);
            Array.Sort(allocations);

            double medianNanoseconds = timings[SampleCount / 2];
            var result = new VisibilityOperationResult
            {
                category = specification.category,
                name = specification.name,
                roots = specification.roots,
                piecesPerRoot = specification.piecesPerRoot,
                players = specification.players,
                visiblePercent = specification.visiblePercent,
                churnPercent = specification.churnPercent,
                deletes = specification.deletes,
                addressedRecords = specification.addressedRecords,
                payloadBits = specification.payloadBits,
                physicsEvents = specification.physicsEvents,
                sourceRecords = specification.sourceRecords,
                iterations = specification.iterations,
                samples = SampleCount,
                minNanoseconds = timings[0],
                medianNanoseconds = medianNanoseconds,
                maxNanoseconds = timings[SampleCount - 1],
                medianAllocatedBytes = allocations[SampleCount / 2],
                nanosecondsPerSourceRecord = specification.sourceRecords > 0
                    ? medianNanoseconds / specification.sourceRecords
                    : -1,
                outputCount = observation.outputCount,
                serializedBits = observation.serializedBits,
                note = specification.note
            };

            Debug.Log(
                $"PurrDiction visibility benchmark: finished " +
                $"{result.category}.{result.name} = " +
                $"{FormatNumber(result.medianNanoseconds)} ns/op, " +
                $"{FormatAllocatedBytes(result.medianAllocatedBytes)} B/op.");
            return result;
        }

        static PredictedHierarchyState CreateHierarchyState(
            int roots,
            int piecesPerRoot,
            int deleteCount)
        {
            int recordCount = checked(roots * piecesPerRoot);
            var spawned = DisposableList<InstanceDetails>.Create(recordCount);
            var deletes = DisposableList<PredictedObjectID>.Create(deleteCount);

            for (var rootIndex = 0; rootIndex < roots; rootIndex++)
            {
                uint rootValue = checked((uint)(2 + rootIndex * piecesPerRoot));
                for (var pieceIndex = 0; pieceIndex < piecesPerRoot; pieceIndex++)
                {
                    var pieceId = new PredictedObjectID(
                        checked(rootValue + (uint)pieceIndex));
                    spawned.Add(new InstanceDetails(
                        prefabId: rootIndex,
                        pieceIndex: (uint)pieceIndex,
                        instanceId: pieceId,
                        spawnPosition: Vector3.zero,
                        spawnRotation: Quaternion.identity,
                        owner: null,
                        parent: null));
                }
            }

            for (var i = 0; i < deleteCount; i++)
            {
                int rootIndex = i * roots / deleteCount;
                deletes.Add(new PredictedObjectID(
                    checked((uint)(2 + rootIndex * piecesPerRoot))));
            }

            return new PredictedHierarchyState(
                spawned,
                deletes,
                checked((uint)(2 + recordCount)));
        }

        static HashSet<PredictedObjectID> CreateVisibleRoots(
            int roots,
            int piecesPerRoot,
            int visiblePercent,
            int offset = 0)
        {
            int visibleCount = roots * visiblePercent / 100;
            var result = new HashSet<PredictedObjectID>();

            if (visibleCount == 0)
                return result;

            if (visiblePercent == 25)
            {
                for (var rootIndex = 0; rootIndex < roots; rootIndex++)
                {
                    if ((rootIndex + offset) % 4 != 0)
                        continue;

                    result.Add(new PredictedObjectID(
                        checked((uint)(2 + rootIndex * piecesPerRoot))));
                }

                return result;
            }

            for (var rootIndex = 0; rootIndex < visibleCount; rootIndex++)
            {
                result.Add(new PredictedObjectID(
                    checked((uint)(2 + rootIndex * piecesPerRoot))));
            }

            return result;
        }

        static void ApplyVisibilityExceptions(
            PlayerVisibilityTimeline timeline,
            int roots,
            int piecesPerRoot,
            HashSet<PredictedObjectID> visible,
            ulong tick)
        {
            for (var rootIndex = 0; rootIndex < roots; rootIndex++)
            {
                var rootId = new PredictedObjectID(
                    checked((uint)(2 + rootIndex * piecesPerRoot)));
                if (!visible.Contains(rootId))
                    timeline.SetVisible(tick, rootId, false);
            }
        }
        static void FillPayload(BitPacker payload, int payloadBits)
        {
            payload.ResetPositionAndMode(false);
            int remaining = payloadBits;
            while (remaining >= 64)
            {
                payload.WriteBits(ulong.MaxValue, 64);
                remaining -= 64;
            }

            if (remaining > 0)
                payload.WriteBits(ulong.MaxValue, (byte)remaining);
        }

        static void WriteReports(VisibilityBenchmarkReport report)
        {
            File.WriteAllText(
                report.jsonPath,
                JsonUtility.ToJson(report, true));
            File.WriteAllText(
                report.markdownPath,
                BuildMarkdown(report));
        }

        static string BuildMarkdown(VisibilityBenchmarkReport report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# Prediction Visibility Benchmarks");
            builder.AppendLine();
            builder.AppendLine($"Generated: `{report.generatedAtUtc}`");
            builder.AppendLine($"Unity: `{report.unityVersion}`");
            builder.AppendLine($"Project: `{report.projectPath}`");
            builder.AppendLine();
            builder.AppendLine(
                "These are opt-in comparison benchmarks. They intentionally have no " +
                "wall-clock pass/fail thresholds.");
            builder.AppendLine();
            builder.AppendLine(
                "| Category | Operation | Shape | Iterations | Median ns/op | " +
                "Min–max ns/op | B/op | ns/source | Output | Serialized bits |");
            builder.AppendLine(
                "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");

            for (var i = 0; i < report.operations.Count; i++)
            {
                var operation = report.operations[i];
                builder.Append("| ")
                    .Append(operation.category)
                    .Append(" | ")
                    .Append(operation.name)
                    .Append(" | ")
                    .Append(BuildShape(operation))
                    .Append(" | ")
                    .Append(operation.iterations)
                    .Append(" | ")
                    .Append(FormatNumber(operation.medianNanoseconds))
                    .Append(" | ")
                    .Append(FormatNumber(operation.minNanoseconds))
                    .Append("–")
                    .Append(FormatNumber(operation.maxNanoseconds))
                    .Append(" | ")
                    .Append(FormatAllocatedBytes(operation.medianAllocatedBytes))
                    .Append(" | ")
                    .Append(operation.nanosecondsPerSourceRecord < 0
                        ? "n/a"
                        : FormatNumber(operation.nanosecondsPerSourceRecord))
                    .Append(" | ")
                    .Append(operation.outputCount)
                    .Append(" | ")
                    .Append(operation.serializedBits < 0
                        ? "n/a"
                        : operation.serializedBits.ToString(
                            CultureInfo.InvariantCulture))
                    .AppendLine(" |");
            }

            if (report.warnings.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## Warnings");
                builder.AppendLine();
                for (var i = 0; i < report.warnings.Count; i++)
                    builder.AppendLine($"- {report.warnings[i]}");
            }

            builder.AppendLine();
            builder.AppendLine("## Notes");
            builder.AppendLine();
            for (var i = 0; i < report.operations.Count; i++)
            {
                var operation = report.operations[i];
                if (!string.IsNullOrEmpty(operation.note))
                {
                    builder.AppendLine(
                        $"- `{operation.category}.{operation.name}`: " +
                        operation.note);
                }
            }

            builder.AppendLine();
            builder.AppendLine("## Command line");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(
                "Unity.exe -batchmode -nographics -projectPath <project> " +
                "-executeMethod " +
                "PurrNet.Prediction.Benchmarks.Editor." +
                "PredictionVisibilityBenchmarkRunner.RunFromCommandLine " +
                "-purrdictionVisibilityBenchmarkOutput " +
                "Temp/PurrDictionVisibilityBenchmarks " +
                "-logFile Temp/PurrDictionVisibilityBenchmarks/benchmark.log");
            builder.AppendLine("```");
            return builder.ToString();
        }

        static string BuildShape(VisibilityOperationResult operation)
        {
            var parts = new List<string>(8);
            if (operation.players > 0)
                parts.Add($"{operation.players} players");
            if (operation.roots > 0)
                parts.Add($"{operation.roots} roots");
            if (operation.piecesPerRoot > 0)
                parts.Add($"{operation.piecesPerRoot} pieces/root");
            if (operation.visiblePercent >= 0)
                parts.Add($"{operation.visiblePercent}% visible");
            if (operation.churnPercent > 0)
                parts.Add($"{operation.churnPercent}% churn");
            if (operation.deletes > 0)
                parts.Add($"{operation.deletes} deletes");
            if (operation.addressedRecords > 0)
                parts.Add($"{operation.addressedRecords} records");
            if (operation.payloadBits > 0)
                parts.Add($"{operation.payloadBits}-bit payload");
            if (operation.physicsEvents > 0)
                parts.Add($"{operation.physicsEvents} events");
            return string.Join(", ", parts);
        }

        static string FormatNumber(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        static string FormatAllocatedBytes(double value)
        {
            return value < 0 ? "n/a" : FormatNumber(value);
        }

        static bool CalibrateAllocationCounter()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            _allocationProbe = new byte[4096];
            long after = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(_allocationProbe);
            _allocationProbe = null;
            return after > before;
        }

        static string GetArgument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (var i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(
                        arguments[i],
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[i + 1];
                }
            }

            return null;
        }

        readonly struct BenchmarkObservation
        {
            public readonly long outputCount;
            public readonly long serializedBits;

            public BenchmarkObservation(long outputCount, long serializedBits)
            {
                this.outputCount = outputCount;
                this.serializedBits = serializedBits;
            }
        }

        sealed class BenchmarkSpecification
        {
            public string category;
            public string name;
            public int roots;
            public int piecesPerRoot;
            public int players;
            public int visiblePercent = -1;
            public int churnPercent;
            public int deletes;
            public int addressedRecords;
            public int payloadBits;
            public int physicsEvents;
            public int sourceRecords;
            public int iterations;
            public string note;
        }

        sealed class TimelineContext : IDisposable
        {
            public readonly PlayerVisibilityTimeline timeline = new();
            readonly PredictedObjectID[] _affected;
            readonly bool _toggle;
            bool _visible = true;
            ulong _tick;

            TimelineContext(PredictedObjectID[] affected, bool toggle)
            {
                _affected = affected;
                _toggle = toggle;
            }

            public static TimelineContext Create(
                int roots,
                int affectedRoots,
                bool toggle)
            {
                var affected = new PredictedObjectID[affectedRoots];
                for (var i = 0; i < affected.Length; i++)
                {
                    affected[i] = new PredictedObjectID(
                        checked((uint)(2 + i % roots)));
                }

                return new TimelineContext(affected, toggle);
            }

            public void Step()
            {
                _tick++;
                if (_toggle)
                    _visible = !_visible;

                for (var i = 0; i < _affected.Length; i++)
                    timeline.SetVisible(_tick, _affected[i], _visible);

                if (_tick > 2)
                    timeline.PruneThrough(_tick - 2);
            }

            public void Dispose()
            {
                timeline.Clear();
            }
        }

        sealed class TimelinePruneContext : IDisposable
        {
            public readonly PlayerVisibilityTimeline timeline = new();
            readonly int _expectedTrackedRoots;
            readonly bool _advanceAcknowledgement;
            ulong _acknowledgedTick;

            TimelinePruneContext(
                ulong acknowledgedTick,
                int expectedTrackedRoots,
                bool advanceAcknowledgement)
            {
                _acknowledgedTick = acknowledgedTick;
                _expectedTrackedRoots = expectedTrackedRoots;
                _advanceAcknowledgement = advanceAcknowledgement;
            }

            public static TimelinePruneContext Create(
                int affectedRoots,
                bool restoreDefault,
                bool prePruneStable)
            {
                var context = new TimelinePruneContext(
                    restoreDefault ? 2UL : 1UL,
                    restoreDefault ? 0 : affectedRoots,
                    prePruneStable);

                for (var i = 0; i < affectedRoots; i++)
                {
                    var root = new PredictedObjectID(checked((uint)(2 + i)));
                    context.timeline.SetVisible(1, root, false);
                }

                if (restoreDefault)
                {
                    for (var i = 0; i < affectedRoots; i++)
                    {
                        var root = new PredictedObjectID(checked((uint)(2 + i)));
                        context.timeline.SetVisible(2, root, true);
                    }
                }
                else if (prePruneStable)
                {
                    context.timeline.PruneThrough(1);
                }

                return context;
            }

            public void Prune()
            {
                if (_advanceAcknowledgement)
                    _acknowledgedTick++;

                timeline.PruneThrough(_acknowledgedTick);
                if (timeline.trackedRootCount != _expectedTrackedRoots)
                {
                    throw new InvalidOperationException(
                        $"Unexpected tracked visibility roots: " +
                        $"{timeline.trackedRootCount}/{_expectedTrackedRoots}.");
                }
            }

            public void Dispose()
            {
                timeline.Clear();
            }
        }
        enum ManagerVisibilityEventOperation
        {
            HideFrom,
            ShowTo,
            Acquire,
            Dispose
        }

        sealed class ManagerVisibilityEventContext : IDisposable
        {
            readonly GameObject _owner;
            readonly PredictionManager _manager;
            readonly PlayerID _player;
            readonly PredictedObjectID[] _affected;
            readonly IDisposable[] _handles;
            readonly ManagerVisibilityEventOperation _operation;

            PlayerVisibilityTimeline _timeline;
            ulong _tick;

            public int lastOperationCount { get; private set; }

            ManagerVisibilityEventContext(
                GameObject owner,
                PredictionManager manager,
                PlayerID player,
                PredictedObjectID[] affected,
                ManagerVisibilityEventOperation operation)
            {
                _owner = owner;
                _manager = manager;
                _player = player;
                _affected = affected;
                _handles = new IDisposable[affected.Length];
                _operation = operation;
            }

            public static ManagerVisibilityEventContext Create(
                int roots,
                int affectedRoots,
                ManagerVisibilityEventOperation operation)
            {
                if (affectedRoots <= 0 || affectedRoots > roots)
                    throw new ArgumentOutOfRangeException(nameof(affectedRoots));

                var owner = new GameObject("Prediction visibility event benchmark");
                var manager = owner.AddComponent<PredictionManager>();
                var affected = new PredictedObjectID[affectedRoots];
                for (var i = 0; i < affected.Length; i++)
                {
                    affected[i] = new PredictedObjectID(
                        checked((uint)(2 + i * roots / affectedRoots)));
                }

                var context = new ManagerVisibilityEventContext(
                    owner,
                    manager,
                    new PlayerID(new PackedULong(50_001), false),
                    affected,
                    operation);
                context.Initialize();
                return context;
            }

            void Initialize()
            {
                _timeline = Prepare();
                switch (_operation)
                {
                    case ManagerVisibilityEventOperation.HideFrom:
                        ValidateVisibility(expectedVisible: true);
                        break;
                    case ManagerVisibilityEventOperation.ShowTo:
                    case ManagerVisibilityEventOperation.Acquire:
                        HideAll();
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: false);
                        break;
                    case ManagerVisibilityEventOperation.Dispose:
                        HideAll();
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: false);
                        AcquireAll();
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: true);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            public void Step()
            {
                int completed;
                switch (_operation)
                {
                    case ManagerVisibilityEventOperation.HideFrom:
                        completed = HideAll();
                        break;
                    case ManagerVisibilityEventOperation.ShowTo:
                        completed = ShowAll();
                        break;
                    case ManagerVisibilityEventOperation.Acquire:
                        completed = AcquireAll();
                        break;
                    case ManagerVisibilityEventOperation.Dispose:
                        completed = DisposeAllHandles();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (completed != _affected.Length)
                {
                    throw new InvalidOperationException(
                        $"Unexpected visibility event count: " +
                        $"{completed}/{_affected.Length}.");
                }

                lastOperationCount = completed;
            }

            public void ResetAfterStep()
            {
                switch (_operation)
                {
                    case ManagerVisibilityEventOperation.HideFrom:
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: false);
                        RequireAll(ShowAll(), "show");
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: true);
                        break;
                    case ManagerVisibilityEventOperation.ShowTo:
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: true);
                        RequireAll(HideAll(), "hide");
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: false);
                        break;
                    case ManagerVisibilityEventOperation.Acquire:
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: true);
                        RequireAll(DisposeAllHandles(), "dispose");
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: false);
                        break;
                    case ManagerVisibilityEventOperation.Dispose:
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: false);
                        RequireAll(AcquireAll(), "acquire");
                        _timeline = Prepare();
                        ValidateVisibility(expectedVisible: true);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            PlayerVisibilityTimeline Prepare()
            {
                _tick++;
                return _manager.PreparePlayerVisibility(
                    _player,
                    _tick,
                    _tick - 1);
            }

            int HideAll()
            {
                int changed = 0;
                for (var i = 0; i < _affected.Length; i++)
                {
                    if (_manager.HideFrom(_player, _affected[i]))
                        changed++;
                }

                return changed;
            }

            int ShowAll()
            {
                int changed = 0;
                for (var i = 0; i < _affected.Length; i++)
                {
                    if (_manager.ShowTo(_player, _affected[i]))
                        changed++;
                }

                return changed;
            }

            int AcquireAll()
            {
                int acquired = 0;
                for (var i = 0; i < _handles.Length; i++)
                {
                    if (_handles[i] != null)
                        continue;

                    _handles[i] = _manager.AcquireVisibility(
                        _player,
                        _affected[i]);
                    acquired++;
                }

                return acquired;
            }

            int DisposeAllHandles()
            {
                int disposed = 0;
                for (var i = 0; i < _handles.Length; i++)
                {
                    var handle = _handles[i];
                    if (handle == null)
                        continue;

                    handle.Dispose();
                    _handles[i] = null;
                    disposed++;
                }

                return disposed;
            }

            void ValidateVisibility(bool expectedVisible)
            {
                for (var i = 0; i < _affected.Length; i++)
                {
                    bool actual = _timeline.IsVisible(_affected[i]);
                    if (actual == expectedVisible)
                        continue;

                    throw new InvalidOperationException(
                        $"Unexpected visibility for {_affected[i]}: " +
                        $"{actual}/{expectedVisible}.");
                }
            }

            void RequireAll(int actual, string operation)
            {
                if (actual != _affected.Length)
                {
                    throw new InvalidOperationException(
                        $"Unexpected {operation} reset count: " +
                        $"{actual}/{_affected.Length}.");
                }
            }

            public void Dispose()
            {
                DisposeAllHandles();
                _manager.RemovePlayerVisibility(_player);
                UnityEngine.Object.DestroyImmediate(_owner);
            }
        }

        sealed class HierarchyProjectionContext : IDisposable
        {
            readonly PredictedHierarchyState _source;
            readonly PlayerVisibilityTimeline _timeline;
            readonly int _expectedSpawned;
            readonly int _expectedDeletes;
            public int lastOutputCount { get; private set; }

            HierarchyProjectionContext(
                PredictedHierarchyState source,
                PlayerVisibilityTimeline timeline,
                int expectedSpawned,
                int expectedDeletes)
            {
                _source = source;
                _timeline = timeline;
                _expectedSpawned = expectedSpawned;
                _expectedDeletes = expectedDeletes;
            }

            public static HierarchyProjectionContext Create(
                int roots,
                int piecesPerRoot,
                int visiblePercent,
                int deleteCount)
            {
                var source = CreateHierarchyState(
                    roots,
                    piecesPerRoot,
                    deleteCount);
                var visible = CreateVisibleRoots(
                    roots,
                    piecesPerRoot,
                    visiblePercent);
                var timeline = new PlayerVisibilityTimeline();
                ApplyVisibilityExceptions(
                    timeline,
                        roots,
                        piecesPerRoot,
                        visible,
                        tick: 1);

                int expectedDeletes = 0;
                for (var i = 0; i < source.toDelete.Count; i++)
                {
                    if (timeline.IsVisible(source.toDelete[i]))
                        expectedDeletes++;
                }

                return new HierarchyProjectionContext(
                    source,
                    timeline,
                    visible.Count * piecesPerRoot,
                    expectedDeletes);
            }

            public void Project()
            {
                var projection = PredictedHierarchy.BuildVisibilityProjection(
                    _source,
                    _timeline,
                    1);
                try
                {
                    int spawned = projection.spawnedPrefabs.Count;
                    int deletes = projection.toDelete.Count;
                    if (spawned != _expectedSpawned ||
                        deletes != _expectedDeletes)
                    {
                        throw new InvalidOperationException(
                            $"Unexpected hierarchy projection: " +
                            $"{spawned}/{_expectedSpawned} spawned, " +
                            $"{deletes}/{_expectedDeletes} deletes.");
                    }

                    lastOutputCount = spawned + deletes;
                }
                finally
                {
                    projection.Dispose();
                }
            }

            public void Dispose()
            {
                _timeline.Clear();
                _source.Dispose();
            }
        }

        sealed class HierarchyPassThroughContext : IDisposable
        {
            readonly PredictedHierarchyState _source;
            readonly PlayerVisibilityTimeline _timeline = new();
            readonly int _expectedOutputCount;
            readonly bool _hasPendingDeletes;

            public int lastOutputCount { get; private set; }

            HierarchyPassThroughContext(
                PredictedHierarchyState source,
                int expectedOutputCount)
            {
                _source = source;
                _expectedOutputCount = expectedOutputCount;
            }

            public static HierarchyPassThroughContext Create(
                int roots,
                int piecesPerRoot)
            {
                return new HierarchyPassThroughContext(
                    CreateHierarchyState(roots, piecesPerRoot, deleteCount: 0),
                    checked(roots * piecesPerRoot));
            }

            public void Select()
            {
                PredictedHierarchyState selected;
                bool ownsSelection;
                if (_timeline.isPassThrough && !_hasPendingDeletes)
                {
                    selected = _source;
                    ownsSelection = false;
                }
                else
                {
                    selected = PredictedHierarchy.BuildVisibilityProjection(
                        _source,
                        _timeline,
                        1);
                    ownsSelection = true;
                }

                try
                {
                    int output = selected.spawnedPrefabs.Count +
                                 selected.toDelete.Count;
                    if (output != _expectedOutputCount)
                    {
                        throw new InvalidOperationException(
                            $"Unexpected pass-through hierarchy count: " +
                            $"{output}/{_expectedOutputCount}.");
                    }

                    lastOutputCount = output;
                }
                finally
                {
                    if (ownsSelection)
                        selected.Dispose();
                }
            }

            public void Dispose()
            {
                _timeline.Clear();
                _source.Dispose();
            }
        }

        sealed class MultiPlayerHierarchyContext : IDisposable
        {
            readonly PredictedHierarchyState _source;
            readonly PlayerVisibilityTimeline[] _timelines;
            readonly int _expectedOutputCount;
            public int lastOutputCount { get; private set; }

            MultiPlayerHierarchyContext(
                PredictedHierarchyState source,
                PlayerVisibilityTimeline[] timelines,
                int expectedOutputCount)
            {
                _source = source;
                _timelines = timelines;
                _expectedOutputCount = expectedOutputCount;
            }

            public static MultiPlayerHierarchyContext Create(
                int roots,
                int piecesPerRoot,
                int players)
            {
                var source = CreateHierarchyState(
                    roots,
                    piecesPerRoot,
                    deleteCount: 0);
                var timelines = new PlayerVisibilityTimeline[players];
                int expected = 0;

                for (var player = 0; player < players; player++)
                {
                    var visible = CreateVisibleRoots(
                        roots,
                        piecesPerRoot,
                        visiblePercent: 25,
                        offset: player);
                    var timeline = new PlayerVisibilityTimeline();
                    ApplyVisibilityExceptions(
                    timeline,
                        roots,
                        piecesPerRoot,
                        visible,
                        tick: 1);
                    timelines[player] = timeline;
                    expected += visible.Count * piecesPerRoot;
                }

                return new MultiPlayerHierarchyContext(
                    source,
                    timelines,
                    expected);
            }

            public void ProjectAll()
            {
                int output = 0;
                for (var player = 0; player < _timelines.Length; player++)
                {
                    var projection = PredictedHierarchy.BuildVisibilityProjection(
                        _source,
                        _timelines[player],
                        1);
                    try
                    {
                        output += projection.spawnedPrefabs.Count;
                    }
                    finally
                    {
                        projection.Dispose();
                    }
                }

                if (output != _expectedOutputCount)
                {
                    throw new InvalidOperationException(
                        $"Unexpected multi-player projection count: " +
                        $"{output}/{_expectedOutputCount}.");
                }

                lastOutputCount = output;
            }

            public void Dispose()
            {
                for (var i = 0; i < _timelines.Length; i++)
                    _timelines[i].Clear();
                _source.Dispose();
            }
        }

        sealed class BaselineMembershipContext : IDisposable
        {
            readonly PredictedHierarchyState _source;
            readonly PredictedObjectID[] _queries;
            readonly HashSet<PredictedObjectID> _rootIndex = new();
            public int lastMatches { get; private set; }

            BaselineMembershipContext(
                PredictedHierarchyState source,
                PredictedObjectID[] queries)
            {
                _source = source;
                _queries = queries;
            }

            public static BaselineMembershipContext Create(
                int roots,
                int piecesPerRoot)
            {
                var queries = new PredictedObjectID[roots];
                for (var rootIndex = 0; rootIndex < roots; rootIndex++)
                {
                    queries[rootIndex] = new PredictedObjectID(
                        checked((uint)(2 + rootIndex * piecesPerRoot)));
                }

                return new BaselineMembershipContext(
                    CreateHierarchyState(roots, piecesPerRoot, 0),
                    queries);
            }

            public void CheckLinear()
            {
                int matches = 0;
                for (var i = 0; i < _queries.Length; i++)
                {
                    if (PredictedHierarchy.StateContainsRoot(
                            _source,
                            _queries[i]))
                    {
                        matches++;
                    }
                }

                Validate(matches);
            }

            public void CheckIndexed()
            {
                _rootIndex.Clear();
                for (var i = 0; i < _source.spawnedPrefabs.Count; i++)
                    _rootIndex.Add(_source.spawnedPrefabs[i].rootId);

                int matches = 0;
                for (var i = 0; i < _queries.Length; i++)
                {
                    if (_rootIndex.Contains(_queries[i]))
                        matches++;
                }

                Validate(matches);
            }

            void Validate(int matches)
            {
                if (matches != _queries.Length)
                {
                    throw new InvalidOperationException(
                        $"Unexpected baseline membership count: " +
                        $"{matches}/{_queries.Length}.");
                }

                lastMatches = matches;
            }

            public void Dispose()
            {
                _source.Dispose();
                _rootIndex.Clear();
            }
        }

        sealed class AddressedStateOmissionContext : IDisposable
        {
            readonly BitPacker _destination;
            readonly BitPacker _records;
            readonly BitPacker _payload;
            readonly PredictedComponentID[] _ids;
            readonly bool[] _changed;
            readonly int _expectedOutputCount;

            public int lastOutputCount { get; private set; }
            public int serializedBits { get; private set; }

            AddressedStateOmissionContext(
                BitPacker destination,
                BitPacker records,
                BitPacker payload,
                PredictedComponentID[] ids,
                bool[] changed,
                int expectedOutputCount)
            {
                _destination = destination;
                _records = records;
                _payload = payload;
                _ids = ids;
                _changed = changed;
                _expectedOutputCount = expectedOutputCount;
            }

            public static AddressedStateOmissionContext Create(
                int candidateCount,
                int payloadBits,
                int changedPercent)
            {
                if (candidateCount <= 0)
                    throw new ArgumentOutOfRangeException(nameof(candidateCount));
                if (changedPercent < 0 || changedPercent > 100)
                    throw new ArgumentOutOfRangeException(nameof(changedPercent));

                var ids = new PredictedComponentID[candidateCount];
                for (var i = 0; i < ids.Length; i++)
                {
                    ids[i] = new PredictedComponentID(
                        new PredictedObjectID(checked((uint)(2 + i * 17))),
                        (uint)(i & 3));
                }

                int changedCount = candidateCount * changedPercent / 100;
                var changed = new bool[candidateCount];
                for (var i = 0; i < changedCount; i++)
                    changed[i * candidateCount / changedCount] = true;

                var payload = BitPackerPool.Get();
                FillPayload(payload, payloadBits);
                return new AddressedStateOmissionContext(
                    BitPackerPool.Get(),
                    BitPackerPool.Get(),
                    payload,
                    ids,
                    changed,
                    changedCount);
            }

            public void WriteBatch()
            {
                _records.ResetPositionAndMode(false);
                int writtenCount = 0;

                for (var i = 0; i < _ids.Length; i++)
                {
                    if (!_changed[i])
                        continue;

                    AddressedPredictionRecords.WriteRecord(
                        _records,
                        _ids[i],
                        isFullState: false,
                        _payload);
                    writtenCount++;
                }

                _destination.ResetPositionAndMode(false);
                AddressedPredictionRecords.WriteSectionCount(
                    writtenCount,
                    _destination);
                _destination.WriteBitsWithoutConsumingIt(
                    _records,
                    _records.positionInBits);

                if (writtenCount != _expectedOutputCount)
                {
                    throw new InvalidOperationException(
                        $"Unexpected omitted state count: " +
                        $"{writtenCount}/{_expectedOutputCount}.");
                }

                lastOutputCount = writtenCount;
                serializedBits = _destination.positionInBits;
            }

            public void Dispose()
            {
                _payload.Dispose();
                _records.Dispose();
                _destination.Dispose();
            }
        }

        sealed class AddressedWriteContext : IDisposable
        {
            readonly BitPacker _destination;
            readonly BitPacker _payload;
            readonly PredictedComponentID[] _ids;
            public BitPacker destination => _destination;
            public int serializedBits { get; private set; }

            AddressedWriteContext(
                BitPacker destination,
                BitPacker payload,
                PredictedComponentID[] ids)
            {
                _destination = destination;
                _payload = payload;
                _ids = ids;
            }

            public static AddressedWriteContext Create(
                int recordCount,
                int payloadBits)
            {
                var ids = new PredictedComponentID[recordCount];
                for (var i = 0; i < ids.Length; i++)
                {
                    ids[i] = new PredictedComponentID(
                        new PredictedObjectID(checked((uint)(2 + i * 17))),
                        (uint)(i & 3));
                }

                var destination = BitPackerPool.Get();
                var payload = BitPackerPool.Get();
                FillPayload(payload, payloadBits);
                return new AddressedWriteContext(destination, payload, ids);
            }

            public void WriteBatch()
            {
                _destination.ResetPositionAndMode(false);
                AddressedPredictionRecords.WriteSectionCount(
                    _ids.Length,
                    _destination);

                for (var i = 0; i < _ids.Length; i++)
                {
                    AddressedPredictionRecords.WriteRecord(
                        _destination,
                        _ids[i],
                        isFullState: (i & 1) == 0,
                        _payload);
                }

                serializedBits = _destination.positionInBits;
            }

            public void Dispose()
            {
                _payload.Dispose();
                _destination.Dispose();
            }
        }

        sealed class AddressedReadContext : IDisposable
        {
            readonly BitPacker _source;
            readonly AddressedPredictionRecords.ReadRecord _reader;
            int _recordCount;
            public int lastRecordCount { get; private set; }
            public int serializedBits { get; }

            AddressedReadContext(
                BitPacker source,
                bool skipPayload,
                int serializedBits)
            {
                _source = source;
                this.serializedBits = serializedBits;
                _reader = skipPayload ? Skip : Consume;
            }

            public static AddressedReadContext Create(
                int recordCount,
                int payloadBits,
                bool skipPayload)
            {
                var writer = AddressedWriteContext.Create(
                    recordCount,
                    payloadBits);
                try
                {
                    writer.WriteBatch();
                    var source = BitPackerPool.Get();
                    source.WriteBitsWithoutConsumingIt(
                        writer.destination,
                        writer.serializedBits);
                    return new AddressedReadContext(
                        source,
                        skipPayload,
                        writer.serializedBits);
                }
                finally
                {
                    writer.Dispose();
                }
            }

            public void ReadBatch()
            {
                _source.ResetPositionAndMode(true);
                _recordCount = 0;
                AddressedPredictionRecords.ReadSection(_reader, _source);
                lastRecordCount = _recordCount;
            }

            void Consume(
                PredictedComponentID _,
                bool __,
                BitPacker payload,
                int payloadBitCount)
            {
                payload.AdvanceBits(payloadBitCount);
                _recordCount++;
            }

            void Skip(
                PredictedComponentID _,
                bool __,
                BitPacker ___,
                int ____)
            {
                _recordCount++;
            }

            public void Dispose()
            {
                _source.Dispose();
            }
        }

#if UNITY_PHYSICS_3D
        sealed class Physics3DProjectionContext : IDisposable
        {
            readonly PredictedPhysicsData _source;
            readonly HashSet<PredictedObjectID> _hidden;
            readonly int _expected;
            public int lastOutputCount { get; private set; }

            Physics3DProjectionContext(
                PredictedPhysicsData source,
                HashSet<PredictedObjectID> hidden,
                int expected)
            {
                _source = source;
                _hidden = hidden;
                _expected = expected;
            }

            // Projection filters by the hidden set, so anything the predicted hierarchy does not
            // own stays visible. Model that by hiding the complement of the retained events.
            public static Physics3DProjectionContext Create(
                int eventCount,
                int retainedPercent)
            {
                int retained = eventCount * retainedPercent / 100;
                var hidden = new HashSet<PredictedObjectID>();
                var source = new PredictedPhysicsData
                {
                    events = DisposableList<PhysicsEvent>.Create(eventCount)
                };

                for (var i = 0; i < eventCount; i++)
                {
                    var me = new PredictedObjectID(checked((uint)(2 + i)));
                    if (i >= retained)
                        hidden.Add(me);

                    source.events.Add(new PhysicsEvent
                    {
                        me = new PredictedComponentID(me, 0),
                        other = new PredictedComponentID(me, 1)
                    });
                }

                return new Physics3DProjectionContext(
                    source,
                    hidden,
                    retained);
            }

            public void Project()
            {
                var projection = PredictionPhysicsVisibility.Project(
                    _source,
                    _hidden);
                try
                {
                    int count = projection.events.Count;
                    if (count != _expected)
                    {
                        throw new InvalidOperationException(
                            $"Unexpected 3D physics projection: " +
                            $"{count}/{_expected}.");
                    }

                    lastOutputCount = count;
                }
                finally
                {
                    projection.Dispose();
                }
            }

            public void Dispose()
            {
                _source.Dispose();
                _hidden.Clear();
            }
        }
#endif

#if UNITY_PHYSICS_2D
        sealed class Physics2DProjectionContext : IDisposable
        {
            readonly PredictedPhysics2DData _source;
            readonly HashSet<PredictedObjectID> _hidden;
            readonly int _expected;
            public int lastOutputCount { get; private set; }

            Physics2DProjectionContext(
                PredictedPhysics2DData source,
                HashSet<PredictedObjectID> hidden,
                int expected)
            {
                _source = source;
                _hidden = hidden;
                _expected = expected;
            }

            // Projection filters by the hidden set, so anything the predicted hierarchy does not
            // own stays visible. Model that by hiding the complement of the retained events.
            public static Physics2DProjectionContext Create(
                int eventCount,
                int retainedPercent)
            {
                int retained = eventCount * retainedPercent / 100;
                var hidden = new HashSet<PredictedObjectID>();
                var source = new PredictedPhysics2DData
                {
                    events = DisposableList<Physics2DEvent>.Create(eventCount)
                };

                for (var i = 0; i < eventCount; i++)
                {
                    var me = new PredictedObjectID(checked((uint)(2 + i)));
                    if (i >= retained)
                        hidden.Add(me);

                    source.events.Add(new Physics2DEvent
                    {
                        me = new PredictedComponentID(me, 0),
                        other = new PredictedComponentID(me, 1)
                    });
                }

                return new Physics2DProjectionContext(
                    source,
                    hidden,
                    retained);
            }

            public void Project()
            {
                var projection = PredictionPhysicsVisibility.Project(
                    _source,
                    _hidden);
                try
                {
                    int count = projection.events.Count;
                    if (count != _expected)
                    {
                        throw new InvalidOperationException(
                            $"Unexpected 2D physics projection: " +
                            $"{count}/{_expected}.");
                    }

                    lastOutputCount = count;
                }
                finally
                {
                    projection.Dispose();
                }
            }

            public void Dispose()
            {
                _source.Dispose();
                _hidden.Clear();
            }
        }
#endif

        [Serializable]
        public sealed class VisibilityBenchmarkReport
        {
            public string generatedAtUtc;
            public string unityVersion;
            public string projectPath;
            public string outputDirectory;
            public string jsonPath;
            public string markdownPath;
            public List<string> warnings = new();
            public List<VisibilityOperationResult> operations = new();
        }

        [Serializable]
        public sealed class VisibilityOperationResult
        {
            public string category;
            public string name;
            public int roots;
            public int piecesPerRoot;
            public int players;
            public int visiblePercent;
            public int churnPercent;
            public int deletes;
            public int addressedRecords;
            public int payloadBits;
            public int physicsEvents;
            public int sourceRecords;
            public int iterations;
            public int samples;
            public double minNanoseconds;
            public double medianNanoseconds;
            public double maxNanoseconds;
            public double medianAllocatedBytes;
            public double nanosecondsPerSourceRecord;
            public long outputCount;
            public long serializedBits;
            public string note;
        }
    }
}
