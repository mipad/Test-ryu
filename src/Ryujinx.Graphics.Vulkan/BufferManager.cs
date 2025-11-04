using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;  // 添加这行
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

        private const MemoryPropertyFlags DefaultBufferMemoryNoCacheFlags =
            MemoryPropertyFlags.HostVisibleBit |
            MemoryPropertyFlags.HostCoherentBit;

        private const MemoryPropertyFlags DeviceLocalBufferMemoryFlags =
            MemoryPropertyFlags.DeviceLocalBit;

        private const MemoryPropertyFlags DeviceLocalMappedBufferMemoryFlags =
            MemoryPropertyFlags.DeviceLocalBit |
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

        // 内存压力阈值和智能分配策略
        private const long MemoryPressureThreshold = 1024L * 1024 * 1024 * 2; // 2GB
        private const long CriticalMemoryThreshold = 1024L * 1024 * 1024 * 1; // 1GB
        private const long LargeBufferThreshold = 100 * 1024 * 1024; // 100MB以上的缓冲区视为大缓冲区

        // 性能统计
        private int _totalCreationAttempts = 0;
        private int _successfulCreations = 0;
        private int _fallbackCreations = 0;

        public BufferManager(VulkanRenderer gd, Device device)
        {
            _device = device;
            _buffers = new IdList<BufferHolder>();
            StagingBuffer = new StagingBuffer(gd, this);

            HostImportedBufferMemoryRequirements = GetHostImportedUsageRequirements(gd);
            
            Logger.Info?.Print(LogClass.Gpu, "BufferManager initialized with smart memory management");
        }

        // 智能内存检查方法
        private MemoryCheckResult CheckMemoryPressure(VulkanRenderer gd, int requestedSize, BufferAllocationType requestedType)
        {
            long totalAllocated = BufferHolder.GetTotalAllocatedMemory();
            long availableEstimate = BufferHolder.GetAvailableMemoryEstimate(gd);
            int failureCount = BufferHolder.GetAllocationFailureCount();
            
            // 计算内存压力等级
            MemoryPressureLevel pressureLevel = MemoryPressureLevel.Low;
            long estimatedUsage = totalAllocated + requestedSize;
            
            if (estimatedUsage > availableEstimate)
            {
                pressureLevel = MemoryPressureLevel.Critical;
            }
            else if (estimatedUsage > availableEstimate * 0.8)
            {
                pressureLevel = MemoryPressureLevel.High;
            }
            else if (estimatedUsage > availableEstimate * 0.6)
            {
                pressureLevel = MemoryPressureLevel.Medium;
            }
            
            // 检查是否为大缓冲区
            bool isLargeBuffer = requestedSize > LargeBufferThreshold;
            
            // 根据失败次数调整策略
            bool recentFailures = failureCount > 5;
            
            Logger.Debug?.Print(LogClass.Gpu, 
                $"Memory Check - Allocated: 0x{totalAllocated:X}, " +
                $"Requested: 0x{requestedSize:X}, " +
                $"Available: 0x{availableEstimate:X}, " +
                $"Pressure: {pressureLevel}, " +
                $"Large: {isLargeBuffer}, " +
                $"Failures: {failureCount}");

            return new MemoryCheckResult
            {
                PressureLevel = pressureLevel,
                ShouldProceed = pressureLevel < MemoryPressureLevel.Critical,
                RecommendedType = GetRecommendedBufferType(requestedType, pressureLevel, isLargeBuffer, recentFailures),
                RequiresGarbageCollection = pressureLevel >= MemoryPressureLevel.High || recentFailures
            };
        }

        // 根据内存压力推荐缓冲区类型
        private BufferAllocationType GetRecommendedBufferType(BufferAllocationType requested, MemoryPressureLevel pressure, bool isLarge, bool recentFailures)
        {
            if (recentFailures)
            {
                // 如果有最近的失败，使用最保守的策略
                return BufferAllocationType.HostMappedNoCache;
            }
            
            switch (pressure)
            {
                case MemoryPressureLevel.Critical:
                    return BufferAllocationType.HostMappedNoCache;
                    
                case MemoryPressureLevel.High:
                    if (isLarge)
                        return BufferAllocationType.HostMapped;
                    else
                        return requested;
                    
                case MemoryPressureLevel.Medium:
                    if (isLarge && requested == BufferAllocationType.DeviceLocal)
                        return BufferAllocationType.HostMapped;
                    else
                        return requested;
                    
                case MemoryPressureLevel.Low:
                default:
                    return requested;
            }
        }

        // 尝试清理内存的方法
        private bool TryFreeMemory(VulkanRenderer gd, int requiredSize, bool aggressive = false)
        {
            Logger.Warning?.Print(LogClass.Gpu, 
                $"Attempting to free memory, required: 0x{requiredSize:X}, aggressive: {aggressive}");

            long memoryBefore = BufferHolder.GetTotalAllocatedMemory();
            
            if (aggressive)
            {
                // 激进模式：多次GC和延迟以充分清理
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect(2, GCCollectionMode.Forced, true);
                    GC.WaitForPendingFinalizers();
                    
                    // 给系统一些时间处理
                    if (i < 2) System.Threading.Thread.Sleep(50);  // 使用完全限定名
                }
            }
            else
            {
                // 普通模式：单次GC
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
            }
            
            long memoryAfter = BufferHolder.GetTotalAllocatedMemory();
            long freed = memoryBefore - memoryAfter;
            
            Logger.Info?.Print(LogClass.Gpu, 
                $"Memory cleanup freed 0x{freed:X} bytes " +
                $"(before: 0x{memoryBefore:X}, after: 0x{memoryAfter:X})");

            // 检查是否释放了足够的内存
            long currentAvailable = BufferHolder.GetAvailableMemoryEstimate(gd);
            bool success = currentAvailable >= requiredSize;
            
            if (success)
            {
                Logger.Info?.Print(LogClass.Gpu, "Memory cleanup successful");
            }
            else
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory cleanup insufficient. Available: 0x{currentAvailable:X}, Required: 0x{requiredSize:X}");
            }
            
            return success;
        }

        // 性能统计方法
        public void LogPerformanceStats()
        {
            double successRate = _totalCreationAttempts > 0 ? 
                (double)_successfulCreations / _totalCreationAttempts * 100 : 0;
                
            Logger.Info?.Print(LogClass.Gpu, 
                $"Buffer Creation Stats: " +
                $"Attempts: {_totalCreationAttempts}, " +
                $"Success: {_successfulCreations} ({successRate:F1}%), " +
                $"Fallbacks: {_fallbackCreations}, " +
                $"Current Buffers: {BufferCount}");
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
            _successfulCreations++;

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
            _successfulCreations++;

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
            _totalCreationAttempts++;
            
            // 智能内存压力检查
            var memoryCheck = CheckMemoryPressure(gd, size, baseType);
            
            if (memoryCheck.RequiresGarbageCollection)
            {
                Logger.Info?.Print(LogClass.Gpu, "Memory pressure detected, performing garbage collection...");
                TryFreeMemory(gd, size, memoryCheck.PressureLevel == MemoryPressureLevel.Critical);
            }
            
            // 使用推荐的缓冲区类型
            BufferAllocationType recommendedType = memoryCheck.RecommendedType;
            
            if (!memoryCheck.ShouldProceed)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Memory pressure too high, aborting buffer creation. Size: 0x{size:X}, Type: {baseType}");
                holder = null;
                return BufferHandle.Null;
            }

            // 使用回退策略创建缓冲区
            BufferAllocationType actualType;
            holder = BufferHolder.CreateWithFallback(gd, _device, size, recommendedType, out actualType);
            
            if (holder != null)
            {
                if (actualType != recommendedType)
                {
                    _fallbackCreations++;
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Buffer created with fallback type: {actualType} (requested: {recommendedType})");
                }
                else
                {
                    _successfulCreations++;
                }

                if (forceMirrors)
                {
                    holder.UseMirrors();
                }

                BufferCount++;

                ulong handle64 = (uint)_buffers.Add(holder);

                // 定期记录性能统计
                if (_totalCreationAttempts % 100 == 0)
                {
                    LogPerformanceStats();
                }

                return Unsafe.As<ulong, BufferHandle>(ref handle64);
            }

            Logger.Error?.Print(LogClass.Gpu, 
                $"Failed to create buffer with size 0x{size:X} after all fallback attempts");
            BufferHolder.RecordAllocationFailure();
            return BufferHandle.Null;
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
                // 创建临时缓冲区前的内存检查
                var memoryCheck = CheckMemoryPressure(gd, size, BufferAllocationType.HostMapped);
                
                if (memoryCheck.RequiresGarbageCollection)
                {
                    TryFreeMemory(gd, size, false);
                }
                
                if (!memoryCheck.ShouldProceed)
                {
                    Logger.Error?.Print(LogClass.Gpu, 
                        $"Cannot create temporary buffer due to memory pressure. Size: 0x{size:X}");
                    return default;
                }

                BufferHandle handle = CreateWithHandle(gd, size, out BufferHolder holder);

                if (handle == BufferHandle.Null)
                {
                    return default;
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

            try
            {
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

                    try
                    {
                        allocation = gd.MemoryAllocator.AllocateDeviceMemory(requirements, allocateFlags, true);
                    }
                    catch (VulkanException ex)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"Memory allocation failed for type {type}: {ex.Message}");
                        allocation = default;
                    }
                }
                while (allocation.Memory.Handle == 0 && (--type != fallbackType));

                if (allocation.Memory.Handle == 0UL)
                {
                    gd.Api.DestroyBuffer(_device, buffer, null);
                    return default;
                }

                gd.Api.BindBufferMemory(_device, buffer, allocation.Memory, allocation.Offset);

                return (buffer, allocation, type);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Buffer creation failed for size 0x{size:X} with type {type}: {ex.Message}");
                return default;
            }
        }

        public BufferHolder Create(
            VulkanRenderer gd,
            int size,
            bool forConditionalRendering = false,
            bool sparseCompatible = false,
            BufferAllocationType baseType = BufferAllocationType.HostMapped)
        {
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

            Logger.Error?.Print(LogClass.Gpu, $"Failed to create buffer with size 0x{size:X} and type \"{baseType}\".");
            return null;
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
                
                BufferCount--;
                Logger.Debug?.Print(LogClass.Gpu, $"Buffer deleted, remaining buffers: {BufferCount}");
            }
        }

        private bool TryGetBuffer(BufferHandle handle, out BufferHolder holder)
        {
            return _buffers.TryGetValue((int)Unsafe.As<BufferHandle, ulong>(ref handle), out holder);
        }

        // 获取内存统计信息
        public (long TotalAllocated, int BufferCount, int Failures, int Fallbacks) GetMemoryStatistics()
        {
            return (BufferHolder.GetTotalAllocatedMemory(), BufferCount, 
                   BufferHolder.GetAllocationFailureCount(), _fallbackCreations);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 记录最终统计
                LogPerformanceStats();
                
                StagingBuffer.Dispose();

                foreach (BufferHolder buffer in _buffers)
                {
                    buffer.Dispose();
                }

                _buffers.Clear();
                
                Logger.Info?.Print(LogClass.Gpu, "BufferManager disposed, all buffers released");
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }

    // 内存检查结果结构
    internal struct MemoryCheckResult
    {
        public MemoryPressureLevel PressureLevel { get; set; }
        public bool ShouldProceed { get; set; }
        public BufferAllocationType RecommendedType { get; set; }
        public bool RequiresGarbageCollection { get; set; }
    }

    // 内存压力等级
    internal enum MemoryPressureLevel
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3
    }
}
