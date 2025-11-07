// OboeHardwareDeviceDriver.cs (稳定版本)
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

        // 稳定性改进
        private long _totalFramesWritten = 0;
        private int _writeFailures = 0;
        private int _currentChannelCount = 2;
        private DateTime _lastResetTime = DateTime.MinValue;
        private readonly TimeSpan _resetCooldown = TimeSpan.FromSeconds(2); // 2秒冷却时间

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
            Logger.Info?.Print(LogClass.Audio, "OboeHardwareDeviceDriver initialized (Stability Focus)");
        }

        private void StartUpdateThread()
        {
            _updateThread = new Thread(() =>
            {
                int updateCounter = 0;
                int failureStreak = 0;
                
                while (_stillRunning)
                {
                    try
                    {
                        Thread.Sleep(10); // 适中的更新频率
                        updateCounter++;
                        
                        if (_isOboeInitialized)
                        {
                            int bufferedFrames = getOboeBufferedFrames();
                            
                            foreach (var session in _sessions.Keys)
                            {
                                session.UpdatePlaybackStatus(bufferedFrames);
                            }
                            
                            // 每50次更新记录一次统计信息
                            if (updateCounter % 50 == 0)
                            {
                                LogStabilityStats(bufferedFrames);
                            }
                            
                            // 检查是否需要重置
                            if (_writeFailures > 0 && failureStreak > 10)
                            {
                                AttemptControlledReset();
                                failureStreak = 0;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Update thread error: {ex.Message}");
                        failureStreak++;
                    }
                }
            })
            {
                Name = "Audio.Oboe.StableThread",
                IsBackground = true,
                Priority = ThreadPriority.Normal // 正常优先级
            };
            _updateThread.Start();
        }

        private void LogStabilityStats(int bufferedFrames)
        {
            Logger.Info?.Print(LogClass.Audio, 
                $"Oboe Stability: Buffered={bufferedFrames} frames, TotalWritten={_totalFramesWritten}, Failures={_writeFailures}");
        }

        private void AttemptControlledReset()
        {
            // 检查冷却时间
            if (DateTime.Now - _lastResetTime < _resetCooldown)
            {
                Logger.Warning?.Print(LogClass.Audio, "Reset on cooldown, skipping");
                return;
            }

            try
            {
                Logger.Warning?.Print(LogClass.Audio, "Performing controlled audio reset due to failures");
                resetOboeAudio();
                _lastResetTime = DateTime.Now;
                _writeFailures = 0; // 重置失败计数
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Audio, $"Controlled reset failed: {ex.Message}");
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
                    
                    shutdownOboeAudio();
                    _isOboeInitialized = false;
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                    
                    Logger.Info?.Print(LogClass.Audio, 
                        $"Oboe Final Stats: Frames written={_totalFramesWritten}, failures={_writeFailures}");
                }
                _disposed = true;
            }
        }

        // ========== 设备能力查询 ==========
        public bool SupportsSampleRate(uint sampleRate) =>
            sampleRate is 48000 or 44100 or 32000 or 24000 or 16000;

        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat == SampleFormat.PcmInt16;

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

            lock (_initLock)
            {
                if (!_isOboeInitialized)
                {
                    InitializeOboe(sampleRate, channelCount);
                }
                else if (_currentChannelCount != channelCount)
                {
                    // 声道数变化时重新初始化
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

                Logger.Info?.Print(LogClass.Audio, "Oboe audio initialized successfully (Stability Focus)");
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

            public override void Dispose()
            {
                Stop();
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

                // 直接使用PCM16数据，避免格式转换
                int sampleCount = buffer.Data.Length / 2;
                int frameCount = sampleCount / _channelCount;

                // 将byte[]转换为short[]
                short[] audioData = new short[sampleCount];
                Buffer.BlockCopy(buffer.Data, 0, audioData, 0, buffer.Data.Length);

                // 写入音频数据
                bool writeSuccess = writeOboeAudio(audioData, frameCount);

                if (writeSuccess)
                {
                    _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, (ulong)sampleCount));
                    _totalWrittenSamples += (ulong)sampleCount;
                    _driver._totalFramesWritten += frameCount;
                    _driver._writeFailures = 0; // 重置连续失败计数
                }
                else
                {
                    _driver._writeFailures++;
                    
                    // 只在连续多次失败时记录警告
                    if (_driver._writeFailures % 20 == 0)
                    {
                        Logger.Warning?.Print(LogClass.Audio, 
                            $"Audio write failures: {_driver._writeFailures}. Buffered frames: {getOboeBufferedFrames()}");
                    }
                }
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