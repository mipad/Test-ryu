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
        private readonly VulkanRenderer _gd;

        private readonly IdList<BufferHolder> _buffers;

        public int BufferCount { get; private set; }

        public StagingBuffer StagingBuffer { get; }

        public MemoryRequirements HostImportedBufferMemoryRequirements { get; }

        public BufferManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
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
                
                // 对于大缓冲区，直接使用虚拟内存，避免GPU内存分配失败
                holder = CreateVirtualMemoryBuffer(gd, size);
                if (holder != null)
                {
                    BufferCount++;
                    ulong handle64 = (uint)_buffers.Add(holder);
                    Logger.Warning?.Print(LogClass.Gpu, $"大缓冲区使用虚拟内存创建成功: 大小=0x{size:X}");
                    return Unsafe.As<ulong, BufferHandle>(ref handle64);
                }
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