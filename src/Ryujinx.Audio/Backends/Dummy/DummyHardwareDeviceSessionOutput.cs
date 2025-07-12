using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Audio.Backends.Dummy
{
    internal class DummyHardwareDeviceSessionOutput : HardwareDeviceSessionOutputBase
    {
        private float _volume;
        private readonly IHardwareDeviceDriver _manager;

        private ulong _playedSampleCount;

        public DummyHardwareDeviceSessionOutput(IHardwareDeviceDriver manager, IVirtualMemoryManager memoryManager, SampleFormat requestedSampleFormat, uint requestedSampleRate, uint requestedChannelCount) : base(memoryManager, requestedSampleFormat, requestedSampleRate, requestedChannelCount)
        {
            _volume = 1f;
            _manager = manager;
        }

        public override void Dispose()
        {
            // Nothing to do.
        }

        public override ulong GetPlayedSampleCount()
        {
            return Interlocked.Read(ref _playedSampleCount);
        }

        public override float GetVolume()
        {
            return _volume;
        }

        public override void PrepareToClose() { }

        public override void QueueBuffer(AudioBuffer buffer)
        {
            Interlocked.Add(ref _playedSampleCount, GetSampleCount(buffer));
            _manager.GetUpdateRequiredEvent().Set();
        }

        public override void QueueBuffers(IList<AudioBuffer> buffers)
        {
            foreach (AudioBuffer buffer in buffers)
            {
                Interlocked.Add(ref _playedSampleCount, GetSampleCount(buffer));
            }
            _manager.GetUpdateRequiredEvent().Set();
        }

        public override IList<AudioBuffer> GetReleasedBuffers(int maxCount)
        {
            // 模拟设备总是立即释放缓冲区
            return new List<AudioBuffer>();
        }

        public override void SetVolume(float volume)
        {
            _volume = volume;
        }

        public override void Start() { }

        public override void Stop() { }

        public override void UnregisterBuffer(AudioBuffer buffer) { }

        public override bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            return true;
        }
    }
}
