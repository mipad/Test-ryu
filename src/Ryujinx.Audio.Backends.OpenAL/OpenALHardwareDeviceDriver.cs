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

// Oboe 相关的命名空间 - 根据 Yuzu 的实现方式
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
    public class OboeHardwareDeviceDriver : IHardwareDeviceDriver, IDisposable
    {
        private readonly ManualResetEvent _updateRequiredEvent;
        private readonly ManualResetEvent _pauseEvent;
        private readonly ConcurrentDictionary<OboeHardwareDeviceSession, byte> _sessions;
        private bool _disposed;
        private bool _stillRunning;
        private readonly Thread _updaterThread;

        private float _volume;
        private readonly object _volumeLock = new();

        private OboeStream _audioStream;
        private OboeDataCallbackImpl _dataCallback;
        private OboeErrorCallbackImpl _errorCallback;

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
                _dataCallback = new OboeDataCallbackImpl(this);
                _errorCallback = new OboeErrorCallbackImpl(this);
                
                var builder = new OboeStreamBuilder()
                    .SetDirection(OboeDirection.Output)
                    .SetPerformanceMode(OboePerformanceMode.LowLatency)
                    .SetSharingMode(OboeSharingMode.Shared)
                    .SetFormat(OboeFormat.I16)
                    .SetChannelCount(2) // Stereo
                    .SetSampleRate(48000) // Standard sample rate
                    .SetDataCallback(_dataCallback)
                    .SetErrorCallback(_errorCallback);

                var result = builder.OpenStream(out _audioStream);
                
                if (result == OboeResult.Ok && _audioStream != null)
                {
                    _audioStream.RequestStart();
                }
                else
                {
                    // Fallback to a more compatible configuration
                    builder.SetPerformanceMode(OboePerformanceMode.None);
                    builder.SetSharingMode(OboeSharingMode.Shared);
                    result = builder.OpenStream(out _audioStream);
                    
                    if (result == OboeResult.Ok && _audioStream != null)
                    {
                        _audioStream.RequestStart();
                    }
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
                sampleRate = 48000; // Standard sample rate
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

                    foreach (OboeHardwareDeviceSession session in _sessions.Keys)
                    {
                        session.Dispose();
                    }

                    _audioStream?.Close();
                    _audioStream?.Dispose();
                    _pauseEvent.Dispose();
                    _updateRequiredEvent.Dispose();
                }

                _disposed = true;
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
                try
                {
                    if (audioData is ByteBuffer buffer)
                    {
                        byte[] mixedData = _driver.GetMixedAudioData(numFrames);
                        
                        if (mixedData != null)
                        {
                            buffer.Rewind();
                            buffer.Put(mixedData);
                        }
                    }
                    
                    _driver.OnAudioReady();
                    
                    return OboeResult.Ok;
                }
                catch (Exception)
                {
                    return OboeResult.ErrorInternal;
                }
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
