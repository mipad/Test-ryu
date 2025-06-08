using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using System.Collections.Generic; // 新增命名空间

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        public bool IsHardwareAccelerated => false;

        private const int WorkBufferSize = 0x200;
        private const int MaxReferenceFrames = 16; // 新增：最大参考帧数

        private readonly byte[] _workBuffer = new byte[WorkBufferSize];
        private FFmpegContext _context = new(AVCodecID.AV_CODEC_ID_H264);
        
        // 新增：参考帧列表
        private readonly List<Surface> _referenceFrames = new List<Surface>();
        
        private int _oldOutputWidth;
        private int _oldOutputHeight;
        private int _frameSkipCounter; // 新增：错误恢复帧跳过计数器

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            // 新增：帧跳过机制
            if (_frameSkipCounter > 0)
            {
                _frameSkipCounter--;
                return false;
            }
            
            Surface outSurf = (Surface)output;

            if (outSurf.RequestedWidth != _oldOutputWidth ||
                outSurf.RequestedHeight != _oldOutputHeight)
            {
                ResetDecoder(); // 改为调用统一的重置方法
                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }

            // 新增：检查是否为参考帧
            bool isReferenceFrame = (pictureInfo.flags & H264PictureInfoFlags.IsReference) != 0;
            
            // 新增：参考帧管理
            if (isReferenceFrame)
            {
                // 验证参考帧索引有效性
                if (pictureInfo.frame_num < 0 || pictureInfo.frame_num > MaxReferenceFrames * 2)
                {
                    // 无效索引，强制标记为非参考帧
                    pictureInfo.flags &= ~H264PictureInfoFlags.IsReference;
                    isReferenceFrame = false;
                }
                else
                {
                    // 添加到参考帧列表
                    _referenceFrames.Add(outSurf);
                }
            }
            
            // 新增：限制参考帧数量
            if (_referenceFrames.Count > MaxReferenceFrames)
            {
                // 移除最旧的参考帧
                var oldest = _referenceFrames[0];
                _referenceFrames.RemoveAt(0);
                oldest.Dispose(); // 释放资源
            }

            Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));

            try
            {
                // 修改：添加严格错误检测标志
                _context.CodecContext->err_recognition = ffmpeg.AV_EF_EXPLODE;
                
                int result = _context.DecodeFrame(outSurf, bs);
                
                // 新增：解码失败时的错误处理
                if (result != 0)
                {
                    throw new FFmpegException($"Decode failed with error code {result}");
                }
                
                return true;
            }
            catch (FFmpegException ex) when (ex.Message.Contains("illegal short term buffer") || 
                                            ex.Message.Contains("reference picture missing"))
            {
                // 新增：参考帧错误恢复机制
                ResetDecoder();
                
                // 跳过后续3帧
                _frameSkipCounter = 3;
                
                return false;
            }
        }

        // 新增：重置解码器方法
        private void ResetDecoder()
        {
            _context.Dispose();
            _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
            
            // 清空参考帧列表
            foreach (var frame in _referenceFrames)
            {
                frame.Dispose();
            }
            _referenceFrames.Clear();
            
            // 重置FFmpeg错误检测标志
            if (_context.CodecContext != null)
            {
                _context.CodecContext->err_recognition = ffmpeg.AV_EF_EXPLODE;
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
            ResetDecoder(); // 使用统一的重置方法
        }
    }
}
