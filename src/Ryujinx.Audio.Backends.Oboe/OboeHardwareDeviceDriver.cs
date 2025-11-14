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

        // 性能统计
        private long _totalFramesWritten = 0;
        private int _underrunCount = 0;
        private int _currentChannelCount = 2;
        private DateTime _lastStatsLogTime = DateTime.Now;

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
            Logger.Info?.Print(LogClass.Audio, "OboeHardwareDeviceDriver initialized (Yuzu-based Implementation)");
        }

        private void StartUpdateThread()
        {
            _updateThread = new Thread(() =>
            {
                int updateCounter = 0;
                
                while (_stillRunning)
                {
                    try
                    {
                        Thread.Sleep(10); // 调整为10ms更新频率
                        updateCounter++;
                        
                        if (_isOboeInitialized)
                        {
                            int bufferedFrames = getOboeBufferedFrames();
                            
                            foreach (var session in _sessions.Keys)
                            {
                                session.UpdatePlaybackStatus(bufferedFrames);
                            }
                            
                            // 每50次更新记录一次统计信息（约0.5秒）
                            if (updateCounter % 50 == 0)
                            {
                                LogPerformanceStats(bufferedFrames);
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
                Priority = ThreadPriority.Normal // 调整为正常优先级
            };
            _updateThread.Start();
        }

        private void LogPerformanceStats(int bufferedFrames)
        {
            var now = DateTime.Now;
            if ((now - _lastStatsLogTime).TotalSeconds >= 5) // 每5秒记录一次详细统计
            {
                Logger.Info?.Print(LogClass.Audio, 
                    $"Oboe Stats: Buffered={bufferedFrames} frames, TotalWritten={_totalFramesWritten}, Underruns={_underrunCount}");
                _lastStatsLogTime = now;
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
                        $"Oboe Final Stats: Frames written={_totalFramesWritten}, underruns={_underrunCount}");
                }
                _disposed = true;
            }
        }

        // ========== 设备能力查询 ==========
        public bool SupportsSampleRate(uint sampleRate) =>
            sampleRate == 48000; // 只支持48000Hz

        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat == SampleFormat.PcmInt16;

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

                Logger.Info?.Print(LogClass.Audio, "Oboe audio initialized successfully (Yuzu-based)");
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
            Thread.Sleep(50); // 给系统一点时间清理
            
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

                // 直接使用PCM16数据，避免格式转换
                int sampleCount = buffer.Data.Length / 2;
                int frameCount = sampleCount / _channelCount;

                // 将byte[]转换为short[]
                short[] audioData = new short[sampleCount];
                Buffer.BlockCopy(buffer.Data, 0, audioData, 0, buffer.Data.Length);

                // 写入音频数据到Oboe
                bool writeSuccess = writeOboeAudio(audioData, frameCount);

                if (writeSuccess)
                {
                    _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, (ulong)sampleCount));
                    _totalWrittenSamples += (ulong)sampleCount;
                    _driver._totalFramesWritten += frameCount;
                    
                    Logger.Debug?.Print(LogClass.Audio, $"Queued audio buffer: {frameCount} frames, {sampleCount} samples");
                }
                else
                {
                    _driver._underrunCount++;
                    Logger.Warning?.Print(LogClass.Audio, $"Audio write failed: {frameCount} frames dropped");
                    
                    // 减少重置频率，避免性能影响
                    if (_driver._underrunCount % 10 == 0) // 每10次underflow重置一次
                    {
                        Logger.Warning?.Print(LogClass.Audio, "Multiple audio write failures, resetting audio system");
                        resetOboeAudio();
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
