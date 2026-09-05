using System.Collections.Generic;
using Unity.Collections;

namespace ContainerBenchmark
{
    /// <summary>
    /// D 组用例工厂：Queue ↔ NativeQueue（int 元素，D1~D4）。
    /// Collections 6.5 的 NativeQueue.ReadOnly 支持零拷贝枚举，D4 可对称测量。
    /// </summary>
    public static class QueueBenchmarkCases
    {
        public static List<IBenchmarkCase> CreateAll(int scale)
        {
            return new List<IBenchmarkCase>(8)
            {
                new QueueEnqueueCase(scale),
                new NativeQueueEnqueueCase(scale),
                new QueueDequeueCase(scale),
                new NativeQueueDequeueCase(scale),
                new QueueMixedCase(scale),
                new NativeQueueMixedCase(scale),
                new QueueTraverseCase(scale),
                new NativeQueueTraverseCase(scale),
            };
        }
    }

    // ==================== D1 入队 ====================

    public sealed class QueueEnqueueCase : BenchmarkCaseBase
    {
        private Queue<int> _queue;

        public QueueEnqueueCase(int scale)
            : base("Queue", false, ContainerFamily.Queue, "D1", "入队(Enqueue)", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _queue = new Queue<int>(Scale); // 预分配容量
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _queue.Enqueue(i);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _queue.Count == Scale;
            desc = ok ? $"Count={_queue.Count}，等于期望 {Scale}" : $"Count={_queue.Count}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            _queue = null;
        }
    }

    public sealed class NativeQueueEnqueueCase : BenchmarkCaseBase
    {
        public override string SemanticNote =>
            "NativeQueue 无公开 Capacity/预分配接口；计时区内 Enqueue 包含其按块扩容或复用成本，而 Queue<int> 已预分配到 N，容量语义不完全对称。";

        private NativeQueue<int> _queue;

        public NativeQueueEnqueueCase(int scale)
            : base("NativeQueue", true, ContainerFamily.Queue, "D1", "入队(Enqueue)", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            // NativeQueue 无公开容量构造/Capacity API；内部块分配属于 Enqueue 的实际成本。
            _queue = new NativeQueue<int>(Allocator.TempJob);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _queue.Enqueue(i);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _queue.Count == Scale;
            desc = ok ? $"Count={_queue.Count}，等于期望 {Scale}" : $"Count={_queue.Count}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            if (_queue.IsCreated)
                _queue.Dispose();
        }
    }

    // ==================== D2 出队 ====================

    public sealed class QueueDequeueCase : BenchmarkCaseBase
    {
        private Queue<int> _queue;

        public QueueDequeueCase(int scale)
            : base("Queue", false, ContainerFamily.Queue, "D2", "出队(Dequeue)", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _queue = new Queue<int>(Scale);
            for (int i = 0; i < Scale; i++)
                _queue.Enqueue(i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            for (int i = 0; i < Scale; i++)
                sum += _queue.Dequeue();
            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = _queue.Count == 0 && Checksum.IntSum == expected;
            desc = ok
                ? $"Count=0，出队累计和={Checksum.IntSum}，等于期望和 {expected}"
                : $"Count={_queue.Count}，出队累计和={Checksum.IntSum}，期望 Count=0 且和={expected}";
            return ok;
        }

        public override void Teardown()
        {
            _queue = null;
        }
    }

    public sealed class NativeQueueDequeueCase : BenchmarkCaseBase
    {
        public override string SemanticNote =>
            "NativeQueue 无公开 Capacity/预分配接口；N 个元素在计时外 Setup 完成，计时区仅含 Dequeue；其原生分块存储与 Queue<int> 的托管环形数组布局不同。";

        private NativeQueue<int> _queue;

