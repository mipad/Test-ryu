using System;
using System.Collections.Concurrent;
using System.Threading;
using Ryujinx.Graphics.Nvdec.MediaCodec;
using Ryujinx.Graphics.Nvdec.MediaCodec.Common;
using Ryujinx.Graphics.Nvdec.MediaCodec.Interfaces;

namespace Ryujinx.Graphics.Nvdec.MediaCodec.H264
{
    public sealed class MediaCodecH264Decoder : IMediaCodecDecoder, IDecoder
    {
        private readonly object _lock = new object();
        private AndroidJavaObject _mediaCodec;
        private AndroidJavaObject _mediaFormat;
        private AndroidJavaObject _surface;
        private MediaCodecContext _context;
        private readonly BlockingCollection<DecodedFrame> _frameQueue;
        private readonly H264ParameterSets _parameterSets;
        private bool _isInitialized;
        private bool _isRunning;
        private Thread _decodeThread;
        
        // 事件
        public event Action<MediaCodecEvent> OnEvent;
        
        // 缓冲区数组
        private ByteBuffer[] _inputBuffers;
        private ByteBuffer[] _outputBuffers;
        
        // MediaCodec 状态枚举
        private enum MediaCodecStatus
        {
            TryAgainLater = -1,
            OutputFormatChanged = -2,
            OutputBuffersChanged = -3
        }
        
        public MediaCodecH264Decoder()
        {
            _frameQueue = new BlockingCollection<DecodedFrame>(new ConcurrentQueue<DecodedFrame>(), 4);
            _parameterSets = new H264ParameterSets();
        }
        
        // IMediaCodecDecoder 接口实现
        public bool Initialize(string mimeType, int width, int height)
        {
            lock (_lock)
            {
                if (_isInitialized)
                    return true;
                
                try
                {
                    // 检查编解码器是否支持
                    if (!MediaCodecConfig.IsCodecSupported(mimeType))
                    {
                        Console.WriteLine($"不支持 MIME 类型: {mimeType}");
                        return false;
                    }
                    
                    // 获取最佳解码器名称
                    string decoderName = MediaCodecConfig.GetBestSupportedCodec(mimeType);
                    if (string.IsNullOrEmpty(decoderName))
                    {
                        Console.WriteLine("找不到合适的解码器");
                        return false;
                    }
                    
                    Console.WriteLine($"使用解码器: {decoderName}");
                    
                    // 创建 MediaCodec 解码器
                    _mediaCodec = AndroidJniWrapper.MediaCodec.CreateDecoderByType(mimeType);
                    if (_mediaCodec == null)
                    {
                        Console.WriteLine("创建 MediaCodec 失败");
                        return false;
                    }
                    
                    // 创建上下文
                    _context = new MediaCodecContext();
                    var config = MediaCodecConfig.CreateDefaultConfig(mimeType, width, height);
                    _context.UpdateConfiguration(config);
                    
                    _isInitialized = true;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"初始化 MediaCodecH264Decoder 失败: {ex.Message}");
                    return false;
                }
            }
        }
        
        // IDecoder 接口实现
        bool IDecoder.Initialize(int width, int height)
        {
            return Initialize(MediaCodecConfig.MimeTypes.H264, width, height);
        }
        
