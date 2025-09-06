#if ANDROID
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;
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

        private bool _disposed;

        public OboeAudioDriver()
        {
            initOboeAudio();
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

            // 设置采样率
            setOboeSampleRate((int)sampleRate);
            
            return new OboeAudioSession(this, sampleFormat, sampleRate, channelCount);
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
                }

                _disposed = true;
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
            private bool _active;
            private float _volume;

            public OboeAudioSession(OboeAudioDriver driver, SampleFormat sampleFormat, uint sampleRate, uint channelCount)
            {
                _driver = driver;
                _sampleFormat = sampleFormat;
                _sampleRate = sampleRate;
                _channelCount = channelCount;
                _volume = 1.0f;
            }

            public void Dispose()
            {
                // 清理资源
            }

            public void PrepareToClose()
            {
                // 准备关闭
            }

            public void Start()
            {
                _active = true;
            }

            public void Stop()
            {
                _active = false;
            }

            public void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) return;

                // 将音频数据转换为float格式并发送到Oboe
                float[] floatData = ConvertToFloat(buffer.Data, _sampleFormat);
                writeOboeAudio(floatData, floatData.Length / (int)_channelCount);
            }

            public void UnregisterBuffer(AudioBuffer buffer)
            {
                // 不需要实现，因为Oboe是实时处理的
            }

            public bool WasBufferFullyConsumed(AudioBuffer buffer)
            {
                // Oboe是实时处理的，所以缓冲区总是被消费
                return true;
            }

            public void SetVolume(float volume)
            {
                _volume = volume;
                // 音量调整在Oboe渲染器中处理
            }

            public float GetVolume()
            {
                return _volume;
            }

            public ulong GetPlayedSampleCount()
            {
                // 返回一个估计值，因为Oboe是实时处理的
                return 0;
            }

            private float[] ConvertToFloat(byte[] audioData, SampleFormat format)
            {
                if (format == SampleFormat.PcmInt16)
                {
                    int sampleCount = audioData.Length / 2;
                    float[] floatData = new float[sampleCount];
                    
                    for (int i = 0; i < sampleCount; i++)
                    {
                        short sample = (short)((audioData[i * 2 + 1] << 8) | audioData[i * 2]);
                        floatData[i] = sample / 32768.0f * _volume;
                    }
                    
                    return floatData;
                }
                
                throw new NotSupportedException($"Sample format {format} is not supported");
            }
        }
    }
}
#endif
