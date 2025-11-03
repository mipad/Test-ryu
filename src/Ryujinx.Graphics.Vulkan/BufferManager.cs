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
            Logger.Debug?.Print(LogClass.Gpu, $"创建缓冲区请求: 大小=0x{size:X} ({size / 1024 / 1024}MB), 类型={baseType}");

            const int LargeBufferThreshold = 256 * 1024 * 1024; // 256MB
            bool isLargeBuffer = size >= LargeBufferThreshold;
            
            if (isLargeBuffer)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"尝试创建大缓冲区: 大小=0x{size:X} ({size / 1024 / 1024}MB), 类型={baseType}");
            }

            // 首先尝试正常创建
            try
            {
                holder = Create(gd, size, forConditionalRendering: false, sparseCompatible, baseType);
                if (holder != null)
                {
                    if (isLargeBuffer)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"大缓冲区创建成功: 大小=0x{size:X}");
                    }

                    BufferCount++;
                    ulong handle64 = (uint)_buffers.Add(holder);
                    return Unsafe.As<ulong, BufferHandle>(ref handle64);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"缓冲区创建异常: {ex.Message}");
            }

            // 如果正常创建失败，尝试回退策略
            Logger.Warning?.Print(LogClass.Gpu, $"缓冲区创建失败，尝试回退类型: 大小=0x{size:X}, 原始类型={baseType}");
            
            // 定义更智能的回退顺序
            BufferAllocationType[] fallbackTypes = GetFallbackTypes(baseType, isLargeBuffer);

            foreach (BufferAllocationType fallbackType in fallbackTypes)
            {
                if (fallbackType == baseType) continue;
                    
                Logger.Warning?.Print(LogClass.Gpu, $"尝试回退类型: {fallbackType}");
                try
                {
                    holder = Create(gd, size, forConditionalRendering: false, sparseCompatible, fallbackType);
                    if (holder != null)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"回退类型成功: {fallbackType}");
                        BufferCount++;
                        ulong handle64 = (uint)_buffers.Add(holder);
                        return Unsafe.As<ulong, BufferHandle>(ref handle64);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"回退类型 {fallbackType} 失败: {ex.Message}");
                }
            }

            // 最后尝试虚拟内存回退
            Logger.Warning?.Print(LogClass.Gpu, $"所有GPU内存分配失败，使用虚拟内存回退: 大小=0x{size:X} ({size / 1024 / 1024}MB)");
            holder = CreateVirtualMemoryBuffer(gd, size);
            if (holder != null)
            {
                BufferCount++;
                ulong handle64 = (uint)_buffers.Add(holder);
                return Unsafe.As<ulong, BufferHandle>(ref handle64);
            }

            Logger.Error?.Print(LogClass.Gpu, $"无法创建缓冲区: 大小=0x{size:X} ({size / 1024 / 1024}MB)");
            
            holder = null;
            return BufferHandle.Null;
        }

        /// <summary>
        /// 根据缓冲区大小和原始类型获取回退顺序
        /// </summary>
        private static BufferAllocationType[] GetFallbackTypes(BufferAllocationType baseType, bool isLargeBuffer)
        {
            if (isLargeBuffer)
            {
                // 对于大缓冲区，优先尝试主机映射内存
                return new[]
                {
                    BufferAllocationType.HostMapped,
                    BufferAllocationType.HostMappedNoCache,
                    BufferAllocationType.DeviceLocalMapped,
                    BufferAllocationType.DeviceLocal
                };
            }
            else
            {
                // 对于小缓冲区，优先尝试主机映射内存
                return new[]
                {
                    BufferAllocationType.HostMappedNoCache,
                    BufferAllocationType.DeviceLocalMapped,
                    BufferAllocationType.DeviceLocal,
                    BufferAllocationType.HostMapped
                };
            }
        }

        /// <summary>
        /// 创建基于系统虚拟内存的缓冲区作为回退方案
        /// </summary>
        private BufferHolder CreateVirtualMemoryBuffer(VulkanRenderer gd, int size)
        {
            Logger.Warning?.Print(LogClass.Gpu, $"创建虚拟内存缓冲区: 大小=0x{size:X} ({size / 1024 / 1024}MB)");

            IntPtr virtualMemory = IntPtr.Zero;
            BufferHolder holder = null;
            bool success = false;

            try
            {
                // 分配系统虚拟内存
                virtualMemory = Marshal.AllocHGlobal(size);
                if (virtualMemory == IntPtr.Zero)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"系统虚拟内存分配失败: 大小=0x{size:X}");
                    return null;
                }

                Logger.Warning?.Print(LogClass.Gpu, $"系统虚拟内存分配成功: 地址=0x{virtualMemory:X}, 大小=0x{size:X}");

                // 清零初始化内存
                unsafe
                {
                    byte* ptr = (byte*)virtualMemory;
                    for (int i = 0; i < size; i++)
                    {
                        ptr[i] = 0;
                    }
                }

                // 创建一个特殊的BufferHolder，它使用虚拟内存而不是Vulkan缓冲区
                holder = new BufferHolder(gd, _device, virtualMemory, size);

                success = true;
                
                Logger.Warning?.Print(LogClass.Gpu, $"虚拟内存缓冲区创建成功: 大小=0x{size:X}");
                return holder;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"创建虚拟内存缓冲区时发生异常: {ex.Message}");
                return null;
            }
            finally
            {
                if (!success && virtualMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(virtualMemory);
                    Logger.Warning?.Print(LogClass.Gpu, $"已释放虚拟内存: 地址=0x{virtualMemory:X}");
                }
            }
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

                // If an allocation with this memory type fails, fall back to the previous one.
                try
                {
                    allocation = gd.MemoryAllocator.AllocateDeviceMemory(requirements, allocateFlags, true);
                }
                catch (VulkanException)
                {
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

        /// <summary>
        /// 复制缓冲区数据，支持虚拟内存缓冲区
        /// </summary>
        public void CopyBuffer(MemoryManager memoryManager, ulong srcVa, ulong dstVa, ulong size)
        {
            if (size == 0) return;

            try
            {
                MultiRange srcRange = TranslateAndCreateMultiBuffersPhysicalOnly(memoryManager, srcVa, size, BufferStage.Copy);
                MultiRange dstRange = TranslateAndCreateMultiBuffersPhysicalOnly(memoryManager, dstVa, size, BufferStage.Copy);

                // 检查是否有虚拟内存缓冲区参与
                bool hasVirtualBuffer = HasVirtualBufferInRange(srcRange) || HasVirtualBufferInRange(dstRange);

                if (hasVirtualBuffer)
                {
                    // 使用支持虚拟内存的复制方法
                    CopyBufferWithVirtualSupport(memoryManager, srcRange, dstRange, srcVa, dstVa, size);
                    return;
                }

                // 原有的复制逻辑
                if (srcRange.Count == 1 && dstRange.Count == 1)
                {
                    CopyBufferSingleRange(memoryManager, srcRange.GetSubRange(0).Address, dstRange.GetSubRange(0).Address, size);
                }
                else
                {
                    ulong copiedSize = 0;
                    ulong srcOffset = 0;
                    ulong dstOffset = 0;
                    int srcRangeIndex = 0;
                    int dstRangeIndex = 0;

                    while (copiedSize < size)
                    {
                        if (srcRange.GetSubRange(srcRangeIndex).Size == srcOffset)
                        {
                            srcRangeIndex++;
                            srcOffset = 0;
                        }

                        if (dstRange.GetSubRange(dstRangeIndex).Size == dstOffset)
                        {
                            dstRangeIndex++;
                            dstOffset = 0;
                        }

                        MemoryRange srcSubRange = srcRange.GetSubRange(srcRangeIndex);
                        MemoryRange dstSubRange = dstRange.GetSubRange(dstRangeIndex);

                        ulong srcSize = srcSubRange.Size - srcOffset;
                        ulong dstSize = dstSubRange.Size - dstOffset;
                        ulong copySize = Math.Min(srcSize, dstSize);

                        CopyBufferSingleRange(memoryManager, srcSubRange.Address + srcOffset, dstSubRange.Address + dstOffset, copySize);

                        srcOffset += copySize;
                        dstOffset += copySize;
                        copiedSize += copySize;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"缓冲区复制失败: {ex.Message}, 源VA=0x{srcVa:X}, 目标VA=0x{dstVa:X}, 大小=0x{size:X}");
            }
        }

        /// <summary>
        /// 检查范围中是否包含虚拟内存缓冲区
        /// </summary>
        private bool HasVirtualBufferInRange(MultiRange range)
        {
            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange subRange = range.GetSubRange(i);
                if (subRange.Address != MemoryManager.PteUnmapped)
                {
                    var buffer = GetBuffer(subRange.Address, subRange.Size, BufferStage.Copy, false);
                    if (buffer != null && buffer.IsVirtualMemoryBuffer)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 支持虚拟内存缓冲区的复制方法
        /// </summary>
        private void CopyBufferWithVirtualSupport(MemoryManager memoryManager, MultiRange srcRange, MultiRange dstRange, ulong srcVa, ulong dstVa, ulong size)
        {
            Logger.Warning?.Print(LogClass.Gpu, $"使用虚拟内存缓冲区复制: 源VA=0x{srcVa:X}, 目标VA=0x{dstVa:X}, 大小=0x{size:X}");

            try
            {
                // 对于虚拟内存缓冲区，使用CPU端复制
                if (srcRange.Count == 1 && dstRange.Count == 1)
                {
                    MemoryRange srcSubRange = srcRange.GetSubRange(0);
                    MemoryRange dstSubRange = dstRange.GetSubRange(0);

                    if (srcSubRange.Address != MemoryManager.PteUnmapped && dstSubRange.Address != MemoryManager.PteUnmapped)
                    {
                        // 获取源数据
                        var srcData = memoryManager.GetSpan(srcSubRange.Address, (int)size);
                        
                        // 写入目标数据
                        memoryManager.Write(dstSubRange.Address, srcData);
                        
                        Logger.Debug?.Print(LogClass.Gpu, $"虚拟内存复制完成: 大小=0x{size:X}");
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"虚拟内存复制跳过: 源或目标地址未映射");
                    }
                }
                else
                {
                    // 多范围复制 - 使用逐块复制
                    ulong copiedSize = 0;
                    ulong srcOffset = 0;
                    ulong dstOffset = 0;
                    int srcRangeIndex = 0;
                    int dstRangeIndex = 0;

                    while (copiedSize < size)
                    {
                        if (srcRange.GetSubRange(srcRangeIndex).Size == srcOffset)
                        {
                            srcRangeIndex++;
                            srcOffset = 0;
                        }

                        if (dstRange.GetSubRange(dstRangeIndex).Size == dstOffset)
                        {
                            dstRangeIndex++;
                            dstOffset = 0;
                        }

                        MemoryRange srcSubRange = srcRange.GetSubRange(srcRangeIndex);
                        MemoryRange dstSubRange = dstRange.GetSubRange(dstRangeIndex);

                        ulong srcSize = srcSubRange.Size - srcOffset;
                        ulong dstSize = dstSubRange.Size - dstOffset;
                        ulong copySize = Math.Min(Math.Min(srcSize, dstSize), size - copiedSize);

                        if (srcSubRange.Address != MemoryManager.PteUnmapped && dstSubRange.Address != MemoryManager.PteUnmapped)
                        {
                            // 获取源数据
                            var srcData = memoryManager.GetSpan(srcSubRange.Address + srcOffset, (int)copySize);
                            
                            // 写入目标数据
                            memoryManager.Write(dstSubRange.Address + dstOffset, srcData);
                        }

                        srcOffset += copySize;
                        dstOffset += copySize;
                        copiedSize += copySize;
                    }
                    
                    Logger.Debug?.Print(LogClass.Gpu, $"虚拟内存多范围复制完成: 大小=0x{size:X}, 范围数=源{srcRange.Count}/目标{dstRange.Count}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"虚拟内存缓冲区复制失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 复制单个范围的缓冲区数据
        /// </summary>
        private void CopyBufferSingleRange(MemoryManager memoryManager, ulong srcAddress, ulong dstAddress, ulong size)
        {
            // 检查源和目标缓冲区是否为虚拟内存缓冲区
            var srcBuffer = GetBuffer(srcAddress, size, BufferStage.Copy, false);
            var dstBuffer = GetBuffer(dstAddress, size, BufferStage.Copy, false);

            if (srcBuffer == null || dstBuffer == null)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"复制操作跳过: 无法获取缓冲区, 源地址=0x{srcAddress:X}, 目标地址=0x{dstAddress:X}, 大小=0x{size:X}");
                return;
            }

            // 如果任一缓冲区是虚拟内存缓冲区，使用支持虚拟内存的复制
            if (srcBuffer.IsVirtualMemoryBuffer || dstBuffer.IsVirtualMemoryBuffer)
            {
                // 使用BufferHolder的CopyData方法
                var cbs = _gd.CommandBufferPool.Rent();
                try
                {
                    BufferHolder.CopyData(_gd, cbs, srcBuffer, dstBuffer, 
                        (int)(srcAddress - srcBuffer.Address), 
                        (int)(dstAddress - dstBuffer.Address), 
                        (int)size);
                }
                finally
                {
                    cbs.Dispose();
                }
                return;
            }

            // 原有的GPU复制逻辑
            int srcOffset = (int)(srcAddress - srcBuffer.Address);
            int dstOffset = (int)(dstAddress - dstBuffer.Address);

            var srcBufferHandle = srcBuffer.GetBuffer();
            var dstBufferHandle = dstBuffer.GetBuffer();

            if (srcBufferHandle == null || dstBufferHandle == null)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"复制操作跳过: 无法获取缓冲区句柄");
                return;
            }

            var cbsCopy = _gd.CommandBufferPool.Rent();
            try
            {
                _gd.Renderer.Pipeline.CopyBuffer(
                    srcBufferHandle.Get(cbsCopy, srcOffset, (int)size).Value,
                    dstBufferHandle.Get(cbsCopy, dstOffset, (int)size, true).Value,
                    srcOffset,
                    dstOffset,
                    (int)size);
            }
            finally
            {
                cbsCopy.Dispose();
            }

            if (srcBuffer.IsModified(srcAddress, size))
            {
                dstBuffer.SignalModified(dstAddress, size, BufferStage.Copy);
            }
            else
            {
                dstBuffer.ClearModified(dstAddress, size);
                memoryManager.Physical.WriteTrackedResource(dstAddress, memoryManager.Physical.GetSpan(srcAddress, (int)size), ResourceKind.Buffer);
            }

            dstBuffer.CopyToDependantVirtualBuffers(dstAddress, size);
        }

        /// <summary>
        /// Clears a buffer at a given address with the specified value.
        /// </summary>
        /// <remarks>
        /// Both the address and size must be aligned to 4 bytes.
        /// </remarks>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">GPU virtual address of the region to clear</param>
        /// <param name="size">Number of bytes to clear</param>
        /// <param name="value">Value to be written into the buffer</param>
        public void ClearBuffer(MemoryManager memoryManager, ulong gpuVa, ulong size, uint value)
        {
            MultiRange range = TranslateAndCreateMultiBuffersPhysicalOnly(memoryManager, gpuVa, size, BufferStage.Copy);

            for (int index = 0; index < range.Count; index++)
            {
                MemoryRange subRange = range.GetSubRange(index);
                Buffer buffer = GetBuffer(subRange.Address, subRange.Size, BufferStage.Copy);

                int offset = (int)(subRange.Address - buffer.Address);

                _context.Renderer.Pipeline.ClearBuffer(buffer.Handle, offset, (int)subRange.Size, value);

                memoryManager.Physical.FillTrackedResource(subRange.Address, subRange.Size, value, ResourceKind.Buffer);

                buffer.CopyToDependantVirtualBuffers(subRange.Address, subRange.Size);
            }
        }

        /// <summary>
        /// Gets a buffer sub-range starting at a given memory address, aligned to the next page boundary.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer sub-range starting at the given memory address</returns>
        public BufferRange GetBufferRangeAligned(MultiRange range, BufferStage stage, bool write = false)
        {
            if (range.Count > 1)
            {
                return GetBuffer(range, stage, write).GetRange(range);
            }
            else
            {
                MemoryRange subRange = range.GetSubRange(0);
                return GetBuffer(subRange.Address, subRange.Size, stage, write).GetRangeAligned(subRange.Address, subRange.Size, write);
            }
        }

        /// <summary>
        /// Gets a buffer sub-range for a given memory range.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer sub-range for the given range</returns>
        public BufferRange GetBufferRange(MultiRange range, BufferStage stage, bool write = false)
        {
            if (range.Count > 1)
            {
                return GetBuffer(range, stage, write).GetRange(range);
            }
            else
            {
                MemoryRange subRange = range.GetSubRange(0);
                return GetBuffer(subRange.Address, subRange.Size, stage, write).GetRange(subRange.Address, subRange.Size, write);
            }
        }

        /// <summary>
        /// Gets a buffer for a given memory range.
        /// A buffer overlapping with the specified range is assumed to already exist on the cache.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer where the range is fully contained</returns>
        private MultiRangeBuffer GetBuffer(MultiRange range, BufferStage stage, bool write = false)
        {
            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange subRange = range.GetSubRange(i);

                if (subRange.Address == MemoryManager.PteUnmapped)
                {
                    continue;
                }

                Buffer subBuffer = _buffers.FindFirstOverlap(subRange.Address, subRange.Size);
                if (subBuffer == null)
                {
                    throw new InvalidOperationException(
                        $"No buffer found for sub-range address 0x{subRange.Address:X8}, size 0x{subRange.Size:X8}");
                }

                subBuffer.SynchronizeMemory(subRange.Address, subRange.Size);

                if (write)
                {
                    subBuffer.SignalModified(subRange.Address, subRange.Size, stage);
                }
            }

            MultiRangeBuffer[] overlaps = new MultiRangeBuffer[10];

            int overlapCount = _multiRangeBuffers.FindOverlaps(range, ref overlaps);

            MultiRangeBuffer buffer = null;

            for (int i = 0; i < overlapCount; i++)
            {
                if (overlaps[i].Range.Contains(range))
                {
                    buffer = overlaps[i];
                    break;
                }
            }

            if (write && buffer != null && !_context.Capabilities.SupportsSparseBuffer)
            {
                buffer.AddModifiedRegion(range, ++_virtualModifiedSequenceNumber);
            }

            return buffer;
        }

        /// <summary>
        /// Gets a buffer for a given memory range.
        /// A buffer overlapping with the specified range is assumed to already exist on the cache.
        /// </summary>
        /// <param name="address">Start address of the memory range</param>
        /// <param name="size">Size in bytes of the memory range</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer where the range is fully contained</returns>
        private Buffer GetBuffer(ulong address, ulong size, BufferStage stage, bool write = false)
        {
            Buffer buffer = null;

            if (size != 0)
            {
                buffer = _buffers.FindFirstOverlap(address, size);
                
                if (buffer == null)
                {
                    CreateBuffer(address, size, stage);
                    buffer = _buffers.FindFirstOverlap(address, size);
                    
                    if (buffer == null)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"Failed to create buffer for address 0x{address:X}, size 0x{size:X}");
                        throw new InvalidOperationException($"No buffer found for address 0x{address:X}, size 0x{size:X}");
                    }
                }
                
                if (buffer != null)
                {
                    buffer.CopyFromDependantVirtualBuffers();
                    buffer.SynchronizeMemory(address, size);
                    
                    if (write)
                    {
                        buffer.SignalModified(address, size, stage);
                    }
                }
            }
            else
            {
                buffer = _buffers.FindFirstOverlap(address, 1);
                if (buffer == null)
                {
                    throw new InvalidOperationException($"No buffer found for address 0x{address:X}");
                }
            }

            return buffer;
        }

        /// <summary>
        /// Performs guest to host memory synchronization of a given memory range.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        public void SynchronizeBufferRange(MultiRange range)
        {
            if (range.Count == 1)
            {
                MemoryRange subRange = range.GetSubRange(0);
                SynchronizeBufferRange(subRange.Address, subRange.Size, copyBackVirtual: true);
            }
            else
            {
                for (int index = 0; index < range.Count; index++)
                {
                    MemoryRange subRange = range.GetSubRange(index);
                    SynchronizeBufferRange(subRange.Address, subRange.Size, copyBackVirtual: false);
                }
            }
        }

        /// <summary>
        /// Performs guest to host memory synchronization of a given memory range.
        /// </summary>
        /// <param name="address">Start address of the memory range</param>
        /// <param name="size">Size in bytes of the memory range</param>
        /// <param name="copyBackVirtual">Whether virtual buffers that uses this buffer as backing memory should have its data copied back if modified</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SynchronizeBufferRange(ulong address, ulong size, bool copyBackVirtual)
        {
            if (size != 0)
            {
                Buffer buffer = _buffers.FindFirstOverlap(address, size);

                // 优化：添加空引用检查
                if (buffer != null)
                {
                    if (copyBackVirtual)
                    {
                        buffer.CopyFromDependantVirtualBuffers();
                    }

                    buffer.SynchronizeMemory(address, size);
                }
            }
        }

        /// <summary>
        /// Signal that the given buffer's handle has changed,
        /// forcing rebind and any overlapping multi-range buffers to be recreated.
        /// </summary>
        /// <param name="buffer">The buffer that has changed handle</param>
        public void BufferBackingChanged(Buffer buffer)
        {
            // 优化：使用阈值控制事件触发频率
            if (++_modifyEventCount >= ModifyEventThreshold)
            {
                NotifyBuffersModified?.Invoke();
                _modifyEventCount = 0;
            }

            RecreateMultiRangeBuffers(buffer.Address, buffer.Size);
        }

        /// <summary>
        /// Prune any invalid entries from a quick access dictionary.
        /// </summary>
        /// <param name="dictionary">Dictionary to prune</param>
        /// <param name="toDelete">List used to track entries to delete</param>
        private static void Prune(Dictionary<ulong, BufferCacheEntry> dictionary, ref List<ulong> toDelete)
        {
            foreach (var entry in dictionary)
            {
                if (entry.Value.UnmappedSequence != entry.Value.Buffer.UnmappedSequence)
                {
                    (toDelete ??= new()).Add(entry.Key);
                }
            }

            if (toDelete != null)
            {
                foreach (ulong entry in toDelete)
                {
                    dictionary.Remove(entry);
                }
            }
        }

        /// <summary>
        /// Prune any invalid entries from the quick access dictionaries.
        /// </summary>
        private void Prune()
        {
            List<ulong> toDelete = null;

            Prune(_dirtyCache, ref toDelete);
            toDelete = null; // Reset for next dictionary

            Prune(_modifiedCache, ref toDelete);

            _pruneCaches = false;
        }

        /// <summary>
        /// Queues a prune of invalid entries the next time a dictionary cache is accessed.
        /// </summary>
        public void QueuePrune()
        {
            _pruneCaches = true;
        }

        public void Delete(BufferHandle handle)
        {
            if (TryGetBuffer(handle, out var holder))
            {
                holder.Dispose();
                _buffers.Remove((int)Unsafe.As<BufferHandle, ulong>(ref handle));
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