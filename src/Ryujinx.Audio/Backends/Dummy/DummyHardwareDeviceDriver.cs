using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

namespace Ryujinx.Audio.Backends.Dummy
{
    public class DummyHardwareDeviceDriver : IHardwareDeviceDriver
    {
        private readonly ManualResetEvent _updateRequiredEvent;
        private readonly ManualResetEvent _pauseEvent;

        public static bool IsSupported => true;

        public float Volume { get; set; }

        public DummyHardwareDeviceDriver()
        {
            _updateRequiredEvent = new ManualResetEvent(false);
            _pauseEvent = new ManualResetEvent(true);

            Volume = 1f;
        }

        // 修复1: 使用完全限定名解决 Direction 冲突
        public IHardwareDeviceSession OpenDeviceSession(IHardwareDeviceDriver.Direction direction, IVirtualMemoryManager memoryManager, SampleFormat sampleFormat, uint sampleRate, uint channelCount)
        {
            if (sampleRate == 0)
            {
                sampleRate = Constants.TargetSampleRate;
            }

            if (channelCount == 0)
            {
                channelCount = 2;
            }

            // 修复2: 使用完全限定名
            if (direction == IHardwareDeviceDriver.Direction.Output)
            {
                return new DummyHardwareDeviceSessionOutput(this, memoryManager, sampleFormat, sampleRate, channelCount);
            }
            else
            {
                return new DummyHardwareDeviceSessionInput(this, memoryManager, sampleFormat, sampleRate, channelCount);
            }
        }

        public ManualResetEvent GetUpdateRequiredEvent()
        {
            return _updateRequiredEvent;
        }

        public ManualResetEvent GetPauseEvent()
        {
            return _pauseEvent;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // NOTE: The _updateRequiredEvent will be disposed somewhere else.
                _pauseEvent.Dispose();
            }
        }

        public bool SupportsSampleRate(uint sampleRate)
        {
            return true;
        }

        public bool SupportsSampleFormat(SampleFormat sampleFormat)
        {
            return true;
        }

        // 修复3: 使用完全限定名解决 Direction 冲突
        public bool SupportsDirection(IHardwareDeviceDriver.Direction direction)
        {
            return direction == IHardwareDeviceDriver.Direction.Output || 
                   direction == IHardwareDeviceDriver.Direction.Input;
        }

        public bool SupportsChannelCount(uint channelCount)
        {
            return channelCount == 1 || channelCount == 2 || channelCount == 6;
        }
    }
}
