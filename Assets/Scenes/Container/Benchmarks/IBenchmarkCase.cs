using System.Collections.Generic;
namespace ContainerBenchmark
{
    /// <summary>
    /// 测试用例统一接口（阶段 2 主控依赖的唯一入口）。
    /// 默认调用顺序契约：Setup() → [计时区] RunOnePass() → [计时结束] → Validate(out desc) → Teardown()。
    /// 显式实现 IReusableReadOnlyBenchmarkCase 时，Setup/Teardown 提升为整个 1+10 pass 序列各一次，
    /// 每个 pass 仍会在计时外 ResetForPass，并执行 RunOnePass → Validate。
    /// 计时由主控负责，用例内不做任何计时。
    /// </summary>
    public interface IBenchmarkCase
    {
        /// <summary>用例唯一标识（如 "A7-List-10000-IntVector3-Normal"）。</summary>
        string Id { get; }

        /// <summary>容器族。</summary>
        ContainerFamily Family { get; }

        /// <summary>容器名（如 List / NativeList / Dictionary / NativeHashMap...）。</summary>
        string ContainerName { get; }

        /// <summary>操作名（中文，如 尾部追加 / 插入(TryAdd)...）。</summary>
        string OperationName { get; }

        /// <summary>用例编号（A1~F2，字符串专项为 B1S/B2S/B3S/B6S）。</summary>
        string OperationCode { get; }

        /// <summary>规模 N。</summary>
        int Scale { get; }

        /// <summary>数据类型维度。</summary>
        BenchmarkValueType ValueType { get; }

        /// <summary>碰撞档位。</summary>
        CollisionProfile Collision { get; }

        /// <summary>是否 Job 形态用例（A8/B7/C6/C7）。</summary>
        bool UseJob { get; }

        /// <summary>容器族是否支持 Job 维度（A/B/C 为 true；D/E/F 无 Job 形态为 false）。</summary>
        bool SupportsJob { get; }

        /// <summary>是否 Native 容器。</summary>
        bool IsNative { get; }

        /// <summary>是否可执行（false 仅用于当前平台或实现明确不存在的可选形态）。</summary>
        bool IsSupported { get; }

        /// <summary>不支持原因（IsSupported == false 时有效）。</summary>
        string UnsupportedReason { get; }

        /// <summary>语义差异标注（字符串语义、容量/存储布局、可选能力等不可完全对称因素）。</summary>
        string SemanticNote { get; }

        /// <summary>防优化累积器（采样后由主控调用 Escape()）。</summary>
        ChecksumSink Checksum { get; }

        /// <summary>计时区内实际执行的原子操作数；通常为 N，混合入出队/Push+Pop 为 2N。</summary>
        long TimedOperationCount { get; }

        /// <summary>准备状态：重建容器与数据，按可用 API 预分配容量。默认每 pass 调用；可复用只读 fixture 每个 session 调用一次。始终位于计时区外。</summary>
        void Setup();

        /// <summary>纯操作循环（Job 场景含调度 + Complete），不做校验/统计/日志。计时区内调用。</summary>
        void RunOnePass();

        /// <summary>计时区外、Teardown 前调用：按契约校验式检查结果。</summary>
        bool Validate(out string desc);

        /// <summary>释放资源（默认每 pass；可复用只读 fixture 每个 session 一次；Native 容器在任何路径均不得泄漏）。</summary>
        void Teardown();
    }

    /// <summary>
    /// 可在同一预热/采样序列中复用已构建 fixture 的只读用例能力。
    /// 主控仍在每个 pass 的计时区外调用 ResetForPass，并逐次执行校验；
    /// Setup/Teardown 则提升为整个用例序列各一次。实现者必须保证 RunOnePass
    /// 不改变被测容器的可观察状态，且跨帧 Native fixture 使用 Persistent 分配器。
    /// </summary>
    public interface IReusableReadOnlyBenchmarkCase
    {
        void ResetForPass();
    }

