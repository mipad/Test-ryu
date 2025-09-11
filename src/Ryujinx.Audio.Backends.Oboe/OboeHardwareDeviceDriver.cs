// OboeHardwareDeviceDriver.cs (终极修复版：配合C++高质量音频后端)
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

        // 修改 P/Invoke：移除 output_channels 参数
        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudio")]
        private static extern void writeOboeAudio(float[] audioData, int num_frames, int input_channels);

        [DllImport("libryujinxjni", EntryPoint = "setOboeSampleRate")]
        private static extern void setOboeSampleRate(int sample_rate);

        [DllImport("libryujinxjni", EntryPoint = "setOboeBufferSize")]
        private static extern void setOboeBufferSize(int buffer_size);

        [DllImport("libryujinxjni", EntryPoint = "setOboeVolume")]
        private static extern void setOboeVolume(float volume);

        [DllImport("libryujinxjni", EntryPoint = "setOboeNoiseShapingEnabled")]
        private static extern void setOboeNoiseShapingEnabled(bool enabled);

        // 这个函数现在设置的是 C++ 端 Oboe 输出流的声道数（目标声道数，例如 2）
        [DllImport("libryujinxjni", EntryPoint = "setOboeChannelCount")]
        private static extern void setOboeChannelCount(int channel_count);

        [DllImport("libryujinxjni", EntryPoint = "isOboeInitialized")]
        [return: MarshalAs(UnmanagedType.I1)]
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
        private bool _noiseShapingEnabled = false; // ✅ 默认关闭噪声整形（推荐）
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
                _volume = value;
                setOboeVolume(value);
            }
        }

        public bool NoiseShapingEnabled
        {
            get => _noiseShapingEnabled;
            set
            {
                _noiseShapingEnabled = value;
                setOboeNoiseShapingEnabled(value);
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
                int lastLoggedFrames = 0;
                int logInterval = 0;

                while (_stillRunning)
                {
                    Thread.Sleep(5);

                    foreach (var session in _sessions.Keys)
                    {
                        int bufferedFrames = getOboeBufferedFrames();
                        session.UpdatePlaybackStatus(bufferedFrames);

                        // ✅ 每30次循环或水位变化>20%时打印日志
                        logInterval++;
                        if (logInterval >= 30 || Math.Abs(bufferedFrames - lastLoggedFrames) > lastLoggedFrames / 5)
                        {
                            Logger.Info?.Print(LogClass.Audio, $"Oboe Buffer Level: {bufferedFrames} frames");
                            lastLoggedFrames = bufferedFrames;
                            logInterval = 0;
                        }
                    }
                }
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
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
                    _updateThread?.Join(50);

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
            uint channelCount) // 这是输入数据的声道数
        {
            if (direction != IHardwareDeviceDriver.Direction.Output)
                throw new ArgumentException($"Unsupported direction: {direction}");

            if (!SupportsChannelCount(channelCount))
                throw new ArgumentException($"Unsupported channel count: {channelCount}");

            if (!SupportsSampleFormat(sampleFormat))
                throw new ArgumentException($"Unsupported sample format: {sampleFormat}");

            // 延迟初始化
            if (!_isOboeInitialized)
            {
                // ✅ 设置 C++ 端 Oboe 输出流的参数
                int bufferSizeInFrames = CalculateBufferSize(sampleRate);
                setOboeSampleRate((int)sampleRate);
                setOboeBufferSize(bufferSizeInFrames);
                // 设置 Oboe 输出流的声道数（目标声道数，例如立体声设为2）
                // 注意：这里硬编码为2，意味着总是下混到立体声。
                // 更高级的实现可以查询设备支持的最大声道数，但下混到立体声是最兼容的方案。
                setOboeChannelCount(2);
                setOboeVolume(_volume);
                setOboeNoiseShapingEnabled(_noiseShapingEnabled);

                initOboeAudio();
                _isOboeInitialized = isOboeInitialized();

                if (!_isOboeInitialized)
                    throw new Exception("Oboe audio failed to initialize");

                Logger.Info?.Print(LogClass.Audio, $"Oboe initialized: SR={sampleRate}, BufSize={bufferSizeInFrames}, TargetChannels=2, NS={(NoiseShapingEnabled ? "ON" : "OFF")}");
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            return session;
        }

        private bool Unregister(OboeAudioSession session) => _sessions.TryRemove(session, out _);

        private int CalculateBufferSize(uint sampleRate)
        {
            // ✅ 统一使用 200ms 缓冲，避免设备差异导致的问题
            int latencyMs = 200;
            return (int)(sampleRate * latencyMs / 1000);
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

                foreach (string perfDevice in highPerfDevices)
                {
                    if (device.Contains(perfDevice) || brand.Contains(perfDevice))
                    {
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
            private readonly int _inputChannelCount; // 输入数据的声道数

            public OboeAudioSession(
                OboeHardwareDeviceDriver driver,
                IVirtualMemoryManager memoryManager,
                SampleFormat sampleFormat,
                uint sampleRate,
                uint inputChannelCount) // 存储输入声道数
                : base(memoryManager, sampleFormat, sampleRate, inputChannelCount)
            {
                _driver = driver;
                _inputChannelCount = (int)inputChannelCount; // 存储输入声道数
                _volume = 1.0f;
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                // C++ 端总是输出2声道（因为我们 setOboeChannelCount(2)）
                int outputChannels = 2;
                ulong playedSamples = _totalWrittenSamples - (ulong)bufferedFrames * (ulong)outputChannels;

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

                // ✅ 移除硬编码限制，依赖C++端RingBuffer自动管理
                // 仅在极端情况下（>8192帧）轻度等待，避免完全阻塞
                int bufferedFrames = getOboeBufferedFrames();
                const int MAX_SAFE_FRAMES = 8192;

                if (bufferedFrames > MAX_SAFE_FRAMES)
                {
                    Logger.Warning?.Print(LogClass.Audio, $"High buffer level: {bufferedFrames} frames, throttling");
                    Thread.Sleep(5); // 轻度等待，不阻塞
                }

                // 优化：复用临时数组，避免频繁分配
                int sampleCount = buffer.Data.Length / 2; // PCMInt16, 2 bytes per sample
                int numFrames = sampleCount / _inputChannelCount;

                if (_driver._tempFloatBuffer.Length < sampleCount)
                {
                    // ✅ 预留10%余量，减少未来扩容
                    int newSize = (int)(sampleCount * 1.1f);
                    _driver._tempFloatBuffer = new float[newSize];
                    Logger.Debug?.Print(LogClass.Audio, $"Resized temp buffer to {newSize} samples");
                }

                // 1. 转换到 Float32
                ConvertToFloatInPlace(buffer.Data, _driver._tempFloatBuffer, sampleCount, _volume);
                
                // 2. 直接调用 C++，传递输入声道数。C++ 端会处理下混和重采样。
                // 注意：调用修改后的 P/Invoke，移除了 output_channels 参数
                writeOboeAudio(_driver._tempFloatBuffer, numFrames, _inputChannelCount);

                // 3. 记录缓冲区信息
                // 计算总样本数（C++端处理后写入环形缓冲区的样本数）
                // 因为我们总是下混到2声道，并且可能重采样，这里无法精确计算。
                // 使用原始样本数作为一个近似值用于同步，可能会有轻微偏差，但通常可接受。
                // 更精确的方法需要 C++ 端回调返回实际写入的帧数，但这会增加复杂度。
                ulong estimatedOutputSamples = (ulong)(numFrames * 2); // 估计：帧数 * 输出声道数(2)
                _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, estimatedOutputSamples));
                _totalWrittenSamples += estimatedOutputSamples;
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
                if (_queuedBuffers.TryPeek(out var driverBuffer) &&
                    driverBuffer.DriverIdentifier == buffer.DataPointer)
                {
                    _queuedBuffers.TryDequeue(out _);
                }
            }

            private static void ConvertToFloatInPlace(byte[] audioData, float[] output, int sampleCount, float volume)
            {
                // ✅ 使用快速路径：避免重复乘法
                float scale = volume * (1.0f / 32768.0f);
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(audioData, i * 2);
                    output[i] = sample * scale;
                }
            }
            // 移除了 ConvertChannels 方法，因为在 C++ 端处理
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
