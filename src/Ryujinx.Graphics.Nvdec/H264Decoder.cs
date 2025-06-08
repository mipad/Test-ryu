using Ryujinx.Graphics.Nvdec.FFmpeg.H264;
using Ryujinx.Graphics.Nvdec.Image; // 包含 DeviceMemoryManager
using Ryujinx.Graphics.Nvdec.Types.H264;
using Ryujinx.Graphics.Video;
using System;
using System.Diagnostics;

namespace Ryujinx.Graphics.Nvdec
{
    static class H264Decoder
    {
        private const int MbSizeInPixels = 16;
        private const long TimeoutThresholdMs = 100; // 100ms超时阈值

        // 使用 DeviceMemoryManager 类型
        private static void FillWithZeros(DeviceMemoryManager memoryManager, ulong offset, uint size)
        {
            const int BufferSize = 0x1000; // 4KB 缓冲区
            byte[] zeroBuffer = new byte[BufferSize]; // 自动初始化为0
            
            uint remaining = size;
            while (remaining > 0)
            {
                uint chunkSize = Math.Min(remaining, (uint)BufferSize);
                memoryManager.Write(offset, zeroBuffer.AsSpan(0, (int)chunkSize));
                offset += chunkSize;
                remaining -= chunkSize;
            }
        }

        public static void Decode(NvdecDecoderContext context, ResourceManager rm, ref NvdecRegisters state)
        {
            PictureInfo pictureInfo = rm.MemoryManager.DeviceRead<PictureInfo>(state.SetDrvPicSetupOffset);
            H264PictureInfo info = pictureInfo.Convert();

            ReadOnlySpan<byte> bitstream = rm.MemoryManager.DeviceGetSpan(state.SetInBufBaseOffset, (int)pictureInfo.BitstreamSize);

            int width = (int)pictureInfo.PicWidthInMbs * MbSizeInPixels;
            int height = (int)pictureInfo.PicHeightInMbs * MbSizeInPixels;

            int surfaceIndex = (int)pictureInfo.OutputSurfaceIndex;

            ulong lumaOffset = state.SetPictureLumaOffset[surfaceIndex];
            ulong chromaOffset = state.SetPictureChromaOffset[surfaceIndex];
            
            // 计算亮度和色度平面大小
            uint lumaSize = (uint)(width * height);
            uint chromaSize = (uint)(width * height / 2);

            Decoder decoder = context.GetH264Decoder();

            ISurface outputSurface = rm.Cache.Get(decoder, 0, 0, width, height);

            // 添加超时处理
            bool decodeSuccess = false;
            var stopwatch = Stopwatch.StartNew();
            
            while (!decodeSuccess && stopwatch.ElapsedMilliseconds < TimeoutThresholdMs)
            {
                try
                {
                    decodeSuccess = decoder.Decode(ref info, outputSurface, bitstream);
                }
                catch
                {
                    // 忽略解码过程中的异常，继续尝试
                }
            }

            if (decodeSuccess)
            {
                if (outputSurface.Field == FrameField.Progressive)
                {
                    SurfaceWriter.Write(
                        rm.MemoryManager,
                        outputSurface,
                        lumaOffset + (ulong)pictureInfo.LumaFrameOffset,
                        chromaOffset + (ulong)pictureInfo.ChromaFrameOffset);
                }
                else
                {
                    SurfaceWriter.WriteInterlaced(
                        rm.MemoryManager,
                        outputSurface,
                        lumaOffset + (ulong)pictureInfo.LumaTopFieldOffset,
                        chromaOffset + (ulong)pictureInfo.ChromaTopFieldOffset,
                        lumaOffset + (ulong)pictureInfo.LumaBottomFieldOffset,
                        chromaOffset + (ulong)pictureInfo.ChromaBottomFieldOffset);
                }
            }
            else
            {
                // 超时后安全处理
                FillWithZeros(rm.MemoryManager, lumaOffset, lumaSize);
                FillWithZeros(rm.MemoryManager, chromaOffset, chromaSize);
            }
            
            rm.Cache.Put(outputSurface);
        }
    }
}
