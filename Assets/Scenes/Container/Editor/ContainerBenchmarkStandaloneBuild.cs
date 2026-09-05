using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class ContainerBenchmarkStandaloneBuild
{
    private const string ScenePath = "Assets/Scenes/Container.unity";
    private const string ProductName = "UnityContainerBenchmark";

    [MenuItem("Container/Build/Release Timing Player")]
    public static void BuildReleaseFromMenu()
    {
        BuildPlayer(Path.GetFullPath("Build/Release/UnityContainerBenchmark.exe"), false);
    }

    [MenuItem("Container/Build/Development GC Player")]
    public static void BuildDevelopmentFromMenu()
    {
        BuildPlayer(Path.GetFullPath("Build/Development/UnityContainerBenchmark.exe"), true);
    }

    public static void BuildFromCommandLine()
    {
        string[] args = Environment.GetCommandLineArgs();
        string output = ReadRequiredOption(args, "containerBuildOutput");
        string channel = ReadRequiredOption(args, "containerBuildChannel");
        bool development;
        if (string.Equals(channel, "ReleaseTiming", StringComparison.Ordinal))
            development = false;
        else if (string.Equals(channel, "DevelopmentGc", StringComparison.Ordinal))
            development = true;
        else
            throw new ArgumentException(
                "-containerBuildChannel must be ReleaseTiming or DevelopmentGc.");

        BuildPlayer(Path.GetFullPath(output), development);
    }

    private static void BuildPlayer(string output, bool development)
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            throw new FileNotFoundException("Container benchmark scene is missing.", ScenePath);
        if (!string.Equals(Path.GetExtension(output), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Build output must end with .exe.", nameof(output));

        string outputDirectory = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Build output has no directory.", nameof(output));
        Directory.CreateDirectory(outputDirectory);

        NamedBuildTarget standalone = NamedBuildTarget.Standalone;
        ScriptingImplementation oldBackend = PlayerSettings.GetScriptingBackend(standalone);
        string oldProductName = PlayerSettings.productName;
        string oldCompanyName = PlayerSettings.companyName;
        bool oldRunInBackground = PlayerSettings.runInBackground;
        EditorBuildSettingsScene[] oldScenes = EditorBuildSettings.scenes;

        try
        {
            PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.IL2CPP);
            PlayerSettings.productName = ProductName;
            PlayerSettings.companyName = "Counull";
            PlayerSettings.runInBackground = true;
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            BuildOptions options = BuildOptions.StrictMode | BuildOptions.DetailedBuildReport;
            if (development)
                options |= BuildOptions.Development;

            string channel = development ? "DevelopmentGc" : "ReleaseTiming";
            Debug.Log($"[ContainerBenchmarkBuild] START channel={channel} scene={ScenePath} output={output}");
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = options,
            });

            BuildSummary summary = report.summary;
            if (summary.result != BuildResult.Succeeded || summary.totalErrors != 0)
            {
                throw new InvalidOperationException(
                    $"Container benchmark build failed: result={summary.result}, errors={summary.totalErrors}.");
            }

            Debug.Log(
                $"[ContainerBenchmarkBuild] COMPLETE channel={channel} size={summary.totalSize} "
                + $"duration={summary.totalTime} guid={summary.guid} output={output}");
        }
        finally
        {
            PlayerSettings.SetScriptingBackend(standalone, oldBackend);
            PlayerSettings.productName = oldProductName;
            PlayerSettings.companyName = oldCompanyName;
            PlayerSettings.runInBackground = oldRunInBackground;
            EditorBuildSettings.scenes = oldScenes;
        }
    }

    private static string ReadRequiredOption(string[] args, string option)
    {
        string expected = "-" + option;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], expected, StringComparison.Ordinal))
                return args[i + 1];
        }
        throw new ArgumentException($"Missing required option {expected}.");
    }
}
