using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Vp8
{
    public sealed class Decoder : IDecoder
    {
        public bool IsHardwareAccelerated => true;

        private const int WorkBufferSize = 0x200;
        private readonly byte[] _workBuffer = new byte[WorkBufferSize];
        private FFmpegContext _context;
        private int _oldOutputWidth;
        private int _oldOutputHeight;
        
        // 硬件解码器名称常量
        private const string VP8MediaCodecDecoder = "vp8_mediacodec";

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref Vp8PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
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
                    
                    // 明确使用vp8_mediacodec硬件解码器
                    _context = new FFmpegContext(VP8MediaCodecDecoder);

                    _oldOutputWidth = outSurf.RequestedWidth;
                    _oldOutputHeight = outSurf.RequestedHeight;
                }

                Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                    $"Starting hardware decode. Bitstream size: {bitstream.Length}, " +
                    $"Output surface: {outSurf.RequestedWidth}x{outSurf.RequestedHeight}");
                
                int result = _context.DecodeFrame(outSurf, bitstream);
                
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

        public void Dispose() => _context?.Dispose();
    }
}
