// Decoder.cs (VP8)
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Vp8
{
    public sealed class Decoder : IDecoder
    {
        private SimpleHardwareDecoder _hwDecoder;
        private SimpleSurface _hwSurface;
        private FFmpegContext _swContext;
        private Surface _swSurface;
        private int _width;
        private int _height;
        private bool _useHardware = true;
        private bool _hardwareInitialized = false;
        private bool _hardwareAvailable = false;
        
        public bool IsHardwareAccelerated => _hardwareInitialized && _useHardware && _hardwareAvailable;
        
        public ISurface CreateSurface(int width, int height)
        {
            _width = width;
            _height = height;
            
            // 检查硬件解码器是否可用
            if (_useHardware && !_hardwareInitialized)
            {
                _hardwareAvailable = PlatformHelper.IsHardwareDecoderAvailable;
                
                if (_hardwareAvailable)
                {
                    try
                    {
                        Console.WriteLine($"Creating hardware decoder for VP8, size: {width}x{height}");
                        _hwDecoder = new SimpleHardwareDecoder(SimpleHWCodecType.VP8, width, height);
                        _hardwareInitialized = true;
                        _useHardware = true;
                        Console.WriteLine($"Hardware VP8 decoder initialized successfully");
                    }
                    catch (Exception ex)
                    {
                        _hardwareInitialized = false;
                        _useHardware = false;
                        Console.WriteLine($"Failed to initialize hardware VP8 decoder: {ex.Message}");
                        EnsureSoftwareDecoderReady();
                    }
                }
                else
                {
                    Console.WriteLine("Hardware decoder not available on this platform");
                    _useHardware = false;
                    EnsureSoftwareDecoderReady();
                }
            }
            
            // 使用硬件解码器
            if (_hardwareInitialized && _useHardware)
            {
                if (_hwSurface == null || 
                    _hwSurface.Width != width ||
                    _hwSurface.Height != height)
                {
                    Console.WriteLine($"Creating hardware surface for VP8: {width}x{height}");
                    _hwSurface = new SimpleSurface(width, height);
                }
                return _hwSurface;
            }
            
            // 回退到软件解码器
            Console.WriteLine($"Creating software surface for VP8: {width}x{height}");
            return CreateSoftwareSurface(width, height);
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
            
            // 尝试硬件解码
            if (_useHardware && _hardwareInitialized && _hwDecoder != null && output is SimpleSurface hwSurface)
            {
                try
                {
                    Console.WriteLine($"Attempting hardware VP8 decode for {_width}x{_height}");
                    
                    if (_hwDecoder.Decode(frame, out var hwFrame))
                    {
                        Console.WriteLine($"Hardware VP8 decode successful, frame: {hwFrame.Width}x{hwFrame.Height}");
                        hwSurface.UpdateFromFrame(ref hwFrame);
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"Hardware VP8 decode returned false");
                        _useHardware = false;
                        EnsureSoftwareDecoderReady();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Hardware VP8 decode failed: {ex.Message}");
                    _useHardware = false;
                    EnsureSoftwareDecoderReady();
                }
            }
            
            // 软件解码回退
            Console.WriteLine($"Falling back to software VP8 decode");
            return DecodeSoftware(output, frame);
        }
        
        private bool DecodeSoftware(ISurface output, byte[] frame)
        {
            try
            {
                EnsureSoftwareDecoderReady();
                
                // 如果输出是软件表面，直接解码到它
                if (output is Surface swSurface)
                {
                    Console.WriteLine($"Decoding VP8 to software surface: {swSurface.Width}x{swSurface.Height}");
                    int result = _swContext.DecodeFrame(swSurface, frame);
                    return result == 0;
                }
                
                // 如果输出是硬件表面但需要软件解码，创建新的软件表面
                if (_swSurface == null || 
                    _swSurface.Width != output.Width ||
                    _swSurface.Height != output.Height)
                {
                    Console.WriteLine($"Creating new software surface for VP8 fallback: {output.Width}x{output.Height}");
                    _swSurface = new Surface(output.Width, output.Height);
                }
                
                Console.WriteLine($"Decoding VP8 to fallback software surface");
                int decodeResult = _swContext.DecodeFrame(_swSurface, frame);
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
            
            if (_swSurface == null || 
                _swSurface.Width != width || 
                _swSurface.Height != height)
            {
                _swSurface = new Surface(width, height);
            }
            
            return _swSurface;
        }
        
        private void EnsureSoftwareDecoderReady()
        {
            if (_swContext == null)
            {
                Console.WriteLine($"Creating software FFmpeg context for VP8");
                _swContext = new FFmpegContext(AVCodecID.AV_CODEC_ID_VP8);
            }
        }
        
        public void Dispose()
        {
            if (_hwDecoder != null)
            {
                Console.WriteLine($"Disposing hardware VP8 decoder");
                _hwDecoder.Dispose();
                _hwDecoder = null;
            }
            
            if (_swContext != null)
            {
                Console.WriteLine($"Disposing software VP8 context");
                _swContext.Dispose();
                _swContext = null;
            }
            
            _hwSurface?.Dispose();
            _swSurface?.Dispose();
        }
    }
}
