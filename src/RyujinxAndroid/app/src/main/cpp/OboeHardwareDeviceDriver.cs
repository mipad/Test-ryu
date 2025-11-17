// OboeHardwareDeviceDriver.cs (支持所有采样率和原始格式)
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
using System.Linq;

namespace Ryujinx.Audio.Backends.Oboe
{
    public class OboeHardwareDeviceDriver : IHardwareDeviceDriver, IDisposable
    {
        // ========== P/Invoke 声明 ==========
        [DllImport("libryujinxjni", EntryPoint = "initOboeAudio")]
        private static extern bool initOboeAudio(int sample_rate, int channel_count);

        [DllImport("libryujinxjni", EntryPoint = "initOboeAudioWithFormat")]
        private static extern bool initOboeAudioWithFormat(int sample_rate, int channel_count, int sample_format);

        [DllImport("libryujinxjni", EntryPoint = "shutdownOboeAudio")]
        private static extern void shutdownOboeAudio();

        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudio")]
        private static extern bool writeOboeAudio(short[] audioData, int num_frames);

        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudioRaw")]
        private static extern bool writeOboeAudioRaw(byte[] audioData, int num_frames, int sample_format);

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

        // 稳定回调控制
        [DllImport("libryujinxjni", EntryPoint = "setOboeStabilizedCallbackEnabled")]
        private static extern void setOboeStabilizedCallbackEnabled(bool enabled);

        [DllImport("libryujinxjni", EntryPoint = "isOboeStabilizedCallbackEnabled")]
        private static extern bool isOboeStabilizedCallbackEnabled();

        [DllImport("libryujinxjni", EntryPoint = "setOboeStabilizedCallbackIntensity")]
        private static extern void setOboeStabilizedCallbackIntensity(float intensity);

        [DllImport("libryujinxjni", EntryPoint = "getOboeStabilizedCallbackIntensity")]
        private static extern float getOboeStabilizedCallbackIntensity();

        // 健康检查和恢复
        [DllImport("libryujinxjni", EntryPoint = "recoverOboeAudio")]
        private static extern void recoverOboeAudio();

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private float _volume = 1.0f;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions = new();
        private bool _isOboeInitialized = false;
        private Thread _updateThread;
        private Thread _healthCheckThread;
        private bool _stillRunning = true;
        private readonly object _initLock = new object();

        private int _currentChannelCount = 2;
        private SampleFormat _currentSampleFormat = SampleFormat.PcmInt16;
        private uint _currentSampleRate = 48000;

        // 稳定回调设置 - 默认开启但使用更保守的设置
        private bool _stabilizedCallbackEnabled = true;
        private float _stabilizedCallbackIntensity = 0.2f;

        // 健康检查相关字段
        private DateTime _lastAudioWriteTime = DateTime.Now;
        private int _consecutiveFailures = 0;
        private const int MAX_CONSECUTIVE_FAILURES = 5;
        private readonly object _recoveryLock = new object();
        private int _totalRecoveryAttempts = 0;
        private const int MAX_RECOVERY_ATTEMPTS_PER_MINUTE = 3;
        private DateTime _lastRecoveryTime = DateTime.MinValue;

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

        // 稳定回调属性
        public bool StabilizedCallbackEnabled
        {
            get => _stabilizedCallbackEnabled;
            set
            {
                if (_stabilizedCallbackEnabled != value)
                {
                    _stabilizedCallbackEnabled = value;
                    setOboeStabilizedCallbackEnabled(value);
                    Logger.Info?.Print(LogClass.Audio, $"Stabilized callback {(value ? "enabled" : "disabled")}");
                }
            }
        }

        public float StabilizedCallbackIntensity
        {
            get => _stabilizedCallbackIntensity;
            set
            {
                float clampedValue = Math.Clamp(value, 0.0f, 1.0f);
                if (Math.Abs(_stabilizedCallbackIntensity - clampedValue) > 0.01f)
                {
                    _stabilizedCallbackIntensity = clampedValue;
                    setOboeStabilizedCallbackIntensity(clampedValue);
                    Logger.Info?.Print(LogClass.Audio, $"Stabilized callback intensity set to {clampedValue:F2}");
                }
            }
        }

