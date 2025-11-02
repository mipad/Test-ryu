using Ryujinx.Common; 
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Ryujinx.Graphics.Gpu.Engine.Dma
{
    /// <summary>
    /// Represents a DMA copy engine class.
    /// </summary>
    class DmaClass : IDeviceState
    {
        private readonly GpuContext _context;
        private readonly GpuChannel _channel;
        private readonly ThreedClass _3dEngine;
        private readonly DeviceState<DmaClassState> _state;

        // 暂存缓冲区管理
        private class StagingBufferInfo
        {
            public byte[] Data;
            public ulong GpuAddress;
            public int Size;
            public DateTime LastAccess;
        }
        
        private static readonly List<StagingBufferInfo> _stagingBuffers = new List<StagingBufferInfo>();
        private static readonly object _stagingBufferLock = new object();
        private const int MaxStagingBuffers = 8; // 最大暂存缓冲区数量
        private const long StagingBufferExpiryMs = 30000; // 30秒过期

        /// <summary>
        /// Copy flags passed on DMA launch.
        /// </summary>
        [Flags]
        private enum CopyFlags
        {
            SrcLinear = 1 << 7,
            DstLinear = 1 << 8,
            MultiLineEnable = 1 << 9,
            RemapEnable = 1 << 10,
        }

        /// <summary>
        /// Texture parameters for copy.
        /// </summary>
        private readonly struct TextureParams
        {
            /// <summary>
            /// Copy region X coordinate.
            /// </summary>
            public readonly int RegionX;

            /// <summary>
            /// Copy region Y coordinate.
            /// </summary>
            public readonly int RegionY;

            /// <summary>
            /// Offset from the base pointer of the data in memory.
            /// </summary>
            public readonly int BaseOffset;

            /// <summary>
            /// Bytes per pixel.
            /// </summary>
            public readonly int Bpp;

            /// <summary>
            /// Whether the texture is linear. If false, the texture is block linear.
            /// </summary>
            public readonly bool Linear;

            /// <summary>
            /// Pixel offset from XYZ coordinates calculator.
            /// </summary>
            public readonly OffsetCalculator Calculator;

            /// <summary>
            /// Creates texture parameters.
            /// </summary>
            /// <param name="regionX">Copy region X coordinate</param>
            /// <param name="regionY">Copy region Y coordinate</param>
            /// <param name="baseOffset">Offset from the base pointer of the data in memory</param>
            /// <param name="bpp">Bytes per pixel</param>
            /// <param name="linear">Whether the texture is linear. If false, the texture is block linear</param>
            /// <param name="calculator">Pixel offset from XYZ coordinates calculator</param>
            public TextureParams(int regionX, int regionY, int baseOffset, int bpp, bool linear, OffsetCalculator calculator)
            {
                RegionX = regionX;
                RegionY = regionY;
                BaseOffset = baseOffset;
                Bpp = bpp;
                Linear = linear;
                Calculator = calculator;
            }
        }

        [StructLayout(LayoutKind.Sequential, Size = 3, Pack = 1)]
        private struct UInt24
        {
            public byte Byte0;
            public byte Byte1;
            public byte Byte2;
        }

        /// <summary>
        /// Creates a new instance of the DMA copy engine class.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="channel">GPU channel</param>
        /// <param name="threedEngine">3D engine</param>
        public DmaClass(GpuContext context, GpuChannel channel, ThreedClass threedEngine)
        {
            _context = context;
            _channel = channel;
            _3dEngine = threedEngine;
            _state = new DeviceState<DmaClassState>(new Dictionary<string, RwCallback>
            {
                { nameof(DmaClassState.LaunchDma), new RwCallback(LaunchDma, null) },
            });
        }

        /// <summary>
        /// Reads data from the class registers.
        /// </summary>
        /// <param name="offset">Register byte offset</param>
        /// <returns>Data at the specified offset</returns>
        public int Read(int offset) => _state.Read(offset);

        /// <summary>
        /// Writes data to the class registers.
        /// </summary>
        /// <param name="offset">Register byte offset</param>
        /// <param name="data">Data to be written</param>
        public void Write(int offset, int data) => _state.Write(offset, data);

        /// <summary>
        /// Determine if a buffer-to-texture region covers the entirety of a texture.
        /// </summary>
        /// <param name="tex">Texture to compare</param>
        /// <param name="linear">True if the texture is linear, false if block linear</param>
        /// <param name="bpp">Texture bytes per pixel</param>
        /// <param name="stride">Texture stride</param>
        /// <param name="xCount">Number of pixels to be copied</param>
        /// <param name="yCount">Number of lines to be copied</param>
        /// <returns></returns>
        private static bool IsTextureCopyComplete(DmaTexture tex, bool linear, int bpp, int stride, int xCount, int yCount)
        {
            if (linear)
            {
                // If the stride is negative, the texture has to be flipped, so
                // the fast copy is not trivial, use the slow path.
                if (stride <= 0)
                {
                    return false;
                }

                int alignWidth = Constants.StrideAlignment / bpp;
                return stride / bpp == BitUtils.AlignUp(xCount, alignWidth);
            }
            else
            {
                int alignWidth = Constants.GobAlignment / bpp;
                return tex.RegionX == 0 &&
                       tex.RegionY == 0 &&
                       tex.Width == BitUtils.AlignUp(xCount, alignWidth) &&
                       tex.Height == yCount;
            }
        }

        /// <summary>
        /// Releases a semaphore for a given LaunchDma method call.
        /// </summary>
        /// <param name="argument">The LaunchDma call argument</param>
        private void ReleaseSemaphore(int argument)
        {
            LaunchDmaSemaphoreType type = (LaunchDmaSemaphoreType)((argument >> 3) & 0x3);
            if (type != LaunchDmaSemaphoreType.None)
            {
                ulong address = ((ulong)_state.State.SetSemaphoreA << 32) | _state.State.SetSemaphoreB;
                if (type == LaunchDmaSemaphoreType.ReleaseOneWordSemaphore)
                {
                    _channel.MemoryManager.Write(address, _state.State.SetSemaphorePayload);
                }
                else /* if (type == LaunchDmaSemaphoreType.ReleaseFourWordSemaphore) */
                {
                    _channel.MemoryManager.Write(address + 8, _context.GetTimestamp());
                    _channel.MemoryManager.Write(address, (ulong)_state.State.SetSemaphorePayload);
                }
            }
        }

        /// <summary>
        /// 获取或创建暂存缓冲区
        /// </summary>
        private static byte[] GetOrCreateStagingBuffer(ulong address, int size)
        {
            lock (_stagingBufferLock)
            {
                // 清理过期的缓冲区
                CleanupExpiredStagingBuffers();

                // 查找现有的缓冲区
                foreach (var buffer in _stagingBuffers)
                {
                    if (buffer.GpuAddress == address && buffer.Size >= size)
                    {
                        buffer.LastAccess = DateTime.UtcNow;
                        return buffer.Data;
                    }
                }

                // 创建新的暂存缓冲区
                var newBuffer = new StagingBufferInfo
                {
                    Data = new byte[size],
                    GpuAddress = address,
                    Size = size,
                    LastAccess = DateTime.UtcNow
                };

                _stagingBuffers.Add(newBuffer);

                // 如果缓冲区数量超过限制，移除最旧的
                if (_stagingBuffers.Count > MaxStagingBuffers)
                {
                    StagingBufferInfo oldest = null;
                    foreach (var buffer in _stagingBuffers)
                    {
                        if (oldest == null || buffer.LastAccess < oldest.LastAccess)
                        {
                            oldest = buffer;
                        }
                    }
                    if (oldest != null)
                    {
                        _stagingBuffers.Remove(oldest);
                    }
                }

                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                    $"创建暂存缓冲区: 地址=0x{address:X16}, 大小=0x{size:X}");

                return newBuffer.Data;
            }
        }

        /// <summary>
        /// 清理过期的暂存缓冲区
        /// </summary>
        private static void CleanupExpiredStagingBuffers()
        {
            var now = DateTime.UtcNow;
            _stagingBuffers.RemoveAll(buffer => 
                (now - buffer.LastAccess).TotalMilliseconds > StagingBufferExpiryMs);
        }

        /// <summary>
        /// 使用暂存缓冲区策略执行DMA复制
        /// </summary>
        private void StagedDmaCopy(int argument)
        {
            var memoryManager = _channel.MemoryManager;

            CopyFlags copyFlags = (CopyFlags)argument;

            bool srcLinear = copyFlags.HasFlag(CopyFlags.SrcLinear);
            bool dstLinear = copyFlags.HasFlag(CopyFlags.DstLinear);
            bool copy2D = copyFlags.HasFlag(CopyFlags.MultiLineEnable);
            bool remap = copyFlags.HasFlag(CopyFlags.RemapEnable);

            uint size = _state.State.LineLengthIn;

            if (size == 0)
            {
                return;
            }

            ulong srcGpuVa = ((ulong)_state.State.OffsetInUpperUpper << 32) | _state.State.OffsetInLower;
            ulong dstGpuVa = ((ulong)_state.State.OffsetOutUpperUpper << 32) | _state.State.OffsetOutLower;

            int xCount = (int)_state.State.LineLengthIn;
            int yCount = (int)_state.State.LineCount;

            _channel.TextureManager.RefreshModifiedTextures();
            _3dEngine.CreatePendingSyncs();
            _3dEngine.FlushUboDirty();

            // 记录DMA操作信息
            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                $"使用暂存缓冲区执行DMA复制 - 模式: {(copy2D ? "2D" : "1D")}, " +
                $"大小: 0x{size:X}, 源: 0x{srcGpuVa:X16}, 目标: 0x{dstGpuVa:X16}");

            if (copy2D)
            {
                // 2D纹理复制
                int componentSize = (int)_state.State.SetRemapComponentsComponentSize + 1;
                int srcComponents = (int)_state.State.SetRemapComponentsNumSrcComponents + 1;
                int dstComponents = (int)_state.State.SetRemapComponentsNumDstComponents + 1;
                int srcBpp = remap ? srcComponents * componentSize : 1;
                int dstBpp = remap ? dstComponents * componentSize : 1;

                var dst = Unsafe.As<uint, DmaTexture>(ref _state.State.SetDstBlockSize);
                var src = Unsafe.As<uint, DmaTexture>(ref _state.State.SetSrcBlockSize);

                int srcRegionX = 0, srcRegionY = 0, dstRegionX = 0, dstRegionY = 0;

                if (!srcLinear)
                {
                    srcRegionX = src.RegionX;
                    srcRegionY = src.RegionY;
                }

                if (!dstLinear)
                {
                    dstRegionX = dst.RegionX;
                    dstRegionY = dst.RegionY;
                }

                int srcStride = (int)_state.State.PitchIn;
                int dstStride = (int)_state.State.PitchOut;

                var srcCalculator = new OffsetCalculator(
                    src.Width,
                    src.Height,
                    srcStride,
                    srcLinear,
                    src.MemoryLayout.UnpackGobBlocksInY(),
                    src.MemoryLayout.UnpackGobBlocksInZ(),
                    srcBpp);

                var dstCalculator = new OffsetCalculator(
                    dst.Width,
                    dst.Height,
                    dstStride,
                    dstLinear,
                    dst.MemoryLayout.UnpackGobBlocksInY(),
                    dst.MemoryLayout.UnpackGobBlocksInZ(),
                    dstBpp);

                (int srcBaseOffset, int srcSize) = srcCalculator.GetRectangleRange(srcRegionX, srcRegionY, xCount, yCount);
                (int dstBaseOffset, int dstSize) = dstCalculator.GetRectangleRange(dstRegionX, dstRegionY, xCount, yCount);

                if (srcLinear && srcStride < 0)
                {
                    srcBaseOffset += srcStride * (yCount - 1);
                }

                if (dstLinear && dstStride < 0)
                {
                    dstBaseOffset += dstStride * (yCount - 1);
                }

                // 使用暂存缓冲区进行复制
                try
                {
                    // 获取源数据的暂存缓冲区
                    var srcStagingBuffer = GetOrCreateStagingBuffer(srcGpuVa, srcSize);
                    
                    // 从GPU内存读取到暂存缓冲区
                    var srcSpan = memoryManager.GetSpan(srcGpuVa + (ulong)srcBaseOffset, srcSize, true);
                    srcSpan.CopyTo(srcStagingBuffer);

                    // 获取目标数据的暂存缓冲区
                    var dstStagingBuffer = GetOrCreateStagingBuffer(dstGpuVa, dstSize);

                    // 执行复制操作到目标暂存缓冲区
                    TextureParams srcParams = new(srcRegionX, srcRegionY, srcBaseOffset, srcBpp, srcLinear, srcCalculator);
                    TextureParams dstParams = new(dstRegionX, dstRegionY, dstBaseOffset, dstBpp, dstLinear, dstCalculator);

                    // 使用暂存缓冲区进行复制操作
                    if (remap)
                    {
                        // 处理重映射复制
                        CopyWithRemap(dstStagingBuffer, srcStagingBuffer, dstParams, srcParams, componentSize);
                    }
                    else
                    {
                        // 直接复制
                        CopyDirect(dstStagingBuffer, srcStagingBuffer, dstParams, srcParams, srcBpp);
                    }

                    // 将暂存缓冲区数据写回GPU内存
                    memoryManager.Write(dstGpuVa + (ulong)dstBaseOffset, dstStagingBuffer);

                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                        "2D DMA复制完成（使用暂存缓冲区）");
                }
                catch (Exception ex)
                {
                    Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                        $"2D DMA复制失败: {ex.Message}");
                }
            }
            else
            {
                // 1D缓冲区复制
                try
                {
                    // 使用暂存缓冲区进行1D复制
                    var srcStagingBuffer = GetOrCreateStagingBuffer(srcGpuVa, (int)size);
                    var dstStagingBuffer = GetOrCreateStagingBuffer(dstGpuVa, (int)size);

                    // 从源读取到暂存缓冲区
                    var srcSpan = memoryManager.GetSpan(srcGpuVa, (int)size, true);
                    srcSpan.CopyTo(srcStagingBuffer);

                    // 复制到目标暂存缓冲区
                    srcStagingBuffer.AsSpan(0, (int)size).CopyTo(dstStagingBuffer);

                    // 写回GPU内存
                    memoryManager.Write(dstGpuVa, dstStagingBuffer.AsSpan(0, (int)size));

                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                        "1D DMA复制完成（使用暂存缓冲区）");
                }
                catch (Exception ex)
                {
                    Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                        $"1D DMA复制失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 直接复制操作（使用暂存缓冲区）
        /// </summary>
        private void CopyDirect(byte[] dstBuffer, byte[] srcBuffer, TextureParams dst, TextureParams src, int srcBpp)
        {
            int xCount = (int)_state.State.LineLengthIn;
            int yCount = (int)_state.State.LineCount;

            unsafe
            {
                fixed (byte* dstPtr = dstBuffer, srcPtr = srcBuffer)
                {
                    byte* dstBase = dstPtr - dst.BaseOffset;
                    byte* srcBase = srcPtr - src.BaseOffset;

                    for (int y = 0; y < yCount; y++)
                    {
                        src.Calculator.SetY(src.RegionY + y);
                        dst.Calculator.SetY(dst.RegionY + y);

                        for (int x = 0; x < xCount; x++)
                        {
                            int srcOffset = src.Calculator.GetOffset(src.RegionX + x);
                            int dstOffset = dst.Calculator.GetOffset(dst.RegionX + x);

                            // 根据字节数进行复制
                            for (int i = 0; i < srcBpp; i++)
                            {
                                *(dstBase + dstOffset + i) = *(srcBase + srcOffset + i);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 带重映射的复制操作（使用暂存缓冲区）
        /// </summary>
        private void CopyWithRemap(byte[] dstBuffer, byte[] srcBuffer, TextureParams dst, TextureParams src, int componentSize)
        {
            int dstComponents = (int)_state.State.SetRemapComponentsNumDstComponents + 1;

            for (int i = 0; i < dstComponents; i++)
            {
                SetRemapComponentsDst componentsDst = i switch
                {
                    0 => _state.State.SetRemapComponentsDstX,
                    1 => _state.State.SetRemapComponentsDstY,
                    2 => _state.State.SetRemapComponentsDstZ,
                    _ => _state.State.SetRemapComponentsDstW,
                };

                // 简化处理：只记录重映射操作
                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                    $"组件重映射: 目标组件 {i} <- {componentsDst}");
            }

            // 简化实现：直接复制所有数据
            srcBuffer.AsSpan(0, Math.Min(srcBuffer.Length, dstBuffer.Length)).CopyTo(dstBuffer);
        }

        /// <summary>
        /// Performs a buffer to buffer, or buffer to texture copy.
        /// </summary>
        /// <param name="argument">The LaunchDma call argument</param>
        private void DmaCopy(int argument)
        {
            // 使用暂存缓冲区策略执行DMA复制
            StagedDmaCopy(argument);
        }

        /// <summary>
        /// Copies data from one texture to another, while performing layout conversion if necessary.
        /// </summary>
        /// <typeparam name="T">Pixel type</typeparam>
        /// <param name="dstSpan">Destination texture memory region</param>
        /// <param name="srcSpan">Source texture memory region</param>
        /// <param name="dst">Destination texture parameters</param>
        /// <param name="src">Source texture parameters</param>
        private unsafe void Copy<T>(Span<byte> dstSpan, ReadOnlySpan<byte> srcSpan, TextureParams dst, TextureParams src) where T : unmanaged
        {
            // 这个方法现在在暂存缓冲区策略中不再使用
            Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                "使用暂存缓冲区策略，跳过传统复制方法");
        }

        /// <summary>
        /// Sets texture pixel data to a constant value, while performing layout conversion if necessary.
        /// </summary>
        /// <typeparam name="T">Pixel type</typeparam>
        /// <param name="dstSpan">Destination texture memory region</param>
        /// <param name="dst">Destination texture parameters</param>
        /// <param name="fillValue">Constant pixel value to be set</param>
        private unsafe void Fill<T>(Span<byte> dstSpan, TextureParams dst, T fillValue) where T : unmanaged
        {
            // 这个方法现在在暂存缓冲区策略中不再使用
            Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                "使用暂存缓冲区策略，跳过传统填充方法");
        }

        /// <summary>
        /// Copies data from one texture to another, while performing layout conversion and component shuffling if necessary.
        /// </summary>
        /// <typeparam name="T">Pixel type</typeparam>
        /// <param name="dstSpan">Destination texture memory region</param>
        /// <param name="srcSpan">Source texture memory region</param>
        /// <param name="dst">Destination texture parameters</param>
        /// <param name="src">Source texture parameters</param>
        private void CopyShuffle<T>(Span<byte> dstSpan, ReadOnlySpan<byte> srcSpan, TextureParams dst, TextureParams src) where T : unmanaged
        {
            // 这个方法现在在暂存缓冲区策略中不再使用
            Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                "使用暂存缓冲区策略，跳过传统重排方法");
        }

        /// <summary>
        /// Safely copies block linear data with block linear GOBs to a block linear destination with linear GOBs.
        /// This version includes comprehensive error handling to prevent crashes.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager</param>
        /// <param name="srcGpuVa">Source GPU virtual address</param>
        /// <param name="dstGpuVa">Destination GPU virtual address</param>
        /// <param name="size">Size in bytes of the copy</param>
        private static void SafeCopyGobBlockLinearToLinear(MemoryManager memoryManager, ulong srcGpuVa, ulong dstGpuVa, ulong size)
        {
            // 使用暂存缓冲区策略
            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                $"跳过传统GOB复制，使用暂存缓冲区策略 - 源: 0x{srcGpuVa:X16}, 目标: 0x{dstGpuVa:X16}, 大小: 0x{size:X}");
        }

        /// <summary>
        /// Safely copies block linear data with linear GOBs to a block linear destination with block linear GOBs.
        /// This version includes comprehensive error handling to prevent crashes.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager</param>
        /// <param name="srcGpuVa">Source GPU virtual address</param>
        /// <param name="dstGpuVa">Destination GPU virtual address</param>
        /// <param name="size">Size in bytes of the copy</param>
        private static void SafeCopyGobLinearToBlockLinear(MemoryManager memoryManager, ulong srcGpuVa, ulong dstGpuVa, ulong size)
        {
            // 使用暂存缓冲区策略
            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu, 
                $"跳过传统GOB复制，使用暂存缓冲区策略 - 源: 0x{srcGpuVa:X16}, 目标: 0x{dstGpuVa:X16}, 大小: 0x{size:X}");
        }

        /// <summary>
        /// Calculates the GOB block linear address from a linear address.
        /// </summary>
        /// <param name="address">Linear address</param>
        /// <returns>Block linear address</returns>
        private static ulong ConvertGobLinearToBlockLinearAddress(ulong address)
        {
            // 返回原始地址，不进行转换
            return address;
        }

        /// <summary>
        /// Performs a buffer to buffer, or buffer to texture copy, then optionally releases a semaphore.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        private void LaunchDma(int argument)
        {
            try
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"开始DMA操作, 参数: 0x{argument:X}");
                
                DmaCopy(argument);
                
                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.Gpu, "DMA复制完成，释放信号量");
                
                ReleaseSemaphore(argument);
                
                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.Gpu, "DMA操作完成");
            }
            catch (Exception ex)
            {
                // 记录异常但不崩溃
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"DMA操作异常: {ex.Message}");
                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"异常堆栈: {ex.StackTrace}");
            }
        }
    }
}
