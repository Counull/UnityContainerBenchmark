using System;using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ContainerBenchmark
{
    /// <summary>
    /// 运行进度快照（主控 → UI 单向传递）。
    /// </summary>
    public struct BenchmarkProgress
    {
        public int totalCases;
        public int completedCases;
        public int failedCases;
        public int skippedCases;
        public string currentCaseName;
        /// <summary>预计剩余秒数 = 已完成用例平均耗时 × 剩余用例数。</summary>
        public double etaSeconds;
        public double elapsedSeconds;

        public float Progress01 =>
            totalCases <= 0 ? 0f : Mathf.Clamp01(completedCases / (float)totalCases);
    }

    /// <summary>
    /// 维度勾选状态（配置页直接读写，主控按它过滤用例清单）。
    /// </summary>
    public sealed class SelectionState
    {
        public readonly HashSet<string> Containers = new HashSet<string>();
        public readonly HashSet<string> Operations = new HashSet<string>();
        public readonly HashSet<int> Scales = new HashSet<int>();
        public readonly HashSet<BenchmarkValueType> ValueTypes = new HashSet<BenchmarkValueType>();
        public readonly HashSet<CollisionProfile> Collisions = new HashSet<CollisionProfile>();
        public bool EnableJob = BenchmarkConfig.DefaultEnableJob;
    }

    /// <summary>
    /// 容器性能测试主控（阶段 2）：
    /// 配置解析（场景序列化字段 + 命令行 -autoRun / -benchmarkOutput）→
    /// 用例清单构建（容器×操作×规模×类型×碰撞×Job 笛卡尔积，默认全开可反选）→
    /// 执行循环（可变用例逐 pass 建销；显式只读用例复用 fixture；计时区始终只含 RunOnePass，预热 1 次 + 采样 10 次）→
    /// 统计（中位数主指标 / 均值 / p95 / 总耗时 / nsPerOp / GC 分配）→
    /// 聚合 BenchmarkSuiteResult 落盘 JSON（失败容错：单用例失败不中断全量）。
    /// 用例发现走 BenchmarkCaseCatalog 静态注册（阶段 1 契约），不硬编码具体用例类。
    /// </summary>
    public sealed class ContainerBenchmark : MonoBehaviour
    {
        // ==================== 场景序列化字段（默认全开，可反选） ====================

        [Header("维度勾选（留空 = 全部勾选）")]
        [Tooltip("勾选的容器名（如 List / NativeList / Dictionary...）。留空表示全部。")]
        public List<string> selectedContainers = new List<string>();

        [Tooltip("勾选的操作编号（A1~F2，字符串专项 B1S/B2S/B3S/B6S）。留空表示全部。")]
        public List<string> selectedOperations = new List<string>();

        [Tooltip("勾选的规模档位。留空表示全部（10K~1M；int 类型额外 2M/5M/10M）。")]
        public List<int> selectedScales = new List<int>();

        [Tooltip("勾选的数据类型维度。留空表示全部。")]
        public List<BenchmarkValueType> selectedValueTypes = new List<BenchmarkValueType>();

        [Tooltip("勾选的碰撞档（仅作用于哈希容器）。留空表示全部。")]
        public List<CollisionProfile> selectedCollisions = new List<CollisionProfile>();

        [Tooltip("Job/Burst 维度开关（默认开）。关闭时跳过 Job 用例（A8/B7/C6/C7）。")]
        public bool enableJob = BenchmarkConfig.DefaultEnableJob;

        [Header("自动运行")]
        [Tooltip("勾选后进入播放立即跑全量并自动导出 HTML 后退出。命令行 -autoRun 必须另带 -benchmarkFull、-benchmarkSmoke 或 -benchmarkShard。")]
        public bool autoRun;

        [Header("输出")]
        [Tooltip("JSON 输出路径（留空 = persistentDataPath/ContainerBenchmarkResult.json）。命令行 -benchmarkOutput 可覆盖。")]
        public string jsonOutputPath;

        // ==================== 运行时状态 ====================

        /// <summary>是否正在运行。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>是否无人值守自动运行模式（-autoRun 或场景勾选）。</summary>
        public bool IsAutoRun { get; private set; }

        /// <summary>最近一次运行聚合结果。</summary>
        public BenchmarkSuiteResult Suite { get; private set; }

        /// <summary>全集用例（未过滤）。</summary>
        public IReadOnlyList<IBenchmarkCase> Universe { get; private set; }

        /// <summary>当前勾选状态（UI 直接读写）。</summary>
        public SelectionState Selection { get; private set; }

        /// <summary>最近一次 JSON 输出文件路径。</summary>
        public string JsonOutputFile { get; private set; }

        // ==================== 事件（UI 订阅） ====================

        public event Action<string> LogEmitted;
        public event Action<BenchmarkProgress> ProgressChanged;
        public event Action<int> RunStarted;
        public event Action<BenchmarkSuiteResult> SuiteCompleted;

        private readonly List<IBenchmarkCase> _selectedCases = new List<IBenchmarkCase>();
        private bool _lastRunSucceeded;
        private bool _benchmarkSmoke;
        private bool _benchmarkFull;
        private bool _benchmarkTimingOnly;
        private bool _measureGc = true;
        private bool _checkpointWriteFailed;
        private string _benchmarkShard = "full";
        private string _commandLineError;
        private bool _explicitBenchmarkOutput;

        private const string GcAllocatedCounterName = "GC Allocated In Frame";
        private const string GcMetricDescription =
            "Unity ProfilerRecorder / Memory / GC Allocated In Frame / current-thread delta inside RunOnePass";
        private static readonly ProfilerRecorderOptions GcRecorderOptions =
            ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.CollectOnlyOnCurrentThread;

        // ==================== 生命周期 ====================

        private void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool cmdAutoRun = HasOption(args, "autoRun");
            _benchmarkSmoke = HasOption(args, "benchmarkSmoke");
            _benchmarkFull = HasOption(args, "benchmarkFull");
            _benchmarkTimingOnly = HasOption(args, "benchmarkTimingOnly");
            bool hasShardOption = HasOption(args, "benchmarkShard");
            _explicitBenchmarkOutput = HasOption(args, "benchmarkOutput");
            IsAutoRun = autoRun || cmdAutoRun;
            if (hasShardOption)
            {
                if (!TryReadOption(args, "benchmarkShard", out string shardValue)
                    || string.IsNullOrWhiteSpace(shardValue))
                {
                    AddCommandLineError("-benchmarkShard 缺少值；允许值：linear-small、linear-large、quadratic-stress、queue-d4");
                }
                else
                {
                    _benchmarkShard = shardValue.Trim().ToLowerInvariant();
                    if (!IsNamedBenchmarkShard(_benchmarkShard))
                    {
                        AddCommandLineError($"未知 -benchmarkShard 值 '{shardValue}'；允许值：linear-small、linear-large、quadratic-stress、queue-d4");
                    }
                }
            }

            int scopeOptionCount = (_benchmarkSmoke ? 1 : 0)
                                   + (_benchmarkFull ? 1 : 0)
                                   + (hasShardOption ? 1 : 0);
            if (scopeOptionCount > 1)
            {
                AddCommandLineError("-benchmarkFull、-benchmarkSmoke 与 -benchmarkShard 互斥，只能指定一个运行范围");
            }
            if (cmdAutoRun && scopeOptionCount == 0)
            {
                AddCommandLineError("命令行 -autoRun 必须显式指定 -benchmarkFull、-benchmarkSmoke 或 -benchmarkShard，避免误跑不可完成的字面全矩阵");
            }
            if (!IsAutoRun && scopeOptionCount > 0)
            {
                AddCommandLineError("-benchmarkFull、-benchmarkSmoke 与 -benchmarkShard 只能与 -autoRun 一起使用");
            }
            if (_benchmarkTimingOnly && !IsAutoRun)
            {
                AddCommandLineError("-benchmarkTimingOnly 只能与 -autoRun 一起使用");
            }
            if (_explicitBenchmarkOutput)
            {
                if (!TryReadOption(args, "benchmarkOutput", out string cmdOutput)
                    || string.IsNullOrWhiteSpace(cmdOutput))
                {
                    AddCommandLineError("-benchmarkOutput 缺少非空路径值");
                }
                else if (!string.Equals(Path.GetExtension(cmdOutput), ".json", StringComparison.OrdinalIgnoreCase)
                         || cmdOutput.EndsWith(".checkpoint.json", StringComparison.OrdinalIgnoreCase))
                {
                    AddCommandLineError("-benchmarkOutput 必须是最终结果 .json 路径，且不能以 .checkpoint.json 结尾");
                }
                else
                {
                    jsonOutputPath = cmdOutput;
                }
            }
            if (IsAutoRun)
            {
                Log($"[autoRun] 无人值守模式：命令行 -autoRun={cmdAutoRun}，场景字段 autoRun={autoRun}，full={_benchmarkFull}，smoke={_benchmarkSmoke}，shard={_benchmarkShard}，timingOnly={_benchmarkTimingOnly}");
            }
        }

        private void Start()
        {
            if (IsAutoRun || !string.IsNullOrWhiteSpace(_commandLineError))
            {
                StartCoroutine(AutoRunRoutine());
            }
        }

        // ==================== 公开入口 ====================

        /// <summary>UI「开始」按钮调用：按当前勾选跑一轮。</summary>
        public void StartRun()
        {
            if (IsRunning)
            {
                Log("已有基准测试在运行中，忽略本次开始请求");
                return;
            }
            StartCoroutine(RunAllCoroutine());
        }

        /// <summary>
        /// 导出 HTML 报告；已有 JSON 结果时与 JSON 同目录，否则使用 persistentDataPath。
        /// 返回路径（失败返回 null）。
        /// </summary>
        public string ExportHtml()
        {
            if (IsRunning)
            {
                LogError("基准测试运行中禁止导出 HTML；请使用原子 checkpoint 查看中途状态");
                return null;
            }
            if (Suite == null)
            {
                LogError("尚未运行基准测试，无法导出 HTML 报告");
                return null;
            }

            try
            {
                string outputPath = HtmlReportExporter.DefaultOutputPath;
                if (!string.IsNullOrWhiteSpace(JsonOutputFile))
                {
                    string jsonDirectory = Path.GetDirectoryName(Path.GetFullPath(JsonOutputFile));
                    if (!string.IsNullOrWhiteSpace(jsonDirectory))
                    {
                        string htmlFileName = _explicitBenchmarkOutput
                            ? Path.GetFileNameWithoutExtension(JsonOutputFile) + ".html"
                            : BenchmarkConfig.HtmlReportFileName;
                        outputPath = Path.Combine(jsonDirectory, htmlFileName);
                    }
                }

                string path = HtmlReportExporter.Export(Suite, outputPath);
                Log($"[导出] HTML 报告已写入: {path}");
                return path;
            }
            catch (Exception e)
            {
                LogError($"HTML 导出失败: {e}");
                return null;
            }
        }

        /// <summary>确保用例全集与勾选状态已构建（幂等，UI 在 Start 时调用）。</summary>
        public void EnsureUniverse()
        {
            if (Universe != null)
            {
                return;
            }

            // 笛卡尔积：类型 × 规模 × 碰撞档 × Job 开关（开）
            // 碰撞档只对哈希容器（B/C）有意义；非哈希用例构造时固定 Normal，
            // 不同碰撞档调用生成的重复 Id 由 seen 集合去重。
            // 字符串专项固定 Normal 碰撞、无 Job 形态。
            var universe = new List<IBenchmarkCase>(4096);
            var seen = new HashSet<string>();
            foreach (BenchmarkValueType valueType in Enum.GetValues(typeof(BenchmarkValueType)))
            {
                foreach (int scale in ScalesFor(valueType))
                {
                    foreach (CollisionProfile collision in CollisionsFor(valueType))
                    {
                        List<IBenchmarkCase> cases =
                            BenchmarkCaseCatalog.CreateAll(scale, valueType, collision, useJob: true);
                        foreach (IBenchmarkCase c in cases)
                        {
                            if (seen.Add(c.Id))
                            {
                                universe.Add(c);
                            }
                        }
                    }
                }
            }

            Universe = universe;
            Selection = BuildInitialSelection();
            Log($"用例全集构建完成：{universe.Count} 个（含不支持标记用例）");
        }

        /// <summary>按当前勾选过滤用例清单（运行前调用）。</summary>
        public List<IBenchmarkCase> BuildSelectedCases()
        {
            EnsureUniverse();
            _selectedCases.Clear();
            foreach (IBenchmarkCase c in Universe)
            {
                if (!Selection.EnableJob && c.UseJob)
                {
                    continue; // Job 开关关闭时跳过 Job 用例
                }
                if (!Selection.Containers.Contains(c.ContainerName))
                {
                    continue;
                }
                if (!Selection.Operations.Contains(c.OperationCode))
                {
                    continue;
                }
                if (!Selection.Scales.Contains(c.Scale))
                {
                    continue;
                }
                if (!Selection.ValueTypes.Contains(c.ValueType))
                {
                    continue;
                }
                bool collisionApplies = c.ValueType == BenchmarkValueType.IntVector3
                                        && (c.Family == ContainerFamily.Dictionary
                                            || c.Family == ContainerFamily.HashSet);
                if (collisionApplies && !Selection.Collisions.Contains(c.Collision))
                {
                    continue;
                }
                if (!MatchesBenchmarkShard(c, _benchmarkShard))
                {
                    continue;
                }
                _selectedCases.Add(c);
            }

            return _selectedCases;
        }

        /// <summary>
        /// 复杂度感知自动化分片的纯筛选谓词。空值/full 保持字面全矩阵；非法值不匹配任何用例。
        /// 暴露为纯函数，供 Editor 自测精确校验分片计数与互斥性。
        /// </summary>
        public static bool MatchesBenchmarkShard(IBenchmarkCase c, string shard)
        {
            if (c == null)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(shard)
                || string.Equals(shard, "full", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bool nonLinear = IsNonLinearCase(c);
            if (string.Equals(shard, "linear-small", StringComparison.OrdinalIgnoreCase))
            {
                return (c.Scale == 10_000
                        || c.Scale == 20_000
                        || c.Scale == 50_000
                        || c.Scale == 100_000
                        || c.Scale == 200_000
                        || c.Scale == 500_000
                        || c.Scale == 1_000_000)
                       && !nonLinear;
            }
            if (string.Equals(shard, "linear-large", StringComparison.OrdinalIgnoreCase))
            {
                return c.ValueType == BenchmarkValueType.IntVector3
                       && (c.Scale == 2_000_000
                           || c.Scale == 5_000_000
                           || c.Scale == 10_000_000)
                       && !nonLinear;
            }
            if (string.Equals(shard, "quadratic-stress", StringComparison.OrdinalIgnoreCase))
            {
                return c.ValueType == BenchmarkValueType.IntVector3
                       && (c.Scale == 10_000 || c.Scale == 20_000 || c.Scale == 50_000)
                       && nonLinear;
            }
            if (string.Equals(shard, "queue-d4", StringComparison.OrdinalIgnoreCase))
            {
                return c.Family == ContainerFamily.Queue && c.OperationCode == "D4";
            }
            return false;
        }

        private static bool IsNonLinearCase(IBenchmarkCase c)
        {
            bool intrinsic = c.OperationCode == "A2"
                             || c.OperationCode == "A4"
                             || c.OperationCode == "A5"
                             || c.OperationCode == "E3";
            bool allCollisionHash = c.ValueType == BenchmarkValueType.IntVector3
                                    && c.Collision == CollisionProfile.AllCollision
                                    && (c.Family == ContainerFamily.Dictionary
                                        || c.Family == ContainerFamily.HashSet);
            return intrinsic || allCollisionHash;
        }

        private static bool IsNamedBenchmarkShard(string shard)
        {
            return string.Equals(shard, "linear-small", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(shard, "linear-large", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(shard, "quadratic-stress", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(shard, "queue-d4", StringComparison.OrdinalIgnoreCase);
        }

        // ==================== 日志 ====================

        public void Log(string message)
        {
            Debug.Log($"[ContainerBenchmark] {message}");
            LogEmitted?.Invoke(message);
        }

        public void LogError(string message)
        {
            Debug.LogError($"[ContainerBenchmark] {message}");
            LogEmitted?.Invoke(message);
        }

        // ==================== 自动运行（整晚挂机） ====================

        private IEnumerator AutoRunRoutine()
        {
            // StartCoroutine 会在首次 yield 前同步执行。先让出首帧，确保所有 Start 都已完成，
            // 同时给 ContainerBenchmarkUI 机会在 autoRun 下禁用自身并退订事件。
            yield return null;
            if (!string.IsNullOrWhiteSpace(_commandLineError))
            {
                LogError("[autoRun][参数] " + _commandLineError);
                yield return null;
                Application.Quit(2);
                yield break;
            }
            if (_benchmarkSmoke)
            {
                ForceSmokeSelectionForAutoRun();
                Log("[autoRun][SMOKE] 开始 Player/产物链路冒烟；该结果不是完整性能矩阵");
            }
            else
            {
                ForceFullSelectionForAutoRun();
                string scope = IsNamedBenchmarkShard(_benchmarkShard)
                    ? $"分片 {_benchmarkShard}"
                    : "全量";
                Log($"[autoRun] 开始{scope}基准测试（预热 1 次 + 稳态采样 10 次/用例）");
            }
            yield return StartCoroutine(RunAllCoroutine());

            int exitCode = 0;
            if (!_lastRunSucceeded || Suite == null || string.IsNullOrWhiteSpace(JsonOutputFile))
            {
                exitCode = 1;
            }

            string htmlPath = null;
            if (Suite != null)
            {
                htmlPath = ExportHtml();
                if (htmlPath == null)
                {
                    LogError("[autoRun] HTML 导出失败，流程终止");
                    exitCode = 1;
                }
            }

            Log($"[autoRun] JSON: {JsonOutputFile ?? "<失败>"}");
            Log($"[autoRun] HTML: {htmlPath ?? "<失败>"}");
            Log($"[autoRun] 全部流程完成，退出码 {exitCode}");
            yield return null;
            Application.Quit(exitCode);
        }

        private string ValidateMeasurementEnvironment()
        {
            if (Application.isEditor)
            {
                return null;
            }
            if (!string.Equals(ScriptingBackendLabel(), "IL2CPP", StringComparison.Ordinal))
            {
                return "ContainerBenchmark Player 只接受 IL2CPP；Mono 结果不可进入正式报告";
            }
            if (IsAutoRun
                && (!string.Equals(Application.unityVersion, BenchmarkEnvironmentContract.UnityVersion,
                        StringComparison.Ordinal)
                    || !string.Equals(BenchmarkEnvironmentContract.RecordedUnityRevision,
                        BenchmarkEnvironmentContract.UnityRevision, StringComparison.Ordinal)
                    || !string.Equals(BenchmarkEnvironmentContract.RecordedCollectionsResolvedSource,
                        BenchmarkEnvironmentContract.CollectionsResolvedSource, StringComparison.Ordinal)))
            {
                return "正式自动运行必须使用通过独立构建入口验证的 Unity Editor revision 与 builtin Collections 环境";
            }
            if (Debug.isDebugBuild && _benchmarkTimingOnly)
            {
                return "IL2CPP Development Player 是 GC 测量通道，禁止 -benchmarkTimingOnly";
            }
            if (!Debug.isDebugBuild && IsAutoRun && !_benchmarkTimingOnly)
            {
                return "IL2CPP Release Player 是纯计时通道，必须显式指定 -benchmarkTimingOnly；GC 请使用 Development 通道";
            }
            return null;
        }

        private void CompleteEnvironmentFailure(int totalCases, string error)
        {
            Suite.environmentError = error;
            Suite.runStatus = "EnvironmentFailed";
            Suite.endedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            UpdateSuiteProgress(totalCases, 0, 0, 0);
            LogError("[环境] " + error);
            JsonOutputFile = SaveJson();
            SaveCheckpointIfEnabled();
            IsRunning = false;
            SuiteCompleted?.Invoke(Suite);
        }

        // ==================== 执行循环 ====================

        private IEnumerator RunAllCoroutine()
        {
            IsRunning = true;
            _lastRunSucceeded = false;
            _checkpointWriteFailed = false;
            _measureGc = true;
            JsonOutputFile = null;
            Suite = null;
            var cases = BuildSelectedCases();
            if (cases.Count == 0)
            {
                LogError("当前勾选下没有可运行的用例，请检查配置页勾选状态");
                IsRunning = false;
                yield break;
            }

            // Release 的交互 UI 没有命令行通道开关，因此自动采用纯计时；无人值守
            // Release 仍由环境门强制要求显式 -benchmarkTimingOnly，避免误标正式证据。
            bool effectiveTimingOnly = _benchmarkTimingOnly
                                       || (!Application.isEditor && !Debug.isDebugBuild);
            Suite = new BenchmarkSuiteResult
            {
                suiteName = "ContainerBenchmark",
                unityVersion = Application.unityVersion,
                unityRevision = BenchmarkEnvironmentContract.RecordedUnityRevision,
                startedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                jobEnabled = Selection.EnableJob,
                platform = Application.platform.ToString(),
                scriptingBackend = ScriptingBackendLabel(),
                buildKind = Application.isEditor ? "Editor" : (Debug.isDebugBuild ? "Development Player" : "Release Player"),
                collectionsManifestRequest = BenchmarkEnvironmentContract.CollectionsManifestVersion,
                collectionsResolvedVersion = BenchmarkEnvironmentContract.CollectionsResolvedVersion,
                collectionsResolvedSource = BenchmarkEnvironmentContract.RecordedCollectionsResolvedSource,
                burstResolvedVersion = BenchmarkEnvironmentContract.BurstResolvedVersion,
                mathematicsResolvedVersion = BenchmarkEnvironmentContract.MathematicsResolvedVersion,
                packagesLockSha256 = BenchmarkEnvironmentContract.PackagesLockSha256,
                runMode = IsAutoRun
                    ? (_benchmarkSmoke ? "Smoke" : (IsNamedBenchmarkShard(_benchmarkShard) ? "Shard" : "Full"))
                    : "InteractiveSelection",
                benchmarkShard = _benchmarkSmoke ? "smoke" : _benchmarkShard,
                selectionPolicy = _benchmarkSmoke
                    ? "representative-smoke-v1"
                    : (string.Equals(_benchmarkShard, "queue-d4", StringComparison.OrdinalIgnoreCase)
                        ? "supplemental-d4-v1"
                        : (IsNamedBenchmarkShard(_benchmarkShard) ? "practical-capped-v2" : "contract-full-v1")),
                measurementChannel = !Application.isEditor && Debug.isDebugBuild
                    ? "DevelopmentGC"
                    : (effectiveTimingOnly ? "TimingOnly" : "Combined"),
                timingOnlyRequested = _benchmarkTimingOnly,
                checkpointEnabled = IsNamedBenchmarkShard(_benchmarkShard),
                runStatus = "Running",
                totalCases = cases.Count,
                buildGuid = Application.buildGUID,
                operatingSystem = SystemInfo.operatingSystem,
                processorType = SystemInfo.processorType,
                processorCount = SystemInfo.processorCount,
                systemMemoryMB = SystemInfo.systemMemorySize,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                graphicsMemoryMB = SystemInfo.graphicsMemorySize,
                gcMetric = GcMetricDescription,
            };
            if (Suite.checkpointEnabled)
            {
                Suite.checkpointFile = ResolveCheckpointPath();
            }

            Log($"筛选后用例数 {cases.Count}（全集 {Universe.Count}）");
            RunStarted?.Invoke(cases.Count);

            string environmentGateError = ValidateMeasurementEnvironment();
            if (!string.IsNullOrWhiteSpace(environmentGateError))
            {
                CompleteEnvironmentFailure(cases.Count, environmentGateError);
                yield break;
            }

            if (effectiveTimingOnly)
            {
                _measureGc = false;
                Suite.gcMetricCalibrated = false;
                Suite.gcUnavailableReason = "Release TimingOnly 通道不采集 GC；GC 以同矩阵 IL2CPP Development 通道测量";
                Suite.environmentWarning = Suite.gcUnavailableReason + "，本文件所有 GC 字段写为 N/A。";
                Log("[环境][警告] " + Suite.environmentWarning);
            }

            try
            {
                if (_measureGc)
                {
                    Suite.gcCalibrationBytes = CalibrateGcAllocationCounter();
                    Suite.gcMetricCalibrated = true;
                    Log($"[环境] GC 计数器校准通过：{Suite.gcCalibrationBytes} B（{GcMetricDescription}）");
                }
            }
            catch (Exception e)
            {
                Suite.gcMetricCalibrated = false;
                Suite.gcUnavailableReason = "GC 计数器校准失败：" + e.Message;
                CompleteEnvironmentFailure(cases.Count, Suite.gcUnavailableReason);
                yield break;
            }

            var runWatch = Stopwatch.StartNew();
            int completed = 0;
            int failed = 0;
            int skipped = 0;

            foreach (IBenchmarkCase c in cases)
            {
                ProgressChanged?.Invoke(new BenchmarkProgress
                {
                    totalCases = cases.Count,
                    completedCases = completed,
                    failedCases = failed,
                    skippedCases = skipped,
                    currentCaseName = c.Id,
                    elapsedSeconds = runWatch.Elapsed.TotalSeconds,
                    etaSeconds = completed > 0
                        ? runWatch.Elapsed.TotalSeconds / completed * (cases.Count - completed)
                        : 0d,
                });
                // 先让 UI 绘制“当前用例”，再执行该用例的连续单次 pass。
                yield return null;

                if (!c.IsSupported)
                {
                    skipped++;
                    Suite.results.Add(CreateSkippedResult(c));
                    Log($"[跳过] {c.Id}: {c.UnsupportedReason}");
                    completed++;
                    UpdateSuiteProgress(cases.Count, completed, failed, skipped);
                    SaveCheckpointIfEnabled();
                    yield return null;
                    continue;
                }

                var outcome = new CaseRunOutcome();
                yield return StartCoroutine(RunCaseCoroutine(c, outcome));
                if (!outcome.succeeded)
                {
                    failed++;
                }

                completed++;
                UpdateSuiteProgress(cases.Count, completed, failed, skipped);
                SaveCheckpointIfEnabled();
            }

            Suite.endedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Suite.runStatus = failed == 0 ? "Completed" : "CompletedWithFailures";
            UpdateSuiteProgress(cases.Count, completed, failed, skipped);
            ProgressChanged?.Invoke(new BenchmarkProgress
            {
                totalCases = cases.Count,
                completedCases = completed,
                failedCases = failed,
                skippedCases = skipped,
                currentCaseName = "完成",
                elapsedSeconds = runWatch.Elapsed.TotalSeconds,
                etaSeconds = 0d,
            });
            Log($"运行结束：完成 {completed}，失败 {failed}，跳过 {skipped}，总耗时 {runWatch.Elapsed.TotalSeconds:F1}s");
            SaveCheckpointIfEnabled();
            if (_checkpointWriteFailed)
            {
                Suite.runStatus = "CompletedWithCheckpointError";
            }
            JsonOutputFile = SaveJson();
            if (string.IsNullOrWhiteSpace(JsonOutputFile))
            {
                Suite.runStatus = Suite.runStatus == "Completed"
                    ? "CompletedWithOutputError"
                    : Suite.runStatus + "AndOutputError";
            }
            _lastRunSucceeded = failed == 0
                                && !_checkpointWriteFailed
                                && !string.IsNullOrWhiteSpace(JsonOutputFile);
            IsRunning = false;
            SuiteCompleted?.Invoke(Suite);
        }

        /// <summary>
        /// 单用例完整流程：预热 1 次（单独记录）+ 稳态采样 10 次。
        /// 普通/可变用例每 pass 执行 Setup → Run → Validate → Teardown；显式实现
        /// IReusableReadOnlyBenchmarkCase 的用例由 session 只构建/释放一次 fixture，
        /// 每 pass 在计时外 ResetForPass 后仍执行完全相同的 Run 与 Validate。
        /// 结果通过 CaseRunOutcome 回传；校验或异常失败均已记录。
        /// </summary>
        private IEnumerator RunCaseCoroutine(IBenchmarkCase c, CaseRunOutcome outcome)
        {
            var session = new BenchmarkCaseSession(c);
            IEnumerator samples = RunCaseSamplesCoroutine(c, outcome, session);
            try
            {
                while (samples.MoveNext())
                {
                    yield return samples.Current;
                }
            }
            finally
            {
                (samples as IDisposable)?.Dispose();
                if (!session.TryRelease(out Exception releaseException))
                {
                    outcome.succeeded = false;
                    string failure = "fixture 释放异常：" + releaseException.Message;
                    BenchmarkCaseResult lastResult = Suite?.results?
                        .LastOrDefault(result => result.id == c.Id && !result.isWarmup);
                    if (lastResult != null)
                    {
                        lastResult.validated = false;
                        lastResult.validateDesc = string.IsNullOrWhiteSpace(lastResult.validateDesc)
                            ? failure
                            : lastResult.validateDesc + " | " + failure;
                    }
                    else if (Suite != null)
                    {
                        Suite.results.Add(CreateFailureResult(c, failure));
                    }
                    LogError($"[失败] {c.Id}: {failure}");
                }
            }
        }

        private IEnumerator RunCaseSamplesCoroutine(
            IBenchmarkCase c, CaseRunOutcome outcome, BenchmarkCaseSession session)
        {
            // ---- 预热：1 次，不计入统计，但数据单独记录 ----
            PassSample warmup = default;
            bool warmupValid = false;
            string warmupDesc = string.Empty;
            Exception passException = null;
            try
            {
                warmup = session.Execute(_measureGc, out warmupValid, out warmupDesc);
            }
            catch (Exception e)
            {
                passException = e;
            }

            if (passException != null)
            {
                var warmupFailure = CreateFailureResult(c, passException);
                warmupFailure.isWarmup = true;
                Suite.results.Add(warmupFailure);
                Suite.results.Add(CreateFailureResult(c, "稳态未执行：预热异常：" + passException.Message));
                LogError($"[失败] {c.Id}: {passException.Message}");
                outcome.succeeded = false;
                yield break;
            }

            var warmupResult = CreateCaseResult(c);
            warmupResult.isWarmup = true;
            warmupResult.sampleMs = new[] { warmup.ms };
            warmupResult.medianMs = warmup.ms;
            warmupResult.meanMs = warmup.ms;
            warmupResult.p95Ms = warmup.ms;
            warmupResult.totalMs = warmup.ms;
            warmupResult.nsPerOp = warmup.ms * 1e6 / Math.Max(1L, c.TimedOperationCount);
            warmupResult.gcBytes = warmup.gcBytes;
            warmupResult.gcBytesAvailable = warmup.gcBytesAvailable;
            warmupResult.validated = warmupValid;
            warmupResult.validateDesc = warmupValid ? "预热样本（不计入统计）" : warmupDesc;
            Suite.results.Add(warmupResult);
            // 每个 pass 之间让出一帧；单个 pass 内部仍保持连续计时。
            yield return null;

            if (!warmupValid)
            {
                // 预热即失败：不再浪费 10 次采样
                LogError($"[失败] {c.Id}: 预热校验失败 → {warmupDesc}");
                Suite.results.Add(CreateFailureResult(c, "预热校验失败：" + warmupDesc));
                outcome.succeeded = false;
                yield break;
            }

            // ---- 稳态采样：10 次 ----
            var samples = new List<double>(BenchmarkConfig.SampleCount);
            var gcSamples = new List<long>(BenchmarkConfig.SampleCount);
            bool valid = true;
            string failDesc = string.Empty;
            string lastPassDesc = string.Empty;
            for (int i = 0; i < BenchmarkConfig.SampleCount; i++)
            {
                PassSample s = default;
                bool passValid = false;
                string passDesc = string.Empty;
                passException = null;
                try
                {
                    s = session.Execute(_measureGc, out passValid, out passDesc);
                }
                catch (Exception e)
                {
                    passException = e;
                }

                if (passException != null)
                {
                    valid = false;
                    failDesc = "异常：" + passException.Message;
                    LogError($"[失败] {c.Id}: 第 {i + 1} 次采样异常 → {passException.Message}");
                    break;
                }

                samples.Add(s.ms);
                if (s.gcBytesAvailable)
                {
                    gcSamples.Add(s.gcBytes);
                }
                if (passValid)
                {
                    lastPassDesc = passDesc;
                }
                else
                {
                    valid = false;
                    failDesc = passDesc;
                    LogError($"[失败] {c.Id}: 第 {i + 1} 次采样校验失败 → {passDesc}");
                    break; // 中止剩余采样，按失败记录
                }
                yield return null;
            }

            var result = BuildStatsResult(c, samples, gcSamples, valid,
                valid ? lastPassDesc : failDesc);
            Suite.results.Add(result);
            string gcLabel = result.gcBytesAvailable ? $"{result.gcBytes} B" : "N/A";
            Log($"完成 {c.Id}: 中位 {result.medianMs:F3} ms，ns/op {result.nsPerOp:F2}，GC {gcLabel}，采样 {samples.Count} 次");
            outcome.succeeded = valid;
        }

        /// <summary>普通/可变用例的单次采样；计时区只含 RunOnePass。</summary>
        private static PassSample ExecutePass(
            IBenchmarkCase c, bool measureGc, out bool valid, out string desc)
        {
            PassSample sample = default;
            Exception primaryException = null;
            try
            {
                c.Setup();
                sample = ExecuteMeasuredPass(c, measureGc, out valid, out desc);
            }
            catch (Exception e)
            {
                primaryException = e;
                valid = false;
                desc = string.Empty;
            }

            try
            {
                // Setup 自身部分失败、Run/Validate 抛异常时也必须尝试释放 Native 资源。
                c.Teardown();
            }
            catch (Exception teardownException)
            {
                if (primaryException != null)
                {
                    throw new AggregateException(
                        "用例执行与 fixture 释放均发生异常", primaryException, teardownException);
                }
                throw;
            }

            if (primaryException != null)
            {
                ExceptionDispatchInfo.Capture(primaryException).Throw();
            }
            return sample;
        }

        /// <summary>共享计时核心；调用方负责在进入前准备/复位并在离开后释放 fixture。</summary>
        private static PassSample ExecuteMeasuredPass(
            IBenchmarkCase c, bool measureGc, out bool valid, out string desc)
        {
            valid = false;
            desc = string.Empty;
            ProfilerRecorder gcRecorder = default;
            bool gcRecorderCreated = false;
            try
            {
                var sw = new Stopwatch();
                // ProfilerRecorder 的 CurrentValue 可在帧内立即读取；前后差值只覆盖 RunOnePass。
                // 是否可用于目标 Release Player 由每次运行开始时的真实分配校准判定。
                // 当前线程过滤用于避免后台线程噪声。
                long allocBefore = 0L;
                if (measureGc)
                {
                    gcRecorder = StartGcAllocationRecorder();
                    gcRecorderCreated = true;
                    allocBefore = gcRecorder.CurrentValue;
                }
                sw.Start();
                c.RunOnePass();
                sw.Stop();
                long allocAfter = measureGc ? gcRecorder.CurrentValue : -1L;
                c.Checksum.Escape(); // 防优化：结果写入 static 字段（计时区外消费）
                valid = c.Validate(out desc);
                return new PassSample
                {
                    ms = sw.Elapsed.TotalMilliseconds,
                    gcBytes = measureGc ? Math.Max(0L, allocAfter - allocBefore) : -1L,
                    gcBytesAvailable = measureGc,
                };
            }
            finally
            {
                if (gcRecorderCreated)
                {
                    gcRecorder.Stop();
                    gcRecorder.Dispose();
                }
            }
        }

        // ==================== 结果构建 ====================

        private static BenchmarkCaseResult CreateCaseResult(IBenchmarkCase c)
        {
            return new BenchmarkCaseResult
            {
                id = c.Id,
                operationCode = c.OperationCode,
                family = c.Family.ToString(),
                container = c.ContainerName,
                operation = c.OperationName,
                scale = c.Scale,
                valueType = c.ValueType.ToString(),
                collision = c.Collision.ToString(),
                useJob = c.UseJob,
                isNative = c.IsNative,
                semanticNote = c.SemanticNote,
                timedOperationCount = c.TimedOperationCount,
            };
        }

        private static BenchmarkCaseResult BuildStatsResult(
            IBenchmarkCase c, List<double> samples, List<long> gcSamples,
            bool valid, string validateDesc)
        {
            var r = CreateCaseResult(c);
            r.isWarmup = false;
            r.sampleMs = samples.ToArray();

            var sorted = samples.ToArray();
            Array.Sort(sorted);
            int n = sorted.Length;
            if (n == 0)
            {
                r.medianMs = 0d;
                r.meanMs = 0d;
                r.p95Ms = 0d;
                r.totalMs = 0d;
                r.nsPerOp = 0d;
                r.gcBytes = -1L;
                r.gcBytesAvailable = false;
                r.validated = false;
                r.validateDesc = string.IsNullOrWhiteSpace(validateDesc)
                    ? "没有可统计的稳态样本"
                    : validateDesc;
                return r;
            }
            // 中位数（主指标）：偶数个取中间两个均值
            r.medianMs = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2d;
            double sum = 0d;
            for (int i = 0; i < n; i++)
            {
                sum += sorted[i];
            }
            r.meanMs = sum / n;
            // p95（最近秩法）
            r.p95Ms = sorted[(int)Math.Ceiling(0.95 * n) - 1];
            r.totalMs = sum;
            r.nsPerOp = r.medianMs * 1e6 / Math.Max(1L, c.TimedOperationCount);

            if (gcSamples.Count > 0)
            {
                r.gcBytes = CalculateLongMedian(gcSamples);
                r.gcBytesAvailable = true;
            }
            else
            {
                r.gcBytes = -1L;
                r.gcBytesAvailable = false;
            }

            r.validated = valid;
            r.validateDesc = validateDesc;
            return r;
        }

        private static BenchmarkCaseResult CreateSkippedResult(IBenchmarkCase c)
        {
            var r = CreateCaseResult(c);
            r.skipped = true;
            r.skipReason = c.UnsupportedReason;
            r.validated = false;
            r.validateDesc = "跳过：" + c.UnsupportedReason;
            return r;
        }

        private static BenchmarkCaseResult CreateFailureResult(IBenchmarkCase c, Exception e)
        {
            return CreateFailureResult(c, "异常：" + e.Message);
        }

        private static BenchmarkCaseResult CreateFailureResult(IBenchmarkCase c, string description)
        {
            var r = CreateCaseResult(c);
            r.isWarmup = false;
            r.sampleMs = Array.Empty<double>();
            r.validated = false;
            r.validateDesc = description;
            return r;
        }

        /// <summary>
        /// 非负整数样本的标准中位数；偶数个样本取中间两项均值并向下取整。
        /// 使用差值形式避免直接相加导致 long 溢出。
        /// </summary>
        public static long CalculateLongMedian(IReadOnlyList<long> values)
        {
            if (values == null || values.Count == 0)
            {
                throw new ArgumentException("中位数至少需要一个样本", nameof(values));
            }

            var sorted = new long[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] < 0L)
                {
                    throw new ArgumentOutOfRangeException(nameof(values), "GC 分配样本不能为负数");
                }
                sorted[i] = values[i];
            }
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            if ((sorted.Length & 1) != 0)
            {
                return sorted[middle];
            }
            long lo = sorted[middle - 1];
            long hi = sorted[middle];
            return lo + (hi - lo) / 2L;
        }

        private void ForceFullSelectionForAutoRun()
        {
            EnsureUniverse();
            Selection.Containers.Clear();
            Selection.Operations.Clear();
            Selection.Scales.Clear();
            Selection.ValueTypes.Clear();
            Selection.Collisions.Clear();
            Selection.Containers.UnionWith(Universe.Select(c => c.ContainerName));
            Selection.Operations.UnionWith(Universe.Select(c => c.OperationCode));
            Selection.Scales.UnionWith(Universe.Select(c => c.Scale));
            Selection.ValueTypes.UnionWith(Universe.Select(c => c.ValueType));
            Selection.Collisions.UnionWith(Universe.Select(c => c.Collision));
            Selection.EnableJob = true;
            Log($"[autoRun] 已忽略场景筛选并强制全量，Job=开，用例数 {BuildSelectedCases().Count}");
        }

        /// <summary>
        /// 自动化链路专用的显式冒烟集合。只有命令行同时传入 -autoRun -benchmarkSmoke 才会使用；
        /// 命令行 -autoRun 必须显式选择 Full、Smoke 或一个命名分片。
        /// </summary>
        private void ForceSmokeSelectionForAutoRun()
        {
            EnsureUniverse();
            Selection.Containers.Clear();
            Selection.Operations.Clear();
            Selection.Scales.Clear();
            Selection.ValueTypes.Clear();
            Selection.Collisions.Clear();
            Selection.Containers.UnionWith(Universe.Select(c => c.ContainerName));
            Selection.Operations.UnionWith(new[]
            {
                "A1", "A7", "A8", "B1", "B1S", "C1", "D1", "D3", "E1", "F1", "F2",
            });
            Selection.Scales.Add(10_000);
            Selection.ValueTypes.Add(BenchmarkValueType.IntVector3);
            Selection.ValueTypes.Add(BenchmarkValueType.StringKey);
            Selection.Collisions.Add(CollisionProfile.Normal);
            Selection.EnableJob = true;
            Log($"[autoRun][SMOKE] 已选择 10K / Normal / 各容器代表操作，用例数 {BuildSelectedCases().Count}");
        }

        private static ProfilerRecorder StartGcAllocationRecorder()
        {
            ProfilerRecorder recorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Memory, GcAllocatedCounterName, 1, GcRecorderOptions);
            if (!recorder.Valid)
            {
                recorder.Dispose();
                throw new InvalidOperationException(
                    $"ProfilerRecorder counter '{GcAllocatedCounterName}' 在当前 Player 不可用");
            }
            return recorder;
        }

        private static long CalibrateGcAllocationCounter()
        {
            ProfilerRecorder recorder = StartGcAllocationRecorder();
            try
            {
                long before = recorder.CurrentValue;
                var probe = new byte[4096];
                probe[0] = 0x5a;
                long after = recorder.CurrentValue;
                System.GC.KeepAlive(probe);
                long delta = after - before;
                if (delta < probe.Length)
                {
                    throw new InvalidOperationException(
                        $"'{GcAllocatedCounterName}' 未观测到校准分配（delta={delta} B）");
                }
                return delta;
            }
            finally
            {
                recorder.Stop();
                recorder.Dispose();
            }
        }

        private static string ScriptingBackendLabel()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#else
            return Application.isEditor ? "Editor runtime (Player target checked separately)" : "Mono";
#endif
        }

        // ==================== 勾选状态初始化 ====================

        private SelectionState BuildInitialSelection()
        {
            var s = new SelectionState { EnableJob = enableJob };
            InitSet(s.Containers, selectedContainers, Universe.Select(c => c.ContainerName));
            InitSet(s.Operations, selectedOperations, Universe.Select(c => c.OperationCode));
            InitSet(s.Scales, selectedScales, Universe.Select(c => c.Scale));
            InitSet(s.ValueTypes, selectedValueTypes, Universe.Select(c => c.ValueType));
            InitSet(s.Collisions, selectedCollisions, Universe.Select(c => c.Collision));
            return s;
        }

        private static void InitSet<T>(HashSet<T> target, ICollection<T> serialized, IEnumerable<T> all)
        {
            // 序列化列表留空 → 全部勾选（默认全开）；非空 → 以其为准并与全集取交（剔除无效项）
            if (serialized == null || serialized.Count == 0)
            {
                target.UnionWith(all);
                return;
            }

            foreach (T v in serialized)
            {
                if (v != null)
                {
                    target.Add(v);
                }
            }
            target.IntersectWith(all);
        }

        // ==================== JSON 落盘 ====================

        private void UpdateSuiteProgress(int total, int completed, int failed, int skipped)
        {
            Suite.totalCases = total;
            Suite.completedCases = completed;
            Suite.failedCases = failed;
            Suite.skippedCases = skipped;
        }

        /// <summary>
        /// 显式分片每完成一个用例后保存可解析快照。先写同目录临时文件，再通过同卷重命名/替换发布，
        /// 避免进程中断把正式 checkpoint 留成半截 JSON。checkpoint 不生成 HTML，也不自动续跑。
        /// </summary>
        private void SaveCheckpointIfEnabled()
        {
            if (Suite == null || !Suite.checkpointEnabled)
            {
                return;
            }

            string path = Suite.checkpointFile;
            string tempPath = path + ".tmp";
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(tempPath, Suite.ToJson(), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
                _checkpointWriteFailed = false;
            }
            catch (Exception e)
            {
                _checkpointWriteFailed = true;
                LogError($"checkpoint 写入失败 {path}: {e}");
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception cleanupException)
                {
                    LogError($"checkpoint 临时文件清理失败 {tempPath}: {cleanupException.Message}");
                }
            }
        }

        private string SaveJson()
        {
            string path = ResolveJsonPath();
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(path, Suite.ToJson(), new UTF8Encoding(false));
                Log($"[结果] JSON 已写入: {path}");
                return path;
            }
            catch (Exception e)
            {
                LogError($"JSON 写入失败 {path}: {e}");
                return null;
            }
        }

        private string ResolveJsonPath()
        {
            if (!string.IsNullOrWhiteSpace(jsonOutputPath))
            {
                return Path.GetFullPath(jsonOutputPath);
            }
            return Path.Combine(Application.persistentDataPath, BenchmarkConfig.ResultFileName);
        }

        private string ResolveCheckpointPath()
        {
            return Path.ChangeExtension(ResolveJsonPath(), "checkpoint.json");
        }

        // ==================== 维度辅助 ====================

        /// <summary>某类型对应的规模档位（int 类型额外 2M/5M/10M）。</summary>
        public static int[] ScalesFor(BenchmarkValueType valueType)
        {
            if (valueType == BenchmarkValueType.IntVector3)
            {
                var all = new int[BenchmarkConfig.Scales.Length + BenchmarkConfig.IntExtraScales.Length];
                BenchmarkConfig.Scales.CopyTo(all, 0);
                BenchmarkConfig.IntExtraScales.CopyTo(all, BenchmarkConfig.Scales.Length);
                return all;
            }
            return BenchmarkConfig.Scales;
        }

        /// <summary>某类型对应的碰撞档（字符串专项固定 Normal）。</summary>
        private static CollisionProfile[] CollisionsFor(BenchmarkValueType valueType)
        {
            return valueType == BenchmarkValueType.StringKey
                ? new[] { CollisionProfile.Normal }
                : new[]
                {
                    CollisionProfile.Normal,
                    CollisionProfile.LimitedDomain,
                    CollisionProfile.AllCollision,
                };
        }

        // ==================== 命令行解析（参考 CubeSocketBenchmark 模式） ====================

        private void AddCommandLineError(string error)
        {
            if (string.IsNullOrWhiteSpace(_commandLineError))
            {
                _commandLineError = error;
            }
            else
            {
                _commandLineError += "；" + error;
            }
        }

        private static bool HasOption(string[] args, string option)
        {
            string expected = "-" + option;
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase)
                    || argument.StartsWith(expected + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryReadOption(string[] args, string option, out string value)
        {
            string expected = "-" + option;
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (argument.StartsWith(expected + "=", StringComparison.OrdinalIgnoreCase))
                {
                    value = argument.Substring(expected.Length + 1);
                    return true;
                }
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length
                    && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    value = args[i + 1];
                    return true;
                }
            }
            value = null;
            return false;
        }

        // ==================== 内部类型 ====================

        private struct PassSample
        {
            public double ms;
            public long gcBytes;
            public bool gcBytesAvailable;
        }

        private sealed class CaseRunOutcome
        {
            public bool succeeded;
        }

        /// <summary>
        /// 封装普通逐 pass fixture 与显式只读复用 fixture 的两种生命周期。
        /// 主协程只依赖 Execute/TryRelease，小接口隐藏 allocator 与异常清理细节。
        /// </summary>
        private sealed class BenchmarkCaseSession
        {
            private readonly IBenchmarkCase _case;
            private readonly IReusableReadOnlyBenchmarkCase _reusable;
            private bool _setupAttempted;
            private bool _released;

            public BenchmarkCaseSession(IBenchmarkCase benchmarkCase)
            {
                _case = benchmarkCase ?? throw new ArgumentNullException(nameof(benchmarkCase));
                _reusable = benchmarkCase as IReusableReadOnlyBenchmarkCase;
            }

            public PassSample Execute(bool measureGc, out bool valid, out string desc)
            {
                if (_reusable == null)
                {
                    return ExecutePass(_case, measureGc, out valid, out desc);
                }

                if (!_setupAttempted)
                {
                    // 先标记再调用；即使 Setup 部分失败，TryRelease 仍会尝试清理已创建资源。
                    _setupAttempted = true;
                    _case.Setup();
                }
                _reusable.ResetForPass();
                return ExecuteMeasuredPass(_case, measureGc, out valid, out desc);
            }

            public bool TryRelease(out Exception exception)
            {
                exception = null;
                if (_released || _reusable == null || !_setupAttempted)
                {
                    return true;
                }

                _released = true;
                try
                {
                    _case.Teardown();
                    return true;
                }
                catch (Exception e)
                {
                    exception = e;
                    return false;
                }
            }
        }
    }
}
