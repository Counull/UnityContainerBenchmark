using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;

namespace ContainerBenchmark
{
    /// <summary>
    /// A 组用例工厂：List ↔ NativeList（int 元素，A1~A8）。
    /// </summary>
    public static class ListBenchmarkCases
    {
        public static List<IBenchmarkCase> CreateAll(int scale, bool useJob)
        {
            var cases = new List<IBenchmarkCase>(16)
            {
                new ListAppendTailCase(scale),
                new NativeListAppendTailCase(scale),
                new ListInsertHeadCase(scale),
                new NativeListInsertHeadCase(scale),
                new ListRemoveTailCase(scale),
                new NativeListRemoveTailCase(scale),
                new ListRemoveHeadCase(scale),
                new NativeListRemoveHeadCase(scale),
                new ListScanDeleteCase(scale),
                new NativeListScanDeleteCase(scale),
                new ListSwapBackDeleteCase(scale),
                new NativeListSwapBackDeleteCase(scale),
                new ListForSumCase(scale),
                new NativeListForSumCase(scale),
            };

            if (useJob)
            {
                cases.Add(new ListJobSumCase(scale));
                cases.Add(new NativeListJobSumCase(scale));
            }

            return cases;
        }
    }

    /// <summary>A 组公共辅助。</summary>
    internal static class ListHelpers
    {
        /// <summary>0..n-1 的等差数列和（long，精确）。</summary>
        internal static long ExpectedSum(int n)
        {
            long m = n;
            return m * (m - 1) / 2;
        }
    }

    // ==================== A1 尾部追加 ====================

    public sealed class ListAppendTailCase : BenchmarkCaseBase
    {
        private List<int> _list;

        public ListAppendTailCase(int scale)
            : base("List", false, ContainerFamily.List, "A1", "尾部追加", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new List<int>(Scale); // 预分配容量，不测扩容
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _list.Count == Scale;
            desc = ok ? $"Count={_list.Count}，等于期望 {Scale}" : $"Count={_list.Count}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            _list = null;
        }
    }

    public sealed class NativeListAppendTailCase : BenchmarkCaseBase
    {
        private NativeList<int> _list;

        public NativeListAppendTailCase(int scale)
            : base("NativeList", true, ContainerFamily.List, "A1", "尾部追加", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new NativeList<int>(Scale, Allocator.TempJob); // 预分配容量
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _list.Length == Scale;
            desc = ok ? $"Length={_list.Length}，等于期望 {Scale}" : $"Length={_list.Length}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            if (_list.IsCreated)
                _list.Dispose();
        }
    }

    // ==================== A2 头部插入（索引 0，保序最坏情况） ====================

    public sealed class ListInsertHeadCase : BenchmarkCaseBase
    {
        private List<int> _list;

        public ListInsertHeadCase(int scale)
            : base("List", false, ContainerFamily.List, "A2", "头部插入(索引0)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new List<int>(Scale);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _list.Insert(0, i);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _list.Count == Scale && IsDescending(_list);
            desc = ok ? $"Count={_list.Count}，内容倒序（保序正确）" : $"Count={_list.Count}，期望 {Scale} 且倒序";
            return ok;
        }

        public override void Teardown()
        {
            _list = null;
        }

        private static bool IsDescending(List<int> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != list.Count - 1 - i)
                    return false;
            }

