using Ryujinx.Audio.Integration;
using Ryujinx.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Audio.Common
{
    /// <summary>
    /// An audio device session.
    /// </summary>
    class AudioDeviceSession : IDisposable
    {
        // ... [existing fields] ...

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

        // 添加格式检查方法
        public bool IsFormatSupported(AudioFormat format)
        {
            return format.Equals(_hardwareFormat);
        }

        // ... [existing methods] ...

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
            int bytesPerSample = _hardwareFormat.BitDepth / 8;
            int samplesPerChannel = (int)(_hardwareFormat.SampleRate * 0.01); // 10ms
            int size = samplesPerChannel * _hardwareFormat.ChannelCount * bytesPerSample;
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
            
            // 实际项目中应使用高质量重采样算法
            // 这里简化实现 - 实际需要完整算法
            return AudioResampler.Resample(data, source, target);
        }
        
        private byte[] ConvertBitDepth(byte[] data, AudioFormat source, AudioFormat target)
        {
            // 实现位深转换
            Logger.Info?.Print(LogClass.Audio, 
                $"Converting bit depth from {source.BitDepth} to {target.BitDepth}");
            
            // 实际项目中应根据源和目标位深进行转换
            return BitDepthConverter.Convert(data, source, target);
        }
        
        private byte[] ConvertChannelLayout(byte[] data, AudioFormat source, AudioFormat target)
        {
            // 实现通道布局转换
            Logger.Info?.Print(LogClass.Audio, 
                $"Converting channel layout from {source.ChannelCount} to {target.ChannelCount}");
            
            // 实际项目中应根据通道配置进行转换
            return ChannelConverter.Convert(data, source, target);
        }

        // 添加获取待处理缓冲区计数方法
        public uint GetPendingBufferCount()
        {
            return (uint)_submitQueue.Count; // 仅未处理缓冲区
        }

        public void Dispose()
        {
            Dispose(true);
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
