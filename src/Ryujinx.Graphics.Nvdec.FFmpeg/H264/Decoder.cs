using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : HardwareDecoder, IH264Decoder
    {
        public override bool IsHardwareAccelerated => _context?.HasHardwareAcceleration ?? false;

        private const int WorkBufferSize = 0x200;
        private readonly byte[] _workBuffer = new byte[WorkBufferSize];

        private int _oldOutputWidth;
        private int _oldOutputHeight;

        public Decoder() : base(AVCodecID.AV_CODEC_ID_H264, HardwareAccelerationMode.Auto)
        {
        }

        protected override AVCodecID GetCodecId() => AVCodecID.AV_CODEC_ID_H264;

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            Surface outSurf = (Surface)output;

            // 检查尺寸变化，需要重新初始化解码器
            if (outSurf.RequestedWidth != _oldOutputWidth ||
                outSurf.RequestedHeight != _oldOutputHeight)
            {
                Dispose();
                InitializeContext(AVCodecID.AV_CODEC_ID_H264);
                
                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }

            // 重建 SPS/PPS
            Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));

            return DecodeFrame(output, bs);
        }

        private static byte[] Prepend(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prep)
        {
            byte[] output = new byte[data.Length + prep.Length];

            prep.CopyTo(output);
            data.CopyTo(new Span<byte>(output)[prep.Length..]);

            return output;
        }
    }
}
