// Decoder.cs (VP8) - 修改为硬件解码器
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Vp8
{
    public sealed class Decoder : IDecoder
    {
        // 硬件解码器
        private HardwareDecoder _hardwareDecoder;
        private HardwareSurface _hardwareSurface;
        
        // 软件解码器作为回退
        private FFmpegContext _softwareContext;
        private Surface _softwareSurface;
        
        private bool _useHardware = true;
        private bool _hardwareInitialized = false;
        
        public bool IsHardwareAccelerated => _hardwareInitialized && _hardwareDecoder?.IsHardwareAccelerated == true;
        
        public ISurface CreateSurface(int width, int height)
        {
            _hardwareSurface = new HardwareSurface(width, height);
            return _hardwareSurface;
        }
        
        public bool Decode(ref Vp8PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            // 重建帧头
            int uncompHeaderSize = pictureInfo.KeyFrame ? 10 : 3;
            byte[] frame = new byte[bitstream.Length + uncompHeaderSize];
            
            uint firstPartSizeShifted = pictureInfo.FirstPartSize << 5;
            
            frame[0] = (byte)(pictureInfo.KeyFrame ? 0 : 1);
            frame[0] |= (byte)((pictureInfo.Version & 7) << 1);
            frame[0] |= 1 << 4;
            frame[0] |= (byte)firstPartSizeShifted;
            frame[1] |= (byte)(firstPartSizeShifted >> 8);
            frame[2] |= (byte)(firstPartSizeShifted >> 16);
            
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
            
            // 尝试硬件解码
            if (_useHardware && _hardwareDecoder != null)
            {
                try
                {
                    if (_hardwareDecoder.DecodeFrame(frame, out var frameData))
                    {
                        var hwSurface = output as HardwareSurface;
                        if (hwSurface != null)
                        {
                            hwSurface.UpdateFromFrameData(ref frameData);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Hardware VP8 decode failed: {ex.Message}");
                    _useHardware = false;
                }
            }
            
            // 软件解码回退
            return DecodeSoftware(output, frame);
        }
        
        private bool DecodeSoftware(ISurface output, byte[] frame)
        {
            try
            {
                // 确保软件解码器存在
                if (_softwareContext == null)
                {
                    _softwareContext = new FFmpegContext(AVCodecID.AV_CODEC_ID_VP8);
                }
                
                // 确保软件表面存在
                if (_softwareSurface == null || 
                    _softwareSurface.RequestedWidth != output.RequestedWidth ||
                    _softwareSurface.RequestedHeight != output.RequestedHeight)
                {
                    _softwareSurface = new Surface(output.RequestedWidth, output.RequestedHeight);
                }
                
                // 解码
                return _softwareContext.DecodeFrame(_softwareSurface, frame) == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Software VP8 decode failed: {ex.Message}");
                return false;
            }
        }
        
        // 初始化硬件解码器
        private void InitializeHardwareDecoder(int width, int height)
        {
            try
            {
                _hardwareDecoder = new HardwareDecoder(HWCodecType.HW_CODEC_VP8, width, height, true);
                _hardwareInitialized = true;
                _useHardware = true;
                Console.WriteLine($"Initialized hardware VP8 decoder for {width}x{height}");
            }
            catch (Exception ex)
            {
                _hardwareInitialized = false;
                _useHardware = false;
                _softwareContext = new FFmpegContext(AVCodecID.AV_CODEC_ID_VP8);
                Console.WriteLine($"Falling back to software VP8 decoder: {ex.Message}");
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
