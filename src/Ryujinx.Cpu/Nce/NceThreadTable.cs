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
        private const int InitialSegmentSize = 4096;
        private const int BitmapElements = 64;
        private const int BitsPerElement = 64;
        private const int MaxSegments = int.MaxValue;
        #endregion

        #region 结构体定义
        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
        private struct ThreadEntry
        {
            public IntPtr ThreadId;
            public IntPtr NativeContextPtr;
        }

        private class MemorySegment
        {
            public MemoryBlock Block;
            public ulong[] Bitmap;
            public object SegmentLock;
            public int ActiveCount;

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
        private static readonly List<MemorySegment> _segments = new List<MemorySegment>();
        private static readonly ConcurrentQueue<int> _freeIndices = new ConcurrentQueue<int>();
        private static readonly object _expansionLock = new object();
        private static int _currentSegmentIndex;
        #endregion

        #region 初始化
        static NceThreadTable()
        {
            ExpandCapacity(InitialSegmentSize);
            _currentSegmentIndex = 0;
        }
        #endregion

        #region 公共接口
        public static int Register(IntPtr threadId, IntPtr nativeContextPtr)
        {
            if (_freeIndices.TryDequeue(out int globalIndex))
            {
                UpdateEntry(globalIndex, threadId, nativeContextPtr);
                return globalIndex;
            }

            for (int segIndex = 0; segIndex < _segments.Count; segIndex++)
            {
                MemorySegment segment = _segments[segIndex];
                if (segment.ActiveCount >= InitialSegmentSize)
                    continue;

                for (int bitmapIndex = 0; bitmapIndex < segment.Bitmap.Length; bitmapIndex++)
                {
                    ulong currentBitmap = segment.Bitmap[bitmapIndex];
                    if (currentBitmap == ulong.MaxValue)
                        continue;

                    int bitPosition = BitOperations.TrailingZeroCount(~currentBitmap);
                    int localIndex = bitmapIndex * BitsPerElement + bitPosition;

                    lock (segment.SegmentLock)
                    {
                        if ((segment.Bitmap[bitmapIndex] & (1UL << bitPosition)) == 0)
                        {
                            segment.Bitmap[bitmapIndex] |= 1UL << bitPosition;
                            segment.ActiveCount++;
                            globalIndex = segIndex * InitialSegmentSize + localIndex;
                            UpdateEntry(globalIndex, threadId, nativeContextPtr);
                            return globalIndex;
                        }
                    }
                }
            }

            lock (_expansionLock)
            {
                if (_freeIndices.TryDequeue(out globalIndex))
                {
                    UpdateEntry(globalIndex, threadId, nativeContextPtr);
                    return globalIndex;
                }

                int newSize = _segments.Count == 0 ? 
                    InitialSegmentSize : 
                    _segments[^1].Bitmap.Length * 2 * BitsPerElement;
                ExpandCapacity(newSize);
                return Register(threadId, nativeContextPtr);
            }
        }

        public static void Unregister(int globalIndex)
        {
            int segIndex = globalIndex / InitialSegmentSize;
            if (segIndex >= _segments.Count) return;

            MemorySegment segment = _segments[segIndex];
            int localIndex = globalIndex % InitialSegmentSize;
            int bitmapIndex = localIndex / BitsPerElement;
            int bitPosition = localIndex % BitsPerElement;

            lock (segment.SegmentLock)
            {
                segment.Bitmap[bitmapIndex] &= ~(1UL << bitPosition);
                segment.ActiveCount--;
                _freeIndices.Enqueue(globalIndex);
                UpdateEntry(globalIndex, IntPtr.Zero, IntPtr.Zero);
            }

            if (segIndex > 0 && IsSegmentEmpty(segment))
                RecycleSegment(segIndex);
        }

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

        public static IntPtr EntriesPointer => 
            _segments.Count > 0 ? _segments[0].Block.Pointer : IntPtr.Zero;
        #endregion

        #region 私有方法
        private static void ExpandCapacity(int entryCount)
        {
            var newSegment = new MemorySegment(entryCount);
            _segments.Add(newSegment);
            _currentSegmentIndex = _segments.Count - 1;
        }

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

        private static bool IsSegmentEmpty(MemorySegment segment)
        {
            foreach (ulong bitmap in segment.Bitmap)
                if (bitmap != 0) return false;
            return segment.ActiveCount == 0;
        }

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

        #region 辅助属性
        public static int TotalCapacity => 
            _segments.Sum(s => s.Bitmap.Length * BitsPerElement);

        public static int ActiveCount => 
            _segments.Sum(s => s.ActiveCount);
        #endregion
    }
}
