using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices; // 添加必要的命名空间
using System.Text; // 添加必要的命名空间

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    // 添加FFmpegUtils类的实现
    internal static class FFmpegUtils
    {
        [DllImport("avutil", CallingConvention = CallingConvention.Cdecl)]
        private static extern int av_strerror(int errnum, byte[] errbuf, int errbuf_size);

        public static string GetErrorDescription(int error)
        {
            byte[] buffer = new byte[64];
            if (av_strerror(error, buffer, buffer.Length) == 0)
            {
                int length = Array.IndexOf(buffer, (byte)0);
                return Encoding.ASCII.GetString(buffer, 0, length);
            }
            return $"Unknown error ({error})";
        }
    }

    public sealed class Decoder : IH264Decoder
    {
        public bool IsHardwareAccelerated => false;

        private const int WorkBufferSize = 0x200;

        private readonly byte[] _workBuffer = new byte[WorkBufferSize];
        private FFmpegContext _context = new(AVCodecID.AV_CODEC_ID_H264);
        
        private int _oldOutputWidth;
        private int _oldOutputHeight;
        private int _frameSkipCounter;

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            if (_frameSkipCounter > 0)
            {
                _frameSkipCounter--;
                return false;
            }
            
            Surface outSurf = (Surface)output;

            if (outSurf.RequestedWidth != _oldOutputWidth ||
                outSurf.RequestedHeight != _oldOutputHeight)
            {
                ResetDecoder();
                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }

            Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));

            try
            {
                int result = _context.DecodeFrame(outSurf, bs);
                
                if (result != 0)
                {
                    // 使用我们实现的FFmpegUtils获取错误描述
                    string errorMsg = FFmpegUtils.GetErrorDescription(result);
                    
                    // 特定错误处理：重置解码器并跳过帧
                    if (errorMsg.Contains("illegal short term buffer") || 
                        errorMsg.Contains("reference picture missing"))
                    {
                        ResetDecoder();
                        _frameSkipCounter = 3;
                        return false;
                    }
                    
                    throw new Exception($"Decode failed with error code {result}: {errorMsg}");
                }
                
                return true;
            }
            catch (Exception ex)
            {
                // 通用错误处理
                ResetDecoder();
                _frameSkipCounter = 3;
                return false;
            }
        }

        private void ResetDecoder()
        {
            _context.Dispose();
            _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
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
