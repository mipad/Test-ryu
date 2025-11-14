// OboeHardwareDeviceDriver.cs (修复版本)
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
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

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

        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudioConverted")]
        private static extern bool writeOboeAudioConverted(byte[] audioData, int num_frames, int sample_format, int input_channels);

        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudioWithDownmix")]
        private static extern bool writeOboeAudioWithDownmix(short[] audioData, int num_frames, int input_channels, int output_channels);

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
        private int _updateIntervalMs = 20;
        private int _logCounter = 0; // 日志计数器，用于减少日志频率

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0f, 1.0f);
                setOboeVolume(_volume);
                // 减少音量设置日志
                if (_logCounter % 50 == 0)
                {
                    Logger.Debug?.Print(LogClass.Audio, $"Oboe volume set to {_volume:F2}");
                }
                _logCounter++;
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
                int updateCount = 0;
                while (_stillRunning)
                {
                    try
                    {
                        Thread.Sleep(_updateIntervalMs);

                        if (_isOboeInitialized)
                        {
                            int bufferedFrames = getOboeBufferedFrames();
                            
                            foreach (var session in _sessions.Keys)
                            {
                                session.UpdatePlaybackStatus(bufferedFrames);
                            }

                            // 每50次更新记录一次统计信息（约1秒）
                            updateCount++;
                            if (updateCount % 50 == 0)
                            {
                                // 减少更新线程的日志输出
                                if (Logger.Debug != null)
                                {
                                    Logger.Debug.Print(LogClass.Audio, 
                                        $"Oboe update: {_sessions.Count} sessions, {bufferedFrames} buffered frames");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // 错误日志仍然保留，但减少频率
                        if (_logCounter % 10 == 0)
                        {
                            Logger.Error?.Print(LogClass.Audio, $"Update thread error: {ex.Message}");
                        }
                        _logCounter++;
                    }
                }
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
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

        public bool SupportsDirection(Direction direction) =>
            direction == Direction.Output;

        // ========== 事件 ==========
        public ManualResetEvent GetPauseEvent() => _pauseEvent;
        public ManualResetEvent GetUpdateRequiredEvent() => _updateRequiredEvent;

        // ========== 打开设备会话 ==========
        public IHardwareDeviceSession OpenDeviceSession(
            Direction direction,
            IVirtualMemoryManager memoryManager,
            SampleFormat sampleFormat,
            uint sampleRate,
            uint channelCount)
        {
            if (direction != Direction.Output)
                throw new ArgumentException($"Unsupported direction: {direction}");

            if (!SupportsChannelCount(channelCount))
                throw new ArgumentException($"Unsupported channel count: {channelCount}");

            if (!SupportsSampleFormat(sampleFormat))
                throw new ArgumentException($"Unsupported sample format: {sampleFormat}");

            if (!SupportsSampleRate(sampleRate))
                throw new ArgumentException($"Unsupported sample rate: {sampleRate}");

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

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
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
            private readonly IVirtualMemoryManager _memoryManager;
            private readonly ConcurrentQueue<OboeAudioBuffer> _queuedBuffers = new();
            private ulong _totalWrittenSamples;
            private ulong _totalPlayedSamples;
            private bool _active;
            private float _volume;
            private readonly int _channelCount;
            private readonly uint _sampleRate;
            private readonly SampleFormat _sampleFormat;
            private int _bufferLogCounter = 0; // 缓冲区日志计数器

            public SampleFormat RequestedSampleFormat => _sampleFormat;
            public uint RequestedSampleRate => _sampleRate;
            public uint RequestedChannelCount => (uint)_channelCount;

            public OboeAudioSession(
                OboeHardwareDeviceDriver driver,
                IVirtualMemoryManager memoryManager,
                SampleFormat sampleFormat,
                uint sampleRate,
                uint channelCount)
            {
                _driver = driver;
                _memoryManager = memoryManager;
                _channelCount = (int)channelCount;
                _sampleRate = sampleRate;
                _sampleFormat = sampleFormat;
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
                    // 减少错误日志频率
                    if (_bufferLogCounter % 20 == 0)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Error in UpdatePlaybackStatus: {ex.Message}");
                    }
                    _bufferLogCounter++;
                }
            }

            public void Dispose()
            {
                Stop();
                _driver.Unregister(this);
                // 移除调试日志
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
                    // 移除调试日志
                }
            }

            public void Stop() 
            {
                if (_active)
                {
                    _active = false;
                    _queuedBuffers.Clear();
                    // 移除调试日志
                }
            }

            public void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) Start();

                // 使用基类方法注册缓冲区
                if (!RegisterBuffer(buffer))
                {
                    // 减少警告日志频率
                    if (_bufferLogCounter % 10 == 0)
                    {
                        Logger.Warning?.Print(LogClass.Audio, "Failed to register audio buffer");
                    }
                    _bufferLogCounter++;
                    return;
                }

                if (buffer.Data == null || buffer.Data.Length == 0) return;

                // 使用C++端的高性能处理
                ProcessAudioData(buffer);
            }

            private byte[] GetBufferSamples(AudioBuffer buffer)
            {
                if (buffer.DataPointer == 0)
                {
                    return null;
                }

                byte[] data = new byte[buffer.DataSize];
                _memoryManager.Read(buffer.DataPointer, data);
                return data;
            }

            private void ProcessAudioData(AudioBuffer buffer)
            {
                // 计算帧数
                int sampleSize = BackendHelper.GetSampleSize(_sampleFormat);
                int sampleCount = buffer.Data.Length / sampleSize;
                int frameCount = sampleCount / _channelCount;

                bool writeSuccess = false;

                // 根据情况选择最优的C++接口
                if (_sampleFormat != SampleFormat.PcmInt16)
                {
                    // 使用C++端的格式转换和声道下混
                    writeSuccess = writeOboeAudioConverted(buffer.Data, frameCount, 
                        (int)_sampleFormat, _channelCount);
                }
                else if (RequestedChannelCount != _channelCount)
                {
                    // 仅使用C++端的声道下混
                    short[] audioData = new short[sampleCount];
                    Buffer.BlockCopy(buffer.Data, 0, audioData, 0, buffer.Data.Length);
                    
                    writeSuccess = writeOboeAudioWithDownmix(audioData, frameCount,
                        _channelCount, _channelCount);
                }
                else
                {
                    // 直接写入PCM16数据
                    short[] audioData = new short[sampleCount];
                    Buffer.BlockCopy(buffer.Data, 0, audioData, 0, buffer.Data.Length);
                    writeSuccess = writeOboeAudio(audioData, frameCount);
                }

                if (writeSuccess)
                {
                    _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, (ulong)sampleCount));
                    _totalWrittenSamples += (ulong)sampleCount;
                    
                    // 减少缓冲区队列日志频率
                    if (_bufferLogCounter % 100 == 0)
                    {
                        Logger.Debug?.Print(LogClass.Audio, 
                            $"Queued audio buffer: {frameCount} frames, {sampleCount} samples");
                    }
                    _bufferLogCounter++;
                }
                else
                {
                    // 减少错误日志频率
                    if (_bufferLogCounter % 5 == 0)
                    {
                        Logger.Warning?.Print(LogClass.Audio, $"Audio write failed: {frameCount} frames");
                    }
                    _bufferLogCounter++;
                    resetOboeAudio();
                }
            }

            public bool RegisterBuffer(AudioBuffer buffer)
            {
                return RegisterBuffer(buffer, GetBufferSamples(buffer));
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
                // 减少音量设置日志
                if (_bufferLogCounter % 50 == 0)
                {
                    Logger.Debug?.Print(LogClass.Audio, $"Session volume set to {_volume:F2}");
                }
                _bufferLogCounter++;
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