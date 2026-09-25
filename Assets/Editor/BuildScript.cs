using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Command-line and menu entry point for the Windows standalone build.</summary>
public static class BuildScript
{
    const string OutputDir = "Builds/Windows";
    const string ExeName = "KartParty.exe";

    [MenuItem("Tools/Kart/Build Windows Player")]
    public static void BuildWindows()
    {
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/Race.unity" };

        PlayerSettings.productName = "Kart Party";
        // The phone server must keep running while the window is unfocused.
        PlayerSettings.runInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
        PlayerSettings.resizableWindow = true;

        Directory.CreateDirectory(OutputDir);
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine(OutputDir, ExeName),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        Debug.Log($"[BuildScript] Result: {summary.result}, size: {summary.totalSize / (1024 * 1024)} MB, " +
                  $"errors: {summary.totalErrors}, output: {summary.outputPath}");

        if (Application.isBatchMode)
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
