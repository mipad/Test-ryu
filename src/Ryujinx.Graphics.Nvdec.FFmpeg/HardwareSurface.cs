// HardwareSurface.cs
using Ryujinx.Graphics.Video;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class HardwareSurface : ISurface, IDisposable
    {
        private HWFrameData _frameData;
        private bool _disposed;
        
        public IntPtr YPtr => _frameData.data[0];
        public IntPtr UPtr => _frameData.data[1];
        public IntPtr VPtr => _frameData.data[2];
        public IntPtr APtr => _frameData.data[3];
        
        public int Width => _frameData.width;
        public int Height => _frameData.height;
        public int Stride => _frameData.linesize[0];
        public int UvWidth => (Width + 1) >> 1;
        public int UvHeight => (Height + 1) >> 1;
        public int UvStride => _frameData.linesize[1];
        
        public Plane YPlane => new(YPtr, Stride * Height);
        public Plane UPlane => new(UPtr, UvStride * UvHeight);
        public Plane VPlane => new(VPtr, UvStride * UvHeight);
        
        public FrameField Field => _frameData.interlaced ? FrameField.Interlaced : FrameField.Progressive;
        
        public int RequestedWidth { get; }
        public int RequestedHeight { get; }
        
        public HWFrameData FrameData => _frameData;
        
        public HWPixelFormat PixelFormat => _frameData.format;
        
        public bool IsHardwareDecoded => true; // 硬件解码表面
        
        public bool IsKeyFrame => _frameData.key_frame;
        
        public long Pts => _frameData.pts;
        public long Dts => _frameData.dts;
        
        public int CodedPictureNumber => _frameData.coded_picture_number;
        public int DisplayPictureNumber => _frameData.display_picture_number;
        
        public int Quality => _frameData.quality;
        
        public int SampleAspectRatioNum => _frameData.sample_aspect_ratio_num;
        public int SampleAspectRatioDen => _frameData.sample_aspect_ratio_den;
        
        public int ColorRange => _frameData.color_range;
        public int ColorPrimaries => _frameData.color_primaries;
        public int ColorTrc => _frameData.color_trc;
        public int Colorspace => _frameData.colorspace;
        
        public HardwareSurface(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;
            _frameData = new HWFrameData();
            _disposed = false;
        }
        
        public void UpdateFromFrameData(ref HWFrameData frameData)
        {
            _frameData = frameData;
        }
        
        public void UpdateFromFrameData(HWFrameData frameData)
        {
            _frameData = frameData;
        }
        
        // 获取平面数据
        public byte[] GetYData()
        {
            if (_frameData.data[0] == IntPtr.Zero)
                return null;
            
            int size = _frameData.GetPlaneSize(0);
            if (size <= 0)
                return null;
            
            byte[] data = new byte[size];
            Marshal.Copy(_frameData.data[0], data, 0, size);
            return data;
        }
        
        public byte[] GetUData()
        {
            if (_frameData.data[1] == IntPtr.Zero)
                return null;
            
            int size = _frameData.GetPlaneSize(1);
            if (size <= 0)
                return null;
            
            byte[] data = new byte[size];
            Marshal.Copy(_frameData.data[1], data, 0, size);
            return data;
        }
        
        public byte[] GetVData()
        {
            if (_frameData.data[2] == IntPtr.Zero)
                return null;
            
            int size = _frameData.GetPlaneSize(2);
            if (size <= 0)
                return null;
            
            byte[] data = new byte[size];
            Marshal.Copy(_frameData.data[2], data, 0, size);
            return data;
        }
        
        public byte[] GetAData()
        {
            if (_frameData.data[3] == IntPtr.Zero)
                return null;
            
            int size = _frameData.GetPlaneSize(3);
            if (size <= 0)
                return null;
            
            byte[] data = new byte[size];
            Marshal.Copy(_frameData.data[3], data, 0, size);
            return data;
        }
        
        // 获取所有平面数据
        public byte[][] GetAllPlaneData()
        {
            return new byte[][]
            {
                GetYData(),
                GetUData(),
                GetVData(),
                GetAData()
            };
        }
        
        // 复制数据到另一个表面
        public bool CopyTo(HardwareSurface target)
        {
            if (target == null || target._disposed)
                return false;
            
            target._frameData = _frameData;
            return true;
        }
        
        // 创建深拷贝
        public HardwareSurface Clone()
        {
            var surface = new HardwareSurface(RequestedWidth, RequestedHeight);
            
            // 复制帧数据
            surface._frameData = _frameData;
            
            // 注意：这里只是浅拷贝指针，如果需要深拷贝数据需要额外处理
            return surface;
        }
        
        // 检查表面是否有效
        public bool IsValid()
        {
            return _frameData.IsValid();
        }
        
        // 获取帧大小
        public int GetFrameSize()
        {
            return _frameData.GetFrameSize();
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                // 注意：这里不释放数据指针，因为数据由解码器管理
                _frameData = new HWFrameData();
                _disposed = true;
            }
        }
        
        ~HardwareSurface()
        {
            Dispose();
        }
    }
}
