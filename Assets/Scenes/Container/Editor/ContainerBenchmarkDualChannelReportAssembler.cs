using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ContainerBenchmark;
using UnityEditor;
using UnityEngine;
using Bench = ContainerBenchmark.ContainerBenchmark;
using Debug = UnityEngine.Debug;

/// <summary>
/// Editor-only 深模块：从一个正式结果根目录或恰好 6 个 Completed JSON 组装
/// 双通道报告。调用方只需要知道两个 Assemble 入口；文件发现、SHA256、期望
/// 用例目录、严格对齐、门禁与字段合成全部隐藏在实现内。
/// </summary>
public static class ContainerBenchmarkDualChannelReportAssembler
{
    private const string TimingRole = "ReleaseTiming";
    private const string GcRole = "DevelopmentGc";
    private const string ExpectedUnityVersion = BenchmarkEnvironmentContract.UnityVersion;
    private const string ExpectedUnityRevision = BenchmarkEnvironmentContract.UnityRevision;
    private const string RequestedCollections = BenchmarkEnvironmentContract.CollectionsManifestVersion;
    private const string ResolvedCollections = BenchmarkEnvironmentContract.CollectionsResolvedVersion;
    private const string ResolvedCollectionsSource = BenchmarkEnvironmentContract.CollectionsResolvedSource;
    private const string ResolvedBurst = BenchmarkEnvironmentContract.BurstResolvedVersion;
    private const string ResolvedMathematics = BenchmarkEnvironmentContract.MathematicsResolvedVersion;
    private const string FormalRunMode = "Shard";
    private const string FormalSelectionPolicy = "practical-capped-v2";
    private const int ExpectedCaseCount = 928;
    private const int ExpectedRowCount = 1856;
    private const double StatisticTolerance = 1e-9;

    private static readonly string[] Shards =
    {
        "linear-small",
        "linear-large",
        "quadratic-stress",
    };