        public NativeQueueDequeueCase(int scale)
            : base("NativeQueue", true, ContainerFamily.Queue, "D2", "出队(Dequeue)", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _queue = new NativeQueue<int>(Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _queue.Enqueue(i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            for (int i = 0; i < Scale; i++)
                sum += _queue.Dequeue();
            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = _queue.Count == 0 && Checksum.IntSum == expected;
            desc = ok
                ? $"Count=0，出队累计和={Checksum.IntSum}，等于期望和 {expected}"
                : $"Count={_queue.Count}，出队累计和={Checksum.IntSum}，期望 Count=0 且和={expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_queue.IsCreated)
                _queue.Dispose();
        }
    }

    // ==================== D3 混合入出队（模拟消息队列常态） ====================

    public sealed class QueueMixedCase : BenchmarkCaseBase
    {
        public override long TimedOperationCount => (long)Scale * 2L;

        private Queue<int> _queue;

        public QueueMixedCase(int scale)
            : base("Queue", false, ContainerFamily.Queue, "D3", "混合入出队", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _queue = new Queue<int>(Scale);
            for (int i = 0; i < Scale / 2; i++)
                _queue.Enqueue(i); // 初始半满
        }

        public override void RunOnePass()
        {
            long sum = 0;
            for (int i = 0; i < Scale; i++)
            {
                _queue.Enqueue(i);
                sum += _queue.Dequeue();
            }

            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            // 出队序列 = 初始 0..N/2-1 一轮 + 重新入队的 0..N/2-1 一轮 → 每个值恰好出队两次
            long expected = (long)(Scale / 2 - 1) * (Scale / 2);
            bool ok = _queue.Count == Scale / 2 && Checksum.IntSum == expected;
            desc = ok
                ? $"Count={_queue.Count}（保持半满），出队累计和={Checksum.IntSum}，等于期望 {expected}"
                : $"Count={_queue.Count}，出队累计和={Checksum.IntSum}，期望 Count={Scale / 2} 且和={expected}";
            return ok;
        }

        public override void Teardown()
        {
            _queue = null;
        }
    }

    public sealed class NativeQueueMixedCase : BenchmarkCaseBase
    {
        public override long TimedOperationCount => (long)Scale * 2L;
        public override string SemanticNote =>
            "NativeQueue 无公开 Capacity/预分配接口；Setup 在计时外置为半满，计时区 Enqueue/Dequeue 可能包含内部块分配与复用成本，而 Queue<int> 已预分配到 N。";

        private NativeQueue<int> _queue;

        public NativeQueueMixedCase(int scale)
            : base("NativeQueue", true, ContainerFamily.Queue, "D3", "混合入出队", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _queue = new NativeQueue<int>(Allocator.TempJob);
            for (int i = 0; i < Scale / 2; i++)
                _queue.Enqueue(i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            for (int i = 0; i < Scale; i++)
            {
                _queue.Enqueue(i);
                sum += _queue.Dequeue();
            }

            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = (long)(Scale / 2 - 1) * (Scale / 2);
            bool ok = _queue.Count == Scale / 2 && Checksum.IntSum == expected;
            desc = ok
                ? $"Count={_queue.Count}（保持半满），出队累计和={Checksum.IntSum}，等于期望 {expected}"
                : $"Count={_queue.Count}，出队累计和={Checksum.IntSum}，期望 Count={Scale / 2} 且和={expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_queue.IsCreated)
                _queue.Dispose();
        }
    }

    // ==================== D4 遍历 ====================

    public sealed class QueueTraverseCase : ReusableReadOnlyBenchmarkCaseBase
    {
        private Queue<int> _queue;

        public QueueTraverseCase(int scale)
            : base("Queue", false, ContainerFamily.Queue, "D4", "遍历(foreach)", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _queue = new Queue<int>(Scale);
            for (int i = 0; i < Scale; i++)
                _queue.Enqueue(i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            foreach (int value in _queue)
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
            _queue = null;
        }
    }

    /// <summary>Collections 6.5：通过 ReadOnly 别名零拷贝枚举 NativeQueue。</summary>
    public sealed class NativeQueueTraverseCase : ReusableReadOnlyBenchmarkCaseBase
    {
        public override string SemanticNote =>
            "Collections 6.5：通过 NativeQueue<int>.AsReadOnly().GetEnumerator() 直接枚举已填充队列，不调用 ToArray、不复制元素；这是主线程只读枚举而非 Job/Burst 形态，底层存储布局仍不同于 Queue<int>.foreach。";

        private NativeQueue<int> _queue;

        public NativeQueueTraverseCase(int scale)
            : base("NativeQueue", true, ContainerFamily.Queue, "D4", "遍历(ReadOnly枚举)", false,
                   scale, BenchmarkValueType.IntVector3, CollisionProfile.Normal, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _queue = new NativeQueue<int>(Allocator.Persistent);
            for (int i = 0; i < Scale; i++)
                _queue.Enqueue(i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            using (var enumerator = _queue.AsReadOnly().GetEnumerator())
            {
                while (enumerator.MoveNext())
                    sum += enumerator.Current;
            }
            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = _queue.Count == Scale && Checksum.IntSum == expected;
            desc = ok
                ? $"Count={_queue.Count}，零拷贝遍历累计和={Checksum.IntSum}，等于期望和 {expected}"
                : $"Count={_queue.Count}，零拷贝遍历累计和={Checksum.IntSum}，期望 Count={Scale} 且和={expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_queue.IsCreated)
                _queue.Dispose();
        }
    }
}
