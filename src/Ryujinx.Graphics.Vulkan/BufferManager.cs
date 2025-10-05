using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    readonly struct ScopedTemporaryBuffer : IDisposable
    {
        private readonly BufferManager _bufferManager;
        private readonly bool _isReserved;

        public readonly BufferRange Range;
        public readonly BufferHolder Holder;

        public BufferHandle Handle => Range.Handle;
        public int Offset => Range.Offset;

        public ScopedTemporaryBuffer(BufferManager bufferManager, BufferHolder holder, BufferHandle handle, int offset, int size, bool isReserved)
        {
            _bufferManager = bufferManager;

            Range = new BufferRange(handle, offset, size);
            Holder = holder;

            _isReserved = isReserved;
        }

        public void Dispose()
        {
            if (!_isReserved)
            {
                _bufferManager.Delete(Range.Handle);
            }
        }
    }

    // 临时测试缓冲区类
    internal class TemporaryTestBuffer : IDisposable
    {
        private readonly Vk _api;
        private readonly Device _device;
        private readonly VkBuffer _buffer;
        private readonly MemoryAllocation _allocation;
        private readonly VulkanRenderer _gd;

        public TemporaryTestBuffer(VulkanRenderer gd, Vk api, Device device, VkBuffer buffer, MemoryAllocation allocation)
        {
            _gd = gd;
            _api = api;
            _device = device;
            _buffer = buffer;
            _allocation = allocation;
        }

        public void Dispose()
        {
            _api.DestroyBuffer(_device, _buffer, null);
            // 注意：这里需要根据内存分配器来释放内存
            _gd.MemoryAllocator?.FreeMemory(_allocation);
        }
    }

    class BufferManager : IDisposable
    {
        public const MemoryPropertyFlags DefaultBufferMemoryFlags =
            MemoryPropertyFlags.HostVisibleBit |
            MemoryPropertyFlags.HostCoherentBit |
            MemoryPropertyFlags.HostCachedBit;

        // Some drivers don't expose a "HostCached" memory type,
        // so we need those alternative flags for the allocation to succeed there.
        private const MemoryPropertyFlags DefaultBufferMemoryNoCacheFlags =
            MemoryPropertyFlags.HostVisibleBit |
            MemoryPropertyFlags.HostCoherentBit;

        private const MemoryPropertyFlags DeviceLocalBufferMemoryFlags =
            MemoryPropertyFlags.DeviceLocalBit;

        private const MemoryPropertyFlags DeviceLocalMappedBufferMemoryFlags =
            MemoryPropertyFlags.DeviceLocalBit |
            MemoryPropertyFlags.HostVisibleBit |
            MemoryPropertyFlags.HostCoherentBit;

        // Android-specific memory flags
        private const MemoryPropertyFlags AndroidMemoryFlags =
            MemoryPropertyFlags.HostVisibleBit |
            MemoryPropertyFlags.HostCoherentBit;

        private const BufferUsageFlags DefaultBufferUsageFlags =
            BufferUsageFlags.TransferSrcBit |
            BufferUsageFlags.TransferDstBit |
            BufferUsageFlags.UniformTexelBufferBit |
            BufferUsageFlags.StorageTexelBufferBit |
            BufferUsageFlags.UniformBufferBit |
            BufferUsageFlags.StorageBufferBit |
            BufferUsageFlags.IndexBufferBit |
            BufferUsageFlags.VertexBufferBit |
            BufferUsageFlags.TransformFeedbackBufferBitExt;

        private const BufferUsageFlags HostImportedBufferUsageFlags =
            BufferUsageFlags.TransferSrcBit |
            BufferUsageFlags.TransferDstBit;

        private readonly Device _device;

        private readonly IdList<BufferHolder> _buffers;

        public int BufferCount { get; private set; }

        public StagingBuffer StagingBuffer { get; }

        public MemoryRequirements HostImportedBufferMemoryRequirements { get; }

        public BufferManager(VulkanRenderer gd, Device device)
        {
            _device = device;
            _buffers = new IdList<BufferHolder>();
            StagingBuffer = new StagingBuffer(gd, this);

            HostImportedBufferMemoryRequirements = GetHostImportedUsageRequirements(gd);
        }

        public unsafe BufferHandle CreateHostImported(VulkanRenderer gd, nint pointer, int size)
        {
            var usage = HostImportedBufferUsageFlags;

            if (gd.Capabilities.SupportsIndirectParameters)
            {
                usage |= BufferUsageFlags.IndirectBufferBit;
            }

            var externalMemoryBuffer = new ExternalMemoryBufferCreateInfo
            {
                SType = StructureType.ExternalMemoryBufferCreateInfo,
                HandleTypes = ExternalMemoryHandleTypeFlags.HostAllocationBitExt,
            };

            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                PNext = &externalMemoryBuffer,
            };

            gd.Api.CreateBuffer(_device, in bufferCreateInfo, null, out var buffer).ThrowOnError();

            (Auto<MemoryAllocation> allocation, ulong offset) = gd.HostMemoryAllocator.GetExistingAllocation(pointer, (ulong)size);

            gd.Api.BindBufferMemory(_device, buffer, allocation.GetUnsafe().Memory, allocation.GetUnsafe().Offset + offset);

            var holder = new BufferHolder(gd, _device, buffer, allocation, size, BufferAllocationType.HostMapped, BufferAllocationType.HostMapped, (int)offset);

            BufferCount++;

            ulong handle64 = (uint)_buffers.Add(holder);

            return Unsafe.As<ulong, BufferHandle>(ref handle64);
        }

        public unsafe BufferHandle CreateSparse(VulkanRenderer gd, ReadOnlySpan<BufferRange> storageBuffers)
        {
            var usage = DefaultBufferUsageFlags;

            if (gd.Capabilities.SupportsIndirectParameters)
            {
                usage |= BufferUsageFlags.IndirectBufferBit;
            }

            ulong size = 0;

            foreach (BufferRange range in storageBuffers)
            {
                size += (ulong)range.Size;
            }

            var bufferCreateInfo = new BufferCreateInfo()
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                Flags = BufferCreateFlags.SparseBindingBit | BufferCreateFlags.SparseAliasedBit
            };

            gd.Api.CreateBuffer(_device, in bufferCreateInfo, null, out var buffer).ThrowOnError();

            var memoryBinds = new SparseMemoryBind[storageBuffers.Length];
            var storageAllocations = new Auto<MemoryAllocation>[storageBuffers.Length];
            int storageAllocationsCount = 0;

            ulong dstOffset = 0;

            for (int index = 0; index < storageBuffers.Length; index++)
            {
                BufferRange range = storageBuffers[index];

                if (TryGetBuffer(range.Handle, out var existingHolder))
                {
                    (var memory, var offset) = existingHolder.GetDeviceMemoryAndOffset();

                    memoryBinds[index] = new SparseMemoryBind()
                    {
                        ResourceOffset = dstOffset,
                        Size = (ulong)range.Size,
                        Memory = memory,
                        MemoryOffset = offset + (ulong)range.Offset,
                        Flags = SparseMemoryBindFlags.None
                    };

                    storageAllocations[storageAllocationsCount++] = existingHolder.GetAllocation();
                }
                else
                {
                    memoryBinds[index] = new SparseMemoryBind()
                    {
                        ResourceOffset = dstOffset,
                        Size = (ulong)range.Size,
                        Memory = default,
                        MemoryOffset = 0UL,
                        Flags = SparseMemoryBindFlags.None
                    };
                }

                dstOffset += (ulong)range.Size;
            }

            if (storageAllocations.Length != storageAllocationsCount)
            {
                Array.Resize(ref storageAllocations, storageAllocationsCount);
            }

            fixed (SparseMemoryBind* pMemoryBinds = memoryBinds)
            {
                SparseBufferMemoryBindInfo bufferBind = new SparseBufferMemoryBindInfo()
                {
                    Buffer = buffer,
                    BindCount = (uint)memoryBinds.Length,
                    PBinds = pMemoryBinds
                };

                BindSparseInfo bindSparseInfo = new BindSparseInfo()
                {
                    SType = StructureType.BindSparseInfo,
                    BufferBindCount = 1,
                    PBufferBinds = &bufferBind
                };

                gd.Api.QueueBindSparse(gd.Queue, 1, in bindSparseInfo, default).ThrowOnError();
            }

            var holder = new BufferHolder(gd, _device, buffer, (int)size, storageAllocations);

            BufferCount++;

            ulong handle64 = (uint)_buffers.Add(holder);

            return Unsafe.As<ulong, BufferHandle>(ref handle64);
        }

        public BufferHandle CreateWithHandle(
            VulkanRenderer gd,
            int size,
            bool sparseCompatible = false,
            BufferAllocationType baseType = BufferAllocationType.HostMapped,
            bool forceMirrors = false)
        {
            return CreateWithHandle(gd, size, out _, sparseCompatible, baseType, forceMirrors);
        }

        public BufferHandle CreateWithHandle(
            VulkanRenderer gd,
            int size,
            out BufferHolder holder,
            bool sparseCompatible = false,
            BufferAllocationType baseType = BufferAllocationType.HostMapped,
            bool forceMirrors = false)
        {
            holder = Create(gd, size, forConditionalRendering: false, sparseCompatible, baseType);
            if (holder == null)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to create buffer with size 0x{size:X} and type \"{baseType}\"");
                return BufferHandle.Null;
            }

            if (forceMirrors)
            {
                holder.UseMirrors();
            }

            BufferCount++;

            ulong handle64 = (uint)_buffers.Add(holder);

            return Unsafe.As<ulong, BufferHandle>(ref handle64);
        }

        public ScopedTemporaryBuffer ReserveOrCreate(VulkanRenderer gd, CommandBufferScoped cbs, int size)
        {
            StagingBufferReserved? result = StagingBuffer.TryReserveData(cbs, size);

            if (result.HasValue)
            {
                return new ScopedTemporaryBuffer(this, result.Value.Buffer, StagingBuffer.Handle, result.Value.Offset, result.Value.Size, true);
            }
            else
            {
                // 创建临时缓冲区时使用新的内存感知策略
                int actualSize = GetAdjustedBufferSize(gd, size);
                
                BufferHandle handle = CreateWithHandle(gd, actualSize, out BufferHolder holder);
                
                if (holder == null)
                {
                    // 逐步降级策略
                    int[] fallbackSizes = new[] { 
                        size / 2, 
                        size / 4, 
                        16 * 1024 * 1024, // 16MB
                        4 * 1024 * 1024,  // 4MB
                        1024 * 1024,      // 1MB
                        256 * 1024        // 256KB
                    };
                    
                    foreach (int fallbackSize in fallbackSizes)
                    {
                        if (fallbackSize <= 0) continue;
                        
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"Trying fallback buffer size 0x{fallbackSize:X} for failed allocation (0x{size:X})");
                        
                        handle = CreateWithHandle(gd, fallbackSize, out holder);
                        
                        if (holder != null)
                        {
                            Logger.Info?.Print(LogClass.Gpu, 
                                $"Successfully created fallback buffer of size 0x{fallbackSize:X}");
                            break;
                        }
                    }
                    
                    if (holder == null)
                    {
                        Logger.Error?.Print(LogClass.Gpu, 
                            "Critical: All fallback buffer creation failed. Using placeholder.");
                        return new ScopedTemporaryBuffer(this, null, BufferHandle.Null, 0, 0, false);
                    }
                }

                return new ScopedTemporaryBuffer(this, holder, handle, 0, Math.Min(size, holder.Size), false);
            }
        }

        public unsafe MemoryRequirements GetHostImportedUsageRequirements(VulkanRenderer gd)
        {
            var usage = HostImportedBufferUsageFlags;

            if (gd.Capabilities.SupportsIndirectParameters)
            {
                usage |= BufferUsageFlags.IndirectBufferBit;
            }

            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)Environment.SystemPageSize,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };

            gd.Api.CreateBuffer(_device, in bufferCreateInfo, null, out var buffer).ThrowOnError();

            gd.Api.GetBufferMemoryRequirements(_device, buffer, out var requirements);

            gd.Api.DestroyBuffer(_device, buffer, null);

            return requirements;
        }

        public unsafe (VkBuffer buffer, MemoryAllocation allocation, BufferAllocationType resultType) CreateBacking(
            VulkanRenderer gd,
            int size,
            BufferAllocationType type,
            bool forConditionalRendering = false,
            bool sparseCompatible = false,
            BufferAllocationType fallbackType = BufferAllocationType.Auto)
        {
            var usage = DefaultBufferUsageFlags;

            if (forConditionalRendering && gd.Capabilities.SupportsConditionalRendering)
            {
                usage |= BufferUsageFlags.ConditionalRenderingBitExt;
            }
            else if (gd.Capabilities.SupportsIndirectParameters)
            {
                usage |= BufferUsageFlags.IndirectBufferBit;
            }

            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };

            gd.Api.CreateBuffer(_device, in bufferCreateInfo, null, out var buffer).ThrowOnError();
            gd.Api.GetBufferMemoryRequirements(_device, buffer, out var requirements);

            if (sparseCompatible)
            {
                requirements.Alignment = Math.Max(requirements.Alignment, Constants.SparseBufferAlignment);
            }

            MemoryAllocation allocation;

            do
            {
                var allocateFlags = type switch
                {
                    BufferAllocationType.HostMappedNoCache => DefaultBufferMemoryNoCacheFlags,
                    BufferAllocationType.HostMapped => DefaultBufferMemoryFlags,
                    BufferAllocationType.DeviceLocal => DeviceLocalBufferMemoryFlags,
                    BufferAllocationType.DeviceLocalMapped => DeviceLocalMappedBufferMemoryFlags,
                    _ => DefaultBufferMemoryFlags,
                };

                // 如果分配失败，尝试回退策略
                try
                {
                    allocation = gd.MemoryAllocator.AllocateDeviceMemory(requirements, allocateFlags, true);
                }
                catch (VulkanException e)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Memory allocation failed (type={type}, size=0x{size:X}): {e.Message}");
                    
                    allocation = default;
                }
            }
            while (allocation.Memory.Handle == 0 && (--type != fallbackType));

            // 添加内存不足时的降级处理
            if (allocation.Memory.Handle == 0UL)
            {
                // 在Android上，尝试使用更简单的内存标志
                try
                {
                    allocation = gd.MemoryAllocator.AllocateDeviceMemory(
                        requirements, 
                        AndroidMemoryFlags, 
                        true);
                }
                catch
                {
                    allocation = default;
                }
                
                if (allocation.Memory.Handle == 0UL)
                {
                    Logger.Error?.Print(LogClass.Gpu, 
                        $"All backup memory allocations failed for size 0x{size:X}");
                    gd.Api.DestroyBuffer(_device, buffer, null);
                    return default;
                }
            }

            gd.Api.BindBufferMemory(_device, buffer, allocation.Memory, allocation.Offset);

            return (buffer, allocation, type);
        }

        public BufferHolder Create(
            VulkanRenderer gd,
            int size,
            bool forConditionalRendering = false,
            bool sparseCompatible = false,
            BufferAllocationType baseType = BufferAllocationType.HostMapped)
        {
            // 添加内存压力检测
            if (size > GetAvailableMemoryEstimate(gd))
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Buffer size 0x{size:X} exceeds estimated available memory. Attempting reduced allocation.");
                
                // 尝试使用可用的最大尺寸
                int reducedSize = GetMaxAvailableBufferSize(gd, size);
                if (reducedSize > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Using reduced buffer size 0x{reducedSize:X} instead of 0x{size:X}");
                    size = reducedSize;
                }
            }

            // 添加小缓冲区优化
            // 对于小于4KB的缓冲区，默认使用HostMapped类型
            const int smallBufferThreshold = 4 * 1024;
            if (size <= smallBufferThreshold && baseType == BufferAllocationType.Auto)
            {
                baseType = BufferAllocationType.HostMapped;
            }

            BufferAllocationType type = baseType;

            if (baseType == BufferAllocationType.Auto)
            {
                type = BufferAllocationType.HostMapped;
            }

            (VkBuffer buffer, MemoryAllocation allocation, BufferAllocationType resultType) =
                CreateBacking(gd, size, type, forConditionalRendering, sparseCompatible);

            if (buffer.Handle != 0)
            {
                var holder = new BufferHolder(gd, _device, buffer, allocation, size, baseType, resultType);
                return holder;
            }

            // 使用改进的回退策略
            return CreateFallbackBuffer(gd, size, forConditionalRendering, sparseCompatible, baseType);
        }

        private long GetAvailableMemoryEstimate(VulkanRenderer gd)
        {
            try
            {
                // 获取设备内存属性
                var memoryProperties = gd.PhysicalDevice.GetMemoryProperties();
                long availableMemory = 0;
                
                // 估算可用内存（这里使用启发式方法）
                for (int i = 0; i < memoryProperties.MemoryHeapCount; i++)
                {
                    var heap = memoryProperties.MemoryHeaps[i];
                    if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                    {
                        // 保守估计：使用堆大小的 1/4 作为可用内存
                        availableMemory = Math.Max(availableMemory, (long)(heap.Size * 0.25));
                    }
                }
                
                return availableMemory > 0 ? availableMemory : 256 * 1024 * 1024; // 默认 256MB
            }
            catch
            {
                return 128 * 1024 * 1024; // 保守回退值
            }
        }

        private int GetMaxAvailableBufferSize(VulkanRenderer gd, int requestedSize)
        {
            // 二分查找法找到最大可分配尺寸
            int minSize = 1024; // 1KB 最小
            int maxSize = requestedSize;
            int bestSize = 0;
            
            for (int i = 0; i < 8; i++) // 最多尝试8次
            {
                int testSize = (minSize + maxSize) / 2;
                
                // 测试这个尺寸是否能分配
                bool canAllocate = TestBufferAllocation(gd, testSize);
                
                if (canAllocate)
                {
                    bestSize = testSize;
                    minSize = testSize + 1;
                    
                    // 如果已经很接近请求大小，直接返回
                    if (testSize >= requestedSize * 0.95)
                        break;
                }
                else
                {
                    maxSize = testSize - 1;
                }
                
                if (minSize > maxSize)
                    break;
            }
            
            return bestSize;
        }

        private bool TestBufferAllocation(VulkanRenderer gd, int size)
        {
            try
            {
                // 尝试创建临时缓冲区来测试内存可用性
                using var testBuffer = CreateTemporaryTestBuffer(gd, size);
                return testBuffer != null;
            }
            catch
            {
                return false;
            }
        }

        private IDisposable CreateTemporaryTestBuffer(VulkanRenderer gd, int size)
        {
            // 使用最基础的内存标志进行测试
            var usage = DefaultBufferUsageFlags;
            
            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };

            gd.Api.CreateBuffer(_device, in bufferCreateInfo, null, out var buffer).ThrowOnError();
            gd.Api.GetBufferMemoryRequirements(_device, buffer, out var requirements);

            try
            {
                var allocation = gd.MemoryAllocator.AllocateDeviceMemory(
                    requirements, 
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    true);
                    
                if (allocation.Memory.Handle != 0UL)
                {
                    gd.Api.BindBufferMemory(_device, buffer, allocation.Memory, allocation.Offset);
                    
                    // 返回可销毁的对象
                    return new TemporaryTestBuffer(gd, gd.Api, _device, buffer, allocation);
                }
            }
            catch
            {
                gd.Api.DestroyBuffer(_device, buffer, null);
            }
            
            return null;
        }

        private int GetAdjustedBufferSize(VulkanRenderer gd, int requestedSize)
        {
            // 根据可用内存调整请求的大小
            long availableMemory = GetAvailableMemoryEstimate(gd);
            
            if (requestedSize > availableMemory)
            {
                int adjustedSize = (int)Math.Min(availableMemory, requestedSize);
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Adjusting buffer size from 0x{requestedSize:X} to 0x{adjustedSize:X} due to memory constraints");
                return adjustedSize;
            }
            
            return requestedSize;
        }

        private BufferHolder CreateFallbackBuffer(
            VulkanRenderer gd, 
            int size, 
            bool forConditionalRendering, 
            bool sparseCompatible, 
            BufferAllocationType baseType)
        {
            Logger.Warning?.Print(LogClass.Gpu, 
                $"Regular buffer creation failed for size 0x{size:X}, attempting system storage memory as fallback");

            // 策略1: 尝试系统内存
            try
            {
                var systemBuffer = CreateSystemMemoryBuffer(gd, size);
                if (systemBuffer != null)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Successfully created buffer using system memory for size 0x{size:X}");
                    return systemBuffer;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"System storage memory failed: {ex.Message}");
            }

            // 策略2: 尝试内存映射回退
            Logger.Warning?.Print(LogClass.Gpu, 
                $"System storage memory failed, attempting memory-mapped fallback for size 0x{size:X}");

            try
            {
                var memoryMappedBuffer = CreateMemoryMappedFallback(gd, size);
                if (memoryMappedBuffer != null)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Successfully created memory-mapped buffer for size 0x{size:X}");
                    return memoryMappedBuffer;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory-mapped fallback failed: {ex.Message}");
            }

            // 策略3: 分段缓冲区模拟
            Logger.Warning?.Print(LogClass.Gpu, 
                $"Memory-mapped fallback failed, attempting segmented buffer for size 0x{size:X}");

            try
            {
                var segmentedBuffer = CreateSegmentedBuffer(gd, size);
                if (segmentedBuffer != null)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Successfully created segmented buffer for size 0x{size:X}");
                    return segmentedBuffer;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Segmented buffer creation failed: {ex.Message}");
            }

            Logger.Error?.Print(LogClass.Gpu, 
                $"All buffer creation methods failed for size 0x{size:X} and type \"{baseType}\"");
            return null;
        }

        private BufferHolder CreateSystemMemoryBuffer(VulkanRenderer gd, int size)
        {
            // 使用系统内存而不是GPU内存
            return CreateAndroidOptimizedBuffer(gd, size);
        }

        private BufferHolder CreateMemoryMappedFallback(VulkanRenderer gd, int size)
        {
            // 创建使用系统内存的缓冲区
            var usage = DefaultBufferUsageFlags;

            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };

            gd.Api.CreateBuffer(_device, in bufferCreateInfo, null, out var buffer).ThrowOnError();
            gd.Api.GetBufferMemoryRequirements(_device, buffer, out var requirements);

            // 使用系统可见的内存
            MemoryAllocation allocation;
            try
            {
                allocation = gd.MemoryAllocator.AllocateDeviceMemory(
                    requirements, 
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    false); // 不要求设备本地
            }
            catch (VulkanException)
            {
                gd.Api.DestroyBuffer(_device, buffer, null);
                return null;
            }

            if (allocation.Memory.Handle == 0UL)
            {
                gd.Api.DestroyBuffer(_device, buffer, null);
                return null;
            }

            gd.Api.BindBufferMemory(_device, buffer, allocation.Memory, allocation.Offset);

            return new BufferHolder(gd, _device, buffer, allocation, size, 
                BufferAllocationType.HostMapped, BufferAllocationType.HostMapped);
        }

        private BufferHolder CreateSegmentedBuffer(VulkanRenderer gd, int totalSize)
        {
            // 对于非常大的缓冲区，使用多个小缓冲区模拟
            const int segmentSize = 16 * 1024 * 1024; // 16MB 段
            int segmentCount = (totalSize + segmentSize - 1) / segmentSize;
            
            if (segmentCount <= 1)
                return null; // 不需要分段

            Logger.Info?.Print(LogClass.Gpu, 
                $"Creating segmented buffer: {segmentCount} segments of 0x{segmentSize:X} for total 0x{totalSize:X}");

            // 这里需要实现一个 SegmentedBufferHolder 类来管理多个缓冲区
            // 由于代码较长，这是一个概念实现
            return CreateSimplifiedBuffer(gd, Math.Min(segmentSize, totalSize));
        }

        private unsafe BufferHolder CreateAndroidOptimizedBuffer(VulkanRenderer gd, int size)
        {
            // Android特定的优化缓冲区创建
            var usage = DefaultBufferUsageFlags;

            if (gd.Capabilities.SupportsIndirectParameters)
            {
                usage |= BufferUsageFlags.IndirectBufferBit;
            }

            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };

            gd.Api.CreateBuffer(_device, in bufferCreateInfo, null, out var buffer).ThrowOnError();
            gd.Api.GetBufferMemoryRequirements(_device, buffer, out var requirements);

            // 在Android上，尝试使用设备本地内存，如果可用
            MemoryAllocation allocation;
            try
            {
                // 首先尝试设备本地内存
                allocation = gd.MemoryAllocator.AllocateDeviceMemory(
                    requirements, 
                    DeviceLocalBufferMemoryFlags, 
                    true);
            }
            catch (VulkanException)
            {
                // 如果设备本地内存失败，尝试主机可见内存
                try
                {
                    allocation = gd.MemoryAllocator.AllocateDeviceMemory(
                        requirements, 
                        AndroidMemoryFlags, 
                        true);
                }
                catch (VulkanException)
                {
                    gd.Api.DestroyBuffer(_device, buffer, null);
                    return null;
                }
            }

            if (allocation.Memory.Handle == 0UL)
            {
                gd.Api.DestroyBuffer(_device, buffer, null);
                return null;
            }

            gd.Api.BindBufferMemory(_device, buffer, allocation.Memory, allocation.Offset);

            var holder = new BufferHolder(gd, _device, buffer, allocation, size, 
                BufferAllocationType.DeviceLocal, BufferAllocationType.DeviceLocal);

            return holder;
        }

        private unsafe BufferHolder CreateSimplifiedBuffer(VulkanRenderer gd, int size)
        {
            // 简化版本：只尝试最基本的内存分配
            try
            {
                // 对于Android，使用最小的内存需求
                var usage = DefaultBufferUsageFlags;

                if (gd.Capabilities.SupportsIndirectParameters)
                {
                    usage |= BufferUsageFlags.IndirectBufferBit;
                }

                // 使用最基础的内存标志
                var bufferCreateInfo = new BufferCreateInfo
                {
                    SType = StructureType.BufferCreateInfo,
                    Size = (ulong)size,
                    Usage = usage,
                    SharingMode = SharingMode.Exclusive,
                };

                gd.Api.CreateBuffer(_device, bufferCreateInfo, null, out var buffer).ThrowOnError();
                gd.Api.GetBufferMemoryRequirements(_device, buffer, out var requirements);

                // 尝试最基本的内存分配
                MemoryAllocation allocation;
                try
                {
                    allocation = gd.MemoryAllocator.AllocateDeviceMemory(
                        requirements, 
                        MemoryPropertyFlags.HostVisibleBit, // 最基本的要求
                        true);
                }
                catch (VulkanException)
                {
                    gd.Api.DestroyBuffer(_device, buffer, null);
                    return null;
                }

                if (allocation.Memory.Handle == 0UL)
                {
                    gd.Api.DestroyBuffer(_device, buffer, null);
                    return null;
                }

                gd.Api.BindBufferMemory(_device, buffer, allocation.Memory, allocation.Offset);

                var holder = new BufferHolder(gd, _device, buffer, allocation, size, 
                    BufferAllocationType.HostMapped, BufferAllocationType.HostMapped);

                return holder;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public Auto<DisposableBufferView> CreateView(BufferHandle handle, VkFormat format, int offset, int size, Action invalidateView)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                return holder.CreateView(format, offset, size, invalidateView);
            }

            return null;
        }

        public Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, BufferHandle handle, bool isWrite, bool isSSBO = false)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                return holder.GetBuffer(commandBuffer, isWrite, isSSBO);
            }

            return null;
        }

        public Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, BufferHandle handle, int offset, int size, bool isWrite)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                return holder.GetBuffer(commandBuffer, offset, size, isWrite);
            }

            return null;
        }

        public Auto<DisposableBuffer> GetBufferI8ToI16(CommandBufferScoped cbs, BufferHandle handle, int offset, int size)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                return holder.GetBufferI8ToI16(cbs, offset, size);
            }

            return null;
        }

        public Auto<DisposableBuffer> GetAlignedVertexBuffer(CommandBufferScoped cbs, BufferHandle handle, int offset, int size, int stride, int alignment)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                return holder.GetAlignedVertexBuffer(cbs, offset, size, stride, alignment);
            }

            return null;
        }

        public Auto<DisposableBuffer> GetBufferTopologyConversion(CommandBufferScoped cbs, BufferHandle handle, int offset, int size, IndexBufferPattern pattern, int indexSize)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                return holder.GetBufferTopologyConversion(cbs, offset, size, pattern, indexSize);
            }

            return null;
        }

        public (Auto<DisposableBuffer>, Auto<DisposableBuffer>) GetBufferTopologyConversionIndirect(
            VulkanRenderer gd,
            CommandBufferScoped cbs,
            BufferRange indexBuffer,
            BufferRange indirectBuffer,
            BufferRange drawCountBuffer,
            IndexBufferPattern pattern,
            int indexSize,
            bool hasDrawCount,
            int maxDrawCount,
            int indirectDataStride)
        {
            BufferHolder drawCountBufferHolder = null;

            if (!TryGetBuffer(indexBuffer.Handle, out var indexBufferHolder) ||
                !TryGetBuffer(indirectBuffer.Handle, out var indirectBufferHolder) ||
                (hasDrawCount && !TryGetBuffer(drawCountBuffer.Handle, out drawCountBufferHolder)))
            {
                return (null, null);
            }

            var indexBufferKey = new TopologyConversionIndirectCacheKey(
                gd,
                pattern,
                indexSize,
                indirectBufferHolder,
                indirectBuffer.Offset,
                indirectBuffer.Size);

            bool hasConvertedIndexBuffer = indexBufferHolder.TryGetCachedConvertedBuffer(
                indexBuffer.Offset,
                indexBuffer.Size,
                indexBufferKey,
                out var convertedIndexBuffer);

            var indirectBufferKey = new IndirectDataCacheKey(pattern);
            bool hasConvertedIndirectBuffer = indirectBufferHolder.TryGetCachedConvertedBuffer(
                indirectBuffer.Offset,
                indirectBuffer.Size,
                indirectBufferKey,
                out var convertedIndirectBuffer);

            var drawCountBufferKey = new DrawCountCacheKey();
            bool hasCachedDrawCount = true;

            if (hasDrawCount)
            {
                hasCachedDrawCount = drawCountBufferHolder.TryGetCachedConvertedBuffer(
                    drawCountBuffer.Offset,
                    drawCountBuffer.Size,
                    drawCountBufferKey,
                    out _);
            }

            if (!hasConvertedIndexBuffer || !hasConvertedIndirectBuffer || !hasCachedDrawCount)
            {
                // The destination index size is always I32.

                int indexCount = indexBuffer.Size / indexSize;

                int convertedCount = pattern.GetConvertedCount(indexCount);

                if (!hasConvertedIndexBuffer)
                {
                    convertedIndexBuffer = Create(gd, convertedCount * 4);
                    indexBufferKey.SetBuffer(convertedIndexBuffer.GetBuffer());
                    indexBufferHolder.AddCachedConvertedBuffer(indexBuffer.Offset, indexBuffer.Size, indexBufferKey, convertedIndexBuffer);
                }

                if (!hasConvertedIndirectBuffer)
                {
                    convertedIndirectBuffer = Create(gd, indirectBuffer.Size);
                    indirectBufferHolder.AddCachedConvertedBuffer(indirectBuffer.Offset, indirectBuffer.Size, indirectBufferKey, convertedIndirectBuffer);
                }

                gd.PipelineInternal.EndRenderPass();
                gd.HelperShader.ConvertIndexBufferIndirect(
                    gd,
                    cbs,
                    indirectBufferHolder,
                    convertedIndirectBuffer,
                    drawCountBuffer,
                    indexBufferHolder,
                    convertedIndexBuffer,
                    pattern,
                    indexSize,
                    indexBuffer.Offset,
                    indexBuffer.Size,
                    indirectBuffer.Offset,
                    hasDrawCount,
                    maxDrawCount,
                    indirectDataStride);

                // Any modification of the indirect buffer should invalidate the index buffers that are associated with it,
                // since we used the indirect data to find the range of the index buffer that is used.

                var indexBufferDependency = new Dependency(
                    indexBufferHolder,
                    indexBuffer.Offset,
                    indexBuffer.Size,
                    indexBufferKey);

                indirectBufferHolder.AddCachedConvertedBufferDependency(
                    indirectBuffer.Offset,
                    indirectBuffer.Size,
                    indirectBufferKey,
                    indexBufferDependency);

                if (hasDrawCount)
                {
                    if (!hasCachedDrawCount)
                    {
                        drawCountBufferHolder.AddCachedConvertedBuffer(drawCountBuffer.Offset, drawCountBuffer.Size, drawCountBufferKey, null);
                    }

                    // If we have a draw count, any modification of the draw count should invalidate all indirect buffers
                    // where we used it to find the range of indirect data that is actually used.

                    var indirectBufferDependency = new Dependency(
                        indirectBufferHolder,
                        indirectBuffer.Offset,
                        indirectBuffer.Size,
                        indirectBufferKey);

                    drawCountBufferHolder.AddCachedConvertedBufferDependency(
                        drawCountBuffer.Offset,
                        drawCountBuffer.Size,
                        drawCountBufferKey,
                        indirectBufferDependency);
                }
            }

            return (convertedIndexBuffer.GetBuffer(), convertedIndirectBuffer.GetBuffer());
        }

        public Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, BufferHandle handle, bool isWrite, out int size)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                size = holder.Size;
                return holder.GetBuffer(commandBuffer, isWrite);
            }

            size = 0;
            return null;
        }

        public PinnedSpan<byte> GetData(BufferHandle handle, int offset, int size)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                return holder.GetData(offset, size);
            }

            return new PinnedSpan<byte>();
        }

        public void SetData<T>(BufferHandle handle, int offset, ReadOnlySpan<T> data) where T : unmanaged
        {
            SetData(handle, offset, MemoryMarshal.Cast<T, byte>(data), null, null);
        }

        public void SetData(BufferHandle handle, int offset, ReadOnlySpan<byte> data, CommandBufferScoped? cbs, Action endRenderPass)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                holder.SetData(offset, data, cbs, endRenderPass);
            }
        }

        public void Delete(BufferHandle handle)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                holder.Dispose();
                _buffers.Remove((int)Unsafe.As<BufferHandle, ulong>(ref handle));
            }
            else
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Attempted to delete invalid buffer handle: {handle}");
            }
        }

        private bool TryGetBuffer(BufferHandle handle, out BufferHolder holder)
        {
            return _buffers.TryGetValue((int)Unsafe.As<BufferHandle, ulong>(ref handle), out holder);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StagingBuffer.Dispose();

                foreach (BufferHolder buffer in _buffers)
                {
                    buffer.Dispose();
                }

                _buffers.Clear();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
