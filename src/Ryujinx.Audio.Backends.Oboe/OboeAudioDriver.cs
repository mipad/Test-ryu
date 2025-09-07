// OboeAudioDriver.cs
#if ANDROID
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ryujinx.Common.Logging;

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
        public static bool IsSupported => true;

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
                setOboeVolume(value);
            }
        }

        // ========== 构造与生命周期 ==========
        public OboeAudioDriver()
        {
            Logger.Info?.Print(LogClass.Audio, "OboeAudioDriver created");
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
                    Logger.Info?.Print(LogClass.Audio, "Shutting down Oboe audio");
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
            Logger.Info?.Print(LogClass.Audio, $"Opening Oboe device session: direction={direction}, sampleRate={sampleRate}, channelCount={channelCount}, format={sampleFormat}");

            if (direction != IHardwareDeviceDriver.Direction.Output)
                throw new ArgumentException($"Unsupported direction: {direction}");

            if (!SupportsChannelCount(channelCount))
                throw new ArgumentException($"Unsupported channel count: {channelCount}");

            if (!SupportsSampleFormat(sampleFormat))
                throw new ArgumentException($"Unsupported sample format: {sampleFormat}");

            // 设置参数 → 初始化 Oboe
            setOboeSampleRate((int)sampleRate);
            setOboeBufferSize(CalculateBufferSize(sampleRate));
            setOboeVolume(_volume);

            Logger.Info?.Print(LogClass.Audio, "Initializing Oboe audio");
            initOboeAudio();

            bool initialized = isOboeInitialized();
            Logger.Info?.Print(LogClass.Audio, $"Oboe initialization result: {initialized}");

            return new OboeAudioSession(this, sampleFormat, sampleRate, channelCount);
        }

        private int CalculateBufferSize(uint sampleRate)
        {
            const int desiredLatencyMs = 20;
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
            private ulong _playedSampleCount = 0;

            public OboeAudioSession(OboeAudioDriver driver, SampleFormat sampleFormat, uint sampleRate, uint channelCount)
            {
                _driver = driver;
                _sampleFormat = sampleFormat;
                _sampleRate = sampleRate;
                _channelCount = channelCount;
                Logger.Info?.Print(LogClass.Audio, $"OboeAudioSession created: sampleRate={sampleRate}, channelCount={channelCount}, format={sampleFormat}");
                
                // 创建时自动激活会话
                Start();
            }

            public void Dispose() 
            {
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession disposed");
            }

            public void PrepareToClose() 
            {
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession preparing to close");
            }

            public void Start() 
            {
                _active = true;
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession started");
            }
            
            public void Stop() 
            {
                _active = false;
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession stopped");
            }

            public void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) 
                {
                    Logger.Warning?.Print(LogClass.Audio, "OboeAudioSession not active, but attempting to queue buffer. Activating session.");
                    Start();
                }

                // 转换为 float[] 并发送
                float[] floatData = ConvertToFloat(buffer.Data, _sampleFormat, _volume);
                
                Logger.Debug?.Print(LogClass.Audio, $"Queueing buffer: {floatData.Length} samples, {floatData.Length / (int)_channelCount} frames");
                
                writeOboeAudio(floatData, floatData.Length / (int)_channelCount);
                
                // 更新已播放样本计数
                _playedSampleCount += (ulong)(floatData.Length / _channelCount);
            }

            public bool RegisterBuffer(AudioBuffer buffer) 
            {
                Logger.Debug?.Print(LogClass.Audio, "RegisterBuffer called");
                return true;
            }
            
            public void UnregisterBuffer(AudioBuffer buffer) 
            {
                Logger.Debug?.Print(LogClass.Audio, "UnregisterBuffer called");
            }
            
            public bool WasBufferFullyConsumed(AudioBuffer buffer) 
            {
                Logger.Debug?.Print(LogClass.Audio, "WasBufferFullyConsumed called");
                return true;
            }

            public void SetVolume(float volume)
            {
                _volume = volume;
                Logger.Info?.Print(LogClass.Audio, $"Setting volume: {volume}");
                setOboeVolume(volume);
            }

            public float GetVolume() 
            {
                Logger.Debug?.Print(LogClass.Audio, "GetVolume called");
                return _volume;
            }

            public ulong GetPlayedSampleCount() 
            {
                Logger.Debug?.Print(LogClass.Audio, $"GetPlayedSampleCount: {_playedSampleCount}");
                return _playedSampleCount;
            }

            private float[] ConvertToFloat(byte[] audioData, SampleFormat format, float volume)
            {
                if (format == SampleFormat.PcmInt16)
                {
                    int sampleCount = audioData.Length / 2;
                    float[] floatData = new float[sampleCount];

                    for (int i = 0; i < sampleCount; i++)
                    {
                        short sample = BitConverter.ToInt16(audioData, i * 2);
                        floatData[i] = sample / 32768.0f * volume;
                    }

                    return floatData;
                }

                throw new NotSupportedException($"Sample format {format} is not supported");
            }
        }
    }
}
#endif
