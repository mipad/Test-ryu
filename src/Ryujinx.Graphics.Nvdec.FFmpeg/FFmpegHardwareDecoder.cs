using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System; 
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    /// <summary>
    /// FFmpeg 硬件解码器包装类（使用 C++ 端硬件解码），实现统一接口
    /// </summary>
    public unsafe class FFmpegHardwareDecoder : IVideoDecoder, IDisposable
    {
        private long _decoderContextId;
        private bool _initialized;
        private string _codecName;
        private bool _useHardwareDecoder;

        // 硬件解码器类型
        public const string HW_TYPE_MEDIACODEC = "mediacodec";
        
        // 像素格式常量
        public const int PIX_FMT_NONE = -1;
        public const int PIX_FMT_YUV420P = 0;
        public const int PIX_FMT_NV12 = 23;
        public const int PIX_FMT_MEDIACODEC = 165;

        // JNI 原生方法声明 - 修复：使用正确的库名 "ryujinxjni"
        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_initFFmpegHardwareDecoder")]
        private static extern void InitFFmpegHardwareDecoder();

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_cleanupFFmpegHardwareDecoder")]
        private static extern void CleanupFFmpegHardwareDecoder();

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_createHardwareDecoderContext")]
        private static extern long CreateHardwareDecoderContext([MarshalAs(UnmanagedType.LPStr)] string codecName);

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_decodeVideoFrame")]
        private static extern int DecodeVideoFrame(long contextId, byte[] inputData, int inputSize,
                                                  int[] frameInfo, byte[][] planeData);

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_flushDecoder")]
        private static extern void FlushDecoder(long contextId);

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_destroyHardwareDecoder")]
        private static extern void DestroyHardwareDecoder(long contextId);

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_isHardwareDecoderSupported")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsHardwareDecoderSupported([MarshalAs(UnmanagedType.LPStr)] string decoderType);

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_isHardwareDecoderAvailable")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsHardwareDecoderAvailable([MarshalAs(UnmanagedType.LPStr)] string codecName);

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_getFFmpegVersion")]
        [return: MarshalAs(UnmanagedType.LPStr)]
        private static extern string GetFFmpegVersion();

        // 新增：直接 C 接口声明（备用方案）
        [DllImport("ryujinxjni", EntryPoint = "InitializeFFmpegHardwareDecoder")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool InitializeFFmpegHardwareDecoder_C();

        [DllImport("ryujinxjni", EntryPoint = "CleanupFFmpegHardwareDecoder")]
        private static extern void CleanupFFmpegHardwareDecoder_C();

        [DllImport("ryujinxjni", EntryPoint = "CreateHardwareDecoderContext")]
        private static extern long CreateHardwareDecoderContext_C([MarshalAs(UnmanagedType.LPStr)] string codecName);

        [DllImport("ryujinxjni", EntryPoint = "DecodeVideoFrame")]
        private static extern int DecodeVideoFrame_C(long contextId, byte[] inputData, int inputSize,
                                                    int[] width, int[] height, int[] format,
                                                    byte[] plane0, int plane0Size,
                                                    byte[] plane1, int plane1Size,
                                                    byte[] plane2, int plane2Size);

        [DllImport("ryujinxjni", EntryPoint = "DestroyHardwareDecoderContext")]
        private static extern void DestroyHardwareDecoderContext_C(long contextId);

        [DllImport("ryujinxjni", EntryPoint = "FlushHardwareDecoder")]
        private static extern void FlushHardwareDecoder_C(long contextId);

        [DllImport("ryujinxjni", EntryPoint = "GetFFmpegVersionString")]
        [return: MarshalAs(UnmanagedType.LPStr)]
        private static extern string GetFFmpegVersionString_C();

        // 实现 IVideoDecoder 接口
        public bool IsInitialized => _initialized;
        public bool IsHardwareDecoder => _useHardwareDecoder;
        public string CodecName => _codecName;
        public string HardwareDecoderName => _codecName + "_mediacodec";

        /// <summary>
        /// 检查 MediaCodec 是否可用
        /// </summary>
        public static bool IsMediaCodecSupported()
        {
            try
            {
                return IsHardwareDecoderSupported(HW_TYPE_MEDIACODEC);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to check MediaCodec support via JNI: {ex.Message}");
                
                // 备用方案：尝试直接 C 接口
                try
                {
                    // 检查是否能初始化硬件解码器
                    bool initialized = InitializeFFmpegHardwareDecoder_C();
                    if (initialized)
                    {
                        CleanupFFmpegHardwareDecoder_C();
                    }
                    return initialized;
                }
                catch (Exception ex2)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to check MediaCodec support via C API: {ex2.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 检查指定编码器的硬件解码是否可用
        /// </summary>
        public static bool IsCodecHardwareSupported(string codecName)
        {
            try
            {
                return IsHardwareDecoderAvailable(codecName);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to check hardware decoder for {codecName}: {ex.Message}");
                
                // 备用方案：尝试创建解码器上下文
                try
                {
                    long contextId = CreateHardwareDecoderContext_C(codecName);
                    if (contextId != 0)
                    {
                        DestroyHardwareDecoderContext_C(contextId);
                        return true;
                    }
                    return false;
                }
                catch (Exception ex2)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to check hardware decoder for {codecName} via C API: {ex2.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 获取 FFmpeg 版本信息
        /// </summary>
        public static string GetVersionInfo()
        {
            try
            {
                string version = GetFFmpegVersion();
                return version ?? "Unknown";
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to get FFmpeg version via JNI: {ex.Message}");
                
                // 备用方案：尝试直接 C 接口
                try
                {
                    string version = GetFFmpegVersionString_C();
                    return version ?? "Unknown";
                }
                catch (Exception ex2)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to get FFmpeg version via C API: {ex2.Message}");
                    return "Unknown";
                }
            }
        }

        /// <summary>
        /// 初始化硬件解码器
        /// </summary>
        public FFmpegHardwareDecoder(string codecName)
        {
            _codecName = codecName ?? throw new ArgumentNullException(nameof(codecName));
            
            try
            {
                // 方法1：首先尝试 JNI 接口
                try
                {
                    InitFFmpegHardwareDecoder();
                    _decoderContextId = CreateHardwareDecoderContext(codecName);
                }
                catch (Exception jniEx)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"JNI interface failed, trying C API: {jniEx.Message}");
                    
                    // 方法2：备用方案 - 使用直接 C 接口
                    if (!InitializeFFmpegHardwareDecoder_C())
                    {
                        throw new InvalidOperationException("Failed to initialize hardware decoder via C API");
                    }
                    
                    _decoderContextId = CreateHardwareDecoderContext_C(codecName);
                }

                _initialized = _decoderContextId != 0;
                _useHardwareDecoder = _initialized;

                if (_initialized)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Hardware decoder initialized for {codecName}, context ID: {_decoderContextId}");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to initialize hardware decoder for {codecName}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Exception initializing hardware decoder for {codecName}: {ex.Message}");
                _initialized = false;
            }
        }

        /// <summary>
        /// 实现 IVideoDecoder 接口的 DecodeFrame 方法
        /// </summary>
        public int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream)
        {
            if (!_initialized)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Hardware decoder not initialized");
                return -1;
            }

            if (output == null || output.Frame == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Output surface is null");
                return -1;
            }

            try
            {
                byte[] inputData = bitstream.ToArray();
                int[] frameInfo = new int[6]; // width, height, format, linesize0, linesize1, linesize2
                byte[][] planeData = new byte[3][]; // Y, U, V 平面

                // 初始化为空数组
                for (int i = 0; i < planeData.Length; i++)
                {
                    planeData[i] = Array.Empty<byte>();
                }

                int result;
                
                // 方法1：首先尝试 JNI 接口
                try
                {
                    result = DecodeVideoFrame(_decoderContextId, inputData, inputData.Length, frameInfo, planeData);
                }
                catch (Exception jniEx)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"JNI decode failed, trying C API: {jniEx.Message}");
                    
                    // 方法2：备用方案 - 使用直接 C 接口
                    int[] width = new int[1], height = new int[1], format = new int[1];
                    
                    // 估计平面大小
                    int estimatedSize = inputData.Length * 2; // 粗略估计
                    byte[] plane0 = new byte[estimatedSize];
                    byte[] plane1 = new byte[estimatedSize / 4];
                    byte[] plane2 = new byte[estimatedSize / 4];
                    
                    result = DecodeVideoFrame_C(_decoderContextId, inputData, inputData.Length,
                                              width, height, format,
                                              plane0, plane0.Length,
                                              plane1, plane1.Length,
                                              plane2, plane2.Length);
                    
                    if (result == 0)
                    {
                        frameInfo[0] = width[0];
                        frameInfo[1] = height[0];
                        frameInfo[2] = format[0];
                        // 对于 C 接口，需要手动计算行大小
                        frameInfo[3] = width[0];  // 粗略估计
                        frameInfo[4] = width[0] / 2;
                        frameInfo[5] = width[0] / 2;
                        
                        // 填充平面数据
                        planeData[0] = plane0;
                        planeData[1] = plane1;
                        planeData[2] = plane2;
                    }
                }

                if (result == 0) // 成功
                {
                    // 将解码后的数据复制到 Surface
                    if (CopyPlanesToSurface(planeData, new FrameInfo(frameInfo), output))
                    {
                        Logger.Debug?.Print(LogClass.FFmpeg, 
                            $"Hardware decoded frame: {frameInfo[0]}x{frameInfo[1]}, format: {frameInfo[2]}, planes: {GetValidPlaneCount(planeData)}");
                        return 0;
                    }
                    else
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, "Failed to copy decoded data to surface");
                        return -1;
                    }
                }
                else if (result == AVERROR_EAGAIN)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder needs more input data");
                    return AVERROR_EAGAIN;
                }
                else if (result == AVERROR_EOF)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder reached end of stream");
                    return AVERROR_EOF;
                }
                else
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, 
                        $"Hardware decode failed for {_codecName}, result: {result}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Exception during hardware decode for {_codecName}: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 实现 IVideoDecoder 接口的 Flush 方法
        /// </summary>
        public void Flush()
        {
            if (_initialized)
            {
                try
                {
                    // 首先尝试 JNI 接口
                    try
                    {
                        FlushDecoder(_decoderContextId);
                    }
                    catch (Exception jniEx)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, $"JNI flush failed, trying C API: {jniEx.Message}");
                        FlushHardwareDecoder_C(_decoderContextId);
                    }
                    
                    Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder flushed");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, 
                        $"Exception flushing hardware decoder: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 解码视频帧到平面数据（备用方法）
        /// </summary>
        public bool DecodeFrameToPlanes(byte[] inputData, out FrameInfo frameInfo, out byte[][] planeData)
        {
            frameInfo = null;
            planeData = null;

            if (!_initialized || inputData == null || inputData.Length == 0)
            {
                return false;
            }

            try
            {
                int[] frameInfoArray = new int[6];
                byte[][] outputPlanes = new byte[3][];

                for (int i = 0; i < outputPlanes.Length; i++)
                {
                    outputPlanes[i] = Array.Empty<byte>();
                }

                int result;
                
                // 首先尝试 JNI 接口
                try
                {
                    result = DecodeVideoFrame(_decoderContextId, inputData, inputData.Length, 
                                            frameInfoArray, outputPlanes);
                }
                catch (Exception jniEx)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"JNI decode failed, trying C API: {jniEx.Message}");
                    
                    // 备用方案：使用 C 接口
                    int[] width = new int[1], height = new int[1], format = new int[1];
                    int estimatedSize = inputData.Length * 2;
                    byte[] plane0 = new byte[estimatedSize];
                    byte[] plane1 = new byte[estimatedSize / 4];
                    byte[] plane2 = new byte[estimatedSize / 4];
                    
                    result = DecodeVideoFrame_C(_decoderContextId, inputData, inputData.Length,
                                              width, height, format,
                                              plane0, plane0.Length,
                                              plane1, plane1.Length,
                                              plane2, plane2.Length);
                    
                    if (result == 0)
                    {
                        frameInfoArray[0] = width[0];
                        frameInfoArray[1] = height[0];
                        frameInfoArray[2] = format[0];
                        frameInfoArray[3] = width[0];
                        frameInfoArray[4] = width[0] / 2;
                        frameInfoArray[5] = width[0] / 2;
                        
                        outputPlanes[0] = plane0;
                        outputPlanes[1] = plane1;
                        outputPlanes[2] = plane2;
                    }
                }

                if (result == 0)
                {
                    frameInfo = new FrameInfo(frameInfoArray);
                    
                    List<byte[]> validPlanes = new List<byte[]>();
                    for (int i = 0; i < outputPlanes.Length; i++)
                    {
                        if (outputPlanes[i] != null && outputPlanes[i].Length > 0)
                        {
                            validPlanes.Add(outputPlanes[i]);
                        }
                    }
                    planeData = validPlanes.ToArray();

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Exception during hardware decode for {_codecName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 将平面数据复制到 Surface 的 AVFrame
        /// </summary>
        private bool CopyPlanesToSurface(byte[][] planeData, FrameInfo frameInfo, Surface surface)
        {
            if (planeData == null || planeData.Length == 0 || surface == null || surface.Frame == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Invalid parameters for CopyPlanesToSurface");
                return false;
            }

            try
            {
                AVFrame* frame = surface.Frame;

                // 设置帧基本信息
                frame->Width = frameInfo.Width;
                frame->Height = frameInfo.Height;
                frame->Format = frameInfo.Format;

                // 设置行大小
                for (int i = 0; i < 3 && i < frame->LineSize.Length; i++)
                {
                    frame->LineSize[i] = GetLineSizeForPlane(i, frameInfo);
                }

                // 复制平面数据
                for (int i = 0; i < planeData.Length && i < frame->Data.Length; i++)
                {
                    if (planeData[i] != null && planeData[i].Length > 0)
                    {
                        CopyPlaneData(planeData[i], (IntPtr)frame->Data[i], frameInfo, i);
                    }
                }

                Logger.Debug?.Print(LogClass.FFmpeg, 
                    $"Copied {planeData.Length} planes to surface, frame: {frameInfo.Width}x{frameInfo.Height}, format: {frameInfo.Format}");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Exception copying planes to surface: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 复制单个平面数据
        /// </summary>
        private void CopyPlaneData(byte[] sourceData, IntPtr destPtr, FrameInfo frameInfo, int planeIndex)
        {
            if (sourceData == null || sourceData.Length == 0 || destPtr == IntPtr.Zero)
            {
                return;
            }

            int planeHeight = GetPlaneHeight(planeIndex, frameInfo.Height);
            int lineSize = GetLineSizeForPlane(planeIndex, frameInfo);
            int expectedDataSize = lineSize * planeHeight;

            // 检查数据大小是否匹配
            if (sourceData.Length < expectedDataSize)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, 
                    $"Plane {planeIndex} data size mismatch: expected {expectedDataSize}, got {sourceData.Length}");
                expectedDataSize = Math.Min(sourceData.Length, expectedDataSize);
            }

            // 逐行复制数据（处理可能的行对齐）
            int sourceOffset = 0;
            for (int row = 0; row < planeHeight; row++)
            {
                int bytesToCopy = Math.Min(lineSize, sourceData.Length - sourceOffset);
                if (bytesToCopy <= 0) break;

                Marshal.Copy(sourceData, sourceOffset, 
                           IntPtr.Add(destPtr, row * lineSize), 
                           bytesToCopy);
                sourceOffset += bytesToCopy;
            }
        }

        /// <summary>
        /// 获取平面的高度
        /// </summary>
        private int GetPlaneHeight(int planeIndex, int frameHeight)
        {
            // 对于 YUV420，Y平面是全高，UV平面是半高
            return planeIndex == 0 ? frameHeight : (frameHeight + 1) / 2;
        }

        /// <summary>
        /// 获取平面的行大小
        /// </summary>
        private int GetLineSizeForPlane(int planeIndex, FrameInfo frameInfo)
        {
            switch (planeIndex)
            {
                case 0: return frameInfo.Linesize0;
                case 1: return frameInfo.Linesize1;
                case 2: return frameInfo.Linesize2;
                default: return 0;
            }
        }

        /// <summary>
        /// 获取有效平面数量
        /// </summary>
        private int GetValidPlaneCount(byte[][] planeData)
        {
            int count = 0;
            foreach (var plane in planeData)
            {
                if (plane != null && plane.Length > 0) count++;
            }
            return count;
        }

        // FFmpeg 错误代码常量
        private const int AVERROR_EAGAIN = -11; // 需要更多输入数据
        private const int AVERROR_EOF = -541478725; // 文件结束

        /// <summary>
        /// 帧信息结构
        /// </summary>
        public class FrameInfo
        {
            public int Width { get; }
            public int Height { get; }
            public int Format { get; }
            public int Linesize0 { get; }
            public int Linesize1 { get; }
            public int Linesize2 { get; }

            public FrameInfo(int[] info)
            {
                if (info != null && info.Length >= 6)
                {
                    Width = info[0];
                    Height = info[1];
                    Format = info[2];
                    Linesize0 = info[3];
                    Linesize1 = info[4];
                    Linesize2 = info[5];
                }
            }

            public override string ToString()
            {
                return $"FrameInfo{{Width={Width}, Height={Height}, Format={Format}, " +
                       $"Linesize=[{Linesize0}, {Linesize1}, {Linesize2}]}}";
            }
        }

        public void Dispose()
        {
            if (_initialized && _decoderContextId != 0)
            {
                try
                {
                    // 首先尝试 JNI 接口
                    try
                    {
                        DestroyHardwareDecoder(_decoderContextId);
                    }
                    catch (Exception jniEx)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, $"JNI destroy failed, trying C API: {jniEx.Message}");
                        DestroyHardwareDecoderContext_C(_decoderContextId);
                    }
                    
                    // 清理硬件解码器
                    try
                    {
                        CleanupFFmpegHardwareDecoder();
                    }
                    catch (Exception cleanupEx)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, $"JNI cleanup failed, trying C API: {cleanupEx.Message}");
                        CleanupFFmpegHardwareDecoder_C();
                    }
                    
                    _decoderContextId = 0;
                    _initialized = false;
                    
                    Logger.Info?.Print(LogClass.FFmpeg, 
                        $"Hardware decoder disposed for {_codecName}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, 
                        $"Exception disposing hardware decoder: {ex.Message}");
                }
            }
        }
    }
}
