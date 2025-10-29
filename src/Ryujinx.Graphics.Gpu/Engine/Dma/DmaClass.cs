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
using System.Runtime.Intrinsics.Arm;
using System.Buffers;
using System.Threading.Tasks;

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

        // ARM优化配置
        private static readonly bool _useNeon = AdvSimd.IsSupported;
        private static readonly bool _useParallel = Environment.ProcessorCount >= 4;
        private const int MemoryPoolThreshold = 1024 * 1024; // 1MB阈值
        private const int ParallelThreshold = 256 * 256; // 降低并行阈值

        /// <summary>
        /// 并行工作项
        /// </summary>
        private struct ParallelWorkItem
        {
            public int StartY;
            public int EndY;
            public TextureParams SrcParams;
            public TextureParams DstParams;
            public int XCount;
            public int Bpp;
        }

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
            public readonly int RegionX;
            public readonly int RegionY;
            public readonly int BaseOffset;
            public readonly int Bpp;
            public readonly bool Linear;
            public readonly OffsetCalculator Calculator;

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
        public int Read(int offset) => _state.Read(offset);

        /// <summary>
        /// Writes data to the class registers.
        /// </summary>
        public void Write(int offset, int data) => _state.Write(offset, data);

        /// <summary>
        /// Determine if a buffer-to-texture region covers the entirety of a texture.
        /// </summary>
        private static bool IsTextureCopyComplete(DmaTexture tex, bool linear, int bpp, int stride, int xCount, int yCount)
        {
            if (linear)
            {
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
                else
                {
                    _channel.MemoryManager.Write(address + 8, _context.GetTimestamp());
                    _channel.MemoryManager.Write(address, (ulong)_state.State.SetSemaphorePayload);
                }
            }
        }

        /// <summary>
        /// Performs a buffer to buffer, or buffer to texture copy.
        /// </summary>
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

                bool isIdentityRemap = !remap ||
                    (_state.State.SetRemapComponentsDstX == SetRemapComponentsDst.SrcX &&
                    (dstComponents < 2 || _state.State.SetRemapComponentsDstY == SetRemapComponentsDst.SrcY) &&
                    (dstComponents < 3 || _state.State.SetRemapComponentsDstZ == SetRemapComponentsDst.SrcZ) &&
                    (dstComponents < 4 || _state.State.SetRemapComponentsDstW == SetRemapComponentsDst.SrcW));

                bool completeSource = IsTextureCopyComplete(src, srcLinear, srcBpp, srcStride, xCount, yCount);
                bool completeDest = IsTextureCopyComplete(dst, dstLinear, dstBpp, dstStride, xCount, yCount);

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
                        return;
                    }
                }

                ReadOnlySpan<byte> srcSpan = memoryManager.GetSpan(srcGpuVa + (ulong)srcBaseOffset, srcSize, true);

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

                        target.SynchronizeMemory();
                        target.SetData(data);
                        target.SignalModified();
                        return;
                    }
                    else if (srcCalculator.LayoutMatches(dstCalculator))
                    {
                        memoryManager.Write(dstGpuVa + (ulong)dstBaseOffset, srcSpan);
                        return;
                    }
                }

                // 内存管理：只对大数据使用ArrayPool
                byte[] dstArray = null;
                byte[] srcArray = null;
                Span<byte> dstSpan;

                if (dstSize > MemoryPoolThreshold)
                {
                    dstArray = ArrayPool<byte>.Shared.Rent(dstSize);
                    dstSpan = dstArray.AsSpan(0, dstSize);
                }
                else
                {
                    dstSpan = new byte[dstSize];
                }

                // 对于并行处理，我们需要将源数据复制到数组
                bool useParallel = _useParallel && (xCount * yCount) > ParallelThreshold && 
                                 srcLinear && dstLinear && srcBpp == dstBpp;
                
                if (useParallel && srcSize > MemoryPoolThreshold)
                {
                    srcArray = ArrayPool<byte>.Shared.Rent(srcSize);
                    srcSpan.CopyTo(srcArray);
                }

                try
                {
                    TextureParams srcParams = new(srcRegionX, srcRegionY, srcBaseOffset, srcBpp, srcLinear, srcCalculator);
                    TextureParams dstParams = new(dstRegionX, dstRegionY, dstBaseOffset, dstBpp, dstLinear, dstCalculator);

                    if (isIdentityRemap)
                    {
                        // 优化：对线性布局的大纹理使用并行处理
                        if (useParallel)
                        {
                            if (srcArray != null)
                            {
                                CopyParallelLinear(dstArray, srcArray, dstParams, srcParams, xCount, yCount, srcBpp);
                            }
                            else
                            {
                                CopyParallelLinear(dstSpan, srcSpan, dstParams, srcParams, xCount, yCount, srcBpp);
                            }
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
                                    if (_useNeon)
                                        CopyNeon(dstSpan, srcSpan, dstParams, srcParams);
                                    else
                                        Copy<Vector128<byte>>(dstSpan, srcSpan, dstParams, srcParams);
                                    break;
                                default:
                                    throw new NotSupportedException($"Unable to copy ${srcBpp} bpp pixel format.");
                            }
                        }
                    }
                    else
                    {
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
                }
                finally
                {
                    if (dstArray != null)
                    {
                        ArrayPool<byte>.Shared.Return(dstArray);
                    }
                    if (srcArray != null)
                    {
                        ArrayPool<byte>.Shared.Return(srcArray);
                    }
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
                    memoryManager.Physical.BufferCache.ClearBuffer(memoryManager, dstGpuVa, size * 4, _state.State.SetRemapConstA);
                }
                else
                {
                    bool srcIsPitchKind = memoryManager.GetKind(srcGpuVa).IsPitch();
                    bool dstIsPitchKind = memoryManager.GetKind(dstGpuVa).IsPitch();

                    if (!srcIsPitchKind && dstIsPitchKind)
                    {
                        CopyGobBlockLinearToLinear(memoryManager, srcGpuVa, dstGpuVa, size);
                    }
                    else if (srcIsPitchKind && !dstIsPitchKind)
                    {
                        CopyGobLinearToBlockLinear(memoryManager, srcGpuVa, dstGpuVa, size);
                    }
                    else
                    {
                        memoryManager.Physical.BufferCache.CopyBuffer(memoryManager, srcGpuVa, dstGpuVa, size);
                    }
                }
            }
        }

        /// <summary>
        /// 优化的并行处理：只对线性布局使用
        /// </summary>
        private void CopyParallelLinear(Span<byte> dstSpan, ReadOnlySpan<byte> srcSpan, TextureParams dst, TextureParams src, int xCount, int yCount, int bpp)
        {
            // 对于线性布局，我们可以简单地进行行并行处理
            int bytesPerLine = xCount * bpp;
            
            Parallel.For(0, yCount, y =>
            {
                int srcOffset = src.Calculator.GetOffset(src.RegionX) + (y * bytesPerLine) - src.BaseOffset;
                int dstOffset = dst.Calculator.GetOffset(dst.RegionX) + (y * bytesPerLine) - dst.BaseOffset;
                
                var sourceLine = srcSpan.Slice(srcOffset, bytesPerLine);
                var destLine = dstSpan.Slice(dstOffset, bytesPerLine);
                
                sourceLine.CopyTo(destLine);
            });
        }

        /// <summary>
        /// 数组版本的并行处理
        /// </summary>
        private void CopyParallelLinear(byte[] dstArray, byte[] srcArray, TextureParams dst, TextureParams src, int xCount, int yCount, int bpp)
        {
            int bytesPerLine = xCount * bpp;
            
            Parallel.For(0, yCount, y =>
            {
                int srcOffset = src.Calculator.GetOffset(src.RegionX) + (y * bytesPerLine) - src.BaseOffset;
                int dstOffset = dst.Calculator.GetOffset(dst.RegionX) + (y * bytesPerLine) - dst.BaseOffset;
                
                Buffer.BlockCopy(srcArray, srcOffset, dstArray, dstOffset, bytesPerLine);
            });
        }

        /// <summary>
        /// ARM优化：使用NEON指令加速16字节拷贝
        /// </summary>
        private unsafe void CopyNeon(Span<byte> dstSpan, ReadOnlySpan<byte> srcSpan, TextureParams dst, TextureParams src)
        {
            int xCount = (int)_state.State.LineLengthIn;
            int yCount = (int)_state.State.LineCount;

            fixed (byte* dstPtr = dstSpan, srcPtr = srcSpan)
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

                        Vector128<byte> data = AdvSimd.LoadVector128(srcBase + srcOffset);
                        AdvSimd.Store(dstBase + dstOffset, data);
                    }
                }
            }
        }

        /// <summary>
        /// Copies data from one texture to another.
        /// </summary>
        private unsafe void Copy<T>(Span<byte> dstSpan, ReadOnlySpan<byte> srcSpan, TextureParams dst, TextureParams src) where T : unmanaged
        {
            int xCount = (int)_state.State.LineLengthIn;
            int yCount = (int)_state.State.LineCount;

            if (src.Linear && dst.Linear && src.Bpp == dst.Bpp)
            {
                // 线性拷贝优化
                for (int y = 0; y < yCount; y++)
                {
                    src.Calculator.SetY(src.RegionY + y);
                    dst.Calculator.SetY(dst.RegionY + y);
                    int srcOffset = src.Calculator.GetOffset(src.RegionX);
                    int dstOffset = dst.Calculator.GetOffset(dst.RegionX);
                    
                    var sourceSlice = srcSpan.Slice(srcOffset - src.BaseOffset, xCount * src.Bpp);
                    var destSlice = dstSpan.Slice(dstOffset - dst.BaseOffset, xCount * dst.Bpp);
                    
                    sourceSlice.CopyTo(destSlice);
                }
            }
            else
            {
                fixed (byte* dstPtr = dstSpan, srcPtr = srcSpan)
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

                            *(T*)(dstBase + dstOffset) = *(T*)(srcBase + srcOffset);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Sets texture pixel data to a constant value.
        /// </summary>
        private unsafe void Fill<T>(Span<byte> dstSpan, TextureParams dst, T fillValue) where T : unmanaged
        {
            int xCount = (int)_state.State.LineLengthIn;
            int yCount = (int)_state.State.LineCount;

            fixed (byte* dstPtr = dstSpan)
            {
                byte* dstBase = dstPtr - dst.BaseOffset;

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
        /// Copies data with component shuffling.
        /// </summary>
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
        /// Copies block linear data to linear.
        /// </summary>
        private static void CopyGobBlockLinearToLinear(MemoryManager memoryManager, ulong srcGpuVa, ulong dstGpuVa, ulong size)
        {
            // 简化：使用批量操作
            const int batchSize = 64;
            
            if (((srcGpuVa | dstGpuVa | size) & 0x3f) == 0)
            {
                for (ulong offset = 0; offset < size; offset += batchSize)
                {
                    ReadOnlySpan<byte> data = memoryManager.GetSpan(ConvertGobLinearToBlockLinearAddress(srcGpuVa + offset), batchSize, true);
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
        /// Copies linear data to block linear.
        /// </summary>
        private static void CopyGobLinearToBlockLinear(MemoryManager memoryManager, ulong srcGpuVa, ulong dstGpuVa, ulong size)
        {
            const int batchSize = 64;
            
            if (((srcGpuVa | dstGpuVa | size) & 0x3f) == 0)
            {
                for (ulong offset = 0; offset < size; offset += batchSize)
                {
                    ReadOnlySpan<byte> data = memoryManager.GetSpan(srcGpuVa + offset, batchSize, true);
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
        /// Calculates the GOB block linear address from a linear address.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ConvertGobLinearToBlockLinearAddress(ulong address)
        {
            return (address & ~0x1f0UL) |
                ((address & 0x40) >> 2) |
                ((address & 0x10) << 1) |
                ((address & 0x180) >> 1) |
                ((address & 0x20) << 3);
        }

        /// <summary>
        /// Performs a buffer to buffer, or buffer to texture copy, then optionally releases a semaphore.
        /// </summary>
        private void LaunchDma(int argument)
        {
            DmaCopy(argument);
            ReleaseSemaphore(argument);
        }
    }
}
