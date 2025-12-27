// Decoder.cs (H264) - 修复硬件解码器使用
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        private const int WorkBufferSize = 0x200;
        private readonly byte[] _workBuffer = new byte[WorkBufferSize];
        
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
                        Console.WriteLine($"Creating hardware decoder for H264, size: {width}x{height}");
                        _hwDecoder = new SimpleHardwareDecoder(SimpleHWCodecType.H264, width, height);
                        _hardwareInitialized = true;
                        _useHardware = true;
                        Console.WriteLine($"Hardware decoder initialized successfully");
                    }
                    catch (Exception ex)
                    {
                        _hardwareInitialized = false;
                        _useHardware = false;
                        Console.WriteLine($"Failed to initialize hardware decoder: {ex.Message}");
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
                    Console.WriteLine($"Creating hardware surface: {width}x{height}");
                    _hwSurface = new SimpleSurface(width, height);
                }
                return _hwSurface;
            }
            
            // 回退到软件解码器
            Console.WriteLine($"Creating software surface: {width}x{height}");
            return CreateSoftwareSurface(width, height);
        }
        
        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            byte[] frame = ReconstructFrame(ref pictureInfo, bitstream);
            
            // 尝试硬件解码
            if (_useHardware && _hardwareInitialized && _hwDecoder != null && output is SimpleSurface hwSurface)
            {
                try
                {
                    Console.WriteLine($"Attempting hardware decode for {_width}x{_height}");
                    
                    if (_hwDecoder.Decode(frame, out var hwFrame))
                    {
                        Console.WriteLine($"Hardware decode successful, frame: {hwFrame.Width}x{hwFrame.Height}");
                        hwSurface.UpdateFromFrame(ref hwFrame);
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"Hardware decode returned false");
                        _useHardware = false;
                        EnsureSoftwareDecoderReady();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Hardware H264 decode failed: {ex.Message}");
                    _useHardware = false;
                    EnsureSoftwareDecoderReady();
                }
            }
            
            // 软件解码回退
            Console.WriteLine($"Falling back to software decode");
            return DecodeSoftware(output, frame);
        }
        
        private byte[] ReconstructFrame(ref H264PictureInfo pictureInfo, ReadOnlySpan<byte> bitstream)
        {
            Span<byte> spsPps = SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer);
            byte[] frame = new byte[bitstream.Length + spsPps.Length];
            
            spsPps.CopyTo(frame);
            bitstream.CopyTo(new Span<byte>(frame)[spsPps.Length..]);
            
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
                    Console.WriteLine($"Decoding to software surface: {swSurface.Width}x{swSurface.Height}");
                    int result = _swContext.DecodeFrame(swSurface, frame);
                    return result == 0;
                }
                
                // 如果输出是硬件表面但需要软件解码，创建新的软件表面
                if (_swSurface == null || 
                    _swSurface.Width != output.Width ||
                    _swSurface.Height != output.Height)
                {
                    Console.WriteLine($"Creating new software surface for fallback: {output.Width}x{output.Height}");
                    _swSurface = new Surface(output.Width, output.Height);
                }
                
                Console.WriteLine($"Decoding to fallback software surface");
                int decodeResult = _swContext.DecodeFrame(_swSurface, frame);
                return decodeResult == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Software H264 decode failed: {ex.Message}");
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
                Console.WriteLine($"Creating software FFmpeg context for H264");
                _swContext = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
            }
        }
        
        public void Dispose()
        {
            if (_hwDecoder != null)
            {
                Console.WriteLine($"Disposing hardware decoder");
                _hwDecoder.Dispose();
                _hwDecoder = null;
            }
            
            if (_swContext != null)
            {
                Console.WriteLine($"Disposing software context");
                _swContext.Dispose();
                _swContext = null;
            }
            
            _hwSurface?.Dispose();
            _swSurface?.Dispose();
        }
    }
}
