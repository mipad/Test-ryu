// OboeHardwareDeviceDriver.cs (完整版本 - 改进状态同步)
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

        [DllImport("libryujinxjni", EntryPoint = "getOboeTotalPlayedFrames")]
        private static extern long getOboeTotalPlayedFrames();

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
        private SampleFormat _currentSampleFormat = SampleFormat.PcmInt16;
        private uint _currentSampleRate = 48000;

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
            Logger.Info?.Print(LogClass.Audio, "OboeHardwareDeviceDriver initialized (改进状态同步版本)");
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
                        
                        if (_isOboeInitialized)
                        {
                            // 更新所有会话的播放状态
                            foreach (var session in _sessions.Keys)
                            {
                                session.UpdatePlaybackStatus();
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

            Logger.Info?.Print(LogClass.Audio, $"Opening Oboe device session: Format={sampleFormat}, Rate={sampleRate}Hz, Channels={channelCount}");

            lock (_initLock)
            {
                if (!_isOboeInitialized)
                {
                    InitializeOboe(sampleRate, channelCount, sampleFormat);
                }
                else if (_currentChannelCount != channelCount || _currentSampleFormat != sampleFormat || _currentSampleRate != sampleRate)
                {
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

                Logger.Info?.Print(LogClass.Audio, "Oboe audio initialized successfully (改进状态同步版本)");
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
            Thread.Sleep(50);
            
            int formatValue = SampleFormatToInt(sampleFormat);
            if (!initOboeAudioWithFormat((int)sampleRate, (int)channelCount, formatValue))
            {
                throw new Exception("Oboe audio reinitialization failed");
            }
            
            setOboeVolume(_volume);
            _currentChannelCount = (int)channelCount;
            _currentSampleFormat = sampleFormat;
            _currentSampleRate = sampleRate;
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

        internal bool Unregister(OboeAudioSession session) 
        {
            bool removed = _sessions.TryRemove(session, out _);
            
            if (_sessions.IsEmpty && _isOboeInitialized)
            {
                shutdownOboeAudio();
                _isOboeInitialized = false;
            }
            
            return removed;
        }

        // ========== 音频会话类 (改进状态同步) ==========
        private class OboeAudioSession : HardwareDeviceSessionOutputBase
        {
            private readonly OboeHardwareDeviceDriver _driver;
            private readonly ConcurrentQueue<OboeAudioBuffer> _queuedBuffers = new();
            private readonly DynamicRingBuffer _ringBuffer;
            private readonly object _playbackLock = new object();
            private ulong _totalWrittenSamples;
            private ulong _totalPlayedSamples;
            private ulong _sessionBaseFrames;
            private bool _active;
            private float _volume;
            private readonly int _channelCount;
            private readonly uint _sampleRate;
            private readonly SampleFormat _sampleFormat;
            private readonly int _bytesPerFrame;
            private bool _needsNativeTrigger;

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
                _bytesPerFrame = GetBytesPerSample(sampleFormat) * _channelCount;
                _ringBuffer = new DynamicRingBuffer();
                _needsNativeTrigger = true;
                
                Logger.Debug?.Print(LogClass.Audio, 
                    $"OboeAudioSession created: Format={sampleFormat}, Rate={sampleRate}Hz, Channels={channelCount}, BytesPerFrame={_bytesPerFrame}");
            }

            public void UpdatePlaybackStatus()
            {
                try
                {
                    if (!_active) return;

                    lock (_playbackLock)
                    {
                        // 获取精确的播放帧数
                        long currentTotalFrames = getOboeTotalPlayedFrames();
                        if (_sessionBaseFrames == 0)
                        {
                            _sessionBaseFrames = (ulong)currentTotalFrames;
                            return;
                        }

                        ulong sessionFramesPlayed = (ulong)currentTotalFrames - _sessionBaseFrames;
                        ulong samplesPlayed = sessionFramesPlayed * (ulong)_channelCount;

                        // 防止回退
                        if (samplesPlayed < _totalPlayedSamples)
                        {
                            return;
                        }

                        ulong availableSampleCount = samplesPlayed - _totalPlayedSamples;

                        // 更新缓冲区播放状态
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
                                
                                Logger.Debug?.Print(LogClass.Audio, 
                                    $"Buffer fully consumed: {driverBuffer.SampleCount} samples");
                            }
                        }

                        // 触发Native层读取数据
                        if (_needsNativeTrigger && _ringBuffer.Length > 0)
                        {
                            TriggerNativePlayback();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Error in UpdatePlaybackStatus: {ex.Message}");
                }
            }

            private void TriggerNativePlayback()
            {
                try
                {
                    int availableFrames = _ringBuffer.Length / _bytesPerFrame;
                    if (availableFrames > 0)
                    {
                        int bytesToWrite = Math.Min(_ringBuffer.Length, 4096 * _bytesPerFrame);
                        byte[] tempBuffer = new byte[bytesToWrite];
                        
                        _ringBuffer.Read(tempBuffer, 0, bytesToWrite);
                        
                        int frameCount = bytesToWrite / _bytesPerFrame;
                        int formatValue = SampleFormatToInt(_sampleFormat);
                        
                        bool writeSuccess = writeOboeAudioRaw(tempBuffer, frameCount, formatValue);
                        
                        if (writeSuccess)
                        {
                            _needsNativeTrigger = _ringBuffer.Length > 0;
                            Logger.Debug?.Print(LogClass.Audio, 
                                $"Triggered native playback: {frameCount} frames, {bytesToWrite} bytes");
                        }
                        else
                        {
                            Logger.Warning?.Print(LogClass.Audio, "Native playback trigger failed");
                            _needsNativeTrigger = true; // 重试
                        }
                    }
                    else
                    {
                        _needsNativeTrigger = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Error in TriggerNativePlayback: {ex.Message}");
                    _needsNativeTrigger = true;
                }
            }

            public override void Dispose()
            {
                lock (_playbackLock)
                {
                    Stop();
                    _ringBuffer?.Dispose();
                    _driver.Unregister(this);
                    Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession disposed");
                }
            }

            public override void PrepareToClose() 
            {
                Stop();
            }

            public override void Start() 
            {
                lock (_playbackLock)
                {
                    if (!_active)
                    {
                        _active = true;
                        _sessionBaseFrames = 0;
                        _needsNativeTrigger = true;
                        Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession started");
                    }
                }
            }

            public override void Stop() 
            {
                lock (_playbackLock)
                {
                    if (_active)
                    {
                        _active = false;
                        _queuedBuffers.Clear();
                        _ringBuffer.Clear();
                        _sessionBaseFrames = 0;
                        _totalWrittenSamples = 0;
                        _totalPlayedSamples = 0;
                        Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession stopped");
                    }
                }
            }

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (buffer.Data == null || buffer.Data.Length == 0) return;

                lock (_playbackLock)
                {
                    if (!_active) 
                    {
                        // 如果会话未激活，立即标记缓冲区为已播放
                        ulong sampleCount = GetSampleCount(buffer);
                        _totalWrittenSamples += sampleCount;
                        _totalPlayedSamples += sampleCount;
                        _driver.GetUpdateRequiredEvent().Set();
                        return;
                    }

                    // 计算样本数和帧数
                    int bytesPerSample = GetBytesPerSample(_sampleFormat);
                    ulong sampleCount = (ulong)(buffer.Data.Length / bytesPerSample);
                    ulong frameCount = sampleCount / (ulong)_channelCount;

                    // 写入环形缓冲区
                    _ringBuffer.Write(buffer.Data, 0, buffer.Data.Length);
                    
                    // 记录缓冲区信息
                    _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, sampleCount));
                    _totalWrittenSamples += sampleCount;
                    
                    // 标记需要触发Native播放
                    _needsNativeTrigger = true;

                    Logger.Debug?.Print(LogClass.Audio, 
                        $"Queued audio buffer: {frameCount} frames, {sampleCount} samples, " +
                        $"Format={_sampleFormat}, Rate={_sampleRate}Hz, " +
                        $"RingBuffer={_ringBuffer.Length} bytes");
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
                lock (_playbackLock)
                {
                    return !_queuedBuffers.TryPeek(out var driverBuffer) || 
                           driverBuffer.DriverIdentifier != buffer.DataPointer;
                }
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
                lock (_playbackLock)
                {
                    return _totalPlayedSamples;
                }
            }

            public override void UnregisterBuffer(AudioBuffer buffer)
            {
                lock (_playbackLock)
                {
                    if (_queuedBuffers.TryPeek(out var driverBuffer) && 
                        driverBuffer.DriverIdentifier == buffer.DataPointer)
                    {
                        _queuedBuffers.TryDequeue(out _);
                    }
                }
            }

            private ulong GetSampleCount(AudioBuffer buffer)
            {
                int bytesPerSample = GetBytesPerSample(_sampleFormat);
                return (ulong)(buffer.Data.Length / bytesPerSample);
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