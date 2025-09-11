// OboeHardwareDeviceDriver.cs (自适应采样率版本)
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
using System.Collections.Generic;

namespace Ryujinx.Audio.Backends.Oboe
{
    public class OboeHardwareDeviceDriver : IHardwareDeviceDriver, IDisposable
    {
        // ========== P/Invoke 声明 ==========
        [DllImport("libryujinxjni", EntryPoint = "initOboeAudio")]
        private static extern void initOboeAudio();

        [DllImport("libryujinxjni", EntryPoint = "shutdownOboeAudio")]
        private static extern void shutdownOboeAudio();

        // 修改：添加 input_sample_rate 参数
        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudio")]
        private static extern void writeOboeAudio(float[] audioData, int num_frames, int input_channels, int input_sample_rate);

        [DllImport("libryujinxjni", EntryPoint = "setOboeBufferSize")]
        private static extern void setOboeBufferSize(int buffer_size);

        [DllImport("libryujinxjni", EntryPoint = "setOboeVolume")]
        private static extern void setOboeVolume(float volume);

        [DllImport("libryujinxjni", EntryPoint = "setOboeNoiseShapingEnabled")]
        private static extern void setOboeNoiseShapingEnabled(bool enabled);

        // 注意：移除了 setOboeSampleRate，因为现在采样率是动态的
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

        // ========== 新增：音频设备信息 P/Invoke 声明 ===============
        [DllImport("libryujinxjni", EntryPoint = "getAudioDevices")]
        private static extern IntPtr GetAudioDevices(IntPtr context);

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private float _volume = 1.0f;
        private bool _noiseShapingEnabled = false;
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
        public bool SupportsSampleRate(uint sampleRate) => true; // 现在支持所有采样率，由C++端处理转换

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

            // 延迟初始化
            if (!_isOboeInitialized)
            {
                int bufferSizeInFrames = CalculateBufferSize(sampleRate);
                setOboeBufferSize(bufferSizeInFrames);
                setOboeChannelCount(2); // 总是下混到立体声
                setOboeVolume(_volume);
                setOboeNoiseShapingEnabled(_noiseShapingEnabled);

                initOboeAudio();
                _isOboeInitialized = isOboeInitialized();

                if (!_isOboeInitialized)
                    throw new Exception("Oboe audio failed to initialize");

                Logger.Info?.Print(LogClass.Audio, $"Oboe initialized: TargetChannels=2, NS={(NoiseShapingEnabled ? "ON" : "OFF")}");
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            return session;
        }

        private bool Unregister(OboeAudioSession session) => _sessions.TryRemove(session, out _);

        private int CalculateBufferSize(uint sampleRate)
        {
            int latencyMs = 200;
            return (int)(sampleRate * latencyMs / 1000);
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
            private readonly int _inputChannelCount;
            private readonly uint _inputSampleRate; // 存储输入采样率

            public OboeAudioSession(
                OboeHardwareDeviceDriver driver,
                IVirtualMemoryManager memoryManager,
                SampleFormat sampleFormat,
                uint sampleRate,
                uint inputChannelCount)
                : base(memoryManager, sampleFormat, sampleRate, inputChannelCount)
            {
                _driver = driver;
                _inputChannelCount = (int)inputChannelCount;
                _inputSampleRate = sampleRate; // 存储输入采样率
                _volume = 1.0f;
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                int outputChannels = 2; // 总是下混到立体声
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

                int bufferedFrames = getOboeBufferedFrames();
                const int MAX_SAFE_FRAMES = 8192;

                if (bufferedFrames > MAX_SAFE_FRAMES)
                {
                    Logger.Warning?.Print(LogClass.Audio, $"High buffer level: {bufferedFrames} frames, throttling");
                    Thread.Sleep(5);
                }

                int sampleCount = buffer.Data.Length / 2;
                int numFrames = sampleCount / _inputChannelCount;

                if (_driver._tempFloatBuffer.Length < sampleCount)
                {
                    int newSize = (int)(sampleCount * 1.1f);
                    _driver._tempFloatBuffer = new float[newSize];
                    Logger.Debug?.Print(LogClass.Audio, $"Resized temp buffer to {newSize} samples");
                }

                ConvertToFloatInPlace(buffer.Data, _driver._tempFloatBuffer, sampleCount, _volume);
                
                // 修改：传递输入采样率给C++端
                writeOboeAudio(_driver._tempFloatBuffer, numFrames, _inputChannelCount, (int)_inputSampleRate);

                ulong estimatedOutputSamples = (ulong)(numFrames * 2);
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
                float scale = volume * (1.0f / 32768.0f);
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(audioData, i * 2);
                    output[i] = sample * scale;
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