            return true;
        }
    }

    public sealed class NativeListInsertHeadCase : BenchmarkCaseBase
    {
        private NativeList<int> _list;

        public NativeListInsertHeadCase(int scale)
            : base("NativeList", true, ContainerFamily.List, "A2", "头部插入(索引0)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new NativeList<int>(Scale, Allocator.TempJob);
        }

        public override void RunOnePass()
        {
            // Collections 6.5 的 InsertRange 走底层批量搬移；随后写入新槽，
            // 与 List.Insert(0,value) 的保序头插/批量搬移语义对齐。
            for (int i = 0; i < Scale; i++)
            {
                _list.InsertRange(0, 1);
                _list[0] = i;
            }

            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _list.Length == Scale;
            if (ok)
            {
                for (int i = 0; i < Scale; i++)
                {
                    if (_list[i] != Scale - 1 - i)
                    {
                        ok = false;
                        break;
                    }
                }
            }

            desc = ok ? $"Length={_list.Length}，内容倒序（保序正确）" : $"Length={_list.Length}，期望 {Scale} 且倒序";
            return ok;
        }

        public override void Teardown()
        {
            if (_list.IsCreated)
                _list.Dispose();
        }
    }

    // ==================== A3 尾部删除 ====================

    public sealed class ListRemoveTailCase : BenchmarkCaseBase
    {
        private List<int> _list;

        public ListRemoveTailCase(int scale)
            : base("List", false, ContainerFamily.List, "A3", "尾部删除", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new List<int>(Scale);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _list.RemoveAt(_list.Count - 1);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _list.Count == 0;
            desc = ok ? "Count=0，删除干净" : $"Count={_list.Count}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            _list = null;
        }
    }

    public sealed class NativeListRemoveTailCase : BenchmarkCaseBase
    {
        private NativeList<int> _list;

        public NativeListRemoveTailCase(int scale)
            : base("NativeList", true, ContainerFamily.List, "A3", "尾部删除", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new NativeList<int>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _list.RemoveAt(_list.Length - 1);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _list.Length == 0;
            desc = ok ? "Length=0，删除干净" : $"Length={_list.Length}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            if (_list.IsCreated)
                _list.Dispose();
        }
    }

    // ==================== A4 头部删除（索引 0，保序最坏情况） ====================

    public sealed class ListRemoveHeadCase : BenchmarkCaseBase
    {
        private List<int> _list;

        public ListRemoveHeadCase(int scale)
            : base("List", false, ContainerFamily.List, "A4", "头部删除(索引0)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new List<int>(Scale);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _list.RemoveAt(0);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _list.Count == 0;
            desc = ok ? "Count=0，删除干净" : $"Count={_list.Count}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            _list = null;
        }
    }

    public sealed class NativeListRemoveHeadCase : BenchmarkCaseBase
    {
        private NativeList<int> _list;

        public NativeListRemoveHeadCase(int scale)
            : base("NativeList", true, ContainerFamily.List, "A4", "头部删除(索引0)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new NativeList<int>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _list.RemoveAt(0);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _list.Length == 0;
            desc = ok ? "Length=0，删除干净" : $"Length={_list.Length}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            if (_list.IsCreated)
                _list.Dispose();
        }
    }

    // ==================== A5 倒序扫描命中删除（保序） ====================

    public sealed class ListScanDeleteCase : BenchmarkCaseBase
    {
        private List<int> _list;

        public ListScanDeleteCase(int scale)
            : base("List", false, ContainerFamily.List, "A5", "倒序扫描命中删除(保序)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new List<int>(Scale);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
        }

        public override void RunOnePass()
        {
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                if ((i & 1) == 0) // 命中偶数索引（确定性 50% 命中率）
                {
                    Checksum.AddInt(_list[i]);
                    _list.RemoveAt(i);
                }
            }
        }

        public override bool Validate(out string desc)
        {
            // 倒序删除不影响更小索引元素的位置 → 幸存者为原奇数索引元素，且保序
            int expected = Scale / 2;
            bool ok = _list.Count == expected;
            if (ok)
            {
                for (int i = 0; i < expected; i++)
                {
                    if (_list[i] != 1 + 2 * i)
                    {
                        ok = false;
                        break;
                    }
                }
            }

            desc = ok
                ? $"Count={_list.Count}（删 50%），幸存元素保序"
                : $"Count={_list.Count}，期望 {expected}，保序校验失败";
            return ok;
        }

        public override void Teardown()
        {
            _list = null;
        }
    }

    public sealed class NativeListScanDeleteCase : BenchmarkCaseBase
    {
        private NativeList<int> _list;

        public NativeListScanDeleteCase(int scale)
            : base("NativeList", true, ContainerFamily.List, "A5", "倒序扫描命中删除(保序)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new NativeList<int>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
        }

        public override void RunOnePass()
        {
            for (int i = _list.Length - 1; i >= 0; i--)
            {
                if ((i & 1) == 0)
                {
                    Checksum.AddInt(_list[i]);
                    _list.RemoveAt(i);
                }
            }
        }

        public override bool Validate(out string desc)
        {
            int expected = Scale / 2;
            bool ok = _list.Length == expected;
            if (ok)
            {
                for (int i = 0; i < expected; i++)
                {
                    if (_list[i] != 1 + 2 * i)
                    {
                        ok = false;
                        break;
                    }
                }
            }

            desc = ok
                ? $"Length={_list.Length}（删 50%），幸存元素保序"
                : $"Length={_list.Length}，期望 {expected}，保序校验失败";
            return ok;
        }

        public override void Teardown()
        {
            if (_list.IsCreated)
                _list.Dispose();
        }
    }

    // ==================== A6 SwapBack 删除（不保序） ====================

    public sealed class ListSwapBackDeleteCase : BenchmarkCaseBase
    {
        public override long TimedOperationCount => Scale / 2L;

        private List<int> _list;
        private int[] _removed;

        public ListSwapBackDeleteCase(int scale)
            : base("List", false, ContainerFamily.List, "A6", "SwapBack删除(手动)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new List<int>(Scale);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
            _removed = new int[Scale / 2];
        }

        public override void RunOnePass()
        {
            int removeCount = Scale / 2;
            for (int k = 0; k < removeCount; k++)
            {
                int value = _list[0];
                Checksum.AddInt(value);
                _removed[k] = value;
                int last = _list.Count - 1;
                _list[0] = _list[last]; // 手动 SwapBack：末元素搬到 0 号位
                _list.RemoveAt(last);
            }
        }

        public override bool Validate(out string desc)
        {
            int removeCount = Scale / 2;
            bool ok = _list.Count == Scale - removeCount
                      && MultiSet.CombinedEqualsRange(_list, _removed, 0, Scale);
            desc = ok
                ? $"Count={_list.Count}，剩余∪已删多重集 == 全集（与顺序无关）"
                : $"Count={_list.Count}，期望 {Scale - removeCount}，多重集校验失败";
            return ok;
        }

        public override void Teardown()
        {
            _list = null;
            _removed = null;
        }
    }

    public sealed class NativeListSwapBackDeleteCase : BenchmarkCaseBase
    {
        public override long TimedOperationCount => Scale / 2L;

        private NativeList<int> _list;
        private int[] _removed;

        public NativeListSwapBackDeleteCase(int scale)
            : base("NativeList", true, ContainerFamily.List, "A6", "SwapBack删除(RemoveAtSwapBack)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new NativeList<int>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
            _removed = new int[Scale / 2];
        }

        public override void RunOnePass()
        {
            int removeCount = Scale / 2;
            for (int k = 0; k < removeCount; k++)
            {
                int value = _list[0];
                Checksum.AddInt(value);
                _removed[k] = value;
                _list.RemoveAtSwapBack(0);
            }
        }

        public override bool Validate(out string desc)
        {
            int removeCount = Scale / 2;
            bool ok = _list.Length == Scale - removeCount
                      && MultiSet.CombinedEqualsRange(_list, _removed, 0, Scale);
            desc = ok
                ? $"Length={_list.Length}，剩余∪已删多重集 == 全集（与顺序无关）"
                : $"Length={_list.Length}，期望 {Scale - removeCount}，多重集校验失败";
            return ok;
        }

        public override void Teardown()
        {
            if (_list.IsCreated)
                _list.Dispose();
            _removed = null;
        }
    }

    // ==================== A7 for 索引遍历累加 ====================

    public sealed class ListForSumCase : ReusableReadOnlyBenchmarkCaseBase
    {
        private List<int> _list;

        public ListForSumCase(int scale)
            : base("List", false, ContainerFamily.List, "A7", "遍历读(for累加)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new List<int>(Scale);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            for (int i = 0; i < _list.Count; i++)
                sum += _list[i];
            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = Checksum.IntSum == expected;
            desc = ok ? $"累加和={Checksum.IntSum}，等于期望和 {expected}" : $"累加和={Checksum.IntSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            _list = null;
        }
    }

    public sealed class NativeListForSumCase : ReusableReadOnlyBenchmarkCaseBase
    {
        private NativeList<int> _list;

        public NativeListForSumCase(int scale)
            : base("NativeList", true, ContainerFamily.List, "A7", "遍历读(for累加)", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new NativeList<int>(Scale, Allocator.Persistent);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            for (int i = 0; i < _list.Length; i++)
                sum += _list[i];
            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = Checksum.IntSum == expected;
            desc = ok ? $"累加和={Checksum.IntSum}，等于期望和 {expected}" : $"累加和={Checksum.IntSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_list.IsCreated)
                _list.Dispose();
        }
    }

    // ==================== A8 Job 遍历求和 ====================

    public sealed class ListJobSumCase : ReusableReadOnlyBenchmarkCaseBase
    {
        private List<int> _list;
        private NativeArray<int> _input;
        private NativeArray<long> _result;

        public ListJobSumCase(int scale)
            : base("List", false, ContainerFamily.List, "A8", "Job遍历求和", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, true)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new List<int>(Scale);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
            _input = new NativeArray<int>(Scale, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _result = new NativeArray<long>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        public override void ResetForPass()
        {
            base.ResetForPass();
            _result[0] = 0L;
        }

        public override void RunOnePass()
        {
            // 公平性规则：托管容器逐元素复制到预分配 NativeArray，复制耗时计入；计时区不分配。
            for (int i = 0; i < _list.Count; i++)
                _input[i] = _list[i];

            new SumIntJob { Input = _input, Result = _result }
                .Schedule().Complete();
            Checksum.AddLong(_result[0]);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = Checksum.IntSum == expected;
            desc = ok ? $"Job 求和={Checksum.IntSum}，等于期望和 {expected}" : $"Job 求和={Checksum.IntSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_result.IsCreated)
                _result.Dispose();
            if (_input.IsCreated)
                _input.Dispose();
            _list = null;
        }
    }

    public sealed class NativeListJobSumCase : ReusableReadOnlyBenchmarkCaseBase
    {
        private NativeList<int> _list;
        private NativeArray<long> _result;

        public NativeListJobSumCase(int scale)
            : base("NativeList", true, ContainerFamily.List, "A8", "Job遍历求和", true,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, true)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new NativeList<int>(Scale, Allocator.Persistent);
            for (int i = 0; i < Scale; i++)
                _list.Add(i);
            _result = new NativeArray<long>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        public override void ResetForPass()
        {
            base.ResetForPass();
            _result[0] = 0L;
        }

        public override void RunOnePass()
        {
            // Native 容器零拷贝：长度在调度前已固定，直接 AsArray，避免不必要的 deferred patch。
            new SumIntJob { Input = _list.AsArray(), Result = _result }
                .Schedule().Complete();
            Checksum.AddLong(_result[0]);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = Checksum.IntSum == expected;
            desc = ok ? $"Job 求和={Checksum.IntSum}，等于期望和 {expected}" : $"Job 求和={Checksum.IntSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_result.IsCreated)
                _result.Dispose();
            if (_list.IsCreated)
                _list.Dispose();
        }
    }
}
