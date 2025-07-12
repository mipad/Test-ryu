using Ryujinx.Audio.Integration;
using Ryujinx.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Audio.Common
{
    // 添加 AudioFormat 定义
    public struct AudioFormat
    {
        public uint SampleRate { get; set; }
        public uint BitDepth { get; set; }
        public uint ChannelCount { get; set; }
        
        public bool Equals(AudioFormat other)
        {
            return SampleRate == other.SampleRate &&
                   BitDepth == other.BitDepth &&
                   ChannelCount == other.ChannelCount;
        }
    }

    /// <summary>
    /// An audio device session.
    /// </summary>
    class AudioDeviceSession : IDisposable
    {
        /// <summary>
        /// The volume of the <see cref="AudioDeviceSession"/>.
        /// </summary>
        private float _volume;

        /// <summary>
        /// The state of the <see cref="AudioDeviceSession"/>.
        /// </summary>
        private AudioDeviceState _state;

        /// <summary>
        /// Array of all buffers currently used or released.
        /// </summary>
        private readonly AudioBuffer[] _buffers;

        /// <summary>
        /// The server index inside <see cref="_buffers"/> (appended but not queued to device driver).
        /// </summary>
        private uint _serverBufferIndex;

        /// <summary>
        /// The hardware index inside <see cref="_buffers"/> (queued to device driver).
        /// </summary>
        private uint _hardwareBufferIndex;

        /// <summary>
        /// The released index inside <see cref="_buffers"/> (released by the device driver).
        /// </summary>
        private uint _releasedBufferIndex;

        /// <summary>
        /// The count of buffer appended (server side).
        /// </summary>
        private uint _bufferAppendedCount;

        /// <summary>
        /// The count of buffer registered (driver side).
        /// </summary>
        private uint _bufferRegisteredCount;

        /// <summary>
        /// The count of buffer released (released by the driver side).
        /// </summary>
        private uint _bufferReleasedCount;

        /// <summary>
        /// The released buffer event.
        /// </summary>
        private readonly IWritableEvent _bufferEvent;

        /// <summary>
        /// The session on the device driver.
        /// </summary>
        private readonly IHardwareDeviceSession _hardwareDeviceSession;

        /// <summary>
        /// Max number of buffers that can be registered to the device driver at a time.
        /// </summary>
        private readonly uint _bufferRegisteredLimit;

        private readonly ConcurrentQueue<AudioBuffer> _submitQueue = new();
        private readonly BlockingCollection<AudioBuffer> _releaseQueue = new();
        private readonly CancellationTokenSource _cts = new();
        private Thread _processingThread;
        
        // 添加硬件支持的格式信息
        private AudioFormat _hardwareFormat;
        
        // 添加静音缓冲区缓存
        private AudioBuffer _cachedSilenceBuffer;

        /// <summary>
        /// Create a new <see cref="AudioDeviceSession"/>.
        /// </summary>
        /// <param name="deviceSession">The device driver session associated</param>
        /// <param name="bufferEvent">The release buffer event</param>
        /// <param name="bufferRegisteredLimit">The max number of buffers that can be registered to the device driver at a time</param>
        public AudioDeviceSession(IHardwareDeviceSession deviceSession, IWritableEvent bufferEvent, uint bufferRegisteredLimit = 4)
        {
            _bufferEvent = bufferEvent;
            _hardwareDeviceSession = deviceSession;
            _bufferRegisteredLimit = bufferRegisteredLimit;
            
            // 获取硬件支持的格式
            _hardwareFormat = deviceSession.GetSupportedFormat();

            _buffers = new AudioBuffer[Constants.AudioDeviceBufferCountMax];
            _serverBufferIndex = 0;
            _hardwareBufferIndex = 0;
            _releasedBufferIndex = 0;

            _bufferAppendedCount = 0;
            _bufferRegisteredCount = 0;
            _bufferReleasedCount = 0;
            _volume = deviceSession.GetVolume();
            _state = AudioDeviceState.Stopped;
            
            // 启动处理线程
            _processingThread = new Thread(ProcessBuffers)
            {
                Priority = ThreadPriority.AboveNormal,
                Name = $"AudioSession_{deviceSession.GetHashCode()}"
            };
            _processingThread.Start();
        }

        // 添加缓冲区处理方法
        private void ProcessBuffers()
        {
            const int MaxBatchSize = 16;
            var batch = new List<AudioBuffer>(MaxBatchSize);
            
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // 批量获取待提交缓冲区
                    while (batch.Count < MaxBatchSize && _submitQueue.TryDequeue(out var buffer))
                    {
                        // 格式转换检查
                        if (!IsFormatSupported(buffer.Format))
                        {
                            // 转换到硬件支持的格式
                            byte[] convertedData = ConvertAudioFormat(
                                buffer.DataPointer,
                                buffer.Format,
                                _hardwareFormat
                            );
                            
                            // 创建新缓冲区对象
                            AudioBuffer convertedBuffer = new()
                            {
                                BufferTag = buffer.BufferTag,
                                DataPointer = convertedData,
                                DataSize = (ulong)convertedData.Length,
                                Format = _hardwareFormat
                            };
                            
                            batch.Add(convertedBuffer);
                        }
                        else
                        {
                            batch.Add(buffer);
                        }
                    }
                    
                    // 批量提交
                    if (batch.Count > 0)
                    {
                        _hardwareDeviceSession.QueueBuffers(batch);
                        batch.Clear();
                    }
                    
                    Thread.Sleep(1); // 避免空转
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Audio processing error: {ex}");
                }
            }
        }

        /// <summary>
        /// Get the released buffer event.
        /// </summary>
        /// <returns>The released buffer event</returns>
        public IWritableEvent GetBufferEvent()
        {
            return _bufferEvent;
        }

        /// <summary>
        /// Get the state of the session.
        /// </summary>
        /// <returns>The state of the session</returns>
        public AudioDeviceState GetState()
        {
            Debug.Assert(_state == AudioDeviceState.Started || _state == AudioDeviceState.Stopped);

            return _state;
        }

        /// <summary>
        /// Get the total buffer count (server + driver + released).
        /// </summary>
        /// <returns>Return the total buffer count</returns>
        private uint GetTotalBufferCount()
        {
            uint bufferCount = _bufferAppendedCount + _bufferRegisteredCount + _bufferReleasedCount;

            Debug.Assert(bufferCount <= Constants.AudioDeviceBufferCountMax);

            return bufferCount;
        }

        /// <summary>
        /// Register a new <see cref="AudioBuffer"/> on the server side.
        /// </summary>
        /// <param name="buffer">The <see cref="AudioBuffer"/> to register</param>
        /// <returns>True if the operation succeeded</returns>
        private bool RegisterBuffer(AudioBuffer buffer)
        {
            if (GetTotalBufferCount() == Constants.AudioDeviceBufferCountMax)
            {
                return false;
            }

            _buffers[_serverBufferIndex] = buffer;
            _serverBufferIndex = (_serverBufferIndex + 1) % Constants.AudioDeviceBufferCountMax;
            _bufferAppendedCount++;

            return true;
        }

        /// <summary>
        /// Flush server buffers to hardware.
        /// </summary>
        private void FlushToHardware()
        {
            uint bufferToFlushCount = Math.Min(Math.Min(_bufferAppendedCount, 4), _bufferRegisteredLimit - _bufferRegisteredCount);

            AudioBuffer[] buffersToFlush = new AudioBuffer[bufferToFlushCount];

            uint hardwareBufferIndex = _hardwareBufferIndex;

            for (int i = 0; i < buffersToFlush.Length; i++)
            {
                buffersToFlush[i] = _buffers[hardwareBufferIndex];

                _bufferAppendedCount--;
                _bufferRegisteredCount++;

                hardwareBufferIndex = (hardwareBufferIndex + 1) % Constants.AudioDeviceBufferCountMax;
            }

            _hardwareBufferIndex = hardwareBufferIndex;

            for (int i = 0; i < buffersToFlush.Length; i++)
            {
                _hardwareDeviceSession.QueueBuffer(buffersToFlush[i]);
            }
        }

        /// <summary>
        /// Get the current index of the <see cref="AudioBuffer"/> playing on the driver side.
        /// </summary>
        /// <param name="playingIndex">The output index of the <see cref="AudioBuffer"/> playing on the driver side</param>
        /// <returns>True if any buffer is playing</returns>
        private bool TryGetPlayingBufferIndex(out uint playingIndex)
        {
            if (_bufferRegisteredCount > 0)
            {
                playingIndex = (_hardwareBufferIndex - _bufferRegisteredCount) % Constants.AudioDeviceBufferCountMax;

                return true;
            }

            playingIndex = 0;

            return false;
        }

        /// <summary>
        /// Try to pop the <see cref="AudioBuffer"/> playing on the driver side.
        /// </summary>
        /// <param name="buffer">The output <see cref="AudioBuffer"/> playing on the driver side</param>
        /// <returns>True if any buffer is playing</returns>
        private bool TryPopPlayingBuffer(out AudioBuffer buffer)
        {
            if (_bufferRegisteredCount > 0)
            {
                uint bufferIndex = (_hardwareBufferIndex - _bufferRegisteredCount) % Constants.AudioDeviceBufferCountMax;

                buffer = _buffers[bufferIndex];

                _buffers[bufferIndex] = null;

                _bufferRegisteredCount--;

                return true;
            }

            buffer = null;

            return false;
        }

        /// <summary>
        /// Try to pop a <see cref="AudioBuffer"/> released by the driver side.
        /// </summary>
        /// <param name="buffer">The output <see cref="AudioBuffer"/> released by the driver side</param>
        /// <returns>True if any buffer has been released</returns>
        public bool TryPopReleasedBuffer(out AudioBuffer buffer)
        {
            return _releaseQueue.TryTake(out buffer, 0);
        }

        /// <summary>
        /// Release a <see cref="AudioBuffer"/>.
        /// </summary>
        /// <param name="buffer">The <see cref="AudioBuffer"/> to release</param>
        private void ReleaseBuffer(AudioBuffer buffer)
        {
            buffer.PlayedTimestamp = (ulong)PerformanceCounter.ElapsedNanoseconds;

            _bufferRegisteredCount--;
            _bufferReleasedCount++;

            _releasedBufferIndex = (_releasedBufferIndex + 1) % Constants.AudioDeviceBufferCountMax;
        }

        /// <summary>
        /// Update the released buffers.
        /// </summary>
        /// <param name="updateForStop">True if the session is currently stopping</param>
        private void UpdateReleaseBuffers(bool updateForStop = false)
        {
            bool wasAnyBuffersReleased = false;

            while (TryGetPlayingBufferIndex(out uint playingIndex))
            {
                if (!updateForStop && !_hardwareDeviceSession.WasBufferFullyConsumed(_buffers[playingIndex]))
                {
                    break;
                }

                if (updateForStop)
                {
                    _hardwareDeviceSession.UnregisterBuffer(_buffers[playingIndex]);
                }

                ReleaseBuffer(_buffers[playingIndex]);

                wasAnyBuffersReleased = true;
            }

            if (wasAnyBuffersReleased)
            {
                _bufferEvent.Signal();
            }
        }

        /// <summary>
        /// Append a new <see cref="AudioBuffer"/>.
        /// </summary>
        /// <param name="buffer">The <see cref="AudioBuffer"/> to append</param>
        /// <returns>True if the buffer was appended</returns>
        public bool AppendBuffer(AudioBuffer buffer)
        {
            _submitQueue.Enqueue(buffer);
            return true;
        }

        public static bool AppendUacBuffer(AudioBuffer buffer, uint handle)
        {
            // NOTE: On hardware, there is another RegisterBuffer method taking a handle.
            // This variant of the call always return false (stubbed?) as a result this logic will never succeed.

            return false;
        }

        /// <summary>
        /// Start the audio session.
        /// </summary>
        /// <returns>A <see cref="ResultCode"/> reporting an error or a success</returns>
        public ResultCode Start()
        {
            if (_state == AudioDeviceState.Started)
            {
                return ResultCode.OperationFailed;
            }

            _hardwareDeviceSession.Start();

            _state = AudioDeviceState.Started;

            FlushToHardware();

            _hardwareDeviceSession.SetVolume(_volume);

            return ResultCode.Success;
        }

        /// <summary>
        /// Stop the audio session.
        /// </summary>
        /// <returns>A <see cref="ResultCode"/> reporting an error or a success</returns>
        public ResultCode Stop()
        {
            if (_state == AudioDeviceState.Started)
            {
                _hardwareDeviceSession.Stop();

                UpdateReleaseBuffers(true);

                _state = AudioDeviceState.Stopped;
            }

            return ResultCode.Success;
        }

        /// <summary>
        /// Get the volume of the session.
        /// </summary>
        /// <returns>The volume of the session</returns>
        public float GetVolume()
        {
            return _hardwareDeviceSession.GetVolume();
        }

        /// <summary>
        /// Set the volume of the session.
        /// </summary>
        /// <param name="volume">The new volume to set</param>
        public void SetVolume(float volume)
        {
            _volume = volume;

            if (_state == AudioDeviceState.Started)
            {
                _hardwareDeviceSession.SetVolume(volume);
            }
        }

        /// <summary>
        /// Get the count of buffer currently in use (server + driver side).
        /// </summary>
        /// <returns>The count of buffer currently in use</returns>
        public uint GetBufferCount()
        {
            return _bufferAppendedCount + _bufferRegisteredCount;
        }

        /// <summary>
        /// Check if a buffer is present.
        /// </summary>
        /// <param name="bufferTag">The unique tag of the buffer</param>
        /// <returns>Return true if a buffer is present</returns>
        public bool ContainsBuffer(ulong bufferTag)
        {
            uint bufferIndex = (_releasedBufferIndex - _bufferReleasedCount) % Constants.AudioDeviceBufferCountMax;

            uint totalBufferCount = GetTotalBufferCount();

            for (int i = 0; i < totalBufferCount; i++)
            {
                if (_buffers[bufferIndex] != null && _buffers[bufferIndex].BufferTag == bufferTag)
                {
                    return true;
                }

                bufferIndex = (bufferIndex + 1) % Constants.AudioDeviceBufferCountMax;
            }

            return false;
        }

        /// <summary>
        /// Get the count of sample played in this session.
        /// </summary>
        /// <returns>The count of sample played in this session</returns>
        public ulong GetPlayedSampleCount()
        {
            if (_state == AudioDeviceState.Stopped)
            {
                return 0;
            }

            return _hardwareDeviceSession.GetPlayedSampleCount();
        }

        /// <summary>
        /// Flush all buffers to the initial state.
        /// </summary>
        /// <returns>True if any buffer was flushed</returns>
        public bool FlushBuffers()
        {
            if (_state == AudioDeviceState.Stopped)
            {
                return false;
            }

            uint bufferCount = GetBufferCount();

            while (TryPopReleasedBuffer(out AudioBuffer buffer))
            {
                _hardwareDeviceSession.UnregisterBuffer(buffer);
            }

            while (TryPopPlayingBuffer(out AudioBuffer buffer))
            {
                _hardwareDeviceSession.UnregisterBuffer(buffer);
            }

            if (_bufferRegisteredCount == 0 || (_bufferReleasedCount + _bufferAppendedCount) > Constants.AudioDeviceBufferCountMax)
            {
                return false;
            }

            _bufferReleasedCount += _bufferAppendedCount;
            _releasedBufferIndex = (_releasedBufferIndex + _bufferAppendedCount) % Constants.AudioDeviceBufferCountMax;
            _bufferAppendedCount = 0;
            _hardwareBufferIndex = _serverBufferIndex;

            if (bufferCount > 0)
            {
                _bufferEvent.Signal();
            }

            return true;
        }

        /// <summary>
        /// Update the session.
        /// </summary>
        public void Update()
        {
            if (_state != AudioDeviceState.Started) return;
            
            // 批量获取已释放缓冲区
            var released = _hardwareDeviceSession.GetReleasedBuffers(16);
            foreach (var buffer in released)
            {
                buffer.PlayedTimestamp = (ulong)PerformanceCounter.ElapsedNanoseconds;
                _releaseQueue.Add(buffer);
                _bufferEvent.Signal();
            }
            
            // 欠载保护
            if (_submitQueue.Count < 8)
            {
                InsertSafetyBuffers(2); // 添加2个静音缓冲区
            }
        }

        // 添加格式检查方法
        public bool IsFormatSupported(AudioFormat format)
        {
            return format.Equals(_hardwareFormat);
        }

        // 添加欠载保护方法
        public void InsertSafetyBuffers(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _submitQueue.Enqueue(CreateSilenceBuffer());
            }
        }

        private AudioBuffer CreateSilenceBuffer()
        {
            // 复用缓存的静音缓冲区
            if (_cachedSilenceBuffer != null)
            {
                return _cachedSilenceBuffer;
            }
            
            // 创建静音缓冲区
            int bytesPerSample = (int)_hardwareFormat.BitDepth / 8;
            int samplesPerChannel = (int)(_hardwareFormat.SampleRate * 0.01); // 10ms
            int size = samplesPerChannel * (int)_hardwareFormat.ChannelCount * bytesPerSample;
            byte[] silenceData = new byte[size]; // 自动初始化为0
            
            _cachedSilenceBuffer = new AudioBuffer
            {
                BufferTag = 0, // 特殊标记表示静音缓冲区
                DataPointer = silenceData,
                DataSize = (ulong)size,
                Format = _hardwareFormat
            };
            
            return _cachedSilenceBuffer;
        }

        // 添加格式转换方法
        private byte[] ConvertAudioFormat(byte[] data, AudioFormat source, AudioFormat target)
        {
            // 如果格式相同则无需转换
            if (source.Equals(target))
            {
                return data;
            }
            
            // 实现采样率转换
            if (source.SampleRate != target.SampleRate)
            {
                data = Resample(data, source, target);
            }
            
            // 实现位深转换
            if (source.BitDepth != target.BitDepth)
            {
                data = ConvertBitDepth(data, source, target);
            }
            
            // 实现通道数转换
            if (source.ChannelCount != target.ChannelCount)
            {
                data = ConvertChannelLayout(data, source, target);
            }
            
            return data;
        }
        
        private byte[] Resample(byte[] data, AudioFormat source, AudioFormat target)
        {
            // 实现采样率转换算法
            Logger.Info?.Print(LogClass.Audio, 
                $"Resampling audio from {source.SampleRate}Hz to {target.SampleRate}Hz");
            
            // 简化实现 - 实际需要完整算法
            return data;
        }
        
        private byte[] ConvertBitDepth(byte[] data, AudioFormat source, AudioFormat target)
        {
            // 实现位深转换
            Logger.Info?.Print(LogClass.Audio, 
                $"Converting bit depth from {source.BitDepth} to {target.BitDepth}");
            
            // 简化实现
            return data;
        }
        
        private byte[] ConvertChannelLayout(byte[] data, AudioFormat source, AudioFormat target)
        {
            // 实现通道布局转换
            Logger.Info?.Print(LogClass.Audio, 
                $"Converting channel layout from {source.ChannelCount} to {target.ChannelCount}");
            
            // 简化实现
            return data;
        }

        // 添加获取待处理缓冲区计数方法
        public uint GetPendingBufferCount()
        {
            return (uint)_submitQueue.Count; // 仅未处理缓冲区
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 停止处理线程
                _cts.Cancel();
                if (!_processingThread.Join(50))
                {
                    Logger.Warning?.Print(LogClass.Audio, "Audio processing thread did not exit gracefully");
                }
                
                // 清理硬件会话
                _hardwareDeviceSession.PrepareToClose();

                // 清空队列
                while (_submitQueue.TryDequeue(out _)) { }
                while (_releaseQueue.TryTake(out _)) { }

                while (TryPopReleasedBuffer(out AudioBuffer buffer))
                {
                    _hardwareDeviceSession.UnregisterBuffer(buffer);
                }

                while (TryPopPlayingBuffer(out AudioBuffer buffer))
                {
                    _hardwareDeviceSession.UnregisterBuffer(buffer);
                }

                _hardwareDeviceSession.Dispose();

                _bufferEvent.Signal();
            }
        }
    }
}
