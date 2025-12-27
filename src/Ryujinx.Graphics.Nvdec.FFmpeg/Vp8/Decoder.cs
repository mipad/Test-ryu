// Decoder.cs (VP8) - 修复软件解码回退
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Vp8
{
    public sealed class Decoder : IDecoder
    {
        // 硬件解码器
        private SimpleHardwareDecoder _hardwareDecoder;
        private SimpleSurface _hardwareSurface;
        
        // 软件解码器
        private FFmpegContext _softwareContext;
        private Surface _softwareSurface;
        
        private bool _useHardware = true;
        private bool _hardwareInitialized = false;
        private int _width;
        private int _height;
        
        public bool IsHardwareAccelerated => _hardwareInitialized && _useHardware;
        
        public ISurface CreateSurface(int width, int height)
        {
            _width = width;
            _height = height;
            
            // 尝试初始化硬件解码器
            if (_useHardware && !_hardwareInitialized)
            {
                InitializeHardwareDecoder(width, height);
            }
            
            // 硬件可用：创建硬件表面
            if (_hardwareInitialized && _useHardware)
            {
                if (_hardwareSurface == null || 
                    _hardwareSurface.RequestedWidth != width ||
                    _hardwareSurface.RequestedHeight != height)
                {
                    _hardwareSurface = new SimpleSurface(width, height);
                }
                return _hardwareSurface;
            }
            
            // 软件回退：创建软件表面
            return CreateSoftwareSurface(width, height);
        }
        
        public bool Decode(ref Vp8PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            // 重建帧头
            byte[] frame = ReconstructFrameHeader(ref pictureInfo, bitstream);
            
            // 尝试硬件解码
            if (_useHardware && _hardwareDecoder != null && output is SimpleSurface hwSurface)
            {
                try
                {
                    if (_hardwareDecoder.Decode(frame, out var hwFrame))
                    {
                        hwSurface.UpdateFromFrame(ref hwFrame);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Hardware VP8 decode failed: {ex.Message}");
                    _useHardware = false;
                    // 硬件失败后，确保软件解码器就绪
                    EnsureSoftwareDecoderReady();
                }
            }
            
            // 软件解码回退
            return DecodeSoftware(output, frame);
        }
        
        private byte[] ReconstructFrameHeader(ref Vp8PictureInfo pictureInfo, ReadOnlySpan<byte> bitstream)
        {
            int uncompHeaderSize = pictureInfo.KeyFrame ? 10 : 3;
            byte[] frame = new byte[bitstream.Length + uncompHeaderSize];
            
            uint firstPartSizeShifted = pictureInfo.FirstPartSize << 5;
            
            frame[0] = (byte)(pictureInfo.KeyFrame ? 0 : 1);
            frame[0] |= (byte)((pictureInfo.Version & 7) << 1);
            frame[0] |= 1 << 4;
            frame[0] |= (byte)firstPartSizeShifted;
            frame[1] = (byte)(firstPartSizeShifted >> 8);
            frame[2] = (byte)(firstPartSizeShifted >> 16);
            
            if (pictureInfo.KeyFrame)
            {
                frame[3] = 0x9d;
                frame[4] = 0x01;
                frame[5] = 0x2a;
                frame[6] = (byte)pictureInfo.FrameWidth;
                frame[7] = (byte)((pictureInfo.FrameWidth >> 8) & 0x3F);
                frame[8] = (byte)pictureInfo.FrameHeight;
                frame[9] = (byte)((pictureInfo.FrameHeight >> 8) & 0x3F);
            }
            
            bitstream.CopyTo(new Span<byte>(frame)[uncompHeaderSize..]);
            return frame;
        }
        
        private bool DecodeSoftware(ISurface output, byte[] frame)
        {
            try
            {
                // 确保软件解码器就绪
                EnsureSoftwareDecoderReady();
                
                // 确保我们有正确的软件表面
                if (_softwareSurface == null || 
                    _softwareSurface.RequestedWidth != output.RequestedWidth ||
                    _softwareSurface.RequestedHeight != output.RequestedHeight)
                {
                    _softwareSurface = new Surface(output.RequestedWidth, output.RequestedHeight);
                }
                
                // 解码到软件表面
                int result = _softwareContext.DecodeFrame(_softwareSurface, frame);
                
                // 如果输出是软件表面，直接返回结果
                if (output is Surface swSurface && swSurface == _softwareSurface)
                {
                    return result == 0;
                }
                
                // 如果输出是硬件表面但需要软件解码，这里无法直接转换
                // 这种情况下，解码会失败，因为表面类型不匹配
                Console.WriteLine("Warning: Output surface type mismatch in software fallback");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Software VP8 decode failed: {ex.Message}");
                return false;
            }
        }
        
        private ISurface CreateSoftwareSurface(int width, int height)
        {
            EnsureSoftwareDecoderReady();
            
            if (_softwareSurface == null || 
                _softwareSurface.RequestedWidth != width || 
                _softwareSurface.RequestedHeight != height)
            {
                _softwareSurface = new Surface(width, height);
            }
            
            return _softwareSurface;
        }
        
        private void EnsureSoftwareDecoderReady()
        {
            if (_softwareContext == null)
            {
                _softwareContext = new FFmpegContext(AVCodecID.AV_CODEC_ID_VP8);
            }
        }
        
        // 初始化硬件解码器
        private void InitializeHardwareDecoder(int width, int height)
        {
            try
            {
                _hardwareDecoder = new SimpleHardwareDecoder(SimpleHWCodecType.VP8, width, height);
                _hardwareInitialized = true;
                _useHardware = true;
                Console.WriteLine($"Initialized hardware VP8 decoder for {width}x{height}");
            }
            catch (Exception ex)
            {
                _hardwareInitialized = false;
                _useHardware = false;
                Console.WriteLine($"Falling back to software VP8 decoder: {ex.Message}");
                EnsureSoftwareDecoderReady();
            }
        }
        
        public void Dispose()
        {
            _hardwareDecoder?.Dispose();
            _softwareContext?.Dispose();
            _hardwareSurface?.Dispose();
            _softwareSurface?.Dispose();
        }
    }
}
