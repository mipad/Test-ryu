// OboeHardwareDeviceDriver.cs (简化版 + 环形缓冲区)
#if ANDROID
using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Audio.Backends.Oboe
{
    public class OboeHardwareDeviceDriver : IHardwareDeviceDriver, IDisposable
    {
        // ========== P/Invoke 声明 ==========
        [DllImport("libryujinxjni", EntryPoint = "createOboeRenderer")]
        private static extern IntPtr createOboeRenderer();

        [DllImport("libryujinxjni", EntryPoint = "destroyOboeRenderer")]
        private static extern void destroyOboeRenderer(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "initOboeRenderer")]
        private static extern bool initOboeRenderer(IntPtr renderer, int sample_rate, int channel_count, int sample_format);

        [DllImport("libryujinxjni", EntryPoint = "shutdownOboeRenderer")]
        private static extern void shutdownOboeRenderer(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "writeOboeRendererAudioRaw")]
        private static extern bool writeOboeRendererAudioRaw(IntPtr renderer, byte[] audioData, int num_frames, int sample_format);

        [DllImport("libryujinxjni", EntryPoint = "setOboeRendererVolume")]
        private static extern void setOboeRendererVolume(IntPtr renderer, float volume);

        [DllImport("libryujinxjni", EntryPoint = "isOboeRendererInitialized")]
        private static extern bool isOboeRendererInitialized(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "isOboeRendererPlaying")]
        private static extern bool isOboeRendererPlaying(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "getOboeRendererBufferedFrames")]
        private static extern int getOboeRendererBufferedFrames(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "resetOboeRenderer")]
        private static extern void resetOboeRenderer(IntPtr renderer);

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);

        public float Volume { get; set; } = 1.0f;

        // ========== 构造与生命周期 ==========
        public OboeHardwareDeviceDriver() { }

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

            return new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
        }

        // ========== 音频会话类 ==========
        private class OboeAudioSession : HardwareDeviceSessionOutputBase
        {
            private readonly OboeHardwareDeviceDriver _driver;
            private ulong _playedSampleCount;
            private bool _active;
            private float _volume;
            private readonly int _channelCount;
            private readonly uint _sampleRate;
            private readonly SampleFormat _sampleFormat;
            private readonly IntPtr _rendererPtr;

            public bool IsActive => _active;

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
                _playedSampleCount = 0;
                
                // 创建独立的渲染器实例
                _rendererPtr = createOboeRenderer();
                if (_rendererPtr == IntPtr.Zero)
                {
                    throw new Exception("Failed to create Oboe audio renderer");
                }

                // 初始化渲染器
                int formatValue = SampleFormatToInt(sampleFormat);
                if (!initOboeRenderer(_rendererPtr, (int)sampleRate, (int)channelCount, formatValue))
                {
                    destroyOboeRenderer(_rendererPtr);
                    throw new Exception("Failed to initialize Oboe audio renderer");
                }

                setOboeRendererVolume(_rendererPtr, _volume);
            }

            public override void Dispose()
            {
                Stop();
                
                if (_rendererPtr != IntPtr.Zero)
                {
                    shutdownOboeRenderer(_rendererPtr);
                    destroyOboeRenderer(_rendererPtr);
                }
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
                }
            }

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) Start();

                if (buffer.Data == null || buffer.Data.Length == 0) return;

                // 计算帧数
                int bytesPerSample = GetBytesPerSample(_sampleFormat);
                int frameCount = buffer.Data.Length / (bytesPerSample * _channelCount);

                // 写入音频数据
                int formatValue = SampleFormatToInt(_sampleFormat);
                bool writeSuccess = writeOboeRendererAudioRaw(_rendererPtr, buffer.Data, frameCount, formatValue);

                if (!writeSuccess)
                {
                    Logger.Warning?.Print(LogClass.Audio, 
                        $"Audio write failed: {frameCount} frames dropped, Format={_sampleFormat}, Rate={_sampleRate}Hz");
                    
                    // 重置渲染器
                    resetOboeRenderer(_rendererPtr);
                }
                else
                {
                    // 更新播放样本计数
                    _playedSampleCount += (ulong)(frameCount * _channelCount);
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
                // 对于环形缓冲区，我们假设数据总是被立即消费
                // 实际的消费状态由环形缓冲区内部管理
                return true;
            }

            public override void SetVolume(float volume)
            {
                _volume = Math.Clamp(volume, 0.0f, 1.0f);
                if (_rendererPtr != IntPtr.Zero)
                {
                    setOboeRendererVolume(_rendererPtr, _volume);
                }
            }

            public override float GetVolume() => _volume;

            public override ulong GetPlayedSampleCount()
            {
                return _playedSampleCount;
            }

            public override void UnregisterBuffer(AudioBuffer buffer)
            {
                // 对于环形缓冲区，不需要手动取消注册缓冲区
            }
        }
    }
}
#endif