using System;using Unity.Collections;

namespace ContainerBenchmark
{
    /// <summary>
    /// 碰撞可控的 int key：Equals 按完整 id 比较；GetHashCode 按 hashOverride 返回。
    /// 同时满足托管 Dictionary 与 Native 容器（unmanaged + IEquatable）的要求，
    /// C# 与 Native 用例使用同一份 key 数组、完全相同的碰撞条件。
    /// </summary>
    public readonly struct HashKey : IEquatable<HashKey>
    {
        /// <summary>完整 key 值（0..N-1 唯一）。</summary>
        public readonly int id;

        /// <summary>哈希覆盖值：GetHashCode 直接返回它，按碰撞档位设置。</summary>
        public readonly int hashOverride;

        public HashKey(int id, int hashOverride)
        {
            this.id = id;
            this.hashOverride = hashOverride;
        }

        /// <summary>按完整 id 比较（不受 hashOverride 影响）。</summary>
        public bool Equals(HashKey other)
        {
            return id == other.id;
        }

        public override bool Equals(object obj)
        {
            return obj is HashKey other && Equals(other);
        }

        /// <summary>按碰撞档位返回 hashOverride。</summary>
        public override int GetHashCode()
        {
            return hashOverride;
        }

        public static bool operator ==(HashKey a, HashKey b)
        {
            return a.id == b.id;
        }

        public static bool operator !=(HashKey a, HashKey b)
        {
            return a.id != b.id;
        }

        public override string ToString()
        {
            return $"HashKey(id={id}, hash={hashOverride})";
        }
    }

    /// <summary>
    /// key 批量生成工厂：固定种子、可复现。
    /// 碰撞三档统一在此生成，C# 与 Native 用例共用同一份数组。
    /// </summary>
    public static class HashKeyFactory
    {
        /// <summary>
        /// 生成 N 个唯一 key（id = idOffset + 0..N-1），hashOverride 按碰撞档设置，
        /// 再用固定种子 Fisher-Yates 洗牌打散插入顺序（可复现，不改变元素内容）。
        /// </summary>
        public static HashKey[] CreateKeys(int scale, CollisionProfile collision, int seed, int idOffset = 0)
        {
            if (scale <= 0)
                throw new ArgumentOutOfRangeException(nameof(scale), "scale 必须大于 0");

            var keys = new HashKey[scale];
            for (int i = 0; i < scale; i++)
            {
                int id = idOffset + i;
                keys[i] = new HashKey(id, ComputeHashOverride(id, scale, collision));
            }

            Shuffle(keys, seed);
            return keys;
        }

        /// <summary>
        /// 未命中 key：id 从 N 开始偏移，与命中集合（0..N-1）无交集，碰撞规则相同。
        /// </summary>
        public static HashKey[] CreateMissKeys(int scale, CollisionProfile collision, int seed)
        {
            return CreateKeys(scale, collision, seed, idOffset: scale);
        }

        /// <summary>
        /// 字符串 key：8~32 字符随机（字母/数字/下划线），固定种子可复现。
        /// </summary>
        public static string[] CreateStringKeys(int scale, int seed)
        {
            if (scale <= 0)
                throw new ArgumentOutOfRangeException(nameof(scale), "scale 必须大于 0");

            var keys = new string[scale];
            var rng = new XorShift64((ulong)seed + 0x9E3779B97F4A7C15UL);
            for (int i = 0; i < scale; i++)
            {
                int length = 8 + (int)(rng.NextUInt64() % 25); // 8..32
                keys[i] = NextString(ref rng, length);
            }

            return keys;
        }

        /// <summary>
        /// FixedString64Bytes key：与 <see cref="CreateStringKeys"/> 同种子、逐字符同内容，
        /// 保证 C# 托管字典与 Native 容器使用完全相同的字符串 key 集合。
        /// </summary>
        public static FixedString64Bytes[] CreateFixedStringKeys(int scale, int seed)
        {
            string[] strings = CreateStringKeys(scale, seed);
            var keys = new FixedString64Bytes[scale];
            for (int i = 0; i < scale; i++)
                keys[i] = new FixedString64Bytes(strings[i]);
            return keys;
        }

        /// <summary>按碰撞档位计算 hashOverride。</summary>
        private static int ComputeHashOverride(int id, int scale, CollisionProfile collision)
        {
            switch (collision)
            {
                case CollisionProfile.Normal:
                    return id; // 正常散列：id 本身
                case CollisionProfile.LimitedDomain:
                    return id % System.Math.Max(1, scale / 16); // 有限域：约 N/16 个哈希值、平均约 16 key/hash
                case CollisionProfile.AllCollision:
                    return 0; // 全碰撞
                default:
                    return id;
            }
        }

        /// <summary>固定种子 Fisher-Yates 洗牌（只交换位置，元素内容不变）。</summary>
        private static void Shuffle(HashKey[] array, int seed)
        {
            var rng = new XorShift64((ulong)seed + 0x2545F4914F6CDD1DUL);
            for (int i = array.Length - 1; i > 0; i--)
            {
                int j = (int)(rng.NextUInt64() % (ulong)(i + 1));
                (array[i], array[j]) = (array[j], array[i]);
            }
        }

        private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

        private static string NextString(ref XorShift64 rng, int length)
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = Alphabet[(int)(rng.NextUInt64() % (ulong)Alphabet.Length)];
            return new string(chars);
        }
    }

    /// <summary>
    /// 跨运行时确定性的 xorshift64 伪随机数（System.Random 在不同运行时算法可能不同，
    /// 为保证 Editor/IL2CPP Player 生成序列完全一致，这里自实现）。
    /// </summary>
    internal struct XorShift64
    {
        private ulong _state;

        public XorShift64(ulong seed)
        {
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public ulong NextUInt64()
        {
            ulong x = _state;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            _state = x;
            return x;
        }
    }
}
