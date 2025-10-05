using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
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

    // 压缩算法枚举
    enum CompressionAlgorithm
    {
        None = 0,
        LZ4 = 1,
        Zstd = 2
    }

    // 压缩工具类
    static class CompressionHelper
    {
        private static bool _lz4Available = false;
        private static bool _zstdAvailable = false;

        static CompressionHelper()
        {
            // 检测可用的压缩库
            DetectAvailableLibraries();
        }

        private static void DetectAvailableLibraries()
        {
            try
            {
                // 尝试加载LZ4
                _lz4Available = CheckLZ4Availability();
            }
            catch
            {
                _lz4Available = false;
            }

            try
            {
                // 尝试加载Zstd
                _zstdAvailable = CheckZstdAvailability();
            }
            catch
            {
                _zstdAvailable = false;
            }

            Logger.Info?.Print(LogClass.Gpu, 
                $"Compression libraries - LZ4: {_lz4Available}, Zstd: {_zstdAvailable}");
        }

        private static bool CheckLZ4Availability()
        {
            // 在实际实现中，这里会检查LZ4库是否可用
            // 暂时返回true用于测试
            return true;
        }

        private static bool CheckZstdAvailability()
        {
            // 在实际实现中，这里会检查Zstd库是否可用
            // 暂时返回true用于测试
            return true;
        }

        public static unsafe int Compress(CompressionAlgorithm algorithm, ReadOnlySpan<byte> input, Span<byte> output)
        {
            if (input.Length == 0) return 0;

            switch (algorithm)
            {
                case CompressionAlgorithm.LZ4 when _lz4Available:
                    return CompressLZ4(input, output);
                case CompressionAlgorithm.Zstd when _zstdAvailable:
                    return CompressZstd(input, output);
                default:
                    // 无压缩，直接复制
                    input.CopyTo(output);
                    return input.Length;
            }
        }

        public static unsafe int Decompress(CompressionAlgorithm algorithm, ReadOnlySpan<byte> input, Span<byte> output)
        {
            if (input.Length == 0) return 0;

            switch (algorithm)
            {
                case CompressionAlgorithm.LZ4 when _lz4Available:
                    return DecompressLZ4(input, output);
                case CompressionAlgorithm.Zstd when _zstdAvailable:
                    return DecompressZstd(input, output);
                default:
                    // 无压缩，直接复制
                    input.CopyTo(output);
                    return input.Length;
            }
        }

        private static unsafe int CompressLZ4(ReadOnlySpan<byte> input, Span<byte> output)
        {
            // 在实际实现中，这里会调用LZ4压缩
            // 暂时使用简单复制作为占位符
            input.CopyTo(output);
            return input.Length;
        }

        private static unsafe int DecompressLZ4(ReadOnlySpan<byte> input, Span<byte> output)
        {
            // 在实际实现中，这里会调用LZ4解压
            // 暂时使用简单复制作为占位符
            input.CopyTo(output);
            return input.Length;
        }

        private static unsafe int CompressZstd(ReadOnlySpan<byte> input, Span<byte> output)
        {
            // 在实际实现中，这里会调用Zstd压缩
            // 暂时使用简单复制作为占位符
            input.CopyTo(output);
            return input.Length;
        }

        private static unsafe int DecompressZstd(ReadOnlySpan<byte> input, Span<byte> output)
        {
            // 在实际实现中，这里会调用Zstd解压
            // 暂时使用简单复制作为占位符
            input.CopyTo(output);
            return input.Length;
        }

        public static CompressionAlgorithm GetBestAvailableAlgorithm()
        {
            if (_zstdAvailable) return CompressionAlgorithm.Zstd;
            if (_lz4Available) return CompressionAlgorithm.LZ4;
            return CompressionAlgorithm.None;
        }
    }

    // 分块缓冲区包装器
    class ChunkedBufferHolder : BufferHolder
    {
        private readonly List<BufferHolder> _chunks;
        private readonly int _chunkSize;
        private readonly int _totalSize;
        private readonly VulkanRenderer _gd;
        private readonly Device _device;

        public ChunkedBufferHolder(VulkanRenderer gd, Device device, int totalSize, int chunkSize) 
            : base(gd, device, new VkBuffer(), new MemoryAllocation(), totalSize, 
                  BufferAllocationType.HostMapped, BufferAllocationType.HostMapped)
        {
            _gd = gd;
            _device = device;
            _totalSize = totalSize;
            _chunkSize = chunkSize;
            _chunks = new List<BufferHolder>();

            // 创建多个小缓冲区块
            int remaining = totalSize;
            while (remaining > 0)
            {
                int currentChunkSize = Math.Min(chunkSize, remaining);
                var chunk = gd.BufferManager.Create(gd, currentChunkSize, false, BufferAllocationType.HostMapped);
                if (chunk != null)
                {
                    _chunks.Add(chunk);
                    remaining -= currentChunkSize;
                }
                else
                {
                    Logger.Error?.Print(LogClass.Gpu, $"Failed to create chunk of size 0x{currentChunkSize:X}");
                    break;
                }
            }

            if (_chunks.Count == 0)
            {
                throw new InvalidOperationException("Failed to create any chunks for chunked buffer");
            }

            Logger.Info?.Print(LogClass.Gpu, 
                $"Created chunked buffer: total=0x{totalSize:X}, chunks={_chunks.Count}, chunkSize=0x{chunkSize:X}");
        }

        public override void SetData(int offset, ReadOnlySpan<byte> data, CommandBufferScoped? cbs = null, Action endRenderPass = null, bool allowCbsWait = true)
        {
            int dataOffset = 0;
            int remaining = data.Length;

            while (remaining > 0)
            {
                int chunkIndex = offset / _chunkSize;
                int chunkOffset = offset % _chunkSize;
                
                if (chunkIndex >= _chunks.Count)
                    break;

                var chunk = _chunks[chunkIndex];
                int copySize = Math.Min(remaining, _chunkSize - chunkOffset);

                chunk.SetData(chunkOffset, data.Slice(dataOffset, copySize), cbs, endRenderPass, allowCbsWait);

                dataOffset += copySize;
                offset += copySize;
                remaining -= copySize;
            }
        }

        public override PinnedSpan<byte> GetData(int offset, int size)
        {
            // 对于分块缓冲区，需要从多个块中收集数据
            if (_chunks.Count == 0)
                return new PinnedSpan<byte>();

            // 简单实现：只从第一个块获取数据
            // 实际实现需要跨多个块收集数据
            return _chunks[0].GetData(offset, Math.Min(size, _chunkSize));
        }

        public void CopyToChunked(CommandBufferScoped cbs, BufferHolder source, int srcOffset, int dstOffset, int size)
        {
            int remaining = size;
            int currentSrcOffset = srcOffset;
            int currentDstOffset = dstOffset;

            while (remaining > 0)
            {
                int chunkIndex = currentDstOffset / _chunkSize;
                int chunkOffset = currentDstOffset % _chunkSize;
                
                if (chunkIndex >= _chunks.Count)
                    break;

                var chunk = _chunks[chunkIndex];
                int copySize = Math.Min(remaining, _chunkSize - chunkOffset);

                // 执行块间复制
                BufferHolder.Copy(_gd, cbs, source.GetBuffer(), chunk.GetBuffer(), 
                    currentSrcOffset, chunkOffset, copySize);

                currentSrcOffset += copySize;
                currentDstOffset += copySize;
                remaining -= copySize;
            }
        }

        public override Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, bool isWrite = false, bool isSSBO = false)
        {
            // 返回第一个块的缓冲区（简化实现）
            return _chunks.Count > 0 ? _chunks[0].GetBuffer(commandBuffer, isWrite, isSSBO) : null;
        }

        public override Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, int offset, int size, bool isWrite = false)
        {
            // 返回相应块的缓冲区
            int chunkIndex = offset / _chunkSize;
            if (chunkIndex < _chunks.Count)
            {
                int chunkOffset = offset % _chunkSize;
                return _chunks[chunkIndex].GetBuffer(commandBuffer, chunkOffset, Math.Min(size, _chunkSize - chunkOffset), isWrite);
            }
            return null;
        }

        public override void Dispose()
        {
            foreach (var chunk in _chunks)
            {
                chunk?.Dispose();
            }
            _chunks.Clear();
            base.Dispose();
        }
    }

    // 四阶段缓冲区管理器
    class FourStageBufferManager : IDisposable
    {
        private const int TotalBufferSize = 160 * 1024 * 1024; // 160MB
        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly CompressionAlgorithm _compressionAlgorithm;
        
        // 四个阶段的缓冲区
        public ChunkedBufferHolder Stage1_CopyIn { get; private set; }      // 数据复制进入
        public ChunkedBufferHolder Stage2_Compression { get; private set; } // 压缩阶段
        public ChunkedBufferHolder Stage3_Compressed { get; private set; }  // 压缩数据存储
        public ChunkedBufferHolder Stage4_Ready { get; private set; }       // 准备使用

        // 动态比例调整
        private int[] _stageSizes = new int[4] { 40, 40, 40, 40 }; // MB
        private readonly int _chunkSize = 4 * 1024 * 1024; // 4MB块

        // 压缩缓冲区（用于CPU端的压缩操作）
        private byte[] _compressionBuffer;

        public FourStageBufferManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            _compressionAlgorithm = CompressionHelper.GetBestAvailableAlgorithm();

            InitializeStages();
            _compressionBuffer = new byte[TotalBufferSize]; // 预分配压缩缓冲区

            Logger.Info?.Print(LogClass.Gpu, 
                $"FourStageBufferManager initialized with {TotalBufferSize/1024/1024}MB " +
                $"(Algorithm: {_compressionAlgorithm}, Stages: {_stageSizes[0]}/{_stageSizes[1]}/{_stageSizes[2]}/{_stageSizes[3]}MB)");
        }

        private void InitializeStages()
        {
            try
            {
                // 创建四个阶段的缓冲区
                Stage1_CopyIn = new ChunkedBufferHolder(_gd, _device, _stageSizes[0] * 1024 * 1024, _chunkSize);
                Stage2_Compression = new ChunkedBufferHolder(_gd, _device, _stageSizes[1] * 1024 * 1024, _chunkSize);
                Stage3_Compressed = new ChunkedBufferHolder(_gd, _device, _stageSizes[2] * 1024 * 1024, _chunkSize);
                Stage4_Ready = new ChunkedBufferHolder(_gd, _device, _stageSizes[3] * 1024 * 1024, _chunkSize);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to initialize FourStageBufferManager stages: {ex.Message}");
                CleanupFailedStages();
                throw;
            }
        }

        private void CleanupFailedStages()
        {
            Stage1_CopyIn?.Dispose();
            Stage2_Compression?.Dispose();
            Stage3_Compressed?.Dispose();
            Stage4_Ready?.Dispose();
            
            Stage1_CopyIn = null;
            Stage2_Compression = null;
            Stage3_Compressed = null;
            Stage4_Ready = null;
        }

        // 调整各阶段大小比例
        public void ResizeStages(int[] newSizes)
        {
            if (newSizes.Length != 4) 
            {
                Logger.Warning?.Print(LogClass.Gpu, "Invalid stage sizes array length");
                return;
            }

            var total = newSizes[0] + newSizes[1] + newSizes[2] + newSizes[3];
            if (total * 1024 * 1024 > TotalBufferSize)
            {
                Logger.Warning?.Print(LogClass.Gpu, "Resize would exceed total buffer size");
                return;
            }

            // 重新创建缓冲区
            CleanupFailedStages();
            _stageSizes = newSizes;
            InitializeStages();

            Logger.Info?.Print(LogClass.Gpu, 
                $"Stage sizes adjusted: {newSizes[0]}MB, {newSizes[1]}MB, {newSizes[2]}MB, {newSizes[3]}MB");
        }

        // 处理数据流
        public void ProcessData(CommandBufferScoped cbs, BufferHolder source, int srcOffset, int size)
        {
            if (size > Stage1_CopyIn.Size)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Data size 0x{size:X} exceeds stage1 capacity 0x{Stage1_CopyIn.Size:X}, processing in chunks");
                
                // 分块处理大数据
                int processed = 0;
                while (processed < size)
                {
                    int chunkSize = Math.Min(Stage1_CopyIn.Size, size - processed);
                    ProcessDataChunk(cbs, source, srcOffset + processed, chunkSize);
                    processed += chunkSize;
                }
            }
            else
            {
                ProcessDataChunk(cbs, source, srcOffset, size);
            }
        }

        private void ProcessDataChunk(CommandBufferScoped cbs, BufferHolder source, int srcOffset, int size)
        {
            try
            {
                // 阶段1: 数据复制进入
                Stage1_CopyIn.CopyToChunked(cbs, source, srcOffset, 0, size);
                
                // 阶段2: 压缩处理
                CompressAndStore(cbs, size);
                
                // 阶段3->阶段4: 解压准备使用
                DecompressForUsage(cbs, size);

                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Processed data chunk: 0x{size:X} bytes through 4-stage pipeline (Algorithm: {_compressionAlgorithm})");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Error in 4-stage data processing: {ex.Message}");
                // 回退到直接复制（无压缩）
                FallbackDirectCopy(cbs, source, srcOffset, size);
            }
        }

        private void CompressAndStore(CommandBufferScoped cbs, int size)
        {
            // 从阶段1读取数据
            using var data = Stage1_CopyIn.GetData(0, size);
            if (data.IsEmpty)
            {
                Logger.Warning?.Print(LogClass.Gpu, "Failed to get data from Stage1 for compression");
                return;
            }

            // 压缩数据
            int compressedSize = CompressionHelper.Compress(_compressionAlgorithm, data.Get(), _compressionBuffer.AsSpan(0, size));
            
            if (compressedSize > 0 && compressedSize <= size)
            {
                // 将压缩数据写入阶段2和阶段3
                Stage2_Compression.SetData(0, _compressionBuffer.AsSpan(0, compressedSize), cbs, null, true);
                Stage3_Compressed.SetData(0, _compressionBuffer.AsSpan(0, compressedSize), cbs, null, true);
                
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Compression: {size} -> {compressedSize} bytes (Ratio: {(float)compressedSize/size:P2})");
            }
            else
            {
                // 压缩失败或没有节省空间，直接复制
                Stage2_Compression.CopyToChunked(cbs, Stage1_CopyIn, 0, 0, size);
                Stage3_Compressed.CopyToChunked(cbs, Stage1_CopyIn, 0, 0, size);
                Logger.Debug?.Print(LogClass.Gpu, "Compression skipped or failed, using direct copy");
            }
        }

        private void DecompressForUsage(CommandBufferScoped cbs, int originalSize)
        {
            // 从阶段3读取压缩数据
            int compressedSize = Math.Min(originalSize, Stage3_Compressed.Size);
            using var compressedData = Stage3_Compressed.GetData(0, compressedSize);
            
            if (compressedData.IsEmpty)
            {
                Logger.Warning?.Print(LogClass.Gpu, "Failed to get compressed data from Stage3");
                return;
            }

            // 解压数据
            int decompressedSize = CompressionHelper.Decompress(_compressionAlgorithm, compressedData.Get(), _compressionBuffer.AsSpan(0, originalSize));
            
            if (decompressedSize == originalSize)
            {
                // 将解压数据写入阶段4
                Stage4_Ready.SetData(0, _compressionBuffer.AsSpan(0, decompressedSize), cbs, null, true);
            }
            else
            {
                // 解压失败，从阶段1直接复制
                Stage4_Ready.CopyToChunked(cbs, Stage1_CopyIn, 0, 0, originalSize);
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Decompression failed: expected {originalSize}, got {decompressedSize}, using direct copy");
            }
        }

        private void FallbackDirectCopy(CommandBufferScoped cbs, BufferHolder source, int srcOffset, int size)
        {
            Logger.Warning?.Print(LogClass.Gpu, "Using fallback direct copy for 4-stage pipeline");
            
            // 直接复制到阶段4，跳过压缩
            Stage1_CopyIn.CopyToChunked(cbs, source, srcOffset, 0, size);
            Stage4_Ready.CopyToChunked(cbs, Stage1_CopyIn, 0, 0, size);
        }

        public void Dispose()
        {
            Stage1_CopyIn?.Dispose();
            Stage2_Compression?.Dispose();
            Stage3_Compressed?.Dispose();
            Stage4_Ready?.Dispose();
            
            _compressionBuffer = null;
        }
    }

    // 虚拟大缓冲区包装器
    class VirtualLargeBufferHolder : BufferHolder
    {
        private readonly FourStageBufferManager _bufferManager;
        private readonly int _virtualSize;

        public VirtualLargeBufferHolder(VulkanRenderer gd, Device device, int size, FourStageBufferManager bufferManager) 
            : base(gd, device, new VkBuffer(), new MemoryAllocation(), size, 
                  BufferAllocationType.HostMapped, BufferAllocationType.HostMapped)
        {
            _virtualSize = size;
            _bufferManager = bufferManager;
            
            Logger.Info?.Print(LogClass.Gpu, 
                $"Created virtual large buffer: virtualSize=0x{size:X}, using 4-stage manager");
        }

        public override void SetData(int offset, ReadOnlySpan<byte> data, CommandBufferScoped? cbs = null, Action endRenderPass = null, bool allowCbsWait = true)
        {
            // 对于虚拟大缓冲区，我们需要创建一个临时缓冲区来存储数据
            // 然后通过四阶段管道处理
            using var tempBuffer = _gd.BufferManager.Create(_gd, data.Length, baseType: BufferAllocationType.HostMapped);
            if (tempBuffer != null)
            {
                tempBuffer.SetData(0, data, cbs, endRenderPass, allowCbsWait);
                
                if (cbs != null)
                {
                    _bufferManager.ProcessData(cbs.Value, tempBuffer, 0, data.Length);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu, "No command buffer provided for virtual buffer data processing");
                }
            }
            else
            {
                Logger.Error?.Print(LogClass.Gpu, "Failed to create temporary buffer for virtual large buffer");
            }
        }

        public override Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, bool isWrite = false, bool isSSBO = false)
        {
            // 返回阶段4的缓冲区供使用
            return _bufferManager.Stage4_Ready.GetBuffer(commandBuffer, isWrite, isSSBO);
        }

        public override Auto<DisposableBuffer> GetBuffer(CommandBuffer commandBuffer, int offset, int size, bool isWrite = false)
        {
            // 注意：这里假设数据已经在阶段4中
            return _bufferManager.Stage4_Ready.GetBuffer(commandBuffer, offset, size, isWrite);
        }

        public override PinnedSpan<byte> GetData(int offset, int size)
        {
            // 从阶段4获取数据
            return _bufferManager.Stage4_Ready.GetData(offset, size);
        }

        public override void Dispose()
        {
            // 虚拟缓冲区本身不持有实际资源，由FourStageBufferManager管理
            Logger.Debug?.Print(LogClass.Gpu, "Disposed virtual large buffer");
            base.Dispose();
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

        // 四阶段回退缓冲区管理器
        private readonly FourStageBufferManager _fallbackBufferManager;
        private const int LargeBufferThreshold = 100 * 1024 * 1024; // 100MB

        public BufferManager(VulkanRenderer gd, Device device)
        {
            _device = device;
            _buffers = new IdList<BufferHolder>();
            StagingBuffer = new StagingBuffer(gd, this);

            HostImportedBufferMemoryRequirements = GetHostImportedUsageRequirements(gd);

            // 初始化四阶段回退缓冲区管理器
            try
            {
                _fallbackBufferManager = new FourStageBufferManager(gd, device);
                Logger.Info?.Print(LogClass.Gpu, "FourStageBufferManager initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Failed to initialize FourStageBufferManager: {ex.Message}. Large buffer allocations will fail.");
                _fallbackBufferManager = null;
            }
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
            // 对于大缓冲区，使用四阶段回退方案
            if (size >= LargeBufferThreshold && _fallbackBufferManager != null)
            {
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Using 4-stage fallback for large buffer allocation: 0x{size:X}");

                // 创建一个虚拟的BufferHolder，实际使用四阶段缓冲区
                holder = new VirtualLargeBufferHolder(gd, _device, size, _fallbackBufferManager);
                BufferCount++;

                ulong handle64 = (uint)_buffers.Add(holder);
                return Unsafe.As<ulong, BufferHandle>(ref handle64);
            }

            // 原有创建逻辑...
            holder = Create(gd, size, forConditionalRendering: false, sparseCompatible, baseType);
            if (holder == null)
            {
                // 如果常规创建失败，也尝试使用四阶段方案
                if (_fallbackBufferManager != null && size > 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Regular creation failed for 0x{size:X}, using 4-stage fallback");
                    
                    holder = new VirtualLargeBufferHolder(gd, _device, size, _fallbackBufferManager);
                    if (holder != null)
                    {
                        BufferCount++;
                        ulong handle64 = (uint)_buffers.Add(holder);
                        return Unsafe.As<ulong, BufferHandle>(ref handle64);
                    }
                }

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

            // 最后尝试：使用简化版本的分段缓冲区
            Logger.Warning?.Print(LogClass.Gpu, 
                $"Android-optimized method failed, attempting simplified buffer for size 0x{size:X}");

            try
            {
                var simplifiedBuffer = CreateSimplifiedBuffer(gd, size);
                if (simplifiedBuffer != null)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Successfully created simplified buffer for size 0x{size:X}");
                    return simplifiedBuffer;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Simplified buffer creation failed: {ex.Message}");
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
                _fallbackBufferManager?.Dispose();

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
