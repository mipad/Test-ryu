// OboeAudioDriver.cs (优化版)
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

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private float _volume = 1.0f;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions = new();
        private bool _isOboeInitialized = false;
        private float[] _tempFloatBuffer = Array.Empty<float>(); // 优化：复用数组

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
                setOboeBufferSize(CalculateBufferSize(sampleRate));
                setOboeVolume(_volume);

                initOboeAudio();
                _isOboeInitialized = isOboeInitialized();

                if (!_isOboeInitialized)
                    throw new Exception("Oboe audio failed to initialize");

                // ✅ 可选：预填充静音
                int silenceFrames = (int)(sampleRate / 10); // 100ms
                float[] silence = new float[silenceFrames * (int)channelCount];
                writeOboeAudio(silence, silenceFrames);
                Logger.Info?.Print(LogClass.Audio, $"Pre-filled {silenceFrames} frames of silence");
            }

            var session = new OboeAudioSession(this, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            return session;
        }

        private bool Unregister(OboeAudioSession session) => _sessions.TryRemove(session, out _);

        private int CalculateBufferSize(uint sampleRate) => (int)(sampleRate * 40 / 1000); // 40ms

        // ========== 音频会话类 ==========
        private class OboeAudioSession : HardwareDeviceSessionOutputBase
        {
            private readonly OboeAudioDriver _driver;
            private readonly ConcurrentQueue<OboeAudioBuffer> _queuedBuffers = new();
            private ulong _totalWrittenSamples;
            private bool _active;
            private float _volume;
            private readonly int _channelCount;

            public OboeAudioSession(OboeAudioDriver driver, SampleFormat sampleFormat, uint sampleRate, uint channelCount) 
                : base(null, sampleFormat, sampleRate, channelCount)
            {
                _driver = driver;
                _channelCount = (int)channelCount;
                _volume = 1.0f;
                Logger.Info?.Print(LogClass.Audio, $"Session created: {sampleRate}Hz, {channelCount}ch");
            }

            public override void Dispose() => _driver.Unregister(this);
            public override void PrepareToClose() { }
            public override void Start() => _active = true;
            public override void Stop() => _active = false;

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) Start();

                if (buffer.Data == null || buffer.Data.Length == 0) return;

                // ✅ 优化：复用临时数组
                int sampleCount = buffer.Data.Length / 2;
                if (_driver._tempFloatBuffer.Length < sampleCount)
                    _driver._tempFloatBuffer = new float[sampleCount];

                ConvertToFloatInPlace(buffer.Data, _driver._tempFloatBuffer, sampleCount, _volume);
                writeOboeAudio(_driver._tempFloatBuffer, sampleCount / _channelCount);

                _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, (ulong)sampleCount));
                _totalWrittenSamples += (ulong)sampleCount;
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
                int bufferedFrames = getOboeBufferedFrames();
                ulong bufferedSamples = (ulong)bufferedFrames * (ulong)_channelCount;
                return _totalWrittenSamples > bufferedSamples 
                    ? _totalWrittenSamples - bufferedSamples 
                    : 0;
            }

            public override void UnregisterBuffer(AudioBuffer buffer)
            {
                // ✅ 只移除队首匹配的缓冲区
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

            public OboeAudioBuffer(ulong driverIdentifier, ulong sampleCount)
            {
                DriverIdentifier = driverIdentifier;
                SampleCount = sampleCount;
            }
        }
    }
}
#endif
