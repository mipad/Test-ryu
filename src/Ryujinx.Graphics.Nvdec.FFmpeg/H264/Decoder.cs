using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        public bool IsHardwareAccelerated => true;

        private const int WorkBufferSize = 0x200;

        private readonly byte[] _workBuffer = new byte[WorkBufferSize];

        private FFmpegContext _context;
        private int _oldOutputWidth;
        private int _oldOutputHeight;
        
        private int _hardwareDecodeFailures = 0;
        private const int MaxHardwareFailures = 1;
        private bool _forceSoftwareDecode = false;

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            Surface outSurf = (Surface)output;

            // 检查是否需要重新创建编解码器上下文
            if (_context == null || 
                outSurf.RequestedWidth != _oldOutputWidth ||
                outSurf.RequestedHeight != _oldOutputHeight)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Resolution changed from {_oldOutputWidth}x{_oldOutputHeight} to {outSurf.RequestedWidth}x{outSurf.RequestedHeight}. Creating new FFmpegContext.");
                
                if (_context != null)
                {
                    _context.Dispose();
                }
                
                // 传递分辨率参数给FFmpegContext构造函数
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264, outSurf.RequestedWidth, outSurf.RequestedHeight);

                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }

            // 检查是否需要强制软件解码
            if (_hardwareDecodeFailures >= MaxHardwareFailures && !_forceSoftwareDecode)
            {
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, 
                    $"Hardware decode failed {_hardwareDecodeFailures} times, forcing software decode");
                _forceSoftwareDecode = true;
                
                _context.Dispose();
                Environment.SetEnvironmentVariable("RYUJINX_FORCE_SOFTWARE_DECODE", "1");
                
                // 重新创建编解码器上下文，使用软件解码
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264, outSurf.RequestedWidth, outSurf.RequestedHeight);
            }

            // 准备解码数据
            Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                $"Starting decode. Bitstream size: {bs.Length}, Output surface: {outSurf.RequestedWidth}x{outSurf.RequestedHeight}");
            
            // 执行解码
            int result = _context.DecodeFrame(outSurf, bs);
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Decode result: {result}");
            
            if (result == 0)
            {
                // 解码成功，重置失败计数
                if (_hardwareDecodeFailures > 0)
                {
                    _hardwareDecodeFailures = 0;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware decode recovered, resetting failure count");
                }
                return true;
            }
            else
            {
                // 解码失败
                if (!_forceSoftwareDecode)
                {
                    _hardwareDecodeFailures++;
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, 
                        $"Hardware decode failure {_hardwareDecodeFailures}/{MaxHardwareFailures}");
                    
                    // 如果硬件解码失败次数达到上限，下次将回退到软件解码
                    if (_hardwareDecodeFailures >= MaxHardwareFailures)
                    {
                        Logger.Warning?.PrintMsg(LogClass.FFmpeg, 
                            "Too many hardware decode failures, will try software decode next frame");
                    }
                }
                else
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Software decode failure");
                }
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

        public void Dispose()
        {
            _context?.Dispose();
        }
    }
}
