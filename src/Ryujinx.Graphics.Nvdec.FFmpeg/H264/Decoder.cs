using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        public bool IsHardwareAccelerated => true;

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

            if (outSurf.RequestedWidth != _oldOutputWidth ||
                outSurf.RequestedHeight != _oldOutputHeight)
            {
                Ryujinx.Common.Logging.Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Resolution changed from {_oldOutputWidth}x{_oldOutputHeight} to {outSurf.RequestedWidth}x{outSurf.RequestedHeight}. Recreating FFmpegContext.");
                _context.Dispose();
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);

                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }

            Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));
            
            Ryujinx.Common.Logging.Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Starting decode. Bitstream size: {bs.Length}, Output surface: {outSurf.RequestedWidth}x{outSurf.RequestedHeight}");
            
            int result = _context.DecodeFrame(outSurf, bs);
            
            Ryujinx.Common.Logging.Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Decode result: {result}");
            
            return result == 0;
        }

        private static byte[] Prepend(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prep)
        {
            byte[] output = new byte[data.Length + prep.Length];

            prep.CopyTo(output);
            data.CopyTo(new Span<byte>(output)[prep.Length..]);

            return output;
        }

        public void Dispose() => _context.Dispose();
    }
}
