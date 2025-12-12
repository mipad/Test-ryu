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

        [DllImport("libryujinxjni", EntryPoint = "getOboeRendererQueueLoad")]
        private static extern float getOboeRendererQueueLoad(IntPtr renderer);

        [DllImport("libryujinxjni", EntryPoint = "getOboeRendererCallbackInterval")]
        private static extern int getOboeRendererCallbackInterval(IntPtr renderer);

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

        public OboeHardwareDeviceDriver()
        {
            StartUpdateThread();
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
                        Thread.Sleep(10);
                        
                        foreach (var session in _sessions.Keys)
                        {
                            if (session.IsActive)
                            {
                                int bufferedFrames = session.GetBufferedFrames();
                                session.UpdatePlaybackStatus(bufferedFrames);
                                
                                if (updateCounter % 100 == 0)
                                {
                                    var stats = session.GetPerformanceStats();
                                    UpdateGlobalStats(stats);
                                }
                            }
                        }
                        
                        updateCounter++;
                    }
                    catch (Exception ex)
                    {
                        Thread.Sleep(100);
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
        
        private void UpdateGlobalStats(PerformanceStats sessionStats)
        {
            lock (_statsLock)
            {
                _globalStats.XRunCount += sessionStats.XRunCount;
                _globalStats.TotalFramesPlayed += sessionStats.TotalFramesPlayed;
                _globalStats.TotalFramesWritten += sessionStats.TotalFramesWritten;
                _globalStats.ErrorCount += sessionStats.ErrorCount;
                
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
                    _stillRunning = false;
                    
                    foreach (var session in _sessions.Keys)
                    {
                        try
                        {
                            session.PrepareToClose();
                        }
                        catch
                        {
                        }
                    }
                    
                    _sessions.Clear();
                    
                    if (_updateThread != null && _updateThread.IsAlive)
                    {
                        if (!_updateThread.Join(TimeSpan.FromSeconds(2)))
                        {
                        }
                    }
                    
                    _pauseEvent?.Dispose();
                    _updateRequiredEvent?.Dispose();
                }
                _disposed = true;
            }
        }

        public bool SupportsSampleRate(uint sampleRate) => true;

        public bool SupportsSampleFormat(SampleFormat sampleFormat) =>
            sampleFormat == SampleFormat.PcmInt16 || 
            sampleFormat == SampleFormat.PcmInt32 ||
            sampleFormat == SampleFormat.PcmFloat;

        public bool SupportsChannelCount(uint channelCount) =>
            channelCount is 1 or 2 or 6;

        public bool SupportsDirection(IHardwareDeviceDriver.Direction direction) =>
            direction == IHardwareDeviceDriver.Direction.Output;

        public ManualResetEvent GetPauseEvent() => _pauseEvent;
        public ManualResetEvent GetUpdateRequiredEvent() => _updateRequiredEvent;

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
            bool removed = _sessions.TryRemove(session, out _);
            return removed;
        }

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
            
            private DateTime _lastWriteTime = DateTime.MinValue;
            private int _consecutiveFailures = 0;
            private int _adaptiveWriteFrames = 240;
            private readonly object _writeLock = new object();
            private bool _shouldThrottle = false;
            private float _currentQueueLoad = 0f;
            private System.Diagnostics.Stopwatch _writeTimer = new System.Diagnostics.Stopwatch();
            private long _totalWriteTimeMs = 0;
            private long _writeCount = 0;

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
                
                _rendererPtr = createOboeRenderer();
                if (_rendererPtr == IntPtr.Zero)
                {
                    throw new Exception("Failed to create Oboe audio renderer");
                }

                int formatValue = SampleFormatToInt(sampleFormat);
                if (!initOboeRenderer(_rendererPtr, (int)sampleRate, (int)channelCount, formatValue))
                {
                    destroyOboeRenderer(_rendererPtr);
                    throw new Exception("Failed to initialize Oboe audio renderer");
                }

                setOboeRendererVolume(_rendererPtr, _volume);
                
                SetPerformanceHintEnabled(_rendererPtr, true);
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
                    double estimatedLatencyMs = (bufferedFrames * 1000.0) / _sampleRate;
                    
                    if (_sessionStats.AverageLatencyMs == 0)
                    {
                        _sessionStats.AverageLatencyMs = estimatedLatencyMs;
                        _sessionStats.MinLatencyMs = estimatedLatencyMs;
                        _sessionStats.MaxLatencyMs = estimatedLatencyMs;
                    }
                    else
                    {
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
                catch
                {
                    return 0;
                }
            }

            public void UpdatePlaybackStatus(int bufferedFrames)
            {
                try
                {
                    double bufferMs = (bufferedFrames * 1000.0) / _sampleRate;
                    
                    if (bufferMs > 100.0)
                    {
                        _shouldThrottle = true;
                        
                        if (bufferMs > 150.0)
                        {
                            _adaptiveWriteFrames = Math.Max(64, _adaptiveWriteFrames * 2 / 3);
                        }
                    }
                    else if (bufferMs < 30.0)
                    {
                        _shouldThrottle = false;
                        _adaptiveWriteFrames = Math.Min(480, _adaptiveWriteFrames * 3 / 2);
                    }
                    
                    bool hadUnderrun = false;
                    if (bufferedFrames == 0 && _queuedBuffers.Count > 0)
                    {
                        var now = DateTime.Now;
                        if ((now - _lastUnderrunTime).TotalSeconds > 1.0)
                        {
                            _underrunCount++;
                            _lastUnderrunTime = now;
                            hadUnderrun = true;
                        }
                    }
                    
                    UpdateStats(bufferedFrames, hadUnderrun);

                    ulong playedSamples = _totalWrittenSamples - (ulong)(bufferedFrames * _channelCount);
                    
                    if (playedSamples < _totalPlayedSamples)
                    {
                        _totalPlayedSamples = playedSamples;
                        return;
                    }
                    
                    ulong availableSampleCount = playedSamples - _totalPlayedSamples;
                    
                    while (availableSampleCount > 0 && _queuedBuffers.TryPeek(out OboeAudioBuffer driverBuffer))
                    {
                        ulong sampleStillNeeded = driverBuffer.SampleCount - driverBuffer.SamplePlayed;
                        ulong playedAudioBufferSampleCount = Math.Min(sampleStillNeeded, availableSampleCount);
                        
                        driverBuffer.SamplePlayed += playedAudioBufferSampleCount;
                        availableSampleCount -= playedAudioBufferSampleCount;
                        _totalPlayedSamples += playedAudioBufferSampleCount;
                        
                        lock (_sessionStatsLock)
                        {
                            _sessionStats.TotalFramesPlayed += (long)(playedAudioBufferSampleCount / (ulong)_channelCount);
                        }
                        
                        if (driverBuffer.SamplePlayed == driverBuffer.SampleCount)
                        {
                            _queuedBuffers.TryDequeue(out _);
                            _driver.GetUpdateRequiredEvent().Set();
                        }
                    }
                }
                catch
                {
                    lock (_sessionStatsLock)
                    {
                        _sessionStats.ErrorCount++;
                    }
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
                        destroyOboeRenderer(_rendererPtr);
                    }
                    catch
                    {
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
                lock (_writeLock)
                {
                    if (!_active) 
                    {
                        Start();
                    }

                    if (buffer.Data == null || buffer.Data.Length == 0) 
                    {
                        return;
                    }

                    int bytesPerSample = GetBytesPerSample(_sampleFormat);
                    int frameCount = buffer.Data.Length / (bytesPerSample * _channelCount);
                    
                    if (frameCount == 0)
                    {
                        return;
                    }

                    if (_shouldThrottle)
                    {
                        if (_consecutiveFailures > 3 && 
                            (DateTime.Now - _lastWriteTime).TotalMilliseconds < 100)
                        {
                            Thread.Sleep(10);
                        }
                    }

                    int bufferedFrames = GetBufferedFrames();
                    
                    int maxBufferMs = 150;
                    int maxBufferedFrames = (int)(_sampleRate * maxBufferMs / 1000);
                    
                    if (bufferedFrames > maxBufferedFrames * 0.7f)
                    {
                        _adaptiveWriteFrames = Math.Max(64, _adaptiveWriteFrames * 3 / 4);
                        _shouldThrottle = true;
                    }
                    else if (bufferedFrames < maxBufferedFrames * 0.3f)
                    {
                        _adaptiveWriteFrames = Math.Min(480, _adaptiveWriteFrames * 5 / 4);
                        _shouldThrottle = false;
                    }

                    if (bufferedFrames > maxBufferedFrames)
                    {
                        lock (_sessionStatsLock)
                        {
                            _sessionStats.ErrorCount++;
                        }
                        
                        _consecutiveFailures++;
                        return;
                    }

                    _writeTimer.Restart();
                    
                    int framesWritten = 0;
                    bool overallSuccess = true;
                    
                    while (framesWritten < frameCount)
                    {
                        int framesToWrite = Math.Min(_adaptiveWriteFrames, frameCount - framesWritten);
                        int bytesToWrite = framesToWrite * _channelCount * bytesPerSample;
                        int offset = framesWritten * _channelCount * bytesPerSample;
                        
                        byte[] chunk = new byte[bytesToWrite];
                        Buffer.BlockCopy(buffer.Data, offset, chunk, 0, bytesToWrite);
                        
                        int formatValue = SampleFormatToInt(_sampleFormat);
                        bool writeSuccess = writeOboeRendererAudioRaw(_rendererPtr, chunk, framesToWrite, formatValue);

                        if (writeSuccess)
                        {
                            framesWritten += framesToWrite;
                            _consecutiveFailures = 0;
                        }
                        else
                        {
                            overallSuccess = false;
                            _consecutiveFailures++;
                            
                            Thread.Sleep(5);
                            
                            if (_consecutiveFailures > 5)
                            {
                                try
                                {
                                    resetOboeRenderer(_rendererPtr);
                                    _consecutiveFailures = 0;
                                    Thread.Sleep(20);
                                }
                                catch
                                {
                                }
                            }
                            break;
                        }
                        
                        int currentBuffered = getOboeRendererBufferedFrames(_rendererPtr);
                        if (currentBuffered > maxBufferedFrames * 0.8f)
                        {
                            Thread.Sleep(5);
                        }
                    }
                    
                    _writeTimer.Stop();
                    _totalWriteTimeMs += _writeTimer.ElapsedMilliseconds;
                    _writeCount++;
                    
                    if (overallSuccess && framesWritten == frameCount)
                    {
                        ulong sampleCount = (ulong)(frameCount * _channelCount);
                        _queuedBuffers.Enqueue(new OboeAudioBuffer(buffer.DataPointer, sampleCount));
                        _totalWrittenSamples += sampleCount;
                        
                        lock (_sessionStatsLock)
                        {
                            _sessionStats.TotalFramesWritten += frameCount;
                        }
                    }
                    else
                    {
                        lock (_sessionStatsLock)
                        {
                            _sessionStats.ErrorCount++;
                        }
                    }
                    
                    _lastWriteTime = DateTime.Now;
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
                    catch
                    {
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