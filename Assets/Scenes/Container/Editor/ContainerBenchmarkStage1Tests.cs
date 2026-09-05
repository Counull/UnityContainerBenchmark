using System;using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using ContainerBenchmark;
using Debug = UnityEngine.Debug;

/// <summary>
/// 阶段 1 临时自测入口（可留可删）：
/// 10K 档把每个用例 Setup → RunOnePass → Validate → Teardown 跑一遍，
/// 并自测 HashKeyFactory 可复现性/耗时、JsonUtility 序列化样例、多重集辅助。
/// 菜单：Container → 阶段1自测(10K全用例)
/// </summary>
public static class ContainerBenchmarkStage1Tests
{
    private const int TestScale = 10_000;

    [MenuItem("Container/阶段1自测(10K全用例)")]
    public static void RunStage1SelfTest()
    {
        RunStage1SelfTestInternal(showDialog: !Application.isBatchMode);
    }

    /// <summary>MCP/批处理入口：执行相同断言，但不打开阻塞 Editor 主线程的模态对话框。</summary>
    public static void RunStage1SelfTestHeadless()
    {
        RunStage1SelfTestInternal(showDialog: false);
    }

    private static void RunStage1SelfTestInternal(bool showDialog)
    {
        int total = 0, passed = 0, failed = 0, skipped = 0;
        var failures = new StringBuilder();

        // ---------- 1) HashKeyFactory：三档分别两次生成、逐字段复现、耗时、未命中无交集 ----------
        var sw = new Stopwatch();
        bool reproducible = true;
        bool keyGenFast = true;
        bool missKeysDisjoint = true;
        foreach (CollisionProfile profile in Enum.GetValues(typeof(CollisionProfile)))
        {
            sw.Restart();
            var keysA = HashKeyFactory.CreateKeys(TestScale, profile, BenchmarkConfig.DefaultSeed);
            sw.Stop();
            long firstMs = sw.ElapsedMilliseconds;

            sw.Restart();
            var keysB = HashKeyFactory.CreateKeys(TestScale, profile, BenchmarkConfig.DefaultSeed);
            sw.Stop();
            long secondMs = sw.ElapsedMilliseconds;

            bool profileReproducible = KeysEqual(keysA, keysB);
            bool profileFast = firstMs < 1000 && secondMs < 1000;
            reproducible &= profileReproducible;
            keyGenFast &= profileFast;

            // 未命中 key 与命中集合无交集
            var miss = HashKeyFactory.CreateMissKeys(TestScale, profile, BenchmarkConfig.DefaultSeed);
            var hitSet = new HashSet<int>();
            foreach (var k in keysA)
                hitSet.Add(k.id);
            bool profileDisjoint = true;
            foreach (var k in miss)
            {
                if (hitSet.Contains(k.id))
                {
                    profileDisjoint = false;
                    break;
                }
            }
            missKeysDisjoint &= profileDisjoint;

            string profileSummary = $"HashKeyFactory 10K {profile}: 两次生成 {firstMs}/{secondMs}ms，" +
                                    $"逐字段复现={profileReproducible}，均<1s={profileFast}，未命中无交集={profileDisjoint}";
            if (profileReproducible && profileFast && profileDisjoint)
            {
                Debug.Log("[阶段1] " + profileSummary);
            }
            else
            {
                failures.AppendLine(profileSummary);
                Debug.LogError("[阶段1] " + profileSummary);
            }
        }

        // ---------- 2) 用例矩阵：Setup → RunOnePass → Validate → Teardown ----------
        var allCases = new List<IBenchmarkCase>(256);
        foreach (CollisionProfile profile in Enum.GetValues(typeof(CollisionProfile)))
            allCases.AddRange(BenchmarkCaseCatalog.CreateAll(TestScale, BenchmarkValueType.IntVector3, profile, useJob: true));
        allCases.AddRange(BenchmarkCaseCatalog.CreateAll(TestScale, BenchmarkValueType.StringKey, CollisionProfile.Normal, useJob: true));

        bool duplicateIdsOk = true;
        var ids = new HashSet<string>();
        foreach (IBenchmarkCase testCase in allCases)
        {
            if (!ids.Add(testCase.Id))
            {
                duplicateIdsOk = false;
                failures.AppendLine("重复用例 Id: " + testCase.Id);
                Debug.LogError("[阶段1] 重复用例 Id: " + testCase.Id);
            }
        }

        foreach (var testCase in allCases)
        {
            total++;
            if (!testCase.IsSupported)
            {
                skipped++;
                Debug.Log($"[阶段1] 跳过 {testCase.Id}: {testCase.UnsupportedReason}");
                continue;
            }

            bool validated = false;
            bool teardownOk = true;
            var caseFailure = new StringBuilder();
            string validateDesc = string.Empty;
            try
            {
                testCase.Setup();
                testCase.RunOnePass();
                validated = testCase.Validate(out validateDesc);
                if (!validated)
                {
                    caseFailure.Append("校验失败: ").Append(validateDesc);
                }
            }
            catch (Exception e)
            {
                caseFailure.Append("执行异常: ").Append(e);
            }
            finally
            {
                try
                {
                    testCase.Teardown();
                }
                catch (Exception e)
                {
                    teardownOk = false;
                    if (caseFailure.Length > 0)
                        caseFailure.Append(" | ");
                    caseFailure.Append("Teardown 异常: ").Append(e);
                }
            }

            if (validated && teardownOk)
            {
                passed++;
                Debug.Log($"[阶段1] 通过 {testCase.Id}: {validateDesc}");
            }
            else
            {
                failed++;
                string detail = caseFailure.Length > 0 ? caseFailure.ToString() : "未知失败";
                failures.AppendLine($"失败 {testCase.Id}: {detail}");
                Debug.LogError($"[阶段1] 失败 {testCase.Id}: {detail}");
            }
        }

        // ---------- 3) JsonUtility 序列化样例 ----------
        var suite = new BenchmarkSuiteResult
        {
            unityVersion = Application.unityVersion,
            unityRevision = BenchmarkEnvironmentContract.RecordedUnityRevision,
            collectionsManifestRequest = BenchmarkEnvironmentContract.CollectionsManifestVersion,
            collectionsResolvedVersion = BenchmarkEnvironmentContract.CollectionsResolvedVersion,
            collectionsResolvedSource = BenchmarkEnvironmentContract.RecordedCollectionsResolvedSource,
            packagesLockSha256 = BenchmarkEnvironmentContract.PackagesLockSha256,
            startedUtc = DateTime.UtcNow.ToString("O"),
            jobEnabled = true,
        };
        var sample = new BenchmarkCaseResult
        {
            id = "A7-List-10000-IntVector3-Normal",
            operationCode = "A7",
            family = ContainerFamily.List.ToString(),
            container = "List",
            operation = "遍历读(for累加)",
            scale = 10_000,
            valueType = BenchmarkValueType.IntVector3.ToString(),
            collision = CollisionProfile.Normal.ToString(),
            useJob = false,
            isNative = false,
            sampleMs = new[] { 1.1, 2.2, 3.3, 4.4, 5.5, 6.6, 7.7, 8.8, 9.9, 10.0 },
            medianMs = 5.5,
            meanMs = 5.95,
            p95Ms = 9.9,
            totalMs = 59.5,
            nsPerOp = 5950.0,
            gcBytes = 0,
            gcBytesAvailable = true,
            validated = true,
            validateDesc = "遍历累加和==期望和",
            semanticNote = string.Empty,
        };
        suite.results.Add(sample);
        var gcUnavailableSample = new BenchmarkCaseResult
        {
            id = "A7-List-10000-IntVector3-Normal-TimingOnly",
            operationCode = "A7",
            family = ContainerFamily.List.ToString(),
            container = "List",
            operation = "遍历读(for累加)",
            scale = 10_000,
            valueType = BenchmarkValueType.IntVector3.ToString(),
            collision = CollisionProfile.Normal.ToString(),
            useJob = false,
            isNative = false,
            sampleMs = new[] { 1.0, 1.1, 1.2 },
            medianMs = 1.1,
            meanMs = 1.1,
            p95Ms = 1.2,
            totalMs = 3.3,
            nsPerOp = 110.0,
            gcBytes = -1L,
            gcBytesAvailable = false,
            validated = true,
            validateDesc = "遍历累加和==期望和（GC N/A）",
            semanticNote = "Release timing-only 序列化样例",
        };
        suite.results.Add(gcUnavailableSample);
        string json = string.Empty;
        bool jsonOk;
        try
        {
            json = suite.ToJson();
            BenchmarkSuiteResult restored = BenchmarkSuiteResult.FromJson(json);
            jsonOk = restored != null && restored.results != null && restored.results.Count == 2
                     && restored.unityRevision == BenchmarkEnvironmentContract.UnityRevision
                     && restored.collectionsResolvedSource == BenchmarkEnvironmentContract.CollectionsResolvedSource
                     && restored.packagesLockSha256 == BenchmarkEnvironmentContract.PackagesLockSha256
                     && restored.results[0].sampleMs != null && restored.results[0].sampleMs.Length == 10
                     && restored.results[0].validated
                     && restored.results[0].gcBytesAvailable
                     && restored.results[0].gcBytes == 0L
                     && restored.results[0].semanticNote == string.Empty
                     && Math.Abs(restored.results[0].medianMs - sample.medianMs) < 1e-9
                     && restored.results[1].sampleMs != null && restored.results[1].sampleMs.Length == 3
                     && restored.results[1].validated
                     && !restored.results[1].gcBytesAvailable
                     && restored.results[1].gcBytes == -1L
                     && restored.results[1].semanticNote == gcUnavailableSample.semanticNote;
        }
        catch (Exception e)
        {
            jsonOk = false;
            failures.AppendLine("JsonUtility 往返异常: " + e);
        }
        Debug.Log($"[阶段1] JsonUtility 序列化字段完整={jsonOk}，样例 JSON：{json}");

        // ---------- 4) 多重集辅助自检 ----------
        var listA = new List<int> { 3, 1, 2 };
        var extra = new[] { 0, 4 };
        bool multisetOk = MultiSet.CombinedEqualsRange(listA, extra, 0, 5)
                          && !MultiSet.CombinedEqualsRange(listA, extra, 0, 6);
        Debug.Log($"[阶段1] MultiSet.CombinedEqualsRange 自检通过={multisetOk}");

        // ---------- 5) GC long 中位数纯函数 ----------
        bool longMedianRejectsNegative = false;
        try
        {
            global::ContainerBenchmark.ContainerBenchmark.CalculateLongMedian(new long[] { 0L, -1L, 4L });
        }
        catch (ArgumentOutOfRangeException)
        {
            longMedianRejectsNegative = true;
        }

        bool longMedianOk = global::ContainerBenchmark.ContainerBenchmark.CalculateLongMedian(
                                new long[] { 0L, 4L, 8L, 12L }) == 6L
                            && global::ContainerBenchmark.ContainerBenchmark.CalculateLongMedian(
                                new long[] { 0L, 4L, 8L }) == 4L
                            && longMedianRejectsNegative;
        if (longMedianOk)
        {
            Debug.Log("[阶段2] GC long 中位数自检通过：偶数=6，奇数=4，负数拒绝=True");
        }
        else
        {
            failures.AppendLine("GC long 中位数自检失败：期望偶数=6、奇数=4、负数拒绝=True");
            Debug.LogError("[阶段2] GC long 中位数自检失败");
        }

        // ---------- 6) 阶段 2 复杂度分片：目录计数、互斥性与排除集 ----------
        // 期望非线性分类在本测试内独立表达，避免测试直接复用生产谓词的内部判断而同错。
        var shardUniverse = BuildFullUniverse();
        var smokeOperations = new HashSet<string>
        {
            "A1", "A7", "A8", "B1", "B1S", "C1", "D1", "D3", "E1", "F1", "F2",
        };
        int fullShardCount = 0;
        int emptyShardCount = 0;
        int invalidShardCount = 0;
        int smokeCount = 0;
        int linearSmallCount = 0;
        int linearLargeCount = 0;
        int quadraticStressCount = 0;
        int queueD4SupplementalCount = 0;
        int selectedSupportedCount = 0;
        int selectedUnsupportedCount = 0;
        int overlapCount = 0;
        int excludedCount = 0;
        int expectedExcludedCount = 0;
        int excludedSemanticMismatchCount = 0;
        foreach (IBenchmarkCase testCase in shardUniverse)
        {
            bool linearSmall = global::ContainerBenchmark.ContainerBenchmark.MatchesBenchmarkShard(
                testCase, "linear-small");
            bool linearLarge = global::ContainerBenchmark.ContainerBenchmark.MatchesBenchmarkShard(
                testCase, "linear-large");
            bool quadraticStress = global::ContainerBenchmark.ContainerBenchmark.MatchesBenchmarkShard(
                testCase, "quadratic-stress");
            bool queueD4Supplemental = global::ContainerBenchmark.ContainerBenchmark.MatchesBenchmarkShard(
                testCase, "queue-d4");

            if (global::ContainerBenchmark.ContainerBenchmark.MatchesBenchmarkShard(testCase, "full"))
                fullShardCount++;
            if (global::ContainerBenchmark.ContainerBenchmark.MatchesBenchmarkShard(testCase, string.Empty))
                emptyShardCount++;
            if (global::ContainerBenchmark.ContainerBenchmark.MatchesBenchmarkShard(testCase, "unknown-shard"))
                invalidShardCount++;
            if (testCase.Scale == TestScale
                && testCase.Collision == CollisionProfile.Normal
                && smokeOperations.Contains(testCase.OperationCode))
            {
                smokeCount++;
            }
            if (linearSmall)
                linearSmallCount++;
            if (linearLarge)
                linearLargeCount++;
            if (quadraticStress)
                quadraticStressCount++;
            if (queueD4Supplemental)
                queueD4SupplementalCount++;

            int membershipCount = (linearSmall ? 1 : 0)
                                  + (linearLarge ? 1 : 0)
                                  + (quadraticStress ? 1 : 0);
            if (membershipCount == 1)
            {
                if (testCase.IsSupported)
                    selectedSupportedCount++;
                else
                    selectedUnsupportedCount++;
            }
            if (membershipCount > 1)
                overlapCount++;

            bool expectedExcluded = testCase.ValueType == BenchmarkValueType.IntVector3
                                    && testCase.Scale >= 100_000
                                    && IsExpectedNonLinearCase(testCase);
            if (expectedExcluded)
                expectedExcludedCount++;
            if (membershipCount == 0)
            {
                excludedCount++;
                if (!expectedExcluded)
                    excludedSemanticMismatchCount++;
            }
            else if (expectedExcluded)
            {
                excludedSemanticMismatchCount++;
            }
        }

        bool shardCountsOk = shardUniverse.Count == 1166
                             && fullShardCount == 1166
                             && emptyShardCount == 1166
                             && invalidShardCount == 0
                             && smokeCount == 19
                             && linearSmallCount == 595
                             && linearLargeCount == 231
                             && quadraticStressCount == 102
                             && queueD4SupplementalCount == 20
                             && selectedSupportedCount == 928
                             && selectedUnsupportedCount == 0
                             && overlapCount == 0
                             && excludedCount == 238
                             && expectedExcludedCount == 238
                             && excludedSemanticMismatchCount == 0;
        string shardSummary = $"目录={shardUniverse.Count}, full/空={fullShardCount}/{emptyShardCount}, "
                              + $"smoke={smokeCount}, linear-small={linearSmallCount}, "
                              + $"linear-large={linearLargeCount}, quadratic-stress={quadraticStressCount}, "
                              + $"queue-d4={queueD4SupplementalCount}, "
                              + $"selectedSupported/unsupported={selectedSupportedCount}/{selectedUnsupportedCount}, "
                              + $"overlap={overlapCount}, excluded={excludedCount}, "
                              + $"expectedExcluded={expectedExcludedCount}, semanticMismatch={excludedSemanticMismatchCount}, "
                              + $"invalid={invalidShardCount}";
        if (shardCountsOk)
        {
            Debug.Log("[阶段2] 分片谓词自检通过：" + shardSummary);
        }
        else
        {
            failures.AppendLine("阶段2分片谓词自检失败：" + shardSummary);
            Debug.LogError("[阶段2] 分片谓词自检失败：" + shardSummary);
        }

        // ---------- 汇总 ----------
        bool stage1MatrixOk = total == 119 && passed == 119 && failed == 0 && skipped == 0;
        if (!stage1MatrixOk)
        {
            string matrixFailure = $"阶段1 10K 矩阵计数不符：total/passed/failed/skipped={total}/{passed}/{failed}/{skipped}，期望 119/119/0/0";
            failures.AppendLine(matrixFailure);
            Debug.LogError("[阶段1] " + matrixFailure);
        }

        bool checksOk = reproducible && keyGenFast && missKeysDisjoint && duplicateIdsOk && jsonOk
                        && multisetOk && longMedianOk && shardCountsOk && stage1MatrixOk;
        string summary = $"阶段1自测：共 {total} 用例，通过 {passed}，失败 {failed}，跳过 {skipped}"
                         + $" | HashKeyFactory 可复现={reproducible}、生成<1s={keyGenFast}"
                         + $"、未命中无交集={missKeysDisjoint} | 重复Id检查={duplicateIdsOk}"
                         + $" | JsonUtility 往返={jsonOk} | 多重集自检={multisetOk}"
                         + $" | GC long中位数={longMedianOk}"
                         + $" | 10K矩阵={stage1MatrixOk}"
                         + $" | 阶段2分片谓词={shardCountsOk}";
        bool success = failed == 0 && checksOk;
        if (!success)
        {
            Debug.LogError($"[阶段1] {summary}\n{failures}");
        }
        else
        {
            Debug.Log($"[阶段1] {summary}");
        }

        if (showDialog)
            EditorUtility.DisplayDialog("阶段1自测", (success ? "通过\n" : "失败\n") + summary, "确定");

        if (!success && !showDialog)
            throw new InvalidOperationException(summary + "\n" + failures);
    }

