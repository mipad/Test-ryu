using Ryujinx.Common;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    class MemoryAllocatorBlockList : IDisposable
    {
        private const ulong InvalidOffset = ulong.MaxValue;

        public class Block : IComparable<Block>
        {
            public DeviceMemory Memory { get; private set; }
            public IntPtr HostPointer { get; private set; }
            public ulong Size { get; }
            public bool Mapped => HostPointer != IntPtr.Zero;
            public ulong LastUsedTime { get; private set; }
            public ulong UsedSize { get; private set; }
            public int AllocationCount { get; private set; }
            public bool IsDestroyed { get; private set; }

            private readonly struct Range : IComparable<Range>
            {
                public ulong Offset { get; }
                public ulong Size { get; }

                public Range(ulong offset, ulong size)
                {
                    Offset = offset;
                    Size = size;
                }

                public int CompareTo(Range other)
                {
                    return Offset.CompareTo(other.Offset);
                }
            }

            private readonly List<Range> _freeRanges;

            public Block(DeviceMemory memory, IntPtr hostPointer, ulong size)
            {
                Memory = memory;
                HostPointer = hostPointer;
                Size = size;
                LastUsedTime = GetCurrentTimestamp();
                UsedSize = 0;
                AllocationCount = 0;
                IsDestroyed = false;
                _freeRanges = new List<Range>
                {
                    new Range(0, size),
                };
            }

            public ulong Allocate(ulong size, ulong alignment)
            {
                if (IsDestroyed)
                    return InvalidOffset;

                LastUsedTime = GetCurrentTimestamp();
                
                for (int i = 0; i < _freeRanges.Count; i++)
                {
                    var range = _freeRanges[i];

                    ulong alignedOffset = BitUtils.AlignUp(range.Offset, alignment);
                    ulong sizeDelta = alignedOffset - range.Offset;
                    ulong usableSize = range.Size - sizeDelta;

                    if (sizeDelta < range.Size && usableSize >= size)
                    {
                        _freeRanges.RemoveAt(i);

                        if (sizeDelta != 0)
                        {
                            InsertFreeRange(range.Offset, sizeDelta);
                        }

                        ulong endOffset = range.Offset + range.Size;
                        ulong remainingSize = endOffset - (alignedOffset + size);
                        if (remainingSize != 0)
                        {
                            InsertFreeRange(endOffset - remainingSize, remainingSize);
                        }

                        UsedSize += size;
                        AllocationCount++;
                        return alignedOffset;
                    }
                }

                return InvalidOffset;
            }

            public void Free(ulong offset, ulong size)
            {
                if (IsDestroyed)
                    return;

                LastUsedTime = GetCurrentTimestamp();
                UsedSize -= size;
                AllocationCount--;
                InsertFreeRangeComingled(offset, size);
            }

            private void InsertFreeRange(ulong offset, ulong size)
            {
                var range = new Range(offset, size);
                int index = _freeRanges.BinarySearch(range);
                if (index < 0)
                {
                    index = ~index;
                }

                _freeRanges.Insert(index, range);
            }

            private void InsertFreeRangeComingled(ulong offset, ulong size)
            {
                ulong endOffset = offset + size;
                var range = new Range(offset, size);
                int index = _freeRanges.BinarySearch(range);
                if (index < 0)
                {
                    index = ~index;
                }

                if (index < _freeRanges.Count && _freeRanges[index].Offset == endOffset)
                {
                    endOffset = _freeRanges[index].Offset + _freeRanges[index].Size;
                    _freeRanges.RemoveAt(index);
                }

                if (index > 0 && _freeRanges[index - 1].Offset + _freeRanges[index - 1].Size == offset)
                {
                    offset = _freeRanges[index - 1].Offset;
                    _freeRanges.RemoveAt(--index);
                }

                range = new Range(offset, endOffset - offset);

                _freeRanges.Insert(index, range);
            }

            public bool IsTotallyFree()
            {
                if (IsDestroyed)
                    return true;

                if (_freeRanges.Count == 1 && _freeRanges[0].Size == Size)
                {
                    Debug.Assert(_freeRanges[0].Offset == 0);
                    return true;
                }

                return false;
            }

            public float GetUsageRatio()
            {
                return (float)UsedSize / Size;
            }

            public bool ShouldReclaim(float threshold, ulong currentTime, ulong timeout)
            {
                if (IsDestroyed)
                    return false;

                // 只回收完全空闲的块，避免破坏正在使用的内存
                return IsTotallyFree() && (currentTime - LastUsedTime) > timeout;
            }

            public int CompareTo(Block other)
            {
                return Size.CompareTo(other.Size);
            }

            public unsafe void Destroy(Vk api, Device device)
            {
                if (IsDestroyed)
                    return;

                if (Mapped)
                {
                    api.UnmapMemory(device, Memory);
                    HostPointer = IntPtr.Zero;
                }

                if (Memory.Handle != 0)
                {
                    api.FreeMemory(device, Memory, null);
                    Memory = default;
                }

                IsDestroyed = true;
            }

            private static ulong GetCurrentTimestamp()
            {
                return (ulong)Stopwatch.GetTimestamp();
            }
        }

        private readonly List<Block> _blocks;

        private readonly Vk _api;
        private readonly Device _device;

        public int MemoryTypeIndex { get; }
        public bool ForBuffer { get; }

        private readonly int _blockAlignment;

        private readonly ReaderWriterLockSlim _lock;

        // 更保守的内存回收配置
        private readonly float _reclaimThreshold = 0.95f; // 95% 内存使用率时触发回收
        private readonly ulong _reclaimTimeout = (ulong)(Stopwatch.Frequency * 10); // 10秒超时

        public MemoryAllocatorBlockList(Vk api, Device device, int memoryTypeIndex, int blockAlignment, bool forBuffer)
        {
            _blocks = new List<Block>();
            _api = api;
            _device = device;
            MemoryTypeIndex = memoryTypeIndex;
            ForBuffer = forBuffer;
            _blockAlignment = blockAlignment;
            _lock = new(LockRecursionPolicy.NoRecursion);
        }

        public unsafe MemoryAllocation Allocate(ulong size, ulong alignment, bool map)
        {
            // 在分配前尝试回收内存
            TryReclaimMemory();

            // Ensure we have a sane alignment value.
            if ((ulong)(int)alignment != alignment || (int)alignment <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(alignment), $"Invalid alignment 0x{alignment:X}.");
            }

            _lock.EnterReadLock();

            try
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var block = _blocks[i];

                    if (!block.IsDestroyed && block.Mapped == map && block.Size >= size)
                    {
                        ulong offset = block.Allocate(size, alignment);
                        if (offset != InvalidOffset)
                        {
                            return new MemoryAllocation(this, block, block.Memory, GetHostPointer(block, offset), offset, size);
                        }
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            // 如果分配失败，再次尝试回收内存并重试
            ForceReclaimMemory();
            
            _lock.EnterReadLock();
            try
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var block = _blocks[i];

                    if (!block.IsDestroyed && block.Mapped == map && block.Size >= size)
                    {
                        ulong offset = block.Allocate(size, alignment);
                        if (offset != InvalidOffset)
                        {
                            return new MemoryAllocation(this, block, block.Memory, GetHostPointer(block, offset), offset, size);
                        }
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            ulong blockAlignedSize = BitUtils.AlignUp(size, (ulong)_blockAlignment);

            var memoryAllocateInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = blockAlignedSize,
                MemoryTypeIndex = (uint)MemoryTypeIndex,
            };

            Result result = _api.AllocateMemory(_device, in memoryAllocateInfo, null, out var deviceMemory);
            if (result != Result.Success)
            {
                // 如果分配仍然失败，尝试更激进的回收
                AggressiveReclaimMemory();
                
                // 最后一次尝试分配
                result = _api.AllocateMemory(_device, in memoryAllocateInfo, null, out deviceMemory);
                if (result != Result.Success)
                {
                    // 如果还是失败，抛出具体错误
                    throw new VulkanException(result);
                }
            }

            IntPtr hostPointer = IntPtr.Zero;

            if (map)
            {
                void* pointer = null;
                _api.MapMemory(_device, deviceMemory, 0, blockAlignedSize, 0, ref pointer).ThrowOnError();
                hostPointer = (IntPtr)pointer;
            }

            var newBlock = new Block(deviceMemory, hostPointer, blockAlignedSize);

            InsertBlock(newBlock);

            ulong newBlockOffset = newBlock.Allocate(size, alignment);
            Debug.Assert(newBlockOffset != InvalidOffset);

            return new MemoryAllocation(this, newBlock, deviceMemory, GetHostPointer(newBlock, newBlockOffset), newBlockOffset, size);
        }

        private static IntPtr GetHostPointer(Block block, ulong offset)
        {
            if (block.HostPointer == IntPtr.Zero || block.IsDestroyed)
            {
                return IntPtr.Zero;
            }

            return (IntPtr)((nuint)block.HostPointer + offset);
        }

        public void Free(Block block, ulong offset, ulong size)
        {
            if (block.IsDestroyed)
                return;

            block.Free(offset, size);

            if (block.IsTotallyFree())
            {
                _lock.EnterWriteLock();

                try
                {
                    for (int i = 0; i < _blocks.Count; i++)
                    {
                        if (_blocks[i] == block)
                        {
                            _blocks.RemoveAt(i);
                            break;
                        }
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                block.Destroy(_api, _device);
            }
        }

        private void InsertBlock(Block block)
        {
            _lock.EnterWriteLock();

            try
            {
                int index = _blocks.BinarySearch(block);
                if (index < 0)
                {
                    index = ~index;
                }

                _blocks.Insert(index, block);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 尝试回收内存，基于使用率和时间阈值
        /// </summary>
        public void TryReclaimMemory()
        {
            _lock.EnterWriteLock();
            try
            {
                ulong currentTime = (ulong)Stopwatch.GetTimestamp();
                ReclaimInternal(currentTime, _reclaimThreshold);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 强制回收内存，使用更激进的策略
        /// </summary>
        public void ForceReclaimMemory()
        {
            _lock.EnterWriteLock();
            try
            {
                ulong currentTime = (ulong)Stopwatch.GetTimestamp();
                // 使用更短的超时时间来强制回收完全空闲的块
                ReclaimInternal(currentTime, 1.0f, (ulong)(Stopwatch.Frequency * 2)); // 2秒超时
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 激进的内存回收，只回收完全空闲的块
        /// </summary>
        public void AggressiveReclaimMemory()
        {
            _lock.EnterWriteLock();
            try
            {
                ulong currentTime = (ulong)Stopwatch.GetTimestamp();
                // 立即回收所有完全空闲的块
                ReclaimInternal(currentTime, 1.0f, 0);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 手动触发内存整理
        /// </summary>
        public void ManualReclaim()
        {
            _lock.EnterWriteLock();
            try
            {
                ulong currentTime = (ulong)Stopwatch.GetTimestamp();
                // 手动回收时使用中等策略
                ReclaimInternal(currentTime, 1.0f, (ulong)(Stopwatch.Frequency * 5)); // 5秒超时
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 获取内存使用统计
        /// </summary>
        public (ulong totalSize, ulong usedSize, int blockCount) GetMemoryStats()
        {
            _lock.EnterReadLock();
            try
            {
                ulong total = 0;
                ulong used = 0;
                int count = 0;

                foreach (var block in _blocks)
                {
                    if (!block.IsDestroyed)
                    {
                        total += block.Size;
                        used += block.UsedSize;
                        count++;
                    }
                }

                return (total, used, count);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        private void ReclaimInternal(ulong currentTime, float threshold, ulong? timeout = null)
        {
            timeout ??= _reclaimTimeout;

            for (int i = _blocks.Count - 1; i >= 0; i--)
            {
                var block = _blocks[i];
                if (!block.IsDestroyed && block.ShouldReclaim(threshold, currentTime, timeout.Value))
                {
                    _blocks.RemoveAt(i);
                    block.Destroy(_api, _device);
                }
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                _blocks[i].Destroy(_api, _device);
            }
            _blocks.Clear();
        }
    }
}
