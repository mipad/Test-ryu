// OboeAudioDriver.cs
#if ANDROID
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
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
        private static extern void writeOboeAudio(float[] audioData, int numFrames);

        [DllImport("libryujinxjni", EntryPoint = "setOboeSampleRate")]
        private static extern void setOboeSampleRate(int sampleRate);

        [DllImport("libryujinxjni", EntryPoint = "setOboeBufferSize")]
        private static extern void setOboeBufferSize(int bufferSize);

        [DllImport("libryujinxjni", EntryPoint = "setOboeVolume")]
        private static extern void setOboeVolume(float volume);

        [DllImport("libryujinxjni", EntryPoint = "isOboeInitialized")]
        private static extern bool isOboeInitialized();

        // ========== 属性 ==========
        public static bool IsSupported => true; // Oboe 在 Android 8.1+ 基本都支持

        private bool _disposed;
        private float _volume = 1.0f;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                setOboeVolume(value); // 实时同步到原生层
            }
        }

        // ========== 构造与生命周期 ==========
        public OboeAudioDriver()
        {
            // 不在这里初始化 Oboe！等 OpenDeviceSession 时再初始化
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
                    shutdownOboeAudio();
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                }
                _disposed = true;
            }
        }

        // ========== 设备能力查询 ==========
        public bool SupportsSampleRate(uint sampleRate) =>
            sampleRate == 48000 || sampleRate == 44100 || sampleRate == 32000;

        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat == SampleFormat.PcmInt16;

        public bool SupportsChannelCount(uint channelCount) =>
            channelCount == 1 || channelCount == 2 || channelCount == 6;

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

            // 设置参数 → 初始化 Oboe（关键！不在构造函数中初始化）
            setOboeSampleRate((int)sampleRate);
            setOboeBufferSize(CalculateBufferSize(sampleRate));
            setOboeVolume(_volume); // 同步初始音量

            initOboeAudio(); // 此时才初始化！

            return new OboeAudioSession(this, sampleFormat, sampleRate, channelCount);
        }

        private int CalculateBufferSize(uint sampleRate)
        {
            const int desiredLatencyMs = 20; // 20ms 延迟
            return (int)(sampleRate * desiredLatencyMs / 1000);
        }

        // ========== 音频会话类 ==========
        private class OboeAudioSession : IHardwareDeviceSession
        {
            private readonly OboeAudioDriver _driver;
            private readonly SampleFormat _sampleFormat;
            private readonly uint _sampleRate;
            private readonly uint _channelCount;
            private bool _active = false;
            private float _volume = 1.0f;

            public OboeAudioSession(OboeAudioDriver driver, SampleFormat sampleFormat, uint sampleRate, uint channelCount)
            {
                _driver = driver;
                _sampleFormat = sampleFormat;
                _sampleRate = sampleRate;
                _channelCount = channelCount;
            }

            public void Dispose() { }

            public void PrepareToClose() { }

            public void Start() => _active = true;
            public void Stop() => _active = false;

            public void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) return;

                // 转换为 float[] 并发送
                float[] floatData = ConvertToFloat(buffer.Data, _sampleFormat, _volume);
                writeOboeAudio(floatData, floatData.Length / (int)_channelCount);

                // 可选：调试日志（缓冲区水位）
                // int buffered = getOboeBufferedFrames();
                // Console.WriteLine($"[Oboe] Buffered frames: {buffered}");
            }

            public bool RegisterBuffer(AudioBuffer buffer) => true; // Oboe 实时流，无需注册
            public void UnregisterBuffer(AudioBuffer buffer) { }   // 无需实现
            public bool WasBufferFullyConsumed(AudioBuffer buffer) => true; // 假设总是消费

            public void SetVolume(float volume)
            {
                _volume = volume;
                OboeAudioDriver.setOboeVolume(volume); // 正确：通过类名调用静态方法 // 同步到驱动（原生层）
            }

            public float GetVolume() => _volume;

            public ulong GetPlayedSampleCount() => 0; // Oboe 是实时流，无法精确计数（可改进）

            private float[] ConvertToFloat(byte[] audioData, SampleFormat format, float volume)
            {
                if (format == SampleFormat.PcmInt16)
                {
                    int sampleCount = audioData.Length / 2;
                    float[] floatData = new float[sampleCount];

                    for (int i = 0; i < sampleCount; i++)
                    {
                        short sample = BitConverter.ToInt16(audioData, i * 2);
                        floatData[i] = sample / 32768.0f * volume; // 音量在 C# 层乘（也可移到原生层）
                    }

                    return floatData;
                }

                throw new NotSupportedException($"Sample format {format} is not supported");
            }
        }
    }
}
#endif
