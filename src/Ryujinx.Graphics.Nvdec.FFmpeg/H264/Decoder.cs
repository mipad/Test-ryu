using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        public bool IsHardwareAccelerated => false;

        private const int WorkBufferSize = 0x200;
        private const int MaxBitrate = 10000000; // 10 Mbps
        private const int MinBitrate = 1000000;  // 1 Mbps

        private readonly byte[] _workBuffer = new byte[WorkBufferSize];

        private FFmpegContext _context = new(AVCodecID.AV_CODEC_ID_H264);

        private int _oldOutputWidth;
        private int _oldOutputHeight;
        
        // 码率自适应
        private int _currentBitrate = 5000000; // 初始5 Mbps
        private int _consecutiveSuccess = 0;
        private int _consecutiveFailures = 0;

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            Surface outSurf = (Surface)output;

            // 检查分辨率变化
            if (outSurf.RequestedWidth != _oldOutputWidth ||
                outSurf.RequestedHeight != _oldOutputHeight)
            {
                _context.Dispose();
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
                
                // 根据分辨率调整码率
                int pixelCount = outSurf.RequestedWidth * outSurf.RequestedHeight;
                _currentBitrate = Math.Min(MaxBitrate, Math.Max(MinBitrate, pixelCount * 10));
                
                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
                _consecutiveSuccess = 0;
                _consecutiveFailures = 0;
            }

            // 重建SPS和PPS
            Span<byte> reconstructedSpsPps = SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer);
            
            // 创建完整比特流
            byte[] completeBitstream = new byte[reconstructedSpsPps.Length + bitstream.Length];
            reconstructedSpsPps.CopyTo(completeBitstream);
            bitstream.CopyTo(new Span<byte>(completeBitstream)[reconstructedSpsPps.Length..]);

            // 解码帧
            int result = _context.DecodeFrame(outSurf, completeBitstream);
            
            if (result == 0)
            {
                _consecutiveSuccess++;
                _consecutiveFailures = 0;
                
                // 如果连续成功，可以尝试稍微降低码率（如果当前码率较高）
                if (_consecutiveSuccess > 30 && _currentBitrate > MinBitrate + 1000000)
                {
                    _currentBitrate -= 100000; // 降低100kbps
                    _consecutiveSuccess = 0;
                }
                
                return true;
            }
            else
            {
                _consecutiveFailures++;
                _consecutiveSuccess = 0;
                
                // 如果连续失败，增加码率
                if (_consecutiveFailures > 5 && _currentBitrate < MaxBitrate)
                {
                    _currentBitrate += 500000; // 增加500kbps
                    _consecutiveFailures = 0;
                    
                    // 重新创建解码器上下文
                    _context.Dispose();
                    _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
                }
                else if (_consecutiveFailures > 20)
                {
                    // 严重失败，完全重置
                    _context.Dispose();
                    _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
                    _consecutiveFailures = 0;
                }
                
                return false;
            }
        }

        public void Dispose() => _context.Dispose();
    }
}