        public bool Configure(MediaFormatConfig config)
        {
            if (!_isInitialized || _mediaCodec == null)
                return false;
            
            lock (_lock)
            {
                try
                {
                    // 从配置创建 Android MediaFormat
                    _mediaFormat = CreateMediaFormatFromConfig(config);
                    
                    // 配置解码器
                    int result = _mediaCodec.Call<int>("configure",
                        _mediaFormat,
                        _surface,    // Surface
                        null,        // crypto
                        0);          // flags
                    
                    return result == 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"配置 MediaCodec 失败: {ex.Message}");
                    return false;
                }
            }
        }
        
        public bool Start()
        {
            if (!_isInitialized || _mediaCodec == null)
                return false;
            
            lock (_lock)
            {
                try
                {
                    _mediaCodec.Call("start");
                    _isRunning = true;
                    
                    // 获取缓冲区
                    UpdateBuffers();
                    
                    // 启动解码线程
                    _decodeThread = new Thread(DecodeThreadProc);
                    _decodeThread.Start();
                    
                    _context.SetRunning(true);
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"启动 MediaCodec 失败: {ex.Message}");
                    return false;
                }
            }
        }
        
        public bool Stop()
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return true;
                
                _isRunning = false;
                
                try
                {
                    _mediaCodec?.Call("stop");
                    _context.SetRunning(false);
                    
                    // 等待解码线程结束
                    if (_decodeThread != null && _decodeThread.IsAlive)
                    {
                        _decodeThread.Join(1000);
                    }
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"停止 MediaCodec 失败: {ex.Message}");
                    return false;
                }
            }
        }
        
        public bool Release()
        {
            Dispose();
            return true;
        }
        
        public int DequeueInputBuffer(long timeoutUs)
        {
            if (!_isInitialized || !_isRunning || _mediaCodec == null)
                return -1;
            
            try
            {
                return _mediaCodec.Call<int>("dequeueInputBuffer", timeoutUs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DequeueInputBuffer 失败: {ex.Message}");
                return -1;
            }
        }
        
        public ByteBuffer GetInputBuffer(int index)
        {
            if (!_isInitialized || _mediaCodec == null || index < 0)
                return null;
            
            try
            {
                // 获取输入缓冲区
                var inputBuffers = _mediaCodec.Call<AndroidJavaObject[]>("getInputBuffers");
                if (inputBuffers == null || index >= inputBuffers.Length)
                    return null;
                
                var buffer = inputBuffers[index];
                byte[] data = buffer.Call<byte[]>("array");
                
                return new ByteBuffer(data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetInputBuffer 失败: {ex.Message}");
                return null;
            }
        }
        
        public void QueueInputBuffer(int index, int offset, int size, long presentationTimeUs, int flags)
        {
            if (!_isInitialized || !_isRunning || _mediaCodec == null || index < 0)
                return;
            
            try
            {
                _mediaCodec.Call("queueInputBuffer", index, offset, size, presentationTimeUs, flags);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"QueueInputBuffer 失败: {ex.Message}");
            }
        }
        
        public int DequeueOutputBuffer(ref BufferInfo info, long timeoutUs)
        {
            if (!_isInitialized || !_isRunning || _mediaCodec == null)
                return -1;
            
            try
            {
                var bufferInfo = new AndroidJavaObject("android.media.MediaCodec$BufferInfo");
                int index = _mediaCodec.Call<int>("dequeueOutputBuffer", bufferInfo, timeoutUs);
                
                if (index >= 0)
                {
                    info.Offset = bufferInfo.Get<int>("offset");
                    info.Size = bufferInfo.Get<int>("size");
                    info.PresentationTimeUs = bufferInfo.Get<long>("presentationTimeUs");
                    info.Flags = bufferInfo.Get<int>("flags");
                }
                else if (index == (int)MediaCodecStatus.OutputFormatChanged)
                {
                    OnEvent?.Invoke(MediaCodecEvent.OutputFormatChanged);
                    UpdateOutputFormat();
                }
                else if (index == (int)MediaCodecStatus.OutputBuffersChanged)
                {
                    OnEvent?.Invoke(MediaCodecEvent.OutputBuffersChanged);
                    UpdateBuffers();
                }
                
                return index;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DequeueOutputBuffer 失败: {ex.Message}");
                return -1;
            }
        }
        
        public ByteBuffer GetOutputBuffer(int index)
        {
            if (!_isInitialized || _mediaCodec == null || index < 0)
                return null;
            
            try
            {
                // 获取输出缓冲区
                var outputBuffers = _mediaCodec.Call<AndroidJavaObject[]>("getOutputBuffers");
                if (outputBuffers == null || index >= outputBuffers.Length)
                    return null;
                
                var buffer = outputBuffers[index];
                byte[] data = buffer.Call<byte[]>("array");
                
                return new ByteBuffer(data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetOutputBuffer 失败: {ex.Message}");
                return null;
            }
        }
        
        public void ReleaseOutputBuffer(int index, bool render)
        {
            if (!_isInitialized || !_isRunning || _mediaCodec == null || index < 0)
                return;
            
            try
            {
                _mediaCodec.Call("releaseOutputBuffer", index, render);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ReleaseOutputBuffer 失败: {ex.Message}");
            }
        }
        
        // IDecoder 接口实现
        public void Decode(byte[] data)
        {
            if (!_isInitialized || !_isRunning || data == null || data.Length == 0)
                return;
            
            lock (_lock)
            {
                try
                {
                    // 获取输入缓冲区
                    int inputBufferIndex = DequeueInputBuffer(10000);
                    if (inputBufferIndex < 0)
                        return;
                    
                    var inputBuffer = GetInputBuffer(inputBufferIndex);
                    if (inputBuffer == null)
                        return;
                    
                    // 检查缓冲区大小
                    if (inputBuffer.Capacity < data.Length)
                    {
                        Console.WriteLine($"输入缓冲区太小: {inputBuffer.Capacity} < {data.Length}");
                        return;
                    }
                    
                    // 复制数据到缓冲区
                    Array.Copy(data, 0, inputBuffer.Data, 0, data.Length);
                    
                    // 提交给解码器
                    long presentationTimeUs = DateTime.UtcNow.Ticks / 10; // 转换为微秒
                    QueueInputBuffer(inputBufferIndex, 0, data.Length, presentationTimeUs, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"解码失败: {ex.Message}");
                }
            }
        }
        
        public void Flush()
        {
            if (!_isInitialized || _mediaCodec == null)
                return;
            
            lock (_lock)
            {
                try
                {
                    _mediaCodec.Call("flush");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Flush 失败: {ex.Message}");
                }
            }
        }
        
        public void Reset()
        {
            Stop();
            Flush();
            _frameQueue?.Clear();
            _context?.Reset();
        }
        
        // 辅助方法
        private void UpdateBuffers()
        {
            try
            {
                // 更新输入缓冲区
                var inputBuffers = _mediaCodec.Call<AndroidJavaObject[]>("getInputBuffers");
                if (inputBuffers != null)
                {
                    _inputBuffers = new ByteBuffer[inputBuffers.Length];
                    for (int i = 0; i < inputBuffers.Length; i++)
                    {
                        byte[] data = inputBuffers[i].Call<byte[]>("array");
                        _inputBuffers[i] = new ByteBuffer(data);
                    }
                }
                
                // 更新输出缓冲区
                var outputBuffers = _mediaCodec.Call<AndroidJavaObject[]>("getOutputBuffers");
                if (outputBuffers != null)
                {
                    _outputBuffers = new ByteBuffer[outputBuffers.Length];
                    for (int i = 0; i < outputBuffers.Length; i++)
                    {
                        byte[] data = outputBuffers[i].Call<byte[]>("array");
                        _outputBuffers[i] = new ByteBuffer(data);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UpdateBuffers 失败: {ex.Message}");
            }
        }
        
        private void UpdateOutputFormat()
        {
            try
            {
                var format = _mediaCodec.Call<AndroidJavaObject>("getOutputFormat");
                if (format != null)
                {
                    int width = format.Get<int>("width");
                    int height = format.Get<int>("height");
                    int colorFormat = format.Get<int>("color-format");
                    
                    // 更新上下文
                    var config = new Dictionary<string, object>
                    {
                        ["width"] = width,
                        ["height"] = height,
                        ["color-format"] = colorFormat
                    };
                    
                    _context.UpdateConfiguration(config);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UpdateOutputFormat 失败: {ex.Message}");
            }
        }
        
        private AndroidJavaObject CreateMediaFormatFromConfig(MediaFormatConfig config)
        {
            var mediaFormat = new AndroidJavaObject("android.media.MediaFormat");
            
            var dict = config.ToDictionary();
            foreach (var kvp in dict)
            {
                var value = kvp.Value;
                if (value is string str)
                {
                    mediaFormat.Call("setString", kvp.Key, str);
                }
                else if (value is int intVal)
                {
                    mediaFormat.Call("setInteger", kvp.Key, intVal);
                }
                else if (value is long longVal)
                {
                    mediaFormat.Call("setLong", kvp.Key, longVal);
                }
                else if (value is float floatVal)
                {
                    mediaFormat.Call("setFloat", kvp.Key, floatVal);
                }
                else if (value is byte[] bytes)
                {
                    var byteBuffer = new AndroidJavaObject("java.nio.ByteBuffer", bytes);
                    mediaFormat.Call("setByteBuffer", kvp.Key, byteBuffer);
                }
            }
            
            return mediaFormat;
        }
        
        private void DecodeThreadProc()
        {
            while (_isRunning)
            {
                try
                {
                    var info = new BufferInfo();
                    int outputBufferIndex = DequeueOutputBuffer(ref info, 10000);
                    
                    if (outputBufferIndex >= 0)
                    {
                        ProcessOutputBuffer(outputBufferIndex, info);
                        ReleaseOutputBuffer(outputBufferIndex, true);
                    }
                    
                    Thread.Sleep(1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"解码线程错误: {ex.Message}");
                    Thread.Sleep(10);
                }
            }
        }
        
        private void ProcessOutputBuffer(int index, BufferInfo info)
        {
            var buffer = GetOutputBuffer(index);
            if (buffer == null || info.Size <= 0)
                return;
            
            // 提取数据
            byte[] frameData = new byte[info.Size];
            Array.Copy(buffer.Data, info.Offset, frameData, 0, info.Size);
            
            // 创建解码帧
            var frame = new DecodedFrame
            {
                Data = frameData,
                Timestamp = info.PresentationTimeUs,
                Flags = info.Flags,
                Width = _context.Width,
                Height = _context.Height
            };
            
            // 加入队列
            if (!_frameQueue.TryAdd(frame))
            {
                Console.WriteLine("帧队列已满，丢弃帧");
            }
            
            _context.IncrementFrameCount();
        }
        
        public void Dispose()
        {
            lock (_lock)
            {
                _isRunning = false;
                
                // 停止解码线程
                if (_decodeThread != null && _decodeThread.IsAlive)
                {
                    _decodeThread.Join(1000);
                    _decodeThread = null;
                }
                
                // 释放 MediaCodec
                if (_mediaCodec != null)
                {
                    try
                    {
                        _mediaCodec.Call("stop");
                        _mediaCodec.Call("release");
                    }
                    catch { }
                    _mediaCodec.Dispose();
                    _mediaCodec = null;
                }
                
                _mediaFormat?.Dispose();
                _surface?.Dispose();
                
                _frameQueue?.Dispose();
                
                _isInitialized = false;
            }
        }
    }
}
