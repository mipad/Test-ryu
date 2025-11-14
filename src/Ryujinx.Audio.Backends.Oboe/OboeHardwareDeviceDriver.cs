// OboeHardwareDeviceDriver.cs 
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
using Ryujinx.Audio.Renderer.Dsp;
using System.Runtime.CompilerServices;

namespace Ryujinx.Audio.Backends.Oboe
{
    public class OboeHardwareDeviceDriver : IHardwareDeviceDriver, IDisposable
    {
        // ========== P/Invoke 声明 ==========
        [DllImport("libryujinxjni", EntryPoint = "initOboeAudio")]
        private static extern bool initOboeAudio(int sample_rate, int channel_count);

        [DllImport("libryujinxjni", EntryPoint = "shutdownOboeAudio")]
        private static extern void shutdownOboeAudio();

        [DllImport("libryujinxjni", EntryPoint = "writeOboeAudio")]
        private static extern bool writeOboeAudio(short[] audioData, int num_frames);

        [DllImport("libryujinxjni", EntryPoint = "setOboeVolume")]
        private static extern void setOboeVolume(float volume);

        [DllImport("libryujinxjni", EntryPoint = "isOboeInitialized")]
        private static extern bool isOboeInitialized();

        [DllImport("libryujinxjni", EntryPoint = "isOboePlaying")]
        private static extern bool isOboePlaying();

        [DllImport("libryujinxjni", EntryPoint = "getOboeBufferedFrames")]
        private static extern int getOboeBufferedFrames();

        [DllImport("libryujinxjni", EntryPoint = "resetOboeAudio")]
        private static extern void resetOboeAudio();

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private float _volume = 1.0f;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions = new();
        private bool _isOboeInitialized = false;
        private Thread _updateThread;
        private bool _stillRunning = true;
        private readonly object _initLock = new object();

