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

        private FFmpegContext _context = new(AVCodecID.AV_CODEC_ID_H264);

        private int _oldOutputWidth;
        private int _oldOutputHeight;
        
        // 失败计数器，用于自动切换到软件解码
        private int _hardwareDecodeFailures = 0;
        private const int MaxHardwareFailures = 3;
        private bool _forceSoftwareDecode = false;
        private bool _hardwareDecodeInitialized = false;
        private bool _shouldReinitialize = false;

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
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Resolution changed from {_oldOutputWidth}x{_oldOutputHeight} to {outSurf.RequestedWidth}x{outSurf.RequestedHeight}. Recreating FFmpegContext.");
                
                _context.Dispose();
                
                // 重置失败计数器
                _hardwareDecodeFailures = 0;
                _forceSoftwareDecode = false;
                _hardwareDecodeInitialized = false;
                _shouldReinitialize = false;
                
                // 重新创建上下文
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
                
                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }
            else if (_shouldReinitialize)
            {
                // 需要重新初始化解码器
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Reinitializing FFmpegContext due to previous failures");
                
                _context.Dispose();
                
                // 重置失败计数器
                _hardwareDecodeFailures = 0;
                _forceSoftwareDecode = false;
                _hardwareDecodeInitialized = false;
                _shouldReinitialize = false;
                
                // 重新创建上下文
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
            }

            // 检查是否应该强制使用软件解码
            CheckHardwareDecodeFallback();

            Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Starting decode. Bitstream size: {bs.Length}, Output surface: {outSurf.RequestedWidth}x{outSurf.RequestedHeight}");
            
            int result = _context.DecodeFrame(outSurf, bs);
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Decode result: {result}");
            
            // 处理解码结果
            if (result == 0)
            {
                // 解码成功
                if (!_hardwareDecodeInitialized)
                {
                    _hardwareDecodeInitialized = true;
                }
                
                // 重置失败计数器
                if (_hardwareDecodeFailures > 0)
                {
                    _hardwareDecodeFailures = 0;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware decode recovered, resetting failure counter");
                }
                
                return true;
            }
            else
            {
                // 解码失败
                if (!_forceSoftwareDecode)
                {
                    _hardwareDecodeFailures++;
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Hardware decode failure {_hardwareDecodeFailures}/{MaxHardwareFailures}");
                    
                    // 如果这是第一次失败，尝试重新初始化解码器
                    if (_hardwareDecodeFailures == 1)
                    {
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, "First hardware decode failure, trying to recover...");
                        _shouldReinitialize = true;
                    }
                }
                else
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Software decode failure");
                }
                
                return false;
            }
        }
        
        private void CheckHardwareDecodeFallback()
        {
            // 如果硬件解码失败次数过多，强制使用软件解码
            if (_hardwareDecodeFailures >= MaxHardwareFailures && !_forceSoftwareDecode)
            {
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Hardware decode failed {_hardwareDecodeFailures} times, forcing software decode");
                _forceSoftwareDecode = true;
                
                // 设置环境变量强制软件解码
                Environment.SetEnvironmentVariable("RYUJINX_FORCE_SOFTWARE_DECODE", "1");
                
                // 标记需要重新初始化
                _shouldReinitialize = true;
            }
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

