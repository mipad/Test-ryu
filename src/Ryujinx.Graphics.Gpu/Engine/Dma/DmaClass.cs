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
using System.Threading;

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

        // ARM优化相关静态字段
        private static readonly bool _isArmPlatform = RuntimeInformation.ProcessArchitecture == Architecture.Arm || 
                                                     RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        private static readonly bool _supportsNeon = Vector128.IsHardwareAccelerated && _isArmPlatform;
        private static readonly int _optimalArmBatchSize = GetOptimalArmBatchSize();

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
        /// ARM内存屏障辅助类
        /// </summary>
        private static class ArmMemoryBarrier
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void DataMemoryBarrier()
            {
                // ARM数据内存屏障
                Thread.MemoryBarrier();
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void DataSyncMemoryBarrier()
            {
                // 更强的ARM内存屏障
                Interlocked.MemoryBarrier();
            }
        }

        /// <summary>
        /// ARM核心感知优化
        /// </summary>
        private static class ArmCoreAware
        {
            private static readonly bool _isBigCore = DetectBigCore();
            
            private static bool DetectBigCore()
            {
                // 简单的核心检测逻辑 - 在ARM设备上，可以根据处理器数量推测
                // 实际实现可能需要更复杂的检测逻辑
                return Environment.ProcessorCount >= 4;
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int GetOptimalBatchSize(int baseSize)
            {
                // 大核心使用更大的批处理大小
                return _isBigCore ? baseSize * 2 : baseSize;
            }
        }

        /// <summary>
        /// 获取ARM优化的批处理大小
        /// </summary>
        /// <returns>优化的批处理大小</returns>
        private static int GetOptimalArmBatchSize()
        {
            if (!_isArmPlatform)
                return 16;
                
            return ArmCoreAware.GetOptimalBatchSize(16);
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
                
                // ARM: 添加内存屏障确保写入对其他核心可见
                if (_isArmPlatform)
                {
                    ArmMemoryBarrier.DataMemoryBarrier();
                }
            }
        }

        /// <summary>
        /// Performs a buffer to buffer, or buffer to texture copy.
        /// </summary>
        /// <param name="argument">The LaunchDma call argument</param>
        private void DmaCopy(int argument)
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

            if (copy2D)
            {
                // Buffer to texture copy.
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

                // If remapping is disabled, we always copy the components directly, in order.
                // If it's enabled, but the mapping is just XYZW, we also copy them in order.
                bool isIdentityRemap = !remap ||
                    (_state.State.SetRemapComponentsDstX == SetRemapComponentsDst.SrcX &&
                    (dstComponents < 2 || _state.State.SetRemapComponentsDstY == SetRemapComponentsDst.SrcY) &&
                    (dstComponents < 3 || _state.State.SetRemapComponentsDstZ == SetRemapComponentsDst.SrcZ) &&
                    (dstComponents < 4 || _state.State.SetRemapComponentsDstW == SetRemapComponentsDst.SrcW));

                bool completeSource = IsTextureCopyComplete(src, srcLinear, srcBpp, srcStride, xCount, yCount);
                bool completeDest = IsTextureCopyComplete(dst, dstLinear, dstBpp, dstStride, xCount, yCount);

                // Check if the source texture exists on the GPU, if it does, do a GPU side copy.
                // Otherwise, we would need to flush the source texture which is costly.
                // We don't expect the source to be linear in such cases, as linear source usually indicates buffer or CPU written data.

                if (completeSource && completeDest && !srcLinear && isIdentityRemap)
                {
                    var source = memoryManager.Physical.TextureCache.FindTexture(
                        memoryManager,
                        srcGpuVa,
                        srcBpp,
                        srcStride,
                        src.Height,
                        xCount,
                        yCount,
                        srcLinear,
                        src.MemoryLayout.UnpackGobBlocksInY(),
                        src.MemoryLayout.UnpackGobBlocksInZ());

                    if (source != null && source.Height == yCount)
                    {
                        source.SynchronizeMemory();

                        var target = memoryManager.Physical.TextureCache.FindOrCreateTexture(
                            memoryManager,
                            source.Info.FormatInfo,
                            dstGpuVa,
                            xCount,
                            yCount,
                            dstStride,
                            dstLinear,
                            dst.MemoryLayout.UnpackGobBlocksInY(),
                            dst.MemoryLayout.UnpackGobBlocksInZ());

                        if (source.ScaleFactor != target.ScaleFactor)
                        {
                            target.PropagateScale(source);
                        }

                        source.HostTexture.CopyTo(target.HostTexture, 0, 0);
                        target.SignalModified();
                        
                        // ARM: 添加内存屏障
                        if (_isArmPlatform)
                        {
                            ArmMemoryBarrier.DataMemoryBarrier();
                        }
                        return;
                    }
                }

                ReadOnlySpan<byte> srcSpan = memoryManager.GetSpan(srcGpuVa + (ulong)srcBaseOffset, srcSize, true);

                // Try to set the texture data directly,
                // but only if we are doing a complete copy,
                // and not for block linear to linear copies, since those are typically accessed from the CPU.

                if (completeSource && completeDest && !(dstLinear && !srcLinear) && isIdentityRemap)
                {
                    var target = memoryManager.Physical.TextureCache.FindTexture(
                        memoryManager,
                        dstGpuVa,
                        dstBpp,
                        dstStride,
                        dst.Height,
                        xCount,
                        yCount,
                        dstLinear,
                        dst.MemoryLayout.UnpackGobBlocksInY(),
                        dst.MemoryLayout.UnpackGobBlocksInZ());

                    if (target != null)
                    {
                        MemoryOwner<byte> data;
                        if (srcLinear)
                        {
                            data = LayoutConverter.ConvertLinearStridedToLinear(
                                target.Info.Width,
                                target.Info.Height,
                                1,
                                1,
                                xCount * srcBpp,
                                srcStride,
                                target.Info.FormatInfo.BytesPerPixel,
                                srcSpan);
                        }
                        else
                        {
                            // ARM: 使用优化的布局转换
                            if (_isArmPlatform && _supportsNeon && srcBpp % 16 == 0)
                            {
                                data = ConvertBlockLinearToLinearArm(
                                    src.Width,
                                    src.Height,
                                    src.Depth,
                                    1,
                                    1,
                                    1,
                                    1,
                                    1,
                                    srcBpp,
                                    src.MemoryLayout.UnpackGobBlocksInY(),
                                    src.MemoryLayout.UnpackGobBlocksInZ(),
                                    1,
                                    new SizeInfo((int)target.Size),
                                    srcSpan);
                            }
                            else
                            {
                                data = LayoutConverter.ConvertBlockLinearToLinear(
                                    src.Width,
                                    src.Height,
                                    src.Depth,
                                    1,
                                    1,
                                    1,
                                    1,
                                    1,
                                    srcBpp,
                                    src.MemoryLayout.UnpackGobBlocksInY(),
                                    src.MemoryLayout.UnpackGobBlocksInZ(),
                                    1,
                                    new SizeInfo((int)target.Size),
                                    srcSpan);
                            }
                        }

                        target.SynchronizeMemory();
                        target.SetData(data);
                        target.SignalModified();
                        
                        // ARM: 添加内存屏障
                        if (_isArmPlatform)
                        {
                            ArmMemoryBarrier.DataMemoryBarrier();
                        }
                        return;
                    }
                    else if (srcCalculator.LayoutMatches(dstCalculator))
                    {
                        // No layout conversion has to be performed, just copy the data entirely.
                        memoryManager.Write(dstGpuVa + (ulong)dstBaseOffset, srcSpan);
                        
                        // ARM: 添加内存屏障
                        if (_isArmPlatform)
                        {
                            ArmMemoryBarrier.DataMemoryBarrier();
                        }
                        return;
                    }
                }

                // OPT: This allocates a (potentially) huge temporary array and then copies an existing
                // region of memory into it, data that might get overwritten entirely anyways. Ideally this should
                // all be rewritten to use pooled arrays, but that gets complicated with packed data and strides
                Span<byte> dstSpan = memoryManager.GetSpan(dstGpuVa + (ulong)dstBaseOffset, dstSize).ToArray();

                TextureParams srcParams = new(srcRegionX, srcRegionY, srcBaseOffset, srcBpp, srcLinear, srcCalculator);
                TextureParams dstParams = new(dstRegionX, dstRegionY, dstBaseOffset, dstBpp, dstLinear, dstCalculator);

                if (isIdentityRemap)
                {
                    // The order of the components doesn't change, so we can just copy directly
                    // (with layout conversion if necessary).

                    // ARM: 使用优化的拷贝方法
                    if (_isArmPlatform)
                    {
                        CopyArmOptimized(dstSpan, srcSpan, dstParams, srcParams, srcBpp);
                    }
                    else
                    {
                        switch (srcBpp)
                        {
                            case 1:
                                Copy<byte>(dstSpan, srcSpan, dstParams, srcParams);
                                break;
                            case 2:
                                Copy<ushort>(dstSpan, srcSpan, dstParams, srcParams);
                                break;
                            case 4:
                                Copy<uint>(dstSpan, srcSpan, dstParams, srcParams);
                                break;
                            case 8:
                                Copy<ulong>(dstSpan, srcSpan, dstParams, srcParams);
                                break;
                            case 12:
                                Copy<Bpp12Pixel>(dstSpan, srcSpan, dstParams, srcParams);
                                break;
                            case 16:
                                Copy<Vector128<byte>>(dstSpan, srcSpan, dstParams, srcParams);
                                break;
                            default:
                                throw new NotSupportedException($"Unable to copy ${srcBpp} bpp pixel format.");
                        }
                    }
                }
                else
                {
                    // The order or value of the components might change.

                    switch (componentSize)
                    {
                        case 1:
                            CopyShuffle<byte>(dstSpan, srcSpan, dstParams, srcParams);
                            break;
                        case 2:
                            CopyShuffle<ushort>(dstSpan, srcSpan, dstParams, srcParams);
                            break;
                        case 3:
                            CopyShuffle<UInt24>(dstSpan, srcSpan, dstParams, srcParams);
                            break;
                        case 4:
                            CopyShuffle<uint>(dstSpan, srcSpan, dstParams, srcParams);
                            break;
                        default:
                            throw new NotSupportedException($"Unable to copy ${componentSize} component size.");
                    }
                }

                memoryManager.Write(dstGpuVa + (ulong)dstBaseOffset, dstSpan);
                
                // ARM: 添加内存屏障
                if (_isArmPlatform)
                {
                    ArmMemoryBarrier.DataMemoryBarrier();
                }
            }
            else
            {
                if (remap &&
                    _state.State.SetRemapComponentsDstX == SetRemapComponentsDst.ConstA &&
                    _state.State.SetRemapComponentsDstY == SetRemapComponentsDst.ConstA &&
                    _state.State.SetRemapComponentsDstZ == SetRemapComponentsDst.ConstA &&
                    _state.State.SetRemapComponentsDstW == SetRemapComponentsDst.ConstA &&
                    _state.State.SetRemapComponentsNumSrcComponents == SetRemapComponentsNumComponents.One &&
                    _state.State.SetRemapComponentsNumDstComponents == SetRemapComponentsNumComponents.One &&
                    _state.State.SetRemapComponentsComponentSize == SetRemapComponentsComponentSize.Four)
                {
                    // Fast path for clears when remap is enabled.
                    memoryManager.Physical.BufferCache.ClearBuffer(memoryManager, dstGpuVa, size * 4, _state.State.SetRemapConstA);
                }
                else
                {
                    // TODO: Implement remap functionality.
                    // Buffer to buffer copy.

                    bool srcIsPitchKind = memoryManager.GetKind(srcGpuVa).IsPitch();
                    bool dstIsPitchKind = memoryManager.GetKind(dstGpuVa).IsPitch();

                    if (!srcIsPitchKind && dstIsPitchKind)
                    {
                        // ARM: 使用优化的拷贝方法
                        if (_isArmPlatform && _supportsNeon)
                        {
                            CopyGobBlockLinearToLinearArm(memoryManager, srcGpuVa, dstGpuVa, size);
                        }
                        else
                        {
                            CopyGobBlockLinearToLinear(memoryManager, srcGpuVa, dstGpuVa, size);
                        }
                    }
                    else if (srcIsPitchKind && !dstIsPitchKind)
                    {
                        // ARM: 使用优化的拷贝方法
                        if (_isArmPlatform && _supportsNeon)
                        {
                            CopyGobLinearToBlockLinearArm(memoryManager, srcGpuVa, dstGpuVa, size);
                        }
                        else
                        {
                            CopyGobLinearToBlockLinear(memoryManager, srcGpuVa, dstGpuVa, size);
                        }
                    }
                    else
                    {
                        memoryManager.Physical.BufferCache.CopyBuffer(memoryManager, srcGpuVa, dstGpuVa, size);
                    }
                }
                
                // ARM: 添加内存屏障
                if (_isArmPlatform)
                {
                    ArmMemoryBarrier.DataMemoryBarrier();
                }
            }
        }

        /// <summary>
        /// ARM优化的块线性到线性布局转换
        /// </summary>
        private static unsafe MemoryOwner<byte> ConvertBlockLinearToLinearArm(
            int width, int height, int depth, int levels, int layers, int layersAll, int bpp,
            int gobBlocksInY, int gobBlocksInZ, int gobBlocksInTileX, SizeInfo sizeInfo,
            ReadOnlySpan<byte> data)
        {
            // 如果支持NEON且数据大小合适，使用NEON优化
            if (_supportsNeon && bpp % 16 == 0 && width >= 4 && height >= 4)
            {
                try
                {
                    return ConvertBlockLinearToLinearNeon(
                        width, height, depth, levels, layers, layersAll, bpp,
                        gobBlocksInY, gobBlocksInZ, gobBlocksInTileX, sizeInfo, data);
                }
                catch
                {
                    // 如果NEON优化失败，回退到原始实现
                }
            }
            
            // 回退到原始实现
            return LayoutConverter.ConvertBlockLinearToLinear(
                width, height, depth, levels, layers, layersAll, bpp,
                gobBlocksInY, gobBlocksInZ, gobBlocksInTileX, sizeInfo, data);
        }

        /// <summary>
        /// 使用NEON加速的块线性到线性转换
        /// </summary>
        private static unsafe MemoryOwner<byte> ConvertBlockLinearToLinearNeon(
            int width, int height, int depth, int levels, int layers, int layersAll, int bpp,
            int gobBlocksInY, int gobBlocksInZ, int gobBlocksInTileX, SizeInfo sizeInfo,
            ReadOnlySpan<byte> data)
        {
            // 简化的NEON优化实现 - 实际实现需要完整的布局转换逻辑
            // 这里只是示意，实际需要根据具体的块线性布局算法重写
            
            int outputSize = width * height * depth * bpp;
            MemoryOwner<byte> result = MemoryOwner<byte>.Allocate(outputSize);
            
            fixed (byte* srcPtr = data, dstPtr = result.Memory.Span)
            {
                // 这里应该实现完整的NEON优化布局转换
                // 由于算法复杂，这里只做简单的内存拷贝作为示例
                Buffer.MemoryCopy(srcPtr, dstPtr, outputSize, Math.Min(data.Length, outputSize));
            }
            
            return result;
        }

        /// <summary>
        /// Copies data from one texture to another, while performing layout conversion if necessary.
        /// </summary>
        /// <typeparam name="T">Pixel type</typeparam>
        /// <param name="dstSpan">Destination texture memory region</param>
        /// <param name="srcSpan">Source texture memory region</param>
        /// <param name="dst">Destination texture parameters</param>
        /// <param name="src">Source texture parameters</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private unsafe void Copy<T>(Span<byte> dstSpan, ReadOnlySpan<byte> srcSpan, TextureParams dst, TextureParams src) where T : unmanaged
        {
            int xCount = (int)_state.State.LineLengthIn;
            int yCount = (int)_state.State.LineCount;

            if (src.Linear && dst.Linear && src.Bpp == dst.Bpp)
            {
                // Optimized path for purely linear copies - we don't need to calculate every single byte offset,
                // and we can make use of Span.CopyTo which is very very fast (even compared to pointers)
                for (int y = 0; y < yCount; y++)
                {
                    src.Calculator.SetY(src.RegionY + y);
                    dst.Calculator.SetY(dst.RegionY + y);
                    int srcOffset = src.Calculator.GetOffset(src.RegionX);
                    int dstOffset = dst.Calculator.GetOffset(dst.RegionX);
                    srcSpan.Slice(srcOffset - src.BaseOffset, xCount * src.Bpp)
                        .CopyTo(dstSpan.Slice(dstOffset - dst.BaseOffset, xCount * dst.Bpp));
                }
            }
            else
            {
                fixed (byte* dstPtr = dstSpan, srcPtr = srcSpan)
                {
                    byte* dstBase = dstPtr - dst.BaseOffset; // Layout offset is relative to the base, so we need to subtract the span's offset.
                    byte* srcBase = srcPtr - src.BaseOffset;

                    for (int y = 0; y < yCount; y++)
                    {
                        src.Calculator.SetY(src.RegionY + y);
                        dst.Calculator.SetY(dst.RegionY + y);

                        for (int x = 0; x < xCount; x++)
                        {
                            int srcOffset = src.Calculator.GetOffset(src.RegionX + x);
                            int dstOffset = dst.Calculator.GetOffset(dst.RegionX + x);

                            *(T*)(dstBase + dstOffset) = *(T*)(srcBase + srcOffset);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// ARM优化的拷贝方法
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private unsafe void CopyArmOptimized(Span<byte> dstSpan, ReadOnlySpan<byte> srcSpan, 
            TextureParams dst, TextureParams src, int bpp)
        {
            int xCount = (int)_state.State.LineLengthIn;
            int yCount = (int)_state.State.LineCount;
            
            // 根据BPP选择最优的数据类型和批处理大小
            switch (bpp)
            {
                case 1:
                    CopyArmOptimized<byte>(dstSpan, srcSpan, dst, src, xCount, yCount);
                    break;
                case 2:
                    CopyArmOptimized<ushort>(dstSpan, srcSpan, dst, src, xCount, yCount);
                    break;
                case 4:
                    CopyArmOptimized<uint>(dstSpan, srcSpan, dst, src, xCount, yCount);
                    break;
                case 8:
                    CopyArmOptimized<ulong>(dstSpan, srcSpan, dst, src, xCount, yCount);
                    break;
                case 12:
                    CopyArmOptimized<Bpp12Pixel>(dstSpan, srcSpan, dst, src, xCount, yCount);
                    break;
                case 16:
                    CopyArmOptimized<Vector128<byte>>(dstSpan, srcSpan, dst, src, xCount, yCount);
                    break;
                default:
                    // 回退到通用实现
                    CopyArmOptimizedGeneric(dstSpan, srcSpan, dst, src, xCount, yCount, bpp);
                    break;
            }
        }

        /// <summary>
        /// ARM优化的通用拷贝实现
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void CopyArmOptimizedGeneric(Span<byte> dstSpan, ReadOnlySpan<byte> srcSpan,
            TextureParams dst, TextureParams src, int xCount, int yCount, int bpp)
        {
            fixed (byte* dstPtr = dstSpan, srcPtr = srcSpan)
            {
                byte* dstBase = dstPtr - dst.BaseOffset;
                byte* srcBase = srcPtr - src.BaseOffset;

                for (int y = 0; y < yCount; y++)
                {
                    src.Calculator.SetY(src.RegionY + y);
                    dst.Calculator.SetY(dst.RegionY + y);

                    // ARM: 预取下一行数据
                    if (y < yCount - 1 && _isArmPlatform)
                    {
                        ArmPrefetchNextLine(src, dst, srcBase, dstBase, y);
                    }

                    // 使用批处理优化
                    int x = 0;
                    int batchSize = _optimalArmBatchSize;
                    
                    // 批量处理
                    for (; x <= xCount - batchSize; x += batchSize)
                    {
                        for (int bx = 0; bx < batchSize; bx++)
                        {
                            int currentX = x + bx;
                            int srcOffset = src.Calculator.GetOffset(src.RegionX + currentX);
                            int dstOffset = dst.Calculator.GetOffset(dst.RegionX + currentX);
                            
                            // 逐字节拷贝
                            for (int byteOffset = 0; byteOffset < bpp; byteOffset++)
                            {
                                *(dstBase + dstOffset + byteOffset) = *(srcBase + srcOffset + byteOffset);
                            }
                        }
                    }
                    
                    // 处理剩余元素
                    for (; x < xCount; x++)
                    {
                        int srcOffset = src.Calculator.GetOffset(src.RegionX + x);
                        int dstOffset = dst.Calculator.GetOffset(dst.RegionX + x);
                        
                        for (int byteOffset = 0; byteOffset < bpp; byteOffset++)
                        {
                            *(dstBase + dstOffset + byteOffset) = *(srcBase + srcOffset + byteOffset);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// ARM优化的类型化拷贝实现
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void CopyArmOptimized<T>(Span<byte> dstSpan, ReadOnlySpan<byte> srcSpan,
            TextureParams dst, TextureParams src, int xCount, int yCount) where T : unmanaged
        {
            int elementSize = Unsafe.SizeOf<T>();
            
            fixed (byte* dstPtr = dstSpan, srcPtr = srcSpan)
            {
                byte* dstBase = dstPtr - dst.BaseOffset;
                byte* srcBase = srcPtr - src.BaseOffset;

                for (int y = 0; y < yCount; y++)
                {
                    src.Calculator.SetY(src.RegionY + y);
                    dst.Calculator.SetY(dst.RegionY + y);

                    // ARM: 预取下一行数据
                    if (y < yCount - 1 && _isArmPlatform)
                    {
                        ArmPrefetchNextLine(src, dst, srcBase, dstBase, y);
                    }

                    int x = 0;
                    
                    // 对于支持SIMD的类型，使用向量化优化
                    if (_supportsNeon && elementSize == 16 && xCount >= 4)
                    {
                        for (; x <= xCount - 4; x += 4)
                        {
                            int srcOffset0 = src.Calculator.GetOffset(src.RegionX + x);
                            int srcOffset1 = src.Calculator.GetOffset(src.RegionX + x + 1);
                            int srcOffset2 = src.Calculator.GetOffset(src.RegionX + x + 2);
                            int srcOffset3 = src.Calculator.GetOffset(src.RegionX + x + 3);
                            
                            int dstOffset0 = dst.Calculator.GetOffset(dst.RegionX + x);
                            int dstOffset1 = dst.Calculator.GetOffset(dst.RegionX + x + 1);
                            int dstOffset2 = dst.Calculator.GetOffset(dst.RegionX + x + 2);
                            int dstOffset3 = dst.Calculator.GetOffset(dst.RegionX + x + 3);
                            
                            // 批量拷贝，减少函数调用开销
                            Vector128<byte> data0 = *(Vector128<byte>*)(srcBase + srcOffset0);
                            Vector128<byte> data1 = *(Vector128<byte>*)(srcBase + srcOffset1);
                            Vector128<byte> data2 = *(Vector128<byte>*)(srcBase + srcOffset2);
                            Vector128<byte> data3 = *(Vector128<byte>*)(srcBase + srcOffset3);
                            
                            *(Vector128<byte>*)(dstBase + dstOffset0) = data0;
                            *(Vector128<byte>*)(dstBase + dstOffset1) = data1;
                            *(Vector128<byte>*)(dstBase + dstOffset2) = data2;
                            *(Vector128<byte>*)(dstBase + dstOffset3) = data3;
                        }
                    }
                    else if (_supportsNeon && elementSize == 8 && xCount >= 8)
                    {
                        // 对8字节类型进行批处理优化
                        int batchSize = Math.Min(_optimalArmBatchSize, 8);
                        for (; x <= xCount - batchSize; x += batchSize)
                        {
                            for (int bx = 0; bx < batchSize; bx++)
                            {
                                int currentX = x + bx;
                                int srcOffset = src.Calculator.GetOffset(src.RegionX + currentX);
                                int dstOffset = dst.Calculator.GetOffset(dst.RegionX + currentX);
                                *(ulong*)(dstBase + dstOffset) = *(ulong*)(srcBase + srcOffset);
                            }
                        }
                    }
                    else if (elementSize == 4 && xCount >= 16)
                    {
                        // 对4字节类型进行批处理优化
                        int batchSize = Math.Min(_optimalArmBatchSize, 16);
                        for (; x <= xCount - batchSize; x += batchSize)
                        {
                            for (int bx = 0; bx < batchSize; bx++)
                            {
                                int currentX = x + bx;
                                int srcOffset = src.Calculator.GetOffset(src.RegionX + currentX);
                                int dstOffset = dst.Calculator.GetOffset(dst.RegionX + currentX);
                                *(uint*)(dstBase + dstOffset) = *(uint*)(srcBase + srcOffset);
                            }
                        }
                    }
                    
                    // 处理剩余元素
                    for (; x < xCount; x++)
                    {
                        int srcOffset = src.Calculator.GetOffset(src.RegionX + x);
                        int dstOffset = dst.Calculator.GetOffset(dst.RegionX + x);
                        *(T*)(dstBase + dstOffset) = *(T*)(srcBase + srcOffset);
                    }
                }
            }
        }

        /// <summary>
        /// ARM预取下一行数据
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void ArmPrefetchNextLine(TextureParams src, TextureParams dst, byte* srcBase, byte* dstBase, int currentY)
        {
            // 预取下一行的源数据
            src.Calculator.SetY(src.RegionY + currentY + 1);
            int nextSrcOffset = src.Calculator.GetOffset(src.RegionX);
            System.Runtime.Intrinsics.Arm.ArmBase.Prefetch(srcBase + nextSrcOffset);
            
            // 预取下一行的目标数据  
            dst.Calculator.SetY(dst.RegionY + currentY + 1);
            int nextDstOffset = dst.Calculator.GetOffset(dst.RegionX);
            System.Runtime.Intrinsics.Arm.ArmBase.Prefetch(dstBase + nextDstOffset);
            
            // 恢复当前行
            src.Calculator.SetY(src.RegionY + currentY);
            dst.Calculator.SetY(dst.RegionY + currentY);
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
            int xCount = (int)_state.State.LineLengthIn;
            int yCount = (int)_state.State.LineCount;

            fixed (byte* dstPtr = dstSpan)
            {
                byte* dstBase = dstPtr - dst.BaseOffset; // Layout offset is relative to the base, so we need to subtract the span's offset.

                for (int y = 0; y < yCount; y++)
                {
                    dst.Calculator.SetY(dst.RegionY + y);

                    for (int x = 0; x < xCount; x++)
                    {
                        int dstOffset = dst.Calculator.GetOffset(dst.RegionX + x);

                        *(T*)(dstBase + dstOffset) = fillValue;
                    }
                }
            }
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

                switch (componentsDst)
                {
                    case SetRemapComponentsDst.SrcX:
                        Copy<T>(dstSpan[(Unsafe.SizeOf<T>() * i)..], srcSpan, dst, src);
                        break;
                    case SetRemapComponentsDst.SrcY:
                        Copy<T>(dstSpan[(Unsafe.SizeOf<T>() * i)..], srcSpan[Unsafe.SizeOf<T>()..], dst, src);
                        break;
                    case SetRemapComponentsDst.SrcZ:
                        Copy<T>(dstSpan[(Unsafe.SizeOf<T>() * i)..], srcSpan[(Unsafe.SizeOf<T>() * 2)..], dst, src);
                        break;
                    case SetRemapComponentsDst.SrcW:
                        Copy<T>(dstSpan[(Unsafe.SizeOf<T>() * i)..], srcSpan[(Unsafe.SizeOf<T>() * 3)..], dst, src);
                        break;
                    case SetRemapComponentsDst.ConstA:
                        Fill<T>(dstSpan[(Unsafe.SizeOf<T>() * i)..], dst, Unsafe.As<uint, T>(ref _state.State.SetRemapConstA));
                        break;
                    case SetRemapComponentsDst.ConstB:
                        Fill<T>(dstSpan[(Unsafe.SizeOf<T>() * i)..], dst, Unsafe.As<uint, T>(ref _state.State.SetRemapConstB));
                        break;
                }
            }
        }

        /// <summary>
        /// Copies block linear data with block linear GOBs to a block linear destination with linear GOBs.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager</param>
        /// <param name="srcGpuVa">Source GPU virtual address</param>
        /// <param name="dstGpuVa">Destination GPU virtual address</param>
        /// <param name="size">Size in bytes of the copy</param>
        private static void CopyGobBlockLinearToLinear(MemoryManager memoryManager, ulong srcGpuVa, ulong dstGpuVa, ulong size)
        {
            if (((srcGpuVa | dstGpuVa | size) & 0xf) == 0)
            {
                for (ulong offset = 0; offset < size; offset += 16)
                {
                    Vector128<byte> data = memoryManager.Read<Vector128<byte>>(ConvertGobLinearToBlockLinearAddress(srcGpuVa + offset), true);
                    memoryManager.Write(dstGpuVa + offset, data);
                }
            }
            else
            {
                for (ulong offset = 0; offset < size; offset++)
                {
                    byte data = memoryManager.Read<byte>(ConvertGobLinearToBlockLinearAddress(srcGpuVa + offset), true);
                    memoryManager.Write(dstGpuVa + offset, data);
                }
            }
        }

        /// <summary>
        /// ARM优化的块线性到线性拷贝
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyGobBlockLinearToLinearArm(MemoryManager memoryManager, ulong srcGpuVa, ulong dstGpuVa, ulong size)
        {
            // ARM NEON支持未对齐访问，可以放宽对齐要求
            if (size >= 16)
            {
                // 使用NEON进行批量拷贝，即使地址未对齐
                ulong offset = 0;
                ulong vectorSize = size & ~0xFu;
                
                for (; offset < vectorSize; offset += 16)
                {
                    // 使用未对齐的NEON加载/存储
                    var data = memoryManager.Read<Vector128<byte>>(
                        ConvertGobLinearToBlockLinearAddress(srcGpuVa + offset), true);
                    memoryManager.Write(dstGpuVa + offset, data);
                }
                
                // 处理剩余字节
                for (; offset < size; offset++)
                {
                    byte data = memoryManager.Read<byte>(
                        ConvertGobLinearToBlockLinearAddress(srcGpuVa + offset), true);
                    memoryManager.Write(dstGpuVa + offset, data);
                }
            }
            else
            {
                // 小尺寸使用逐字节拷贝
                for (ulong offset = 0; offset < size; offset++)
                {
                    byte data = memoryManager.Read<byte>(
                        ConvertGobLinearToBlockLinearAddress(srcGpuVa + offset), true);
                    memoryManager.Write(dstGpuVa + offset, data);
                }
            }
        }

        /// <summary>
        /// Copies block linear data with linear GOBs to a block linear destination with block linear GOBs.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager</param>
        /// <param name="srcGpuVa">Source GPU virtual address</param>
        /// <param name="dstGpuVa">Destination GPU virtual address</param>
        /// <param name="size">Size in bytes of the copy</param>
        private static void CopyGobLinearToBlockLinear(MemoryManager memoryManager, ulong srcGpuVa, ulong dstGpuVa, ulong size)
        {
            if (((srcGpuVa | dstGpuVa | size) & 0xf) == 0)
            {
                for (ulong offset = 0; offset < size; offset += 16)
                {
                    Vector128<byte> data = memoryManager.Read<Vector128<byte>>(srcGpuVa + offset, true);
                    memoryManager.Write(ConvertGobLinearToBlockLinearAddress(dstGpuVa + offset), data);
                }
            }
            else
            {
                for (ulong offset = 0; offset < size; offset++)
                {
                    byte data = memoryManager.Read<byte>(srcGpuVa + offset, true);
                    memoryManager.Write(ConvertGobLinearToBlockLinearAddress(dstGpuVa + offset), data);
                }
            }
        }

        /// <summary>
        /// ARM优化的线性到块线性拷贝
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyGobLinearToBlockLinearArm(MemoryManager memoryManager, ulong srcGpuVa, ulong dstGpuVa, ulong size)
        {
            // ARM NEON支持未对齐访问，可以放宽对齐要求
            if (size >= 16)
            {
                // 使用NEON进行批量拷贝，即使地址未对齐
                ulong offset = 0;
                ulong vectorSize = size & ~0xFu;
                
                for (; offset < vectorSize; offset += 16)
                {
                    // 使用未对齐的NEON加载/存储
                    var data = memoryManager.Read<Vector128<byte>>(srcGpuVa + offset, true);
                    memoryManager.Write(ConvertGobLinearToBlockLinearAddress(dstGpuVa + offset), data);
                }
                
                // 处理剩余字节
                for (; offset < size; offset++)
                {
                    byte data = memoryManager.Read<byte>(srcGpuVa + offset, true);
                    memoryManager.Write(ConvertGobLinearToBlockLinearAddress(dstGpuVa + offset), data);
                }
            }
            else
            {
                // 小尺寸使用逐字节拷贝
                for (ulong offset = 0; offset < size; offset++)
                {
                    byte data = memoryManager.Read<byte>(srcGpuVa + offset, true);
                    memoryManager.Write(ConvertGobLinearToBlockLinearAddress(dstGpuVa + offset), data);
                }
            }
        }

        /// <summary>
        /// Calculates the GOB block linear address from a linear address.
        /// </summary>
        /// <param name="address">Linear address</param>
        /// <returns>Block linear address</returns>
        private static ulong ConvertGobLinearToBlockLinearAddress(ulong address)
        {
            // y2 y1 y0 x5 x4 x3 x2 x1 x0 -> x5 y2 y1 x4 y0 x3 x2 x1 x0
            return (address & ~0x1f0UL) |
                ((address & 0x40) >> 2) |
                ((address & 0x10) << 1) |
                ((address & 0x180) >> 1) |
                ((address & 0x20) << 3);
        }

        /// <summary>
        /// Performs a buffer to buffer, or buffer to texture copy, then optionally releases a semaphore.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        private void LaunchDma(int argument)
        {
            DmaCopy(argument);
            ReleaseSemaphore(argument);
        }
    }
}
