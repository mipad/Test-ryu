using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common.Logging;
using System.Linq;

namespace Ryujinx.Graphics.Vulkan
{
    class MemoryAllocator : IDisposable
    {
        private const ulong MaxDeviceMemoryUsageEstimate = 16UL * 1024 * 1024 * 1024;
        private const ulong LargeAllocationThreshold = 256 * 1024 * 1024; // 256MB
        private const ulong ChunkSize = 64 * 1024 * 1024; // 64MB 分块大小

        private readonly Vk _api;
        private readonly VulkanPhysicalDevice _physicalDevice;
        private readonly Device _device;
        private readonly List<MemoryAllocatorBlockList> _blockLists;
        private readonly int _blockAlignment;
        private readonly ReaderWriterLockSlim _lock;
        
        // 内存压力回调
        public event Action<ulong, ulong> OnMemoryPressure;

        // 分块分配管理器
        private class ChunkedAllocation : IDisposable
        {
            public List<MemoryAllocation> Chunks { get; }
            public ulong TotalSize { get; }

            public ChunkedAllocation(List<MemoryAllocation> chunks, ulong totalSize)
            {
                Chunks = chunks;
                TotalSize = totalSize;
            }

            public void Dispose()
            {
                foreach (var chunk in Chunks)
                {
                    chunk.Dispose();
                }
                Chunks.Clear();
            }
        }

        private readonly Dictionary<IntPtr, ChunkedAllocation> _chunkedAllocations = new();
        private long _chunkedAllocationCounter = 1;

        public MemoryAllocator(Vk api, VulkanPhysicalDevice physicalDevice, Device device)
        {
            _api = api;
            _physicalDevice = physicalDevice;
            _device = device;
            _blockLists = new List<MemoryAllocatorBlockList>();
            _blockAlignment = (int)Math.Min(int.MaxValue, MaxDeviceMemoryUsageEstimate / _physicalDevice.PhysicalDeviceProperties.Limits.MaxMemoryAllocationCount);
            _lock = new(LockRecursionPolicy.NoRecursion);
        }

        public MemoryAllocation AllocateDeviceMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags = 0,
            bool isBuffer = false)
        {
            int memoryTypeIndex = FindSuitableMemoryTypeIndex(requirements.MemoryTypeBits, flags);
            if (memoryTypeIndex < 0)
            {
                return default;
            }

            bool map = flags.HasFlag(MemoryPropertyFlags.HostVisibleBit);
            
            // 检查可用显存
            if (!CanAllocate(requirements.Size, memoryTypeIndex))
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Insufficient memory! Requested: {FormatSize(requirements.Size)}, " +
                    $"Available: {FormatSize(GetAvailableMemory(memoryTypeIndex))}");
                
                // 触发内存压力回调
                OnMemoryPressure?.Invoke(requirements.Size, 0);
                Thread.Sleep(100); // 给释放操作一点时间
            }

            // 大内存分配使用分块策略
            if (requirements.Size > LargeAllocationThreshold)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Allocating large buffer: {FormatSize(requirements.Size)} " +
                    $"(Type: {memoryTypeIndex}, Flags: {flags})");
                
                return AllocateChunked(memoryTypeIndex, requirements.Size, requirements.Alignment, map, isBuffer);
            }

            // 普通分配
            return AllocateWithRetry(memoryTypeIndex, requirements.Size, requirements.Alignment, map, isBuffer);
        }

        private MemoryAllocation AllocateChunked(
            int memoryTypeIndex, 
            ulong size, 
            ulong alignment, 
            bool map, 
            bool isBuffer)
        {
            // 计算需要多少块
            ulong chunks = (size + ChunkSize - 1)
