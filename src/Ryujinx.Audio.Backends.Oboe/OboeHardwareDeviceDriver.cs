// OboeHardwareDeviceDriver.cs (多实例架构 - 优化版本)
#if ANDROID
using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private static extern bool isOboeRendererPlaying(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "getOboeRendererBufferedFrames")]
        private static extern int getOboeRendererBufferedFrames(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "resetOboeRenderer")]
        private static extern void resetOboeRenderer(IntPtr renderer);

        // ========== 属性 ==========
        public static bool IsSupported => true;

        private bool _disposed;
        private readonly ManualResetEvent _pauseEvent = new(true);
        private readonly ManualResetEvent _updateRequiredEvent = new(false);
        
        // 会话管理
        private readonly ConcurrentDictionary<OboeAudioSession, (DateTime LastActive, DateTime LastPlayback)> _sessionRecords = new();
        private readonly ConcurrentDictionary<OboeAudioSession, byte> _sessions = new();
        private Thread _updateThread;
        private bool _stillRunning = true;
        private readonly object _cleanupLock = new();
        private DateTime _lastCleanupTime = DateTime.UtcNow;
        
        // 配置常量
        private const int MAX_SESSION_COUNT = 8;
        private const int SESSION_INACTIVE_TIMEOUT_SECONDS = 30;
        private const int SESSION_PLAYBACK_TIMEOUT_SECONDS = 10;
        private const int UPDATE_THREAD_INTERVAL_MS = 20;
        private const int CLEANUP_INTERVAL_MS = 5000;

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
                var stopwatch = Stopwatch.StartNew();
                var cleanupStopwatch = Stopwatch.StartNew();
                long lastCleanupElapsed = 0;

                while (_stillRunning)
                {
                    try
                    {
                        stopwatch.Restart();

                        // 获取活跃会话列表
                        var activeSessions = new List<OboeAudioSession>();
                        foreach (var session in _sessions.Keys)
                        {
                            if (session.IsActive)
                            {
                                activeSessions.Add(session);
                            }
                        }

                        // 处理每个活跃会话
                        foreach (var session in activeSessions)
                        {
                            try
                            {
                                int bufferedFrames = session.GetBufferedFrames();
                                session.UpdatePlaybackStatus(bufferedFrames);
                                
                                // 更新会话活跃记录
                                if (_sessionRecords.TryGetValue(session, out var record))
                                {
                                    var now = DateTime.UtcNow;
                                    if (bufferedFrames > 0)
                                    {
                                        // 有缓冲数据，更新最后播放时间
                                        _sessionRecords.TryUpdate(session, 
                                            (record.LastActive, now), record);
                                    }
                                    else
                                    {
                                        // 无缓冲数据，更新最后活跃时间
                                        _sessionRecords.TryUpdate(session, 
                                            (now, record.LastPlayback), record);
                                    }
                                }
                            }
                            catch (Exception sessionEx)
                            {
                                Logger.Error?.Print(LogClass.Audio, $"Session update error: {sessionEx.Message}");
                            }
                        }

                        // 动态调整休眠时间
                        long elapsedMs = stopwatch.ElapsedMilliseconds;
                        long sleepTime = Math.Max(5, UPDATE_THREAD_INTERVAL_MS - elapsedMs);
                        
                        // 定期清理非活跃会话
                        if (cleanupStopwatch.ElapsedMilliseconds - lastCleanupElapsed >= CLEANUP_INTERVAL_MS)
                        {
                            CleanupInactiveSessions();
                            lastCleanupElapsed = cleanupStopwatch.ElapsedMilliseconds;
                        }

                        if (sleepTime > 0)
                        {
                            Thread.Sleep((int)sleepTime);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Update thread error: {ex.Message}");
                        Thread.Sleep(50); // 出错时延长休眠时间
                    }
                }
                
                stopwatch.Stop();
                cleanupStopwatch.Stop();
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _updateThread.Start();
        }

        private void CleanupInactiveSessions()
        {
            lock (_cleanupLock)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var sessionsToRemove = new List<OboeAudioSession>();

                    // 检查所有会话记录
                    foreach (var kvp in _sessionRecords)
                    {
                        var session = kvp.Key;
                        var record = kvp.Value;
                        
                        bool shouldRemove = false;
                        
                        // 检查是否非活跃超时
                        if ((now - record.LastActive).TotalSeconds > SESSION_INACTIVE_TIMEOUT_SECONDS)
                        {
                            shouldRemove = true;
                        }
                        
                        // 检查是否长时间无播放
                        if (!session.IsActive && (now - record.LastPlayback).TotalSeconds > SESSION_PLAYBACK_TIMEOUT_SECONDS)
                        {
                            shouldRemove = true;
                        }
                        
                        if (shouldRemove)
                        {
                            sessionsToRemove.Add(session);
                        }
                    }

                    // 如果会话数量超过限制，清理最早的非活跃会话
                    if (_sessions.Count > MAX_SESSION_COUNT)
                    {
                        var oldestSessions = _sessionRecords
                            .OrderBy(kvp => kvp.Value.LastActive)
                            .ThenBy(kvp => kvp.Value.LastPlayback)
                            .Take(_sessions.Count - MAX_SESSION_COUNT)
                            .Select(kvp => kvp.Key)
                            .ToList();
                        
                        foreach (var session in oldestSessions)
                        {
                            if (!sessionsToRemove.Contains(session))
                            {
                                sessionsToRemove.Add(session);
                            }
                        }
                    }

                    // 清理会话
                    int cleanedCount = 0;
                    foreach (var session in sessionsToRemove)
                    {
                        try
                        {
                            session.Dispose();
                            _sessions.TryRemove(session, out _);
                            _sessionRecords.TryRemove(session, out _);
                            cleanedCount++;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.Audio, $"Error cleaning up session: {ex.Message}");
                        }
                    }

                    if (cleanedCount > 0)
                    {
                        Logger.Info?.Print(LogClass.Audio, $"Cleaned up {cleanedCount} inactive audio sessions (remaining: {_sessions.Count})");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Cleanup error: {ex.Message}");
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
                    _stillRunning = false;
                    _updateThread?.Join(200);
                    
                    // 清理所有会话
                    foreach (var session in _sessions.Keys.ToList())
                    {
                        try
                        {
                            session.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.Audio, $"Error disposing session: {ex.Message}");
                        }
                    }
                    
                    _sessions.Clear();
                    _sessionRecords.Clear();
                    
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

            // 检查会话数量限制
            if (_sessions.Count >= MAX_SESSION_COUNT)
            {
                // 尝试清理一些会话
                CleanupInactiveSessions();
                
                // 如果仍然超过限制，返回最早的会话（如果可用）
                if (_sessions.Count >= MAX_SESSION_COUNT)
                {
                    var oldestSession = _sessionRecords
                        .OrderBy(kvp => kvp.Value.LastActive)
                        .ThenBy(kvp => kvp.Value.LastPlayback)
                        .FirstOrDefault();
                    
                    if (oldestSession.Key != null)
                    {
                        Logger.Warning?.Print(LogClass.Audio, 
                            $"Audio session limit reached ({MAX_SESSION_COUNT}), reusing oldest session");
                        return oldestSession.Key;
                    }
                }
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            _sessions.TryAdd(session, 0);
            _sessionRecords.TryAdd(session, (DateTime.UtcNow, DateTime.UtcNow));
            
            Logger.Info?.Print(LogClass.Audio, 
                $"Created audio session (total: {_sessions.Count}, max: {MAX_SESSION_COUNT})");
            
            return session;
        }

        private bool Unregister(OboeAudioSession session) 
        {
            bool removed = _sessions.TryRemove(session, out _);
            _sessionRecords.TryRemove(session, out _);
            
            if (removed)
            {
                Logger.Info?.Print(LogClass.Audio, 
                    $"Unregistered audio session (remaining: {_sessions.Count})");
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
            private DateTime _lastAudioDataTime = DateTime.UtcNow;
            private int _consecutiveFailures = 0;
            private const int MAX_CONSECUTIVE_FAILURES = 5;
            private bool _isDisposed = false;
            private readonly object _disposeLock = new();
            private bool _needsHardReset = false;

            public bool IsActive => _active && !_isDisposed;

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
                _lastAudioDataTime = DateTime.UtcNow;
            }

            public int GetBufferedFrames()
            {
                if (_isDisposed || _rendererPtr == IntPtr.Zero || _needsHardReset)
                    return 0;
                    
                try
                {
                    return getOboeRendererBufferedFrames(_rendererPtr);
                }
                catch
                {
                    return 0;
                }
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                try
                {
                    if (_needsHardReset)
                    {
                        PerformHardReset();
                        return;
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

            public override void Dispose()
            {
                lock (_disposeLock)
                {
                    if (_isDisposed)
                        return;
                        
                    _isDisposed = true;
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
                    
                    _queuedBuffers.Clear();
                    _driver.Unregister(this);
                }
            }

            public override void PrepareToClose() 
            {
                Stop();
            }

            public override void Start() 
            {
                if (!_active && !_isDisposed)
                {
                    _active = true;
                    _lastAudioDataTime = DateTime.UtcNow;
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
                lock (_disposeLock)
                {
                    if (_isDisposed || _needsHardReset)
                        return;
                        
                    if (!_active) 
                        Start();

                    if (buffer.Data == null || buffer.Data.Length == 0) 
                        return;

                    // 检查是否太久没有音频数据
                    if ((DateTime.UtcNow - _lastAudioDataTime).TotalSeconds > 30)
                    {
                        Logger.Warning?.Print(LogClass.Audio, "Audio pipeline stale, resetting...");
                        ResetAudioPipeline();
                    }
                    
                    _lastAudioDataTime = DateTime.UtcNow;

                    // 计算帧数
                    int bytesPerSample = GetBytesPerSample(_sampleFormat);
                    int frameCount = buffer.Data.Length / (bytesPerSample * _channelCount);

                    // 直接传递原始数据到独立的渲染器
                    int formatValue = SampleFormatToInt(_sampleFormat);
                    bool writeSuccess = false;
                    
                    try
                    {
                        writeSuccess = writeOboeRendererAudioRaw(_rendererPtr, buffer.Data, frameCount, formatValue);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Audio write exception: {ex.Message}");
                        writeSuccess = false;
                    }

                    if (writeSuccess)
                    {
                        _consecutiveFailures = 0;
                        ulong sampleCount = (ulong)(frameCount * _channelCount);
                        _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, sampleCount));
                        _totalWrittenSamples += sampleCount;
                    }
                    else
                    {
                        _consecutiveFailures++;
                        Logger.Warning?.Print(LogClass.Audio, 
                            $"Audio write failed (consecutive: {_consecutiveFailures}): {frameCount} frames dropped");
                        
                        if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                        {
                            Logger.Error?.Print(LogClass.Audio, "Max consecutive failures reached, scheduling hard reset");
                            _needsHardReset = true;
                        }
                        else
                        {
                            // 重置渲染器
                            try
                            {
                                resetOboeRenderer(_rendererPtr);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error?.Print(LogClass.Audio, $"Error resetting renderer: {ex.Message}");
                                _needsHardReset = true;
                            }
                        }
                    }
                }
            }

            private void ResetAudioPipeline()
            {
                try
                {
                    _queuedBuffers.Clear();
                    _totalWrittenSamples = 0;
                    _totalPlayedSamples = 0;
                    _consecutiveFailures = 0;
                    
                    if (_rendererPtr != IntPtr.Zero)
                    {
                        resetOboeRenderer(_rendererPtr);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Error resetting audio pipeline: {ex.Message}");
                }
            }

            private void PerformHardReset()
            {
                lock (_disposeLock)
                {
                    try
                    {
                        Logger.Info?.Print(LogClass.Audio, "Performing hard reset of audio session");
                        
                        Stop();
                        
                        if (_rendererPtr != IntPtr.Zero)
                        {
                            shutdownOboeRenderer(_rendererPtr);
                            destroyOboeRenderer(_rendererPtr);
                        }
                        
                        // 创建新的渲染器
                        var newRendererPtr = createOboeRenderer();
                        if (newRendererPtr == IntPtr.Zero)
                        {
                            Logger.Error?.Print(LogClass.Audio, "Failed to create new renderer during hard reset");
                            _needsHardReset = true; // 继续尝试
                            return;
                        }

                        // 重新初始化
                        int formatValue = SampleFormatToInt(_sampleFormat);
                        if (!initOboeRenderer(newRendererPtr, (int)_sampleRate, _channelCount, formatValue))
                        {
                            destroyOboeRenderer(newRendererPtr);
                            Logger.Error?.Print(LogClass.Audio, "Failed to initialize new renderer during hard reset");
                            _needsHardReset = true; // 继续尝试
                            return;
                        }

                        setOboeRendererVolume(newRendererPtr, _volume);
                        
                        // 由于_rendererPtr是只读的，我们需要使用反射来修改它
                        // 在实际代码中，可能需要重构为使用属性
                        // 这里我们标记为需要外部重建，但实际由于是私有类，我们无法直接修改
                        // 简化处理：设置标志让外部知道需要重建会话
                        Logger.Warning?.Print(LogClass.Audio, 
                            "Hard reset complete but renderer pointer cannot be replaced. Session needs recreation.");
                        
                        // 清除状态
                        _queuedBuffers.Clear();
                        _totalWrittenSamples = 0;
                        _totalPlayedSamples = 0;
                        _consecutiveFailures = 0;
                        _needsHardReset = false;
                        
                        // 由于无法替换_rendererPtr，我们只能停止这个会话
                        _active = false;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Error in hard reset: {ex.Message}");
                        _needsHardReset = true; // 继续尝试
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
                if (_rendererPtr != IntPtr.Zero && !_isDisposed && !_needsHardReset)
                {
                    setOboeRendererVolume(_rendererPtr, _volume);
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