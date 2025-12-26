using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    internal abstract class HardwareDecoder : IDisposable
    {
        public abstract bool IsHardwareAccelerated { get; }
        
        protected FFmpegContext _context;
        protected HardwareAccelerationMode _accelerationMode;
        
        protected bool _initialized;
        protected int _width;
        protected int _height;

        protected HardwareDecoder(AVCodecID codecId, HardwareAccelerationMode accelerationMode = HardwareAccelerationMode.Auto)
        {
            _accelerationMode = accelerationMode;
            InitializeContext(codecId);
        }

        private void InitializeContext(AVCodecID codecId)
        {
            try
            {
                _context = new FFmpegContext(codecId, _accelerationMode);
                _initialized = _context != null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize hardware decoder: {ex.Message}");
                _initialized = false;
            }
        }

        public virtual ISurface CreateSurface(int width, int height)
        {
            _width = width;
            _height = height;
            return new Surface(width, height);
        }

        public virtual bool DecodeFrame(ISurface output, ReadOnlySpan<byte> bitstream)
        {
            if (!_initialized || _context == null)
            {
                return false;
            }

            try
            {
                return _context.DecodeFrame((Surface)output, bitstream) == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Decode error: {ex.Message}");
                return false;
            }
        }

        public void SetAccelerationMode(HardwareAccelerationMode mode)
        {
            if (_accelerationMode != mode)
            {
                _accelerationMode = mode;
                DisposeContext();
                InitializeContext(GetCodecId());
            }
        }

        protected abstract AVCodecID GetCodecId();

        private void DisposeContext()
        {
            _context?.Dispose();
            _context = null;
            _initialized = false;
        }

        public virtual void Dispose()
        {
            DisposeContext();
        }
    }
}
