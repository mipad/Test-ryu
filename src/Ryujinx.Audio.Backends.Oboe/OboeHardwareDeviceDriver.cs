// OboeHardwareDeviceDriver.cs (优化延迟版本)
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
        
        // 会话管理 - 使用更高效的数据结构
        private readonly ConcurrentDictionary<OboeAudioSession, SessionInfo> _sessionInfos = new();
        private readonly ConcurrentBag<OboeAudioSession> _activeSessions = new();
        private readonly object _sessionUpdateLock = new();
        
        private Thread _updateThread;
        private bool _stillRunning = true;
        private readonly object _cleanupLock = new();
        private volatile bool _needsCleanup = false;
        
        // 配置常量 - 优化延迟
        private const int MAX_SESSION_COUNT = 6; // 减少最大会话数
        private const int SESSION_INACTIVE_TIMEOUT_SECONDS = 15; // 减少不活跃超时
        private const int UPDATE_THREAD_INTERVAL_MS = 5; // 减少更新间隔
        private const int CLEANUP_INTERVAL_MS = 3000; // 减少清理间隔
        private const int MAX_AUDIO_BUFFER_MS = 100; // 最大音频缓冲100ms

        public float Volume { get; set; } = 1.0f;

        // 会话信息结构
        private struct SessionInfo
        {
            public DateTime LastActive;
            public DateTime LastPlayback;
            public DateTime LastUpdate;
            public int UpdateCount;
            public bool IsMarkedForRemoval;
        }

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
                long lastActiveCheckElapsed = 0;
                const long ACTIVE_CHECK_INTERVAL = 100; // 每100ms检查活跃会话

                while (_stillRunning)
                {
                    try
                    {
                        long startTime = stopwatch.ElapsedMilliseconds;
                        
                        // 使用更高效的方法处理活跃会话
                        ProcessActiveSessions();
                        
                        // 定期检查活跃会话（每100ms）
                        if (stopwatch.ElapsedMilliseconds - lastActiveCheckElapsed >= ACTIVE_CHECK_INTERVAL)
                        {
                            UpdateActiveSessionsList();
                            lastActiveCheckElapsed = stopwatch.ElapsedMilliseconds;
                        }
                        
                        // 定期清理非活跃会话
                        if (cleanupStopwatch.ElapsedMilliseconds - lastCleanupElapsed >= CLEANUP_INTERVAL_MS)
                        {
                            _needsCleanup = true;
                            lastCleanupElapsed = cleanupStopwatch.ElapsedMilliseconds;
                        }
                        
                        // 异步清理，不阻塞主循环
                        if (_needsCleanup)
                        {
                            ThreadPool.QueueUserWorkItem(_ => CleanupInactiveSessions());
                            _needsCleanup = false;
                        }
                        
                        // 计算剩余时间，精确控制间隔
                        long elapsed = stopwatch.ElapsedMilliseconds - startTime;
                        long sleepTime = Math.Max(1, UPDATE_THREAD_INTERVAL_MS - elapsed);
                        
                        if (sleepTime > 0)
                        {
                            // 使用更精确的休眠
                            if (sleepTime > 10)
                            {
                                Thread.Sleep((int)sleepTime);
                            }
                            else
                            {
                                // 短时间使用自旋等待，减少上下文切换
                                Thread.SpinWait(100);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Update thread error: {ex.Message}");
                        Thread.Sleep(10); // 出错时短暂休眠
                    }
                }
                
                stopwatch.Stop();
                cleanupStopwatch.Stop();
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal // 提高优先级，减少延迟
            };
            _updateThread.Start();
        }

        private void ProcessActiveSessions()
        {
            // 快速遍历活跃会话
            foreach (var session in _activeSessions)
            {
                try
                {
                    // 快速获取缓冲帧数
                    int bufferedFrames = session.GetBufferedFrames();
                    
                    // 只更新有缓冲数据的会话
                    if (bufferedFrames > 0)
                    {
                        session.UpdatePlaybackStatus(bufferedFrames);
                        
                        // 更新会话信息
                        if (_sessionInfos.TryGetValue(session, out var info))
                        {
                            var now = DateTime.UtcNow;
                            info.LastUpdate = now;
                            info.LastPlayback = now;
                            info.UpdateCount++;
                            _sessionInfos.TryUpdate(session, info, info);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Session process error: {ex.Message}");
                    // 从活跃列表中移除问题会话
                    RemoveFromActiveSessions(session);
                }
            }
        }

        private void UpdateActiveSessionsList()
        {
            lock (_sessionUpdateLock)
            {
                // 清空当前活跃列表
                while (_activeSessions.TryTake(out _)) { }
                
                // 重新填充活跃会话
                foreach (var kvp in _sessionInfos)
                {
                    var session = kvp.Key;
                    var info = kvp.Value;
                    
                    // 检查会话是否活跃且不需要移除
                    if (session.IsActive && !info.IsMarkedForRemoval)
                    {
                        _activeSessions.Add(session);
                        
                        // 更新最后活跃时间
                        if ((DateTime.UtcNow - info.LastActive).TotalSeconds > 1)
                        {
                            info.LastActive = DateTime.UtcNow;
                            _sessionInfos.TryUpdate(session, info, info);
                        }
                    }
                }
            }
        }

        private void RemoveFromActiveSessions(OboeAudioSession session)
        {
            lock (_sessionUpdateLock)
            {
                var tempList = new List<OboeAudioSession>();
                while (_activeSessions.TryTake(out var activeSession))
                {
                    if (activeSession != session)
                    {
                        tempList.Add(activeSession);
                    }
                }
                
                foreach (var s in tempList)
                {
                    _activeSessions.Add(s);
                }
            }
        }

        private void CleanupInactiveSessions()
        {
            try
            {
                var now = DateTime.UtcNow;
                var sessionsToRemove = new List<OboeAudioSession>();

                // 快速检查需要清理的会话
                foreach (var kvp in _sessionInfos)
                {
                    var session = kvp.Key;
                    var info = kvp.Value;
                    
                    // 检查是否超时
                    if ((now - info.LastActive).TotalSeconds > SESSION_INACTIVE_TIMEOUT_SECONDS ||
                        (!session.IsActive && (now - info.LastPlayback).TotalSeconds > SESSION_INACTIVE_TIMEOUT_SECONDS))
                    {
                        info.IsMarkedForRemoval = true;
                        _sessionInfos.TryUpdate(session, info, info);
                        sessionsToRemove.Add(session);
                    }
                }

                // 清理标记的会话
                foreach (var session in sessionsToRemove)
                {
                    try
                    {
                        session.Dispose();
                        _sessionInfos.TryRemove(session, out _);
                        RemoveFromActiveSessions(session);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Error cleaning up session: {ex.Message}");
                    }
                }

                // 如果会话数量超过限制，清理最早的会话
                if (_sessionInfos.Count > MAX_SESSION_COUNT)
                {
                    var oldestSessions = _sessionInfos
                        .OrderBy(kvp => kvp.Value.LastActive)
                        .ThenBy(kvp => kvp.Value.LastPlayback)
                        .Take(_sessionInfos.Count - MAX_SESSION_COUNT)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    
                    foreach (var session in oldestSessions)
                    {
                        try
                        {
                            session.Dispose();
                            _sessionInfos.TryRemove(session, out _);
                            RemoveFromActiveSessions(session);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.Audio, $"Error removing oldest session: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Audio, $"Cleanup error: {ex.Message}");
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
                    _updateThread?.Join(100);
                    
                    // 清理所有会话
                    foreach (var kvp in _sessionInfos)
                    {
                        try
                        {
                            kvp.Key.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.Audio, $"Error disposing session: {ex.Message}");
                        }
                    }
                    
                    _sessionInfos.Clear();
                    _activeSessions.Clear();
                    
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
            if (_sessionInfos.Count >= MAX_SESSION_COUNT)
            {
                CleanupInactiveSessions();
                
                // 如果仍然超过限制，抛出异常而不是重用会话
                if (_sessionInfos.Count >= MAX_SESSION_COUNT)
                {
                    throw new InvalidOperationException(
                        $"Maximum audio session count reached ({MAX_SESSION_COUNT}). Please close some sessions.");
                }
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            var now = DateTime.UtcNow;
            var sessionInfo = new SessionInfo
            {
                LastActive = now,
                LastPlayback = now,
                LastUpdate = now,
                UpdateCount = 0,
                IsMarkedForRemoval = false
            };
            
            _sessionInfos.TryAdd(session, sessionInfo);
            
            // 添加到活跃会话
            lock (_sessionUpdateLock)
            {
                _activeSessions.Add(session);
            }
            
            Logger.Debug?.Print(LogClass.Audio, 
                $"Created audio session (total: {_sessionInfos.Count}, max: {MAX_SESSION_COUNT})");
            
            return session;
        }

        private bool Unregister(OboeAudioSession session) 
        {
            bool removed = _sessionInfos.TryRemove(session, out _);
            RemoveFromActiveSessions(session);
            
            if (removed)
            {
                Logger.Debug?.Print(LogClass.Audio, 
                    $"Unregistered audio session (remaining: {_sessionInfos.Count})");
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
            private const int MAX_CONSECUTIVE_FAILURES = 3; // 减少失败阈值
            private bool _isDisposed = false;
            private readonly object _disposeLock = new();
            private readonly Stopwatch _playbackStopwatch = new();
            private long _lastBufferTimeMs = 0;

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
                _playbackStopwatch.Start();
            }

            public int GetBufferedFrames()
            {
                if (_isDisposed || _rendererPtr == IntPtr.Zero)
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
                    if (!_active || _isDisposed)
                        return;
                    
                    // 计算缓冲时间（毫秒）
                    int bufferedMs = (int)((bufferedFrames * 1000.0) / _sampleRate);
                    
                    // 如果缓冲太大，跳过一些帧来减少延迟
                    if (bufferedMs > MAX_AUDIO_BUFFER_MS)
                    {
                        int framesToSkip = (bufferedMs - MAX_AUDIO_BUFFER_MS / 2) * (int)_sampleRate / 1000;
                        if (framesToSkip > 0)
                        {
                            SkipBufferedFrames(framesToSkip);
                            bufferedFrames = GetBufferedFrames(); // 重新获取
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
                    
                    // 快速更新缓冲区播放状态
                    UpdateBufferPlayback(availableSampleCount);
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Audio, $"Error in UpdatePlaybackStatus: {ex.Message}");
                }
            }

            private void SkipBufferedFrames(int framesToSkip)
            {
                // 这里简化处理，实际应该通知Oboe渲染器跳过一些帧
                Logger.Debug?.Print(LogClass.Audio, $"Skipping {framesToSkip} frames to reduce latency");
            }

            private void UpdateBufferPlayback(ulong availableSampleCount)
            {
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
                    _playbackStopwatch.Stop();
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
                    _playbackStopwatch.Restart();
                }
            }

            public override void Stop() 
            {
                if (_active)
                {
                    _active = false;
                    _queuedBuffers.Clear();
                    _playbackStopwatch.Stop();
                }
            }

            public override void QueueBuffer(AudioBuffer buffer)
            {
                lock (_disposeLock)
                {
                    if (_isDisposed)
                        return;
                        
                    if (!_active) 
                        Start();

                    if (buffer.Data == null || buffer.Data.Length == 0) 
                        return;

                    // 检查是否太久没有音频数据
                    long currentTime = _playbackStopwatch.ElapsedMilliseconds;
                    if (currentTime - _lastBufferTimeMs > 30000) // 30秒
                    {
                        Logger.Debug?.Print(LogClass.Audio, "Audio pipeline stale, resetting...");
                        ResetAudioPipeline();
                    }
                    
                    _lastBufferTimeMs = currentTime;

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
                        
                        // 记录延迟信息
                        int bufferedFrames = GetBufferedFrames();
                        int bufferedMs = (int)((bufferedFrames * 1000.0) / _sampleRate);
                        if (bufferedMs > 50) // 如果延迟大于50ms，记录警告
                        {
                            Logger.Debug?.Print(LogClass.Audio, $"Audio latency: {bufferedMs}ms (frames: {bufferedFrames})");
                        }
                    }
                    else
                    {
                        _consecutiveFailures++;
                        Logger.Warning?.Print(LogClass.Audio, 
                            $"Audio write failed (consecutive: {_consecutiveFailures})");
                        
                        if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                        {
                            Logger.Error?.Print(LogClass.Audio, "Max consecutive failures reached, resetting");
                            ResetAudioPipeline();
                            _consecutiveFailures = 0;
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
                if (_rendererPtr != IntPtr.Zero && !_isDisposed)
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
