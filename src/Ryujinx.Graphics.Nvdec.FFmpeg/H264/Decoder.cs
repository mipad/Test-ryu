using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using Ryujinx.Common.Logging;

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
        private bool _forceSoftwareDecoding = false;
        private int _decodeFailures = 0;
        private const int MaxDecodeFailures = 10;

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            Surface outSurf = (Surface)output;

            // 检查解码器是否初始化成功
            if (!_context.IsInitialized)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Decoder context not initialized properly");
                return false;
            }

            // 检查是否需要重新初始化（只有在分辨率真正变化且解码器已初始化时才重新初始化）
            bool resolutionChanged = outSurf.RequestedWidth != _oldOutputWidth || 
                                   outSurf.RequestedHeight != _oldOutputHeight;

            if (resolutionChanged && _oldOutputWidth != 0 && _oldOutputHeight != 0)
            {
                Logger.Info?.Print(LogClass.FFmpeg, 
                    $"Resolution changed: {_oldOutputWidth}x{_oldOutputHeight} -> {outSurf.RequestedWidth}x{outSurf.RequestedHeight}");
                
                // 只有在连续解码失败时才重新创建解码器
                if (_decodeFailures > MaxDecodeFailures / 2)
                {
                    _context.Dispose();
                    _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264, !_forceSoftwareDecoding);
                    _decodeFailures = 0;
                    Logger.Info?.Print(LogClass.FFmpeg, "Recreated decoder due to resolution change and previous failures");
                }

                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }
            else if (_oldOutputWidth == 0 && _oldOutputHeight == 0)
            {
                // 首次设置分辨率
                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }

            // 如果硬件解码连续失败，强制使用软件解码
            if (_context.ConsecutiveErrors >= 5 && _context.IsHardwareDecoder && !_forceSoftwareDecoding)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, "Too many hardware decoding errors, switching to software decoding");
                _forceSoftwareDecoding = true;
                _context.Dispose();
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264, false);
                _decodeFailures = 0;
            }

            Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));

            bool decodeResult = _context.DecodeFrame(outSurf, bs) == 0;

            if (!decodeResult)
            {
                _decodeFailures++;
                Logger.Warning?.Print(LogClass.FFmpeg, $"Decode failed, consecutive failures: {_decodeFailures}");

                // 如果连续失败次数过多，强制刷新解码器
                if (_decodeFailures >= 3)
                {
                    _context.ForceFlush();
                    _decodeFailures = 0;
                    Logger.Info?.Print(LogClass.FFmpeg, "Forced decoder flush after multiple failures");
                }

                // 尝试重新解码一次（使用刷新后的状态）
                if (_decodeFailures < 2)
                {
                    decodeResult = _context.DecodeFrame(outSurf, bs) == 0;
                    if (decodeResult)
                    {
                        Logger.Debug?.Print(LogClass.FFmpeg, "Recovery decode successful after flush");
                        _decodeFailures = 0;
                    }
                }
            }
            else
            {
                _decodeFailures = 0; // 重置失败计数
            }

            return decodeResult;
        }

        private static byte[] Prepend(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prep)
        {
            byte[] output = new byte[data.Length + prep.Length];

            prep.CopyTo(output);
            data.CopyTo(new Span<byte>(output)[prep.Length..]);

            return output;
        }

        public void Dispose() 
        {
            _context?.Dispose();
        }

        // 新增方法：获取解码器状态信息
        public string GetDecoderStatus()
        {
            if (_context == null) return "Decoder not initialized";
            
            var stats = _context.GetPerformanceStats();
            return $"Decoder: {_context.DecoderType} ({_context.CodecName}), " +
                   $"Frames: {stats.frameCount}, " +
                   $"Avg Time: {stats.averageTime:F2}ms, " +
                   $"FPS: {stats.fps:F1}, " +
                   $"Failures: {_decodeFailures}, " +
                   $"Errors: {_context.ConsecutiveErrors}";
        }

        // 新增方法：强制使用软件解码
        public void ForceSoftwareDecoder()
        {
            if (!_forceSoftwareDecoding)
            {
                _forceSoftwareDecoding = true;
                _context.Dispose();
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264, false);
                Logger.Info?.Print(LogClass.FFmpeg, "Forced software decoder usage");
            }
        }
    }
}
