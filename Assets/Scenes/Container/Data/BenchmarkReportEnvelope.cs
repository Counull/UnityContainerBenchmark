using System;
using System.Collections.Generic;

namespace ContainerBenchmark
{
    /// <summary>
    /// 双通道正式报告的数据 envelope。report 保持 BenchmarkSuiteResult 形状，
    /// 旧报告消费者可以继续读取同一套结果字段；provenance 与 diagnostics 只承载
    /// 汇总来源和严格门禁，不进入 Player 测量路径。
    /// </summary>
    [Serializable]
    public sealed class BenchmarkReportEnvelope
    {
        public string schemaVersion = "container-benchmark-dual-channel-v1";
        public string reportKind = "DualChannelFormal";
        public string createdUtc;
        public BenchmarkSuiteResult report = new BenchmarkSuiteResult();
        public BenchmarkReportProvenance provenance = new BenchmarkReportProvenance();
        public BenchmarkReportDiagnostics diagnostics = new BenchmarkReportDiagnostics();
    }

    /// <summary>合成字段的测量来源以及 6 个不可变输入文件。</summary>
    [Serializable]
    public sealed class BenchmarkReportProvenance
    {
        public string timingSource = "IL2CPP Release Player";
        public string timingStatistic = "Release Player 稳态 10 次采样中位数";
        public string timingBuildGuid;
        public string gcSource = "IL2CPP Development Player";
        public string gcStatistic = "Development Player 稳态 10 次采样 GC 分配中位数";
        public string gcBuildGuid;
        public List<BenchmarkReportSource> sources = new List<BenchmarkReportSource>();
    }

    /// <summary>一个正式输入 JSON 的来源、角色与内容哈希。</summary>
    [Serializable]
    public sealed class BenchmarkReportSource
    {
        public string role;
        public string shard;
        public string sourceFile;
        public string sha256;
        public string checkpointFile;
        public string checkpointSha256;
        public string buildGuid;
        public string buildKind;
        public string measurementChannel;
        public string startedUtc;
        public string endedUtc;
        public int caseCount;
        public int rowCount;
    }

    /// <summary>
    /// 正式汇总门禁的可序列化诊断。passed 只有在 6 源、928 ID、0 skip、
    /// 1856 行以及跨通道逐 ID/逐行严格对齐全部成立时才为 true。
    /// </summary>
    [Serializable]
    public sealed class BenchmarkReportDiagnostics
    {
        public bool passed;
        public int expectedSources = 6;
        public int sourceCount;
        public int expectedCases = 928;
        public int uniqueCases;
        public int expectedSupportedCases = 928;
        public int supportedCases;
        public int expectedSkippedCases;
        public int skippedCases;
        public int expectedRows = 1856;
        public int mergedRows;
        public int missingCases;
        public int duplicateCases;
        public int mismatchCases;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
    }
}
