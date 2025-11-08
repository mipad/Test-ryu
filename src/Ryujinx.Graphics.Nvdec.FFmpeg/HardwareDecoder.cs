using System;
using System.Runtime.InteropServices;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    /// <summary>
    /// 硬件解码器包装类 - 用于C#层调用硬件解码功能
    /// </summary>
    public unsafe class HardwareDecoder : IDisposable
    {
        private const string LibraryName = "ryujinxjni";
        private bool _disposed = false;
        private IntPtr _decoderHandle;
        private readonly string _codecMime;
        private readonly int _width;
        private readonly int _height;

        // Native方法声明
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool initializeHardwareDecoder([MarshalAs(UnmanagedType.LPStr)] string codecMime, int width, int height);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool decodeHardwareFrame(byte[] data, int size);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void releaseHardwareDecoder();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool isHardwareCodecSupported([MarshalAs(UnmanagedType.LPStr)] string codecMime);

        public HardwareDecoder(string codecMime, int width, int height)
        {
            _codecMime = codecMime;
            _width = width;
            _height = height;

            Logger.Info?.Print(LogClass.FFmpeg, $"Creating hardware decoder: {codecMime}, {width}x{height}");
            
            if (!Initialize())
            {
                throw new InvalidOperationException($"Failed to initialize hardware decoder for {codecMime}");
            }
        }

        private bool Initialize()
        {
            try
            {
                bool success = initializeHardwareDecoder(_codecMime, _width, _height);
                if (success)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Hardware decoder initialized successfully: {_codecMime}");
                    return true;
                }
                else
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"Failed to initialize hardware decoder: {_codecMime}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Exception initializing hardware decoder: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 解码视频帧
        /// </summary>
        public bool DecodeFrame(ReadOnlySpan<byte> bitstream)
        {
            if (_disposed)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, "Hardware decoder is disposed");
                return false;
            }

            try
            {
                byte[] data = bitstream.ToArray();
                bool success = decodeHardwareFrame(data, data.Length);
                
                if (!success)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, "Hardware decode frame failed");
                }
                
                return success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Exception decoding frame with hardware decoder: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查编解码器是否支持硬件解码
        /// </summary>
        public static bool IsCodecSupported(string codecMime)
        {
            try
            {
                return isHardwareCodecSupported(codecMime);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Exception checking codec support: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 刷新解码器
        /// </summary>
        public void Flush()
        {
            // 硬件解码器刷新逻辑
            Logger.Debug?.Print(LogClass.FFmpeg, "Flushing hardware decoder");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    releaseHardwareDecoder();
                    Logger.Info?.Print(LogClass.FFmpeg, "Hardware decoder released");
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"Exception releasing hardware decoder: {ex.Message}");
                }
                finally
                {
                    _disposed = true;
                }
            }
        }
    }
}