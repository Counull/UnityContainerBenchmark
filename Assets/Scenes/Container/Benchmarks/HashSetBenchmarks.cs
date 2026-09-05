using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;

namespace ContainerBenchmark
{
    /// <summary>
    /// C 组用例工厂：HashSet ↔ NativeParallelHashSet（HashKey 元素，C1~C7）。
    /// </summary>
    public static class HashSetBenchmarkCases
    {
        public static List<IBenchmarkCase> CreateAll(int scale, CollisionProfile collision, bool useJob)
        {
            var cases = new List<IBenchmarkCase>(13)
            {
                new HashSetInsertCase(scale, collision),
                new NativeHashSetInsertCase(scale, collision),
                new HashSetHitQueryCase(scale, collision),
                new NativeHashSetHitQueryCase(scale, collision),
                new HashSetMissQueryCase(scale, collision),
                new NativeHashSetMissQueryCase(scale, collision),
                new HashSetRemoveCase(scale, collision),
                new NativeHashSetRemoveCase(scale, collision),
                new HashSetTraverseCase(scale, collision),
                new NativeHashSetTraverseCase(scale, collision),
            };

            if (useJob)
            {
                cases.Add(new HashSetJobTraverseCase(scale, collision));
                cases.Add(new NativeHashSetJobTraverseCase(scale, collision));
                cases.Add(new NativeHashSetParallelWriteCase(scale, collision)); // C7 仅 Native
            }

            return cases;
        }
    }

    // ==================== C1 插入 ====================

    public sealed class HashSetInsertCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private HashSet<HashKey> _set;

