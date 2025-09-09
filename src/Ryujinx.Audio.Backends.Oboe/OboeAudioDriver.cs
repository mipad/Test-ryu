// OboeAudioDriver.cs (完整修复版)
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
    public class OboeAudioDriver : IHardwareDeviceDriver, IDisposable
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

        public float Volume
        {
            get => _volume;
            set
            {
                Logger.Info?.Print(LogClass.Audio, $"Setting driver volume: {value}");
                _volume = value;
                setOboeVolume(value);
            }
        }

        // ========== 构造与生命周期 ==========
        public OboeAudioDriver()
        {
            Logger.Info?.Print(LogClass.Audio, "OboeAudioDriver constructor called");
            StartUpdateThread();
        }

        private void StartUpdateThread()
        {
            _updateThread = new Thread(() =>
            {
                while (_stillRunning)
                {
                    Thread.Sleep(10); // 适当间隔
                    
                    foreach (var session in _sessions.Keys)
                    {
                        int bufferedFrames = getOboeBufferedFrames();
                        session.UpdatePlaybackStatus(bufferedFrames);
                    }
                }
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true
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
                Logger.Info?.Print(LogClass.Audio, "Disposing OboeAudioDriver");
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
                Logger.Info?.Print(LogClass.Audio, "OboeAudioDriver disposed");
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
            Logger.Info?.Print(LogClass.Audio, $"Opening Oboe device session: {sampleRate}Hz, {channelCount}ch");

            if (direction != IHardwareDeviceDriver.Direction.Output)
                throw new ArgumentException($"Unsupported direction: {direction}");

            if (!SupportsChannelCount(channelCount))
                throw new ArgumentException($"Unsupported channel count: {channelCount}");

            if (!SupportsSampleFormat(sampleFormat))
                throw new ArgumentException($"Unsupported sample format: {sampleFormat}");

            // 延迟初始化
            if (!_isOboeInitialized)
            {
                setOboeSampleRate((int)sampleRate);
                // 修复：将缓冲区大小除以通道数转换为帧数
                setOboeBufferSize(CalculateBufferSize(sampleRate) / (int)channelCount);
                setOboeVolume(_volume);

                initOboeAudio();
                _isOboeInitialized = isOboeInitialized();

                if (!_isOboeInitialized)
                    throw new Exception("Oboe audio failed to initialize");

                Logger.Info?.Print(LogClass.Audio, "Oboe audio initialized successfully");
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            return session;
        }

        private bool Unregister(OboeAudioSession session) => _sessions.TryRemove(session, out _);

        // ✅ 修复：通过 JNI 获取设备信息
        private int CalculateBufferSize(uint sampleRate)
        {
            int latencyMs = IsHighPerformanceDevice() ? 20 : 60;
            int bufferSize = (int)(sampleRate * latencyMs / 1000);
            Logger.Debug?.Print(LogClass.Audio, $"CalculateBufferSize: latencyMs={latencyMs}, bufferSize={bufferSize}");
            return bufferSize;
        }

        // ✅ 修复：使用 JNI 获取设备型号和品牌，添加天玑8100识别
        private bool IsHighPerformanceDevice()
        {
            try
            {
                string device = Marshal.PtrToStringAnsi(GetAndroidDeviceModel())?.ToLower() ?? "";
                string brand = Marshal.PtrToStringAnsi(GetAndroidDeviceBrand())?.ToLower() ?? "";
                
                // 添加天玑8100的识别
                if (device.Contains("mt6893") || device.Contains("dimensity8100") || brand.Contains("mediatek"))
                {
                    Logger.Debug?.Print(LogClass.Audio, $"High performance device detected: {device} / {brand}");
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
                        Logger.Debug?.Print(LogClass.Audio, $"High performance device detected: {device} / {brand}");
                        return true;
                    }
                }
                
                Logger.Debug?.Print(LogClass.Audio, $"Low performance device: {device} / {brand}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Audio, $"Failed to detect device performance: {ex.Message}");
                return false;
            }
        }

        // ========== 音频会话类 ==========
        private class OboeAudioSession : HardwareDeviceSessionOutputBase
        {
            private readonly OboeAudioDriver _driver;
            private readonly ConcurrentQueue<OboeAudioBuffer> _queuedBuffers = new();
            private ulong _totalWrittenSamples;
            private ulong _totalPlayedSamples;
            private bool _active;
            private float _volume;
            private readonly int _channelCount;

            public OboeAudioSession(
                OboeAudioDriver driver,
                IVirtualMemoryManager memoryManager,
                SampleFormat sampleFormat,
                uint sampleRate,
                uint channelCount) 
                : base(memoryManager, sampleFormat, sampleRate, channelCount)
            {
                _driver = driver;
                _channelCount = (int)channelCount;
                _volume = 1.0f;
                Logger.Info?.Print(LogClass.Audio, $"Session created: {sampleRate}Hz, {channelCount}ch");
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                ulong playedSamples = _totalWrittenSamples - (ulong)bufferedFrames * (ulong)_channelCount;
                
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
                if (!_active) Start();

                if (buffer.Data == null || buffer.Data.Length == 0) return;

                // --- 新增：流量控制逻辑 ---
                // 获取当前缓冲的帧数
                int bufferedFrames = getOboeBufferedFrames();
                // 计算最大允许缓冲的帧数（例如 5 * 1024 帧，约 100ms）
                int maxBufferedFrames = 5 * 1024;

                // 如果缓冲过多，就等待一段时间再重试，避免疯狂写入
                while (bufferedFrames > maxBufferedFrames && _driver._stillRunning)
                {
                    Logger.Debug?.Print(LogClass.Audio, $"QueueBuffer: Buffered {bufferedFrames} frames, waiting...");
                    Thread.Sleep(5); // 等待5ms
                    bufferedFrames = getOboeBufferedFrames(); // 重新检查
                }
                // --- 流量控制逻辑结束 ---

                // 优化：复用临时数组
                int sampleCount = buffer.Data.Length / 2;
                if (_driver._tempFloatBuffer.Length < sampleCount)
                    _driver._tempFloatBuffer = new float[sampleCount];

                ConvertToFloatInPlace(buffer.Data, _driver._tempFloatBuffer, sampleCount, _volume);
                writeOboeAudio(_driver._tempFloatBuffer, sampleCount / _channelCount);

                // 记录缓冲区信息
                _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, (ulong)sampleCount));
                _totalWrittenSamples += (ulong)sampleCount;

                // 添加写入频率日志
                Logger.Debug?.Print(LogClass.Audio, $"QueueBuffer: wrote {sampleCount} samples, buffered frames: {bufferedFrames}");
            }

            public override bool WasBufferFullyConsumed(AudioBuffer buffer) =>
                !_queuedBuffers.TryPeek(out var driverBuffer) || 
                driverBuffer.DriverIdentifier != buffer.DataPointer;

            public override void SetVolume(float volume)
            {
                _volume = volume;
                setOboeVolume(volume);
            }

            public override float GetVolume() => _volume;

            public override ulong GetPlayedSampleCount()
            {
                return _totalPlayedSamples;
            }

            public override void UnregisterBuffer(AudioBuffer buffer)
            {
                // 只移除队首匹配的缓冲区
                if (_queuedBuffers.TryPeek(out var driverBuffer) && 
                    driverBuffer.DriverIdentifier == buffer.DataPointer)
                {
                    _queuedBuffers.TryDequeue(out _);
                }
            }

            private static void ConvertToFloatInPlace(byte[] audioData, float[] output, int sampleCount, float volume)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(audioData, i * 2);
                    output[i] = sample / 32768.0f * volume;
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
