using Ryujinx.Graphics.Nvdec.FFmpeg.H264;
using Ryujinx.Graphics.Nvdec.Image;
using Ryujinx.Graphics.Nvdec.Types.H264;
using Ryujinx.Graphics.Video;
using System;
using System.Diagnostics; // 添加诊断命名空间

namespace Ryujinx.Graphics.Nvdec
{
    static class H264Decoder
    {
        private const int MbSizeInPixels = 16;

        public static void Decode(NvdecDecoderContext context, ResourceManager rm, ref NvdecRegisters state)
        {
            // 记录开始解码
            Debug.WriteLine($"[H264Decoder.Decode] 开始H.264解码");
            
            PictureInfo pictureInfo = rm.MemoryManager.DeviceRead<PictureInfo>(state.SetDrvPicSetupOffset);
            H264PictureInfo info = pictureInfo.Convert();

            // 记录图片信息
            Debug.WriteLine($"[H264Decoder.Decode] 图片信息: {pictureInfo.PicWidthInMbs}x{pictureInfo.PicHeightInMbs} MBs, " +
                           $"比特流大小: {pictureInfo.BitstreamSize} 字节, " +
                           $"输出表面索引: {pictureInfo.OutputSurfaceIndex}");

            ReadOnlySpan<byte> bitstream = rm.MemoryManager.DeviceGetSpan(state.SetInBufBaseOffset, (int)pictureInfo.BitstreamSize);

            int width = (int)pictureInfo.PicWidthInMbs * MbSizeInPixels;
            int height = (int)pictureInfo.PicHeightInMbs * MbSizeInPixels;

            // 记录分辨率信息
            Debug.WriteLine($"[H264Decoder.Decode] 解码分辨率: {width}x{height}");

            int surfaceIndex = (int)pictureInfo.OutputSurfaceIndex;

            uint lumaOffset = state.SetPictureLumaOffset[surfaceIndex];
            uint chromaOffset = state.SetPictureChromaOffset[surfaceIndex];

            // 记录偏移信息
            Debug.WriteLine($"[H264Decoder.Decode] 表面索引: {surfaceIndex}, " +
                           $"亮度偏移: 0x{lumaOffset:X}, " +
                           $"色度偏移: 0x{chromaOffset:X}");

            Decoder decoder = context.GetH264Decoder();
            
            // 记录解码器类型
            Debug.WriteLine($"[H264Decoder.Decode] 使用的解码器类型: {decoder.GetType().FullName}");

            ISurface outputSurface = rm.Cache.Get(decoder, 0, 0, width, height);

            // 记录解码开始
            Debug.WriteLine($"[H264Decoder.Decode] 开始解码比特流...");
            
            if (decoder.Decode(ref info, outputSurface, bitstream))
            {
                Debug.WriteLine($"[H264Decoder.Decode] 解码成功!");
                
                if (outputSurface.Field == FrameField.Progressive)
                {
                    Debug.WriteLine($"[H264Decoder.Decode] 写入逐行扫描帧");
                    SurfaceWriter.Write(
                        rm.MemoryManager,
                        outputSurface,
                        lumaOffset + pictureInfo.LumaFrameOffset,
                        chromaOffset + pictureInfo.ChromaFrameOffset);
                }
                else
                {
                    Debug.WriteLine($"[H264Decoder.Decode] 写入隔行扫描帧");
                    SurfaceWriter.WriteInterlaced(
                        rm.MemoryManager,
                        outputSurface,
                        lumaOffset + pictureInfo.LumaTopFieldOffset,
                        chromaOffset + pictureInfo.ChromaTopFieldOffset,
                        lumaOffset + pictureInfo.LumaBottomFieldOffset,
                        chromaOffset + pictureInfo.ChromaBottomFieldOffset);
                }
            }
            else
            {
                Debug.WriteLine($"[H264Decoder.Decode] 解码失败!");
            }

            rm.Cache.Put(outputSurface);
            Debug.WriteLine($"[H264Decoder.Decode] 解码完成，表面已放回缓存");
        }
    }
}
