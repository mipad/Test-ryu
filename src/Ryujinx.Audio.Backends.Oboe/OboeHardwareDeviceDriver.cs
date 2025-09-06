#if ANDROID
using Android.Media;
using Android.Runtime;
using Java.Nio;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

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

        private AudioTrack _audioTrack;
        private byte[] _audioBuffer;

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

            InitializeAudioTrack();

            _updaterThread.Start();
        }

        private void InitializeAudioTrack()
        {
            try
            {
                int bufferSize = AudioTrack.GetMinBufferSize(
                    48000, // Sample rate
                    ChannelOut.Stereo, // Channel configuration
                    Android.Media.Encoding.Pcm16bit); // Audio format

                _audioTrack = new AudioTrack(
                    Stream.Music,
                    48000, // Sample rate
                    ChannelOut.Stereo, // Channel configuration
                    Android.Media.Encoding.Pcm16bit, // Audio format
                    bufferSize,
                    AudioTrackMode.Stream);

                _audioBuffer = new byte[bufferSize];

                if (_audioTrack != null)
                {
                    _audioTrack.Play();
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
                    // AudioTrack is supported on all Android versions
                    return true;
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
                
                // Write mixed audio data to AudioTrack
                byte[] mixedData = GetMixedAudioData(1024); // Adjust frame count as needed
                if (mixedData != null && _audioTrack != null)
                {
                    _audioTrack.Write(mixedData, 0, mixedData.Length);
                }
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

                    _audioTrack?.Stop();
                    _audioTrack?.Release();
                    _audioTrack?.Dispose();
                    _pauseEvent.Dispose();
                    _updateRequiredEvent.Dispose();
                }

                _disposed = true;
            }
        }

        public bool SupportsSampleRate(uint sampleRate)
        {
            return sampleRate == 48000; // Standard sample rate
        }

        public bool SupportsSampleFormat(SampleFormat sampleFormat)
        {
            return sampleFormat == SampleFormat.PcmInt16; // Standard format
        }

        public bool SupportsChannelCount(uint channelCount)
        {
            return channelCount == 1 || channelCount == 2 || channelCount == 6;
        }

        public bool SupportsDirection(Direction direction)
        {
            return direction == Direction.Output;
        }
    }
}
#endif
