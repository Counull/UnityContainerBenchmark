using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using ContainerBenchmark;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// 秒级回归入口：沿生产 RunCaseCoroutine 跑一个真实的全碰撞只读用例，
/// 防止 1 次预热 + 10 次样本重复构建同一昂贵容器。
/// </summary>
public static class ContainerBenchmarkSetupCostRegression
{
    private const int ProbeScale = 5_000;
    private const int ValidationScale = 1_000;
    private const int ExpectedReusableCaseCount = 29;
    private const string Prefix = "[SETUP-COST-PROBE]";

    [MenuItem("Container/Setup 成本回归(5K 全碰撞)")]
    public static void RunHeadless()
    {
        var host = new GameObject("ContainerBenchmarkSetupCostRegression");
        var benchmark = host.AddComponent<global::ContainerBenchmark.ContainerBenchmark>();
        try
        {
            SetPrivateField(benchmark, "_measureGc", false);

            var reusableCases = new List<IBenchmarkCase>();
            reusableCases.AddRange(BenchmarkCaseCatalog.CreateAll(
                ValidationScale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, useJob: true));
            reusableCases.AddRange(BenchmarkCaseCatalog.CreateAll(
                ValidationScale, BenchmarkValueType.StringKey, CollisionProfile.Normal, useJob: true));
            reusableCases = reusableCases
                .Where(testCase => testCase.IsSupported && testCase is IReusableReadOnlyBenchmarkCase)
                .ToList();
            if (reusableCases.Count != ExpectedReusableCaseCount)
            {
                throw new InvalidOperationException(
                    $"{Prefix} 可复用案例目录应为 {ExpectedReusableCaseCount}，实际 {reusableCases.Count}");
            }

            foreach (IBenchmarkCase inner in reusableCases)
            {
                CountingCase probe = RunProductionCase(benchmark, inner, out double wallMs);
                AssertLifecycle(probe, wallMs);
            }
            Debug.Log($"{Prefix} lifecycle-matrix={reusableCases.Count}/{ExpectedReusableCaseCount} passed");

            var throwingCase = new ThrowOnSecondRunCase();
            CountingCase throwingProbe = RunProductionCase(
                benchmark, throwingCase, out _, expectSuccess: false);
            AssertExceptionalRelease(benchmark.Suite, throwingProbe);
            Debug.Log($"{Prefix} exceptional-release=passed, "
                      + $"setup/reset/run/validate/teardown={throwingProbe.SetupCount}/{throwingProbe.ResetCount}/{throwingProbe.RunCount}/{throwingProbe.ValidateCount}/{throwingProbe.TeardownCount}");

            var setupAndReleaseFailure = new SetupAndTeardownThrowCase();
            CountingCase setupFailureProbe = RunProductionCase(
                benchmark, setupAndReleaseFailure, out _, expectSuccess: false);
            AssertSetupAndReleaseFailure(benchmark.Suite, setupFailureProbe);
            AssertOrdinaryDualFailurePreserved();
            Debug.Log($"{Prefix} dual-failure-preservation=passed");

            IBenchmarkCase costCase = BenchmarkCaseCatalog
                .CreateAll(ProbeScale, BenchmarkValueType.IntVector3, CollisionProfile.AllCollision, useJob: true)
                .Single(testCase => testCase.OperationCode == "C5" && testCase.ContainerName == "HashSet");
            CountingCase costProbe = RunProductionCase(benchmark, costCase, out double costWallMs);
            AssertLifecycle(costProbe, costWallMs);

            double setupMs = TicksToMilliseconds(costProbe.SetupTicks);
            double runMs = TicksToMilliseconds(costProbe.RunTicks);
            double teardownMs = TicksToMilliseconds(costProbe.TeardownTicks);
            double setupShare = costWallMs <= 0d ? 0d : setupMs / costWallMs;
            Debug.Log($"{Prefix} case={costCase.Id}, passes={ExpectedPasses}, "
                      + $"setup/reset/run/validate/teardown={costProbe.SetupCount}/{costProbe.ResetCount}/{costProbe.RunCount}/{costProbe.ValidateCount}/{costProbe.TeardownCount}, "
                      + $"setup={setupMs:F2}ms, timed-run={runMs:F2}ms, teardown={teardownMs:F2}ms, "
                      + $"wall={costWallMs:F2}ms, setup-share={setupShare:P1}");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static int ExpectedPasses => BenchmarkConfig.WarmupCount + BenchmarkConfig.SampleCount;

    private static CountingCase RunProductionCase(
        global::ContainerBenchmark.ContainerBenchmark benchmark,
        IBenchmarkCase inner,
        out double wallMs,
        bool expectSuccess = true)
    {
        var probe = new CountingCase(inner);
        SetPrivateField(benchmark, "<Suite>k__BackingField", new BenchmarkSuiteResult());

        Type benchmarkType = typeof(global::ContainerBenchmark.ContainerBenchmark);
        Type outcomeType = benchmarkType.GetNestedType("CaseRunOutcome", BindingFlags.NonPublic);
        MethodInfo runCase = benchmarkType.GetMethod(
            "RunCaseCoroutine", BindingFlags.Instance | BindingFlags.NonPublic);
        if (outcomeType == null || runCase == null)
            throw new MissingMethodException("无法定位生产 RunCaseCoroutine/CaseRunOutcome 回归缝");

        object outcome = Activator.CreateInstance(outcomeType);
        var routine = (IEnumerator)runCase.Invoke(benchmark, new[] { (object)probe, outcome });
        var wall = Stopwatch.StartNew();
        try
        {
            while (routine.MoveNext())
            {
                // RunCaseCoroutine 只在 pass 之间 yield null；手动推进即可复用生产执行路径。
            }
        }
        finally
        {
            wall.Stop();
            (routine as IDisposable)?.Dispose();
        }
        wallMs = wall.Elapsed.TotalMilliseconds;

        FieldInfo succeededField = outcomeType.GetField("succeeded", BindingFlags.Instance | BindingFlags.Public);
        bool succeeded = succeededField != null && (bool)succeededField.GetValue(outcome);
        bool resultsValid = benchmark.Suite != null
                            && benchmark.Suite.results.Count == 2
                            && benchmark.Suite.results.All(result => result.validated && !result.skipped);
        if (expectSuccess && (!succeeded || !resultsValid))
        {
            throw new InvalidOperationException(
                $"{Prefix} 生产结果或逐 pass 校验失败：case={inner.Id}, succeeded={succeeded}, resultsValid={resultsValid}");
        }
        if (!expectSuccess && succeeded)
            throw new InvalidOperationException($"{Prefix} 故障注入未使生产执行器进入失败路径：case={inner.Id}");
        return probe;
    }

    private static void AssertExceptionalRelease(BenchmarkSuiteResult suite, CountingCase probe)
    {
        bool resultShapeValid = suite != null
                                && suite.results.Count == 2
                                && suite.results[0].isWarmup
                                && suite.results[0].validated
                                && !suite.results[1].isWarmup
                                && !suite.results[1].validated
                                && suite.results[1].validateDesc?.Contains(ThrowOnSecondRunCase.FailureMessage) == true;
        if (!resultShapeValid)
            throw new InvalidOperationException($"{Prefix} 异常路径必须保留预热结果并记录单个稳态失败结果");

        if (probe.SetupCount != 1 || probe.TeardownCount != 1)
            throw new InvalidOperationException(
                $"{Prefix} 异常路径也必须恰好释放一次 fixture：setup={probe.SetupCount}, teardown={probe.TeardownCount}");
        if (probe.ResetCount != 2 || probe.RunCount != 2 || probe.ValidateCount != 1)
            throw new InvalidOperationException(
                $"{Prefix} 故障必须发生在首个稳态 Run：reset/run/validate={probe.ResetCount}/{probe.RunCount}/{probe.ValidateCount}");
    }

    private static void AssertSetupAndReleaseFailure(BenchmarkSuiteResult suite, CountingCase probe)
    {
        string descriptions = suite == null
            ? string.Empty
            : string.Join(" | ", suite.results.Select(result => result?.validateDesc ?? string.Empty));
        bool resultShapeValid = suite != null
                                && suite.results.Count == 2
                                && !suite.results[0].validated
                                && !suite.results[1].validated
                                && descriptions.Contains(SetupAndTeardownThrowCase.SetupFailureMessage)
                                && descriptions.Contains(SetupAndTeardownThrowCase.TeardownFailureMessage);
        if (!resultShapeValid)
            throw new InvalidOperationException($"{Prefix} Setup/Teardown 双故障必须同时保留在结果诊断中");
        if (probe.SetupCount != 1 || probe.ResetCount != 0 || probe.RunCount != 0
            || probe.ValidateCount != 0 || probe.TeardownCount != 1)
        {
            throw new InvalidOperationException(
                $"{Prefix} Setup 部分失败也必须恰好尝试一次释放："
                + $"setup/reset/run/validate/teardown={probe.SetupCount}/{probe.ResetCount}/{probe.RunCount}/{probe.ValidateCount}/{probe.TeardownCount}");
        }
    }

    private static void AssertOrdinaryDualFailurePreserved()
    {
        Type benchmarkType = typeof(global::ContainerBenchmark.ContainerBenchmark);
        MethodInfo executePass = benchmarkType.GetMethod(
            "ExecutePass", BindingFlags.Static | BindingFlags.NonPublic);
        if (executePass == null)
            throw new MissingMethodException("无法定位生产 ExecutePass 异常回归缝");

        var testCase = new OrdinaryDualFailureCase();
        Exception observed = null;
        try
        {
            executePass.Invoke(null, new object[] { testCase, false, false, string.Empty });
        }
        catch (TargetInvocationException e)
        {
            observed = e.InnerException;
        }

        AggregateException aggregate = observed as AggregateException;
        string messages = aggregate == null
            ? string.Empty
            : string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(exception => exception.Message));
        if (aggregate == null
            || !messages.Contains(OrdinaryDualFailureCase.RunFailureMessage)
            || !messages.Contains(OrdinaryDualFailureCase.TeardownFailureMessage))
        {
            throw new InvalidOperationException($"{Prefix} 普通用例的执行异常与释放异常必须同时保留");
        }
        if (testCase.SetupCount != 1 || testCase.RunCount != 1
            || testCase.ValidateCount != 0 || testCase.TeardownCount != 1)
        {
            throw new InvalidOperationException($"{Prefix} 普通双故障调用计数不正确");
        }
    }

    private static void AssertLifecycle(CountingCase probe, double wallMs)
    {
        string summary = $"case={probe.Id}, passes={ExpectedPasses}, "
                         + $"setup/reset/run/validate/teardown={probe.SetupCount}/{probe.ResetCount}/{probe.RunCount}/{probe.ValidateCount}/{probe.TeardownCount}, "
                         + $"wall={wallMs:F2}ms";

        if (probe.RunCount != ExpectedPasses || probe.ValidateCount != ExpectedPasses)
            throw new InvalidOperationException($"{Prefix} 生产采样次数失真：{summary}");
        if (probe.ResetCount != ExpectedPasses)
            throw new InvalidOperationException($"{Prefix} 每个 pass 必须在计时外独立复位：{summary}");
        if (probe.SetupCount != 1 || probe.TeardownCount != 1)
            throw new InvalidOperationException(
                $"{Prefix} 只读案例必须只构建/释放一次昂贵 fixture，当前发生重复 setup：{summary}");
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
            throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000d / Stopwatch.Frequency;
    }

    private sealed class CountingCase : IBenchmarkCase, IReusableReadOnlyBenchmarkCase
    {
        private readonly IBenchmarkCase _inner;
        private readonly IReusableReadOnlyBenchmarkCase _reusable;

        public CountingCase(IBenchmarkCase inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _reusable = inner as IReusableReadOnlyBenchmarkCase
                        ?? throw new ArgumentException("回归案例必须显式支持只读 fixture 复用", nameof(inner));
        }

        public int SetupCount { get; private set; }
        public int ResetCount { get; private set; }
        public int RunCount { get; private set; }
        public int ValidateCount { get; private set; }
        public int TeardownCount { get; private set; }
        public long SetupTicks { get; private set; }
        public long RunTicks { get; private set; }
        public long TeardownTicks { get; private set; }

        public string Id => _inner.Id;
        public ContainerFamily Family => _inner.Family;
        public string ContainerName => _inner.ContainerName;
        public string OperationName => _inner.OperationName;
        public string OperationCode => _inner.OperationCode;
        public int Scale => _inner.Scale;
        public BenchmarkValueType ValueType => _inner.ValueType;
        public CollisionProfile Collision => _inner.Collision;
        public bool UseJob => _inner.UseJob;
        public bool SupportsJob => _inner.SupportsJob;
        public bool IsNative => _inner.IsNative;
        public bool IsSupported => _inner.IsSupported;
        public string UnsupportedReason => _inner.UnsupportedReason;
        public string SemanticNote => _inner.SemanticNote;
        public ChecksumSink Checksum => _inner.Checksum;
        public long TimedOperationCount => _inner.TimedOperationCount;

        public void Setup()
        {
            SetupCount++;
            var watch = Stopwatch.StartNew();
            try
            {
                _inner.Setup();
            }
            finally
            {
                watch.Stop();
                SetupTicks += watch.ElapsedTicks;
            }
        }

        public void RunOnePass()
        {
            RunCount++;
            var watch = Stopwatch.StartNew();
            try
            {
                _inner.RunOnePass();
            }
            finally
            {
                watch.Stop();
                RunTicks += watch.ElapsedTicks;
            }
        }

        public void ResetForPass()
        {
            ResetCount++;
            _reusable.ResetForPass();
        }

        public bool Validate(out string desc)
        {
            ValidateCount++;
            return _inner.Validate(out desc);
        }

        public void Teardown()
        {
            TeardownCount++;
            var watch = Stopwatch.StartNew();
            try
            {
                _inner.Teardown();
            }
            finally
            {
                watch.Stop();
                TeardownTicks += watch.ElapsedTicks;
            }
        }
    }

    private sealed class ThrowOnSecondRunCase : ReusableReadOnlyBenchmarkCaseBase
    {
        public const string FailureMessage = "expected steady-pass failure";

        private int _runCount;

        public ThrowOnSecondRunCase()
            : base("RegressionThrow", false, ContainerFamily.List, "R1", "故障注入", false,
                1, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            _runCount = 0;
        }

        public override void RunOnePass()
        {
            _runCount++;
            if (_runCount == 2)
                throw new InvalidOperationException(FailureMessage);
            Checksum.AddInt(_runCount);
        }

        public override bool Validate(out string desc)
        {
            desc = "故障注入预热校验通过";
            return _runCount == 1;
        }

        public override void Teardown()
        {
        }
    }

    private sealed class SetupAndTeardownThrowCase : ReusableReadOnlyBenchmarkCaseBase
    {
        public const string SetupFailureMessage = "expected partial setup failure";
        public const string TeardownFailureMessage = "expected setup cleanup failure";

        public SetupAndTeardownThrowCase()
            : base("RegressionSetupThrow", false, ContainerFamily.List, "R2", "Setup故障注入", false,
                1, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            throw new InvalidOperationException(SetupFailureMessage);
        }

        public override void RunOnePass()
        {
            throw new InvalidOperationException("Setup 失败后不得进入 RunOnePass");
        }

        public override bool Validate(out string desc)
        {
            desc = "Setup 失败后不得进入 Validate";
            return false;
        }

        public override void Teardown()
        {
            throw new InvalidOperationException(TeardownFailureMessage);
        }
    }

    private sealed class OrdinaryDualFailureCase : BenchmarkCaseBase
    {
        public const string RunFailureMessage = "expected ordinary run failure";
        public const string TeardownFailureMessage = "expected ordinary teardown failure";

        public int SetupCount { get; private set; }
        public int RunCount { get; private set; }
        public int ValidateCount { get; private set; }
        public int TeardownCount { get; private set; }

        public OrdinaryDualFailureCase()
            : base("RegressionOrdinaryThrow", false, ContainerFamily.List, "R3", "普通双故障注入", false,
                1, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            SetupCount++;
        }

        public override void RunOnePass()
        {
            RunCount++;
            throw new InvalidOperationException(RunFailureMessage);
        }

        public override bool Validate(out string desc)
        {
            ValidateCount++;
            desc = "Run 失败后不得进入 Validate";
            return false;
        }

        public override void Teardown()
        {
            TeardownCount++;
            throw new InvalidOperationException(TeardownFailureMessage);
        }
    }
}
