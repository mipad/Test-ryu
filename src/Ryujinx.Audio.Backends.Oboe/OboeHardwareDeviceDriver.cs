// OboeHardwareDeviceDriver.cs (深度修复版)
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
        private int _sampleRate = 48000;

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
                    Thread.Sleep(15); // 进一步增加间隔时间减少CPU使用
                    
                    foreach (var session in _sessions.Keys)
                    {
                        int bufferedFrames = getOboeBufferedFrames();
                        session.UpdatePlaybackStatus(bufferedFrames);
                    }
                }
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.Normal // 改回正常优先级
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
                    _updateThread?.Join(100);
                    
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
            sampleRate is 48000 or 44100 or 32000 or 24000 or 16000 or 96000;

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

            // 保存采样率
            _sampleRate = (int)sampleRate;

            // 延迟初始化
            if (!_isOboeInitialized)
            {
                setOboeSampleRate(_sampleRate);
                setOboeBufferSize(CalculateBufferSize(sampleRate, channelCount));
                setOboeVolume(_volume);

                initOboeAudio();
                _isOboeInitialized = isOboeInitialized();

                if (!_isOboeInitialized)
                    throw new Exception("Oboe audio failed to initialize");
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            return session;
        }

        private bool Unregister(OboeAudioSession session) => _sessions.TryRemove(session, out _);

        private int CalculateBufferSize(uint sampleRate, uint channelCount)
        {
            // 使用固定大小的缓冲区而不是基于延迟时间的计算
            return 4096 * (int)channelCount; // 固定大小的缓冲区
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

                // --- 优化的流量控制逻辑 ---
                int bufferedFrames = getOboeBufferedFrames();
                int maxBufferedFrames = 8192; // 增加最大缓冲帧数

                // 如果缓冲过多，就等待一段时间再重试
                int waitCount = 0;
                while (bufferedFrames > maxBufferedFrames && _driver._stillRunning && waitCount < 20)
                {
                    Thread.Sleep(10);
                    bufferedFrames = getOboeBufferedFrames();
                    waitCount++;
                }

                // 优化：复用临时数组
                int sampleCount = buffer.Data.Length / 2;
                if (_driver._tempFloatBuffer.Length < sampleCount)
                    _driver._tempFloatBuffer = new float[sampleCount];

                ConvertToFloatInPlace(buffer.Data, _driver._tempFloatBuffer, sampleCount, _volume);
                
                // 分批写入以避免缓冲区溢出
                int framesPerWrite = 512; // 小批量写入
                int totalFrames = sampleCount / _channelCount;
                
                for (int offset = 0; offset < totalFrames; offset += framesPerWrite)
                {
                    int framesToWrite = Math.Min(framesPerWrite, totalFrames - offset);
                    int samplesToWrite = framesToWrite * _channelCount;
                    float[] chunk = new float[samplesToWrite];
                    
                    Array.Copy(_driver._tempFloatBuffer, offset * _channelCount, chunk, 0, samplesToWrite);
                    writeOboeAudio(chunk, framesToWrite);
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
                // 使用更精确的转换方法
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(audioData, i * 2);
                    // 使用更精确的归一化方法
                    output[i] = (sample / 32768.0f) * volume;
                    
                    // 应用轻微的平滑处理减少爆音
                    if (i > 0)
                    {
                        output[i] = (output[i] + output[i-1] * 0.1f) / 1.1f;
                    }
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
