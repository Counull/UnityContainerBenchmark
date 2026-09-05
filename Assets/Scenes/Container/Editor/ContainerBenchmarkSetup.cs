using System;using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ContainerBenchmark;
// 命名空间 ContainerBenchmark 与主控类同名：用别名避免 CS0118（命名空间优先解析）
using Bench = ContainerBenchmark.ContainerBenchmark;

/// <summary>
/// 阶段 2 编辑器辅助：
/// 1) 菜单「Container → 搭建基准场景(Canvas+主控+UI)」：一键创建 Canvas(UGUI) + EventSystem +
///    挂载 ContainerBenchmark / ContainerBenchmarkUI，配置默认参数（供阶段 3 使用）。
///    说明：UI 树由 ContainerBenchmarkUI 在运行时的 Start() 中用代码构建（单一实现源，避免双份维护）。
/// 2) 菜单「Container → 生成假测试数据并导出HTML」：脱离阶段 1 自测导出器全链路（假数据 → 内联 HTML）。
/// </summary>
public static class ContainerBenchmarkSetup
{
    private const string RootObjectName = "ContainerBenchmark";

    [MenuItem("Container/搭建基准场景(Canvas+主控+UI)")]
    public static void CreateBenchmarkScene()
    {
        // ---- Canvas ----
        Canvas canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
        if (canvas == null)
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");
            canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
        }

