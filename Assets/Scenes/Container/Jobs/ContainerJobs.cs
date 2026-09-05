using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace ContainerBenchmark
{
    /// <summary>
    /// int 求和 Job（A8 遍历用）：单个 Burst IJob 顺序遍历并写回结果。
    /// 结果写回单元素容器，天然逃逸防优化；调度前需把 Result[0] 清零。
    /// 避免所有 worker 争用同一个原子变量，让 A8 表示遍历/调度成本而非原子串行化。
    /// </summary>
    [BurstCompile]
    public struct SumIntJob : IJob
    {
        [ReadOnly] public NativeArray<int> Input;
        public NativeList<long> Result; // 单元素

        public void Execute()
        {
            long sum = 0L;
            for (int i = 0; i < Input.Length; i++)
                sum += Input[i];
            Result[0] = sum;
        }
    }

    /// <summary>
    /// 托管 Dictionary 的 B7 求和 Job：顺序遍历预分配 NativeArray。
    /// 与 NativeHashMap 的 B7 都使用单个 IJob，保持相同调度拓扑。
    /// </summary>
    [BurstCompile]
    public struct SumVector3SequentialJob : IJob
    {
        [ReadOnly] public NativeArray<Vector3> Input;
        public NativeList<Vector3> Result;

        public void Execute()
        {
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < Input.Length; i++)
                sum += Input[i];
            Result[0] = sum;
        }
    }

    /// <summary>
    /// NativeHashMap Vector3 求和 Job（B7 Native）：直接遍历 ReadOnly 视图，
    /// 不物化 value 数组，结果写回预分配的单元素 NativeList。
    /// </summary>
    [BurstCompile]
    public struct SumNativeHashMapVector3Job : IJob
    {
        [ReadOnly] public NativeHashMap<HashKey, Vector3>.ReadOnly Input;
        public NativeList<Vector3> Result;

        public void Execute()
        {
            Vector3 sum = Vector3.zero;
            foreach (var pair in Input)
                sum += pair.Value;
            Result[0] = sum;
        }
    }

    /// <summary>
    /// 托管 HashSet 的 C6 求和 Job：顺序遍历预分配 NativeArray。
    /// 与 NativeParallelHashSet 的 C6 都使用单个 IJob，保持相同调度拓扑。
    /// </summary>
    [BurstCompile]
    public struct SumHashKeySequentialJob : IJob
    {
        [ReadOnly] public NativeArray<HashKey> Input;
        public NativeList<long> Result;

        public void Execute()
        {
            long sum = 0;
            for (int i = 0; i < Input.Length; i++)
                sum += Input[i].id;
            Result[0] = sum;
        }
    }

    /// <summary>
    /// NativeParallelHashSet 求和 Job（C6 Native）：直接遍历 ReadOnly 视图，
    /// 不物化 key 数组，结果写回预分配的单元素 NativeList。
    /// </summary>
    [BurstCompile]
    public struct SumNativeHashSetJob : IJob
    {
        [ReadOnly] public NativeParallelHashSet<HashKey>.ReadOnly Input;
        public NativeList<long> Result;

        public void Execute()
        {
            long sum = 0;
            foreach (HashKey key in Input)
                sum += key.id;
            Result[0] = sum;
        }
    }

    /// <summary>
    /// HashKey 并行写 Job（C7 用）：NativeParallelHashSet 的 ParallelWriter
    /// 按 index 分片并发插入。
    /// </summary>
    [BurstCompile]
    public struct HashKeyParallelInsertJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<HashKey> Keys;
        public NativeParallelHashSet<HashKey>.ParallelWriter Writer;

        public void Execute(int index)
        {
            Writer.Add(Keys[index]);
        }
    }
}
