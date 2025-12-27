// Decoder.cs (VP8)
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Vp8
{
    public sealed class Decoder : IDecoder
    {
        private SimpleHardwareDecoder _hardwareDecoder;
        private SimpleSurface _hardwareSurface;
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
            
            if (_useHardware && !_hardwareInitialized)
            {
                InitializeHardwareDecoder(width, height);
            }
            
            if (_hardwareInitialized && _useHardware)
            {
                if (_hardwareSurface == null || 
                    _hardwareSurface.Width != width ||
                    _hardwareSurface.Height != height)
                {
                    _hardwareSurface = new SimpleSurface(width, height);
                }
                return _hardwareSurface;
            }
            
            return CreateSoftwareSurface(width, height);
        }
        
        public bool Decode(ref Vp8PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            byte[] frame = ReconstructFrameHeader(ref pictureInfo, bitstream);
            
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
                    EnsureSoftwareDecoderReady();
                }
            }
            
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
                EnsureSoftwareDecoderReady();
                
                // 如果输出是软件表面，直接解码到它
                if (output is Surface swSurface)
                {
                    int result = _softwareContext.DecodeFrame(swSurface, frame);
                    return result == 0;
                }
                
                // 如果输出是硬件表面但需要软件解码，创建新的软件表面
                if (_softwareSurface == null || 
                    _softwareSurface.Width != output.Width ||
                    _softwareSurface.Height != output.Height)
                {
                    _softwareSurface = new Surface(output.Width, output.Height);
                }
                
                int decodeResult = _softwareContext.DecodeFrame(_softwareSurface, frame);
                return decodeResult == 0;
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
                _softwareSurface.Width != width || 
                _softwareSurface.Height != height)
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
