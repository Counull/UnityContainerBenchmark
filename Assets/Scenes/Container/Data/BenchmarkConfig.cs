namespace ContainerBenchmark{
    /// <summary>
    /// 容器性能测试全局配置常量（契约层，阶段 2 主控直接引用）。
    /// </summary>
    public static class BenchmarkConfig
    {
        /// <summary>key 生成固定种子：同一参数两次调用生成完全一致的序列（可复现）。</summary>
        public const int DefaultSeed = 20260826;

        /// <summary>稳态采样次数（主指标取中位数）。</summary>
        public const int SampleCount = 10;

        /// <summary>预热次数（不计入统计，但数据单独记录）。</summary>
        public const int WarmupCount = 1;

        /// <summary>Job 维度默认开关（默认开启）。</summary>
        public const bool DefaultEnableJob = true;

        /// <summary>IJobParallelFor 调度 batch 大小。</summary>
        public const int JobInnerLoopBatch = 128;

        /// <summary>结果 JSON 输出文件名。</summary>
        public const string ResultFileName = "ContainerBenchmarkResult.json";

        /// <summary>HTML 报告输出文件名。</summary>
        public const string HtmlReportFileName = "ContainerBenchmarkReport.html";

        /// <summary>
        /// 规模档位：1-2-5 序列 7 档，所有操作通用。
        /// </summary>
        public static readonly int[] Scales =
        {
            10_000, 20_000, 50_000, 100_000, 200_000, 500_000, 1_000_000,
        };

        /// <summary>int 类型额外规模档位（2M/5M/10M）。</summary>
        public static readonly int[] IntExtraScales =
        {
            2_000_000, 5_000_000, 10_000_000,
        };
    }

    /// <summary>
    /// 正式证据环境的单一契约源。构建入口会从真实 Editor 与 lock 文件校验这些值，
    /// 并只为通过校验的 Player 注入 VerifiedScriptingDefine；非正式构建不得自报已验证环境。
    /// </summary>
    public static class BenchmarkEnvironmentContract
    {
        public const string UnityVersion = "6000.5.9f1";
        public const string UnityRevision = "b57deb96f08d";
        public const string FullUnityVersion = "6000.5.9f1 (b57deb96f08d)";
        public const string VerifiedScriptingDefine =
            "CONTAINER_BENCHMARK_ENV_6000_5_9F1_B57DEB96F08D";

        public const string CollectionsManifestVersion = "6.5.0";
        public const string CollectionsResolvedVersion = "6.5.0";
        public const string CollectionsResolvedSource = "builtin";
        public const string BurstResolvedVersion = "1.8.30";
        public const string MathematicsResolvedVersion = "1.4.0";
        public const string PackagesLockSha256 =
            "5496DF13106EE5FE221DD76C28BE21048FA43163AF6C657FF034EE7FE2B779E6";

        public static string RecordedUnityRevision
        {
            get
            {
#if UNITY_EDITOR || CONTAINER_BENCHMARK_ENV_6000_5_9F1_B57DEB96F08D
                return UnityRevision;
#else
                return "unverified";
#endif
            }
        }

        public static string RecordedCollectionsResolvedSource
        {
            get
            {
#if UNITY_EDITOR || CONTAINER_BENCHMARK_ENV_6000_5_9F1_B57DEB96F08D
                return CollectionsResolvedSource;
#else
                return "unverified";
#endif
            }
        }
    }

    /// <summary>哈希碰撞档位。</summary>
    public enum CollisionProfile
    {
        /// <summary>正常散列：hash = id 本身。</summary>
        Normal,

        /// <summary>有限哈希域：hash = id % (N/16)，共有约 N/16 个哈希值、平均约 16 key/hash。</summary>
        LimitedDomain,

        /// <summary>全碰撞：hash = 0，所有 key 落在同一个桶内。</summary>
        AllCollision,
    }

    /// <summary>数据类型维度。</summary>
    public enum BenchmarkValueType
    {
        /// <summary>int key + Vector3 value（纯性能，完全对称）。</summary>
        IntVector3,

        /// <summary>字符串 key 专项（8~32 字符随机，仅测 B1/B2/B3/B6）。</summary>
        StringKey,
    }

    /// <summary>容器族（供阶段 2 结果筛选/排序使用）。</summary>
    public enum ContainerFamily
    {
        List,
        Dictionary,
        HashSet,
        Queue,
        LinkedList,
        StackApprox,
    }
}
