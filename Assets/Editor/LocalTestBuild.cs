using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class LocalTestBuild
{
    public static void BuildWindows()
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/PredictionTests/Bootstrap.unity" },
            locationPathName = "build/StandaloneWindows64/PurrDictionTests.exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        var report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"Build failed: {report.summary.result}, errors={report.summary.totalErrors}");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"Build succeeded: {report.summary.outputPath} ({report.summary.totalSize} bytes)");
        EditorApplication.Exit(0);
    }
}
