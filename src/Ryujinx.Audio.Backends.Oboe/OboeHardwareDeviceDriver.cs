// OboeHardwareDeviceDriver.cs (多实例架构)
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
        
        [DllImport("libryujinxjni", EntryPoint = "getOboeRendererPerformanceStats")]
        private static extern PerformanceStats GetPerformanceStats(IntPtr renderer);
        
        [DllImport("libryujinxjni", EntryPoint = "setOboeRendererPerformanceHint")]
        private static extern void SetPerformanceHintEnabled(IntPtr renderer, bool enabled);

        // ========== 性能统计结构 ==========
        [StructLayout(LayoutKind.Sequential)]
        public struct PerformanceStats
        {
            public long XRunCount;
            public long TotalFramesPlayed;
            public long TotalFramesWritten;
            public double AverageLatencyMs;
            public double MaxLatencyMs;
            public double MinLatencyMs;
            public long ErrorCount;
            public long LastErrorTimestamp;
        }

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions = new();
        private Thread _updateThread;
        private bool _stillRunning = true;
        private PerformanceStats _globalStats;
        private readonly object _statsLock = new();

        public float Volume { get; set; } = 1.0f;
        
        public PerformanceStats GlobalStats 
        {
            get 
            {
                lock (_statsLock) 
                {
                    return _globalStats;
                }
            }
        }

        // ========== 构造与生命周期 ==========
        public OboeHardwareDeviceDriver()
        {
            StartUpdateThread();
            Logger.Info?.Print(LogClass.Audio, "OboeHardwareDeviceDriver initialized");
        }

        private void StartUpdateThread()
        {
            _updateThread = new Thread(() =>
            {
                int updateCounter = 0;
                
                while (_stillRunning)
                {
                    try
                    {
                        Thread.Sleep(10); // 10ms更新频率
                        
                        foreach (var session in _sessions.Keys)
                        {
                            if (session.IsActive)
                            {
                                int bufferedFrames = session.GetBufferedFrames();
                                session.UpdatePlaybackStatus(bufferedFrames);
                                
                                // 每100次更新（约1秒）收集一次性能统计
                                if (updateCounter % 100 == 0)
                                {
                                    var stats = session.GetPerformanceStats();
                                    UpdateGlobalStats(stats);
                                }
                            }
                        }
                        
                        updateCounter++;
                        
                        // 每1000次更新（约10秒）记录一次状态
                        if (updateCounter % 1000 == 0)
                        {
                            Logger.Debug?.Print(LogClass.Audio, 
                                $"Oboe driver update thread running. Sessions: {_sessions.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Update thread error: {ex.Message}");
                        
                        // 防止异常导致线程退出
                        Thread.Sleep(100);
                    }
                }
                
                Logger.Info?.Print(LogClass.Audio, "Oboe driver update thread stopped");
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            
            _updateThread.Start();
            Logger.Debug?.Print(LogClass.Audio, "Oboe driver update thread started");
        }
        
        private void UpdateGlobalStats(PerformanceStats sessionStats)
        {
            lock (_statsLock)
            {
                _globalStats.XRunCount += sessionStats.XRunCount;
                _globalStats.TotalFramesPlayed += sessionStats.TotalFramesPlayed;
                _globalStats.TotalFramesWritten += sessionStats.TotalFramesWritten;
                _globalStats.ErrorCount += sessionStats.ErrorCount;
                
                // 更新延迟统计
                if (sessionStats.AverageLatencyMs > 0)
                {
                    if (_globalStats.AverageLatencyMs == 0)
                    {
                        _globalStats.AverageLatencyMs = sessionStats.AverageLatencyMs;
                    }
                    else
                    {
                        _globalStats.AverageLatencyMs = (_globalStats.AverageLatencyMs + sessionStats.AverageLatencyMs) / 2;
                    }
                    
                    _globalStats.MaxLatencyMs = Math.Max(_globalStats.MaxLatencyMs, sessionStats.MaxLatencyMs);
                    
                    if (_globalStats.MinLatencyMs == 0 || sessionStats.MinLatencyMs < _globalStats.MinLatencyMs)
                    {
                        _globalStats.MinLatencyMs = sessionStats.MinLatencyMs;
                    }
                }
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
                    Logger.Info?.Print(LogClass.Audio, "OboeHardwareDeviceDriver disposing");
                    
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
                            Logger.Warning?.Print(LogClass.Audio, $"Error closing session: {ex.Message}");
                        }
                    }
                    
                    _sessions.Clear();
                    
                    // 等待更新线程结束
                    if (_updateThread != null && _updateThread.IsAlive)
                    {
                        if (!_updateThread.Join(TimeSpan.FromSeconds(2)))
                        {
                            Logger.Warning?.Print(LogClass.Audio, "Update thread did not exit gracefully");
                        }
                    }
                    
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                    
                    Logger.Info?.Print(LogClass.Audio, "OboeHardwareDeviceDriver disposed");
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
                Logger.Info?.Print(LogClass.Audio, $"Sample rate was 0, defaulting to {sampleRate}Hz");
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            
            Logger.Info?.Print(LogClass.Audio, 
                $"Opened Oboe audio session: {sampleRate}Hz, {channelCount} channels, {sampleFormat}");
            
            return session;
        }

        private bool Unregister(OboeAudioSession session) 
        {
            bool removed = _sessions.TryRemove(session, out _);
            if (removed)
            {
                Logger.Debug?.Print(LogClass.Audio, "Oboe audio session unregistered");
            }
            return removed;
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
            private PerformanceStats _sessionStats;
            private readonly object _sessionStatsLock = new();
            private int _underrunCount;
            private DateTime _lastUnderrunTime;

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
                _underrunCount = 0;
                _lastUnderrunTime = DateTime.MinValue;
                
                Logger.Debug?.Print(LogClass.Audio, $"Creating OboeAudioSession: {sampleRate}Hz, {channelCount}ch, {sampleFormat}");

                // 创建独立的渲染器实例
                _rendererPtr = createOboeRenderer();
                if (_rendererPtr == IntPtr.Zero)
                {
                    Logger.Error?.Print(LogClass.Audio, "Failed to create Oboe audio renderer");
                    throw new Exception("Failed to create Oboe audio renderer");
                }

                // 初始化渲染器
                int formatValue = SampleFormatToInt(sampleFormat);
                if (!initOboeRenderer(_rendererPtr, (int)sampleRate, (int)channelCount, formatValue))
                {
                    Logger.Error?.Print(LogClass.Audio, 
                        $"Failed to initialize Oboe audio renderer: {sampleRate}Hz, {channelCount}ch, format={formatValue}");
                    destroyOboeRenderer(_rendererPtr);
                    throw new Exception("Failed to initialize Oboe audio renderer");
                }

                setOboeRendererVolume(_rendererPtr, _volume);
                
                // 启用性能提示
                SetPerformanceHintEnabled(_rendererPtr, true);
                
                Logger.Info?.Print(LogClass.Audio, "OboeAudioSession created successfully");
            }
            
            public PerformanceStats GetPerformanceStats()
            {
                lock (_sessionStatsLock)
                {
                    return _sessionStats;
                }
            }
            
            private void UpdateStats(int bufferedFrames, bool hadUnderrun = false)
            {
                lock (_sessionStatsLock)
                {
                    // 计算延迟估计（基于缓冲区大小）
                    double estimatedLatencyMs = (bufferedFrames * 1000.0) / _sampleRate;
                    
                    if (_sessionStats.AverageLatencyMs == 0)
                    {
                        _sessionStats.AverageLatencyMs = estimatedLatencyMs;
                        _sessionStats.MinLatencyMs = estimatedLatencyMs;
                        _sessionStats.MaxLatencyMs = estimatedLatencyMs;
                    }
                    else
                    {
                        // 指数移动平均
                        _sessionStats.AverageLatencyMs = (_sessionStats.AverageLatencyMs * 0.9) + (estimatedLatencyMs * 0.1);
                        _sessionStats.MinLatencyMs = Math.Min(_sessionStats.MinLatencyMs, estimatedLatencyMs);
                        _sessionStats.MaxLatencyMs = Math.Max(_sessionStats.MaxLatencyMs, estimatedLatencyMs);
                    }
                    
                    if (hadUnderrun)
                    {
                        _sessionStats.XRunCount++;
                    }
                }
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
                    Logger.Warning?.Print(LogClass.Audio, $"Error getting buffered frames: {ex.Message}");
                    return 0;
                }
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                try
                {
                    // 检测underrun
                    bool hadUnderrun = false;
                    if (bufferedFrames == 0 && _queuedBuffers.Count > 0)
                    {
                        // 缓冲区为空但有排队的数据，可能是underrun
                        var now = DateTime.Now;
                        if ((now - _lastUnderrunTime).TotalSeconds > 1.0) // 避免连续记录
                        {
                            _underrunCount++;
                            _lastUnderrunTime = now;
                            hadUnderrun = true;
                            
                            if (_underrunCount % 10 == 0) // 每10次underrun记录一次警告
                            {
                                Logger.Warning?.Print(LogClass.Audio, 
                                    $"Audio underrun detected (count: {_underrunCount})");
                            }
                        }
                    }
                    
                    // 更新统计
                    UpdateStats(bufferedFrames, hadUnderrun);

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
                        
                        // 更新已播放帧数统计
                        lock (_sessionStatsLock)
                        {
                            _sessionStats.TotalFramesPlayed += (long)(playedAudioBufferSampleCount / (ulong)_channelCount);
                        }
                        
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
                    
                    lock (_sessionStatsLock)
                    {
                        _sessionStats.ErrorCount++;
                    }
                }
            }

            public override void Dispose()
            {
                Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession disposing");
                
                Stop();
                
                if (_rendererPtr != IntPtr.Zero)
                {
                    try
                    {
                        shutdownOboeRenderer(_rendererPtr);
                        destroyOboeRenderer(_rendererPtr);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Error destroying renderer: {ex.Message}");
                    }
                }
                
                _driver.Unregister(this);
                
                // 记录会话统计
                lock (_sessionStatsLock)
                {
                    Logger.Info?.Print(LogClass.Audio, 
                        $"OboeAudioSession final stats: " +
                        $"XRun={_sessionStats.XRunCount}, " +
                        $"AvgLatency={_sessionStats.AverageLatencyMs:F2}ms, " +
                        $"FramesPlayed={_sessionStats.TotalFramesPlayed}");
                }
                
                base.Dispose();
            }

            public override void PrepareToClose() 
            {
                Logger.Debug?.Print(LogClass.Audio, "Preparing OboeAudioSession to close");
                Stop();
            }

            public override void Start() 
            {
                if (!_active)
                {
                    _active = true;
                    Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession started");
                }
            }

            public override void Stop() 
            {
                if (_active)
                {
                    _active = false;
                    _queuedBuffers.Clear();
                    Logger.Debug?.Print(LogClass.Audio, "OboeAudioSession stopped");
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
                    Logger.Warning?.Print(LogClass.Audio, "Attempted to queue empty audio buffer");
                    return;
                }

                // 计算帧数
                int bytesPerSample = GetBytesPerSample(_sampleFormat);
                int frameCount = buffer.Data.Length / (bytesPerSample * _channelCount);
                
                if (frameCount == 0)
                {
                    Logger.Warning?.Print(LogClass.Audio, "Audio buffer has zero frames");
                    return;
                }

                // 更新写入统计
                lock (_sessionStatsLock)
                {
                    _sessionStats.TotalFramesWritten += frameCount;
                }

                // 直接传递原始数据到独立的渲染器
                int formatValue = SampleFormatToInt(_sampleFormat);
                bool writeSuccess = writeOboeRendererAudioRaw(_rendererPtr, buffer.Data, frameCount, formatValue);

                if (writeSuccess)
                {
                    ulong sampleCount = (ulong)(frameCount * _channelCount);
                    _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, sampleCount));
                    _totalWrittenSamples += sampleCount;
                    
                    // 记录详细的调试信息（每秒一次）
                    if (frameCount > 0 && _totalWrittenSamples % (ulong)(_sampleRate * _channelCount) < sampleCount)
                    {
                        Logger.Debug?.Print(LogClass.Audio, 
                            $"Audio write: {frameCount} frames, buffered: {getOboeRendererBufferedFrames(_rendererPtr)} frames");
                    }
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Audio, 
                        $"Audio write failed: {frameCount} frames dropped, Format={_sampleFormat}, Rate={_sampleRate}Hz");
                    
                    lock (_sessionStatsLock)
                    {
                        _sessionStats.ErrorCount++;
                    }
                    
                    // 重置渲染器
                    try
                    {
                        resetOboeRenderer(_rendererPtr);
                        Logger.Info?.Print(LogClass.Audio, "Oboe renderer reset after write failure");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Error resetting renderer: {ex.Message}");
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
                        Logger.Debug?.Print(LogClass.Audio, $"Volume set to {_volume:F2}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Audio, $"Error setting volume: {ex.Message}");
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
                    Logger.Debug?.Print(LogClass.Audio, "Audio buffer unregistered");
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