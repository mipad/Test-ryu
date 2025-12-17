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
using System.Linq;
using System.Collections.Generic;

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
        private readonly ConcurrentDictionary<OboeAudioSession, SessionInfo> _sessions = new();
        private Thread _updateThread;
        private bool _stillRunning = true;
        private int _cleanupCounter = 0;
        private const int CLEANUP_INTERVAL = 100; // 每100次更新循环清理一次
        private const int MAX_SESSIONS = 8; // 最大会话数限制
        private const int INACTIVE_TIMEOUT_MS = 10000; // 非活跃会话超时时间（10秒）

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
                        Thread.Sleep(10); // 10ms更新频率
                        
                        // 定期清理非活跃会话
                        _cleanupCounter++;
                        if (_cleanupCounter >= CLEANUP_INTERVAL)
                        {
                            _cleanupCounter = 0;
                            CleanupInactiveSessions();
                        }
                        
                        // 只处理活跃会话
                        var activeSessions = GetActiveSessions();
                        foreach (var session in activeSessions)
                        {
                            if (session.IsActive)
                            {
                                int bufferedFrames = session.GetBufferedFrames();
                                session.UpdatePlaybackStatus(bufferedFrames);
                                
                                // 更新会话活动时间
                                if (_sessions.TryGetValue(session, out var sessionInfo))
                                {
                                    sessionInfo.LastActivityTime = DateTime.UtcNow;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Audio, $"Update thread error: {ex.Message}");
                    }
                }
            })
            {
                Name = "Audio.Oboe.UpdateThread",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _updateThread.Start();
        }

        private List<OboeAudioSession> GetActiveSessions()
        {
            // 获取所有活跃会话
            var activeSessions = new List<OboeAudioSession>();
            foreach (var kvp in _sessions)
            {
                if (kvp.Key.IsActive || IsRecentlyActive(kvp.Value))
                {
                    activeSessions.Add(kvp.Key);
                }
            }
            return activeSessions;
        }

        private bool IsRecentlyActive(SessionInfo sessionInfo)
        {
            // 判断会话是否最近活跃（1秒内）
            return (DateTime.UtcNow - sessionInfo.LastActivityTime).TotalMilliseconds < 1000;
        }

        private void CleanupInactiveSessions()
        {
            try
            {
                var sessionsToRemove = new List<OboeAudioSession>();
                var now = DateTime.UtcNow;

                foreach (var kvp in _sessions)
                {
                    var session = kvp.Key;
                    var sessionInfo = kvp.Value;

                    // 检查会话是否非活跃且超时
                    if (!session.IsActive && 
                        (now - sessionInfo.LastActivityTime).TotalMilliseconds > INACTIVE_TIMEOUT_MS)
                    {
                        sessionsToRemove.Add(session);
                    }
                }

                // 清理超时会话
                foreach (var session in sessionsToRemove)
                {
                    if (_sessions.TryRemove(session, out _))
                    {
                        try
                        {
                            session.Dispose();
                            Logger.Debug?.Print(LogClass.Audio, $"Cleaned up inactive audio session: {session.GetHashCode()}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning?.Print(LogClass.Audio, $"Error disposing inactive session: {ex.Message}");
                        }
                    }
                }

                // 如果会话数仍然超过限制，强制清理最老的会话
                if (_sessions.Count > MAX_SESSIONS)
                {
                    ForceCleanupOldestSessions(_sessions.Count - MAX_SESSIONS);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Audio, $"Error in CleanupInactiveSessions: {ex.Message}");
            }
        }

        private void ForceCleanupOldestSessions(int countToRemove)
        {
            try
            {
                // 按最后活动时间排序，清理最老的会话
                var oldestSessions = _sessions
                    .OrderBy(kvp => kvp.Value.LastActivityTime)
                    .Take(countToRemove)
                    .ToList();

                foreach (var kvp in oldestSessions)
                {
                    var session = kvp.Key;
                    if (_sessions.TryRemove(session, out _))
                    {
                        try
                        {
                            session.Dispose();
                            Logger.Warning?.Print(LogClass.Audio, $"Force cleaned up audio session due to limit: {session.GetHashCode()}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning?.Print(LogClass.Audio, $"Error disposing session during force cleanup: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Audio, $"Error in ForceCleanupOldestSessions: {ex.Message}");
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
                    foreach (var kvp in _sessions)
                    {
                        try
                        {
                            kvp.Key.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning?.Print(LogClass.Audio, $"Error disposing session during driver shutdown: {ex.Message}");
                        }
                    }
                    _sessions.Clear();
                    
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
            if (_sessions.Count >= MAX_SESSIONS)
            {
                // 尝试清理非活跃会话
                CleanupInactiveSessions();
                
                // 如果仍然超过限制，抛出异常
                if (_sessions.Count >= MAX_SESSIONS)
                {
                    throw new InvalidOperationException(
                        $"Maximum number of audio sessions ({MAX_SESSIONS}) reached. " +
                        "Please wait for existing sessions to finish or close some sessions.");
                }
            }

            var session = new OboeAudioSession(this, memoryManager, sampleFormat, sampleRate, channelCount);
            var sessionInfo = new SessionInfo
            {
                LastActivityTime = DateTime.UtcNow,
                CreationTime = DateTime.UtcNow
            };
            
            if (_sessions.TryAdd(session, sessionInfo))
            {
                Logger.Debug?.Print(LogClass.Audio, 
                    $"Created new audio session. Total sessions: {_sessions.Count}, " +
                    $"SampleRate: {sampleRate}, Channels: {channelCount}, Format: {sampleFormat}");
            }
            
            return session;
        }

        private bool Unregister(OboeAudioSession session) 
        {
            if (_sessions.TryRemove(session, out _))
            {
                Logger.Debug?.Print(LogClass.Audio, 
                    $"Unregistered audio session. Remaining sessions: {_sessions.Count}");
                return true;
            }
            return false;
        }

        // ========== 会话信息类 ==========
        private class SessionInfo
        {
            public DateTime LastActivityTime { get; set; }
            public DateTime CreationTime { get; set; }
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
            private IntPtr _rendererPtr; // 移除了 readonly 修饰符
            private int _consecutiveFailures;
            private const int MAX_CONSECUTIVE_FAILURES = 5;
            private DateTime _lastAudioDataTime;
            private bool _disposed;
            private readonly object _rendererLock = new(); // 添加锁对象用于线程安全

            public bool IsActive => _active && !_disposed;

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
                _lastAudioDataTime = DateTime.UtcNow;
                
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
            }

            public int GetBufferedFrames()
            {
                if (_disposed || _rendererPtr == IntPtr.Zero)
                    return 0;
                    
                return getOboeRendererBufferedFrames(_rendererPtr);
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                if (_disposed) return;
                
                try
                {
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
                if (_disposed) return;
                
                _disposed = true;
                Stop();
                
                lock (_rendererLock)
                {
                    if (_rendererPtr != IntPtr.Zero)
                    {
                        try
                        {
                            shutdownOboeRenderer(_rendererPtr);
                            destroyOboeRenderer(_rendererPtr);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning?.Print(LogClass.Audio, $"Error destroying Oboe renderer: {ex.Message}");
                        }
                        _rendererPtr = IntPtr.Zero;
                    }
                }
                
                _queuedBuffers.Clear();
                _driver.Unregister(this);
            }

            public override void PrepareToClose() 
            {
                Stop();
            }

            public override void Start() 
            {
                if (!_active && !_disposed)
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
                if (_disposed) return;
                
                // 检查是否太久没有数据
                if ((DateTime.UtcNow - _lastAudioDataTime).TotalSeconds > 30)
                {
                    ResetAudioPipeline();
                }
                
                if (!_active) Start();

                if (buffer.Data == null || buffer.Data.Length == 0) return;

                // 计算帧数
                int bytesPerSample = GetBytesPerSample(_sampleFormat);
                int frameCount = buffer.Data.Length / (bytesPerSample * _channelCount);

                // 直接传递原始数据到独立的渲染器
                int formatValue = SampleFormatToInt(_sampleFormat);
                bool writeSuccess;
                
                lock (_rendererLock)
                {
                    if (_rendererPtr == IntPtr.Zero) return;
                    writeSuccess = writeOboeRendererAudioRaw(_rendererPtr, buffer.Data, frameCount, formatValue);
                }

                if (writeSuccess)
                {
                    _consecutiveFailures = 0;
                    ulong sampleCount = (ulong)(frameCount * _channelCount);
                    _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, sampleCount));
                    _totalWrittenSamples += sampleCount;
                    _lastAudioDataTime = DateTime.UtcNow;
                }
                else
                {
                    _consecutiveFailures++;
                    Logger.Warning?.Print(LogClass.Audio, 
                        $"Audio write failed (consecutive: {_consecutiveFailures}): " +
                        $"{frameCount} frames dropped, Format={_sampleFormat}, Rate={_sampleRate}Hz");
                    
                    if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                    {
                        // 完全重置
                        HardReset();
                    }
                    else
                    {
                        // 重置渲染器
                        lock (_rendererLock)
                        {
                            if (_rendererPtr != IntPtr.Zero)
                            {
                                resetOboeRenderer(_rendererPtr);
                            }
                        }
                    }
                }
            }

            private void ResetAudioPipeline()
            {
                if (_disposed) return;
                
                Logger.Info?.Print(LogClass.Audio, "Resetting audio pipeline due to inactivity");
                Stop();
                
                lock (_rendererLock)
                {
                    if (_rendererPtr != IntPtr.Zero)
                    {
                        shutdownOboeRenderer(_rendererPtr);
                    }
                    
                    // 重新初始化
                    int formatValue = SampleFormatToInt(_sampleFormat);
                    if (_rendererPtr != IntPtr.Zero && !initOboeRenderer(_rendererPtr, (int)_sampleRate, _channelCount, formatValue))
                    {
                        Logger.Error?.Print(LogClass.Audio, "Failed to reinitialize Oboe audio renderer after inactivity");
                    }
                    else if (_rendererPtr != IntPtr.Zero)
                    {
                        setOboeRendererVolume(_rendererPtr, _volume);
                        Start();
                    }
                }
                
                _consecutiveFailures = 0;
            }

            private void HardReset()
            {
                if (_disposed) return;
                
                Logger.Warning?.Print(LogClass.Audio, "Performing hard reset of audio session");
                
                Stop();
                
                IntPtr newRenderer = IntPtr.Zero;
                bool resetSuccessful = false;
                
                try
                {
                    // 销毁旧的渲染器
                    lock (_rendererLock)
                    {
                        if (_rendererPtr != IntPtr.Zero)
                        {
                            shutdownOboeRenderer(_rendererPtr);
                            destroyOboeRenderer(_rendererPtr);
                            _rendererPtr = IntPtr.Zero;
                        }
                    }
                    
                    // 重新创建渲染器
                    newRenderer = createOboeRenderer();
                    if (newRenderer == IntPtr.Zero)
                    {
                        Logger.Error?.Print(LogClass.Audio, "Failed to create new Oboe renderer during hard reset");
                        return;
                    }
                    
                    // 重新初始化
                    int formatValue = SampleFormatToInt(_sampleFormat);
                    if (!initOboeRenderer(newRenderer, (int)_sampleRate, _channelCount, formatValue))
                    {
                        destroyOboeRenderer(newRenderer);
                        Logger.Error?.Print(LogClass.Audio, "Failed to initialize new Oboe renderer during hard reset");
                        return;
                    }
                    
                    // 更新渲染器指针
                    lock (_rendererLock)
                    {
                        _rendererPtr = newRenderer;
                    }
                    
                    setOboeRendererVolume(newRenderer, _volume);
                    Start();
                    resetSuccessful = true;
                }
                finally
                {
                    if (!resetSuccessful && newRenderer != IntPtr.Zero)
                    {
                        // 如果重置失败，清理新创建的渲染器
                        destroyOboeRenderer(newRenderer);
                    }
                }
                
                _consecutiveFailures = 0;
                _totalWrittenSamples = 0;
                _totalPlayedSamples = 0;
                _queuedBuffers.Clear();
                
                if (resetSuccessful)
                {
                    Logger.Info?.Print(LogClass.Audio, "Audio session hard reset completed successfully");
                }
                else
                {
                    Logger.Error?.Print(LogClass.Audio, "Audio session hard reset failed");
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
                lock (_rendererLock)
                {
                    if (_rendererPtr != IntPtr.Zero && !_disposed)
                    {
                        setOboeRendererVolume(_rendererPtr, _volume);
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
