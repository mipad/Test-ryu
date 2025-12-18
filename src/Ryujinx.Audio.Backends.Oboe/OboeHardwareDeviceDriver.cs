// OboeHardwareDeviceDriver.cs (合并版本 - 专注稳定性)
#if ANDROID
using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Audio.Backends.Oboe
{
    public class OboeHardwareDeviceDriver : IHardwareDeviceDriver, IDisposable
    {
        // ========== P/Invoke 声明 - 多实例接口 ==========
        [DllImport("libryujinxjni", EntryPoint = "createOboeRenderer")]
        private static extern IntPtr createOboeRenderer();

        [DllImport("libryujinxjni", EntryPoint = "destroyOboeRenderer")]
        private static extern void destroyOboeRenderer(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "initOboeRenderer")]
        private static extern bool initOboeRenderer(IntPtr renderer, int sample_rate, int channel_count, int sample_format);

        [DllImport("libryujinxjni", EntryPoint = "shutdownOboeRenderer")]
        private static extern void shutdownOboeRenderer(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "writeOboeRendererAudioRaw")]
        private static extern bool writeOboeRendererAudioRaw(IntPtr renderer, byte[] audioData, int num_frames, int sample_format);

        [DllImport("libryujinxjni", EntryPoint = "setOboeRendererVolume")]
        private static extern void setOboeRendererVolume(IntPtr renderer, float volume);

        [DllImport("libryujinxjni", EntryPoint = "isOboeRendererInitialized")]
        private static extern bool isOboeRendererInitialized(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "isOboeRendererPlaying")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool isOboeRendererPlaying(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "getOboeRendererBufferedFrames")]
        private static extern int getOboeRendererBufferedFrames(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "resetOboeRenderer")]
        private static extern void resetOboeRenderer(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "setOboeRendererPerformanceHint")]
        private static extern void setOboeRendererPerformanceHint(IntPtr renderer, bool enabled);

        [DllImport("libryujinxjni", EntryPoint = "setOboeRendererErrorCallback")]
        private static extern void setOboeRendererErrorCallback(IntPtr renderer);

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions = new();
        private Thread _updateThread;
        private bool _stillRunning = true;

        public float Volume { get; set; } = 1.0f;

        // ========== 构造与生命周期 ==========
        public OboeHardwareDeviceDriver()
        {
            StartUpdateThread();
        }

        private void StartUpdateThread()
        {
            _updateThread = new Thread(() =>
            {
                while (_stillRunning)
                {
                    try
                    {
                        Thread.Sleep(5); // 5ms更新频率，提高响应性
                        
                        foreach (var session in _sessions.Keys)
                        {
                            if (session.IsActive)
                            {
                                int bufferedFrames = session.GetBufferedFrames();
                                session.UpdatePlaybackStatus(bufferedFrames);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Update thread error: {ex.Message}");
                        // 线程继续运行，不因异常而退出
                        Thread.Sleep(100);
                    }
                }
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal  // 提高优先级
            };
            _updateThread.Start();
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
                    
                    // 停止所有会话
                    foreach (var session in _sessions.Keys)
                    {
                        try
                        {
                            session.PrepareToClose();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning?.Print(LogClass.Audio, $"Error stopping session: {ex.Message}");
                        }
                    }
                    
                    _sessions.Clear();
                    
                    // 等待更新线程结束
                    if (_updateThread != null && _updateThread.IsAlive)
                    {
                        if (!_updateThread.Join(TimeSpan.FromSeconds(2)))
                        {
                            Logger.Warning?.Print(LogClass.Audio, "Update thread did not exit cleanly");
                        }
                    }
                    
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                }
                _disposed = true;
            }
        }

        // ========== 设备能力查询 ==========
        public bool SupportsSampleRate(uint sampleRate) => true;

        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat == SampleFormat.PcmInt16 || 
            sampleFormat == SampleFormat.PcmInt32 ||
            sampleFormat == SampleFormat.PcmFloat;

        public bool SupportsChannelCount(uint channelCount) =>
            channelCount is 1 or 2 or 6;

        public bool SupportsDirection(IHardwareDeviceDriver.Direction direction) =>
            direction == IHardwareDeviceDriver.Direction.Output;

        // ========== 事件 ==========
        public ManualResetEvent GetPauseEvent() => _pauseEvent;
        public ManualResetEvent GetUpdateRequiredEvent() => _updateRequiredEvent;

        // ========== 打开设备会话 ==========
        public IHardwareDeviceSession OpenDeviceSession(
            IHardwareDeviceDriver.Direction direction,
            IVirtualMemoryManager memoryManager,
            SampleFormat sampleFormat,
            uint sampleRate,
            uint channelCount)
        {
            if (direction != IHardwareDeviceDriver.Direction.Output)
                throw new ArgumentException($"Unsupported direction: {direction}");

            if (!SupportsChannelCount(channelCount))
                throw new ArgumentException($"Unsupported channel count: {channelCount}");

            if (!SupportsSampleFormat(sampleFormat))
                throw new ArgumentException($"Unsupported sample format: {sampleFormat}");

            if (sampleRate == 0)
            {
                sampleRate = 48000;
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            
            return session;
        }

        private bool Unregister(OboeAudioSession session) 
        {
            return _sessions.TryRemove(session, out _);
        }

        // ========== 音频会话类 ==========
        private class OboeAudioSession : HardwareDeviceSessionOutputBase
        {
            private readonly OboeHardwareDeviceDriver _driver;
            private readonly ConcurrentQueue<OboeAudioBuffer> _queuedBuffers = new();
            private ulong _totalWrittenSamples;
            private ulong _totalPlayedSamples;
            private bool _active;
            private float _volume;
            private readonly int _channelCount;
            private readonly uint _sampleRate;
            private readonly SampleFormat _sampleFormat;
            private readonly IntPtr _rendererPtr;
            private int _consecutiveFailures;
            private DateTime _lastSuccessTime;
            private bool _needsRecovery;

            public bool IsActive => _active;

            public OboeAudioSession(
                OboeHardwareDeviceDriver driver,
                IVirtualMemoryManager memoryManager,
                SampleFormat sampleFormat,
                uint sampleRate,
                uint channelCount) 
                : base(memoryManager, sampleFormat, sampleRate, channelCount)
            {
                _driver = driver;
                _channelCount = (int)channelCount;
                _sampleRate = sampleRate;
                _sampleFormat = sampleFormat;
                _volume = 1.0f;
                _consecutiveFailures = 0;
                _lastSuccessTime = DateTime.Now;
                _needsRecovery = false;
                
                // 创建独立的渲染器实例
                _rendererPtr = createOboeRenderer();
                if (_rendererPtr == IntPtr.Zero)
                {
                    throw new Exception("Failed to create Oboe audio renderer");
                }

                // 初始化渲染器
                int formatValue = SampleFormatToInt(sampleFormat);
                if (!initOboeRenderer(_rendererPtr, (int)sampleRate, (int)channelCount, formatValue))
                {
                    destroyOboeRenderer(_rendererPtr);
                    throw new Exception("Failed to initialize Oboe audio renderer");
                }

                setOboeRendererVolume(_rendererPtr, _volume);
                
                // 启用性能提示
                setOboeRendererPerformanceHint(_rendererPtr, true);
            }

            public int GetBufferedFrames()
            {
                if (_rendererPtr == IntPtr.Zero) return 0;
                
                try
                {
                    return getOboeRendererBufferedFrames(_rendererPtr);
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Audio, $"Failed to get buffered frames: {ex.Message}");
                    return 0;
                }
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                try
                {
                    // 检测是否需要恢复
                    CheckAndRecover();
                    
                    // 检查是否长时间没有音频活动
                    if (bufferedFrames == 0 && _queuedBuffers.Count > 0 && IsActive)
                    {
                        var now = DateTime.Now;
                        if ((now - _lastSuccessTime).TotalSeconds > 2.0) // 2秒没有播放
                        {
                            Logger.Warning?.Print(LogClass.Audio, 
                                $"Possible audio stall detected. Buffered: {bufferedFrames}, Queue: {_queuedBuffers.Count}");
                            
                            _needsRecovery = true;
                        }
                    }
                    
                    // 计算已播放的样本数
                    ulong playedSamples = _totalWrittenSamples - (ulong)(bufferedFrames * _channelCount);
                    
                    // 防止回退
                    if (playedSamples < _totalPlayedSamples)
                    {
                        _totalPlayedSamples = playedSamples;
                        return;
                    }
                    
                    ulong availableSampleCount = playedSamples - _totalPlayedSamples;
                    
                    // 更新缓冲区播放状态
                    while (availableSampleCount > 0 && _queuedBuffers.TryPeek(out OboeAudioBuffer driverBuffer))
                    {
                        ulong sampleStillNeeded = driverBuffer.SampleCount - driverBuffer.SamplePlayed;
                        ulong playedAudioBufferSampleCount = Math.Min(sampleStillNeeded, availableSampleCount);
                        
                        driverBuffer.SamplePlayed += playedAudioBufferSampleCount;
                        availableSampleCount -= playedAudioBufferSampleCount;
                        _totalPlayedSamples += playedAudioBufferSampleCount;
                        
                        // 如果缓冲区播放完毕，移除它
                        if (driverBuffer.SamplePlayed == driverBuffer.SampleCount)
                        {
                            _queuedBuffers.TryDequeue(out _);
                            _driver.GetUpdateRequiredEvent().Set();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Error in UpdatePlaybackStatus: {ex.Message}");
                }
            }
            
            private void CheckAndRecover()
            {
                if (!_needsRecovery) return;
                
                try
                {
                    Logger.Info?.Print(LogClass.Audio, "Attempting audio recovery...");
                    
                    // 重置失败计数器
                    _consecutiveFailures = 0;
                    _needsRecovery = false;
                    _lastSuccessTime = DateTime.Now;
                    
                    // 尝试恢复音频流
                    if (_rendererPtr != IntPtr.Zero)
                    {
                        // 先停止
                        try
                        {
                            shutdownOboeRenderer(_rendererPtr);
                        }
                        catch { }
                        
                        // 重新初始化
                        int formatValue = SampleFormatToInt(_sampleFormat);
                        bool success = initOboeRenderer(_rendererPtr, (int)_sampleRate, _channelCount, formatValue);
                        
                        if (success)
                        {
                            Logger.Info?.Print(LogClass.Audio, "Audio recovery successful");
                            setOboeRendererVolume(_rendererPtr, _volume);
                        }
                        else
                        {
                            Logger.Error?.Print(LogClass.Audio, "Audio recovery failed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Recovery error: {ex.Message}");
                }
            }

            public override void Dispose()
            {
                Stop();
                
                if (_rendererPtr != IntPtr.Zero)
                {
                    try
                    {
                        shutdownOboeRenderer(_rendererPtr);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Audio, $"Error shutting down renderer: {ex.Message}");
                    }
                    
                    try
                    {
                        destroyOboeRenderer(_rendererPtr);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Audio, $"Error destroying renderer: {ex.Message}");
                    }
                }
                
                _driver.Unregister(this);
            }

            public override void PrepareToClose() 
            {
                Stop();
            }

            public override void Start() 
            {
                if (!_active)
                {
                    _active = true;
                    _lastSuccessTime = DateTime.Now;
                }
            }

            public override void Stop() 
            {
                if (_active)
                {
                    _active = false;
                    _queuedBuffers.Clear();
                }
            }

            public override void QueueBuffer(AudioBuffer buffer)
            {
                if (!_active) 
                {
                    Start();
                }

                if (buffer.Data == null || buffer.Data.Length == 0) 
                {
                    return;
                }

                // 计算帧数
                int bytesPerSample = GetBytesPerSample(_sampleFormat);
                int frameCount = buffer.Data.Length / (bytesPerSample * _channelCount);
                
                if (frameCount == 0)
                {
                    return;
                }

                // 直接传递原始数据到独立的渲染器
                int formatValue = SampleFormatToInt(_sampleFormat);
                bool writeSuccess = false;
                
                try
                {
                    writeSuccess = writeOboeRendererAudioRaw(_rendererPtr, buffer.Data, frameCount, formatValue);
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Audio, $"Write failed with exception: {ex.Message}");
                    writeSuccess = false;
                }

                if (writeSuccess)
                {
                    _consecutiveFailures = 0;
                    _lastSuccessTime = DateTime.Now;
                    
                    ulong sampleCount = (ulong)(frameCount * _channelCount);
                    _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, sampleCount));
                    _totalWrittenSamples += sampleCount;
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Audio, 
                        $"Audio write failed: {frameCount} frames dropped, Format={_sampleFormat}, Rate={_sampleRate}Hz");
                    
                    _consecutiveFailures++;
                    
                    // 如果连续失败多次，标记需要恢复
                    if (_consecutiveFailures >= 3)
                    {
                        _needsRecovery = true;
                        Logger.Warning?.Print(LogClass.Audio, 
                            $"Multiple consecutive failures ({_consecutiveFailures}), scheduling recovery");
                    }
                    
                    // 尝试重置渲染器
                    try
                    {
                        resetOboeRenderer(_rendererPtr);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Audio, $"Reset failed: {ex.Message}");
                    }
                }
            }

            private int SampleFormatToInt(SampleFormat format)
            {
                return format switch
                {
                    SampleFormat.PcmInt16 => 1,
                    SampleFormat.PcmInt24 => 2,
                    SampleFormat.PcmInt32 => 3,
                    SampleFormat.PcmFloat => 4,
                    _ => 1,
                };
            }

            private int GetBytesPerSample(SampleFormat format)
            {
                return format switch
                {
                    SampleFormat.PcmInt16 => 2,
                    SampleFormat.PcmInt24 => 3,
                    SampleFormat.PcmInt32 => 4,
                    SampleFormat.PcmFloat => 4,
                    _ => 2,
                };
            }

            public override bool WasBufferFullyConsumed(AudioBuffer buffer)
            {
                return !_queuedBuffers.TryPeek(out var driverBuffer) || 
                       driverBuffer.DriverIdentifier != buffer.DataPointer;
            }

            public override void SetVolume(float volume)
            {
                _volume = Math.Clamp(volume, 0.0f, 1.0f);
                if (_rendererPtr != IntPtr.Zero)
                {
                    try
                    {
                        setOboeRendererVolume(_rendererPtr, _volume);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Audio, $"Failed to set volume: {ex.Message}");
                    }
                }
            }

            public override float GetVolume() => _volume;

            public override ulong GetPlayedSampleCount()
            {
                return _totalPlayedSamples;
            }

            public override void UnregisterBuffer(AudioBuffer buffer)
            {
                if (_queuedBuffers.TryPeek(out var driverBuffer) && 
                    driverBuffer.DriverIdentifier == buffer.DataPointer)
                {
                    _queuedBuffers.TryDequeue(out _);
                }
            }
        }

        // ========== 内部缓冲区类 ==========
        private class OboeAudioBuffer
        {
            public readonly ulong DriverIdentifier;
            public readonly ulong SampleCount;
            public ulong SamplePlayed;

            public OboeAudioBuffer(ulong driverIdentifier, ulong sampleCount)
            {
                DriverIdentifier = driverIdentifier;
                SampleCount = sampleCount;
                SamplePlayed = 0;
            }
        }
    }
}
#endif
