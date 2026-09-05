using System;using System.Collections.Generic;

namespace ContainerBenchmark
{
    /// <summary>
    /// 单条用例的测量结果（阶段 2 主控填充）。
    /// JsonUtility 可序列化：要求 [Serializable] + 公共字段。
    /// </summary>
    [Serializable]
    public class BenchmarkCaseResult
    {
        /// <summary>用例唯一标识（如 "A7-List-10000-IntVector3-Normal"）。</summary>
        public string id;

        /// <summary>用例编号（A1~F2，字符串专项为 B1S/B2S/B3S/B6S）。</summary>
        public string operationCode;

        /// <summary>容器族。</summary>
        public string family;

        /// <summary>容器名（如 List / NativeList / Dictionary / NativeHashMap...）。</summary>
        public string container;

        /// <summary>操作名（如 尾部追加 / 插入(TryAdd)...）。</summary>
        public string operation;

        /// <summary>规模 N。</summary>
        public int scale;

        /// <summary>数据类型维度。</summary>
        public string valueType;

        /// <summary>碰撞档。</summary>
        public string collision;

        /// <summary>是否 Job 形态用例（A8/B7/C6/C7）。</summary>
        public bool useJob;

        /// <summary>是否 Native 容器。</summary>
        public bool isNative;

        /// <summary>是否预热样本（1 次预热单独记录，不计入统计）。</summary>
        public bool isWarmup;

        /// <summary>10 次稳态采样原始耗时数组（毫秒）。</summary>
        public double[] sampleMs = new double[0];

        /// <summary>中位数（毫秒，主指标）。</summary>
        public double medianMs;

        /// <summary>均值（毫秒）。</summary>
        public double meanMs;

        /// <summary>p95（毫秒）。</summary>
        public double p95Ms;

        /// <summary>10 次采样总耗时（毫秒）。</summary>
        public double totalMs;

        /// <summary>每次操作纳秒（ns/op）。</summary>
        public double nsPerOp;

        /// <summary>计时区内执行的原子操作数；用于计算 ns/op。</summary>
        public long timedOperationCount;

        /// <summary>单次采样 GC 分配字节数；-1 表示当前测量通道不可用（N/A）。</summary>
        public long gcBytes = -1L;

        /// <summary>该结果是否包含可信 GC 分配测量。</summary>
        public bool gcBytesAvailable;

        /// <summary>校验结果。</summary>
        public bool validated;

        /// <summary>校验描述（通过说明或失败原因）。</summary>
        public string validateDesc;

        /// <summary>语义差异标注（字符串语义、容量/存储布局、可选能力等不可完全对称因素）。</summary>
        public string semanticNote;

        /// <summary>是否因当前环境或实现明确不支持而跳过。</summary>
        public bool skipped;

        /// <summary>跳过原因。</summary>
        public string skipReason;
    }

    /// <summary>
    /// 整个测试套件的聚合结果（阶段 2 主控聚合落盘 JSON）。
    /// </summary>
    [Serializable]
    public class BenchmarkSuiteResult
    {
        /// <summary>套件名称。</summary>
        public string suiteName = "ContainerBenchmark";

        /// <summary>Unity 版本与正式构建入口校验过的 Editor revision。</summary>
        public string unityVersion;
        public string unityRevision;

        /// <summary>开始时间（UTC，ISO8601）。</summary>
        public string startedUtc;

        /// <summary>结束时间（UTC，ISO8601）。</summary>
        public string endedUtc;

        /// <summary>Job 维度是否开启。</summary>
        public bool jobEnabled = true;

        /// <summary>运行平台与后端，便于确认结果环境。</summary>
        public string platform;
        public string scriptingBackend;
        public string buildKind;

        /// <summary>
        /// 包解析快照。manifest 请求、实际解析版本/来源必须同时保留；
        /// packagesLockSha256 用于证明双通道来自同一依赖图。
        /// </summary>
        public string collectionsManifestRequest;
        public string collectionsResolvedVersion;
        public string collectionsResolvedSource;
        public string burstResolvedVersion;
        public string mathematicsResolvedVersion;
        public string packagesLockSha256;

        /// <summary>运行范围：Full / Smoke / Shard / InteractiveSelection。</summary>
        public string runMode;

        /// <summary>可选自动化分片；空字符串表示未分片。</summary>
        public string benchmarkShard;

        /// <summary>本次实际选择策略，便于识别复杂度裁剪与覆盖范围。</summary>
        public string selectionPolicy;

        /// <summary>测量通道：Combined、TimingOnly 或 DevelopmentGC。</summary>
        public string measurementChannel;

        /// <summary>是否由命令行显式请求纯计时；交互 Release 可自动采用 TimingOnly。</summary>
        public bool timingOnlyRequested;

        /// <summary>逐用例 checkpoint 是否开启，以及最近 checkpoint 文件。</summary>
        public bool checkpointEnabled;
        public string checkpointFile;

        /// <summary>运行状态与累计进度；checkpoint 中可用于判断是否完整。</summary>
        public string runStatus;
        public int totalCases;
        public int completedCases;
        public int failedCases;
        public int skippedCases;

        /// <summary>Player 构建 GUID；Editor 中可能为空。</summary>
        public string buildGuid;

        /// <summary>运行机器与图形设备快照，便于结果复现。</summary>
        public string operatingSystem;
        public string processorType;
        public int processorCount;
        public int systemMemoryMB;
        public string graphicsDeviceName;
        public string graphicsDeviceType;
        public int graphicsMemoryMB;

        /// <summary>GC 指标及启动时校准结果。</summary>
        public string gcMetric;
        public bool gcMetricCalibrated;
        public long gcCalibrationBytes;
        public string environmentError;
        public string environmentWarning;
        public string gcUnavailableReason;

        /// <summary>全部用例结果（含预热记录与跳过记录）。</summary>
        public List<BenchmarkCaseResult> results = new List<BenchmarkCaseResult>();

        /// <summary>序列化为 JSON。</summary>
        public string ToJson()
        {
            return UnityEngine.JsonUtility.ToJson(this);
        }

        /// <summary>从 JSON 反序列化。</summary>
        public static BenchmarkSuiteResult FromJson(string json)
        {
            return UnityEngine.JsonUtility.FromJson<BenchmarkSuiteResult>(json);
        }
    }
}
