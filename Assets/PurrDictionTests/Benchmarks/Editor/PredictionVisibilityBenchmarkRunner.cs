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
            AddTimelineBenchmark(report, 4096, 25, 0, 256);
            AddTimelineBenchmark(report, 4096, 25, 10, 128);
        }

        static void AddTimelineBenchmark(
            VisibilityBenchmarkReport report,
            int roots,
            int visiblePercent,
            int churnPercent,
            int iterations)
        {
            string mode = churnPercent == 0 ? "Stable" : $"Churn{churnPercent}Percent";
            var specification = new BenchmarkSpecification
            {
                category = "Timeline",
                name = $"RecordAndPrune.{mode}",
                roots = roots,
                visiblePercent = visiblePercent,
                churnPercent = churnPercent,
                iterations = iterations,
                sourceRecords = roots
            };

            report.operations.Add(Measure(
                specification,
                () => TimelineContext.Create(roots, visiblePercent, churnPercent),
                (context, _) => context.Step(),
                context => new BenchmarkObservation(context.timeline.current.Count, -1),
                context => context.Dispose()));
        }

        static void AddHierarchyBenchmarks(VisibilityBenchmarkReport report)
        {
            AddHierarchyBenchmark(report, 1024, 4, 100, 0, 64);
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

                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    long started = Stopwatch.GetTimestamp();

                    for (var i = 0; i < specification.iterations; i++)
                        body(context, i);

                    long elapsed = Stopwatch.GetTimestamp() - started;
                    long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

                    timings[sample] =
                        elapsed * 1_000_000_000.0 /
                        Stopwatch.Frequency /
                        specification.iterations;
                    allocations[sample] =
                        (allocatedAfter - allocatedBefore) /
                        (double)specification.iterations;
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
                $"{FormatNumber(result.medianAllocatedBytes)} B/op.");
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
                    .Append(FormatNumber(operation.medianAllocatedBytes))
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
            readonly HashSet<PredictedObjectID> _desiredA;
            readonly HashSet<PredictedObjectID> _desiredB;
            readonly bool _churn;
            ulong _tick = 1;

            TimelineContext(
                HashSet<PredictedObjectID> desiredA,
                HashSet<PredictedObjectID> desiredB,
                bool churn)
            {
                _desiredA = desiredA;
                _desiredB = desiredB;
                _churn = churn;
                timeline.Record(_tick, _desiredA);
            }

            public static TimelineContext Create(
                int roots,
                int visiblePercent,
                int churnPercent)
            {
                var desiredA = CreateVisibleRoots(
                    roots,
                    piecesPerRoot: 1,
                    visiblePercent);
                var desiredB = new HashSet<PredictedObjectID>(desiredA);

                int churnCount = roots * churnPercent / 100;
                var ordered = new List<PredictedObjectID>(desiredB);
                for (var i = 0; i < churnCount && i < ordered.Count; i++)
                    desiredB.Remove(ordered[i]);

                return new TimelineContext(
                    desiredA,
                    desiredB,
                    churnPercent > 0);
            }

            public void Step()
            {
                _tick++;
                var desired = _churn && (_tick & 1UL) == 0
                    ? _desiredB
                    : _desiredA;
                timeline.Record(_tick, desired);
                timeline.PruneThrough(_tick - 2);
            }

            public void Dispose()
            {
                timeline.Clear();
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
                timeline.Record(1, visible);

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
                    timeline.Record(1, visible);
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
            readonly HashSet<PredictedObjectID> _visible;
            readonly int _expected;
            public int lastOutputCount { get; private set; }

            Physics3DProjectionContext(
                PredictedPhysicsData source,
                HashSet<PredictedObjectID> visible,
                int expected)
            {
                _source = source;
                _visible = visible;
                _expected = expected;
            }

            public static Physics3DProjectionContext Create(
                int eventCount,
                int retainedPercent)
            {
                int retained = eventCount * retainedPercent / 100;
                var visible = new HashSet<PredictedObjectID>();
                var source = new PredictedPhysicsData
                {
                    events = DisposableList<PhysicsEvent>.Create(eventCount)
                };

                for (var i = 0; i < retained; i++)
                    visible.Add(new PredictedObjectID(checked((uint)(2 + i))));

                for (var i = 0; i < eventCount; i++)
                {
                    var me = i < retained
                        ? new PredictedObjectID(checked((uint)(2 + i)))
                        : new PredictedObjectID(checked((uint)(100_000 + i)));
                    source.events.Add(new PhysicsEvent
                    {
                        me = new PredictedComponentID(me, 0),
                        other = new PredictedComponentID(me, 1)
                    });
                }

                return new Physics3DProjectionContext(
                    source,
                    visible,
                    retained);
            }

            public void Project()
            {
                var projection = PredictionPhysicsVisibility.Project(
                    _source,
                    _visible);
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
                _visible.Clear();
            }
        }
#endif

#if UNITY_PHYSICS_2D
        sealed class Physics2DProjectionContext : IDisposable
        {
            readonly PredictedPhysics2DData _source;
            readonly HashSet<PredictedObjectID> _visible;
            readonly int _expected;
            public int lastOutputCount { get; private set; }

            Physics2DProjectionContext(
                PredictedPhysics2DData source,
                HashSet<PredictedObjectID> visible,
                int expected)
            {
                _source = source;
                _visible = visible;
                _expected = expected;
            }

            public static Physics2DProjectionContext Create(
                int eventCount,
                int retainedPercent)
            {
                int retained = eventCount * retainedPercent / 100;
                var visible = new HashSet<PredictedObjectID>();
                var source = new PredictedPhysics2DData
                {
                    events = DisposableList<Physics2DEvent>.Create(eventCount)
                };

                for (var i = 0; i < retained; i++)
                    visible.Add(new PredictedObjectID(checked((uint)(2 + i))));

                for (var i = 0; i < eventCount; i++)
                {
                    var me = i < retained
                        ? new PredictedObjectID(checked((uint)(2 + i)))
                        : new PredictedObjectID(checked((uint)(100_000 + i)));
                    source.events.Add(new Physics2DEvent
                    {
                        me = new PredictedComponentID(me, 0),
                        other = new PredictedComponentID(me, 1)
                    });
                }

                return new Physics2DProjectionContext(
                    source,
                    visible,
                    retained);
            }

            public void Project()
            {
                var projection = PredictionPhysicsVisibility.Project(
                    _source,
                    _visible);
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
                _visible.Clear();
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
