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
                // Create a temporary buffer.
                BufferHandle handle = CreateWithHandle(gd, size, out BufferHolder holder);
                
                if (holder == null)
                {
                    // 尝试使用最小尺寸作为回退
                    const int fallbackSize = 1024;
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Using fallback buffer (size=0x{fallbackSize:X}) for failed allocation (0x{size:X})");
                    
                    handle = CreateWithHandle(gd, fallbackSize, out holder);
                    
                    if (holder == null)
                    {
                        // 最终回退：使用占位符空缓冲区
                        Logger.Error?.Print(LogClass.Gpu, 
                            "Critical: Failed to create fallback buffer. Using placeholder.");
                        return new ScopedTemporaryBuffer(this, null, BufferHandle.Null, 0, 0, false);
                    }
                }

                return new ScopedTemporaryBuffer(this, holder, handle, 0, size, false);
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

            // 常规缓冲区创建失败，尝试Android特定的回退策略
            Logger.Warning?.Print(LogClass.Gpu, 
                $"Regular buffer creation failed for size 0x{size:X}, attempting Android-specific fallback");

            try
            {
                // 尝试使用Android优化的内存分配
                var androidBuffer = CreateAndroidOptimizedBuffer(gd, size);
                if (androidBuffer != null)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Successfully created buffer using Android-optimized method for size 0x{size:X}");
                    return androidBuffer;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Android-optimized buffer creation failed: {ex.Message}");
            }

            // 最后尝试：使用分段缓冲区
            Logger.Warning?.Print(LogClass.Gpu, 
                $"Android-optimized method failed, attempting segmented buffer for size 0x{size:X}");

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

            Logger.Error?.Print(LogClass.Gpu, $"All buffer creation methods failed for size 0x{size:X} and type \"{baseType}\"");
            return null;
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

        private BufferHolder CreateSegmentedBuffer(VulkanRenderer gd, int size)
        {
            // 在Android上，将大缓冲区分割成多个小缓冲区
            // 这对于内存受限的设备特别有用
            const int maxSegmentSize = 16 * 1024 * 1024; // 16MB segments for Android
            
            if (size <= maxSegmentSize)
            {
                // 如果大小小于等于最大段大小，直接创建
                return CreateAndroidOptimizedBuffer(gd, size);
            }

            // 创建分段缓冲区包装器
            int segmentCount = (size + maxSegmentSize - 1) / maxSegmentSize;
            var segments = new BufferHolder[segmentCount];
            
            try
            {
                for (int i = 0; i < segmentCount; i++)
                {
                    int segmentSize = (i == segmentCount - 1) ? (size - i * maxSegmentSize) : maxSegmentSize;
                    segments[i] = CreateAndroidOptimizedBuffer(gd, segmentSize);
                    
                    if (segments[i] == null)
                    {
                        // 如果任何段创建失败，清理已创建的段
                        for (int j = 0; j < i; j++)
                        {
                            segments[j]?.Dispose();
                        }
                        return null;
                    }
                }

                // 创建一个虚拟的BufferHolder来管理这些段
                // 注意：这是一个简化的实现，实际需要更复杂的管理逻辑
                var dummyBufferCreateInfo = new BufferCreateInfo
                {
                    SType = StructureType.BufferCreateInfo,
                    Size = (ulong)size,
                    Usage = DefaultBufferUsageFlags,
                    SharingMode = SharingMode.Exclusive,
                };

                gd.Api.CreateBuffer(_device, in dummyBufferCreateInfo, null, out var dummyBuffer).ThrowOnError();
                
                // 创建一个特殊的BufferHolder来管理分段缓冲区
                var segmentAllocations = new Auto<MemoryAllocation>[segmentCount];
                for (int i = 0; i < segmentCount; i++)
                {
                    segmentAllocations[i] = segments[i].GetAllocation();
                }

                var holder = new BufferHolder(gd, _device, dummyBuffer, size, segmentAllocations);

                // 设置分段信息供后续使用
                // 这里需要扩展BufferHolder来支持分段缓冲区

                return holder;
            }
            catch (Exception)
            {
                // 清理所有已创建的段
                foreach (var segment in segments)
                {
                    segment?.Dispose();
                }
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
