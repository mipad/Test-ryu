namespace Ryujinx.Graphics.Nvdec.MediaCodec.H264
{
    public sealed class MediaCodecH264Decoder : IMediaCodecDecoder, IDecoder
    {
        private readonly object _lock = new object();
        private AndroidJavaObject _mediaCodec;
        private AndroidJavaObject _mediaFormat;
        private AndroidJavaObject _surface;
        private MediaCodecContext _context;
        private readonly FrameQueue _frameQueue;
        private readonly H264ParameterSets _parameterSets;
        private bool _isInitialized;
        private bool _isRunning;
        
        public MediaCodecH264Decoder()
        {
            _frameQueue = new FrameQueue(4);
            _parameterSets = new H264ParameterSets();
        }
        
        public bool Initialize(string mimeType, int width, int height)
        {
            lock (_lock)
            {
                try
                {
                    // 创建 MediaCodec 解码器
                    _mediaCodec = AndroidJniWrapper.MediaCodec.CreateDecoderByType(mimeType);
                    if (_mediaCodec == null)
                        return false;
                    
                    // 创建 MediaFormat
                    _mediaFormat = AndroidJniWrapper.MediaFormat.CreateVideoFormat(
                        mimeType, width, height);
                    
                    // 设置 H.264 特定参数
                    ConfigureH264Format();
                    
                    // 创建上下文
                    _context = new MediaCodecContext();
                    
                    _isInitialized = true;
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorHandler.LogError("初始化MediaCodecH264Decoder失败", ex);
                    return false;
                }
            }
        }
        
        private void ConfigureH264Format()
        {
            // 设置帧率
            AndroidJniWrapper.MediaFormat.SetInteger(_mediaFormat, "frame-rate", 30);
            
            // 设置关键帧间隔
            AndroidJniWrapper.MediaFormat.SetInteger(_mediaFormat, "i-frame-interval", 1);
            
            // 设置比特率模式
            AndroidJniWrapper.MediaFormat.SetInteger(_mediaFormat, "bitrate-mode", 2); // VBR
            
            // 设置颜色格式
            AndroidJniWrapper.MediaFormat.SetInteger(_mediaFormat, "color-format", 0x15); // YUV420SemiPlanar
            
            // 设置优先级
            AndroidJniWrapper.MediaFormat.SetInteger(_mediaFormat, "priority", 0);
            
            // 设置编解码器配置文件
            if (_parameterSets.Sps != null)
            {
                var sps = _parameterSets.Sps;
                AndroidJniWrapper.MediaFormat.SetInteger(_mediaFormat, "profile", sps.ProfileIdc);
                AndroidJniWrapper.MediaFormat.SetInteger(_mediaFormat, "level", sps.LevelIdc);
            }
        }
        
        public bool ConfigureWithSurface(IMediaCodecSurface surface)
        {
            if (!_isInitialized || surface == null)
                return false;
            
            try
            {
                // 使用提供的 Surface 配置解码器
                int result = _mediaCodec.Call<int>("configure",
                    _mediaFormat,
                    surface.NativeSurface,  // 输出到 Surface
                    null,                   // crypto
                    0);                     // flags
                
                return result == 0;
            }
            catch (Exception ex)
            {
                ErrorHandler.LogError("配置MediaCodec失败", ex);
                return false;
            }
        }
        
        public bool Start()
        {
            if (!_isInitialized)
                return false;
            
            lock (_lock)
            {
                try
                {
                    _mediaCodec.Call("start");
                    _isRunning = true;
                    
                    // 启动解码线程
                    ThreadPool.QueueUserWorkItem(DecodeThread);
                    
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorHandler.LogError("启动MediaCodec失败", ex);
                    return false;
                }
            }
        }
        
        private void DecodeThread(object state)
        {
            while (_isRunning)
            {
                try
                {
                    ProcessOutput();
                }
                catch (Exception ex)
                {
                    ErrorHandler.LogError("解码线程出错", ex);
                    Thread.Sleep(1);
                }
            }
        }
        
        private void ProcessOutput()
        {
            // 获取输出缓冲区信息
            var bufferInfo = new AndroidJavaObject("android.media.MediaCodec$BufferInfo");
            int outputBufferIndex = _mediaCodec.Call<int>(
                "dequeueOutputBuffer", bufferInfo, 10000);
            
            if (outputBufferIndex >= 0)
            {
                // 处理解码后的帧
                ProcessDecodedFrame(outputBufferIndex, bufferInfo);
                
                // 释放输出缓冲区
                _mediaCodec.Call("releaseOutputBuffer", outputBufferIndex, false);
            }
            else if (outputBufferIndex == (int)MediaCodecStatus.OutputFormatChanged)
            {
                // 输出格式发生变化
                HandleOutputFormatChanged();
            }
            else if (outputBufferIndex == (int)MediaCodecStatus.TryAgainLater)
            {
                // 稍后重试
                Thread.Sleep(1);
            }
        }
        
        private void ProcessDecodedFrame(int bufferIndex, AndroidJavaObject bufferInfo)
        {
            // 获取输出缓冲区
            var outputBuffers = _mediaCodec.Call<AndroidJavaObject[]>("getOutputBuffers");
            var outputBuffer = outputBuffers[bufferIndex];
            
            // 获取缓冲区信息
            int offset = bufferInfo.Get<int>("offset");
            int size = bufferInfo.Get<int>("size");
            long presentationTimeUs = bufferInfo.Get<long>("presentationTimeUs");
            int flags = bufferInfo.Get<int>("flags");
            
            // 读取数据
            byte[] data = new byte[size];
            outputBuffer.Call<byte[]>("get", data);
            
            // 创建帧并加入队列
            var frame = new DecodedFrame
            {
                Data = data,
                Timestamp = presentationTimeUs,
                Flags = flags,
                Width = _context.Width,
                Height = _context.Height
            };
            
            _frameQueue.Enqueue(frame);
        }
        
        public bool Decode(ref H264PictureInfo info, ISurface surface, ReadOnlySpan<byte> bitstream)
        {
            if (!_isInitialized || !_isRunning)
                return false;
            
            lock (_lock)
            {
                try
                {
                    // 获取输入缓冲区
                    int inputBufferIndex = _mediaCodec.Call<int>("dequeueInputBuffer", 10000);
                    if (inputBufferIndex < 0)
                        return false;
                    
                    // 获取输入缓冲区
                    var inputBuffers = _mediaCodec.Call<AndroidJavaObject[]>("getInputBuffers");
                    var inputBuffer = inputBuffers[inputBufferIndex];
                    
                    // 清空并填充数据
                    inputBuffer.Call("clear");
                    
                    // 将数据写入缓冲区
                    // 注意：需要处理 Android ByteBuffer 的写入
                    var byteArray = AndroidJNI.NewByteArray(bitstream.Length);
                    AndroidJNI.SetByteArrayRegion(byteArray, 0, bitstream.Length, bitstream.ToArray());
                    inputBuffer.Call("put", byteArray);
                    
                    // 提交给解码器
                    long pts = CalculatePresentationTime(info);
                    _mediaCodec.Call("queueInputBuffer",
                        inputBufferIndex,
                        0,                      // offset
                        bitstream.Length,       // size
                        pts,                    // presentationTimeUs
                        0);                     // flags
                    
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorHandler.LogError("解码失败", ex);
                    return false;
                }
            }
        }
        
        private long CalculatePresentationTime(H264PictureInfo info)
        {
            // 根据 H.264 信息计算时间戳
            // 这里简化处理，实际需要根据 PTS 计算
            return _context.FrameCount * 1000000L / 30; // 假设 30fps
        }
        
        public ISurface CreateSurface(int width, int height)
        {
            // 创建适配 MediaCodec 的 Surface
            return new MediaCodecSurface(width, height);
        }
        
        public void Dispose()
        {
            lock (_lock)
            {
                _isRunning = false;
                
                if (_mediaCodec != null)
                {
                    try
                    {
                        _mediaCodec.Call("stop");
                        _mediaCodec.Call("release");
                    }
                    catch { }
                    _mediaCodec?.Dispose();
                    _mediaCodec = null;
                }
                
                _mediaFormat?.Dispose();
                _surface?.Dispose();
                
                _frameQueue.Clear();
            }
        }
    }
}
