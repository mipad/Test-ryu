// Decoder.cs (H264)
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
                if (_hwSurface == null || 
                    _hwSurface.Width != width ||
                    _hwSurface.Height != height)
                {
                    _hwSurface = new SimpleSurface(width, height);
                }
                return _hwSurface;
            }
            
            return CreateSoftwareSurface(width, height);
        }
        
        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            byte[] frame = ReconstructFrame(ref pictureInfo, bitstream);
            
            if (_useHardware && _hwDecoder != null && output is SimpleSurface hwSurface)
            {
                try
                {
                    if (_hwDecoder.Decode(frame, out var hwFrame))
                    {
                        hwSurface.UpdateFromFrame(ref hwFrame);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Hardware H264 decode failed: {ex.Message}");
                    _useHardware = false;
                    EnsureSoftwareDecoderReady();
                }
            }
            
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
                    int result = _swContext.DecodeFrame(swSurface, frame);
                    return result == 0;
                }
                
                // 如果输出是硬件表面但需要软件解码，创建新的软件表面
                if (_swSurface == null || 
                    _swSurface.Width != output.Width ||
                    _swSurface.Height != output.Height)
                {
                    _swSurface = new Surface(output.Width, output.Height);
                }
                
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
                _swContext = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
            }
        }
        
        private void InitializeHardwareDecoder(int width, int height)
        {
            try
            {
                _hwDecoder = new SimpleHardwareDecoder(SimpleHWCodecType.H264, width, height);
                _hardwareInitialized = true;
                _useHardware = true;
                Console.WriteLine($"Initialized hardware H264 decoder for {width}x{height}");
            }
            catch (Exception ex)
            {
                _hardwareInitialized = false;
                _useHardware = false;
                Console.WriteLine($"Falling back to software H264 decoder: {ex.Message}");
                EnsureSoftwareDecoderReady();
            }
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
