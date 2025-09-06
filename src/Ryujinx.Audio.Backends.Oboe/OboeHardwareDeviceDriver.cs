#if ANDROID
using Oboe;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

namespace Ryujinx.Audio.Backends.Oboe
{
    public class OboeHardwareDeviceDriver : IHardwareDeviceDriver
    {
        private readonly ManualResetEvent _updateRequiredEvent;
        private readonly ManualResetEvent _pauseEvent;
        private readonly ConcurrentDictionary<OboeHardwareDeviceSession, byte> _sessions;
        private bool _stillRunning;
        private readonly Thread _updaterThread;

        private float _volume;
        private readonly object _volumeLock = new();

        private AudioStream _audioStream;
        private AudioStreamBuilder _streamBuilder;

        public float Volume
        {
            get
            {
                lock (_volumeLock)
                {
                    return _volume;
                }
            }
            set
            {
                lock (_volumeLock)
                {
                    _volume = value;

                    foreach (OboeHardwareDeviceSession session in _sessions.Keys)
                    {
                        session.UpdateMasterVolume(value);
                    }
                }
            }
        }

        public OboeHardwareDeviceDriver()
        {
            _updateRequiredEvent = new ManualResetEvent(false);
            _pauseEvent = new ManualResetEvent(true);
            _sessions = new ConcurrentDictionary<OboeHardwareDeviceSession, byte>();

            _stillRunning = true;
            _updaterThread = new Thread(Update)
            {
                Name = "HardwareDeviceDriver.Oboe",
                IsBackground = true
            };

            _volume = 1f;

            InitializeOboeStream();

            _updaterThread.Start();
        }

        private void InitializeOboeStream()
        {
            try
            {
                _streamBuilder = new AudioStreamBuilder()
                    .SetDirection(Direction.Output)
                    .SetPerformanceMode(PerformanceMode.LowLatency)
                    .SetSharingMode(SharingMode.Exclusive)
                    .SetFormat(SampleFormat.I16)
                    .SetChannelCount(ChannelCount.Stereo)
                    .SetSampleRate((int)Constants.TargetSampleRate)
                    .SetDataCallback(new OboeDataCallback(this))
                    .SetErrorCallback(new OboeErrorCallback(this));

                _audioStream = _streamBuilder.Build();
                
                if (_audioStream != null)
                {
                    _audioStream.RequestStart();
                }
            }
            catch (Exception ex)
            {
                // Handle initialization error
                // In a real implementation, you might want to log this error
            }
        }

        public static bool IsSupported
        {
            get
            {
                try
                {
                    // Oboe is supported on Android API 16+ (Jelly Bean)
                    return Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.JellyBean;
                }
                catch
                {
                    return false;
                }
            }
        }

        public IHardwareDeviceSession OpenDeviceSession(Direction direction, IVirtualMemoryManager memoryManager, SampleFormat sampleFormat, uint sampleRate, uint channelCount)
        {
            if (channelCount == 0)
            {
                channelCount = 2;
            }

            if (sampleRate == 0)
            {
                sampleRate = Constants.TargetSampleRate;
            }

            if (direction != Direction.Output)
            {
                throw new ArgumentException($"{direction}");
            }
            else if (!SupportsChannelCount(channelCount))
            {
                throw new ArgumentException($"{channelCount}");
            }

            OboeHardwareDeviceSession session = new(this, memoryManager, sampleFormat, sampleRate, channelCount);

            _sessions.TryAdd(session, 0);

            return session;
        }

        internal bool Unregister(OboeHardwareDeviceSession session)
        {
            return _sessions.TryRemove(session, out _);
        }

        public ManualResetEvent GetUpdateRequiredEvent()
        {
            return _updateRequiredEvent;
        }

        public ManualResetEvent GetPauseEvent()
        {
            return _pauseEvent;
        }

        internal void OnAudioReady()
        {
            bool updateRequired = false;

            foreach (OboeHardwareDeviceSession session in _sessions.Keys)
            {
                if (session.NeedsUpdate())
                {
                    updateRequired = true;
                }
            }

            if (updateRequired)
            {
                _updateRequiredEvent.Set();
            }
        }

        private void Update()
        {
            while (_stillRunning)
            {
                Thread.Sleep(10);
            }
        }

        internal byte[] GetMixedAudioData(int numFrames)
        {
            // Mix audio from all active sessions
            byte[] mixedData = new byte[numFrames * 2 * 2]; // 16-bit stereo

            foreach (OboeHardwareDeviceSession session in _sessions.Keys)
            {
                if (session.IsActive)
                {
                    byte[] sessionData = session.GetAudioData(numFrames);
                    if (sessionData != null)
                    {
                        MixAudioData(mixedData, sessionData);
                    }
                }
            }

            return mixedData;
        }

        private void MixAudioData(byte[] target, byte[] source)
        {
            // Simple audio mixing - in a real implementation, you'd need proper
            // audio mixing with clipping protection
            for (int i = 0; i < Math.Min(target.Length, source.Length); i++)
            {
                int mixed = target[i] + source[i];
                target[i] = (byte)Math.Min(Math.Max(mixed, 0), 255);
            }
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
                _stillRunning = false;

                foreach (OboeHardwareDeviceSession session in _sessions.Keys)
                {
                    session.Dispose();
                }

                _audioStream?.Close();
                _audioStream?.Dispose();
                _pauseEvent.Dispose();
            }
        }

        public bool SupportsSampleRate(uint sampleRate)
        {
            return sampleRate == 48000; // Oboe supports various rates, but we'll use 48kHz for compatibility
        }

        public bool SupportsSampleFormat(SampleFormat sampleFormat)
        {
            return sampleFormat == SampleFormat.PcmInt16; // Oboe supports various formats, but we'll use PCM16
        }

        public bool SupportsChannelCount(uint channelCount)
        {
            return channelCount == 1 || channelCount == 2 || channelCount == 6;
        }

        public bool SupportsDirection(Direction direction)
        {
            return direction == Direction.Output;
        }

        // Oboe data callback class
        private class OboeDataCallback : AudioStreamDataCallback
        {
            private readonly OboeHardwareDeviceDriver _driver;

            public OboeDataCallback(OboeHardwareDeviceDriver driver)
            {
                _driver = driver;
            }

            public override DataCallbackResult OnAudioReady(AudioStream stream, object audioData, int numFrames)
            {
                byte[] mixedData = _driver.GetMixedAudioData(numFrames);
                
                if (mixedData != null && audioData is byte[] targetBuffer)
                {
                    int bytesToCopy = Math.Min(mixedData.Length, targetBuffer.Length);
                    Buffer.BlockCopy(mixedData, 0, targetBuffer, 0, bytesToCopy);
                }
                
                _driver.OnAudioReady();
                
                return DataCallbackResult.Continue;
            }
        }

        // Oboe error callback class
        private class OboeErrorCallback : AudioStreamErrorCallback
        {
            private readonly OboeHardwareDeviceDriver _driver;

            public OboeErrorCallback(OboeHardwareDeviceDriver driver)
            {
                _driver = driver;
            }

            public override bool OnError(AudioStream stream, Result error)
            {
                // Handle error - in a real implementation, you might want to log this
                return true; // Return true to indicate the error was handled
            }
        }
    }
}
#endif
