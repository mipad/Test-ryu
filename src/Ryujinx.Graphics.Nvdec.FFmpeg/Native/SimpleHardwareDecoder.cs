// SimpleHardwareDecoder.cs - 添加详细日志
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
        public IntPtr[] Data;      // Y, U, V 平面数据指针
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public int[] Linesize;     // 行大小
        public int Width;
        public int Height;
        public int Format;         // 0=YUV420P, 1=NV12
        [MarshalAs(UnmanagedType.I1)]
        public bool KeyFrame;
        public long Pts;
    }
    
    // Android硬件解码器API（仅Android平台可用）
#if ANDROID
    [SupportedOSPlatform("android")]
    public static class SimpleHardwareDecoderApi
    {
        // Android库名为libhardware_decoder.so
        private const string LibraryName = "libhardware_decoder";
        
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
        public static extern IntPtr hw_get_last_error(IntPtr handle);
    }
    
    // Android硬件解码器包装类
    [SupportedOSPlatform("android")]
    public class SimpleHardwareDecoder : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;
        
        public SimpleHardwareDecoder(SimpleHWCodecType codec, int width, int height)
        {
            Console.WriteLine($"SimpleHardwareDecoder constructor: codec={codec}, width={width}, height={height}");
            
            bool hwAvailable = SimpleHardwareDecoderApi.hw_is_available();
            Console.WriteLine($"Hardware decoder available: {hwAvailable}");
            
            if (!hwAvailable)
                throw new NotSupportedException("Hardware decoder not available on this platform");
            
            _handle = SimpleHardwareDecoderApi.hw_create(codec, width, height, true);
            Console.WriteLine($"hw_create returned: {_handle}");
            
            if (_handle == IntPtr.Zero)
            {
                IntPtr errorPtr = SimpleHardwareDecoderApi.hw_get_last_error(_handle);
                string error = Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
                Console.WriteLine($"hw_create error: {error}");
                throw new InvalidOperationException($"Failed to create hardware decoder: {error}");
            }
            
            Console.WriteLine($"SimpleHardwareDecoder created successfully");
        }
        
        public bool Decode(byte[] data, out SimpleHWFrame frame)
        {
            Console.WriteLine($"SimpleHardwareDecoder.Decode called, data length: {data?.Length ?? 0}");
            
            frame = new SimpleHWFrame
            {
                Data = new IntPtr[3],
                Linesize = new int[3]
            };
            
            if (_disposed || _handle == IntPtr.Zero)
            {
                Console.WriteLine($"Decoder is disposed or handle is zero");
                return false;
            }
            
            int result = SimpleHardwareDecoderApi.hw_decode(_handle, data, data.Length, ref frame);
            Console.WriteLine($"hw_decode returned: {result}, frame: {frame.Width}x{frame.Height}, format={frame.Format}");
            
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
                Console.WriteLine($"Disposing SimpleHardwareDecoder");
                if (_handle != IntPtr.Zero)
                {
                    SimpleHardwareDecoderApi.hw_destroy(_handle);
                    _handle = IntPtr.Zero;
                }
                _disposed = true;
            }
        }
        
        public string GetLastError()
        {
            if (_handle == IntPtr.Zero)
                return "No handle";
                
            IntPtr errorPtr = SimpleHardwareDecoderApi.hw_get_last_error(_handle);
            string error = Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
            Console.WriteLine($"GetLastError: {error}");
            return error;
        }
    }
#else
    // 非Android平台返回空实现
    public static class SimpleHardwareDecoderApi
    {
        public static IntPtr hw_create(SimpleHWCodecType codec, int width, int height, bool use_hw) 
        {
            Console.WriteLine($"hw_create called but not on Android: codec={codec}, width={width}, height={height}");
            return IntPtr.Zero;
        }
        
        public static int hw_decode(IntPtr handle, byte[] data, int size, ref SimpleHWFrame frame) 
        {
            Console.WriteLine($"hw_decode called but not on Android");
            return -1;
        }
        
        public static void hw_destroy(IntPtr handle) 
        {
            Console.WriteLine($"hw_destroy called but not on Android");
        }
        
        public static bool hw_is_available() 
        {
            Console.WriteLine($"hw_is_available called but not on Android");
            return false;
        }
        
        public static IntPtr hw_get_last_error(IntPtr handle) 
        {
            Console.WriteLine($"hw_get_last_error called but not on Android");
            return IntPtr.Zero;
        }
    }
    
    // 非Android平台返回空实现
    public class SimpleHardwareDecoder : IDisposable
    {
        public SimpleHardwareDecoder(SimpleHWCodecType codec, int width, int height)
        {
            Console.WriteLine($"SimpleHardwareDecoder constructor called but not on Android");
            throw new PlatformNotSupportedException("Hardware decoder is only supported on Android");
        }
        
        public bool Decode(byte[] data, out SimpleHWFrame frame)
        {
            Console.WriteLine($"Decode called but not on Android");
            throw new PlatformNotSupportedException("Hardware decoder is only supported on Android");
        }
        
        public bool Decode(ReadOnlySpan<byte> data, out SimpleHWFrame frame)
        {
            Console.WriteLine($"Decode called but not on Android");
            throw new PlatformNotSupportedException("Hardware decoder is only supported on Android");
        }
        
        public void Dispose() 
        {
            Console.WriteLine($"Dispose called but not on Android");
        }
        
        public string GetLastError() => "Not supported on this platform";
    }
#endif
    
    // 平台检测辅助类
    public static class PlatformHelper
    {
        public static bool IsAndroid =>
#if ANDROID
            true;
#else
            false;
#endif
        
        public static bool IsHardwareDecoderAvailable => IsAndroid && SimpleHardwareDecoderApi.hw_is_available();
    }
}
