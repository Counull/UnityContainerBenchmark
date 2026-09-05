using System.Collections.Generic;using Unity.Collections;

namespace ContainerBenchmark
{
    /// <summary>
    /// F 组用例工厂：Stack 近似对比（int 元素，无 NativeStack，F1~F2）。
    /// </summary>
    public static class StackApproxBenchmarkCases
    {
        public static List<IBenchmarkCase> CreateAll(int scale)
        {
            return new List<IBenchmarkCase>(2)
            {
                new ListAsStackCase(scale),
                new NativeListAsStackCase(scale),
            };
        }
    }

    /// <summary>F1：List 当栈，尾部 Push/Pop。</summary>
    public sealed class ListAsStackCase : BenchmarkCaseBase
    {
        public override long TimedOperationCount => (long)Scale * 2L;

        private List<int> _list;

        public ListAsStackCase(int scale)
            : base("List(当栈)", false, ContainerFamily.StackApprox, "F1", "栈Push/Pop", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _list = new List<int>(Scale); // 预分配容量
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _list.Add(i); // Push

            long sum = 0;
            for (int i = 0; i < Scale; i++)
            {
                sum += _list[_list.Count - 1]; // Pop（读尾）
                _list.RemoveAt(_list.Count - 1);
            }

            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = _list.Count == 0 && Checksum.IntSum == expected;
            desc = ok
                ? $"Count=0，出栈累计和={Checksum.IntSum}，等于期望和 {expected}"
                : $"Count={_list.Count}，出栈累计和={Checksum.IntSum}，期望 Count=0 且和={expected}";
            return ok;
        }

        public override void Teardown()
        {
            _list = null;
        }
    }

    /// <summary>F2：NativeList 当栈，尾部 Push/Pop。</summary>
    public sealed class NativeListAsStackCase : BenchmarkCaseBase
    {
        public override long TimedOperationCount => (long)Scale * 2L;

        private NativeList<int> _list;

        public NativeListAsStackCase(int scale)
            : base("NativeList(当栈)", true, ContainerFamily.StackApprox, "F2", "栈Push/Pop", false,
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
                _list.Add(i); // Push

            long sum = 0;
            for (int i = 0; i < Scale; i++)
            {
                sum += _list[_list.Length - 1]; // Pop（读尾）
                _list.RemoveAt(_list.Length - 1);
            }

            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = _list.Length == 0 && Checksum.IntSum == expected;
            desc = ok
                ? $"Length=0，出栈累计和={Checksum.IntSum}，等于期望和 {expected}"
                : $"Length={_list.Length}，出栈累计和={Checksum.IntSum}，期望 Length=0 且和={expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_list.IsCreated)
                _list.Dispose();
        }
    }
}
