using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ContainerBenchmark;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditorInternal;
using UnityEngine;
using PackageManagerInfo = UnityEditor.PackageManager.PackageInfo;
using PackageManagerSource = UnityEditor.PackageManager.PackageSource;

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
        ValidateEvidenceEnvironment();
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
                extraScriptingDefines = new[]
                {
                    BenchmarkEnvironmentContract.VerifiedScriptingDefine,
                },
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

    private static void ValidateEvidenceEnvironment()
    {
        string fullUnityVersion = InternalEditorUtility.GetFullUnityVersion();
        if (!string.Equals(fullUnityVersion, BenchmarkEnvironmentContract.FullUnityVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Formal build requires Unity {BenchmarkEnvironmentContract.FullUnityVersion}; actual {fullUnityVersion}.");
        }

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
        string lockPath = Path.Combine(projectRoot, "Packages", "packages-lock.json");
        if (!File.Exists(manifestPath) || !File.Exists(lockPath))
            throw new FileNotFoundException("Formal build requires manifest.json and packages-lock.json.");

        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            if (!manifest.RootElement.TryGetProperty("dependencies", out JsonElement dependencies)
                || !dependencies.TryGetProperty("com.unity.collections", out JsonElement collectionsRequest)
                || !string.Equals(collectionsRequest.GetString(),
                    BenchmarkEnvironmentContract.CollectionsManifestVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"manifest.json must directly request Collections {BenchmarkEnvironmentContract.CollectionsManifestVersion}.");
            }
        }

        using (JsonDocument packageLock = JsonDocument.Parse(File.ReadAllText(lockPath)))
        {
            bool lockEntryValid = packageLock.RootElement.TryGetProperty(
                                      "dependencies", out JsonElement dependencies)
                                  && dependencies.TryGetProperty(
                                      "com.unity.collections", out JsonElement collections)
                                  && collections.TryGetProperty("version", out JsonElement version)
                                  && collections.TryGetProperty("source", out JsonElement source)
                                  && string.Equals(version.GetString(),
                                      BenchmarkEnvironmentContract.CollectionsResolvedVersion,
                                      StringComparison.Ordinal)
                                  && string.Equals(source.GetString(),
                                      BenchmarkEnvironmentContract.CollectionsResolvedSource,
                                      StringComparison.Ordinal);
            if (!lockEntryValid)
            {
                throw new InvalidOperationException(
                    "packages-lock.json must resolve Collections 6.5.0 from builtin.");
            }
        }

        PackageManagerInfo collectionsPackage =
            PackageManagerInfo.FindForPackageName("com.unity.collections");
        if (collectionsPackage == null
            || !collectionsPackage.isDirectDependency
            || !string.Equals(collectionsPackage.version,
                BenchmarkEnvironmentContract.CollectionsResolvedVersion, StringComparison.Ordinal)
            || collectionsPackage.source != PackageManagerSource.BuiltIn)
        {
            throw new InvalidOperationException(
                "Unity Package Manager must register Collections 6.5.0 as a direct builtin dependency.");
        }

        string lockSha256;
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(lockPath))
        {
            lockSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }
        if (!string.Equals(lockSha256, BenchmarkEnvironmentContract.PackagesLockSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"packages-lock SHA256 mismatch: expected {BenchmarkEnvironmentContract.PackagesLockSha256}, actual {lockSha256}.");
        }

        Debug.Log(
            $"[ContainerBenchmarkBuild] VERIFIED_ENV unity={fullUnityVersion} "
            + $"collections={BenchmarkEnvironmentContract.CollectionsResolvedVersion}/"
            + $"{BenchmarkEnvironmentContract.CollectionsResolvedSource} lockSha256={lockSha256}");
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
