// OboeAudioDriver.cs
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
        private static extern void writeOboeAudio(float[] audioData, int numFrames);

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
                    Logger.Info?.Print(LogClass.Audio, "Shutting down Oboe audio");
                    shutdownOboeAudio();
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                }
                _disposed = true;
                Logger.Info?.Print(LogClass.Audio, "OboeAudioDriver disposed");
            }
        }

        // ========== 设备能力查询 ==========
        public bool SupportsSampleRate(uint sampleRate)
        {
            bool supported = sampleRate == 48000 || sampleRate == 44100 || sampleRate == 32000;
            Logger.Debug?.Print(LogClass.Audio, $"SupportsSampleRate({sampleRate}): {supported}");
            return supported;
        }

        public bool SupportsSampleFormat(SampleFormat sampleFormat)
        {
            bool supported = sampleFormat == SampleFormat.PcmInt16;
            Logger.Debug?.Print(LogClass.Audio, $"SupportsSampleFormat({sampleFormat}): {supported}");
            return supported;
        }

        public bool SupportsChannelCount(uint channelCount)
        {
            bool supported = channelCount == 1 || channelCount == 2 || channelCount == 6;
            Logger.Debug?.Print(LogClass.Audio, $"SupportsChannelCount({channelCount}): {supported}");
            return supported;
        }

        public bool SupportsDirection(IHardwareDeviceDriver.Direction direction)
        {
            bool supported = direction == IHardwareDeviceDriver.Direction.Output;
            Logger.Debug?.Print(LogClass.Audio, $"SupportsDirection({direction}): {supported}");
            return supported;
        }

        // ========== 事件 ==========
        public ManualResetEvent GetPauseEvent()
        {
            Logger.Debug?.Print(LogClass.Audio, "GetPauseEvent called");
            return _pauseEvent;
        }
        
        public ManualResetEvent GetUpdateRequiredEvent()
        {
            Logger.Debug?.Print(LogClass.Audio, "GetUpdateRequiredEvent called");
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
            Logger.Info?.Print(LogClass.Audio, $"Opening Oboe device session: direction={direction}, sampleRate={sampleRate}, channelCount={channelCount}, format={sampleFormat}");

            if (direction != IHardwareDeviceDriver.Direction.Output)
            {
                Logger.Error?.Print(LogClass.Audio, $"Unsupported direction: {direction}");
                throw new ArgumentException($"Unsupported direction: {direction}");
            }

            if (!SupportsChannelCount(channelCount))
            {
                Logger.Error?.Print(LogClass.Audio, $"Unsupported channel count: {channelCount}");
                throw new ArgumentException($"Unsupported channel count: {channelCount}");
            }

            if (!SupportsSampleFormat(sampleFormat))
            {
                Logger.Error?.Print(LogClass.Audio, $"Unsupported sample format: {sampleFormat}");
                throw new ArgumentException($"Unsupported sample format: {sampleFormat}");
            }

            // 设置参数 → 初始化 Oboe
            Logger.Info?.Print(LogClass.Audio, $"Setting Oboe parameters: sampleRate={(int)sampleRate}, bufferSize={CalculateBufferSize(sampleRate)}");
            setOboeSampleRate((int)sampleRate);
            setOboeBufferSize(CalculateBufferSize(sampleRate));
            setOboeVolume(_volume);

            Logger.Info?.Print(LogClass.Audio, "Initializing Oboe audio");
            initOboeAudio();

            bool initialized = isOboeInitialized();
            Logger.Info?.Print(LogClass.Audio, $"Oboe initialization result: {initialized}");

            if (!initialized)
            {
                Logger.Error?.Print(LogClass.Audio, "Oboe audio failed to initialize!");
                throw new Exception("Oboe audio failed to initialize");
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
            int bufferSize = (int)(sampleRate * desiredLatencyMs / 1000);
            Logger.Debug?.Print(LogClass.Audio, $"CalculateBufferSize({sampleRate}): {bufferSize}");
            return bufferSize;
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
                
                Logger.Info?.Print(LogClass.Audio, $"OboeAudioSession created: sampleRate={sampleRate}, channelCount={channelCount}, format={sampleFormat}");
            }

            public override void Dispose()
            {
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession disposed");
                _driver.Unregister(this);
            }

            public override void PrepareToClose()
            {
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession preparing to close");
            }

            public override void Start()
            {
                _active = true;
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession started");
            }

            public override void Stop()
            {
                _active = false;
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession stopped");
            }

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active)
                {
                    Logger.Warning?.Print(LogClass.Audio, "OboeAudioSession not active, but attempting to queue buffer. Activating session.");
                    Start();
                }

                if (buffer.Data == null || buffer.Data.Length == 0)
                {
                    Logger.Warning?.Print(LogClass.Audio, "Empty audio buffer received");
                    return;
                }

                // 转换为 float[] 并发送
                float[] floatData = ConvertToFloat(buffer.Data, RequestedSampleFormat, _volume);
                
                int numFrames = floatData.Length / (int)RequestedChannelCount;
                
                Logger.Debug?.Print(LogClass.Audio, $"Queueing buffer: {floatData.Length} samples, {numFrames} frames");
                
                try
                {
                    writeOboeAudio(floatData, numFrames);
                    
                    // 创建缓冲区记录
                    OboeAudioBuffer driverBuffer = new OboeAudioBuffer(buffer.DataPointer, GetSampleCount(buffer));
                    _queuedBuffers.Enqueue(driverBuffer);
                    
                    Logger.Debug?.Print(LogClass.Audio, "Audio data sent to Oboe successfully");
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Failed to write audio data to Oboe: {ex.Message}");
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
                Logger.Info?.Print(LogClass.Audio, $"Setting session volume: {volume}");
                setOboeVolume(volume);
            }

            public override float GetVolume()
            {
                Logger.Debug?.Print(LogClass.Audio, "GetVolume called");
                return _volume;
            }

            public override ulong GetPlayedSampleCount()
            {
                // 从 Oboe 获取已缓冲的帧数并计算已播放的样本数
                int bufferedFrames = getOboeBufferedFrames();
                ulong totalQueuedSamples = 0;
                
                foreach (var buffer in _queuedBuffers)
                {
                    totalQueuedSamples += buffer.SampleCount;
                }
                
                ulong playedSamples = totalQueuedSamples - (ulong)bufferedFrames * RequestedChannelCount;
                _playedSampleCount = Math.Max(_playedSampleCount, playedSamples);
                
                Logger.Debug?.Print(LogClass.Audio, $"GetPlayedSampleCount: {_playedSampleCount} (buffered: {bufferedFrames})");
                return _playedSampleCount;
            }

            public override void UnregisterBuffer(AudioBuffer buffer)
            {
                Logger.Debug?.Print(LogClass.Audio, "UnregisterBuffer called");
                // 从队列中移除已消耗的缓冲区
                while (_queuedBuffers.TryPeek(out OboeAudioBuffer driverBuffer) && 
                       driverBuffer.DriverIdentifier != buffer.DataPointer)
                {
                    if (_queuedBuffers.TryDequeue(out OboeAudioBuffer consumedBuffer))
                    {
                        Logger.Debug?.Print(LogClass.Audio, $"Unregistered buffer: {consumedBuffer.DriverIdentifier}");
                    }
                }
            }

            private float[] ConvertToFloat(byte[] audioData, SampleFormat format, float volume)
            {
                Logger.Debug?.Print(LogClass.Audio, $"Converting audio data: {audioData.Length} bytes, format: {format}, volume: {volume}");
                
                if (format == SampleFormat.PcmInt16)
                {
                    int sampleCount = audioData.Length / 2;
                    float[] floatData = new float[sampleCount];

                    for (int i = 0; i < sampleCount; i++)
                    {
                        short sample = BitConverter.ToInt16(audioData, i * 2);
                        floatData[i] = sample / 32768.0f * volume;
                    }

                    Logger.Debug?.Print(LogClass.Audio, $"Converted {sampleCount} samples to float format");
                    return floatData;
                }

                Logger.Error?.Print(LogClass.Audio, $"Unsupported sample format: {format}");
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
