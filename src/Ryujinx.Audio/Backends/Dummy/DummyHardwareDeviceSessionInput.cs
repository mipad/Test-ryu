using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

namespace Ryujinx.Audio.Backends.Dummy
{
    class DummyHardwareDeviceSessionInput : IHardwareDeviceSession
    {
        public SessionDirection Direction => SessionDirection.Input;

        public DummyHardwareDeviceSessionInput(
            DummyHardwareDeviceDriver driver,
            IVirtualMemoryManager memoryManager,
            SampleFormat sampleFormat,
            uint sampleRate,
            uint channelCount)
        {
            // 初始化逻辑保留但为空
        }

        public void Dispose()
        {
            // 无资源需要释放
        }

        public ulong GetPlayedSampleCount()
        {
            return 0;
        }

        public float GetVolume()
        {
            return 1.0f;
        }

        public void PrepareToClose() { }

        public void QueueBuffer(AudioBuffer buffer) { }

        public void QueueBuffers(IList<AudioBuffer> buffers)
        {
            // 批量提交缓冲区的空实现
        }

        public IList<AudioBuffer> GetReleasedBuffers(int maxCount)
        {
            return new List<AudioBuffer>();
        }

        public bool RegisterBuffer(AudioBuffer buffer)
        {
            return true;
        }

        public void SetVolume(float volume) { }

        public void Start() { }

        public void Stop() { }

        public void UnregisterBuffer(AudioBuffer buffer) { }

        public bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            return true;
        }

        public AudioBuffer CreateSilenceBuffer()
        {
            return new AudioBuffer
            {
                Data = new byte[Constants.DefaultSilenceBufferSize],
                DataSize = Constants.DefaultSilenceBufferSize,
                BufferTag = 0,
                PlayedTimestamp = (ulong)PerformanceCounter.ElapsedNanoseconds
            };
        }
    }
}