    private static readonly Dictionary<string, int> ExpectedCasesByShard =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "linear-small", 595 },
            { "linear-large", 231 },
            { "quadratic-stress", 102 },
        };

    /// <summary>
    /// 发现 formalRoot/release-timing 与 formalRoot/development-gc 下的三个分片。
    /// 每个目录必须恰好包含一个非 checkpoint JSON。
    /// </summary>
    public static BenchmarkReportEnvelope AssembleFromRoot(string formalRoot)
    {
        if (string.IsNullOrWhiteSpace(formalRoot))
        {
            throw new ArgumentException("正式结果根目录不能为空", nameof(formalRoot));
        }

        string root = Path.GetFullPath(formalRoot);
        var discoveryErrors = new List<string>();
        var paths = new List<string>(6);
        DiscoverChannel(root, "release-timing", discoveryErrors, paths);
        DiscoverChannel(root, "development-gc", discoveryErrors, paths);
        return AssembleInternal(paths, discoveryErrors);
    }

    /// <summary>
    /// 从调用方显式给出的 6 个最终 JSON 组装；角色和分片从 JSON 元数据识别，
    /// 不依赖文件名或参数顺序。
    /// </summary>
    public static BenchmarkReportEnvelope AssembleFromFiles(params string[] jsonFiles)
    {
        if (jsonFiles == null)
        {
            throw new ArgumentNullException(nameof(jsonFiles));
        }
        return AssembleInternal(jsonFiles, Array.Empty<string>());
    }

    [MenuItem("Container/导出双通道正式报告")]
    public static void ExportFromMenu()
    {
        string root = EditorUtility.OpenFolderPanel("选择 formal 结果根目录", string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var envelope = AssembleFromRoot(root);
        if (!envelope.diagnostics.passed)
        {
            EditorUtility.DisplayDialog("双通道报告门禁失败", BuildDiagnosticSummary(envelope.diagnostics), "确定");
            return;
        }

        string outputPath = Path.Combine(root, "ContainerBenchmarkDualChannelReport.html");
        HtmlReportExporter.Export(envelope, outputPath);
        Debug.Log("[ContainerBenchmark][双通道] 正式报告已导出: " + outputPath);
        EditorUtility.RevealInFinder(outputPath);
    }

    /// <summary>
    /// -batchmode -executeMethod 入口。
    /// 参数：-containerBenchmarkFormalRoot &lt;dir&gt; [-benchmarkOutput &lt;html&gt;]。
    /// </summary>
    public static void ExportFromCommandLine()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!TryReadOption(args, "containerBenchmarkFormalRoot", out string root))
        {
            throw new ArgumentException("缺少 -containerBenchmarkFormalRoot <dir>");
        }

        var envelope = AssembleFromRoot(root);
        if (!envelope.diagnostics.passed)
        {
            throw new InvalidDataException(BuildDiagnosticSummary(envelope.diagnostics));
        }

        string outputPath = TryReadOption(args, "benchmarkOutput", out string requestedOutput)
            ? Path.GetFullPath(requestedOutput)
            : Path.Combine(Path.GetFullPath(root), "ContainerBenchmarkDualChannelReport.html");
        HtmlReportExporter.Export(envelope, outputPath);
        Debug.Log("[ContainerBenchmark][双通道] 正式报告已导出: " + outputPath);
    }

    private static BenchmarkReportEnvelope AssembleInternal(
        IEnumerable<string> jsonFiles, IEnumerable<string> initialErrors)
    {
        var envelope = new BenchmarkReportEnvelope
        {
            createdUtc = DateTime.UtcNow.ToString("o"),
        };
        BenchmarkReportDiagnostics diagnostics = envelope.diagnostics;
        diagnostics.errors.AddRange(initialErrors.Where(x => !string.IsNullOrWhiteSpace(x)));

        string[] paths = jsonFiles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length != diagnostics.expectedSources)
        {
            AddError(diagnostics, $"正式输入必须恰好为 {diagnostics.expectedSources} 个 JSON，实际 {paths.Length} 个");
        }

        var documents = new List<SourceDocument>(paths.Length);
        foreach (string path in paths)
        {
            SourceDocument document = ReadSource(path, diagnostics);
            if (document != null)
            {
                documents.Add(document);
            }
        }
        diagnostics.sourceCount = documents.Count;

        Dictionary<string, HashSet<string>> expectedIds = BuildExpectedShardIds(
            diagnostics, out Dictionary<string, ExpectedCaseDefinition> expectedCases);
        string packagesLockSha = CalculateCurrentPackagesLockSha(diagnostics);

        ValidateSourceSlots(documents, diagnostics);
        foreach (SourceDocument document in documents)
        {
            ValidateSource(document, expectedIds, expectedCases, packagesLockSha, diagnostics);
            envelope.provenance.sources.Add(ToProvenance(document));
        }
        ValidateCompatibleEnvironments(documents, diagnostics);
        envelope.provenance.timingBuildGuid = SingleBuildGuid(documents, TimingRole);
        envelope.provenance.gcBuildGuid = SingleBuildGuid(documents, GcRole);

        var duplicateCaseKeys = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, BenchmarkCaseResult> timingRows = IndexRows(
            documents.Where(x => x.role == TimingRole), TimingRole, duplicateCaseKeys, diagnostics);
        Dictionary<string, BenchmarkCaseResult> gcRows = IndexRows(
            documents.Where(x => x.role == GcRole), GcRole, duplicateCaseKeys, diagnostics);
        diagnostics.duplicateCases = duplicateCaseKeys.Count;

        HashSet<string> expectedAll = new HashSet<string>(StringComparer.Ordinal);
        foreach (HashSet<string> shardIds in expectedIds.Values)
        {
            expectedAll.UnionWith(shardIds);
        }
        ValidateExpectedRows(expectedAll, timingRows, gcRows, diagnostics);

        var mismatchedCases = new HashSet<string>(StringComparer.Ordinal);
        ValidateChannelAlignment(expectedAll, timingRows, gcRows, mismatchedCases, diagnostics);
        diagnostics.mismatchCases = mismatchedCases.Count;

        BuildMergedReport(documents, timingRows, gcRows, envelope);
        diagnostics.uniqueCases = CountSteadyCaseIds(timingRows);
        diagnostics.supportedCases = envelope.report.results.Count(r => !r.isWarmup && !r.skipped);
        diagnostics.skippedCases = envelope.report.results.Count(r => r.skipped);
        diagnostics.mergedRows = envelope.report.results.Count;

        ValidateFormalGate(diagnostics);
        diagnostics.passed = diagnostics.errors.Count == 0;
        envelope.report.runStatus = diagnostics.passed ? "Completed" : "AssemblyFailed";
        envelope.report.environmentError = diagnostics.passed
            ? string.Empty
            : BuildDiagnosticSummary(diagnostics);
        return envelope;
    }

    private static void DiscoverChannel(
        string root, string channelDirectory, List<string> errors, List<string> paths)
    {
        foreach (string shard in Shards)
        {
            string directory = Path.Combine(root, channelDirectory, shard);
            if (!Directory.Exists(directory))
            {
                errors.Add("缺少正式结果目录: " + directory);
                continue;
            }

            string[] candidates = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(x => !x.EndsWith(".checkpoint.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length != 1)
            {
                errors.Add($"{channelDirectory}/{shard} 必须恰好有一个最终 JSON，实际 {candidates.Length} 个");
                continue;
            }
            paths.Add(candidates[0]);
        }
    }

    private static SourceDocument ReadSource(string path, BenchmarkReportDiagnostics diagnostics)
    {
        if (!File.Exists(path))
        {
            AddError(diagnostics, "输入 JSON 不存在: " + path);
            return null;
        }
        if (path.EndsWith(".checkpoint.json", StringComparison.OrdinalIgnoreCase))
        {
            AddError(diagnostics, "checkpoint 不能作为正式输入: " + path);
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            string checkpointPath = Path.ChangeExtension(path, "checkpoint.json");
            string checkpointSha256 = string.Empty;
            if (!File.Exists(checkpointPath))
            {
                AddError(diagnostics, "缺少最终 checkpoint: " + checkpointPath);
            }
            else
            {
                checkpointSha256 = CalculateSha256(File.ReadAllBytes(checkpointPath));
                string sourceSha256 = CalculateSha256(bytes);
                if (!string.Equals(sourceSha256, checkpointSha256, StringComparison.Ordinal))
                {
                    AddError(diagnostics, "最终 JSON 与 checkpoint SHA256 不一致: " + path);
                }
            }
            string json = Encoding.UTF8.GetString(bytes);
            BenchmarkSuiteResult suite = BenchmarkSuiteResult.FromJson(json);
            if (suite == null)
            {
                AddError(diagnostics, "JSON 无法反序列化为 BenchmarkSuiteResult: " + path);
                return null;
            }
            if (suite.results == null)
            {
                suite.results = new List<BenchmarkCaseResult>();
            }

            string role = ClassifyRole(suite);
            if (string.IsNullOrEmpty(role))
            {
                AddError(diagnostics,
                    $"无法识别输入通道（buildKind={suite.buildKind}, measurementChannel={suite.measurementChannel}）: {path}");
            }

            return new SourceDocument
            {
                path = path,
                sha256 = CalculateSha256(bytes),
                checkpointPath = checkpointPath,
                checkpointSha256 = checkpointSha256,
                role = role,
                shard = suite.benchmarkShard ?? string.Empty,
                suite = suite,
            };
        }
        catch (Exception e)
        {
            AddError(diagnostics, "读取输入失败 " + path + ": " + e.Message);
            return null;
        }
    }

    private static string ClassifyRole(BenchmarkSuiteResult suite)
    {
        if (ContainsIgnoreCase(suite.buildKind, "Release")
            && string.Equals(suite.measurementChannel, "TimingOnly", StringComparison.OrdinalIgnoreCase))
        {
            return TimingRole;
        }
        if (ContainsIgnoreCase(suite.buildKind, "Development")
            && (string.Equals(suite.measurementChannel, "Combined", StringComparison.OrdinalIgnoreCase)
                || ContainsIgnoreCase(suite.measurementChannel, "GC")))
        {
            return GcRole;
        }
        return string.Empty;
    }

    private static void ValidateSourceSlots(
        List<SourceDocument> documents, BenchmarkReportDiagnostics diagnostics)
    {
        foreach (string role in new[] { TimingRole, GcRole })
        {
            foreach (string shard in Shards)
            {
                int count = documents.Count(x => x.role == role && x.shard == shard);
                if (count != 1)
                {
                    AddError(diagnostics, $"源槽 {role}/{shard} 必须恰好一个，实际 {count} 个");
                }
            }
        }
    }

    private static void ValidateSource(
        SourceDocument document,
        Dictionary<string, HashSet<string>> expectedIds,
        Dictionary<string, ExpectedCaseDefinition> expectedCases,
        string currentPackagesLockSha,
        BenchmarkReportDiagnostics diagnostics)
    {
        BenchmarkSuiteResult suite = document.suite;
        string label = document.role + "/" + document.shard;
        if (!string.Equals(suite.runStatus, "Completed", StringComparison.Ordinal))
        {
            AddError(diagnostics, label + " runStatus 必须为 Completed，实际 " + suite.runStatus);
        }
        if (!string.IsNullOrWhiteSpace(suite.environmentError))
        {
            AddError(diagnostics, label + " 存在 environmentError: " + suite.environmentError);
        }
        if (!ExpectedCasesByShard.TryGetValue(document.shard, out int expectedCaseCount))
        {
            AddError(diagnostics, label + " 使用未知分片");
            return;
        }
        if (suite.totalCases != expectedCaseCount || suite.completedCases != expectedCaseCount)
        {
            AddError(diagnostics,
                $"{label} 进度应为 {expectedCaseCount}/{expectedCaseCount}，实际 {suite.completedCases}/{suite.totalCases}");
        }
        if (suite.failedCases != 0 || suite.skippedCases != 0)
        {
            AddError(diagnostics,
                $"{label} 必须 0 failed / 0 skipped，实际 {suite.failedCases} failed / {suite.skippedCases} skipped");
        }
        if (suite.results.Count != expectedCaseCount * 2)
        {
            AddError(diagnostics,
                $"{label} 应有 {expectedCaseCount * 2} 行（预热+稳态），实际 {suite.results.Count} 行");
        }
        if (!ContainsIgnoreCase(suite.scriptingBackend, "IL2CPP"))
        {
            AddError(diagnostics, label + " 不是 IL2CPP: " + suite.scriptingBackend);
        }
        if (!string.Equals(suite.runMode, FormalRunMode, StringComparison.Ordinal))
        {
            AddError(diagnostics, label + " runMode 必须为 " + FormalRunMode + "，实际 " + suite.runMode);
        }
        if (!string.Equals(suite.selectionPolicy, FormalSelectionPolicy, StringComparison.Ordinal))
        {
            AddError(diagnostics,
                label + " selectionPolicy 必须为 " + FormalSelectionPolicy + "，实际 " + suite.selectionPolicy);
        }
        if (!suite.jobEnabled)
        {
            AddError(diagnostics, label + " Job 维度未开启");
        }
        if (!suite.checkpointEnabled)
        {
            AddError(diagnostics, label + " checkpointEnabled 必须为 true");
        }
        if (string.IsNullOrWhiteSpace(suite.checkpointFile))
        {
            AddError(diagnostics, label + " checkpointFile 为空");
        }
        else
        {
            try
            {
                if (!string.Equals(Path.GetFullPath(suite.checkpointFile), document.checkpointPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AddError(diagnostics, label + " checkpointFile 与最终 JSON sibling 不一致");
                }
            }
            catch (Exception e)
            {
                AddError(diagnostics, label + " checkpointFile 非法: " + e.Message);
            }
        }
        if (string.IsNullOrWhiteSpace(suite.buildGuid))
        {
            AddError(diagnostics, label + " buildGuid 为空");
        }
        RequireEqual(suite.unityVersion, ExpectedUnityVersion,
            label + " Unity version", diagnostics);
        RequireEqual(suite.unityRevision, ExpectedUnityRevision,
            label + " Unity revision", diagnostics);
        if (document.role == TimingRole)
        {
            if (!string.Equals(suite.buildKind, "Release Player", StringComparison.Ordinal)
                || !string.Equals(suite.measurementChannel, "TimingOnly", StringComparison.Ordinal)
                || !suite.timingOnlyRequested
                || suite.gcMetricCalibrated)
            {
                AddError(diagnostics, label + " 必须是显式 TimingOnly 的 IL2CPP Release Player 源");
            }
        }
        else if (document.role == GcRole)
        {
            if (!string.Equals(suite.buildKind, "Development Player", StringComparison.Ordinal)
                || !string.Equals(suite.measurementChannel, "DevelopmentGC", StringComparison.Ordinal)
                || suite.timingOnlyRequested)
            {
                AddError(diagnostics, label + " 必须是 DevelopmentGC 的 IL2CPP Development Player 源");
            }
        }
        else
        {
            AddError(diagnostics, label + " 通道角色无法识别");
        }

        ValidatePackageSnapshot(suite, currentPackagesLockSha, label, diagnostics);

        var actualIds = new HashSet<string>(
            suite.results.Where(r => r != null && !r.isWarmup).Select(r => r.id),
            StringComparer.Ordinal);
        if (expectedIds.TryGetValue(document.shard, out HashSet<string> expectedShardIds))
        {
            foreach (string missing in expectedShardIds.Except(actualIds).Take(20))
            {
                AddError(diagnostics, label + " 缺少期望 ID: " + missing);
            }
            foreach (string extra in actualIds.Except(expectedShardIds).Take(20))
            {
                AddError(diagnostics, label + " 包含非本分片 ID: " + extra);
            }
        }

        foreach (BenchmarkCaseResult row in suite.results)
        {
            if (row == null)
            {
                AddError(diagnostics, label + " 包含 null 结果行");
                continue;
            }
            if (row.skipped)
            {
                AddError(diagnostics, label + " 不允许跳过: " + row.id);
            }
            if (!row.validated)
            {
                AddError(diagnostics, label + " 校验失败: " + row.id + " / " + row.validateDesc);
            }
            int expectedSamples = row.isWarmup ? 1 : BenchmarkConfig.SampleCount;
            int actualSamples = row.sampleMs == null ? 0 : row.sampleMs.Length;
            if (actualSamples != expectedSamples)
            {
                AddError(diagnostics,
                    $"{label} {RowKey(row)} 采样数应为 {expectedSamples}，实际 {actualSamples}");
            }
            if (expectedCases.TryGetValue(row.id ?? string.Empty, out ExpectedCaseDefinition expectedCase))
            {
                ValidateExpectedCaseMetadata(row, expectedCase, label, diagnostics);
            }
            else
            {
                AddError(diagnostics, label + " 无法找到当前 catalog 元数据: " + RowKey(row));
            }
            ValidateRowStatistics(row, label, diagnostics);

            if (document.role == TimingRole)
            {
                if (row.gcBytesAvailable || row.gcBytes != -1L)
                {
                    AddError(diagnostics, label + " Release timing 行意外携带 GC: " + RowKey(row));
                }
            }
            else if (document.role == GcRole && (!row.gcBytesAvailable || row.gcBytes < 0L))
            {
                AddError(diagnostics, label + " Development GC 行缺少有效 GC: " + RowKey(row));
            }
        }
        if (document.role == GcRole && !suite.gcMetricCalibrated)
        {
            AddError(diagnostics, label + " Development GC 校准未通过");
        }
    }

    private static void ValidatePackageSnapshot(
        BenchmarkSuiteResult suite,
        string currentPackagesLockSha,
        string label,
        BenchmarkReportDiagnostics diagnostics)
    {
        RequireEqual(suite.collectionsManifestRequest, RequestedCollections,
            label + " manifest Collections", diagnostics);
        RequireEqual(suite.collectionsResolvedVersion, ResolvedCollections,
            label + " resolved Collections", diagnostics);
        RequireEqual(suite.collectionsResolvedSource, ResolvedCollectionsSource,
            label + " resolved Collections source", diagnostics);
        RequireEqual(suite.burstResolvedVersion, ResolvedBurst,
            label + " resolved Burst", diagnostics);
        RequireEqual(suite.mathematicsResolvedVersion, ResolvedMathematics,
            label + " resolved Mathematics", diagnostics);
        if (!IsSha256(suite.packagesLockSha256))
        {
            AddError(diagnostics, label + " packages-lock SHA256 缺失或格式非法");
        }
        else if (!string.IsNullOrEmpty(currentPackagesLockSha)
                 && !string.Equals(suite.packagesLockSha256, currentPackagesLockSha,
                     StringComparison.OrdinalIgnoreCase))
        {
            AddError(diagnostics, label + " packages-lock SHA256 与当前工程不一致");
        }
    }

    private static void ValidateCompatibleEnvironments(
        List<SourceDocument> documents, BenchmarkReportDiagnostics diagnostics)
    {
        foreach (string role in new[] { TimingRole, GcRole })
        {
            string[] buildGuids = documents.Where(x => x.role == role)
                .Select(x => x.suite.buildGuid)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (buildGuids.Length != 1)
            {
                AddError(diagnostics,
                    $"{role} 三分片必须来自同一 buildGuid，实际唯一非空 GUID 数 {buildGuids.Length}");
            }
        }

        SourceDocument baselineDocument = documents.FirstOrDefault(x => x.role == TimingRole);
        if (baselineDocument == null)
        {
            return;
        }
        BenchmarkSuiteResult baseline = baselineDocument.suite;
        foreach (SourceDocument document in documents)
        {
            BenchmarkSuiteResult suite = document.suite;
            string label = document.role + "/" + document.shard;
            CompareField(baseline.unityVersion, suite.unityVersion, label + " Unity", diagnostics);
            CompareField(baseline.unityRevision, suite.unityRevision,
                label + " Unity revision", diagnostics);
            CompareField(baseline.platform, suite.platform, label + " platform", diagnostics);
            CompareField(baseline.scriptingBackend, suite.scriptingBackend, label + " backend", diagnostics);
            CompareField(baseline.selectionPolicy, suite.selectionPolicy, label + " selectionPolicy", diagnostics);
            CompareField(baseline.operatingSystem, suite.operatingSystem, label + " OS", diagnostics);
            CompareField(baseline.processorType, suite.processorType, label + " CPU", diagnostics);
            if (baseline.processorCount != suite.processorCount)
            {
                AddError(diagnostics, label + " logical CPU 数与基准源不一致");
            }
            if (baseline.systemMemoryMB != suite.systemMemoryMB)
            {
                AddError(diagnostics, label + " 系统内存快照与基准源不一致");
            }
            CompareField(baseline.collectionsManifestRequest,
                suite.collectionsManifestRequest, label + " manifest Collections", diagnostics);
            CompareField(baseline.collectionsResolvedVersion,
                suite.collectionsResolvedVersion, label + " resolved Collections", diagnostics);
            CompareField(baseline.collectionsResolvedSource,
                suite.collectionsResolvedSource, label + " resolved Collections source", diagnostics);
            CompareField(baseline.burstResolvedVersion,
                suite.burstResolvedVersion, label + " Burst", diagnostics);
            CompareField(baseline.mathematicsResolvedVersion,
                suite.mathematicsResolvedVersion, label + " Mathematics", diagnostics);
            CompareField(baseline.packagesLockSha256,
                suite.packagesLockSha256, label + " packages-lock SHA256", diagnostics, ignoreCase: true);
        }
    }

    private static Dictionary<string, BenchmarkCaseResult> IndexRows(
        IEnumerable<SourceDocument> documents,
        string role,
        HashSet<string> duplicateCaseKeys,
        BenchmarkReportDiagnostics diagnostics)
    {
        var rows = new Dictionary<string, BenchmarkCaseResult>(StringComparer.Ordinal);
        foreach (SourceDocument document in OrderSources(documents))
        {
            foreach (BenchmarkCaseResult row in document.suite.results)
            {
                if (row == null)
                {
                    continue;
                }
                string key = RowKey(row);
                if (!rows.TryAdd(key, row))
                {
                    duplicateCaseKeys.Add(role + "/" + row.id);
                    AddError(diagnostics, role + " 出现重复结果行: " + key);
                }
            }
        }
        return rows;
    }

    private static void ValidateExpectedRows(
        HashSet<string> expectedIds,
        Dictionary<string, BenchmarkCaseResult> timingRows,
        Dictionary<string, BenchmarkCaseResult> gcRows,
        BenchmarkReportDiagnostics diagnostics)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in expectedIds)
        {
            foreach (string suffix in new[] { "|warmup", "|steady" })
            {
                string key = id + suffix;
                if (!timingRows.ContainsKey(key))
                {
                    missing.Add(TimingRole + "/" + id);
                }
                if (!gcRows.ContainsKey(key))
                {
                    missing.Add(GcRole + "/" + id);
                }
            }
        }
        diagnostics.missingCases = missing.Count;
        foreach (string item in missing.Take(20))
        {
            AddError(diagnostics, "缺少期望用例: " + item);
        }
    }

    private static void ValidateChannelAlignment(
        HashSet<string> expectedIds,
        Dictionary<string, BenchmarkCaseResult> timingRows,
        Dictionary<string, BenchmarkCaseResult> gcRows,
        HashSet<string> mismatchedCases,
        BenchmarkReportDiagnostics diagnostics)
    {
        foreach (string id in expectedIds)
        {
            foreach (string suffix in new[] { "|warmup", "|steady" })
            {
                string key = id + suffix;
                if (!timingRows.TryGetValue(key, out BenchmarkCaseResult timing)
                    || !gcRows.TryGetValue(key, out BenchmarkCaseResult gc))
                {
                    continue;
                }
                if (!SameCaseMetadata(timing, gc))
                {
                    if (mismatchedCases.Add(id))
                    {
                        AddError(diagnostics, "Release/Development 元数据不一致: " + id);
                    }
                }
            }
        }
    }

    private static void BuildMergedReport(
        List<SourceDocument> documents,
        Dictionary<string, BenchmarkCaseResult> timingRows,
        Dictionary<string, BenchmarkCaseResult> gcRows,
        BenchmarkReportEnvelope envelope)
    {
        SourceDocument baselineDocument = OrderSources(
                documents.Where(x => x.role == TimingRole))
            .FirstOrDefault();
        BenchmarkSuiteResult baseline = baselineDocument?.suite;
        var report = new BenchmarkSuiteResult
        {
            suiteName = "ContainerBenchmark Dual-Channel Formal Report",
            unityVersion = baseline?.unityVersion,
            unityRevision = baseline?.unityRevision,
            startedUtc = MinIso(documents.Select(x => x.suite.startedUtc)),
            endedUtc = MaxIso(documents.Select(x => x.suite.endedUtc)),
            jobEnabled = true,
            platform = baseline?.platform,
            scriptingBackend = baseline?.scriptingBackend,
            buildKind = "Release timing + Development GC",
            collectionsManifestRequest = baseline?.collectionsManifestRequest,
            collectionsResolvedVersion = baseline?.collectionsResolvedVersion,
            collectionsResolvedSource = baseline?.collectionsResolvedSource,
            burstResolvedVersion = baseline?.burstResolvedVersion,
            mathematicsResolvedVersion = baseline?.mathematicsResolvedVersion,
            packagesLockSha256 = baseline?.packagesLockSha256,
            runMode = "FormalMerged",
            benchmarkShard = "linear-small + linear-large + quadratic-stress",
            selectionPolicy = baseline?.selectionPolicy,
            measurementChannel = "DualChannel",
            timingOnlyRequested = false,
            checkpointEnabled = false,
            checkpointFile = string.Empty,
            totalCases = ExpectedCaseCount,
            completedCases = ExpectedCaseCount,
            failedCases = 0,
            skippedCases = 0,
            buildGuid = "timing=" + (envelope.provenance.timingBuildGuid ?? string.Empty)
                        + ";gc=" + (envelope.provenance.gcBuildGuid ?? string.Empty),
            operatingSystem = baseline?.operatingSystem,
            processorType = baseline?.processorType,
            processorCount = baseline?.processorCount ?? 0,
            systemMemoryMB = baseline?.systemMemoryMB ?? 0,
            graphicsDeviceName = baseline?.graphicsDeviceName,
            graphicsDeviceType = baseline?.graphicsDeviceType,
            graphicsMemoryMB = baseline?.graphicsMemoryMB ?? 0,
            gcMetric = "GC.Alloc / Development Player steady 10-sample median",
            gcMetricCalibrated = documents.Where(x => x.role == GcRole)
                .All(x => x.suite.gcMetricCalibrated),
            gcCalibrationBytes = MedianLong(documents.Where(x => x.role == GcRole)
                .Select(x => x.suite.gcCalibrationBytes)),
            environmentWarning =
                "双通道报告：耗时取 IL2CPP Release Player 稳态 10 次采样中位数；" +
                "GC 取 IL2CPP Development Player 稳态 10 次采样分配中位数。",
            gcUnavailableReason = string.Empty,
            results = new List<BenchmarkCaseResult>(ExpectedRowCount),
        };

        foreach (SourceDocument document in OrderSources(documents.Where(x => x.role == TimingRole)))
        {
            foreach (BenchmarkCaseResult timing in document.suite.results)
            {
                if (timing == null)
                {
                    continue;
                }
                BenchmarkCaseResult merged = CloneResult(timing);
                // 预热只用于观察冷启动，不冒充 10 次稳态 GC 统计。
                merged.gcBytes = -1L;
                merged.gcBytesAvailable = false;
                if (!merged.isWarmup
                    && gcRows.TryGetValue(RowKey(merged), out BenchmarkCaseResult gc))
                {
                    merged.gcBytes = gc.gcBytes;
                    merged.gcBytesAvailable = gc.gcBytesAvailable;
                }
                report.results.Add(merged);
            }
        }
        envelope.report = report;
    }

    private static void ValidateFormalGate(BenchmarkReportDiagnostics diagnostics)
    {
        RequireCount(diagnostics.sourceCount, diagnostics.expectedSources, "输入源", diagnostics);
        RequireCount(diagnostics.uniqueCases, diagnostics.expectedCases, "唯一用例 ID", diagnostics);
        RequireCount(diagnostics.supportedCases, diagnostics.expectedSupportedCases, "支持用例", diagnostics);
        RequireCount(diagnostics.skippedCases, diagnostics.expectedSkippedCases, "跳过用例", diagnostics);
        RequireCount(diagnostics.mergedRows, diagnostics.expectedRows, "合并结果行", diagnostics);
        RequireCount(diagnostics.missingCases, 0, "缺失用例", diagnostics);
        RequireCount(diagnostics.duplicateCases, 0, "重复用例", diagnostics);
        RequireCount(diagnostics.mismatchCases, 0, "跨通道不匹配用例", diagnostics);
    }

    private static Dictionary<string, HashSet<string>> BuildExpectedShardIds(
        BenchmarkReportDiagnostics diagnostics,
        out Dictionary<string, ExpectedCaseDefinition> expectedCases)
    {
        expectedCases = new Dictionary<string, ExpectedCaseDefinition>(StringComparer.Ordinal);
        var expected = Shards.ToDictionary(
            shard => shard,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var universe = new Dictionary<string, IBenchmarkCase>(StringComparer.Ordinal);
        foreach (BenchmarkValueType valueType in Enum.GetValues(typeof(BenchmarkValueType)))
        {
            int[] scales = Bench.ScalesFor(valueType);
            CollisionProfile[] collisions = valueType == BenchmarkValueType.StringKey
                ? new[] { CollisionProfile.Normal }
                : (CollisionProfile[])Enum.GetValues(typeof(CollisionProfile));
            foreach (int scale in scales)
            {
                foreach (CollisionProfile collision in collisions)
                {
                    foreach (IBenchmarkCase benchmarkCase in
                             BenchmarkCaseCatalog.CreateAll(scale, valueType, collision, useJob: true))
                    {
                        universe.TryAdd(benchmarkCase.Id, benchmarkCase);
                    }
                }
            }
        }

        foreach (IBenchmarkCase benchmarkCase in universe.Values)
        {
            foreach (string shard in Shards)
            {
                if (Bench.MatchesBenchmarkShard(benchmarkCase, shard))
                {
                    expected[shard].Add(benchmarkCase.Id);
                    expectedCases.TryAdd(benchmarkCase.Id, new ExpectedCaseDefinition(benchmarkCase));
                }
            }
        }
        foreach (string shard in Shards)
        {
            int actual = expected[shard].Count;
            int required = ExpectedCasesByShard[shard];
            if (actual != required)
            {
                AddError(diagnostics,
                    $"当前代码生成的 {shard} 期望 ID 数为 {actual}，正式契约要求 {required}");
            }
        }
        int total = expected.Values.Sum(x => x.Count);
        if (total != ExpectedCaseCount)
        {
            AddError(diagnostics, $"当前代码三分片总 ID 数为 {total}，正式契约要求 {ExpectedCaseCount}");
        }
        if (expectedCases.Count != ExpectedCaseCount)
        {
            AddError(diagnostics,
                $"当前代码生成的正式元数据数为 {expectedCases.Count}，正式契约要求 {ExpectedCaseCount}");
        }
        return expected;
    }

    private static string CalculateCurrentPackagesLockSha(BenchmarkReportDiagnostics diagnostics)
    {
        try
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            string path = Path.Combine(projectRoot ?? string.Empty, "Packages", "packages-lock.json");
            if (!File.Exists(path))
            {
                AddError(diagnostics, "当前工程缺少 Packages/packages-lock.json");
                return string.Empty;
            }
            return CalculateSha256(File.ReadAllBytes(path));
        }
        catch (Exception e)
        {
            AddError(diagnostics, "计算当前 packages-lock SHA256 失败: " + e.Message);
            return string.Empty;
        }
    }

    private static BenchmarkReportSource ToProvenance(SourceDocument document)
    {
        return new BenchmarkReportSource
        {
            role = document.role,
            shard = document.shard,
            sourceFile = document.path,
            sha256 = document.sha256,
            checkpointFile = document.checkpointPath,
            checkpointSha256 = document.checkpointSha256,
            buildGuid = document.suite.buildGuid,
            buildKind = document.suite.buildKind,
            measurementChannel = document.suite.measurementChannel,
            startedUtc = document.suite.startedUtc,
            endedUtc = document.suite.endedUtc,
            caseCount = document.suite.results.Count(r => r != null && !r.isWarmup),
            rowCount = document.suite.results.Count,
        };
    }

    private static IEnumerable<SourceDocument> OrderSources(IEnumerable<SourceDocument> documents)
    {
        return documents.OrderBy(x => x.role == TimingRole ? 0 : 1)
            .ThenBy(x => Array.IndexOf(Shards, x.shard))
            .ThenBy(x => x.path, StringComparer.OrdinalIgnoreCase);
    }

    private static BenchmarkCaseResult CloneResult(BenchmarkCaseResult source)
    {
        return new BenchmarkCaseResult
        {
            id = source.id,
            operationCode = source.operationCode,
            family = source.family,
            container = source.container,
            operation = source.operation,
            scale = source.scale,
            valueType = source.valueType,
            collision = source.collision,
            useJob = source.useJob,
            isNative = source.isNative,
            isWarmup = source.isWarmup,
            sampleMs = source.sampleMs == null ? Array.Empty<double>() : (double[])source.sampleMs.Clone(),
            medianMs = source.medianMs,
            meanMs = source.meanMs,
            p95Ms = source.p95Ms,
            totalMs = source.totalMs,
            nsPerOp = source.nsPerOp,
            timedOperationCount = source.timedOperationCount,
            gcBytes = source.gcBytes,
            gcBytesAvailable = source.gcBytesAvailable,
            validated = source.validated,
            validateDesc = source.validateDesc,
            semanticNote = source.semanticNote,
            skipped = source.skipped,
            skipReason = source.skipReason,
        };
    }

    private static bool SameCaseMetadata(BenchmarkCaseResult a, BenchmarkCaseResult b)
    {
        return string.Equals(a.id, b.id, StringComparison.Ordinal)
               && string.Equals(a.operationCode, b.operationCode, StringComparison.Ordinal)
               && string.Equals(a.family, b.family, StringComparison.Ordinal)
               && string.Equals(a.container, b.container, StringComparison.Ordinal)
               && string.Equals(a.operation, b.operation, StringComparison.Ordinal)
               && a.scale == b.scale
               && string.Equals(a.valueType, b.valueType, StringComparison.Ordinal)
               && string.Equals(a.collision, b.collision, StringComparison.Ordinal)
               && a.useJob == b.useJob
               && a.isNative == b.isNative
               && a.isWarmup == b.isWarmup
               && a.timedOperationCount == b.timedOperationCount
               && a.skipped == b.skipped
               && string.Equals(a.semanticNote ?? string.Empty, b.semanticNote ?? string.Empty,
                   StringComparison.Ordinal)
               && string.Equals(a.skipReason ?? string.Empty, b.skipReason ?? string.Empty,
                   StringComparison.Ordinal);
    }

    private static void ValidateExpectedCaseMetadata(
        BenchmarkCaseResult row,
        ExpectedCaseDefinition expected,
        string label,
        BenchmarkReportDiagnostics diagnostics)
    {
        bool matches = string.Equals(row.id, expected.id, StringComparison.Ordinal)
                       && string.Equals(row.operationCode, expected.operationCode, StringComparison.Ordinal)
                       && string.Equals(row.family, expected.family, StringComparison.Ordinal)
                       && string.Equals(row.container, expected.container, StringComparison.Ordinal)
                       && row.scale == expected.scale
                       && string.Equals(row.valueType, expected.valueType, StringComparison.Ordinal)
                       && string.Equals(row.collision, expected.collision, StringComparison.Ordinal)
                       && row.useJob == expected.useJob
                       && row.isNative == expected.isNative
                       && row.timedOperationCount == expected.timedOperationCount
                       && string.Equals(row.semanticNote ?? string.Empty, expected.semanticNote,
                           StringComparison.Ordinal);
        if (!matches)
        {
            AddError(diagnostics, label + " 与当前 catalog 核心元数据不一致: " + RowKey(row));
        }
    }

    private static void ValidateRowStatistics(
        BenchmarkCaseResult row,
        string label,
        BenchmarkReportDiagnostics diagnostics)
    {
        if (row.sampleMs == null || row.sampleMs.Length == 0)
        {
            return;
        }
        if (row.sampleMs.Any(x => double.IsNaN(x) || double.IsInfinity(x) || x <= 0d))
        {
            AddError(diagnostics, label + " 包含非有限或非正耗时样本: " + RowKey(row));
            return;
        }
        if (row.timedOperationCount <= 0L)
        {
            AddError(diagnostics, label + " timedOperationCount 必须为正数: " + RowKey(row));
            return;
        }

        double[] sorted = (double[])row.sampleMs.Clone();
        Array.Sort(sorted);
        int count = sorted.Length;
        double median = (count & 1) == 0
            ? (sorted[count / 2 - 1] + sorted[count / 2]) / 2d
            : sorted[count / 2];
        double total = 0d;
        for (int i = 0; i < count; i++)
        {
            total += sorted[i];
        }
        double mean = total / count;
        double p95 = sorted[(int)Math.Ceiling(0.95d * count) - 1];
        double nsPerOp = median * 1e6d / row.timedOperationCount;

        RequireNear(row.medianMs, median, label + " medianMs " + RowKey(row), diagnostics);
        RequireNear(row.meanMs, mean, label + " meanMs " + RowKey(row), diagnostics);
        RequireNear(row.p95Ms, p95, label + " p95Ms " + RowKey(row), diagnostics);
        RequireNear(row.totalMs, total, label + " totalMs " + RowKey(row), diagnostics);
        RequireNear(row.nsPerOp, nsPerOp, label + " nsPerOp " + RowKey(row), diagnostics);
    }

    private static int CountSteadyCaseIds(Dictionary<string, BenchmarkCaseResult> rows)
    {
        return rows.Values.Where(x => !x.isWarmup)
            .Select(x => x.id)
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private static string RowKey(BenchmarkCaseResult row)
    {
        return (row.id ?? string.Empty) + (row.isWarmup ? "|warmup" : "|steady");
    }

    private static string CalculateSha256(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }
    }

    private static string SingleBuildGuid(IEnumerable<SourceDocument> documents, string role)
    {
        string[] values = documents.Where(x => x.role == role)
            .Select(x => x.suite.buildGuid)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 1 ? values[0] : string.Empty;
    }

    private static bool IsSha256(string value)
    {
        return !string.IsNullOrEmpty(value)
               && value.Length == 64
               && value.All(Uri.IsHexDigit);
    }

    private static void RequireEqual(
        string actual, string expected, string label, BenchmarkReportDiagnostics diagnostics)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            AddError(diagnostics, $"{label} 应为 {expected}，实际 {actual ?? "<null>"}");
        }
    }

    private static void CompareField(
        string expected,
        string actual,
        string label,
        BenchmarkReportDiagnostics diagnostics,
        bool ignoreCase = false)
    {
        StringComparison comparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(expected ?? string.Empty, actual ?? string.Empty, comparison))
        {
            AddError(diagnostics, label + " 与基准源不一致");
        }
    }

    private static void RequireCount(
        int actual, int expected, string label, BenchmarkReportDiagnostics diagnostics)
    {
        if (actual != expected)
        {
            AddError(diagnostics, $"{label}应为 {expected}，实际 {actual}");
        }
    }

    private static void RequireNear(
        double actual,
        double expected,
        string label,
        BenchmarkReportDiagnostics diagnostics)
    {
        double tolerance = StatisticTolerance * Math.Max(1d, Math.Abs(expected));
        if (double.IsNaN(actual) || double.IsInfinity(actual) || Math.Abs(actual - expected) > tolerance)
        {
            AddError(diagnostics,
                $"{label} 与 sampleMs 重算值不一致：stored={actual:R}, expected={expected:R}");
        }
    }

    private static bool ContainsIgnoreCase(string value, string fragment)
    {
        return !string.IsNullOrEmpty(value)
               && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static long MedianLong(IEnumerable<long> values)
    {
        long[] sorted = values.Where(x => x >= 0L).OrderBy(x => x).ToArray();
        return sorted.Length == 0 ? 0L : sorted[sorted.Length / 2];
    }

    private static string MinIso(IEnumerable<string> values)
    {
        return values.Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string MaxIso(IEnumerable<string> values)
    {
        return values.Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderByDescending(x => x, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;
    }

    private static void AddError(BenchmarkReportDiagnostics diagnostics, string message)
    {
        if (!diagnostics.errors.Contains(message))
        {
            diagnostics.errors.Add(message);
        }
    }

    private static string BuildDiagnosticSummary(BenchmarkReportDiagnostics diagnostics)
    {
        string gate =
            $"sources={diagnostics.sourceCount}/{diagnostics.expectedSources}, " +
            $"cases={diagnostics.uniqueCases}/{diagnostics.expectedCases}, " +
            $"supported={diagnostics.supportedCases}/{diagnostics.expectedSupportedCases}, " +
            $"skip={diagnostics.skippedCases}/{diagnostics.expectedSkippedCases}, " +
            $"rows={diagnostics.mergedRows}/{diagnostics.expectedRows}, " +
            $"missing={diagnostics.missingCases}, duplicate={diagnostics.duplicateCases}, " +
            $"mismatch={diagnostics.mismatchCases}";
        if (diagnostics.errors.Count == 0)
        {
            return gate;
        }
        return gate + Environment.NewLine + string.Join(Environment.NewLine,
            diagnostics.errors.Take(20).Select(x => "- " + x));
    }

    private static bool TryReadOption(string[] args, string option, out string value)
    {
        string expected = "-" + option;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith(expected + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg.Substring(expected.Length + 1).Trim('"');
                return !string.IsNullOrWhiteSpace(value);
            }
            if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length)
            {
                value = args[i + 1].Trim('"');
                return !string.IsNullOrWhiteSpace(value);
            }
        }
        value = string.Empty;
        return false;
    }

    private sealed class SourceDocument
    {
        public string path;
        public string sha256;
        public string checkpointPath;
        public string checkpointSha256;
        public string role;
        public string shard;
        public BenchmarkSuiteResult suite;
    }

    private sealed class ExpectedCaseDefinition
    {
        public readonly string id;
        public readonly string operationCode;
        public readonly string family;
        public readonly string container;
        public readonly int scale;
        public readonly string valueType;
        public readonly string collision;
        public readonly bool useJob;
        public readonly bool isNative;
        public readonly long timedOperationCount;
        public readonly string semanticNote;

        public ExpectedCaseDefinition(IBenchmarkCase benchmarkCase)
        {
            id = benchmarkCase.Id;
            operationCode = benchmarkCase.OperationCode;
            family = benchmarkCase.Family.ToString();
            container = benchmarkCase.ContainerName;
            scale = benchmarkCase.Scale;
            valueType = benchmarkCase.ValueType.ToString();
            collision = benchmarkCase.Collision.ToString();
            useJob = benchmarkCase.UseJob;
            isNative = benchmarkCase.IsNative;
            timedOperationCount = benchmarkCase.TimedOperationCount;
            semanticNote = benchmarkCase.SemanticNote ?? string.Empty;
        }
    }
}