    /// <summary>
    /// 用例基类：承载元数据与 ChecksumSink。
    /// </summary>
    public abstract class BenchmarkCaseBase : IBenchmarkCase
    {
        public string Id { get; }
        public ContainerFamily Family { get; }
        public string ContainerName { get; }
        public string OperationName { get; }
        public string OperationCode { get; }
        public int Scale { get; }
        public BenchmarkValueType ValueType { get; }
        public CollisionProfile Collision { get; }
        public bool UseJob { get; }
        public bool SupportsJob { get; }
        public bool IsNative { get; }
        public ChecksumSink Checksum { get; } = new ChecksumSink();
        public virtual long TimedOperationCount => Scale;

        public virtual bool IsSupported => true;
        public virtual string UnsupportedReason => string.Empty;
        public virtual string SemanticNote => string.Empty;

        protected BenchmarkCaseBase(
            string containerName, bool isNative, ContainerFamily family,
            string operationCode, string operationName, bool supportsJob,
            int scale, BenchmarkValueType valueType, CollisionProfile collision, bool useJob)
        {
            ContainerName = containerName;
            IsNative = isNative;
            Family = family;
            OperationCode = operationCode;
            OperationName = operationName;
            SupportsJob = supportsJob;
            Scale = scale;
            ValueType = valueType;
            Collision = collision;
            UseJob = useJob;
            Id = $"{operationCode}-{containerName}-{scale}-{valueType}-{collision}";
        }

        public abstract void Setup();
        public abstract void RunOnePass();
        public abstract bool Validate(out string desc);
        public abstract void Teardown();
    }

    /// <summary>
    /// 只读 fixture 复用用例的公共基类。把每 pass 的可变观测状态收敛为 Checksum，
    /// 昂贵容器构建仍由具体 Setup 实现，最终释放仍由具体 Teardown 实现。
    /// </summary>
    public abstract class ReusableReadOnlyBenchmarkCaseBase : BenchmarkCaseBase,
        IReusableReadOnlyBenchmarkCase
    {
        protected ReusableReadOnlyBenchmarkCaseBase(
            string containerName, bool isNative, ContainerFamily family,
            string operationCode, string operationName, bool supportsJob,
            int scale, BenchmarkValueType valueType, CollisionProfile collision, bool useJob)
            : base(containerName, isNative, family, operationCode, operationName, supportsJob,
                scale, valueType, collision, useJob)
        {
        }

        public virtual void ResetForPass()
        {
            Checksum.Reset();
        }
    }

    /// <summary>
    /// 用例目录（静态注册列表，阶段 2 主控通过它发现用例，不硬编码）：
    /// 按 规模 × 类型 × 碰撞档 × Job 开关 构建全部用例（含"不支持"标记用例，由主控决定跳过或记录）。
    /// </summary>
    public static class BenchmarkCaseCatalog
    {
        /// <summary>
        /// 构建某规模下的全部用例。
        /// </summary>
        /// <param name="scale">规模 N。</param>
        /// <param name="valueType">数据类型维度（IntVector3 / StringKey）。</param>
        /// <param name="collision">碰撞档（仅 B/C 的 int-key 场景生效；字符串专项固定 Normal）。</param>
        /// <param name="useJob">Job 维度开关（默认开）。</param>
        public static List<IBenchmarkCase> CreateAll(
            int scale, BenchmarkValueType valueType, CollisionProfile collision, bool useJob)
        {
            var cases = new List<IBenchmarkCase>(64);
            if (valueType == BenchmarkValueType.IntVector3)
            {
                // 碰撞维度只作用于 B/C。非哈希族仅在 Normal 档构建一次，
                // 避免调用方遍历三种碰撞档时创建重复对象与重复数据。
                if (collision == CollisionProfile.Normal)
                {
                    cases.AddRange(ListBenchmarkCases.CreateAll(scale, useJob));
                    cases.AddRange(QueueBenchmarkCases.CreateAll(scale));
                    cases.AddRange(LinkedListBenchmarkCases.CreateAll(scale));
                    cases.AddRange(StackApproxBenchmarkCases.CreateAll(scale));
                }

                cases.AddRange(DictionaryBenchmarkCases.CreateAll(scale, collision, useJob));
                cases.AddRange(HashSetBenchmarkCases.CreateAll(scale, collision, useJob));
            }
            else if (collision == CollisionProfile.Normal)
            {
                // StringKey：字符串专项，仅测正常形态 B1/B2/B3/B6。
                cases.AddRange(DictionaryBenchmarkCases.CreateStringCases(scale));
            }

            return cases;
        }
    }
}
