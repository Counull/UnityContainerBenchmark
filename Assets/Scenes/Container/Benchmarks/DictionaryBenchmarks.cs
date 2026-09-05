using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace ContainerBenchmark
{
    /// <summary>
    /// B 组用例工厂：Dictionary ↔ NativeHashMap。
    /// int-key 场景：Dictionary&lt;HashKey, Vector3&gt; vs NativeHashMap&lt;HashKey, Vector3&gt;（B1~B7，含碰撞三档）；
    /// 字符串专项：Dictionary&lt;string, int&gt; vs NativeHashMap&lt;FixedString64Bytes, int&gt;（B1S/B2S/B3S/B6S，仅正常形态）。
    /// </summary>
    public static class DictionaryBenchmarkCases
    {
        public static List<IBenchmarkCase> CreateAll(int scale, CollisionProfile collision, bool useJob)
        {
            var cases = new List<IBenchmarkCase>(14)
            {
                new DictInsertCase(scale, collision),
                new NativeMapInsertCase(scale, collision),
                new DictHitQueryCase(scale, collision),
                new NativeMapHitQueryCase(scale, collision),
                new DictMissQueryCase(scale, collision),
                new NativeMapMissQueryCase(scale, collision),
                new DictOverwriteCase(scale, collision),
                new NativeMapOverwriteCase(scale, collision),
                new DictRemoveCase(scale, collision),
                new NativeMapRemoveCase(scale, collision),
                new DictTraverseCase(scale, collision),
                new NativeMapTraverseCase(scale, collision),
            };

            if (useJob)
            {
                cases.Add(new DictJobTraverseCase(scale, collision));
                cases.Add(new NativeMapJobTraverseCase(scale, collision));
            }

            return cases;
        }

        /// <summary>字符串专项：仅 B1/B2/B3/B6，固定 Normal 碰撞档。</summary>
        public static List<IBenchmarkCase> CreateStringCases(int scale)
        {
            return new List<IBenchmarkCase>(8)
            {
                new DictStringInsertCase(scale),
                new NativeMapStringInsertCase(scale),
                new DictStringHitQueryCase(scale),
                new NativeMapStringHitQueryCase(scale),
                new DictStringMissQueryCase(scale),
                new NativeMapStringMissQueryCase(scale),
                new DictStringTraverseCase(scale),
                new NativeMapStringTraverseCase(scale),
            };
        }
    }

    /// <summary>B 组公共辅助。</summary>
    internal static class DictHelpers
    {
        /// <summary>key.id 对应的 Vector3 值（确定性）。</summary>
        internal static Vector3 ValueOf(int id)
        {
            // 各分量均为 0/1，10M 档累计仍小于 float 精确整数上限 2^24；
            // 因而无论 Dictionary/NativeHashMap 采用何种枚举顺序，Vector3 累加都精确一致。
            int parity = id & 1;
            return new Vector3(parity, 1 - parity, 1f);
        }

        /// <summary>B4 覆盖写入的新值（与旧值区分）。</summary>
        internal static Vector3 NewValueOf(int id)
        {
            return new Vector3(id + 1_000_000f, id + 1_000_001f, id + 1_000_002f);
        }

        /// <summary>全部 N 个有限值域 value 的精确和。</summary>
        internal static Vector3 ExpectedValueSum(int n)
        {
            int oddCount = n / 2;
            int evenCount = n - oddCount;
            return new Vector3(oddCount, evenCount, n);
        }

        /// <summary>
        /// 有限值域保证三个分量在 10M 档以内可被 float 精确表示，因此使用逐分量精确比较。
        /// </summary>
        internal static bool VectorSumExact(Vector3 actual, Vector3 expected)
        {
            return actual.x == expected.x && actual.y == expected.y && actual.z == expected.z;
        }
    }

    // ==================== B1 插入（TryAdd） ====================

    public sealed class DictInsertCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private Vector3[] _values;
        private Dictionary<HashKey, Vector3> _dict;

        public DictInsertCase(int scale, CollisionProfile collision)
            : base("Dictionary", false, ContainerFamily.Dictionary, "B1", "插入(TryAdd)", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _values = new Vector3[Scale];
            for (int i = 0; i < Scale; i++)
                _values[i] = DictHelpers.ValueOf(_keys[i].id);
            _dict = new Dictionary<HashKey, Vector3>(Scale); // 预分配容量，不测扩容
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(_keys[i], _values[i]);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _dict.Count == Scale;
            desc = ok ? $"Count={_dict.Count}，等于期望 {Scale}" : $"Count={_dict.Count}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            _dict = null;
            _values = null;
            _keys = null;
        }
    }

    public sealed class NativeMapInsertCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private Vector3[] _values;
        private NativeHashMap<HashKey, Vector3> _map;

        public NativeMapInsertCase(int scale, CollisionProfile collision)
            : base("NativeHashMap", true, ContainerFamily.Dictionary, "B1", "插入(TryAdd)", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _values = new Vector3[Scale];
            for (int i = 0; i < Scale; i++)
                _values[i] = DictHelpers.ValueOf(_keys[i].id);
            _map = new NativeHashMap<HashKey, Vector3>(Scale, Allocator.TempJob); // 预分配容量
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(_keys[i], _values[i]);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _map.Count == Scale;
            desc = ok ? $"Count={_map.Count}，等于期望 {Scale}" : $"Count={_map.Count}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            if (_map.IsCreated)
                _map.Dispose();
            _values = null;
            _keys = null;
        }
    }

    // ==================== B2 命中查询 ====================

    public sealed class DictHitQueryCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private Dictionary<HashKey, Vector3> _dict;

        public DictHitQueryCase(int scale, CollisionProfile collision)
            : base("Dictionary", false, ContainerFamily.Dictionary, "B2", "命中查询", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _dict = new Dictionary<HashKey, Vector3>(Scale);
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(_keys[i], DictHelpers.ValueOf(_keys[i].id));
        }

        public override void RunOnePass()
        {
            var sum = Vector3.zero;
            for (int i = 0; i < Scale; i++)
            {
                if (_dict.TryGetValue(_keys[i], out var value))
                    sum += value;
            }

            Checksum.AddVector(sum);
        }

        public override bool Validate(out string desc)
        {
            Vector3 expected = DictHelpers.ExpectedValueSum(Scale);
            bool ok = DictHelpers.VectorSumExact(Checksum.VectorSum, expected);
            desc = ok
                ? $"命中查询累计和={Checksum.VectorSum}，精确等于期望"
                : $"命中查询累计和={Checksum.VectorSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            _dict = null;
            _keys = null;
        }
    }

    public sealed class NativeMapHitQueryCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private NativeHashMap<HashKey, Vector3> _map;

        public NativeMapHitQueryCase(int scale, CollisionProfile collision)
            : base("NativeHashMap", true, ContainerFamily.Dictionary, "B2", "命中查询", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _map = new NativeHashMap<HashKey, Vector3>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(_keys[i], DictHelpers.ValueOf(_keys[i].id));
        }

        public override void RunOnePass()
        {
            var sum = Vector3.zero;
            for (int i = 0; i < Scale; i++)
            {
                if (_map.TryGetValue(_keys[i], out var value))
                    sum += value;
            }

            Checksum.AddVector(sum);
        }

        public override bool Validate(out string desc)
        {
            Vector3 expected = DictHelpers.ExpectedValueSum(Scale);
            bool ok = DictHelpers.VectorSumExact(Checksum.VectorSum, expected);
            desc = ok
                ? $"命中查询累计和={Checksum.VectorSum}，精确等于期望"
                : $"命中查询累计和={Checksum.VectorSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_map.IsCreated)
                _map.Dispose();
            _keys = null;
        }
    }

    // ==================== B3 未命中查询 ====================

    public sealed class DictMissQueryCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private HashKey[] _missKeys;
        private Dictionary<HashKey, Vector3> _dict;

        public DictMissQueryCase(int scale, CollisionProfile collision)
            : base("Dictionary", false, ContainerFamily.Dictionary, "B3", "未命中查询", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _missKeys = HashKeyFactory.CreateMissKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _dict = new Dictionary<HashKey, Vector3>(Scale);
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(_keys[i], DictHelpers.ValueOf(_keys[i].id));
        }

        public override void RunOnePass()
        {
            int hits = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_dict.TryGetValue(_missKeys[i], out _))
                    hits++;
            }

            Checksum.AddInt(hits);
        }

        public override bool Validate(out string desc)
        {
            bool ok = Checksum.IntSum == 0 && _dict.Count == Scale;
            desc = ok ? "命中数=0，Count 不变" : $"命中数={Checksum.IntSum}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            _dict = null;
            _keys = null;
            _missKeys = null;
        }
    }

    public sealed class NativeMapMissQueryCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private HashKey[] _missKeys;
        private NativeHashMap<HashKey, Vector3> _map;

        public NativeMapMissQueryCase(int scale, CollisionProfile collision)
            : base("NativeHashMap", true, ContainerFamily.Dictionary, "B3", "未命中查询", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _missKeys = HashKeyFactory.CreateMissKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _map = new NativeHashMap<HashKey, Vector3>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(_keys[i], DictHelpers.ValueOf(_keys[i].id));
        }

        public override void RunOnePass()
        {
            int hits = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_map.TryGetValue(_missKeys[i], out _))
                    hits++;
            }

            Checksum.AddInt(hits);
        }

        public override bool Validate(out string desc)
        {
            bool ok = Checksum.IntSum == 0 && _map.Count == Scale;
            desc = ok ? "命中数=0，Count 不变" : $"命中数={Checksum.IntSum}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            if (_map.IsCreated)
                _map.Dispose();
            _keys = null;
            _missKeys = null;
        }
    }

    // ==================== B4 覆盖 ====================

    public sealed class DictOverwriteCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private Vector3[] _newValues;
        private Dictionary<HashKey, Vector3> _dict;

        public DictOverwriteCase(int scale, CollisionProfile collision)
            : base("Dictionary", false, ContainerFamily.Dictionary, "B4", "覆盖", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _newValues = new Vector3[Scale];
            _dict = new Dictionary<HashKey, Vector3>(Scale);
            for (int i = 0; i < Scale; i++)
            {
                _dict.TryAdd(_keys[i], DictHelpers.ValueOf(_keys[i].id));
                _newValues[i] = DictHelpers.NewValueOf(_keys[i].id);
            }
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _dict[_keys[i]] = _newValues[i];
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _dict.Count == Scale;
            if (ok)
            {
                for (int i = 0; i < Scale; i++)
                {
                    if (!_dict.TryGetValue(_keys[i], out var value) || value != _newValues[i])
                    {
                        ok = false;
                        break;
                    }
                }
            }

            desc = ok ? "覆盖后 TryGetValue 全部读到新值，Count 不变" : "覆盖校验失败：存在旧值或缺失 key";
            return ok;
        }

        public override void Teardown()
        {
            _dict = null;
            _newValues = null;
            _keys = null;
        }
    }

    public sealed class NativeMapOverwriteCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private Vector3[] _newValues;
        private NativeHashMap<HashKey, Vector3> _map;

        public NativeMapOverwriteCase(int scale, CollisionProfile collision)
            : base("NativeHashMap", true, ContainerFamily.Dictionary, "B4", "覆盖", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _newValues = new Vector3[Scale];
            _map = new NativeHashMap<HashKey, Vector3>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
            {
                _map.TryAdd(_keys[i], DictHelpers.ValueOf(_keys[i].id));
                _newValues[i] = DictHelpers.NewValueOf(_keys[i].id);
            }
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _map[_keys[i]] = _newValues[i]; // NativeHashMap 索引器 set = 插入或覆盖
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _map.Count == Scale;
            if (ok)
            {
                for (int i = 0; i < Scale; i++)
                {
                    if (!_map.TryGetValue(_keys[i], out var value) || value != _newValues[i])
                    {
                        ok = false;
                        break;
                    }
                }
            }

            desc = ok ? "覆盖后 TryGetValue 全部读到新值，Count 不变" : "覆盖校验失败：存在旧值或缺失 key";
            return ok;
        }

        public override void Teardown()
        {
            if (_map.IsCreated)
                _map.Dispose();
            _newValues = null;
            _keys = null;
        }
    }

    // ==================== B5 删除 ====================

    public sealed class DictRemoveCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private Dictionary<HashKey, Vector3> _dict;

        public DictRemoveCase(int scale, CollisionProfile collision)
            : base("Dictionary", false, ContainerFamily.Dictionary, "B5", "删除", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _dict = new Dictionary<HashKey, Vector3>(Scale);
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(_keys[i], DictHelpers.ValueOf(_keys[i].id));
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _dict.Remove(_keys[i]);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _dict.Count == 0;
            desc = ok ? "Count=0，删除干净" : $"Count={_dict.Count}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            _dict = null;
            _keys = null;
        }
    }

    public sealed class NativeMapRemoveCase : BenchmarkCaseBase
    {
        private HashKey[] _keys;
        private NativeHashMap<HashKey, Vector3> _map;

        public NativeMapRemoveCase(int scale, CollisionProfile collision)
            : base("NativeHashMap", true, ContainerFamily.Dictionary, "B5", "删除", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _map = new NativeHashMap<HashKey, Vector3>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(_keys[i], DictHelpers.ValueOf(_keys[i].id));
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _map.Remove(_keys[i]);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _map.Count == 0;
            desc = ok ? "Count=0，删除干净" : $"Count={_map.Count}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            if (_map.IsCreated)
                _map.Dispose();
            _keys = null;
        }
    }

    // ==================== B6 遍历 ====================

    public sealed class DictTraverseCase : BenchmarkCaseBase
    {
        private Dictionary<HashKey, Vector3> _dict;

        public DictTraverseCase(int scale, CollisionProfile collision)
            : base("Dictionary", false, ContainerFamily.Dictionary, "B6", "遍历(foreach)", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _dict = new Dictionary<HashKey, Vector3>(Scale);
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(keys[i], DictHelpers.ValueOf(keys[i].id));
        }

        public override void RunOnePass()
        {
            var sum = Vector3.zero;
            foreach (var pair in _dict)
                sum += pair.Value;
            Checksum.AddVector(sum);
        }

        public override bool Validate(out string desc)
        {
            Vector3 expected = DictHelpers.ExpectedValueSum(Scale);
            bool ok = DictHelpers.VectorSumExact(Checksum.VectorSum, expected);
            desc = ok
                ? $"遍历累计和={Checksum.VectorSum}，精确等于期望"
                : $"遍历累计和={Checksum.VectorSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            _dict = null;
        }
    }

    public sealed class NativeMapTraverseCase : BenchmarkCaseBase
    {
        private NativeHashMap<HashKey, Vector3> _map;

        public NativeMapTraverseCase(int scale, CollisionProfile collision)
            : base("NativeHashMap", true, ContainerFamily.Dictionary, "B6", "遍历(foreach)", true,
                   scale, BenchmarkValueType.IntVector3, collision, false)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _map = new NativeHashMap<HashKey, Vector3>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(keys[i], DictHelpers.ValueOf(keys[i].id));
        }

        public override void RunOnePass()
        {
            var sum = Vector3.zero;
            foreach (var pair in _map)
                sum += pair.Value;
            Checksum.AddVector(sum);
        }

        public override bool Validate(out string desc)
        {
            Vector3 expected = DictHelpers.ExpectedValueSum(Scale);
            bool ok = DictHelpers.VectorSumExact(Checksum.VectorSum, expected);
            desc = ok
                ? $"遍历累计和={Checksum.VectorSum}，精确等于期望"
                : $"遍历累计和={Checksum.VectorSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_map.IsCreated)
                _map.Dispose();
        }
    }

    // ==================== B7 Job 遍历求和 ====================

    public sealed class DictJobTraverseCase : BenchmarkCaseBase
    {
        private Dictionary<HashKey, Vector3> _dict;
        private NativeArray<Vector3> _input;
        private NativeList<Vector3> _result;

        public DictJobTraverseCase(int scale, CollisionProfile collision)
            : base("Dictionary", false, ContainerFamily.Dictionary, "B7", "Job遍历求和", true,
                   scale, BenchmarkValueType.IntVector3, collision, true)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _dict = new Dictionary<HashKey, Vector3>(Scale);
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(keys[i], DictHelpers.ValueOf(keys[i].id));
            _input = new NativeArray<Vector3>(Scale, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            _result = new NativeList<Vector3>(1, Allocator.TempJob);
            _result.Add(Vector3.zero);
        }

        public override void RunOnePass()
        {
            // 公平性规则：托管容器逐元素复制到预分配 NativeArray，复制耗时计入；计时区不分配。
            // Dictionary.Values 会在每个新 Dictionary 的首次访问时创建并缓存
            // ValueCollection；直接枚举 Dictionary 可避免把这笔 GC 误计入 B7。
            int index = 0;
            foreach (KeyValuePair<HashKey, Vector3> pair in _dict)
                _input[index++] = pair.Value;

            _result[0] = Vector3.zero;
            new SumVector3SequentialJob { Input = _input, Result = _result }
                .Schedule().Complete();
            Checksum.AddVector(_result[0]);
        }

        public override bool Validate(out string desc)
        {
            Vector3 expected = DictHelpers.ExpectedValueSum(Scale);
            bool ok = DictHelpers.VectorSumExact(Checksum.VectorSum, expected);
            desc = ok
                ? $"Job 求和={Checksum.VectorSum}，精确等于期望"
                : $"Job 求和={Checksum.VectorSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_result.IsCreated)
                _result.Dispose();
            if (_input.IsCreated)
                _input.Dispose();
            _dict = null;
        }
    }

    public sealed class NativeMapJobTraverseCase : BenchmarkCaseBase
    {
        private NativeHashMap<HashKey, Vector3> _map;
        private NativeList<Vector3> _result;

        public NativeMapJobTraverseCase(int scale, CollisionProfile collision)
            : base("NativeHashMap", true, ContainerFamily.Dictionary, "B7", "Job遍历求和", true,
                   scale, BenchmarkValueType.IntVector3, collision, true)
        {
        }

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateKeys(Scale, Collision, BenchmarkConfig.DefaultSeed);
            _map = new NativeHashMap<HashKey, Vector3>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(keys[i], DictHelpers.ValueOf(keys[i].id));
            _result = new NativeList<Vector3>(1, Allocator.TempJob);
            _result.Add(Vector3.zero);
        }

        public override void RunOnePass()
        {
            // Native 容器零拷贝：ReadOnly 视图由单个 Burst Job 直接遍历。
            _result[0] = Vector3.zero;
            new SumNativeHashMapVector3Job { Input = _map.AsReadOnly(), Result = _result }
                .Schedule().Complete();
            Checksum.AddVector(_result[0]);
        }

        public override bool Validate(out string desc)
        {
            Vector3 expected = DictHelpers.ExpectedValueSum(Scale);
            bool ok = DictHelpers.VectorSumExact(Checksum.VectorSum, expected);
            desc = ok
                ? $"Job 求和={Checksum.VectorSum}，精确等于期望"
                : $"Job 求和={Checksum.VectorSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_result.IsCreated)
                _result.Dispose();
            if (_map.IsCreated)
                _map.Dispose();
        }
    }

    // ==================== 字符串专项 B1S/B2S/B3S/B6S ====================
    // 语义差异：托管 Dictionary<string,int> 用引用类型字符串（堆分配 + .NET string.GetHashCode，
    // 跨运行时可能不同）；NativeHashMap<FixedString64Bytes,int> 用定长 64 字节值类型键
    // （无堆分配、按值复制、哈希算法不同）。结果元数据标注在 SemanticNote。

    public sealed class DictStringInsertCase : BenchmarkCaseBase
    {
        private string[] _keys;
        private Dictionary<string, int> _dict;

        public DictStringInsertCase(int scale)
            : base("Dictionary(string)", false, ContainerFamily.Dictionary, "B1S", "插入(TryAdd)", false,
                   scale, BenchmarkValueType.StringKey, CollisionProfile.Normal, false)
        {
        }

        public override string SemanticNote =>
            "字符串专项：引用类型字符串 key（堆分配），哈希为 .NET string.GetHashCode（跨运行时可能不同）";

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateStringKeys(Scale, BenchmarkConfig.DefaultSeed);
            _dict = new Dictionary<string, int>(Scale);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(_keys[i], i);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _dict.Count == Scale;
            desc = ok ? $"Count={_dict.Count}，等于期望 {Scale}" : $"Count={_dict.Count}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            _dict = null;
            _keys = null;
        }
    }

    public sealed class NativeMapStringInsertCase : BenchmarkCaseBase
    {
        private FixedString64Bytes[] _keys;
        private NativeHashMap<FixedString64Bytes, int> _map;

        public NativeMapStringInsertCase(int scale)
            : base("NativeHashMap(FixedString64)", true, ContainerFamily.Dictionary, "B1S", "插入(TryAdd)", false,
                   scale, BenchmarkValueType.StringKey, CollisionProfile.Normal, false)
        {
        }

        public override string SemanticNote =>
            "字符串专项：定长 64 字节值类型键（FixedString64Bytes，无堆分配、按值复制），哈希算法与 C# string 不同";

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateFixedStringKeys(Scale, BenchmarkConfig.DefaultSeed);
            _map = new NativeHashMap<FixedString64Bytes, int>(Scale, Allocator.TempJob);
        }

        public override void RunOnePass()
        {
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(_keys[i], i);
            Checksum.AddLong(Scale);
        }

        public override bool Validate(out string desc)
        {
            bool ok = _map.Count == Scale;
            desc = ok ? $"Count={_map.Count}，等于期望 {Scale}" : $"Count={_map.Count}，期望 {Scale}";
            return ok;
        }

        public override void Teardown()
        {
            if (_map.IsCreated)
                _map.Dispose();
            _keys = null;
        }
    }

    public sealed class DictStringHitQueryCase : BenchmarkCaseBase
    {
        private string[] _keys;
        private Dictionary<string, int> _dict;

        public DictStringHitQueryCase(int scale)
            : base("Dictionary(string)", false, ContainerFamily.Dictionary, "B2S", "命中查询", false,
                   scale, BenchmarkValueType.StringKey, CollisionProfile.Normal, false)
        {
        }

        public override string SemanticNote =>
            "字符串专项：引用类型字符串 key（堆分配），哈希为 .NET string.GetHashCode（跨运行时可能不同）";

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateStringKeys(Scale, BenchmarkConfig.DefaultSeed);
            _dict = new Dictionary<string, int>(Scale);
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(_keys[i], i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_dict.TryGetValue(_keys[i], out int value))
                    sum += value;
            }

            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = Checksum.IntSum == expected;
            desc = ok ? $"命中查询累计和={Checksum.IntSum}，等于期望和 {expected}" : $"命中查询累计和={Checksum.IntSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            _dict = null;
            _keys = null;
        }
    }

    public sealed class NativeMapStringHitQueryCase : BenchmarkCaseBase
    {
        private FixedString64Bytes[] _keys;
        private NativeHashMap<FixedString64Bytes, int> _map;

        public NativeMapStringHitQueryCase(int scale)
            : base("NativeHashMap(FixedString64)", true, ContainerFamily.Dictionary, "B2S", "命中查询", false,
                   scale, BenchmarkValueType.StringKey, CollisionProfile.Normal, false)
        {
        }

        public override string SemanticNote =>
            "字符串专项：定长 64 字节值类型键（FixedString64Bytes，无堆分配、按值复制），哈希算法与 C# string 不同";

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateFixedStringKeys(Scale, BenchmarkConfig.DefaultSeed);
            _map = new NativeHashMap<FixedString64Bytes, int>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(_keys[i], i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_map.TryGetValue(_keys[i], out int value))
                    sum += value;
            }

            Checksum.AddLong(sum);
        }

        public override bool Validate(out string desc)
        {
            long expected = ListHelpers.ExpectedSum(Scale);
            bool ok = Checksum.IntSum == expected;
            desc = ok ? $"命中查询累计和={Checksum.IntSum}，等于期望和 {expected}" : $"命中查询累计和={Checksum.IntSum}，期望 {expected}";
            return ok;
        }

        public override void Teardown()
        {
            if (_map.IsCreated)
                _map.Dispose();
            _keys = null;
        }
    }

    public sealed class DictStringMissQueryCase : BenchmarkCaseBase
    {
        private string[] _keys;
        private string[] _missKeys;
        private Dictionary<string, int> _dict;

        public DictStringMissQueryCase(int scale)
            : base("Dictionary(string)", false, ContainerFamily.Dictionary, "B3S", "未命中查询", false,
                   scale, BenchmarkValueType.StringKey, CollisionProfile.Normal, false)
        {
        }

        public override string SemanticNote =>
            "字符串专项：引用类型字符串 key（堆分配），哈希为 .NET string.GetHashCode（跨运行时可能不同）";

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateStringKeys(Scale, BenchmarkConfig.DefaultSeed);
            _missKeys = HashKeyFactory.CreateStringKeys(Scale, BenchmarkConfig.DefaultSeed + 101);
            _dict = new Dictionary<string, int>(Scale);
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(_keys[i], i);
        }

        public override void RunOnePass()
        {
            int hits = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_dict.TryGetValue(_missKeys[i], out _))
                    hits++;
            }

            Checksum.AddInt(hits);
        }

        public override bool Validate(out string desc)
        {
            bool ok = Checksum.IntSum == 0 && _dict.Count == Scale;
            desc = ok ? "命中数=0，Count 不变" : $"命中数={Checksum.IntSum}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            _dict = null;
            _keys = null;
            _missKeys = null;
        }
    }

    public sealed class NativeMapStringMissQueryCase : BenchmarkCaseBase
    {
        private FixedString64Bytes[] _keys;
        private FixedString64Bytes[] _missKeys;
        private NativeHashMap<FixedString64Bytes, int> _map;

        public NativeMapStringMissQueryCase(int scale)
            : base("NativeHashMap(FixedString64)", true, ContainerFamily.Dictionary, "B3S", "未命中查询", false,
                   scale, BenchmarkValueType.StringKey, CollisionProfile.Normal, false)
        {
        }

        public override string SemanticNote =>
            "字符串专项：定长 64 字节值类型键（FixedString64Bytes，无堆分配、按值复制），哈希算法与 C# string 不同";

        public override void Setup()
        {
            Checksum.Reset();
            _keys = HashKeyFactory.CreateFixedStringKeys(Scale, BenchmarkConfig.DefaultSeed);
            _missKeys = HashKeyFactory.CreateFixedStringKeys(Scale, BenchmarkConfig.DefaultSeed + 101);
            _map = new NativeHashMap<FixedString64Bytes, int>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(_keys[i], i);
        }

        public override void RunOnePass()
        {
            int hits = 0;
            for (int i = 0; i < Scale; i++)
            {
                if (_map.TryGetValue(_missKeys[i], out _))
                    hits++;
            }

            Checksum.AddInt(hits);
        }

        public override bool Validate(out string desc)
        {
            bool ok = Checksum.IntSum == 0 && _map.Count == Scale;
            desc = ok ? "命中数=0，Count 不变" : $"命中数={Checksum.IntSum}，期望 0";
            return ok;
        }

        public override void Teardown()
        {
            if (_map.IsCreated)
                _map.Dispose();
            _keys = null;
            _missKeys = null;
        }
    }

    public sealed class DictStringTraverseCase : BenchmarkCaseBase
    {
        private Dictionary<string, int> _dict;

        public DictStringTraverseCase(int scale)
            : base("Dictionary(string)", false, ContainerFamily.Dictionary, "B6S", "遍历(foreach)", false,
                   scale, BenchmarkValueType.StringKey, CollisionProfile.Normal, false)
        {
        }

        public override string SemanticNote =>
            "字符串专项：引用类型字符串 key（堆分配），哈希为 .NET string.GetHashCode（跨运行时可能不同）";

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateStringKeys(Scale, BenchmarkConfig.DefaultSeed);
            _dict = new Dictionary<string, int>(Scale);
            for (int i = 0; i < Scale; i++)
                _dict.TryAdd(keys[i], i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            foreach (var pair in _dict)
                sum += pair.Value;
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
            _dict = null;
        }
    }

    public sealed class NativeMapStringTraverseCase : BenchmarkCaseBase
    {
        private NativeHashMap<FixedString64Bytes, int> _map;

        public NativeMapStringTraverseCase(int scale)
            : base("NativeHashMap(FixedString64)", true, ContainerFamily.Dictionary, "B6S", "遍历(foreach)", false,
                   scale, BenchmarkValueType.StringKey, CollisionProfile.Normal, false)
        {
        }

        public override string SemanticNote =>
            "字符串专项：定长 64 字节值类型键（FixedString64Bytes，无堆分配、按值复制），哈希算法与 C# string 不同";

        public override void Setup()
        {
            Checksum.Reset();
            var keys = HashKeyFactory.CreateFixedStringKeys(Scale, BenchmarkConfig.DefaultSeed);
            _map = new NativeHashMap<FixedString64Bytes, int>(Scale, Allocator.TempJob);
            for (int i = 0; i < Scale; i++)
                _map.TryAdd(keys[i], i);
        }

        public override void RunOnePass()
        {
            long sum = 0;
            foreach (var pair in _map)
                sum += pair.Value;
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
            if (_map.IsCreated)
                _map.Dispose();
        }
    }
}
