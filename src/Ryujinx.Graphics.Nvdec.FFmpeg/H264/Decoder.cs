// Decoder.cs (H264) - 修改为使用简化硬件解码器
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        private const int WorkBufferSize = 0x200;
        private readonly byte[] _workBuffer = new byte[WorkBufferSize];
        
        // 硬件解码器
        private SimpleHardwareDecoder _hwDecoder;
        private SimpleSurface _hwSurface;
        
        // 软件解码器回退
        private FFmpegContext _swContext;
        private Surface _swSurface;
        
        private int _width;
        private int _height;
        private bool _useHardware = true;
        
        public bool IsHardwareAccelerated => _hwDecoder != null && _useHardware;
        
        public ISurface CreateSurface(int width, int height)
        {
            _width = width;
            _height = height;
            
            // 尝试初始化硬件解码器
            if (_useHardware && _hwDecoder == null)
            {
                try
                {
                    _hwDecoder = new SimpleHardwareDecoder(
                        SimpleHWCodecType.H264, width, height);
                    _hwSurface = new SimpleSurface(width, height);
                    Console.WriteLine($"Hardware decoder initialized: {width}x{height}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Hardware decoder failed: {ex.Message}");
                    _useHardware = false;
                }
            }
            
            // 回退到软件解码器
            if (!_useHardware || _hwDecoder == null)
            {
                if (_swContext == null)
                    _swContext = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
                
                if (_swSurface == null || 
                    _swSurface.RequestedWidth != width || 
                    _swSurface.RequestedHeight != height)
                {
                    _swSurface = new Surface(width, height);
                }
                
                return _swSurface;
            }
            
            return _hwSurface;
        }
        
        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            // 重建SPS/PPS
            Span<byte> bs = Prepend(bitstream, 
                SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));
            
            // 尝试硬件解码
            if (_useHardware && _hwDecoder != null)
            {
                try
                {
                    if (_hwDecoder.Decode(bs, out var frame) && 
                        output is SimpleSurface hwSurface)
                    {
                        hwSurface.UpdateFromFrame(ref frame);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Hardware decode error: {ex.Message}");
                    _useHardware = false;
                }
            }
            
            // 软件解码回退
            if (_swContext == null)
                _swContext = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
            
            if (_swSurface == null || 
                _swSurface.RequestedWidth != output.RequestedWidth || 
                _swSurface.RequestedHeight != output.RequestedHeight)
            {
                _swSurface = new Surface(output.RequestedWidth, output.RequestedHeight);
            }
            
            return _swContext.DecodeFrame(_swSurface, bs) == 0;
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
            _hwDecoder?.Dispose();
            _swContext?.Dispose();
            _hwSurface?.Dispose();
            _swSurface?.Dispose();
        }
    }
}
