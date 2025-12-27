// SimpleSurface.cs
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class SimpleSurface : ISurface, IDisposable
    {
        private SimpleHWFrame _frame;
        private bool _disposed;
        
        public int Width => _frame.Width;
        public int Height => _frame.Height;
        public int Stride => _frame.Linesize[0];
        public int UvWidth => (Width + 1) >> 1;
        public int UvHeight => (Height + 1) >> 1;
        public int UvStride => _frame.Linesize[1];
        
        public Plane YPlane => new(_frame.Data[0], Stride * Height);
        public Plane UPlane => new(_frame.Data[1], UvStride * UvHeight);
        public Plane VPlane => new(_frame.Data[2], UvStride * UvHeight);
        
        public FrameField Field => FrameField.Progressive;
        
        public int RequestedWidth { get; }
        public int RequestedHeight { get; }
        
        public SimpleSurface(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;
            _frame = new SimpleHWFrame
            {
                Data = new IntPtr[3],
                Linesize = new int[3]
            };
        }
        
        public void UpdateFromFrame(ref SimpleHWFrame frame)
        {
            _frame = frame;
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                _frame = default;
                _disposed = true;
            }
        }
    }
}
