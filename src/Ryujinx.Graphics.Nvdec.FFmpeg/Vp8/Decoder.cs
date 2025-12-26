using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Vp8
{
    public sealed class Decoder : HardwareDecoder, IDecoder
    {
        public override bool IsHardwareAccelerated => _context?.HasHardwareAcceleration ?? false;

        private readonly FFmpegContext _fallbackContext;
        private bool _useFallback;

        public Decoder() : base(AVCodecID.AV_CODEC_ID_VP8, HardwareAccelerationMode.Auto)
        {
            // 创建备用软件解码器
            _fallbackContext = new FFmpegContext(AVCodecID.AV_CODEC_ID_VP8, HardwareAccelerationMode.Software);
        }

        protected override AVCodecID GetCodecId() => AVCodecID.AV_CODEC_ID_VP8;

        public bool Decode(ref Vp8PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            Surface outSurf = (Surface)output;

            // 重建 VP8 帧头
            int uncompHeaderSize = pictureInfo.KeyFrame ? 10 : 3;
            byte[] frame = new byte[bitstream.Length + uncompHeaderSize];

            uint firstPartSizeShifted = pictureInfo.FirstPartSize << 5;

            frame[0] = (byte)(pictureInfo.KeyFrame ? 0 : 1);
            frame[0] |= (byte)((pictureInfo.Version & 7) << 1);
            frame[0] |= 1 << 4;
            frame[0] |= (byte)firstPartSizeShifted;
            frame[1] |= (byte)(firstPartSizeShifted >> 8);
            frame[2] |= (byte)(firstPartSizeShifted >> 16);

            if (pictureInfo.KeyFrame)
            {
                frame[3] = 0x9d;
                frame[4] = 0x01;
                frame[5] = 0x2a;
                frame[6] = (byte)pictureInfo.FrameWidth;
                frame[7] = (byte)((pictureInfo.FrameWidth >> 8) & 0x3F);
                frame[8] = (byte)pictureInfo.FrameHeight;
                frame[9] = (byte)((pictureInfo.FrameHeight >> 8) & 0x3F);
            }

            bitstream.CopyTo(new Span<byte>(frame)[uncompHeaderSize..]);

            // 尝试硬件解码，如果失败则使用软件解码
            if (!_useFallback)
            {
                if (_context?.DecodeFrame(outSurf, frame) == 0)
                {
                    return true;
                }
                
                // 硬件解码失败，切换到软件解码
                _useFallback = true;
            }

            // 使用软件解码
            return _fallbackContext?.DecodeFrame(outSurf, frame) == 0;
        }

        public override void Dispose()
        {
            base.Dispose();
            _fallbackContext?.Dispose();
        }
    }
}
