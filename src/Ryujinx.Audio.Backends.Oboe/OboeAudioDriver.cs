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

        // 修改writeOboeAudio的声明，使用更安全的封送方式
        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudio")]
        private static extern void writeOboeAudio(
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] audioData, 
            int numFrames);

        [DllImport("libryujinxjni", EntryPoint = "setOboeSampleRate")]
        private static extern void setOboeSampleRate(int sampleRate);

        [DllImport("libryujinxjni", EntryPoint = "setOboeBufferSize")]
        private static extern void setOboeBufferSize(int bufferSize);

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
            _volume = 1.0f;
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
                    _isOboeInitialized = false;
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                }
                _disposed = true;
            }
        }

        // ========== 设备能力查询 ==========
        public bool SupportsSampleRate(uint sampleRate)
        {
            return sampleRate == 48000 || sampleRate == 44100 || sampleRate == 32000 || sampleRate == 24000 || sampleRate == 16000;
        }

        public bool SupportsSampleFormat(SampleFormat sampleFormat)
        {
            return sampleFormat == SampleFormat.PcmInt16;
        }

        public bool SupportsChannelCount(uint channelCount)
        {
            return channelCount == 1 || channelCount == 2 || channelCount == 6;
        }

        public bool SupportsDirection(IHardwareDeviceDriver.Direction direction)
        {
            return direction == IHardwareDeviceDriver.Direction.Output;
        }

        // ========== 事件 ==========
        public ManualResetEvent GetPauseEvent()
        {
            return _pauseEvent;
        }
        
        public ManualResetEvent GetUpdateRequiredEvent()
        {
            return _updateRequiredEvent;
        }

        // ========== 打开设备会话 ==========
        public IHardwareDeviceSession OpenDeviceSession(
            IHardwareDeviceDriver.Direction direction,
            IVirtualMemoryManager memoryManager,
            SampleFormat sampleFormat,
            uint sampleRate,
            uint channelCount)
        {
            if (direction != IHardwareDeviceDriver.Direction.Output)
            {
                throw new ArgumentException($"Unsupported direction: {direction}");
            }

            if (!SupportsChannelCount(channelCount))
            {
                throw new ArgumentException($"Unsupported channel count: {channelCount}");
            }

            if (!SupportsSampleFormat(sampleFormat))
            {
                throw new ArgumentException($"Unsupported sample format: {sampleFormat}");
            }

            // 设置参数 → 初始化 Oboe（仅初始化一次）
            if (!_isOboeInitialized)
            {
                setOboeSampleRate((int)sampleRate);
                setOboeBufferSize(CalculateBufferSize(sampleRate));
                setOboeVolume(_volume);

                initOboeAudio();

                _isOboeInitialized = isOboeInitialized();

                if (!_isOboeInitialized)
                {
                    throw new Exception("Oboe audio failed to initialize");
                }
            }

            OboeAudioSession session = new OboeAudioSession(this, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            
            return session;
        }

        private bool Unregister(OboeAudioSession session)
        {
            return _sessions.TryRemove(session, out _);
        }

        private int CalculateBufferSize(uint sampleRate)
        {
            const int desiredLatencyMs = 20;
            return (int)(sampleRate * desiredLatencyMs / 1000);
        }

        // ========== 音频会话类 ==========
        private class OboeAudioSession : HardwareDeviceSessionOutputBase
        {
            private readonly OboeAudioDriver _driver;
            private readonly ConcurrentQueue<OboeAudioBuffer> _queuedBuffers;
            private ulong _playedSampleCount;
            private bool _active;
            private float _volume;
            private readonly int _bytesPerFrame;

            public OboeAudioSession(OboeAudioDriver driver, SampleFormat sampleFormat, uint sampleRate, uint channelCount) 
                : base(null, sampleFormat, sampleRate, channelCount)
            {
                _driver = driver;
                _queuedBuffers = new ConcurrentQueue<OboeAudioBuffer>();
                _playedSampleCount = 0;
                _active = false;
                _volume = 1.0f;
                _bytesPerFrame = BackendHelper.GetSampleSize(sampleFormat) * (int)channelCount;
            }

            public override void Dispose()
            {
                _driver.Unregister(this);
            }

            public override void PrepareToClose()
            {
            }

            public override void Start()
            {
                _active = true;
            }

            public override void Stop()
            {
                _active = false;
            }

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active)
                {
                    Start();
                }

                if (buffer.Data == null || buffer.Data.Length == 0)
                {
                    return;
                }

                // 转换为 float[] 并发送
                float[] floatData = ConvertToFloat(buffer.Data, RequestedSampleFormat, _volume);
                
                int numFrames = floatData.Length / (int)RequestedChannelCount;
                
                try
                {
                    writeOboeAudio(floatData, numFrames);
                    
                    // 创建缓冲区记录
                    OboeAudioBuffer driverBuffer = new OboeAudioBuffer(buffer.DataPointer, GetSampleCount(buffer));
                    _queuedBuffers.Enqueue(driverBuffer);
                }
                catch (Exception ex)
                {
                    // 静默处理异常
                }
            }

            public override bool WasBufferFullyConsumed(AudioBuffer buffer)
            {
                if (!_queuedBuffers.TryPeek(out OboeAudioBuffer driverBuffer))
                {
                    return true;
                }

                return driverBuffer.DriverIdentifier != buffer.DataPointer;
            }

            public override void SetVolume(float volume)
            {
                _volume = volume;
                setOboeVolume(volume);
            }

            public override float GetVolume()
            {
                return _volume;
            }

            public override ulong GetPlayedSampleCount()
            {
                int bufferedFrames = getOboeBufferedFrames();
                ulong totalQueuedSamples = 0;
                
                foreach (var buffer in _queuedBuffers)
                {
                    totalQueuedSamples += buffer.SampleCount;
                }
                
                ulong playedSamples = totalQueuedSamples - (ulong)bufferedFrames * RequestedChannelCount;
                _playedSampleCount = Math.Max(_playedSampleCount, playedSamples);
                
                return _playedSampleCount;
            }

            public override void UnregisterBuffer(AudioBuffer buffer)
            {
                while (_queuedBuffers.TryPeek(out OboeAudioBuffer driverBuffer) && 
                       driverBuffer.DriverIdentifier != buffer.DataPointer)
                {
                    _queuedBuffers.TryDequeue(out OboeAudioBuffer consumedBuffer);
                }
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
