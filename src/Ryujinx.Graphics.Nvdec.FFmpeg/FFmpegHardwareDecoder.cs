using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System; 
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;

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
        private bool _disposed = false;

        // 硬件解码器类型
        public const string HW_TYPE_MEDIACODEC = "mediacodec";
        
        // 像素格式常量
        public const int PIX_FMT_NONE = -1;
        public const int PIX_FMT_YUV420P = 0;
        public const int PIX_FMT_NV12 = 23;
        public const int PIX_FMT_MEDIACODEC = 165;

        // JNI 原生方法声明 - 使用正确的库名 "ryujinxjni"
        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_initFFmpegHardwareDecoder")]
        private static extern void InitFFmpegHardwareDecoder();

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_cleanupFFmpegHardwareDecoder")]
        private static extern void CleanupFFmpegHardwareDecoder();

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_createHardwareDecoderContext")]
        private static extern long CreateHardwareDecoderContext([MarshalAs(UnmanagedType.LPStr)] string codecName);

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_decodeVideoFrame")]
        private static extern int DecodeVideoFrame(long contextId, byte[] inputData, int inputSize,
                                                  [In, Out] int[] frameInfo, [In, Out] byte[][] planeData);

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

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_getHardwarePixelFormat")]
        private static extern int GetHardwarePixelFormat([MarshalAs(UnmanagedType.LPStr)] string decoderName);

        // 新增：获取支持的硬件解码器列表
        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_getSupportedHardwareDecoders")]
        private static extern IntPtr GetSupportedHardwareDecoders();

        [DllImport("ryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_getFrameInfo")]
        private static extern IntPtr GetFrameInfo(long contextId);

        // 直接 C 接口声明（备用方案）
        [DllImport("ryujinxjni", EntryPoint = "InitializeFFmpegHardwareDecoder")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool InitializeFFmpegHardwareDecoder_C();

        [DllImport("ryujinxjni", EntryPoint = "CleanupFFmpegHardwareDecoder")]
        private static extern void CleanupFFmpegHardwareDecoder_C();

        [DllImport("ryujinxjni", EntryPoint = "CreateHardwareDecoderContext")]
        private static extern long CreateHardwareDecoderContext_C([MarshalAs(UnmanagedType.LPStr)] string codecName);

        [DllImport("ryujinxjni", EntryPoint = "DecodeVideoFrame")]
        private static extern int DecodeVideoFrame_C(long contextId, 
                                                    [MarshalAs(UnmanagedType.LPArray)] byte[] inputData, 
                                                    int inputSize,
                                                    [MarshalAs(UnmanagedType.LPArray)] int[] width,
                                                    [MarshalAs(UnmanagedType.LPArray)] int[] height, 
                                                    [MarshalAs(UnmanagedType.LPArray)] int[] format,
                                                    [MarshalAs(UnmanagedType.LPArray)] byte[] plane0, 
                                                    int plane0Size,
                                                    [MarshalAs(UnmanagedType.LPArray)] byte[] plane1, 
                                                    int plane1Size,
                                                    [MarshalAs(UnmanagedType.LPArray)] byte[] plane2, 
                                                    int plane2Size);

        [DllImport("ryujinxjni", EntryPoint = "DestroyHardwareDecoderContext")]
        private static extern void DestroyHardwareDecoderContext_C(long contextId);

        [DllImport("ryujinxjni", EntryPoint = "FlushHardwareDecoder")]
        private static extern void FlushHardwareDecoder_C(long contextId);

        [DllImport("ryujinxjni", EntryPoint = "GetFFmpegVersionString")]
        [return: MarshalAs(UnmanagedType.LPStr)]
        private static extern string GetFFmpegVersionString_C();

        // 实现 IVideoDecoder 接口
        public bool IsInitialized => _initialized && !_disposed;
        public bool IsHardwareDecoder => _useHardwareDecoder;
        public string CodecName => _codecName;
        public string HardwareDecoderName => _codecName + "_mediacodec";

        // 统计信息
        private int _framesDecoded = 0;
        private int _framesFailed = 0;
        private DateTime _startTime = DateTime.Now;

        /// <summary>
        /// 检查 MediaCodec 是否可用
        /// </summary>
        public static bool IsMediaCodecSupported()
        {
            try
            {
                Logger.Info?.Print(LogClass.FFmpeg, "Checking MediaCodec support via JNI...");
                bool supported = IsHardwareDecoderSupported(HW_TYPE_MEDIACODEC);
                Logger.Info?.Print(LogClass.FFmpeg, $"MediaCodec support check result: {supported}");
                return supported;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Failed to check MediaCodec support via JNI: {ex.Message}");
                Logger.Debug?.Print(LogClass.FFmpeg, $"MediaCodec support check exception: {ex}");
                
                // 备用方案：尝试直接 C 接口
                try
                {
                    Logger.Info?.Print(LogClass.FFmpeg, "Trying MediaCodec support check via C API...");
                    bool initialized = InitializeFFmpegHardwareDecoder_C();
                    if (initialized)
                    {
                        CleanupFFmpegHardwareDecoder_C();
                    }
                    Logger.Info?.Print(LogClass.FFmpeg, $"MediaCodec C API check result: {initialized}");
                    return initialized;
                }
                catch (Exception ex2)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"Failed to check MediaCodec support via C API: {ex2.Message}");
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
                Logger.Info?.Print(LogClass.FFmpeg, $"Checking hardware decoder support for {codecName} via JNI...");
                bool available = IsHardwareDecoderAvailable(codecName);
                Logger.Info?.Print(LogClass.FFmpeg, $"Hardware decoder support for {codecName}: {available}");
                return available;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Failed to check hardware decoder for {codecName}: {ex.Message}");
                
                // 备用方案：尝试创建解码器上下文
                try
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Trying hardware decoder support check for {codecName} via C API...");
                    long contextId = CreateHardwareDecoderContext_C(codecName);
                    bool available = contextId != 0;
                    if (available)
                    {
                        DestroyHardwareDecoderContext_C(contextId);
                    }
                    Logger.Info?.Print(LogClass.FFmpeg, $"Hardware decoder C API check for {codecName}: {available}");
                    return available;
                }
                catch (Exception ex2)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"Failed to check hardware decoder for {codecName} via C API: {ex2.Message}");
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
                Logger.Info?.Print(LogClass.FFmpeg, "Getting FFmpeg version via JNI...");
                string version = GetFFmpegVersion();
                Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg JNI version: {version}");
                return version ?? "Unknown";
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Failed to get FFmpeg version via JNI: {ex.Message}");
                
                // 备用方案：尝试直接 C 接口
                try
                {
                    Logger.Info?.Print(LogClass.FFmpeg, "Getting FFmpeg version via C API...");
                    string version = GetFFmpegVersionString_C();
                    Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg C API version: {version}");
                    return version ?? "Unknown";
                }
                catch (Exception ex2)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"Failed to get FFmpeg version via C API: {ex2.Message}");
                    return "Unknown";
                }
            }
        }

        /// <summary>
        /// 获取支持的硬件解码器列表
        /// </summary>
        public static string[] GetSupportedDecoders()
        {
            try
            {
                Logger.Info?.Print(LogClass.FFmpeg, "Getting supported hardware decoders...");
                IntPtr decodersPtr = GetSupportedHardwareDecoders();
                if (decodersPtr == IntPtr.Zero)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, "No supported hardware decoders found");
                    return Array.Empty<string>();
                }

                // 这里需要根据实际的 JNI 数组处理逻辑来实现
                // 暂时返回空数组，实际实现需要处理 JNI 字符串数组
                Logger.Info?.Print(LogClass.FFmpeg, "Supported hardware decoders list retrieved");
                return Array.Empty<string>();
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Failed to get supported hardware decoders: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 初始化硬件解码器
        /// </summary>
        public FFmpegHardwareDecoder(string codecName)
        {
            Logger.Info?.Print(LogClass.FFmpeg, $"=== Starting hardware decoder initialization for {codecName} ===");
            
            _codecName = codecName ?? throw new ArgumentNullException(nameof(codecName));
            _startTime = DateTime.Now;
            
            try
            {
                // 方法1：首先尝试 JNI 接口
                bool jniSuccess = false;
                try
                {
                    Logger.Info?.Print(LogClass.FFmpeg, "Attempting JNI initialization...");
                    InitFFmpegHardwareDecoder();
                    Logger.Info?.Print(LogClass.FFmpeg, "JNI initialization successful, creating decoder context...");
                    
                    _decoderContextId = CreateHardwareDecoderContext(codecName);
                    jniSuccess = _decoderContextId != 0;
                    
                    if (jniSuccess)
                    {
                        Logger.Info?.Print(LogClass.FFmpeg, $"JNI decoder context created successfully, ID: {_decoderContextId}");
                    }
                    else
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, "JNI decoder context creation failed");
                    }
                }
                catch (Exception jniEx)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"JNI interface failed: {jniEx.Message}");
                    Logger.Debug?.Print(LogClass.FFmpeg, $"JNI exception details: {jniEx}");
                }

                // 方法2：如果 JNI 失败，尝试直接 C 接口
                if (!jniSuccess)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, "Falling back to C API initialization...");
                    try
                    {
                        if (!InitializeFFmpegHardwareDecoder_C())
                        {
                            throw new InvalidOperationException("Failed to initialize hardware decoder via C API");
                        }
                        
                        _decoderContextId = CreateHardwareDecoderContext_C(codecName);
                        if (_decoderContextId == 0)
                        {
                            throw new InvalidOperationException("Failed to create hardware decoder context via C API");
                        }
                        
                        Logger.Info?.Print(LogClass.FFmpeg, $"C API decoder context created successfully, ID: {_decoderContextId}");
                    }
                    catch (Exception cApiEx)
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, $"C API initialization also failed: {cApiEx.Message}");
                        throw new InvalidOperationException($"Hardware decoder initialization failed for {codecName} via both JNI and C API", cApiEx);
                    }
                }

                _initialized = _decoderContextId != 0;
                _useHardwareDecoder = _initialized;

                if (_initialized)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"=== Hardware decoder successfully initialized for {codecName}, context ID: {_decoderContextId} ===");
                    
                    // 获取硬件像素格式信息
                    try
                    {
                        int pixelFormat = GetHardwarePixelFormat(codecName + "_mediacodec");
                        Logger.Info?.Print(LogClass.FFmpeg, $"Hardware pixel format: {pixelFormat}");
                    }
                    catch (Exception pfEx)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to get hardware pixel format: {pfEx.Message}");
                    }
                }
                else
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"=== Hardware decoder initialization FAILED for {codecName} ===");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"=== Exception during hardware decoder initialization for {codecName}: {ex.Message} ===");
                Logger.Debug?.Print(LogClass.FFmpeg, $"Initialization exception details: {ex}");
                _initialized = false;
                throw;
            }
        }

        /// <summary>
        /// 实现 IVideoDecoder 接口的 DecodeFrame 方法
        /// </summary>
        public int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream)
        {
            if (_disposed)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Hardware decoder is disposed, cannot decode frame");
                return -1;
            }

            if (!_initialized)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Hardware decoder not initialized");
                return -1;
            }

            if (output == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Output surface is null");
                return -1;
            }

            if (output.Frame == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Output surface frame is null");
                return -1;
            }

            if (bitstream.IsEmpty)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, "Input bitstream is empty");
                return AVERROR_EAGAIN;
            }

            Logger.Debug?.Print(LogClass.FFmpeg, $"Starting hardware decode frame #{_framesDecoded + 1}, input size: {bitstream.Length} bytes");

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

                int result = -1;
                bool usedJni = false;
                
                // 方法1：首先尝试 JNI 接口
                try
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, "Attempting JNI decode...");
                    result = DecodeVideoFrame(_decoderContextId, inputData, inputData.Length, frameInfo, planeData);
                    usedJni = true;
                    Logger.Debug?.Print(LogClass.FFmpeg, $"JNI decode result: {result}");
                }
                catch (Exception jniEx)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"JNI decode failed: {jniEx.Message}");
                    
                    // 方法2：备用方案 - 使用直接 C 接口
                    try
                    {
                        Logger.Debug?.Print(LogClass.FFmpeg, "Attempting C API decode...");
                        int[] width = new int[1], height = new int[1], format = new int[1];
                        
                        // 估计平面大小
                        int estimatedSize = inputData.Length * 3; // 保守估计
                        byte[] plane0 = new byte[estimatedSize];
                        byte[] plane1 = new byte[estimatedSize / 2];
                        byte[] plane2 = new byte[estimatedSize / 2];
                        
                        result = DecodeVideoFrame_C(_decoderContextId, inputData, inputData.Length,
                                                  width, height, format,
                                                  plane0, plane0.Length,
                                                  plane1, plane1.Length,
                                                  plane2, plane2.Length);
                        
                        Logger.Debug?.Print(LogClass.FFmpeg, $"C API decode result: {result}");
                        
                        if (result == 0)
                        {
                            frameInfo[0] = width[0];
                            frameInfo[1] = height[0];
                            frameInfo[2] = format[0];
                            // 对于 C 接口，需要手动计算行大小
                            frameInfo[3] = width[0];  // 粗略估计
                            frameInfo[4] = Math.Max(1, width[0] / 2);
                            frameInfo[5] = Math.Max(1, width[0] / 2);
                            
                            // 填充平面数据
                            planeData[0] = plane0;
                            planeData[1] = plane1;
                            planeData[2] = plane2;
                        }
                    }
                    catch (Exception cApiEx)
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, $"C API decode also failed: {cApiEx.Message}");
                        result = -1;
                    }
                }

                // 处理解码结果
                if (result == 0) // 成功
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, $"Decode successful, frame info: {frameInfo[0]}x{frameInfo[1]}, format: {frameInfo[2]}");
                    
                    // 将解码后的数据复制到 Surface
                    if (CopyPlanesToSurface(planeData, new FrameInfo(frameInfo), output))
                    {
                        _framesDecoded++;
                        Logger.Debug?.Print(LogClass.FFmpeg, 
                            $"Hardware decoded frame #{_framesDecoded}: {frameInfo[0]}x{frameInfo[1]}, format: {frameInfo[2]}, planes: {GetValidPlaneCount(planeData)}, method: {(usedJni ? "JNI" : "C API")}");
                        return 0;
                    }
                    else
                    {
                        _framesFailed++;
                        Logger.Error?.Print(LogClass.FFmpeg, "Failed to copy decoded data to surface");
                        return -1;
                    }
                }
                else if (result == AVERROR_EAGAIN)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder needs more input data (EAGAIN)");
                    return AVERROR_EAGAIN;
                }
                else if (result == AVERROR_EOF)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, "Hardware decoder reached end of stream (EOF)");
                    return AVERROR_EOF;
                }
                else
                {
                    _framesFailed++;
                    Logger.Warning?.Print(LogClass.FFmpeg, 
                        $"Hardware decode failed for {_codecName}, result: {result}, method: {(usedJni ? "JNI" : "C API")}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                _framesFailed++;
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Exception during hardware decode for {_codecName}: {ex.Message}");
                Logger.Debug?.Print(LogClass.FFmpeg, $"Decode exception details: {ex}");
                return -1;
            }
        }

        /// <summary>
        /// 实现 IVideoDecoder 接口的 Flush 方法
        /// </summary>
        public void Flush()
        {
            if (_disposed)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, "Cannot flush disposed hardware decoder");
                return;
            }

            if (!_initialized)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, "Cannot flush uninitialized hardware decoder");
                return;
            }

            Logger.Info?.Print(LogClass.FFmpeg, "Flushing hardware decoder...");

            try
            {
                // 首先尝试 JNI 接口
                try
                {
                    FlushDecoder(_decoderContextId);
                    Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder flushed via JNI");
                }
                catch (Exception jniEx)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"JNI flush failed: {jniEx.Message}");
                    try
                    {
                        FlushHardwareDecoder_C(_decoderContextId);
                        Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder flushed via C API");
                    }
                    catch (Exception cApiEx)
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, $"C API flush also failed: {cApiEx.Message}");
                    }
                }

                // 重置统计信息
                _framesDecoded = 0;
                _framesFailed = 0;
                Logger.Info?.Print(LogClass.FFmpeg, "Hardware decoder flush completed, statistics reset");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Exception flushing hardware decoder: {ex.Message}");
            }
        }

        /// <summary>
        /// 解码视频帧到平面数据（备用方法）
        /// </summary>
        public bool DecodeFrameToPlanes(byte[] inputData, out FrameInfo frameInfo, out byte[][] planeData)
        {
            frameInfo = null;
            planeData = null;

            if (_disposed)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Cannot decode with disposed hardware decoder");
                return false;
            }

            if (!_initialized)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Hardware decoder not initialized");
                return false;
            }

            if (inputData == null || inputData.Length == 0)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Input data is null or empty");
                return false;
            }

            Logger.Debug?.Print(LogClass.FFmpeg, $"Decoding frame to planes, input size: {inputData.Length} bytes");

            try
            {
                int[] frameInfoArray = new int[6];
                byte[][] outputPlanes = new byte[3][];

                for (int i = 0; i < outputPlanes.Length; i++)
                {
                    outputPlanes[i] = Array.Empty<byte>();
                }

                int result;
                bool usedJni = false;
                
                // 首先尝试 JNI 接口
                try
                {
                    result = DecodeVideoFrame(_decoderContextId, inputData, inputData.Length, 
                                            frameInfoArray, outputPlanes);
                    usedJni = true;
                }
                catch (Exception jniEx)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"JNI decode failed: {jniEx.Message}");
                    
                    // 备用方案：使用 C 接口
                    int[] width = new int[1], height = new int[1], format = new int[1];
                    int estimatedSize = inputData.Length * 3;
                    byte[] plane0 = new byte[estimatedSize];
                    byte[] plane1 = new byte[estimatedSize / 2];
                    byte[] plane2 = new byte[estimatedSize / 2];
                    
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
                        frameInfoArray[4] = Math.Max(1, width[0] / 2);
                        frameInfoArray[5] = Math.Max(1, width[0] / 2);
                        
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

                    Logger.Debug?.Print(LogClass.FFmpeg, 
                        $"Successfully decoded frame to planes: {frameInfoArray[0]}x{frameInfoArray[1]}, format: {frameInfoArray[2]}, valid planes: {validPlanes.Count}, method: {(usedJni ? "JNI" : "C API")}");
                    return true;
                }
                else
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, 
                        $"Failed to decode frame to planes, result: {result}, method: {(usedJni ? "JNI" : "C API")}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Exception during hardware decode for {_codecName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取解码统计信息
        /// </summary>
        public string GetStatistics()
        {
            TimeSpan uptime = DateTime.Now - _startTime;
            double successRate = _framesDecoded + _framesFailed > 0 ? 
                (double)_framesDecoded / (_framesDecoded + _framesFailed) * 100 : 0;
            
            return $"HardwareDecoder Statistics: " +
                   $"Uptime: {uptime:hh\\:mm\\:ss}, " +
                   $"Frames: {_framesDecoded} decoded, {_framesFailed} failed, " +
                   $"Success Rate: {successRate:F2}%, " +
                   $"Codec: {_codecName}, " +
                   $"Context ID: {_decoderContextId}";
        }

        /// <summary>
        /// 将平面数据复制到 Surface 的 AVFrame
        /// </summary>
        private bool CopyPlanesToSurface(byte[][] planeData, FrameInfo frameInfo, Surface surface)
        {
            if (planeData == null || planeData.Length == 0)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Plane data is null or empty");
                return false;
            }

            if (surface == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Surface is null");
                return false;
            }

            if (surface.Frame == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Surface frame is null");
                return false;
            }

            if (frameInfo.Width <= 0 || frameInfo.Height <= 0)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Invalid frame dimensions: {frameInfo.Width}x{frameInfo.Height}");
                return false;
            }

            try
            {
                AVFrame* frame = surface.Frame;

                // 设置帧基本信息
                frame->Width = frameInfo.Width;
                frame->Height = frameInfo.Height;
                frame->Format = frameInfo.Format;

                Logger.Debug?.Print(LogClass.FFmpeg, 
                    $"Setting frame properties: {frameInfo.Width}x{frameInfo.Height}, format: {frameInfo.Format}");

                // 设置行大小
                for (int i = 0; i < 3 && i < 8; i++) // AVFrame.LineSize 通常是 8 个元素
                {
                    int lineSize = GetLineSizeForPlane(i, frameInfo);
                    frame->LineSize[i] = lineSize;
                    Logger.Debug?.Print(LogClass.FFmpeg, $"Plane {i} line size: {lineSize}");
                }

                // 复制平面数据
                int planesCopied = 0;
                for (int i = 0; i < planeData.Length && i < 8; i++) // AVFrame.Data 通常是 8 个指针
                {
                    if (planeData[i] != null && planeData[i].Length > 0 && frame->Data[i] != null)
                    {
                        if (CopyPlaneData(planeData[i], (IntPtr)frame->Data[i], frameInfo, i))
                        {
                            planesCopied++;
                        }
                    }
                    else
                    {
                        Logger.Debug?.Print(LogClass.FFmpeg, 
                            $"Plane {i} skipped - data: {(planeData[i] == null ? "null" : $"length={planeData[i].Length}")}, " +
                            $"frame data: {(frame->Data[i] == null ? "null" : "valid")}");
                    }
                }

                Logger.Debug?.Print(LogClass.FFmpeg, 
                    $"Copied {planesCopied} planes to surface, frame: {frameInfo.Width}x{frameInfo.Height}, format: {frameInfo.Format}");

                return planesCopied > 0;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Exception copying planes to surface: {ex.Message}");
                Logger.Debug?.Print(LogClass.FFmpeg, $"Copy exception details: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 复制单个平面数据
        /// </summary>
        private bool CopyPlaneData(byte[] sourceData, IntPtr destPtr, FrameInfo frameInfo, int planeIndex)
        {
            if (sourceData == null || sourceData.Length == 0)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Plane {planeIndex} source data is null or empty");
                return false;
            }

            if (destPtr == IntPtr.Zero)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Plane {planeIndex} destination pointer is null");
                return false;
            }

            try
            {
                int planeHeight = GetPlaneHeight(planeIndex, frameInfo.Height);
                int lineSize = GetLineSizeForPlane(planeIndex, frameInfo);
                int expectedDataSize = lineSize * planeHeight;

                Logger.Debug?.Print(LogClass.FFmpeg, 
                    $"Copying plane {planeIndex}: height={planeHeight}, lineSize={lineSize}, expectedSize={expectedDataSize}, actualSize={sourceData.Length}");

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
                    if (sourceOffset >= sourceData.Length)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, 
                            $"Plane {planeIndex} source data exhausted at row {row}/{planeHeight}");
                        break;
                    }

                    int bytesToCopy = Math.Min(lineSize, sourceData.Length - sourceOffset);
                    if (bytesToCopy <= 0) 
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, 
                            $"Plane {planeIndex} no bytes to copy at row {row}");
                        break;
                    }

                    IntPtr destRowPtr = IntPtr.Add(destPtr, row * lineSize);
                    Marshal.Copy(sourceData, sourceOffset, destRowPtr, bytesToCopy);
                    sourceOffset += bytesToCopy;
                }

                Logger.Debug?.Print(LogClass.FFmpeg, 
                    $"Plane {planeIndex} copy completed: {sourceOffset} bytes copied");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Exception copying plane {planeIndex}: {ex.Message}");
                return false;
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
                case 0: return frameInfo.Linesize0 > 0 ? frameInfo.Linesize0 : frameInfo.Width;
                case 1: return frameInfo.Linesize1 > 0 ? frameInfo.Linesize1 : Math.Max(1, frameInfo.Width / 2);
                case 2: return frameInfo.Linesize2 > 0 ? frameInfo.Linesize2 : Math.Max(1, frameInfo.Width / 2);
                default: return Math.Max(1, frameInfo.Width / (planeIndex + 1));
            }
        }

        /// <summary>
        /// 获取有效平面数量
        /// </summary>
        private int GetValidPlaneCount(byte[][] planeData)
        {
            int count = 0;
            if (planeData != null)
            {
                foreach (var plane in planeData)
                {
                    if (plane != null && plane.Length > 0) count++;
                }
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
            if (_disposed) return;

            Logger.Info?.Print(LogClass.FFmpeg, $"=== Disposing hardware decoder for {_codecName} ===");
            Logger.Info?.Print(LogClass.FFmpeg, GetStatistics());

            try
            {
                if (_initialized && _decoderContextId != 0)
                {
                    // 首先尝试 JNI 接口
                    try
                    {
                        Logger.Debug?.Print(LogClass.FFmpeg, "Destroying hardware decoder via JNI...");
                        DestroyHardwareDecoder(_decoderContextId);
                        Logger.Debug?.Print(LogClass.FFmpeg, "JNI destroy successful");
                    }
                    catch (Exception jniEx)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, $"JNI destroy failed: {jniEx.Message}");
                        try
                        {
                            Logger.Debug?.Print(LogClass.FFmpeg, "Destroying hardware decoder via C API...");
                            DestroyHardwareDecoderContext_C(_decoderContextId);
                            Logger.Debug?.Print(LogClass.FFmpeg, "C API destroy successful");
                        }
                        catch (Exception cApiEx)
                        {
                            Logger.Error?.Print(LogClass.FFmpeg, $"C API destroy also failed: {cApiEx.Message}");
                        }
                    }

                    // 清理硬件解码器
                    try
                    {
                        Logger.Debug?.Print(LogClass.FFmpeg, "Cleaning up FFmpeg hardware decoder...");
                        CleanupFFmpegHardwareDecoder();
                        Logger.Debug?.Print(LogClass.FFmpeg, "JNI cleanup successful");
                    }
                    catch (Exception cleanupEx)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, $"JNI cleanup failed: {cleanupEx.Message}");
                        try
                        {
                            CleanupFFmpegHardwareDecoder_C();
                            Logger.Debug?.Print(LogClass.FFmpeg, "C API cleanup successful");
                        }
                        catch (Exception cCleanupEx)
                        {
                            Logger.Error?.Print(LogClass.FFmpeg, $"C API cleanup also failed: {cCleanupEx.Message}");
                        }
                    }

                    _decoderContextId = 0;
                    _initialized = false;
                }

                _disposed = true;
                Logger.Info?.Print(LogClass.FFmpeg, $"=== Hardware decoder disposed for {_codecName} ===");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Exception disposing hardware decoder: {ex.Message}");
                Logger.Debug?.Print(LogClass.FFmpeg, $"Dispose exception details: {ex}");
            }
            finally
            {
                _disposed = true;
            }
        }

        ~FFmpegHardwareDecoder()
        {
            if (!_disposed)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Hardware decoder for {_codecName} was not properly disposed!");
                Dispose();
            }
        }
    }
}
