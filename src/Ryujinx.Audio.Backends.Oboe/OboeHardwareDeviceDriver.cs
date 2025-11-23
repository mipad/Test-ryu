// OboeHardwareDeviceDriver.cs (多实例架构 + 共享内存)
#if ANDROID
using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Audio.Backends.Oboe
{
    public class OboeHardwareDeviceDriver : IHardwareDeviceDriver, IDisposable
    {
        // ========== P/Invoke 声明 - 多实例接口 ==========
        [DllImport("libryujinxjni", EntryPoint = "createOboeRenderer")]
        private static extern IntPtr createOboeRenderer();

        [DllImport("libryujinxjni", EntryPoint = "destroyOboeRenderer")]
        private static extern void destroyOboeRenderer(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "initOboeRenderer")]
        private static extern bool initOboeRenderer(IntPtr renderer, int sample_rate, int channel_count, int sample_format);

        [DllImport("libryujinxjni", EntryPoint = "shutdownOboeRenderer")]
        private static extern void shutdownOboeRenderer(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "writeOboeRendererAudioRaw")]
        private static extern bool writeOboeRendererAudioRaw(IntPtr renderer, byte[] audioData, int num_frames, int sample_format);

        [DllImport("libryujinxjni", EntryPoint = "setOboeRendererVolume")]
        private static extern void setOboeRendererVolume(IntPtr renderer, float volume);

        [DllImport("libryujinxjni", EntryPoint = "isOboeRendererInitialized")]
        private static extern bool isOboeRendererInitialized(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "isOboeRendererPlaying")]
        private static extern bool isOboeRendererPlaying(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "getOboeRendererBufferedFrames")]
        private static extern int getOboeRendererBufferedFrames(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "resetOboeRenderer")]
        private static extern void resetOboeRenderer(IntPtr renderer);

        // ========== 共享内存 P/Invoke ==========
        [DllImport("libryujinxjni", EntryPoint = "initSharedAudioMemory")]
        private static extern bool initSharedAudioMemory(int size);

        [DllImport("libryujinxjni", EntryPoint = "getSharedAudioMemoryAddr")]
        private static extern long getSharedAudioMemoryAddr();

        [DllImport("libryujinxjni", EntryPoint = "writeSharedAudioData")]
        private static extern void writeSharedAudioData(IntPtr renderer, int data_size, int sample_rate, int channels, int sample_format);

        [DllImport("libryujinxjni", EntryPoint = "getSharedAudioAvailable")]
        private static extern int getSharedAudioAvailable();

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions = new();
        private Thread _updateThread;
        private bool _stillRunning = true;

        // 共享内存相关
        private const int SHARED_MEMORY_SIZE = 2 * 1024 * 1024; // 2MB 共享内存
        private bool _sharedMemoryInitialized = false;
        private unsafe SharedAudioHeader* _sharedMemoryHeader;
        private byte* _sharedAudioData;

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct SharedAudioHeader
        {
            public uint write_pos;
            public uint read_pos;
            public uint data_size;
            public uint buffer_size;
            public uint sample_rate;
            public uint channels;
            public uint sample_format;
        }

        public float Volume { get; set; } = 1.0f;

        // ========== 构造与生命周期 ==========
        public OboeHardwareDeviceDriver()
        {
            InitializeSharedMemory();
            StartUpdateThread();
        }

        private void InitializeSharedMemory()
        {
            try
            {
                if (initSharedAudioMemory(SHARED_MEMORY_SIZE))
                {
                    long sharedMemAddr = getSharedAudioMemoryAddr();
                    if (sharedMemAddr != 0)
                    {
                        unsafe
                        {
                            _sharedMemoryHeader = (SharedAudioHeader*)sharedMemAddr;
                            _sharedAudioData = (byte*)(sharedMemAddr + sizeof(SharedAudioHeader));
                            
                            _sharedMemoryHeader->write_pos = 0;
                            _sharedMemoryHeader->read_pos = 0;
                            _sharedMemoryHeader->data_size = 0;
                            _sharedMemoryHeader->buffer_size = (uint)(SHARED_MEMORY_SIZE - sizeof(SharedAudioHeader));
                            
                            _sharedMemoryInitialized = true;
                            Logger.Info?.Print(LogClass.Audio, "Shared audio memory initialized successfully");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Audio, $"Failed to initialize shared audio memory: {ex.Message}");
                _sharedMemoryInitialized = false;
            }
        }

        private void StartUpdateThread()
        {
            _updateThread = new Thread(() =>
            {
                while (_stillRunning)
                {
                    try
                    {
                        Thread.Sleep(10); // 10ms更新频率
                        
                        foreach (var session in _sessions.Keys)
                        {
                            if (session.IsActive)
                            {
                                int bufferedFrames = session.GetBufferedFrames();
                                session.UpdatePlaybackStatus(bufferedFrames);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Update thread error: {ex.Message}");
                    }
                }
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _updateThread.Start();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _stillRunning = false;
                    _updateThread?.Join(100);
                    
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                }
                _disposed = true;
            }
        }

        // ========== 设备能力查询 ==========
        public bool SupportsSampleRate(uint sampleRate) => true;

        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat == SampleFormat.PcmInt16 || 
            sampleFormat == SampleFormat.PcmInt32 ||
            sampleFormat == SampleFormat.PcmFloat;

        public bool SupportsChannelCount(uint channelCount) =>
            channelCount is 1 or 2 or 6;

        public bool SupportsDirection(IHardwareDeviceDriver.Direction direction) =>
            direction == IHardwareDeviceDriver.Direction.Output;

        // ========== 事件 ==========
        public ManualResetEvent GetPauseEvent() => _pauseEvent;
        public ManualResetEvent GetUpdateRequiredEvent() => _updateRequiredEvent;

        // ========== 打开设备会话 ==========
        public IHardwareDeviceSession OpenDeviceSession(
            IHardwareDeviceDriver.Direction direction,
            IVirtualMemoryManager memoryManager,
            SampleFormat sampleFormat,
            uint sampleRate,
            uint channelCount)
        {
            if (direction != IHardwareDeviceDriver.Direction.Output)
                throw new ArgumentException($"Unsupported direction: {direction}");

            if (!SupportsChannelCount(channelCount))
                throw new ArgumentException($"Unsupported channel count: {channelCount}");

            if (!SupportsSampleFormat(sampleFormat))
                throw new ArgumentException($"Unsupported sample format: {sampleFormat}");

            if (sampleRate == 0)
            {
                sampleRate = 48000;
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount, _sharedMemoryInitialized);
            _sessions.TryAdd(session, 0);
            
            return session;
        }

        private bool Unregister(OboeAudioSession session) 
        {
            return _sessions.TryRemove(session, out _);
        }

        // ========== 音频会话类 ==========
        private class OboeAudioSession : HardwareDeviceSessionOutputBase
        {
            private readonly OboeHardwareDeviceDriver _driver;
            private readonly ConcurrentQueue<OboeAudioBuffer> _queuedBuffers = new();
            private ulong _totalWrittenSamples;
            private ulong _totalPlayedSamples;
            private bool _active;
            private float _volume;
            private readonly int _channelCount;
            private readonly uint _sampleRate;
            private readonly SampleFormat _sampleFormat;
            private readonly IntPtr _rendererPtr;
            private readonly bool _useSharedMemory;

            // 共享内存相关
            private unsafe SharedAudioHeader* _sharedHeader;
            private unsafe byte* _sharedData;
            private readonly object _sharedMemoryLock = new object();

            public bool IsActive => _active;

            public OboeAudioSession(
                OboeHardwareDeviceDriver driver,
                IVirtualMemoryManager memoryManager,
                SampleFormat sampleFormat,
                uint sampleRate,
                uint channelCount,
                bool useSharedMemory) 
                : base(memoryManager, sampleFormat, sampleRate, channelCount)
            {
                _driver = driver;
                _channelCount = (int)channelCount;
                _sampleRate = sampleRate;
                _sampleFormat = sampleFormat;
                _volume = 1.0f;
                _useSharedMemory = useSharedMemory;
                
                // 创建独立的渲染器实例
                _rendererPtr = createOboeRenderer();
                if (_rendererPtr == IntPtr.Zero)
                {
                    throw new Exception("Failed to create Oboe audio renderer");
                }

                // 初始化渲染器
                int formatValue = SampleFormatToInt(sampleFormat);
                if (!initOboeRenderer(_rendererPtr, (int)sampleRate, (int)channelCount, formatValue))
                {
                    destroyOboeRenderer(_rendererPtr);
                    throw new Exception("Failed to initialize Oboe audio renderer");
                }

                setOboeRendererVolume(_rendererPtr, _volume);

                // 初始化共享内存
                if (_useSharedMemory)
                {
                    unsafe
                    {
                        long sharedMemAddr = getSharedAudioMemoryAddr();
                        if (sharedMemAddr != 0)
                        {
                            _sharedHeader = (SharedAudioHeader*)sharedMemAddr;
                            _sharedData = (byte*)(sharedMemAddr + sizeof(SharedAudioHeader));
                            
                            // 更新音频参数到共享内存头
                            _sharedHeader->sample_rate = sampleRate;
                            _sharedHeader->channels = channelCount;
                            _sharedHeader->sample_format = (uint)formatValue;
                        }
                    }
                }
            }

            public int GetBufferedFrames()
            {
                return _rendererPtr != IntPtr.Zero ? getOboeRendererBufferedFrames(_rendererPtr) : 0;
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                try
                {
                    // 计算已播放的样本数
                    ulong playedSamples = _totalWrittenSamples - (ulong)(bufferedFrames * _channelCount);
                    
                    // 防止回退
                    if (playedSamples < _totalPlayedSamples)
                    {
                        _totalPlayedSamples = playedSamples;
                        return;
                    }
                    
                    ulong availableSampleCount = playedSamples - _totalPlayedSamples;
                    
                    // 更新缓冲区播放状态
                    while (availableSampleCount > 0 && _queuedBuffers.TryPeek(out OboeAudioBuffer driverBuffer))
                    {
                        ulong sampleStillNeeded = driverBuffer.SampleCount - driverBuffer.SamplePlayed;
                        ulong playedAudioBufferSampleCount = Math.Min(sampleStillNeeded, availableSampleCount);
                        
                        driverBuffer.SamplePlayed += playedAudioBufferSampleCount;
                        availableSampleCount -= playedAudioBufferSampleCount;
                        _totalPlayedSamples += playedAudioBufferSampleCount;
                        
                        // 如果缓冲区播放完毕，移除它
                        if (driverBuffer.SamplePlayed == driverBuffer.SampleCount)
                        {
                            _queuedBuffers.TryDequeue(out _);
                            _driver.GetUpdateRequiredEvent().Set();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Error in UpdatePlaybackStatus: {ex.Message}");
                }
            }

            public override void Dispose()
            {
                Stop();
                
                if (_rendererPtr != IntPtr.Zero)
                {
                    shutdownOboeRenderer(_rendererPtr);
                    destroyOboeRenderer(_rendererPtr);
                }
                
                _driver.Unregister(this);
            }

            public override void PrepareToClose() 
            {
                Stop();
            }

            public override void Start() 
            {
                if (!_active)
                {
                    _active = true;
                }
            }

            public override void Stop() 
            {
                if (_active)
                {
                    _active = false;
                    _queuedBuffers.Clear();
                }
            }

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) Start();

                if (buffer.Data == null || buffer.Data.Length == 0) return;

                // 计算帧数
                int bytesPerSample = GetBytesPerSample(_sampleFormat);
                int frameCount = buffer.Data.Length / (bytesPerSample * _channelCount);

                if (_useSharedMemory)
                {
                    // 使用共享内存路径
                    WriteToSharedMemory(buffer.Data, frameCount);
                }
                else
                {
                    // 传统路径
                    int formatValue = SampleFormatToInt(_sampleFormat);
                    bool writeSuccess = writeOboeRendererAudioRaw(_rendererPtr, buffer.Data, frameCount, formatValue);

                    if (writeSuccess)
                    {
                        ulong sampleCount = (ulong)(frameCount * _channelCount);
                        _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, sampleCount));
                        _totalWrittenSamples += sampleCount;
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.Audio, 
                            $"Audio write failed: {frameCount} frames dropped, Format={_sampleFormat}, Rate={_sampleRate}Hz");
                        
                        // 重置渲染器
                        resetOboeRenderer(_rendererPtr);
                    }
                }
            }

            private unsafe void WriteToSharedMemory(byte[] audioData, int frameCount)
            {
                lock (_sharedMemoryLock)
                {
                    if (_sharedHeader == null || _sharedData == null) return;

                    int dataSize = audioData.Length;
                    uint bufferSize = _sharedHeader->buffer_size;
                    uint writePos = _sharedHeader->write_pos;
                    uint readPos = _sharedHeader->read_pos;
                    uint currentDataSize = _sharedHeader->data_size;

                    // 检查可用空间
                    uint availableSpace = bufferSize - currentDataSize;
                    if (availableSpace < dataSize)
                    {
                        // 缓冲区不足，等待空间释放
                        int waitCount = 0;
                        while (availableSpace < dataSize && waitCount < 50) // 最多等待5ms
                        {
                            Thread.Sleep(100); // 100us
                            availableSpace = bufferSize - _sharedHeader->data_size;
                            waitCount++;
                        }

                        if (availableSpace < dataSize)
                        {
                            Logger.Warning?.Print(LogClass.Audio, 
                                $"Shared audio buffer overflow: need {dataSize}, available {availableSpace}");
                            return;
                        }
                    }

                    // 写入数据到环形缓冲区
                    if (writePos + dataSize <= bufferSize)
                    {
                        Marshal.Copy(audioData, 0, (IntPtr)(_sharedData + writePos), dataSize);
                        writePos += (uint)dataSize;
                        if (writePos == bufferSize)
                        {
                            writePos = 0;
                        }
                    }
                    else
                    {
                        uint firstPart = bufferSize - writePos;
                        Marshal.Copy(audioData, 0, (IntPtr)(_sharedData + writePos), (int)firstPart);
                        Marshal.Copy(audioData, (int)firstPart, (IntPtr)_sharedData, dataSize - (int)firstPart);
                        writePos = (uint)(dataSize - firstPart);
                    }

                    // 更新共享内存头
                    _sharedHeader->write_pos = writePos;
                    _sharedHeader->data_size = currentDataSize + (uint)dataSize;

                    // 通知Native端有新数据
                    int formatValue = SampleFormatToInt(_sampleFormat);
                    writeSharedAudioData(_rendererPtr, dataSize, (int)_sampleRate, _channelCount, formatValue);

                    // 更新本地队列状态
                    ulong sampleCount = (ulong)(frameCount * _channelCount);
                    _queuedBuffers.Enqueue(new OboeAudioBuffer((ulong)audioData.Length, sampleCount));
                    _totalWrittenSamples += sampleCount;
                }
            }

            private int SampleFormatToInt(SampleFormat format)
            {
                return format switch
                {
                    SampleFormat.PcmInt16 => 1,
                    SampleFormat.PcmInt24 => 2,
                    SampleFormat.PcmInt32 => 3,
                    SampleFormat.PcmFloat => 4,
                    _ => 1,
                };
            }

            private int GetBytesPerSample(SampleFormat format)
            {
                return format switch
                {
                    SampleFormat.PcmInt16 => 2,
                    SampleFormat.PcmInt24 => 3,
                    SampleFormat.PcmInt32 => 4,
                    SampleFormat.PcmFloat => 4,
                    _ => 2,
                };
            }

            public override bool WasBufferFullyConsumed(AudioBuffer buffer)
            {
                return !_queuedBuffers.TryPeek(out var driverBuffer) || 
                       driverBuffer.DriverIdentifier != (ulong)buffer.Data.Length;
            }

            public override void SetVolume(float volume)
            {
                _volume = Math.Clamp(volume, 0.0f, 1.0f);
                if (_rendererPtr != IntPtr.Zero)
                {
                    setOboeRendererVolume(_rendererPtr, _volume);
                }
            }

            public override float GetVolume() => _volume;

            public override ulong GetPlayedSampleCount()
            {
                return _totalPlayedSamples;
            }

            public override void UnregisterBuffer(AudioBuffer buffer)
            {
                if (_queuedBuffers.TryPeek(out var driverBuffer) && 
                    driverBuffer.DriverIdentifier == (ulong)buffer.Data.Length)
                {
                    _queuedBuffers.TryDequeue(out _);
                }
            }
        }

        // ========== 内部缓冲区类 ==========
        private class OboeAudioBuffer
        {
            public readonly ulong DriverIdentifier;
            public readonly ulong SampleCount;
            public ulong SamplePlayed;

            public OboeAudioBuffer(ulong driverIdentifier, ulong sampleCount)
            {
                DriverIdentifier = driverIdentifier;
                SampleCount = sampleCount;
                SamplePlayed = 0;
            }
        }
    }
}
#endif