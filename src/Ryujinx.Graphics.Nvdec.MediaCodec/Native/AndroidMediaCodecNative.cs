// Ryujinx.Graphics.Nvdec.MediaCodec.Native/AndroidMediaCodecNative.cs
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.MediaCodec.Native
{
    public static class AndroidMediaCodecNative
    {
        private const string LibraryName = "ryujinxjni"; // 我们编译的 C++ 库
        
        // 解码器状态枚举
        public enum DecoderStatus
        {
            Uninitialized = 0,
            Initialized,
            Running,
            Stopped,
            Error
        }
        
        // 颜色格式
        public enum ColorFormat
        {
            YUV420Planar = 0x13,        // YV12
            YUV420SemiPlanar = 0x15,    // NV12
            YUV420Flexible = 0x7F420888 // Flexible YUV
        }
        
        // 解码器句柄类型
        [StructLayout(LayoutKind.Sequential)]
        public struct DecoderHandle
        {
            public IntPtr Handle;
        }
        
        // 解码帧信息结构
        [StructLayout(LayoutKind.Sequential)]
        public struct DecodedFrame
        {
            public int Width;
            public int Height;
            public int Flags;
            public long PresentationTimeUs;
            public IntPtr YData;
            public IntPtr UData;
            public IntPtr VData;
            public int YSize;
            public int USize;
            public int VSize;
        }
        
        // 创建解码器
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern DecoderHandle CreateMediaCodecH264Decoder();
        
        // 初始化解码器
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool InitMediaCodecH264Decoder(
            DecoderHandle decoder,
            int width,
            int height,
            byte[] spsData,
            int spsSize,
            byte[] ppsData,
            int ppsSize);
        
        // 初始化解码器（带 Surface）
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool InitMediaCodecH264DecoderWithSurface(
            DecoderHandle decoder,
            int width,
            int height,
            IntPtr surface,  // ANativeWindow*
            byte[] spsData,
            int spsSize,
            byte[] ppsData,
            int ppsSize);
        
        // 启动解码器
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool StartMediaCodecH264Decoder(DecoderHandle decoder);
        
        // 解码帧
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool DecodeH264Frame(
            DecoderHandle decoder,
            byte[] frameData,
            int frameSize,
            long presentationTimeUs);
        
        // 获取解码后的帧
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool GetDecodedFrame(
            DecoderHandle decoder,
            out DecodedFrame frame,
            int timeoutUs);
        
        // 释放解码帧
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ReleaseDecodedFrame(ref DecodedFrame frame);
        
        // 停止解码器
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool StopMediaCodecH264Decoder(DecoderHandle decoder);
        
        // 销毁解码器
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void DestroyMediaCodecH264Decoder(DecoderHandle decoder);
        
        // 检查 H.264 支持
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool IsMediaCodecH264Supported();
        
        // 获取设备信息
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr GetMediaCodecDeviceInfo();
        
        // 获取最佳解码器名称
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr GetBestH264DecoderName();
        
        // 获取解码器状态
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern DecoderStatus GetDecoderStatus(DecoderHandle decoder);
        
        // 检查解码器是否运行
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool IsDecoderRunning(DecoderHandle decoder);
    }
}
