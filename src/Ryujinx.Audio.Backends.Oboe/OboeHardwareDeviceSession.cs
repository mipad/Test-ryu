#if ANDROID
using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Audio.Backends.Oboe
{
    internal class OboeHardwareDeviceSession : HardwareDeviceSessionOutputBase
    {
        private readonly OboeHardwareDeviceDriver _driver;
        private readonly Queue<OboeAudioBuffer> _queuedBuffers;
        private ulong _playedSampleCount;
        private float _volume;
        private bool _isActive;

        private readonly object _lock = new();

        public bool IsActive
        {
            get
            {
                lock (_lock)
                {
                    return _isActive;
                }
            }
        }

        public OboeHardwareDeviceSession(OboeHardwareDeviceDriver driver, IVirtualMemoryManager memoryManager, SampleFormat requestedSampleFormat, uint requestedSampleRate, uint requestedChannelCount) : base(memoryManager, requestedSampleFormat, requestedSampleRate, requestedChannelCount)
        {
            _driver = driver;
            _queuedBuffers = new Queue<OboeAudioBuffer>();
            _isActive = false;
            _playedSampleCount = 0;
            SetVolume(1f);
        }

        public override void PrepareToClose()
        {
            lock (_lock)
            {
                // Clear any queued buffers
                while (_queuedBuffers.Count > 0)
                {
                    _queuedBuffers.Dequeue();
                }
            }
        }

        public override void QueueBuffer(AudioBuffer buffer)
        {
            lock (_lock)
            {
                OboeAudioBuffer driverBuffer = new()
                {
                    DriverIdentifier = buffer.DataPointer,
                    SampleCount = GetSampleCount(buffer),
                    AudioData = buffer.Data,
                    Size = buffer.Data.Length
                };

                _queuedBuffers.Enqueue(driverBuffer);
            }
        }

        public override void SetVolume(float volume)
        {
            lock (_lock)
            {
                _volume = volume;
                UpdateMasterVolume(_driver.Volume);
            }
        }

        public override float GetVolume()
        {
            lock (_lock)
            {
                return _volume;
            }
        }

        public void UpdateMasterVolume(float newVolume)
        {
            lock (_lock)
            {
                // Volume is applied during mixing, not directly to Oboe
            }
        }

        public override void Start()
        {
            lock (_lock)
            {
                _isActive = true;
            }
        }

        public override void Stop()
        {
            lock (_lock)
            {
                _isActive = false;
            PrepareToClose();
            _playedSampleCount = 0;
            SetVolume(0.0f);
            UpdateMasterVolume(0.0f);
            _driver.GetUpdateRequiredEvent().Set();
        }
       }
       
        public override void UnregisterBuffer(AudioBuffer buffer)
        {
            lock (_lock)
            {
                // Find and remove the buffer from the queue
                var newQueue = new Queue<OboeAudioBuffer>();
                while (_queuedBuffers.Count > 0)
                {
                    OboeAudioBuffer driverBuffer = _queuedBuffers.Dequeue();
                    if (driverBuffer.DriverIdentifier != buffer.DataPointer)
                    {
                        newQueue.Enqueue(driverBuffer);
                    }
                }
                
                while (newQueue.Count > 0)
                {
                    _queuedBuffers.Enqueue(newQueue.Dequeue());
                }
            }
        }

        public override bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            lock (_lock)
            {
                if (!_queuedBuffers.TryPeek(out OboeAudioBuffer driverBuffer))
                {
                    return true;
                }

                return driverBuffer.DriverIdentifier != buffer.DataPointer;
            }
        }

        public override ulong GetPlayedSampleCount()
        {
            lock (_lock)
            {
                return _playedSampleCount;
            }
        }

        public bool NeedsUpdate()
        {
            lock (_lock)
            {
                return _isActive && _queuedBuffers.Count > 0;
            }
        }

        public byte[] GetAudioData(int numFrames)
        {
            lock (_lock)
            {
                if (!_isActive || _queuedBuffers.Count == 0)
                {
                    return null;
                }

                int samplesNeeded = numFrames * 2; // 16-bit stereo
                byte[] audioData = new byte[samplesNeeded * 2]; // 16-bit samples
                int offset = 0;

                while (offset < audioData.Length && _queuedBuffers.Count > 0)
                {
                    OboeAudioBuffer buffer = _queuedBuffers.Peek();
                    int bytesToCopy = Math.Min(buffer.Size, audioData.Length - offset);

                    if (bytesToCopy > 0)
                    {
                        Buffer.BlockCopy(buffer.AudioData, 0, audioData, offset, bytesToCopy);
                        offset += bytesToCopy;
                        buffer.Size -= bytesToCopy;

                        if (buffer.Size <= 0)
                        {
                            _queuedBuffers.Dequeue();
                            _playedSampleCount += buffer.SampleCount;
                        }
                    }
                }

                // Apply volume
                if (_volume != 1.0f)
                {
                    ApplyVolume(audioData, _volume);
                }

                return audioData;
            }
        }

        private void ApplyVolume(byte[] audioData, float volume)
        {
            // Simple volume application for 16-bit audio
            for (int i = 0; i < audioData.Length; i += 2)
            {
                short sample = (short)((audioData[i + 1] << 8) | audioData[i]);
                sample = (short)(sample * volume);
                audioData[i] = (byte)(sample & 0xFF);
                audioData[i + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _driver.Unregister(this))
            {
                lock (_lock)
                {
                    PrepareToClose();
                    Stop();
                }
            }
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
#endif