        // ========== 构造与生命周期 ==========
        public OboeHardwareDeviceDriver()
        {
            // 应用默认的稳定回调设置
            setOboeStabilizedCallbackEnabled(_stabilizedCallbackEnabled);
            setOboeStabilizedCallbackIntensity(_stabilizedCallbackIntensity);
            
            StartUpdateThread();
            StartHealthCheckThread();
            Logger.Info?.Print(LogClass.Audio, "OboeHardwareDeviceDriver initialized (支持所有采样率和原始格式, 稳定回调默认开启)");
        }

        private void StartUpdateThread()
        {
            _updateThread = new Thread(() =>
            {
                while (_stillRunning)
                {
                    try
                    {
                        Thread.Sleep(10); // 调整为10ms更新频率
                        
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
                Priority = ThreadPriority.Normal
            };
            _updateThread.Start();
        }

        private void StartHealthCheckThread()
        {
            _healthCheckThread = new Thread(() =>
            {
                while (_stillRunning)
                {
                    try
                    {
                        Thread.Sleep(2000); // 每2秒检查一次
                        
                        if (_isOboeInitialized)
                        {
                            PerformHealthCheck();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Health check thread error: {ex.Message}");
                    }
                }
            })
            {
                Name = "Audio.Oboe.HealthCheckThread",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _healthCheckThread.Start();
        }

        private void PerformHealthCheck()
        {
            try
            {
                // 检查音频写入是否长时间没有活动（10秒）
                if ((DateTime.Now - _lastAudioWriteTime).TotalSeconds > 10)
                {
                    Logger.Warning?.Print(LogClass.Audio, "No audio activity detected for 10 seconds, possible audio stall");
                    // 这里可以添加进一步的检查，但不要自动恢复，因为可能是正常的静音期
                }

                // 检查连续失败次数
                if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                {
                    Logger.Warning?.Print(LogClass.Audio, $"Detected {_consecutiveFailures} consecutive failures, triggering recovery");
                    TriggerAudioRecovery();
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Audio, $"Health check error: {ex}");
            }
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
                    _healthCheckThread?.Join(100);
                    
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
            true; // 支持所有采样率，像SDL2一样

        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat == SampleFormat.PcmInt16 || 
            sampleFormat == SampleFormat.PcmInt32 ||
            sampleFormat == SampleFormat.PcmFloat;

        public bool SupportsChannelCount(uint channelCount) =>
            channelCount is 1 or 2 or 6; // 支持1、2、6声道

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

            // 不再检查采样率，支持所有采样率
            if (sampleRate == 0)
            {
                sampleRate = 48000; // 默认采样率
            }

            Logger.Info?.Print(LogClass.Audio, $"Opening Oboe device session: Format={sampleFormat}, Rate={sampleRate}Hz, Channels={channelCount}");

            lock (_initLock)
            {
                if (!_isOboeInitialized)
                {
                    InitializeOboe(sampleRate, channelCount, sampleFormat);
                }
                else if (_currentChannelCount != channelCount || _currentSampleFormat != sampleFormat || _currentSampleRate != sampleRate)
                {
                    // 声道数、格式或采样率变化时重新初始化
                    Logger.Info?.Print(LogClass.Audio, 
                        $"Audio configuration changed {_currentChannelCount}ch/{_currentSampleFormat}/{_currentSampleRate}Hz -> {channelCount}ch/{sampleFormat}/{sampleRate}Hz, reinitializing");
                    ReinitializeOboe(sampleRate, channelCount, sampleFormat);
                }
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            
            return session;
        }

        private void InitializeOboe(uint sampleRate, uint channelCount, SampleFormat sampleFormat)
        {
            try
            {
                Logger.Info?.Print(LogClass.Audio, 
                    $"Initializing Oboe audio: sampleRate={sampleRate}, channels={channelCount}, format={sampleFormat}");

                int formatValue = SampleFormatToInt(sampleFormat);
                if (!initOboeAudioWithFormat((int)sampleRate, (int)channelCount, formatValue))
                {
                    throw new Exception("Oboe audio initialization failed");
                }

                setOboeVolume(_volume);
                _isOboeInitialized = true;
                _currentChannelCount = (int)channelCount;
                _currentSampleFormat = sampleFormat;
                _currentSampleRate = sampleRate;
                _lastAudioWriteTime = DateTime.Now;
                _consecutiveFailures = 0;

                Logger.Info?.Print(LogClass.Audio, "Oboe audio initialized successfully (支持所有采样率和原始格式, 稳定回调默认开启)");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Audio, $"Oboe audio initialization failed: {ex}");
                throw;
            }
        }

        private void ReinitializeOboe(uint sampleRate, uint channelCount, SampleFormat sampleFormat)
        {
            shutdownOboeAudio();
            Thread.Sleep(50); // 给系统一点时间清理
            
            int formatValue = SampleFormatToInt(sampleFormat);
            if (!initOboeAudioWithFormat((int)sampleRate, (int)channelCount, formatValue))
            {
                throw new Exception("Oboe audio reinitialization failed");
            }
            
            setOboeVolume(_volume);
            _currentChannelCount = (int)channelCount;
            _currentSampleFormat = sampleFormat;
            _currentSampleRate = sampleRate;
            _lastAudioWriteTime = DateTime.Now;
            _consecutiveFailures = 0;
        }

        private int SampleFormatToInt(SampleFormat format)
        {
            return format switch
            {
                SampleFormat.PcmInt16 => 1,
                SampleFormat.PcmInt24 => 2,
                SampleFormat.PcmInt32 => 3,
                SampleFormat.PcmFloat => 4,
                _ => 1, // 默认PCM16
            };
        }

        private bool WriteAudioWithRecovery(byte[] audioData, int frameCount, int sampleFormat)
        {
            try
            {
                bool success = writeOboeAudioRaw(audioData, frameCount, sampleFormat);
                
                if (success)
                {
                    _lastAudioWriteTime = DateTime.Now;
                    _consecutiveFailures = 0;
                    return true;
                }
                else
                {
                    _consecutiveFailures++;
                    Logger.Warning?.Print(LogClass.Audio, 
                        $"Audio write failed, consecutive failures: {_consecutiveFailures}");
                    
                    if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                    {
                        TriggerAudioRecovery();
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Audio, $"Exception in audio write: {ex}");
                _consecutiveFailures++;
                
                if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                {
                    TriggerAudioRecovery();
                }
                return false;
            }
        }

        private void TriggerAudioRecovery()
        {
            lock (_recoveryLock)
            {
                // 检查恢复频率限制
                var timeSinceLastRecovery = DateTime.Now - _lastRecoveryTime;
                if (timeSinceLastRecovery.TotalMinutes < 1 && _totalRecoveryAttempts >= MAX_RECOVERY_ATTEMPTS_PER_MINUTE)
                {
                    Logger.Warning?.Print(LogClass.Audio, 
                        $"Too many recovery attempts ({_totalRecoveryAttempts}) in last minute, skipping recovery");
                    return;
                }

                // 重置每分钟计数器
                if (timeSinceLastRecovery.TotalMinutes >= 1)
                {
                    _totalRecoveryAttempts = 0;
                }

                Logger.Warning?.Print(LogClass.Audio, "Triggering audio recovery...");
                _totalRecoveryAttempts++;
                _lastRecoveryTime = DateTime.Now;
                
                try
                {
                    // 使用JNI层的恢复函数
                    recoverOboeAudio();
                    _consecutiveFailures = 0;
                    _lastAudioWriteTime = DateTime.Now;
                    
                    Logger.Info?.Print(LogClass.Audio, "Audio recovery completed successfully");
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Audio recovery failed: {ex}");
                    
                    // 如果JNI恢复失败，尝试传统的重置方法
                    try
                    {
                        resetOboeAudio();
                        Logger.Info?.Print(LogClass.Audio, "Fallback audio reset completed");
                    }
                    catch (Exception resetEx)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Fallback audio reset also failed: {resetEx}");
                    }
                }
            }
        }

        private bool Unregister(OboeAudioSession session) 
        {
            bool removed = _sessions.TryRemove(session, out _);
            
            // 如果没有会话了，关闭音频
            if (_sessions.IsEmpty && _isOboeInitialized)
            {
                shutdownOboeAudio();
                _isOboeInitialized = false;
            }
            
            return removed;
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

            public OboeAudioSession(
                OboeHardwareDeviceDriver driver,
                IVirtualMemoryManager memoryManager,
                SampleFormat sampleFormat,
                uint sampleRate,
                uint channelCount) 
                : base(memoryManager, sampleFormat, sampleRate, channelCount)
            {
                _driver = driver;
                _channelCount = (int)channelCount;
                _sampleRate = sampleRate;
                _sampleFormat = sampleFormat;
                _volume = 1.0f;
                
                Logger.Debug?.Print(LogClass.Audio, $"OboeAudioSession created: Format={sampleFormat}, Rate={sampleRate}Hz, Channels={channelCount}");
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
                _driver.Unregister(this);
                Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession disposed");
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
                    Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession started");
                }
            }

            public override void Stop() 
            {
                if (_active)
                {
                    _active = false;
                    _queuedBuffers.Clear();
                    Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession stopped");
                }
            }

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) Start();

                if (buffer.Data == null || buffer.Data.Length == 0) return;

                // 记录原始格式信息
                Logger.Debug?.Print(LogClass.Audio, 
                    $"QueueBuffer (Raw Format) - " +
                    $"Format: {_sampleFormat}, " +
                    $"Data Size: {buffer.Data.Length} bytes, " +
                    $"Channels: {_channelCount}, " +
                    $"SampleRate: {_sampleRate}");

                // 计算帧数
                int bytesPerSample = GetBytesPerSample(_sampleFormat);
                int frameCount = buffer.Data.Length / (bytesPerSample * _channelCount);

                // 直接传递原始数据，使用带恢复机制的写入方法
                int formatValue = SampleFormatToInt(_sampleFormat);
                bool writeSuccess = _driver.WriteAudioWithRecovery(buffer.Data, frameCount, formatValue);

                if (writeSuccess)
                {
                    ulong sampleCount = (ulong)(frameCount * _channelCount);
                    _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, sampleCount));
                    _totalWrittenSamples += sampleCount;
                    
                    Logger.Debug?.Print(LogClass.Audio, 
                        $"Queued audio buffer (Raw): {frameCount} frames, {sampleCount} samples, Format={_sampleFormat}, Rate={_sampleRate}Hz");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Audio, 
                        $"Audio write failed: {frameCount} frames dropped, Format={_sampleFormat}, Rate={_sampleRate}Hz");
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
                    _ => 1, // 默认PCM16
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
                    _ => 2, // 默认PCM16
                };
            }

            public override bool WasBufferFullyConsumed(AudioBuffer buffer)
            {
                return !_queuedBuffers.TryPeek(out var driverBuffer) || 
                       driverBuffer.DriverIdentifier != buffer.DataPointer;
            }

            public override void SetVolume(float volume)
            {
                _volume = Math.Clamp(volume, 0.0f, 1.0f);
                setOboeVolume(_volume);
                Logger.Debug?.Print(LogClass.Audio, $"Session volume set to {_volume:F2}");
            }

            public override float GetVolume() => _volume;

            public override ulong GetPlayedSampleCount()
            {
                return _totalPlayedSamples;
            }

            public override void UnregisterBuffer(AudioBuffer buffer)
            {
                if (_queuedBuffers.TryPeek(out var driverBuffer) && 
                    driverBuffer.DriverIdentifier == buffer.DataPointer)
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