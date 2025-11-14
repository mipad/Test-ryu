// OboeHardwareDeviceDriver.cs 
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
        // ========== P/Invoke 声明 ==========
        [DllImport("libryujinxjni", EntryPoint = "initOboeAudio")]
        private static extern bool initOboeAudio(int sample_rate, int channel_count);

        [DllImport("libryujinxjni", EntryPoint = "shutdownOboeAudio")]
        private static extern void shutdownOboeAudio();

        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudio")]
        private static extern bool writeOboeAudio(short[] audioData, int num_frames);

        [DllImport("libryujinxjni", EntryPoint = "setOboeVolume")]
        private static extern void setOboeVolume(float volume);

        [DllImport("libryujinxjni", EntryPoint = "isOboeInitialized")]
        private static extern bool isOboeInitialized();

        [DllImport("libryujinxjni", EntryPoint = "isOboePlaying")]
        private static extern bool isOboePlaying();

        [DllImport("libryujinxjni", EntryPoint = "getOboeBufferedFrames")]
        private static extern int getOboeBufferedFrames();

        [DllImport("libryujinxjni", EntryPoint = "resetOboeAudio")]
        private static extern void resetOboeAudio();

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private float _volume = 1.0f;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions = new();
        private bool _isOboeInitialized = false;
        private Thread _updateThread;
        private bool _stillRunning = true;
        private readonly object _initLock = new object();
        private int _currentChannelCount = 2;

        // 性能监控变量
        private int _consecutiveFailures = 0;
        private readonly object _performanceLock = new object();

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0f, 1.0f);
                setOboeVolume(_volume);
                Logger.Debug?.Print(LogClass.Audio, $"Oboe volume set to {_volume:F2}");
            }
        }

        // ========== 构造与生命周期 ==========
        public OboeHardwareDeviceDriver()
        {
            StartUpdateThread();
            Logger.Info?.Print(LogClass.Audio, "OboeHardwareDeviceDriver initialized");
        }

        private void StartUpdateThread()
        {
            _updateThread = new Thread(() =>
            {
                while (_stillRunning)
                {
                    try
                    {
                        // 增加更新间隔，减少CPU占用 (从5ms增加到20ms)
                        Thread.Sleep(20);

                        if (_isOboeInitialized)
                        {
                            int bufferedFrames = getOboeBufferedFrames();
                            
                            foreach (var session in _sessions.Keys)
                            {
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
                Priority = ThreadPriority.BelowNormal // 降低线程优先级
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
                    Logger.Info?.Print(LogClass.Audio, "Disposing OboeHardwareDeviceDriver");
                    
                    _stillRunning = false;
                    _updateThread?.Join(100);
                    
                    shutdownOboeAudio();
                    _isOboeInitialized = false;
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                }
                _disposed = true;
            }
        }

        // ========== 设备能力查询 ==========
        public bool SupportsSampleRate(uint sampleRate) =>
            sampleRate == Constants.TargetSampleRate;

        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat == SampleFormat.PcmInt16;

        public bool SupportsChannelCount(uint channelCount) =>
            channelCount == 1 || channelCount == 2 || channelCount == Constants.ChannelCountMax;

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

            if (!SupportsSampleRate(sampleRate))
                throw new ArgumentException($"Unsupported sample rate: {sampleRate}");

            // 允许 memoryManager 为 null，像 OpenAL 后端一样
            Logger.Debug?.Print(LogClass.Audio, $"Opening Oboe device session - MemoryManager: {(memoryManager == null ? "null" : "provided")}");

            lock (_initLock)
            {
                if (!_isOboeInitialized)
                {
                    InitializeOboe(sampleRate, channelCount);
                }
                else if (_currentChannelCount != channelCount)
                {
                    Logger.Info?.Print(LogClass.Audio, 
                        $"Channel count changed {_currentChannelCount} -> {channelCount}, reinitializing");
                    ReinitializeOboe(sampleRate, channelCount);
                }
            }

            var session = new OboeAudioSession(this, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            
            return session;
        }

        private void InitializeOboe(uint sampleRate, uint channelCount)
        {
            try
            {
                Logger.Info?.Print(LogClass.Audio, 
                    $"Initializing Oboe audio: sampleRate={sampleRate}, channels={channelCount}");

                if (!initOboeAudio((int)sampleRate, (int)channelCount))
                {
                    throw new Exception("Oboe audio initialization failed");
                }

                setOboeVolume(_volume);
                _isOboeInitialized = true;
                _currentChannelCount = (int)channelCount;

                Logger.Info?.Print(LogClass.Audio, "Oboe audio initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Audio, $"Oboe audio initialization failed: {ex}");
                throw;
            }
        }

        private void ReinitializeOboe(uint sampleRate, uint channelCount)
        {
            shutdownOboeAudio();
            Thread.Sleep(50);
            
            if (!initOboeAudio((int)sampleRate, (int)channelCount))
            {
                throw new Exception("Oboe audio reinitialization failed");
            }
            
            setOboeVolume(_volume);
            _currentChannelCount = (int)channelCount;
        }

        private bool Unregister(OboeAudioSession session) 
        {
            bool removed = _sessions.TryRemove(session, out _);
            
            if (_sessions.IsEmpty && _isOboeInitialized)
            {
                shutdownOboeAudio();
                _isOboeInitialized = false;
            }
            
            return removed;
        }

        // ========== 音频会话类 ==========
        private class OboeAudioSession : IHardwareDeviceSession
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

            public SampleFormat RequestedSampleFormat => _sampleFormat;
            public uint RequestedSampleRate => _sampleRate;
            public uint RequestedChannelCount => (uint)_channelCount;

            public OboeAudioSession(
                OboeHardwareDeviceDriver driver,
                SampleFormat sampleFormat,
                uint sampleRate,
                uint channelCount)
            {
                _driver = driver;
                _sampleFormat = sampleFormat;
                _sampleRate = sampleRate;
                _channelCount = (int)channelCount;
                _volume = 1.0f;
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                try
                {
                    ulong playedSamples = _totalWrittenSamples - (ulong)(bufferedFrames * _channelCount);
                    
                    if (playedSamples < _totalPlayedSamples)
                    {
                        _totalPlayedSamples = playedSamples;
                        return;
                    }
                    
                    ulong availableSampleCount = playedSamples - _totalPlayedSamples;
                    
                    while (availableSampleCount > 0 && _queuedBuffers.TryPeek(out OboeAudioBuffer driverBuffer))
                    {
                        ulong sampleStillNeeded = driverBuffer.SampleCount - driverBuffer.SamplePlayed;
                        ulong playedAudioBufferSampleCount = Math.Min(sampleStillNeeded, availableSampleCount);
                        
                        driverBuffer.SamplePlayed += playedAudioBufferSampleCount;
                        availableSampleCount -= playedAudioBufferSampleCount;
                        _totalPlayedSamples += playedAudioBufferSampleCount;
                        
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

            public void Dispose()
            {
                Stop();
                _driver.Unregister(this);
                Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession disposed");
            }

            public void PrepareToClose() 
            {
                Stop();
            }

            public void Start() 
            {
                if (!_active)
                {
                    _active = true;
                    Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession started");
                }
            }

            public void Stop() 
            {
                if (_active)
                {
                    _active = false;
                    _queuedBuffers.Clear();
                    Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession stopped");
                }
            }

            public void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) Start();

                // 直接使用 buffer.Data，不依赖 MemoryManager
                if (buffer.Data == null || buffer.Data.Length == 0) 
                {
                    Logger.Warning?.Print(LogClass.Audio, "Audio buffer data is null or empty");
                    return;
                }

                // 直接处理音频数据
                ProcessAudioData(buffer);
            }

            private void ProcessAudioData(AudioBuffer buffer)
            {
                try
                {
                    var startTime = DateTime.Now.Ticks;
                    
                    // 转换为short数组用于Oboe
                    int sampleCount = buffer.Data.Length / 2; // PCM16每个样本2字节
                    int frameCount = sampleCount / _channelCount;
                    
                    // 如果连续失败，尝试调整缓冲区大小
                    int adaptiveFrameCount = frameCount;
                    if (_driver._consecutiveFailures > 3 && frameCount > 480)
                    {
                        adaptiveFrameCount = 480; // 限制最大帧数
                        sampleCount = adaptiveFrameCount * _channelCount;
                    }
                    
                    short[] audioData = new short[sampleCount];
                    Buffer.BlockCopy(buffer.Data, 0, audioData, 0, Math.Min(buffer.Data.Length, sampleCount * 2));

                    bool writeSuccess = writeOboeAudio(audioData, adaptiveFrameCount);

                    var endTime = DateTime.Now.Ticks;
                    var processTime = (endTime - startTime) / TimeSpan.TicksPerMillisecond;
                    
                    lock (_driver._performanceLock)
                    {
                        if (writeSuccess)
                        {
                            _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, (ulong)sampleCount));
                            _totalWrittenSamples += (ulong)sampleCount;
                            _driver._consecutiveFailures = 0;
                            
                            // 减少调试日志频率，只在必要时记录
                            if (frameCount > 480) // 只记录较大的缓冲区
                            {
                                Logger.Debug?.Print(LogClass.Audio, 
                                    $"Queued audio buffer: {frameCount} frames, {sampleCount} samples");
                            }
                            
                            // 记录处理时间过长的操作
                            if (processTime > 10) // 超过 10ms
                            {
                                Logger.Warning?.Print(LogClass.Audio, 
                                    $"Audio processing took {processTime}ms for {frameCount} frames");
                            }
                        }
                        else
                        {
                            _driver._consecutiveFailures++;
                            
                            // 连续失败时降低日志级别，避免日志风暴
                            if (_driver._consecutiveFailures % 10 == 1) // 每10次失败记录一次
                            {
                                Logger.Warning?.Print(LogClass.Audio, 
                                    $"Audio write failed: {frameCount} frames (consecutive failures: {_driver._consecutiveFailures})");
                            }
                            
                            // 只有在连续失败很多次时才考虑重置
                            if (_driver._consecutiveFailures > 20)
                            {
                                Logger.Error?.Print(LogClass.Audio, 
                                    $"Multiple audio write failures, resetting audio system: {_driver._consecutiveFailures}");
                                resetOboeAudio();
                                _driver._consecutiveFailures = 0; // 重置计数器
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Error in ProcessAudioData: {ex.Message}");
                }
            }

            public bool RegisterBuffer(AudioBuffer buffer)
            {
                // 直接返回 true，因为我们不依赖 MemoryManager 来读取数据
                // 数据应该在 buffer.Data 中已经提供
                return buffer.Data != null;
            }

            public bool RegisterBuffer(AudioBuffer buffer, byte[] samples)
            {
                if (samples == null)
                {
                    return false;
                }

                buffer.Data ??= samples;
                return true;
            }

            public void UnregisterBuffer(AudioBuffer buffer)
            {
                if (_queuedBuffers.TryPeek(out var driverBuffer) && 
                    driverBuffer.DriverIdentifier == buffer.DataPointer)
                {
                    _queuedBuffers.TryDequeue(out _);
                }
            }

            public bool WasBufferFullyConsumed(AudioBuffer buffer)
            {
                return !_queuedBuffers.TryPeek(out var driverBuffer) || 
                       driverBuffer.DriverIdentifier != buffer.DataPointer;
            }

            public void SetVolume(float volume)
            {
                _volume = Math.Clamp(volume, 0.0f, 1.0f);
                setOboeVolume(_volume);
                Logger.Debug?.Print(LogClass.Audio, $"Session volume set to {_volume:F2}");
            }

            public float GetVolume() => _volume;

            public ulong GetPlayedSampleCount()
            {
                return _totalPlayedSamples;
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