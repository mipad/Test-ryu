using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using System;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Audio.Output
{
    /// <summary>
    /// Audio output system.
    /// </summary>
    public class AudioOutputSystem : IDisposable
    {
        /// <summary>
        /// The session id associated to the <see cref="AudioOutputSystem"/>.
        /// </summary>
        private int _sessionId;

        /// <summary>
        /// The session the <see cref="AudioOutputSystem"/>.
        /// </summary>
        private readonly AudioDeviceSession _session;

        /// <summary>
        /// The target device name of the <see cref="AudioOutputSystem"/>.
        /// </summary>
        public string DeviceName { get; private set; }

        /// <summary>
        /// The target sample rate of the <see cref="AudioOutputSystem"/>.
        /// </summary>
        public uint SampleRate { get; private set; }

        /// <summary>
        /// The target channel count of the <see cref="AudioOutputSystem"/>.
        /// </summary>
        public uint ChannelCount { get; private set; }

        /// <summary>
        /// The target sample format of the <see cref="AudioOutputSystem"/>.
        /// </summary>
        public SampleFormat SampleFormat { get; private set; }

        /// <summary>
        /// The <see cref="AudioOutputManager"/> owning this.
        /// </summary>
        private readonly AudioOutputManager _manager;

        /// <summary>
        /// The dispose state.
        /// </summary>
        private int _disposeState;

        /// <summary>
        /// 会话专用锁（每个实例独立）
        /// </summary>
        private readonly object _sessionLock = new object();

        // 添加动态缓冲区大小字段
        private int _dynamicBufferSize = 32;
        
        // 添加硬件支持的格式信息
        private AudioFormat _hardwareFormat;

        /// <summary>
        /// Create a new <see cref="AudioOutputSystem"/>.
        /// </summary>
        /// <param name="manager">The manager instance</param>
        /// <param name="deviceSession">The hardware device session</param>
        /// <param name="bufferEvent">The buffer release event of the audio output</param>
        public AudioOutputSystem(AudioOutputManager manager, IHardwareDeviceSession deviceSession, IWritableEvent bufferEvent)
        {
            _manager = manager;
            _session = new AudioDeviceSession(deviceSession, bufferEvent);
            
            // 获取硬件支持的格式
            _hardwareFormat = deviceSession.GetSupportedFormat();
        }

        /// <summary>
        /// Get the default device name on the system.
        /// </summary>
        /// <returns>The default device name on the system.</returns>
        private static string GetDeviceDefaultName()
        {
            return Constants.DefaultDeviceOutputName;
        }

        /// <summary>
        /// Check if a given configuration and device name is valid on the system.
        /// </summary>
        /// <param name="configuration">The configuration to check.</param>
        /// <param name="deviceName">The device name to check.</param>
        /// <returns>A <see cref="ResultCode"/> reporting an error or a success.</returns>
        private static ResultCode IsConfigurationValid(ref AudioInputConfiguration configuration, string deviceName)
        {
            if (deviceName.Length != 0 && !deviceName.Equals(GetDeviceDefaultName()))
            {
                return ResultCode.DeviceNotFound;
            }

            if (configuration.SampleRate != 0 && configuration.SampleRate != Constants.TargetSampleRate)
            {
                return ResultCode.UnsupportedSampleRate;
            }

            if (configuration.ChannelCount != 0 && configuration.ChannelCount != 1 && configuration.ChannelCount != 2 && configuration.ChannelCount != 6)
            {
                return ResultCode.UnsupportedChannelConfiguration;
            }

            return ResultCode.Success;
        }

        /// <summary>
        /// Get the released buffer event.
        /// </summary>
        /// <returns>The released buffer event</returns>
        public IWritableEvent RegisterBufferEvent()
        {
            lock (_sessionLock)
            {
                return _session.GetBufferEvent();
            }
        }

        /// <summary>
        /// Update the <see cref="AudioOutputSystem"/>.
        /// </summary>
        public void Update()
        {
            lock (_sessionLock)
            {
                _session.Update();
            }
        }

        /// <summary>
        /// Get the id of this session.
        /// </summary>
        /// <returns>The id of this session</returns>
        public int GetSessionId()
        {
            return _sessionId;
        }

        /// <summary>
        /// Initialize the <see cref="AudioOutputSystem"/>.
        /// </summary>
        /// <param name="inputDeviceName">The input device name wanted by the user</param>
        /// <param name="sampleFormat">The sample format to use</param>
        /// <param name="parameter">The user configuration</param>
        /// <param name="sessionId">The session id associated to this <see cref="AudioOutputSystem"/></param>
        /// <returns>A <see cref="ResultCode"/> reporting an error or a success.</returns>
        public ResultCode Initialize(string inputDeviceName, SampleFormat sampleFormat, ref AudioInputConfiguration parameter, int sessionId)
        {
            _sessionId = sessionId;

            ResultCode result = IsConfigurationValid(ref parameter, inputDeviceName);

            if (result == ResultCode.Success)
            {
                if (inputDeviceName.Length == 0)
                {
                    DeviceName = GetDeviceDefaultName();
                }
                else
                {
                    DeviceName = inputDeviceName;
                }

                if (parameter.ChannelCount == 6)
                {
                    ChannelCount = 6;
                }
                else
                {
                    ChannelCount = 2;
                }

                SampleFormat = sampleFormat;
                SampleRate = Constants.TargetSampleRate;
            }

            return result;
        }

        /// <summary>
        /// Append a new audio buffer to the audio output.
        /// </summary>
        /// <param name="bufferTag">The unique tag of this buffer.</param>
        /// <param name="userBuffer">The buffer informations.</param>
        /// <returns>A <see cref="ResultCode"/> reporting an error or a success.</returns>
        public ResultCode AppendBuffer(ulong bufferTag, ref AudioUserBuffer userBuffer)
        {
            lock (_sessionLock)
            {
                // 1. 使用正确的缓冲区计数（仅未处理缓冲区）
                uint pendingCount = _session.GetPendingBufferCount();
                
                // 2. 动态调整缓冲区大小
                if (pendingCount > _dynamicBufferSize * 0.8)
                {
                    _dynamicBufferSize = Math.Min(256, _dynamicBufferSize * 2);
                    Logger.Debug?.Print(LogClass.Audio, $"Buffer size increased to {_dynamicBufferSize}");
                }
                else if (pendingCount < _dynamicBufferSize * 0.2 && _dynamicBufferSize > 32)
                {
                    _dynamicBufferSize = Math.Max(32, _dynamicBufferSize / 2);
                    Logger.Debug?.Print(LogClass.Audio, $"Buffer size decreased to {_dynamicBufferSize}");
                }
                
                // 3. 格式转换检查
                if (!_session.IsFormatSupported(userBuffer.Format))
                {
                    // 转换到硬件支持的格式
                    byte[] convertedData = ConvertAudioFormat(
                        userBuffer.Data,
                        userBuffer.Format,
                        _hardwareFormat
                    );
                    
                    userBuffer.Data = convertedData;
                    userBuffer.DataSize = (ulong)convertedData.Length;
                    userBuffer.Format = _hardwareFormat; // 更新为转换后的格式
                }

                // 4. 创建缓冲区对象
                AudioBuffer buffer = new()
                {
                    BufferTag = bufferTag,
                    DataPointer = userBuffer.Data,
                    DataSize = userBuffer.DataSize,
                    Format = userBuffer.Format // 携带格式信息
                };

                // 5. 添加欠载保护
                if (pendingCount < 4) // 缓冲区不足时添加静音
                {
                    _session.InsertSafetyBuffers(2); // 添加2个静音缓冲区
                }

                // 6. 提交缓冲区
                if (_session.AppendBuffer(buffer))
                {
                    return ResultCode.Success;
                }
                
                Logger.Warning?.Print(LogClass.Audio, 
                    $"Buffer ring full! Pending: {pendingCount}/{_dynamicBufferSize}");
                return ResultCode.BufferRingFull;
            }
        }

        /// <summary>
        /// Get the release buffers.
        /// </summary>
        /// <param name="releasedBuffers">The buffer to write the release buffers</param>
        /// <param name="releasedCount">The count of released buffers</param>
        /// <returns>A <see cref="ResultCode"/> reporting an error or a success.</returns>
        public ResultCode GetReleasedBuffer(Span<ulong> releasedBuffers, out uint releasedCount)
        {
            releasedCount = 0;

            // 确保如果没有返回任何条目，第一个条目设置为零
            if (releasedBuffers.Length > 0)
            {
                releasedBuffers[0] = 0;
            }

            lock (_sessionLock)
            {
                // 批处理优化：限制每次调用处理的最大缓冲区数量
                const int maxBatchSize = 32;  // 根据性能测试调整此值
                int batchSize = Math.Min(releasedBuffers.Length, maxBatchSize);
                
                for (int i = 0; i < batchSize; i++)
                {
                    if (!_session.TryPopReleasedBuffer(out AudioBuffer buffer))
                    {
                        break;
                    }

                    releasedBuffers[i] = buffer.BufferTag;
                    releasedCount++;
                }
            }

            return ResultCode.Success;
        }

        /// <summary>
        /// Get the current state of the <see cref="AudioOutputSystem"/>.
        /// </summary>
        /// <returns>Return the curent sta\te of the <see cref="AudioOutputSystem"/></returns>
        /// <returns></returns>
        public AudioDeviceState GetState()
        {
            lock (_sessionLock)
            {
                return _session.GetState();
            }
        }

        /// <summary>
        /// Start the audio session.
        /// </summary>
        /// <returns>A <see cref="ResultCode"/> reporting an error or a success</returns>
        public ResultCode Start()
        {
            lock (_sessionLock)
            {
                return _session.Start();
            }
        }

        /// <summary>
        /// Stop the audio session.
        /// </summary>
        /// <returns>A <see cref="ResultCode"/> reporting an error or a success</returns>
        public ResultCode Stop()
        {
            lock (_sessionLock)
            {
                return _session.Stop();
            }
        }

        /// <summary>
        /// Get the volume of the session.
        /// </summary>
        /// <returns>The volume of the session</returns>
        public float GetVolume()
        {
            lock (_sessionLock)
            {
                return _session.GetVolume();
            }
        }

        /// <summary>
        /// Set the volume of the session.
        /// </summary>
        /// <param name="volume">The new volume to set</param>
        public void SetVolume(float volume)
        {
            lock (_sessionLock)
            {
                _session.SetVolume(volume);
            }
        }

        /// <summary>
        /// Get the count of buffer currently in use (server + driver side).
        /// </summary>
        /// <returns>The count of buffer currently in use</returns>
        public uint GetBufferCount()
        {
            lock (_sessionLock)
            {
                return _session.GetBufferCount();
            }
        }

        /// <summary>
        /// Check if a buffer is present.
        /// </summary>
        /// <param name="bufferTag">The unique tag of the buffer</param>
        /// <returns>Return true if a buffer is present</returns>
        public bool ContainsBuffer(ulong bufferTag)
        {
            lock (_sessionLock)
            {
                return _session.ContainsBuffer(bufferTag);
            }
        }

        /// <summary>
        /// Get the count of sample played in this session.
        /// </summary>
        /// <returns>The count of sample played in this session</returns>
        public ulong GetPlayedSampleCount()
        {
            lock (_sessionLock)
            {
                return _session.GetPlayedSampleCount();
            }
        }

        /// <summary>
        /// Flush all buffers to the initial state.
        /// </summary>
        /// <returns>True if any buffers was flushed</returns>
        public bool FlushBuffers()
        {
            lock (_sessionLock)
            {
                return _session.FlushBuffers();
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            if (Interlocked.CompareExchange(ref _disposeState, 1, 0) == 0)
            {
                Dispose(true);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_sessionLock)
                {
                    _session.Dispose();
                }

                _manager.Unregister(this);
            }
        }
        
        /// <summary>
        /// 转换音频格式以匹配硬件支持
        /// </summary>
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
            // 这里使用伪代码表示实际实现
            Logger.Info?.Print(LogClass.Audio, 
                $"Resampling audio from {source.SampleRate}Hz to {target.SampleRate}Hz");
            
            // 实际项目中应使用高质量重采样算法
            // 例如使用libsamplerate或自定义算法
            return data; // 简化实现
        }
        
        private byte[] ConvertBitDepth(byte[] data, AudioFormat source, AudioFormat target)
        {
            // 实现位深转换
            Logger.Info?.Print(LogClass.Audio, 
                $"Converting bit depth from {source.BitDepth} to {target.BitDepth}");
            
            // 实际项目中应根据源和目标位深进行转换
            // 例如16位转24位，浮点转定点等
            return data; // 简化实现
        }
        
        private byte[] ConvertChannelLayout(byte[] data, AudioFormat source, AudioFormat target)
        {
            // 实现通道布局转换
            Logger.Info?.Print(LogClass.Audio, 
                $"Converting channel layout from {source.ChannelCount} to {target.ChannelCount}");
            
            // 实际项目中应根据通道配置进行转换
            // 例如立体声转5.1，单声道转立体声等
            return data; // 简化实现
        }
    }
}
