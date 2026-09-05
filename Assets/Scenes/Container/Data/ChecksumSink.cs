using System;using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;

namespace ContainerBenchmark
{
    /// <summary>
    /// 防优化累积器：所有操作结果写入本对象；采样结束后由主控调用 Escape()
    /// 把结果写进 static 字段，防止 IL2CPP/Mono 把"结果无人消费"的代码优化消除。
    /// </summary>
    public sealed class ChecksumSink
    {
        // 逃逸目标（static 字段：任何编译器都无法证明其值无人读取）
        public static long IntGlobal;
        public static float FloatGlobalX;
        public static float FloatGlobalY;
        public static float FloatGlobalZ;

        private long _intSum;
        private float _vecX;
        private float _vecY;
        private float _vecZ;

        /// <summary>int/long 累积值。</summary>
        public long IntSum => _intSum;

        /// <summary>Vector3 累积值。</summary>
        public Vector3 VectorSum => new Vector3(_vecX, _vecY, _vecZ);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddInt(int value)
        {
            _intSum += value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddLong(long value)
        {
            _intSum += value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddVector(Vector3 value)
        {
            _vecX += value.x;
            _vecY += value.y;
            _vecZ += value.z;
        }

        /// <summary>清零累积值（每次 Setup 时调用）。</summary>
        public void Reset()
        {
            _intSum = 0;
            _vecX = 0f;
            _vecY = 0f;
            _vecZ = 0f;
        }

        /// <summary>
        /// 逃逸：把累积结果写入 static 字段。
        /// 禁止内联，保证这是一个真实的函数调用边界，IL2CPP 无法消除调用前的计算。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Escape()
        {
            IntGlobal += _intSum;
            FloatGlobalX += _vecX;
            FloatGlobalY += _vecY;
            FloatGlobalZ += _vecZ;
        }
    }

    /// <summary>
    /// 多重集校验辅助：用于 SwapBack 删除后的元素集合比对（与顺序无关）。
    /// </summary>
    public static class MultiSet
    {
        /// <summary>
        /// 校验 List 中的元素多重集 == 连续区间 [fromInclusive, toExclusive)。
        /// </summary>
        public static bool MatchesRange(List<int> list, int fromInclusive, int toExclusive)
        {
            if (list.Count != toExclusive - fromInclusive)
                return false;

            var sorted = new int[list.Count];
            list.CopyTo(sorted, 0);
            Array.Sort(sorted);
            return IsRange(sorted, fromInclusive, toExclusive);
        }

        /// <summary>NativeList 版本。</summary>
        public static bool MatchesRange(in NativeList<int> list, int fromInclusive, int toExclusive)
        {
            var array = list.ToArray(Allocator.Temp);
            try
            {
                return MatchesRange(array, fromInclusive, toExclusive);
            }
            finally
            {
                array.Dispose();
            }
        }

        /// <summary>NativeArray 版本。</summary>
        public static bool MatchesRange(NativeArray<int> array, int fromInclusive, int toExclusive)
        {
            if (array.Length != toExclusive - fromInclusive)
                return false;

            var sorted = array.ToArray();
            Array.Sort(sorted);
            return IsRange(sorted, fromInclusive, toExclusive);
        }

        /// <summary>
        /// 校验 List 与额外数组合并后的多重集 == 连续区间 [fromInclusive, toExclusive)。
        /// 典型用法：SwapBack 删除后，剩余元素 ∪ 已删元素 == 原全集。
        /// </summary>
        public static bool CombinedEqualsRange(List<int> list, int[] extra, int fromInclusive, int toExclusive)
        {
            if (list.Count + extra.Length != toExclusive - fromInclusive)
                return false;

            var merged = new int[list.Count + extra.Length];
            list.CopyTo(merged, 0);
            Array.Copy(extra, 0, merged, list.Count, extra.Length);
            Array.Sort(merged);
            return IsRange(merged, fromInclusive, toExclusive);
        }

        /// <summary>NativeList 版本。</summary>
        public static bool CombinedEqualsRange(in NativeList<int> list, int[] extra, int fromInclusive, int toExclusive)
        {
            var array = list.ToArray(Allocator.Temp);
            try
            {
                if (array.Length + extra.Length != toExclusive - fromInclusive)
                    return false;

                var merged = new int[array.Length + extra.Length];
                var managed = array.ToArray();
                Array.Copy(managed, 0, merged, 0, managed.Length);
                Array.Copy(extra, 0, merged, managed.Length, extra.Length);
                Array.Sort(merged);
                return IsRange(merged, fromInclusive, toExclusive);
            }
            finally
            {
                array.Dispose();
            }
        }

        private static bool IsRange(int[] sorted, int fromInclusive, int toExclusive)
        {
            for (int i = 0; i < sorted.Length; i++)
            {
                if (sorted[i] != fromInclusive + i)
                    return false;
            }

            return true;
        }
    }
}
