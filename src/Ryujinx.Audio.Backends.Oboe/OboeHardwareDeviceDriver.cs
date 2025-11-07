// OboeHardwareDeviceDriver.cs (双音频流共享模式)
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
        private static extern void initOboeAudio();

        [DllImport("libryujinxjni", EntryPoint = "shutdownOboeAudio")]
        private static extern void shutdownOboeAudio();

        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudio")]
        private static extern bool writeOboeAudio(float[] audioData, int num_frames);

        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudioToStream")]
        private static extern bool writeOboeAudioToStream(float[] audioData, int num_frames, int stream_id);

        [DllImport("libryujinxjni", EntryPoint = "setOboeSampleRate")]
        private static extern void setOboeSampleRate(int sample_rate);

        [DllImport("libryujinxjni", EntryPoint = "setOboeVolume")]
        private static extern void setOboeVolume(float volume);

        [DllImport("libryujinxjni", EntryPoint = "isOboeInitialized")]
        private static extern bool isOboeInitialized();

        [DllImport("libryujinxjni", EntryPoint = "isOboePlaying")]
        private static extern bool isOboePlaying();

        [DllImport("libryujinxjni", EntryPoint = "getOboeBufferedFrames")]
        private static extern int getOboeBufferedFrames();

        // ========== 多流管理 P/Invoke 声明 ==========
        [DllImport("libryujinxjni", EntryPoint = "createAdditionalOboeStream")]
        private static extern bool createAdditionalOboeStream();

        [DllImport("libryujinxjni", EntryPoint = "switchToOboeStream")]
        private static extern bool switchToOboeStream(int stream_id);

        [DllImport("libryujinxjni", EntryPoint = "getCurrentOboeStreamId")]
        private static extern int getCurrentOboeStreamId();

        [DllImport("libryujinxjni", EntryPoint = "getOboeStreamCount")]
        private static extern int getOboeStreamCount();

        // ========== 设备信息 P/Invoke 声明 ===============
        [DllImport("libryujinxjni", EntryPoint = "getAndroidDeviceModel")]
        private static extern IntPtr GetAndroidDeviceModel();

        [DllImport("libryujinxjni", EntryPoint = "getAndroidDeviceBrand")]
        private static extern IntPtr GetAndroidDeviceBrand();

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private float _volume = 1.0f;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions = new();
        private bool _isOboeInitialized = false;
        private float[] _tempFloatBuffer = Array.Empty<float>();
        private Thread _updateThread;
        private bool _stillRunning = true;
        private readonly object _initLock = new object();
        private readonly object _bufferLock = new object();

        // 多流管理
        private int _currentStreamId = 0;
        private int _streamCount = 1;
        private bool _useMultipleStreams = false;
        private int _streamSwitchCounter = 0;
        private const int STREAM_SWITCH_INTERVAL = 1000; // 每1000帧切换一次流

        // 统计信息
        private long _totalFramesWritten = 0;
        private long _totalFramesPlayed = 0;
        private int _underrunCount = 0;
        private int _overflowCount = 0;
        private int _streamFailoverCount = 0;

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0f, 1.0f);
                setOboeVolume(_volume);
                Logger.Info?.Print(LogClass.Audio, $"Oboe volume set to {_volume}");
            }
        }

        // ========== 构造与生命周期 ==========
        public OboeHardwareDeviceDriver()
        {
            // 检测是否需要使用多流模式
            _useMultipleStreams = ShouldUseMultipleStreams();
            StartUpdateThread();
            Logger.Info?.Print(LogClass.Audio, $"OboeHardwareDeviceDriver initialized, MultiStream: {_useMultipleStreams}");
        }

        private bool ShouldUseMultipleStreams()
        {
            try
            {
                // Android 11+ 建议使用多流共享模式
                string device = Marshal.PtrToStringAnsi(GetAndroidDeviceModel())?.ToLower() ?? "";
                string brand = Marshal.PtrToStringAnsi(GetAndroidDeviceBrand())?.ToLower() ?? "";
                
                // 在高性能设备上启用多流模式
                if (IsHighPerformanceDevice())
                {
                    Logger.Info?.Print(LogClass.Audio, "High performance device detected, enabling multi-stream mode");
                    return true;
                }
                
                // 在已知有音频问题的设备上启用多流模式
                string[] problematicDevices = {
                    "pixel", "samsung", "oneplus", "xiaomi", "huawei"
                };
                
                foreach (string deviceName in problematicDevices)
                {
                    if (device.Contains(deviceName) || brand.Contains(deviceName))
                    {
                        Logger.Info?.Print(LogClass.Audio, $"Problematic device detected ({deviceName}), enabling multi-stream mode");
                        return true;
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Audio, $"Error detecting device type: {ex.Message}");
                return false; // 默认不使用多流
            }
        }

        private void StartUpdateThread()
        {
            _updateThread = new Thread(() =>
            {
                int errorCount = 0;
                const int maxErrorCount = 10;
                
                while (_stillRunning)
                {
                    try
                    {
                        Thread.Sleep(10); // 10ms 更新间隔
                        
                        if (_isOboeInitialized)
                        {
                            // 定期检查流状态
                            CheckStreamHealth();
                            
                            foreach (var session in _sessions.Keys)
                            {
                                int bufferedFrames = getOboeBufferedFrames();
                                session.UpdatePlaybackStatus(bufferedFrames);
                            }
                        }
                        errorCount = 0; // 重置错误计数
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        Logger.Error?.Print(LogClass.Audio, $"Update thread error #{errorCount}: {ex.Message}");
                        
                        if (errorCount >= maxErrorCount)
                        {
                            Logger.Error?.Print(LogClass.Audio, "Too many errors in update thread, stopping");
                            break;
                        }
                    }
                }
                
                Logger.Info?.Print(LogClass.Audio, "Oboe update thread stopped");
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _updateThread.Start();
        }

        private void CheckStreamHealth()
        {
            if (!_useMultipleStreams) return;

            try
            {
                bool isPlaying = isOboePlaying();
                int currentStreamId = getCurrentOboeStreamId();
                int streamCount = getOboeStreamCount();

                // 如果当前流没有播放，尝试切换到另一个流
                if (!isPlaying && streamCount > 1)
                {
                    int newStreamId = (currentStreamId + 1) % streamCount;
                    if (switchToOboeStream(newStreamId))
                    {
                        _currentStreamId = newStreamId;
                        _streamFailoverCount++;
                        Logger.Warning?.Print(LogClass.Audio, $"Stream failover: switched to stream {newStreamId} (total failovers: {_streamFailoverCount})");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Audio, $"Error checking stream health: {ex.Message}");
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
                    _updateThread?.Join(200);
                    
                    shutdownOboeAudio();
                    _isOboeInitialized = false;
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                    
                    Logger.Info?.Print(LogClass.Audio, 
                        $"Oboe statistics: Frames written={_totalFramesWritten}, played={_totalFramesPlayed}, " +
                        $"underruns={_underrunCount}, overflows={_overflowCount}, stream failovers={_streamFailoverCount}");
                }
                _disposed = true;
            }
        }

        // ========== 设备能力查询 ==========
        public bool SupportsSampleRate(uint sampleRate) =>
            sampleRate is 48000 or 44100 or 32000 or 24000 or 16000 or 8000;

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

            // 线程安全的延迟初始化
            lock (_initLock)
            {
                if (!_isOboeInitialized)
                {
                    try
                    {
                        Logger.Info?.Print(LogClass.Audio, 
                            $"Initializing Oboe audio: sampleRate={sampleRate}, channels={channelCount}, MultiStream={_useMultipleStreams}");

                        setOboeSampleRate((int)sampleRate);
                        setOboeVolume(_volume);

                        initOboeAudio();
                        
                        // 等待初始化完成
                        for (int i = 0; i < 10; i++)
                        {
                            if (isOboeInitialized())
                            {
                                _isOboeInitialized = true;
                                break;
                            }
                            Thread.Sleep(10);
                        }

                        if (!_isOboeInitialized)
                            throw new Exception("Oboe audio failed to initialize within timeout");

                        // 如果启用多流模式，创建额外的音频流
                        if (_useMultipleStreams)
                        {
                            if (createAdditionalOboeStream())
                            {
                                _streamCount = getOboeStreamCount();
                                Logger.Info?.Print(LogClass.Audio, $"Created additional audio streams, total: {_streamCount}");
                            }
                            else
                            {
                                Logger.Warning?.Print(LogClass.Audio, "Failed to create additional audio streams, using single stream mode");
                                _useMultipleStreams = false;
                            }
                        }

                        Logger.Info?.Print(LogClass.Audio, "Oboe audio initialized successfully");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Oboe audio initialization failed: {ex}");
                        throw;
                    }
                }
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            
            Logger.Info?.Print(LogClass.Audio, $"Oboe audio session created: {session.GetHashCode()}");
            return session;
        }

        private bool Unregister(OboeAudioSession session)
        {
            Logger.Info?.Print(LogClass.Audio, $"Oboe audio session unregistered: {session.GetHashCode()}");
            return _sessions.TryRemove(session, out _);
        }

        private bool IsHighPerformanceDevice()
        {
            try
            {
                string device = Marshal.PtrToStringAnsi(GetAndroidDeviceModel())?.ToLower() ?? "";
                string brand = Marshal.PtrToStringAnsi(GetAndroidDeviceBrand())?.ToLower() ?? "";
                
                if (device.Contains("mt6893") || device.Contains("dimensity8100") || brand.Contains("mediatek"))
                {
                    return true;
                }
                
                string[] highPerfDevices = {
                    "sdm845", "sdm855", "sdm865", "sdm888", "sm8350", "sm8450", "sm8550",
                    "kirin980", "kirin990", "kirin9000", "dimensity9000", "dimensity9200",
                    "exynos9820", "exynos990", "exynos2100", "exynos2200",
                    "starqlte", "beyond1", "dreamlte", "raphael", "cepheus", "vangogh"
                };
                
                foreach (string perfDevice in highPerfDevices) {
                    if (device.Contains(perfDevice) || brand.Contains(perfDevice)) {
                        return true;
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
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
            private int _consecutiveOverflows = 0;
            private const int MaxConsecutiveOverflows = 5;

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
                
                Logger.Info?.Print(LogClass.Audio, 
                    $"OboeAudioSession created: channels={_channelCount}, sampleRate={_sampleRate}");
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                try
                {
                    // 更精确的播放状态计算
                    ulong playedSamples = _totalWrittenSamples - (ulong)(bufferedFrames * _channelCount);
                    
                    if (playedSamples < _totalPlayedSamples)
                    {
                        // 处理计数器回绕或重置
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
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession disposed");
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
                if (!_active) 
                    Start();

                if (buffer.Data == null || buffer.Data.Length == 0) 
                    return;

                lock (_driver._bufferLock)
                {
                    // 流量控制：检查缓冲区状态
                    int bufferedFrames = getOboeBufferedFrames();
                    int maxBufferedFrames = CalculateOptimalBufferSize();
                    
                    // 如果缓冲过多，采用非阻塞等待
                    int waitCount = 0;
                    const int maxWaitCount = 3;
                    
                    while (bufferedFrames > maxBufferedFrames && _driver._stillRunning && waitCount < maxWaitCount)
                    {
                        Thread.Sleep(1);
                        bufferedFrames = getOboeBufferedFrames();
                        waitCount++;
                    }

                    // 如果仍然缓冲过多，处理溢出
                    if (bufferedFrames > maxBufferedFrames * 1.5f)
                    {
                        _consecutiveOverflows++;
                        if (_consecutiveOverflows >= MaxConsecutiveOverflows)
                        {
                            Logger.Warning?.Print(LogClass.Audio, 
                                $"Audio buffer overflow (consecutive #{_consecutiveOverflows}), skipping frame");
                            _driver._overflowCount++;
                            return;
                        }
                    }
                    else
                    {
                        _consecutiveOverflows = 0;
                    }

                    // 准备音频数据
                    int sampleCount = buffer.Data.Length / 2;
                    if (_driver._tempFloatBuffer.Length < sampleCount)
                    {
                        _driver._tempFloatBuffer = new float[sampleCount];
                    }

                    // 转换音频格式
                    ConvertToFloatOptimized(buffer.Data, _driver._tempFloatBuffer, sampleCount, _volume);
                    
                    // 写入音频数据（支持多流模式）
                    bool writeSuccess = WriteAudioWithStreamManagement(_driver._tempFloatBuffer, sampleCount / _channelCount);
                    
                    if (writeSuccess)
                    {
                        // 记录缓冲区信息
                        _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, (ulong)sampleCount));
                        _totalWrittenSamples += (ulong)sampleCount;
                        _driver._totalFramesWritten += sampleCount / _channelCount;
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.Audio, "Failed to write audio data to Oboe");
                        _driver._underrunCount++;
                    }
                }
            }

            private bool WriteAudioWithStreamManagement(float[] audioData, int numFrames)
            {
                if (!_driver._useMultipleStreams)
                {
                    // 单流模式
                    return writeOboeAudio(audioData, numFrames);
                }
                else
                {
                    // 多流模式：轮换使用不同的流
                    _driver._streamSwitchCounter++;
                    if (_driver._streamSwitchCounter >= STREAM_SWITCH_INTERVAL)
                    {
                        _driver._streamSwitchCounter = 0;
                        int nextStreamId = (_driver._currentStreamId + 1) % _driver._streamCount;
                        if (switchToOboeStream(nextStreamId))
                        {
                            _driver._currentStreamId = nextStreamId;
                            Logger.Debug?.Print(LogClass.Audio, $"Switched to audio stream {nextStreamId}");
                        }
                    }

                    // 尝试写入当前流
                    bool success = writeOboeAudioToStream(audioData, numFrames, _driver._currentStreamId);
                    
                    // 如果失败且有多流，尝试其他流
                    if (!success && _driver._streamCount > 1)
                    {
                        for (int i = 1; i < _driver._streamCount; i++)
                        {
                            int alternateStreamId = (_driver._currentStreamId + i) % _driver._streamCount;
                            success = writeOboeAudioToStream(audioData, numFrames, alternateStreamId);
                            if (success)
                            {
                                _driver._currentStreamId = alternateStreamId;
                                _driver._streamFailoverCount++;
                                Logger.Warning?.Print(LogClass.Audio, 
                                    $"Stream failover: switched to stream {alternateStreamId} due to write failure");
                                break;
                            }
                        }
                    }
                    
                    return success;
                }
            }

            private int CalculateOptimalBufferSize()
            {
                // 基于采样率动态计算最佳缓冲区大小
                int baseBufferMs = IsLowLatencyDevice() ? 60 : 100;
                return (int)(_sampleRate * baseBufferMs / 1000);
            }

            private bool IsLowLatencyDevice()
            {
                try
                {
                    string device = Marshal.PtrToStringAnsi(GetAndroidDeviceModel())?.ToLower() ?? "";
                    return device.Contains("pixel") || device.Contains("samsung") || device.Contains("oneplus");
                }
                catch
                {
                    return false;
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
                Logger.Debug?.Print(LogClass.Audio, $"Session volume set to {_volume}");
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

            private static void ConvertToFloatOptimized(byte[] audioData, float[] output, int sampleCount, float volume)
            {
                // 高度优化的转换函数，避免爆音和失真
                const float scale = 1.0f / 32768.0f;
                
                for (int i = 0; i < sampleCount; i++)
                {
                    int byteIndex = i * 2;
                    
                    // 手动转换避免BitConverter开销，并确保正确的字节顺序
                    short sample = (short)((audioData[byteIndex + 1] << 8) | audioData[byteIndex]);
                    
                    // 应用音量和限制
                    float converted = sample * scale * volume;
                    
                    // 硬限制避免爆音
                    if (converted > 1.0f) converted = 1.0f;
                    else if (converted < -1.0f) converted = -1.0f;
                    
                    output[i] = converted;
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