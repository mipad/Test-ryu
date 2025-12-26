using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        public bool IsHardwareAccelerated => false;

        private const int WorkBufferSize = 0x200;

        private readonly byte[] _workBuffer = new byte[WorkBufferSize];

        private FFmpegContext _context = new(AVCodecID.AV_CODEC_ID_H264);

        private int _oldOutputWidth;
        private int _oldOutputHeight;

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            Surface outSurf = (Surface)output;

            // 检查是否需要重新创建解码器上下文（如果分辨率改变）
            if (outSurf.RequestedWidth != _oldOutputWidth ||
                outSurf.RequestedHeight != _oldOutputHeight)
            {
                _context.Dispose();
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);

                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }

            // 重建SPS和PPS并添加到比特流前面
            Span<byte> reconstructedSpsPps = SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer);
            
            // 创建包含SPS/PPS和原始比特流的完整数据
            byte[] completeBitstream = new byte[reconstructedSpsPps.Length + bitstream.Length];
            reconstructedSpsPps.CopyTo(completeBitstream);
            bitstream.CopyTo(new Span<byte>(completeBitstream)[reconstructedSpsPps.Length..]);

            // 解码帧
            return _context.DecodeFrame(outSurf, completeBitstream) == 0;
        }

        public void Dispose() => _context.Dispose();
    }
}
