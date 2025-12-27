// SimpleHardwareDecoder.cs
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    // 最小化结构体
    public enum SimpleHWCodecType
    {
        H264 = 0,
        VP8 = 1,
        VP9 = 2
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct SimpleHWFrame
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public IntPtr[] Data;      // Y, U, V
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public int[] Linesize;     // 行大小
        public int Width;
        public int Height;
        public int Format;         // 0=YUV420P, 1=NV12
        [MarshalAs(UnmanagedType.I1)]
        public bool KeyFrame;
        public long Pts;
    }
    
    // 简化API
    public static class SimpleHardwareDecoderApi
    {
        private const string LibraryName = "hardware_decoder";
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr hw_create(SimpleHWCodecType codec, int width, int height, [MarshalAs(UnmanagedType.I1)] bool use_hw);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decode(IntPtr handle, byte[] data, int size, ref SimpleHWFrame frame);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_destroy(IntPtr handle);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool hw_is_available();
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string hw_get_last_error();
    }
    
    // 简化包装类
    public class SimpleHardwareDecoder : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;
        
        public SimpleHardwareDecoder(SimpleHWCodecType codec, int width, int height)
        {
            if (!SimpleHardwareDecoderApi.hw_is_available())
                throw new NotSupportedException("Hardware decoder not available");
            
            _handle = SimpleHardwareDecoderApi.hw_create(codec, width, height, true);
            if (_handle == IntPtr.Zero)
            {
                string error = SimpleHardwareDecoderApi.hw_get_last_error();
                throw new InvalidOperationException($"Failed to create decoder: {error}");
            }
        }
        
        public bool Decode(byte[] data, out SimpleHWFrame frame)
        {
            frame = new SimpleHWFrame
            {
                Data = new IntPtr[3],
                Linesize = new int[3]
            };
            
            if (_disposed || _handle == IntPtr.Zero)
                return false;
            
            int result = SimpleHardwareDecoderApi.hw_decode(_handle, data, data.Length, ref frame);
            return result == 0;
        }
        
        public bool Decode(ReadOnlySpan<byte> data, out SimpleHWFrame frame)
        {
            byte[] buffer = data.ToArray();
            return Decode(buffer, out frame);
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                if (_handle != IntPtr.Zero)
                {
                    SimpleHardwareDecoderApi.hw_destroy(_handle);
                    _handle = IntPtr.Zero;
                }
                _disposed = true;
            }
        }
    }
}