        public HashSetInsertCase(int scale, CollisionProfile collision)
            : base("HashSet", false, ContainerFamily.HashSet, "C1", "插入(Add)", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new HashSet<HashKey>(Scale); // 预分配容量，不测扩容
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _set.Add(_keys[i]);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _set.Count == Scale;
            desc = ok ? $"Count={_set.Count}，等于期望 {Scale}" : $"Count={_set.Count}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            _set = null;
            _keys = null;
        }
    }

    public sealed class NativeHashSetInsertCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private NativeParallelHashSet<HashKey> _set;

        public NativeHashSetInsertCase(int scale, CollisionProfile collision)
            : base("NativeParallelHashSet", true, ContainerFamily.HashSet, "C1", "插入(Add)", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new NativeParallelHashSet<HashKey>(Scale, Allocator.TempJob); // 预分配容量
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _set.Add(_keys[i]);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _set.Count() == Scale;
            desc = ok ? $"Count={_set.Count()}，等于期望 {Scale}" : $"Count={_set.Count()}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            if (_set.IsCreated)
                _set.Dispose();
            _keys = null;
        }
    }

    // ==================== C2 命中查询 ====================

    public sealed class HashSetHitQueryCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private HashSet<HashKey> _set;

        public HashSetHitQueryCase(int scale, CollisionProfile collision)
            : base("HashSet", false, ContainerFamily.HashSet, "C2", "命中查询", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new HashSet<HashKey>(Scale);
            for (int i = 0; i < Scale; i++)
                _set.Add(_keys[i]);
        }

        public override void RunOnePass()
        {
            int hits = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_set.Contains(_keys[i]))
                    hits++;
            }

            Checksum.AddInt(hits);
        }

        public override bool Validate(out string desc)
        {
            bool ok = Checksum.IntSum == Scale;
            desc = ok ? $"命中数={Checksum.IntSum}，等于期望 {Scale}" : $"命中数={Checksum.IntSum}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            _set = null;
            _keys = null;
        }
    }

    public sealed class NativeHashSetHitQueryCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private NativeParallelHashSet<HashKey> _set;

        public NativeHashSetHitQueryCase(int scale, CollisionProfile collision)
            : base("NativeParallelHashSet", true, ContainerFamily.HashSet, "C2", "命中查询", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new NativeParallelHashSet<HashKey>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _set.Add(_keys[i]);
        }

        public override void RunOnePass()
        {
            int hits = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_set.Contains(_keys[i]))
                    hits++;
            }

            Checksum.AddInt(hits);
        }

        public override bool Validate(out string desc)
        {
            bool ok = Checksum.IntSum == Scale;
            desc = ok ? $"命中数={Checksum.IntSum}，等于期望 {Scale}" : $"命中数={Checksum.IntSum}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            if (_set.IsCreated)
                _set.Dispose();
            _keys = null;
        }
    }

    // ==================== C3 未命中查询 ====================

    public sealed class HashSetMissQueryCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private HashKey[] _missKeys;
        private HashSet<HashKey> _set;

        public HashSetMissQueryCase(int scale, CollisionProfile collision)
            : base("HashSet", false, ContainerFamily.HashSet, "C3", "未命中查询", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _missKeys = HashKeyFactory.CreateMissKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new HashSet<HashKey>(Scale);
            for (int i = 0; i < Scale; i++)
                _set.Add(_keys[i]);
        }

        public override void RunOnePass()
        {
            int hits = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_set.Contains(_missKeys[i]))
                    hits++;
            }

            Checksum.AddInt(hits);
        }

        public override bool Validate(out string desc)
        {
            bool ok = Checksum.IntSum == 0 && _set.Count == Scale;
            desc = ok ? "命中数=0，Count 不变" : $"命中数={Checksum.IntSum}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            _set = null;
            _keys = null;
            _missKeys = null;
        }
    }

    public sealed class NativeHashSetMissQueryCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private HashKey[] _missKeys;
        private NativeParallelHashSet<HashKey> _set;

        public NativeHashSetMissQueryCase(int scale, CollisionProfile collision)
            : base("NativeParallelHashSet", true, ContainerFamily.HashSet, "C3", "未命中查询", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _missKeys = HashKeyFactory.CreateMissKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new NativeParallelHashSet<HashKey>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _set.Add(_keys[i]);
        }

        public override void RunOnePass()
        {
            int hits = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_set.Contains(_missKeys[i]))
                    hits++;
            }

            Checksum.AddInt(hits);
        }

        public override bool Validate(out string desc)
        {
            bool ok = Checksum.IntSum == 0 && _set.Count() == Scale;
            desc = ok ? "命中数=0，Count 不变" : $"命中数={Checksum.IntSum}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            if (_set.IsCreated)
                _set.Dispose();
            _keys = null;
            _missKeys = null;
        }
    }

    // ==================== C4 删除 ====================

    public sealed class HashSetRemoveCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private HashSet<HashKey> _set;

        public HashSetRemoveCase(int scale, CollisionProfile collision)
            : base("HashSet", false, ContainerFamily.HashSet, "C4", "删除", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new HashSet<HashKey>(Scale);
            for (int i = 0; i < Scale; i++)
                _set.Add(_keys[i]);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _set.Remove(_keys[i]);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _set.Count == 0;
            desc = ok ? "Count=0，删除干净" : $"Count={_set.Count}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            _set = null;
            _keys = null;
        }
    }

    public sealed class NativeHashSetRemoveCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private NativeParallelHashSet<HashKey> _set;

        public NativeHashSetRemoveCase(int scale, CollisionProfile collision)
            : base("NativeParallelHashSet", true, ContainerFamily.HashSet, "C4", "删除", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new NativeParallelHashSet<HashKey>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _set.Add(_keys[i]);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _set.Remove(_keys[i]);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _set.Count() == 0;
            desc = ok ? "Count=0，删除干净" : $"Count={_set.Count()}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            if (_set.IsCreated)
                _set.Dispose();
            _keys = null;
        }
    }

    // ==================== C5 遍历 ====================

    public sealed class HashSetTraverseCase : BenchmarkCaseBase
    {
        private HashSet<HashKey> _set;

        public HashSetTraverseCase(int scale, CollisionProfile collision)
            : base("HashSet", false, ContainerFamily.HashSet, "C5", "遍历(foreach)", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new HashSet<HashKey>(Scale);
            for (int i = 0; i < Scale; i++)
                _set.Add(keys[i]);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            foreach (var key in _set)
                sum += key.id;
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
            _set = null;
        }
    }

    public sealed class NativeHashSetTraverseCase : BenchmarkCaseBase
    {
        private NativeParallelHashSet<HashKey> _set;

        public NativeHashSetTraverseCase(int scale, CollisionProfile collision)
            : base("NativeParallelHashSet", true, ContainerFamily.HashSet, "C5", "遍历(foreach)", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new NativeParallelHashSet<HashKey>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _set.Add(keys[i]);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            foreach (HashKey key in _set)
                sum += key.id;
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
            if (_set.IsCreated)
                _set.Dispose();
        }
    }

    // ==================== C6 Job 遍历求和 ====================

    public sealed class HashSetJobTraverseCase : BenchmarkCaseBase
    {
        private HashSet<HashKey> _set;
        private NativeArray<HashKey> _input;
        private NativeList<long> _result;

        public HashSetJobTraverseCase(int scale, CollisionProfile collision)
            : base("HashSet", false, ContainerFamily.HashSet, "C6", "Job遍历求和", true,
                   scale, BenchmarkValueType.IntVector3, collision, true)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new HashSet<HashKey>(Scale);
            for (int i = 0; i < Scale; i++)
                _set.Add(keys[i]);
            _input = new NativeArray<HashKey>(Scale, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            _result = new NativeList<long>(1, Allocator.TempJob);
            _result.Add(0);
        }

        public override void RunOnePass()
        {
            // 公平性规则：托管容器逐元素复制到预分配 NativeArray，复制耗时计入；计时区不分配。
            int index = 0;
            foreach (HashKey key in _set)
                _input[index++] = key;

            _result[0] = 0;
            new SumHashKeySequentialJob { Input = _input, Result = _result }
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
            _set = null;
        }
    }

    public sealed class NativeHashSetJobTraverseCase : BenchmarkCaseBase
    {
        private NativeParallelHashSet<HashKey> _set;
        private NativeList<long> _result;

        public NativeHashSetJobTraverseCase(int scale, CollisionProfile collision)
            : base("NativeParallelHashSet", true, ContainerFamily.HashSet, "C6", "Job遍历求和", true,
                   scale, BenchmarkValueType.IntVector3, collision, true)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _set = new NativeParallelHashSet<HashKey>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _set.Add(keys[i]);
            _result = new NativeList<long>(1, Allocator.TempJob);
            _result.Add(0);
        }

        public override void RunOnePass()
        {
            // Native 容器零拷贝：ReadOnly 视图由单个 Burst Job 直接遍历。
            _result[0] = 0;
            new SumNativeHashSetJob { Input = _set.AsReadOnly(), Result = _result }
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
            if (_set.IsCreated)
                _set.Dispose();
        }
    }

    // ==================== C7 并行写（仅 NativeParallelHashSet） ====================

    public sealed class NativeHashSetParallelWriteCase : BenchmarkCaseBase
    {
        private NativeArray<HashKey> _keys;
        private NativeParallelHashSet<HashKey> _set;

        public NativeHashSetParallelWriteCase(int scale, CollisionProfile collision)
            : base("NativeParallelHashSet", true, ContainerFamily.HashSet, "C7", "并行写(ParallelWriter)", true,
                   scale, BenchmarkValueType.IntVector3, collision, true)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = new NativeArray<HashKey>(
                HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed),
                Allocator.TempJob);
            _set = new NativeParallelHashSet<HashKey>(Scale, Allocator.TempJob); // 预分配容量
        }

        public override void RunOnePass()
        {
            // IJobParallelFor + ParallelWriter 按 index 分片并发插入
            new HashKeyParallelInsertJob
            {
                Keys = _keys,
                Writer = _set.AsParallelWriter(),
            }.Schedule(Scale, BenchmarkConfig.JobInnerLoopBatch).Complete();
            // Count 汇总属于校验/结果统计，不放进并行写计时区；完成的 Job 句柄已由 Complete 消费。
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _set.Count() == Scale;
            if (ok)
            {
                for (int i = 0; i < _keys.Length; i++)
                {
                    if (!_set.Contains(_keys[i]))
                    {
                        ok = false;
                        break;
                    }
                }
            }

            desc = ok
                ? $"Count={_set.Count()}，全部 key 均可查到"
                : $"Count={_set.Count()}，期望 {Scale}，存在缺失 key";
            return ok;
        }

        public override void Teardown()
        {
            if (_keys.IsCreated)
                _keys.Dispose();
            if (_set.IsCreated)
                _set.Dispose();
        }
    }
}