        // ---- EventSystem（旧输入系统 StandaloneInputModule） ----
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
        }

        // ---- 主方向光（保持场景具备标准可视化/截图环境；Overlay UI 本身不依赖它） ----
        Light mainLight = UnityEngine.Object.FindFirstObjectByType<Light>();
        if (mainLight == null)
        {
            var lightGo = new GameObject("Directional Light", typeof(Light));
            Undo.RegisterCreatedObjectUndo(lightGo, "Create Directional Light");
            mainLight = lightGo.GetComponent<Light>();
            mainLight.type = LightType.Directional;
            mainLight.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
        RenderSettings.sun = mainLight;

        // ---- 主控 + UI ----
        GameObject benchGo = GameObject.Find(RootObjectName);
        if (benchGo == null)
        {
            benchGo = new GameObject(RootObjectName);
            Undo.RegisterCreatedObjectUndo(benchGo, "Create " + RootObjectName);
        }

        Bench bench = benchGo.GetComponent<Bench>();
        if (bench == null)
        {
            bench = Undo.AddComponent<Bench>(benchGo);
        }
        // 默认参数：全部勾选（留空列表）、Job 开、非自动运行
        bench.selectedContainers = new List<string>();
        bench.selectedOperations = new List<string>();
        bench.selectedScales = new List<int>();
        bench.selectedValueTypes = new List<BenchmarkValueType>();
        bench.selectedCollisions = new List<CollisionProfile>();
        bench.enableJob = BenchmarkConfig.DefaultEnableJob;
        bench.autoRun = false;
        bench.jsonOutputPath = string.Empty;

        if (benchGo.GetComponent<ContainerBenchmarkUI>() == null)
        {
            Undo.AddComponent<ContainerBenchmarkUI>(benchGo);
        }

        Selection.activeGameObject = benchGo;
        Debug.Log("[ContainerBenchmark] 基准场景搭建完成：Canvas + EventSystem + " + RootObjectName +
                  "（主控 ContainerBenchmark + 四段式 UI ContainerBenchmarkUI）。UI 树在运行时构建，进入播放模式即可见。");
    }

    [MenuItem("Container/生成假测试数据并导出HTML")]
    public static void ExportFakeHtml()
    {
        try
        {
            BenchmarkSuiteResult suite = FakeDataFactory.CreateFakeSuite();
            string path = HtmlReportExporter.Export(suite, HtmlReportExporter.DefaultOutputPath);
            Debug.Log($"[ContainerBenchmark] 假数据 HTML 已导出: {path}");
            EditorUtility.DisplayDialog("Container 基准测试", "假数据 HTML 已导出:\n" + path, "确定");
        }
        catch (Exception e)
        {
            Debug.LogError("[ContainerBenchmark] 假数据导出失败: " + e);
            EditorUtility.DisplayDialog("Container 基准测试", "导出失败:\n" + e.Message, "确定");
        }
    }

    /// <summary>
    /// 假数据工厂（固定种子、确定性）：覆盖 List/Dictionary/HashSet/Queue/LinkedList/Stack/字符串专项，
    /// 多规模、碰撞三档、含 Job 用例、预热样本、失败与跳过示例，用于脱离阶段 1 自测导出器。
    /// </summary>
    public static class FakeDataFactory
    {
        public static BenchmarkSuiteResult CreateFakeSuite()
        {
            var rnd = new System.Random(20260826);
            var suite = new BenchmarkSuiteResult
            {
                suiteName = "ContainerBenchmark-Fake",
                unityVersion = Application.unityVersion,
                startedUtc = DateTime.UtcNow.AddMinutes(-8).ToString("o", CultureInfo.InvariantCulture),
                endedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                jobEnabled = true,
                platform = Application.platform.ToString(),
                scriptingBackend = "FakeData",
                buildKind = "Exporter self-test",
                runMode = "Fake",
                buildGuid = Application.buildGUID,
                operatingSystem = SystemInfo.operatingSystem,
                processorType = SystemInfo.processorType,
                processorCount = SystemInfo.processorCount,
                systemMemoryMB = SystemInfo.systemMemorySize,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                graphicsMemoryMB = SystemInfo.graphicsMemorySize,
                gcMetric = "Fake deterministic fixture",
                gcMetricCalibrated = true,
                gcCalibrationBytes = 4096,
            };

            // (容器, 是否Native, 族, 编号, 操作名, 基准ns/op, 基准GC字节/10K, 是否测碰撞)
            var defs = new List<(string container, bool isNative, ContainerFamily family, string code, string name,
                double baseNs, long baseGc, bool collisionAware)>
            {
                ("List", false, ContainerFamily.List, "A1", "尾部追加", 8, 0, false),
                ("NativeList", true, ContainerFamily.List, "A1", "尾部追加", 3, 0, false),
                ("List", false, ContainerFamily.List, "A7", "索引遍历求和", 1.5, 0, false),
                ("NativeList", true, ContainerFamily.List, "A7", "索引遍历求和", 1.8, 0, false),
                ("List", false, ContainerFamily.List, "A8", "Job遍历求和", 2.5, 0, false),
                ("NativeList", true, ContainerFamily.List, "A8", "Job遍历求和", 0.8, 0, false),
                ("Dictionary", false, ContainerFamily.Dictionary, "B1", "插入(TryAdd)", 60, 48, true),
                ("NativeHashMap", true, ContainerFamily.Dictionary, "B1", "插入(TryAdd)", 40, 0, true),
                ("Dictionary", false, ContainerFamily.Dictionary, "B2", "命中查询", 45, 0, true),
                ("NativeHashMap", true, ContainerFamily.Dictionary, "B2", "命中查询", 12, 0, true),
                ("Dictionary", false, ContainerFamily.Dictionary, "B7", "Job遍历", 30, 32, true),
                ("NativeHashMap", true, ContainerFamily.Dictionary, "B7", "Job遍历", 6, 0, true),
                ("HashSet", false, ContainerFamily.HashSet, "C1", "插入(Add)", 55, 40, true),
                ("NativeParallelHashSet", true, ContainerFamily.HashSet, "C1", "插入(Add)", 38, 0, true),
                ("HashSet", false, ContainerFamily.HashSet, "C2", "命中查询", 42, 0, true),
                ("NativeParallelHashSet", true, ContainerFamily.HashSet, "C2", "命中查询", 10, 0, true),
                ("Queue", false, ContainerFamily.Queue, "D1", "入队(Enqueue)", 10, 24, false),
                ("NativeQueue", true, ContainerFamily.Queue, "D1", "入队(Enqueue)", 5, 0, false),
                ("LinkedList", false, ContainerFamily.LinkedList, "E1", "尾部追加", 55, 64, false),
                ("LinkedList", false, ContainerFamily.LinkedList, "E4", "遍历", 30, 0, false),
                ("List(当栈)", false, ContainerFamily.StackApprox, "F1", "栈Push/Pop", 12, 0, false),
                ("NativeList(当栈)", true, ContainerFamily.StackApprox, "F2", "栈Push/Pop", 6, 0, false),
                ("Dictionary(string)", false, ContainerFamily.Dictionary, "B1S", "插入(TryAdd)", 90, 60, false),
                ("NativeHashMap(FixedString64)", true, ContainerFamily.Dictionary, "B1S", "插入(TryAdd)", 70, 0, false),
            };

            int[] scales = { 10_000, 100_000, 1_000_000, 2_000_000 };
            var profiles = new[] { CollisionProfile.Normal, CollisionProfile.LimitedDomain, CollisionProfile.AllCollision };

            foreach (var def in defs)
            {
                foreach (int scale in scales)
                {
                    var cols = def.collisionAware ? profiles : new[] { CollisionProfile.Normal };
                    foreach (CollisionProfile col in cols)
                    {
                        double colFactor = col == CollisionProfile.Normal ? 1d
                            : col == CollisionProfile.LimitedDomain ? 1.4d : 3.2d;
                        double jitter = 0.9 + rnd.NextDouble() * 0.2;
                        double medianMs = def.baseNs * (1 + Math.Log10(scale / 10000.0) * 0.12) * colFactor * jitter * scale / 1e6;
                        long gc = (long)(def.baseGc * (scale / 10000.0) * (col == CollisionProfile.AllCollision ? 2 : 1) * jitter);

                        suite.results.Add(MakeResult(def, scale, col, false, rnd, medianMs, gc));
                        suite.results.Add(MakeResult(def, scale, col, true, rnd, medianMs * 1.35, gc));
                    }
                }
            }

            // 两个失败示例（校验失败标红展示）
            if (suite.results.Count > 7)
            {
                suite.results[7].validated = false;
                suite.results[7].validateDesc = "示例失败：Count=9999，期望 10000";
            }
            if (suite.results.Count > 23)
            {
                suite.results[23].validated = false;
                suite.results[23].validateDesc = "示例失败：累加和=49995001，期望 49995000";
            }

            // 一个与真实容器契约无关的通用跳过示例，用于验证报告空态/标色。
            var skip = new BenchmarkCaseResult
            {
                id = "X0-UnsupportedFixture-10000-IntVector3-Normal",
                operationCode = "X0",
                family = "Fixture",
                container = "UnsupportedFixture",
                operation = "平台能力示例",
                scale = 10000,
                valueType = BenchmarkValueType.IntVector3.ToString(),
                collision = CollisionProfile.Normal.ToString(),
                useJob = false,
                isNative = false,
                isWarmup = false,
                skipped = true,
                skipReason = "示例：目标平台未提供该可选能力",
                gcBytes = -1L,
                gcBytesAvailable = false,
                validated = false,
                validateDesc = "跳过：目标平台未提供该可选能力（示例）",
            };
            suite.results.Add(skip);
            return suite;
        }

        private static BenchmarkCaseResult MakeResult(
            (string container, bool isNative, ContainerFamily family, string code, string name,
                double baseNs, long baseGc, bool collisionAware) def,
            int scale, CollisionProfile col, bool isWarmup, System.Random rnd, double medianMs, long gc)
        {
            int sampleCount = isWarmup ? 1 : BenchmarkConfig.SampleCount;
            var samples = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = medianMs * (0.95 + rnd.NextDouble() * 0.1);
            }
            Array.Sort(samples);

            double sum = 0d;
            foreach (double s in samples)
            {
                sum += s;
            }
            double mean = sum / sampleCount;
            double median = sampleCount % 2 == 1
                ? samples[sampleCount / 2]
                : (samples[sampleCount / 2 - 1] + samples[sampleCount / 2]) / 2d;

            bool jobForm = def.code == "A8" || def.code == "B7" || def.code == "C6" || def.code == "C7";
            bool stringForm = def.code.EndsWith("S", StringComparison.Ordinal);
            long timedOps = def.family == ContainerFamily.StackApprox ? (long)scale * 2L : scale;
            var r = new BenchmarkCaseResult
            {
                id = $"{def.code}-{def.container}-{scale}-{(stringForm ? "StringKey" : "IntVector3")}-{col}",
                operationCode = def.code,
                family = def.family.ToString(),
                container = def.container,
                operation = def.name,
                scale = scale,
                valueType = stringForm ? BenchmarkValueType.StringKey.ToString() : BenchmarkValueType.IntVector3.ToString(),
                collision = col.ToString(),
                useJob = jobForm,
                isNative = def.isNative,
                isWarmup = isWarmup,
                sampleMs = samples,
                medianMs = median,
                meanMs = mean,
                p95Ms = samples[sampleCount - 1],
                totalMs = sum,
                nsPerOp = median * 1e6 / timedOps,
                gcBytes = gc,
                gcBytesAvailable = true,
                validated = true,
                validateDesc = isWarmup ? "预热样本（不计入统计，示例）" : "校验通过（示例数据）",
                semanticNote = stringForm ? "示例：引用类型 vs 定长 64 字节、哈希算法不同" : string.Empty,
                timedOperationCount = timedOps,
            };
            return r;
        }
    }
}
