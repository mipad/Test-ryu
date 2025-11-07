// OboeHardwareDeviceDriver.cs (实时音频版本)
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

        [DllImport("libryujinxjni", EntryPoint = "setRealTimeMode")]
        private static extern void setRealTimeMode(bool enabled);

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

        // 实时音频优化
        private long _totalFramesWritten = 0;
        private int _currentChannelCount = 2;
        private DateTime _lastAudioUpdate = DateTime.Now;
        private int _consecutiveFailures = 0;
        private bool _realTimeMode = true;

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0f, 1.0f);
                setOboeVolume(_volume);
            }
        }

        // ========== 构造与生命周期 ==========
        public OboeHardwareDeviceDriver()
        {
            StartUpdateThread();
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
                        Thread.Sleep(8); // 更快的更新频率
                        updateCounter++;
                        
                        if (_isOboeInitialized)
                        {
                            int bufferedFrames = getOboeBufferedFrames();
                            
                            foreach (var session in _sessions.Keys)
                            {
                                session.UpdatePlaybackStatus(bufferedFrames);
                            }
                            
                            // 实时音频：动态调整模式
                            if (updateCounter % 30 == 0)
                            {
                                AdjustRealTimeMode(bufferedFrames);
                            }
                            
                            // 智能恢复
                            if (_consecutiveFailures > 50)
                            {
                                AttemptRecovery();
                                _consecutiveFailures = 0;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        _consecutiveFailures++;
                    }
                }
            })
            {
                Name = "Audio.Oboe.RealTimeThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal // 提高优先级
            };
            _updateThread.Start();
        }

        private void AdjustRealTimeMode(int bufferedFrames)
        {
            // 根据缓冲区状态动态调整实时模式
            bool newRealTimeMode = bufferedFrames < 1000; // 缓冲区较小则启用实时模式
            
            if (newRealTimeMode != _realTimeMode)
            {
                _realTimeMode = newRealTimeMode;
                setRealTimeMode(_realTimeMode);
            }
        }

        private void AttemptRecovery()
        {
            try
            {
                resetOboeAudio();
            }
            catch (Exception)
            {
                // 忽略恢复错误
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
                _currentChannelCount = (int)channelCount;

                if (!_isOboeInitialized)
                {
                    InitializeOboe(sampleRate, channelCount);
                }
                else if (_currentChannelCount != channelCount)
                {
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
                if (!initOboeAudio((int)sampleRate, (int)channelCount))
                {
                    throw new Exception("Oboe audio initialization failed");
                }

                setOboeVolume(_volume);
                setRealTimeMode(_realTimeMode);
                _isOboeInitialized = true;
                _currentChannelCount = (int)channelCount;
            }
            catch (Exception)
            {
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
            setRealTimeMode(_realTimeMode);
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
            private int _consecutiveWriteFailures;

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
                _consecutiveWriteFailures = 0;
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
                catch (Exception)
                {
                    // 忽略更新错误
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

                // 实时音频：直接处理PCM16数据
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
                    _consecutiveWriteFailures = 0;
                    _driver._consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveWriteFailures++;
                    _driver._consecutiveFailures++;
                    
                    // 实时音频：快速恢复
                    if (_consecutiveWriteFailures > 10)
                    {
                        _consecutiveWriteFailures = 0;
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