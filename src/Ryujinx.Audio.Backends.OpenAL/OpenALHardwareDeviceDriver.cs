#if ANDROID
using Android.Runtime;
using Java.Nio;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

// Oboe 相关的命名空间
using OboeStream = Com.Google.Android.Oboe.AudioStream;
using OboeStreamBuilder = Com.Google.Android.Oboe.AudioStreamBuilder;
using OboeDirection = Com.Google.Android.Oboe.Direction;
using OboePerformanceMode = Com.Google.Android.Oboe.PerformanceMode;
using OboeSharingMode = Com.Google.Android.Oboe.SharingMode;
using OboeFormat = Com.Google.Android.Oboe.Format;
using OboeDataCallback = Com.Google.Android.Oboe.AudioStreamDataCallback;
using OboeErrorCallback = Com.Google.Android.Oboe.AudioStreamErrorCallback;
using OboeResult = Com.Google.Android.Oboe.Result;

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

        private OboeStream _audioStream;

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
                var builder = new OboeStreamBuilder()
                    .SetDirection(OboeDirection.Output)
                    .SetPerformanceMode(OboePerformanceMode.LowLatency)
                    .SetSharingMode(OboeSharingMode.Exclusive)
                    .SetFormat(OboeFormat.I16)
                    .SetChannelCount(2) // Stereo
                    .SetSampleRate((int)Constants.TargetSampleRate)
                    .SetDataCallback(new OboeDataCallbackImpl(this))
                    .SetErrorCallback(new OboeErrorCallbackImpl(this));

                _audioStream = builder.Build();
                
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

        // Oboe data callback implementation
        private class OboeDataCallbackImpl : OboeDataCallback
        {
            private readonly OboeHardwareDeviceDriver _driver;

            public OboeDataCallbackImpl(OboeHardwareDeviceDriver driver)
            {
                _driver = driver;
            }

            public override OboeResult OnAudioReady(OboeStream stream, Java.Lang.Object audioData, int numFrames)
            {
                byte[] mixedData = _driver.GetMixedAudioData(numFrames);
                
                if (mixedData != null && audioData is ByteBuffer buffer)
                {
                    buffer.Rewind();
                    buffer.Put(mixedData);
                }
                
                _driver.OnAudioReady();
                
                return OboeResult.Ok;
            }
        }

        // Oboe error callback implementation
        private class OboeErrorCallbackImpl : OboeErrorCallback
        {
            private readonly OboeHardwareDeviceDriver _driver;

            public OboeErrorCallbackImpl(OboeHardwareDeviceDriver driver)
            {
                _driver = driver;
            }

            public override bool OnError(OboeStream stream, OboeResult error)
            {
                // Handle error - in a real implementation, you might want to log this
                return true; // Return true to indicate the error was handled
            }
        }
    }
}
#endif
