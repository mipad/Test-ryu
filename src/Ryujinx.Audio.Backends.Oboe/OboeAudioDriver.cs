#if ANDROID
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

namespace Ryujinx.Audio.Backends.Oboe
{
    public class OboeAudioDriver : IHardwareDeviceDriver, IDisposable
    {
        [DllImport("libryujinxjni")]
        private static extern void initOboeAudio();
        
        [DllImport("libryujinxjni")]
        private static extern void shutdownOboeAudio();
        
        [DllImport("libryujinxjni")]
        private static extern void writeOboeAudio(float[] audioData, int numFrames);
        
        [DllImport("libryujinxjni")]
        private static extern void setOboeSampleRate(int sampleRate);
        
        [DllImport("libryujinxjni")]
        private static extern void setOboeBufferSize(int bufferSize);

        private readonly ManualResetEvent _updateRequiredEvent;
        private readonly ManualResetEvent _pauseEvent;
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions;
        private bool _stillRunning;
        private readonly Thread _updaterThread;

        private float _volume;
        private bool _isInitialized;

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                foreach (OboeAudioSession session in _sessions.Keys)
                {
                    session.UpdateMasterVolume(value);
                }
            }
        }

        public OboeAudioDriver()
        {
            _updateRequiredEvent = new ManualResetEvent(false);
            _pauseEvent = new ManualResetEvent(true);
            _sessions = new ConcurrentDictionary<OboeAudioSession, byte>();

            _stillRunning = true;
            _updaterThread = new Thread(Update)
            {
                Name = "HardwareDeviceDriver.Oboe",
                IsBackground = true
            };

            _volume = 1f;
            _isInitialized = false;
            
            _updaterThread.Start();
        }

        public static bool IsSupported
        {
            get
            {
                try
                {
                    // 尝试初始化 Oboe 来检查是否支持
                    initOboeAudio();
                    shutdownOboeAudio();
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public ManualResetEvent GetPauseEvent()
        {
            return _pauseEvent;
        }

        public ManualResetEvent GetUpdateRequiredEvent()
        {
            return _updateRequiredEvent;
        }

        public IHardwareDeviceSession OpenDeviceSession(Direction direction, IVirtualMemoryManager memoryManager, SampleFormat sampleFormat, uint sampleRate, uint channelCount)
        {
            if (direction != Direction.Output)
            {
                throw new ArgumentException($"{direction}");
            }
            else if (!SupportsChannelCount(channelCount))
            {
                throw new ArgumentException($"{channelCount}");
            }

            // 在这里设置参数 + 初始化
            setOboeSampleRate((int)sampleRate);
            setOboeBufferSize(CalculateBufferSize(sampleRate));
            
            // 此时才初始化音频流
            if (!_isInitialized)
            {
                initOboeAudio();
                _isInitialized = true;
            }

            OboeAudioSession session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);

            _sessions.TryAdd(session, 0);

            return session;
        }

        private int CalculateBufferSize(uint sampleRate)
        {
            int desiredLatencyMs = 20; // 目标延迟 20ms
            return (int)(sampleRate * desiredLatencyMs / 1000);
        }

        internal bool Unregister(OboeAudioSession session)
        {
            return _sessions.TryRemove(session, out _);
        }

        private void Update()
        {
            while (_stillRunning)
            {
                bool updateRequired = false;

                foreach (OboeAudioSession session in _sessions.Keys)
                {
                    if (session.Update())
                    {
                        updateRequired = true;
                    }
                }

                if (updateRequired)
                {
                    _updateRequiredEvent.Set();
                }

                // 休眠以避免占用过多CPU
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stillRunning = false;

                foreach (OboeAudioSession session in _sessions.Keys)
                {
                    session.Dispose();
                }

                if (_isInitialized)
                {
                    shutdownOboeAudio();
                    _isInitialized = false;
                }
                
                _pauseEvent?.Dispose();
                _updateRequiredEvent?.Dispose();
            }
        }

        public bool SupportsSampleRate(uint sampleRate)
        {
            return sampleRate == 48000 || sampleRate == 44100 || sampleRate == 32000;
        }

        public bool SupportsSampleFormat(SampleFormat sampleFormat)
        {
            return sampleFormat == SampleFormat.PcmInt16;
        }

        public bool SupportsChannelCount(uint channelCount)
        {
            return channelCount == 1 || channelCount == 2 || channelCount == 6;
        }

        public bool SupportsDirection(Direction direction)
        {
            return direction == Direction.Output;
        }

        // Oboe音频会话类
        private class OboeAudioSession : IHardwareDeviceSession
        {
            private readonly OboeAudioDriver _driver;
            private readonly SampleFormat _sampleFormat;
            private readonly uint _sampleRate;
            private readonly uint _channelCount;
            private bool _isActive;
            private float _volume;
            private ulong _playedSampleCount;
            private readonly ConcurrentQueue<OboeAudioBuffer> _queuedBuffers;
            private readonly object _lock = new object();

            public OboeAudioSession(OboeAudioDriver driver, IVirtualMemoryManager memoryManager, SampleFormat sampleFormat, uint sampleRate, uint channelCount)
            {
                _driver = driver;
                _sampleFormat = sampleFormat;
                _sampleRate = sampleRate;
                _channelCount = channelCount;
                _isActive = false;
                _volume = 1.0f;
                _playedSampleCount = 0;
                _queuedBuffers = new ConcurrentQueue<OboeAudioBuffer>();
            }

            public void Dispose()
            {
                // 清理所有排队的缓冲区
                while (_queuedBuffers.TryDequeue(out OboeAudioBuffer buffer))
                {
                    // 缓冲区由Oboe管理，不需要手动释放
                }
                
                _driver.Unregister(this);
            }

            public void PrepareToClose()
            {
                // 准备关闭
            }

            public void Start()
            {
                _isActive = true;
            }

            public void Stop()
            {
                _isActive = false;
            }

            public void QueueBuffer(AudioBuffer buffer)
            {
                if (!_isActive) return;

                // 将音频数据转换为float格式并发送到Oboe
                float[] floatData = ConvertToFloat(buffer.Data, _sampleFormat);
                
                // 创建缓冲区记录
                OboeAudioBuffer oboeBuffer = new OboeAudioBuffer
                {
                    DriverIdentifier = buffer.DataPointer,
                    SampleCount = GetSampleCount(buffer)
                };
                
                _queuedBuffers.Enqueue(oboeBuffer);
                
                // 写入音频数据
                writeOboeAudio(floatData, floatData.Length / (int)_channelCount);
            }

            public bool RegisterBuffer(AudioBuffer buffer)
            {
                // Oboe是实时处理的，不需要注册缓冲区
                return true;
            }

            public void UnregisterBuffer(AudioBuffer buffer)
            {
                // 不需要实现，因为Oboe是实时处理的
            }

            public bool WasBufferFullyConsumed(AudioBuffer buffer)
            {
                // 检查缓冲区是否已被处理
                return !_queuedBuffers.Any(b => b.DriverIdentifier == buffer.DataPointer);
            }

            public void SetVolume(float volume)
            {
                _volume = volume;
            }

            public float GetVolume()
            {
                return _volume;
            }

            public void UpdateMasterVolume(float masterVolume)
            {
                // 主音量更新时应用
                _volume = masterVolume;
            }

            public ulong GetPlayedSampleCount()
            {
                return _playedSampleCount;
            }

            public bool Update()
            {
                // 模拟缓冲区处理，实际上Oboe是实时处理的
                // 这里我们假设所有已写入的缓冲区都已被处理
                bool buffersProcessed = false;
                
                while (_queuedBuffers.TryPeek(out OboeAudioBuffer buffer))
                {
                    _playedSampleCount += buffer.SampleCount;
                    _queuedBuffers.TryDequeue(out _);
                    buffersProcessed = true;
                }
                
                return buffersProcessed;
            }

            private ulong GetSampleCount(AudioBuffer buffer)
            {
                return (ulong)(buffer.Data.Length / (GetSampleSize() * _channelCount));
            }

            private int GetSampleSize()
            {
                return _sampleFormat switch
                {
                    SampleFormat.PcmInt16 => 2,
                    _ => throw new NotImplementedException($"Unsupported sample format {_sampleFormat}"),
                };
            }

            private float[] ConvertToFloat(byte[] audioData, SampleFormat format)
            {
                if (format == SampleFormat.PcmInt16)
                {
                    int sampleCount = audioData.Length / 2;
                    float[] floatData = new float[sampleCount];
                    
                    for (int i = 0; i < sampleCount; i++)
                    {
                        short sample = BitConverter.ToInt16(audioData, i * 2);
                        floatData[i] = sample / 32768.0f * _volume;
                    }
                    
                    return floatData;
                }
                
                throw new NotSupportedException($"Sample format {format} is not supported");
            }
        }
        
        private class OboeAudioBuffer
        {
            public ulong DriverIdentifier;
            public ulong SampleCount;
        }
    }
}
#endif
