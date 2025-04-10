using Ryujinx.Common.Collections;
using System;

namespace Ryujinx.Memory.WindowsShared
{
    /// <summary>
    /// A intrusive Red-Black Tree that also supports getting nodes overlapping a given range.
    /// </summary>
    /// <typeparam name="T">Type of the value stored on the node</typeparam>
    class MappingTree<T> : IntrusiveRedBlackTree<RangeNode<T>>
    {
        private const int ArrayGrowthSize = 16;
        
        public RangeNode<T>? GetNodeByKey(ulong address)
        {
            RangeNode<T>? node = Root;

            while (node != null)
            {
                int cmp = node.CompareTo(address);

                if (cmp == 0)
                {
                    return node;
                }
                else if (cmp < 0)
                {
                    node = node.Left;
                }
                else
                {
                    node = node.Right;
                }
            }

            return null;
        }
        
        public int GetNodes(ulong start, ulong end, ref RangeNode<T>[] overlaps, int overlapCount = 0)
        {
            // 明确声明 node 为可空类型
            RangeNode<T>? node = GetNodeByKey(start);

            for (; node != null; node = node.Successor)
            {
                if (overlaps.Length <= overlapCount)
                {
                    Array.Resize(ref overlaps, overlapCount + ArrayGrowthSize);
                }

                overlaps[overlapCount++] = node;

                if (node.End >= end)
                {
                    break;
                }
            }

            return overlapCount;
        }
    }

    class RangeNode<T> : IntrusiveRedBlackTreeNode<RangeNode<T>>, IComparable<RangeNode<T>>, IComparable<ulong>
    {
        public ulong Start { get; }
        public ulong End { get; private set; }
        public T Value { get; }

        public RangeNode(ulong start, ulong end, T value)
        {
            Start = start;
            End = end;
            Value = value;
        }

        public void Extend(ulong sizeDelta)
        {
            End += sizeDelta;
        }

        // 修复 CS8767：匹配接口 IComparable<RangeNode<T>> 的可空性
        public int CompareTo(RangeNode<T>? other)
        {
            // 根据 IComparable 约定，非 null 实例大于 null
            if (other is null)
            {
                return 1;
            }

            if (Start < other.Start)
            {
                return -1;
            }
            else if (Start <= other.End - 1UL)
            {
                return 0;
            }
            else
            {
                return 1;
            }
        }

        public int CompareTo(ulong address)
        {
            if (address < Start)
            {
                return 1;
            }
            else if (address <= End - 1UL)
            {
                return 0;
            }
            else
            {
                return -1;
            }
        }
    }
}
