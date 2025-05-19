// NceThreadTable.cs
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Linq;  

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// 高性能线程上下文管理表，支持动态扩容
    /// </summary>
    public static class NceThreadTable
    {
        #region 常量定义
        private const int InitialSegmentSize = 4096;    // 初始每个内存块管理的条目数
        private const int BitmapElements = 64;          // 每个位图段的ulong数量
        private const int BitsPerElement = 64;          // 每个ulong的位数
        private const int MaxSegments = int.MaxValue;   // 最大内存块数量（理论限制）
        #endregion

        #region 结构体定义
        /// <summary>
        /// 线程上下文条目结构
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
        private struct ThreadEntry
        {
            public IntPtr ThreadId;         // 线程ID（8字节）
            public IntPtr NativeContextPtr; // 原生上下文指针（8字节）
        }

        /// <summary>
        /// 内存块描述结构
        /// </summary>
        private class MemorySegment
        {
            public MemoryBlock Block;       // 内存块实例
            public ulong[] Bitmap;          // 位图（每个bit表示条目是否使用）
            public object SegmentLock;      // 块级锁对象
            public int ActiveCount;         // 当前活跃条目数

            public MemorySegment(int entryCount)
            {
                Block = new MemoryBlock((ulong)(Unsafe.SizeOf<ThreadEntry>() * entryCount));
                Bitmap = new ulong[entryCount / BitsPerElement];
                SegmentLock = new object();
                ActiveCount = 0;
            }
        }
        #endregion

        #region 私有字段
        private static readonly List<MemorySegment> _segments = new List<MemorySegment>();  // 内存块列表
        private static readonly ConcurrentQueue<int> _freeIndices = new ConcurrentQueue<int>(); // 空闲索引队列
        private static readonly object _expansionLock = new object(); // 扩容操作锁
        private static int _currentSegmentIndex; // 当前分配的内存块索引
        #endregion

        #region 构造函数
        /// <summary>
        /// 静态构造函数初始化首个内存块
        /// </summary>
        static NceThreadTable()
        {
            ExpandCapacity(InitialSegmentSize);
            _currentSegmentIndex = 0;
        }
        #endregion

        #region 公共方法
        /// <summary>
        /// 注册线程上下文
        /// </summary>
        /// <param name="threadId">线程ID</param>
        /// <param name="nativeContextPtr">原生上下文指针</param>
        /// <returns>全局索引</returns>
        public static int Register(IntPtr threadId, IntPtr nativeContextPtr)
        {
            // 尝试从空闲队列快速获取索引
            if (_freeIndices.TryDequeue(out int globalIndex))
            {
                UpdateEntry(globalIndex, threadId, nativeContextPtr);
                return globalIndex;
            }

            // 遍历现有内存块寻找空位
            for (int segIndex = 0; segIndex < _segments.Count; segIndex++)
            {
                MemorySegment segment = _segments[segIndex];
                
                // 跳过已满的块
                if (segment.ActiveCount >= InitialSegmentSize) 
                    continue;

                for (int bitmapIndex = 0; bitmapIndex < segment.Bitmap.Length; bitmapIndex++)
                {
                    ulong currentBitmap = segment.Bitmap[bitmapIndex];
                    
                    // 当前位图段已满则跳过
                    if (currentBitmap == ulong.MaxValue) 
                        continue;

                    // 使用位操作快速找到第一个空闲位
                    int bitPosition = BitOperations.TrailingZeroCount(~currentBitmap);
                    int localIndex = bitmapIndex * BitsPerElement + bitPosition;

                    lock (segment.SegmentLock)
                    {
                        // 双重检查锁定
                        if ((segment.Bitmap[bitmapIndex] & (1UL << bitPosition)) == 0)
                        {
                            // 更新位图
                            segment.Bitmap[bitmapIndex] |= 1UL << bitPosition;
                            segment.ActiveCount++;
                            
                            // 计算全局索引
                            globalIndex = segIndex * InitialSegmentSize + localIndex;
                            
                            // 写入条目数据
                            UpdateEntry(globalIndex, threadId, nativeContextPtr);
                            return globalIndex;
                        }
                    }
                }
            }

            // 触发动态扩容
            lock (_expansionLock)
            {
                // 双重检查空闲队列
                if (_freeIndices.TryDequeue(out globalIndex))
                {
                    UpdateEntry(globalIndex, threadId, nativeContextPtr);
                    return globalIndex;
                }

                // 计算新的内存块大小（指数增长策略）
                int newSize = _segments.Count == 0 ? 
                    InitialSegmentSize : 
                    _segments[^1].Bitmap.Length * 2 * BitsPerElement;
                
                ExpandCapacity(newSize);
                return Register(threadId, nativeContextPtr); // 递归调用
            }
        }

        /// <summary>
    /// 根据全局索引获取条目指针（新增方法）
    /// </summary>
    public static IntPtr GetEntryPointer(int globalIndex)
    {
        int segIndex = globalIndex / InitialSegmentSize;
        int localIndex = globalIndex % InitialSegmentSize;
        
        if (segIndex >= _segments.Count)
            throw new IndexOutOfRangeException();
        
        return _segments[segIndex].Block.GetPointer(
            (ulong)(localIndex * Unsafe.SizeOf<ThreadEntry>()),
            (ulong)Unsafe.SizeOf<ThreadEntry>());
    }

    /// <summary>
    /// 临时兼容属性（可选）
    /// </summary>
    public static IntPtr EntriesPointer => 
        _segments.Count > 0 ? _segments[0].Block.Pointer : IntPtr.Zero;
    }
    
        /// <summary>
        /// 注销线程上下文
        /// </summary>
        /// <param name="globalIndex">全局索引</param>
        public static void Unregister(int globalIndex)
        {
            // 计算所在内存块
            int segIndex = globalIndex / InitialSegmentSize;
            if (segIndex >= _segments.Count) return;

            MemorySegment segment = _segments[segIndex];
            int localIndex = globalIndex % InitialSegmentSize;

            // 计算位图位置
            int bitmapIndex = localIndex / BitsPerElement;
            int bitPosition = localIndex % BitsPerElement;

            lock (segment.SegmentLock)
            {
                // 清除位图标记
                segment.Bitmap[bitmapIndex] &= ~(1UL << bitPosition);
                segment.ActiveCount--;

                // 将索引加入空闲队列
                _freeIndices.Enqueue(globalIndex);

                // 清空条目数据
                UpdateEntry(globalIndex, IntPtr.Zero, IntPtr.Zero);
            }

            // 尝试回收完全空闲的内存块（保留第一个块）
            if (segIndex > 0 && IsSegmentEmpty(segment))
            {
                RecycleSegment(segIndex);
            }
        }

        /// <summary>
        /// 根据全局索引获取原生上下文指针
        /// </summary>
        public static IntPtr GetNativeContext(int globalIndex)
        {
            int segIndex = globalIndex / InitialSegmentSize;
            int localIndex = globalIndex % InitialSegmentSize;

            if (segIndex >= _segments.Count)
                throw new IndexOutOfRangeException("Invalid global index");

            MemorySegment segment = _segments[segIndex];
            IntPtr entryPtr = segment.Block.GetPointer(
                (ulong)(localIndex * Unsafe.SizeOf<ThreadEntry>()),
                (ulong)Unsafe.SizeOf<ThreadEntry>());

            unsafe
            {
                return ((ThreadEntry*)entryPtr)->NativeContextPtr;
            }
        }
        #endregion

        #region 私有方法
        /// <summary>
        /// 扩容内存空间
        /// </summary>
        private static void ExpandCapacity(int entryCount)
        {
            var newSegment = new MemorySegment(entryCount);
            _segments.Add(newSegment);
            _currentSegmentIndex = _segments.Count - 1;
        }

        /// <summary>
        /// 更新指定索引的条目数据
        /// </summary>
        private static void UpdateEntry(int globalIndex, IntPtr threadId, IntPtr contextPtr)
        {
            int segIndex = globalIndex / InitialSegmentSize;
            int localIndex = globalIndex % InitialSegmentSize;

            MemorySegment segment = _segments[segIndex];
            IntPtr entryPtr = segment.Block.GetPointer(
                (ulong)(localIndex * Unsafe.SizeOf<ThreadEntry>()),
                (ulong)Unsafe.SizeOf<ThreadEntry>());

            unsafe
            {
                ThreadEntry* entry = (ThreadEntry*)entryPtr;
                entry->ThreadId = threadId;
                entry->NativeContextPtr = contextPtr;
            }
        }

        /// <summary>
        /// 检查内存块是否完全空闲
        /// </summary>
        private static bool IsSegmentEmpty(MemorySegment segment)
        {
            foreach (ulong bitmap in segment.Bitmap)
            {
                if (bitmap != 0) return false;
            }
            return segment.ActiveCount == 0;
        }

        /// <summary>
        /// 回收内存块
        /// </summary>
        private static void RecycleSegment(int segIndex)
        {
            lock (_expansionLock)
            {
                if (segIndex >= _segments.Count) return;

                MemorySegment segment = _segments[segIndex];
                if (IsSegmentEmpty(segment))
                {
                    segment.Block.Dispose();
                    _segments.RemoveAt(segIndex);
                }
            }
        }
        #endregion

        #region 辅助属性（调试用）
        /// <summary>
        /// 当前总容量（调试用）
        /// </summary>
        public static int TotalCapacity => _segments.Sum(s => s.Bitmap.Length * BitsPerElement);

        /// <summary>
        /// 当前活跃条目数（调试用）
        /// </summary>
        public static int ActiveCount => _segments.Sum(s => s.ActiveCount);
        #endregion
    }
}
