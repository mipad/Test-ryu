// OboeHardwareDeviceDriver.cs (基于yuzu实现优化版)
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
        private static extern void writeOboeAudio(float[] audioData, int num_frames);

        [DllImport("libryujinxjni", EntryPoint = "setOboeSampleRate")]
        private static extern void setOboeSampleRate(int sample_rate);

        [DllImport("libryujinxjni", EntryPoint = "setOboeBufferSize")]
        private static extern void setOboeBufferSize(int buffer_size);

        [DllImport("libryujinxjni", EntryPoint = "setOboeVolume")]
        private static extern void setOboeVolume(float volume);

        [DllImport("libryujinxjni", EntryPoint = "isOboeInitialized")]
        private static extern bool isOboeInitialized();

        [DllImport("libryujinxjni", EntryPoint = "getOboeBufferedFrames")]
        private static extern int getOboeBufferedFrames();

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
                while (_stillRunning)
                {
                    Thread.Sleep(10); // 调整为10ms间隔，减少CPU占用
                    
                    if (_isOboeInitialized)
                    {
                        foreach (var session in _sessions.Keys)
                        {
                            int bufferedFrames = getOboeBufferedFrames();
                            session.UpdatePlaybackStatus(bufferedFrames);
                        }
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
                    _updateThread?.Join(100); // 增加等待时间确保线程安全退出
                    
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
                        setOboeSampleRate((int)sampleRate);
                        // 移除setOboeBufferSize调用，由Oboe自动管理缓冲区大小
                        setOboeVolume(_volume);

                        initOboeAudio();
                        _isOboeInitialized = isOboeInitialized();

                        if (!_isOboeInitialized)
                            throw new Exception("Oboe audio failed to initialize");

                        Logger.Info?.Print(LogClass.Audio, "Oboe audio initialized successfully");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Oboe audio initialization failed: {ex.Message}");
                        throw;
                    }
                }
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            return session;
        }

        private bool Unregister(OboeAudioSession session) => _sessions.TryRemove(session, out _);

        // 移除CalculateBufferSize方法，由Oboe自动管理缓冲区

        private bool IsHighPerformanceDevice()
        {
            try
            {
                string device = Marshal.PtrToStringAnsi(GetAndroidDeviceModel())?.ToLower() ?? "";
                string brand = Marshal.PtrToStringAnsi(GetAndroidDeviceBrand())?.ToLower() ?? "";
                
                // 基于yuzu的经验，优先使用OpenSLES避免AAudio问题
                // 这里主要判断是否需要启用低延迟模式
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
                ulong playedSamples = _totalWrittenSamples - (ulong)bufferedFrames * (ulong)_channelCount;
                
                // 确保不会出现负数
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

            public override void Dispose() => _driver.Unregister(this);
            public override void PrepareToClose() { }
            public override void Start() => _active = true;
            public override void Stop() => _active = false;

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) 
                    Start();

                if (buffer.Data == null || buffer.Data.Length == 0) 
                    return;

                // --- 优化的流量控制逻辑 ---
                int bufferedFrames = getOboeBufferedFrames();
                
                // 基于采样率动态计算最大缓冲帧数
                int maxBufferedFrames = (int)(_sampleRate * 0.1); // 100ms缓冲
                maxBufferedFrames = Math.Clamp(maxBufferedFrames, 512, 4096);

                // 如果缓冲过多，就等待一段时间再重试（非阻塞方式）
                int retryCount = 0;
                const int maxRetries = 5;
                
                while (bufferedFrames > maxBufferedFrames && _driver._stillRunning && retryCount < maxRetries)
                {
                    Thread.Sleep(1); // 减少等待时间
                    bufferedFrames = getOboeBufferedFrames();
                    retryCount++;
                }

                // 如果仍然缓冲过多，跳过这一帧以避免音频卡顿
                if (bufferedFrames > maxBufferedFrames * 2)
                {
                    Logger.Warning?.Print(LogClass.Audio, "Audio buffer overflow, skipping frame");
                    return;
                }

                // 优化：复用临时数组
                int sampleCount = buffer.Data.Length / 2;
                if (_driver._tempFloatBuffer.Length < sampleCount)
                {
                    _driver._tempFloatBuffer = new float[sampleCount];
                }

                ConvertToFloatInPlace(buffer.Data, _driver._tempFloatBuffer, sampleCount, _volume);
                
                try
                {
                    writeOboeAudio(_driver._tempFloatBuffer, sampleCount / _channelCount);
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Failed to write audio data: {ex.Message}");
                    return;
                }

                // 记录缓冲区信息
                _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, (ulong)sampleCount));
                _totalWrittenSamples += (ulong)sampleCount;
            }

            public override bool WasBufferFullyConsumed(AudioBuffer buffer) =>
                !_queuedBuffers.TryPeek(out var driverBuffer) || 
                driverBuffer.DriverIdentifier != buffer.DataPointer;

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

            private static void ConvertToFloatInPlace(byte[] audioData, float[] output, int sampleCount, float volume)
            {
                // 优化的转换函数，减少分支和计算
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = (short)((audioData[i * 2 + 1] << 8) | audioData[i * 2]);
                    output[i] = (sample * volume) / 32768.0f;
                    
                    // 限制输出范围避免爆音
                    output[i] = Math.Clamp(output[i], -1.0f, 1.0f);
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