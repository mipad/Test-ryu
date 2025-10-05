using Ryujinx.Common;
using Silk.NET.Vulkan;
using Ryujinx.Common.Logging;
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
                _freeRanges = new List<Range>
                {
                    new Range(0, size),
                };
            }

            public ulong Allocate(ulong size, ulong alignment)
            {
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

                        return alignedOffset;
                    }
                }

                return InvalidOffset;
            }

            public void Free(ulong offset, ulong size)
            {
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
                if (_freeRanges.Count == 1 && _freeRanges[0].Size == Size)
                {
                    Debug.Assert(_freeRanges[0].Offset == 0);
                    return true;
                }

                return false;
            }

            // 添加 FreeMemory 属性计算空闲内存大小
            public ulong FreeMemory
            {
                get
                {
                    ulong free = 0;
                    foreach (var range in _freeRanges)
                    {
                        free += range.Size;
                    }
                    return free;
                }
            }

            // 添加 LargestFreeBlock 属性
            public ulong LargestFreeBlock
            {
                get
                {
                    ulong largest = 0;
                    foreach (var range in _freeRanges)
                    {
                        if (range.Size > largest)
                        {
                            largest = range.Size;
                        }
                    }
                    return largest;
                }
            }

            public int CompareTo(Block other)
            {
                return Size.CompareTo(other.Size);
            }

            public unsafe void Destroy(Vk api, Device device)
            {
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
            }
        }

        private readonly List<Block> _blocks;

        private readonly Vk _api;
        private readonly Device _device;

        public int MemoryTypeIndex { get; }
        public bool ForBuffer { get; }

        private readonly int _blockAlignment;

        private readonly ReaderWriterLockSlim _lock;

        // 添加内存统计
        private ulong _totalAllocated;
        private ulong _totalFreed;

        public MemoryAllocatorBlockList(Vk api, Device device, int memoryTypeIndex, int blockAlignment, bool forBuffer)
        {
            _blocks = new List<Block>();
            _api = api;
            _device = device;
            MemoryTypeIndex = memoryTypeIndex;
            ForBuffer = forBuffer;
            _blockAlignment = blockAlignment;
            _lock = new(LockRecursionPolicy.NoRecursion);
            _totalAllocated = 0;
            _totalFreed = 0;
        }

        public unsafe MemoryAllocation Allocate(ulong size, ulong alignment, bool map)
        {
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

                    if (block.Mapped == map && block.Size >= size)
                    {
                        ulong offset = block.Allocate(size, alignment);
                        if (offset != InvalidOffset)
                        {
                            _totalAllocated += size;
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

            // 在Android上，如果请求的大小超过256MB，尝试使用更小的块
            if (blockAlignedSize > 256 * 1024 * 1024)
            {
                // 对于大内存分配，尝试分段
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Large memory allocation requested: 0x{blockAlignedSize:X}. This may fail on Android.");
                
                // 尝试使用256MB作为最大块大小
                ulong maxReasonableSize = 256 * 1024 * 1024;
                if (blockAlignedSize > maxReasonableSize)
                {
                    blockAlignedSize = maxReasonableSize;
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Reducing allocation size to 0x{blockAlignedSize:X} for Android compatibility");
                }
            }

            var memoryAllocateInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = blockAlignedSize,
                MemoryTypeIndex = (uint)MemoryTypeIndex,
            };

            try
            {
                _api.AllocateMemory(_device, in memoryAllocateInfo, null, out var deviceMemory).ThrowOnError();

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

                _totalAllocated += size;
                return new MemoryAllocation(this, newBlock, deviceMemory, GetHostPointer(newBlock, newBlockOffset), newBlockOffset, size);
            }
            catch (VulkanException ex)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Failed to allocate memory: Size=0x{blockAlignedSize:X}, TypeIndex={MemoryTypeIndex}, Error={ex.Message}");
                throw;
            }
        }

        private static IntPtr GetHostPointer(Block block, ulong offset)
        {
            if (block.HostPointer == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            return (IntPtr)((nuint)block.HostPointer + offset);
        }

        public void Free(Block block, ulong offset, ulong size)
        {
            block.Free(offset, size);
            _totalFreed += size;

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

        // 添加 GetFreeMemory 方法
        public ulong GetFreeMemory()
        {
            ulong freeMemory = 0;
            foreach (var block in _blocks)
            {
                freeMemory += block.FreeMemory;
            }
            return freeMemory;
        }

        // 添加 GetLargestFreeBlock 方法
        public ulong GetLargestFreeBlock()
        {
            ulong largest = 0;
            foreach (var block in _blocks)
            {
                ulong blockLargest = block.LargestFreeBlock;
                if (blockLargest > largest)
                {
                    largest = blockLargest;
                }
            }
            return largest;
        }

        // 添加内存统计方法
        public (ulong allocated, ulong freed, ulong currentUsage) GetMemoryStatistics()
        {
            ulong currentUsage = 0;
            foreach (var block in _blocks)
            {
                currentUsage += block.Size - block.FreeMemory;
            }
            return (_totalAllocated, _totalFreed, currentUsage);
        }

        public void Dispose()
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                _blocks[i].Destroy(_api, _device);
            }
        }
    }
}