    private static bool KeysEqual(HashKey[] a, HashKey[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].id != b[i].id || a[i].hashOverride != b[i].hashOverride)
                return false;
        }

        return true;
    }

    private static List<IBenchmarkCase> BuildFullUniverse()
    {
        var universe = new List<IBenchmarkCase>(2048);
        var seen = new HashSet<string>();
        foreach (BenchmarkValueType valueType in Enum.GetValues(typeof(BenchmarkValueType)))
        {
            foreach (int scale in global::ContainerBenchmark.ContainerBenchmark.ScalesFor(valueType))
            {
                CollisionProfile[] profiles = valueType == BenchmarkValueType.StringKey
                    ? new[] { CollisionProfile.Normal }
                    : new[]
                    {
                        CollisionProfile.Normal,
                        CollisionProfile.LimitedDomain,
                        CollisionProfile.AllCollision,
                    };
                foreach (CollisionProfile profile in profiles)
                {
                    foreach (IBenchmarkCase testCase in
                             BenchmarkCaseCatalog.CreateAll(scale, valueType, profile, useJob: true))
                    {
                        if (seen.Add(testCase.Id))
                            universe.Add(testCase);
                    }
                }
            }
        }
        return universe;
    }

    private static bool IsExpectedNonLinearCase(IBenchmarkCase testCase)
    {
        bool intrinsic = testCase.OperationCode == "A2"
                         || testCase.OperationCode == "A4"
                         || testCase.OperationCode == "A5"
                         || testCase.OperationCode == "E3";
        bool allCollisionHash = testCase.ValueType == BenchmarkValueType.IntVector3
                                && testCase.Collision == CollisionProfile.AllCollision
                                && (testCase.Family == ContainerFamily.Dictionary
                                    || testCase.Family == ContainerFamily.HashSet);
        return intrinsic || allCollisionHash;
    }
}
