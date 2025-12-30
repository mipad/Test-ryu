using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        // 我们专注于硬件解码，所以这里返回true
        public bool IsHardwareAccelerated => true;

        private const int WorkBufferSize = 0x200;
        private readonly byte[] _workBuffer = new byte[WorkBufferSize];
        private FFmpegContext _context;
        private int _oldOutputWidth;
        private int _oldOutputHeight;
        
        // 硬件解码器名称常量
        private const string H264MediaCodecDecoder = "h264_mediacodec";

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            Surface outSurf = (Surface)output;

            try
            {
                // 如果分辨率变化或者上下文未初始化，重新创建解码器上下文
                if (_context == null || 
                    outSurf.RequestedWidth != _oldOutputWidth ||
                    outSurf.RequestedHeight != _oldOutputHeight)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                        $"{( _context == null ? "Creating" : "Recreating")} hardware decoder context. " +
                        $"Resolution: {outSurf.RequestedWidth}x{outSurf.RequestedHeight}");
                    
                    _context?.Dispose();
                    
                    // 明确使用h264_mediacodec硬件解码器
                    _context = new FFmpegContext(H264MediaCodecDecoder);

                    _oldOutputWidth = outSurf.RequestedWidth;
                    _oldOutputHeight = outSurf.RequestedHeight;
                }

                Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));
                
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                    $"Starting hardware decode. Bitstream size: {bs.Length}, " +
                    $"Output surface: {outSurf.RequestedWidth}x{outSurf.RequestedHeight}");
                
                int result = _context.DecodeFrame(outSurf, bs);
                
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Hardware decode result: {result}");
                
                return result == 0;
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                    $"Hardware decode failed: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static byte[] Prepend(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prep)
        {
            byte[] output = new byte[data.Length + prep.Length];

            prep.CopyTo(output);
            data.CopyTo(new Span<byte>(output)[prep.Length..]);

            return output;
        }

        public void Dispose() => _context?.Dispose();
    }
}
