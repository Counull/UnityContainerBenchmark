using System.Collections.Generic;
namespace ContainerBenchmark
{
    /// <summary>
    /// E 组用例工厂：LinkedList 专项（int 元素，无 Native 对应物，E1~E4）。
    /// Job 维度：LinkedList 无连续存储/拷贝即失真，标注"无 Job 形态"（SupportsJob = false）。
    /// </summary>
    public static class LinkedListBenchmarkCases
    {
        public static List<IBenchmarkCase> CreateAll(int scale)
        {
            return new List<IBenchmarkCase>(4)
            {
                new LinkedListAppendTailCase(scale),
                new LinkedListHoldNodeRemoveCase(scale),
                new LinkedListSearchRemoveCase(scale),
                new LinkedListTraverseCase(scale),
            };
        }
    }

    // ==================== E1 尾部追加 ====================

    public sealed class LinkedListAppendTailCase : BenchmarkCaseBase
    {
        private LinkedList<int> _list;

        public LinkedListAppendTailCase(int scale)
            : base("LinkedList", false, ContainerFamily.LinkedList, "E1", "尾部追加(AddLast)", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new LinkedList<int>(); // 无容量概念
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _list.AddLast(i);
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

    // ==================== E2 持有 Node 逐个删除（O(1)） ====================

    public sealed class LinkedListHoldNodeRemoveCase : BenchmarkCaseBase
    {
        private LinkedList<int> _list;

        public LinkedListHoldNodeRemoveCase(int scale)
            : base("LinkedList", false, ContainerFamily.LinkedList, "E2", "持有Node删除(O(1))", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new LinkedList<int>();
            for (int i = 0; i < Scale; i++)
                _list.AddLast(i);
        }

        public override void RunOnePass()
        {
            // 从首节点开始沿 Next 链逐个删除：每次 O(1)，不触发搜索
            var node = _list.First;
            while (node != null)
            {
                var next = node.Next;
                _list.Remove(node);
                node = next;
            }

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

    // ==================== E3 仅值搜索删除（含搜索成本） ====================

    public sealed class LinkedListSearchRemoveCase : BenchmarkCaseBase
    {
        private LinkedList<int> _list;

        public LinkedListSearchRemoveCase(int scale)
            : base("LinkedList", false, ContainerFamily.LinkedList, "E3", "仅值搜索删除", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new LinkedList<int>();
            for (int i = 0; i < Scale; i++)
                _list.AddLast(i);
        }

        public override void RunOnePass()
        {
            // 逆序找值：目标始终靠近链尾，每次从表头 Find 都承担搜索成本（总体 O(N²)）。
            for (int value = Scale - 1; value >= 0; value--)
            {
                var node = _list.Find(value);
                if (node != null)
                    _list.Remove(node);
            }

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

    // ==================== E4 foreach 遍历 ====================

    public sealed class LinkedListTraverseCase : ReusableReadOnlyBenchmarkCaseBase
    {
        private LinkedList<int> _list;

        public LinkedListTraverseCase(int scale)
            : base("LinkedList", false, ContainerFamily.LinkedList, "E4", "遍历(foreach)", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new LinkedList<int>();
            for (int i = 0; i < Scale; i++)
                _list.AddLast(i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            foreach (int value in _list)
                sum += value;
            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = Checksum.IntSum == expected;
            desc = ok ? $"遍历累计和={Checksum.IntSum}，等于期望和 {expected}" : $"遍历累计和={Checksum.IntSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            _list = null;
        }
    }
}
