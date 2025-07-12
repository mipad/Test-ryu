using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common; 
using Ryujinx.Memory;
using System;
using System.Collections.Generic;

namespace Ryujinx.Audio.Backends.Dummy
{
    class DummyHardwareDeviceSessionInput : IHardwareDeviceSession
    {
        public IHardwareDeviceDriver.Direction Direction => IHardwareDeviceDriver.Direction.Input;

        private readonly IVirtualMemoryManager _memoryManager;
        private readonly SampleFormat _sampleFormat;
        private readonly uint _sampleRate;
        private readonly uint _channelCount;

        public DummyHardwareDeviceSessionInput(
            IVirtualMemoryManager memoryManager,
            SampleFormat sampleFormat,
            uint sampleRate,
            uint channelCount)
        {
            _memoryManager = memoryManager;
            _sampleFormat = sampleFormat;
            _sampleRate = sampleRate;
            _channelCount = channelCount;
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
                DataSize = (ulong)Constants.DefaultSilenceBufferSize,
                BufferTag = 0,
                PlayedTimestamp = (ulong)PerformanceCounter.ElapsedNanoseconds
            };
        }
    }
}
