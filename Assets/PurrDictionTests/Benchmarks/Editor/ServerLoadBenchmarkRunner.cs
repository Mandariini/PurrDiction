using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PurrNet.Prediction.Benchmarks.Editor
{
    /// <summary>
    /// Builds the PredictionTests player once and runs the ServerLoadBenchmarkScenario
    /// across a sweep of simulated latencies, collecting the server's frame-write marker
    /// stats per latency so ping-dependent server cost can be measured and compared.
    /// </summary>
    public static class ServerLoadBenchmarkRunner
    {
        private const string BootstrapScene = "Assets/PredictionTests/Bootstrap.unity";
        private const string DefaultOutputDirectory = "Builds/ServerLoadBenchmark";
        private const string DefaultLatencies = "0,50,100,200";
        private const int DefaultClientCount = 3;
        private const int DefaultObjects = 200;
        private const int DefaultSeconds = 20;
        private const int DefaultRunTimeoutSeconds = 420;

        [MenuItem("Tools/PurrDiction/Analysis/Run Server Load Latency Sweep", false, -86)]
        public static void RunFromMenu()
        {
            RunInternal(exitEditor: false);
        }

        public static void RunFromCommandLine()
        {
            RunInternal(exitEditor: true);
        }

        private static void RunInternal(bool exitEditor)
        {
            var exitCode = 0;

            try
            {
                var options = Options.FromCommandLine();
                var success = Run(options);
                exitCode = success ? 0 : -1;
            }
            catch (Exception e)
            {
                exitCode = -1;
                Debug.LogException(e);
            }
            finally
            {
                if (exitEditor || Application.isBatchMode)
                    EditorApplication.Exit(exitCode);
            }
        }

        private static bool Run(Options options)
        {
            Directory.CreateDirectory(options.outputDirectory);

            var playerPath = options.playerPath;
            if (string.IsNullOrEmpty(playerPath))
                playerPath = Path.Combine(options.outputDirectory, "Player", "PurrDictionTests.exe");

            if (!options.skipBuild)
                BuildPlayer(playerPath);

            var rows = new List<SweepRow>();
            var success = true;

            for (var i = 0; i < options.latenciesMs.Length; i++)
            {
                var latency = options.latenciesMs[i];
                Debug.Log($"[ServerLoadBenchmark] Running latency {latency}ms ({i + 1}/{options.latenciesMs.Length})");

                var row = RunOneLatency(playerPath, options, latency, i);
                rows.Add(row);
                success &= row.success;
            }

            WriteReport(options, rows);
            return success;
        }

        private static void BuildPlayer(string playerPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(playerPath) ?? ".");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { BootstrapScene },
                locationPathName = playerPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            });

            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Server load benchmark player build failed: {report.summary.result} ({report.summary.totalErrors} errors)");
            }
        }

        private static SweepRow RunOneLatency(string playerPath, Options options, int latencyMs, int runIndex)
        {
            var expectedConnections = options.clientCount + 1;
            var port = options.basePort + runIndex;
            var runDir = Path.Combine(options.outputDirectory, $"latency-{latencyMs}");
            Directory.CreateDirectory(runDir);

            var processes = new List<(Process process, string role, string resultsPath)>();

            var sharedArgs =
                "-batchmode -nographics -serverLoadBenchmark " +
                $"-count {expectedConnections} -port {port} -connectTimeout 180 " +
                $"-benchObjects {options.objects} -benchSeconds {options.seconds} " +
                $"-benchInputEvery {options.inputEvery} " +
                $"-latencyMin {latencyMs} -latencyMax {latencyMs} " +
                (options.packetLoss > 0 ? $"-packetLoss {options.packetLoss} " : "");

            processes.Add(StartPlayer(playerPath, runDir, "host", sharedArgs + "-role host"));
            Thread.Sleep(1000);

            for (var i = 0; i < options.clientCount; i++)
                processes.Add(StartPlayer(playerPath, runDir, $"client-{i + 1}", sharedArgs + "-role client -serverHost 127.0.0.1"));

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < options.runTimeoutSeconds)
            {
                var allExited = true;
                foreach (var entry in processes)
                {
                    if (!entry.process.HasExited)
                    {
                        allExited = false;
                        break;
                    }
                }

                if (allExited)
                    break;

                Thread.Sleep(250);
            }

            var row = new SweepRow { latencyMs = latencyMs, success = true };

            foreach (var entry in processes)
            {
                if (!entry.process.HasExited)
                {
                    try { entry.process.Kill(); }
                    catch (Exception e) { Debug.LogWarning($"Failed to kill {entry.role}: {e.Message}"); }
                    row.success = false;
                    row.failure = $"{entry.role} timed out";
                }
                else if (entry.process.ExitCode != 0)
                {
                    row.success = false;
                    row.failure = $"{entry.role} exited with {entry.process.ExitCode}";
                }

                entry.process.Dispose();
            }

            var hostResults = processes[0].resultsPath;
            row.benchMessage = ExtractBenchMessage(hostResults);
            if (row.benchMessage == null && row.success)
            {
                row.success = false;
                row.failure = "host results missing bench message";
            }

            return row;
        }

        private static (Process, string, string) StartPlayer(string playerPath, string runDir, string stem, string roleArguments)
        {
            var resultsPath = Path.GetFullPath(Path.Combine(runDir, $"{stem}.json"));
            var logPath = Path.GetFullPath(Path.Combine(runDir, $"{stem}.log"));

            var arguments =
                $"{roleArguments} " +
                $"-results \"{resultsPath}\" " +
                $"-logFile \"{logPath}\"";

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = playerPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Directory.GetCurrentDirectory()
            });

            if (process == null)
                throw new InvalidOperationException($"Failed to start benchmark player '{playerPath}'.");

            return (process, stem, resultsPath);
        }

        private static string ExtractBenchMessage(string resultsPath)
        {
            if (!File.Exists(resultsPath))
                return null;

            var json = File.ReadAllText(resultsPath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var wrapped = JsonUtility.FromJson<ScenarioDetailsArrayDto>("{\"items\":" + json + "}");
            if (wrapped.items == null)
                return null;

            foreach (var item in wrapped.items)
            {
                if (item.name == "ServerLoadBenchmarkScenario" && item.result.success)
                    return item.result.message;
            }

            return null;
        }

        private static void WriteReport(Options options, List<SweepRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Server Load Latency Sweep");
            sb.AppendLine();
            sb.AppendLine($"Generated: `{DateTime.UtcNow:O}`");
            sb.AppendLine($"Unity: `{Application.unityVersion}`");
            sb.AppendLine($"Clients: `{options.clientCount}` Objects: `{options.objects}` Seconds: `{options.seconds}` PacketLoss: `{options.packetLoss}%`");
            sb.AppendLine();

            foreach (var row in rows)
            {
                sb.AppendLine($"## {row.latencyMs} ms");
                sb.AppendLine();
                if (!row.success)
                    sb.AppendLine($"FAILED: {row.failure}");
                if (row.benchMessage != null)
                    sb.AppendLine($"```\n{row.benchMessage.Replace(" | ", "\n")}\n```");
                sb.AppendLine();
            }

            var reportPath = Path.Combine(options.outputDirectory, "server-load-sweep.md");
            File.WriteAllText(reportPath, sb.ToString());
            Debug.Log($"[ServerLoadBenchmark] Report written to {reportPath}\n{sb}");
        }

        private static string GetArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        private static bool HasArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static int GetIntArgument(string name, int fallback)
        {
            var value = GetArgument(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : fallback;
        }

        private sealed class Options
        {
            public string outputDirectory;
            public string playerPath;
            public bool skipBuild;
            public int clientCount;
            public int objects;
            public int inputEvery;
            public int seconds;
            public int packetLoss;
            public int basePort;
            public int runTimeoutSeconds;
            public int[] latenciesMs;

            public static Options FromCommandLine()
            {
                var latenciesRaw = GetArgument("-slbLatencies") ?? DefaultLatencies;
                var parts = latenciesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var latencies = new List<int>();
                foreach (var part in parts)
                {
                    if (int.TryParse(part.Trim(), out var parsed) && parsed >= 0)
                        latencies.Add(parsed);
                }

                if (latencies.Count == 0)
                    latencies.AddRange(new[] { 0, 50, 100, 200 });

                var output = GetArgument("-slbOutput") ?? DefaultOutputDirectory;
                if (!Path.IsPathRooted(output))
                    output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", output));

                var player = GetArgument("-slbPlayer");
                if (!string.IsNullOrEmpty(player) && !Path.IsPathRooted(player))
                    player = Path.GetFullPath(Path.Combine(Application.dataPath, "..", player));

                return new Options
                {
                    outputDirectory = output,
                    playerPath = player,
                    skipBuild = HasArgument("-slbSkipBuild"),
                    clientCount = Math.Max(1, GetIntArgument("-slbClients", DefaultClientCount)),
                    objects = Math.Max(1, GetIntArgument("-slbObjects", DefaultObjects)),
                    inputEvery = Math.Max(0, GetIntArgument("-slbInputEvery", 1)),
                    seconds = Math.Max(5, GetIntArgument("-slbSeconds", DefaultSeconds)),
                    packetLoss = Math.Max(0, GetIntArgument("-slbPacketLoss", 0)),
                    basePort = GetIntArgument("-slbBasePort", new System.Random().Next(24000, 32000)),
                    runTimeoutSeconds = Math.Max(60, GetIntArgument("-slbRunTimeout", DefaultRunTimeoutSeconds)),
                    latenciesMs = latencies.ToArray()
                };
            }
        }

        private sealed class SweepRow
        {
            public int latencyMs;
            public bool success;
            public string failure;
            public string benchMessage;
        }

        [Serializable]
        private sealed class ScenarioDetailsArrayDto
        {
            public ScenarioDetailsDto[] items;
        }

        [Serializable]
        private struct ScenarioDetailsDto
        {
            public string name;
            public ScenarioResultDto result;
        }

        [Serializable]
        private struct ScenarioResultDto
        {
            public bool success;
            public string message;
        }
    }
}