        // 移除性能统计相关字段
        private int _currentChannelCount = 2;

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0f, 1.0f);
                setOboeVolume(_volume);
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
                    try
                    {
                        Thread.Sleep(10);
                        
                        if (_isOboeInitialized)
                        {
                            int bufferedFrames = getOboeBufferedFrames();
                            
                            foreach (var session in _sessions.Keys)
                            {
                                session.UpdatePlaybackStatus(bufferedFrames);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // 只保留错误日志
                    }
                }
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.Normal
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
            sampleRate == 48000;

        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat == SampleFormat.PcmInt16 ||
            sampleFormat == SampleFormat.PcmInt8 ||
            sampleFormat == SampleFormat.PcmInt24 ||
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

            if (!SupportsSampleRate(sampleRate))
                throw new ArgumentException($"Unsupported sample rate: {sampleRate}");

            lock (_initLock)
            {
                if (!_isOboeInitialized)
                {
                    InitializeOboe(sampleRate, channelCount);
                }
                else if (_currentChannelCount != channelCount)
                {
                    // 声道数变化时重新初始化
                    ReinitializeOboe(sampleRate, channelCount);
                }
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            
            return session;
        }

        private void InitializeOboe(uint sampleRate, uint channelCount)
        {
            try
            {
                if (!initOboeAudio((int)sampleRate, (int)channelCount))
                {
                    throw new Exception("Oboe audio initialization failed");
                }

                setOboeVolume(_volume);
                _isOboeInitialized = true;
                _currentChannelCount = (int)channelCount;
            }
            catch (Exception ex)
            {
                throw new Exception($"Oboe audio initialization failed: {ex}");
            }
        }

        private void ReinitializeOboe(uint sampleRate, uint channelCount)
        {
            shutdownOboeAudio();
            Thread.Sleep(50);
            
            if (!initOboeAudio((int)sampleRate, (int)channelCount))
            {
                throw new Exception("Oboe audio reinitialization failed");
            }
            
            setOboeVolume(_volume);
            _currentChannelCount = (int)channelCount;
        }

        private bool Unregister(OboeAudioSession session) 
        {
            bool removed = _sessions.TryRemove(session, out _);
            
            // 如果没有会话了，关闭音频
            if (_sessions.IsEmpty && _isOboeInitialized)
            {
                shutdownOboeAudio();
                _isOboeInitialized = false;
            }
            
            return removed;
        }

        // ========== 音频格式转换辅助方法 ==========
        private static short[] ConvertToPcm16(byte[] data, SampleFormat sourceFormat, int channelCount)
        {
            int sourceSampleSize = BackendHelper.GetSampleSize(sourceFormat);
            int sampleCount = data.Length / sourceSampleSize;
            short[] pcm16Data = new short[sampleCount];

            switch (sourceFormat)
            {
                case SampleFormat.PcmInt8:
                    ConvertPcm8ToPcm16(data, pcm16Data);
                    break;
                case SampleFormat.PcmInt16:
                    Buffer.BlockCopy(data, 0, pcm16Data, 0, data.Length);
                    break;
                case SampleFormat.PcmInt24:
                    ConvertPcm24ToPcm16(data, pcm16Data);
                    break;
                case SampleFormat.PcmInt32:
                    ConvertPcm32ToPcm16(data, pcm16Data);
                    break;
                case SampleFormat.PcmFloat:
                    ConvertPcmFloatToPcm16(data, pcm16Data);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported sample format: {sourceFormat}");
            }

            return pcm16Data;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ConvertPcm8ToPcm16(byte[] source, short[] destination)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                sbyte sample = (sbyte)source[i];
                destination[i] = (short)(sample * 256);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ConvertPcm24ToPcm16(byte[] source, short[] destination)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                int offset = i * 3;
                int sample = (source[offset] << 8) | (source[offset + 1] << 16) | (source[offset + 2] << 24);
                sample >>= 8;
                destination[i] = (short)sample;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ConvertPcm32ToPcm16(byte[] source, short[] destination)
        {
            ReadOnlySpan<int> sourceSamples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(source);
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = (short)(sourceSamples[i] >> 16);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ConvertPcmFloatToPcm16(byte[] source, short[] destination)
        {
            ReadOnlySpan<float> sourceSamples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source);
            for (int i = 0; i < destination.Length; i++)
            {
                float sample = sourceSamples[i];
                sample = Math.Clamp(sample, -1.0f, 1.0f);
                destination[i] = (short)(sample * 32767.0f);
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
            private readonly int _channelCount;
            private readonly uint _sampleRate;
            private readonly SampleFormat _sourceFormat;

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
                _sourceFormat = sampleFormat;
                _volume = 1.0f;
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                try
                {
                    // 计算已播放的样本数
                    ulong playedSamples = _totalWrittenSamples - (ulong)(bufferedFrames * _channelCount);
                    
                    // 防止回退
                    if (playedSamples < _totalPlayedSamples)
                    {
                        _totalPlayedSamples = playedSamples;
                        return;
                    }
                    
                    ulong availableSampleCount = playedSamples - _totalPlayedSamples;
                    
                    // 更新缓冲区播放状态
                    while (availableSampleCount > 0 && _queuedBuffers.TryPeek(out OboeAudioBuffer driverBuffer))
                    {
                        ulong sampleStillNeeded = driverBuffer.SampleCount - driverBuffer.SamplePlayed;
                        ulong playedAudioBufferSampleCount = Math.Min(sampleStillNeeded, availableSampleCount);
                        
                        driverBuffer.SamplePlayed += playedAudioBufferSampleCount;
                        availableSampleCount -= playedAudioBufferSampleCount;
                        _totalPlayedSamples += playedAudioBufferSampleCount;
                        
                        // 如果缓冲区播放完毕，移除它
                        if (driverBuffer.SamplePlayed == driverBuffer.SampleCount)
                        {
                            _queuedBuffers.TryDequeue(out _);
                            _driver.GetUpdateRequiredEvent().Set();
                        }
                    }
                }
                catch (Exception)
                {
                    // 静默处理错误
                }
            }

            public override void Dispose()
            {
                Stop();
                _driver.Unregister(this);
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
                    _queuedBuffers.Clear();
                }
            }

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) Start();

                if (buffer.Data == null || buffer.Data.Length == 0) return;

                short[] audioData;
                int sampleCount;
                int frameCount;

                // 格式转换：将任意格式转换为PCM16
                if (_sourceFormat != SampleFormat.PcmInt16)
                {
                    audioData = ConvertToPcm16(buffer.Data, _sourceFormat, _channelCount);
                    sampleCount = audioData.Length;
                    frameCount = sampleCount / _channelCount;
                }
                else
                {
                    // 直接使用PCM16数据
                    sampleCount = buffer.Data.Length / 2;
                    frameCount = sampleCount / _channelCount;

                    audioData = new short[sampleCount];
                    Buffer.BlockCopy(buffer.Data, 0, audioData, 0, buffer.Data.Length);
                }

                // 写入音频数据到Oboe
                bool writeSuccess = writeOboeAudio(audioData, frameCount);

                if (writeSuccess)
                {
                    _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, (ulong)sampleCount));
                    _totalWrittenSamples += (ulong)sampleCount;
                }
                else
                {
                    // 减少重置频率，避免性能影响
                    if (DateTime.Now.Ticks % 10 == 0)
                    {
                        resetOboeAudio();
                    }
                }
            }

            public override bool WasBufferFullyConsumed(AudioBuffer buffer)
            {
                return !_queuedBuffers.TryPeek(out var driverBuffer) || 
                       driverBuffer.DriverIdentifier != buffer.DataPointer;
            }

            public override void SetVolume(float volume)
            {
                _volume = Math.Clamp(volume, 0.0f, 1.0f);
                setOboeVolume(_volume);
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