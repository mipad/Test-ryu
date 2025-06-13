using Ryujinx.Common;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Gpu.Synchronization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    public sealed class GpuContext : IDisposable
    {
        // 移动设备优化常量
        private const int MobileCommandBatchSize = 50;
        private const int MaxCommandBufferSize = 100;
        private const int SlowFrameThreshold = 33; // 30FPS
        
        private const int NsToTicksFractionNumerator = 384;
        private const int NsToTicksFractionDenominator = 625;

        public ManualResetEvent HostInitalized { get; }
        public IRenderer Renderer { get; }
        public GPFifoDevice GPFifo { get; }
        public SynchronizationManager Synchronization { get; }
        public Window Window { get; }
        internal int SequenceNumber { get; private set; }
        internal ulong SyncNumber { get; private set; }
        internal List<ISyncActionHandler> SyncActions { get; }
        internal List<ISyncActionHandler> SyncpointActions { get; }
        internal List<BufferMigration> BufferMigrations { get; }
        internal Queue<Action> DeferredActions { get; }
        internal ConcurrentDictionary<ulong, PhysicalMemory> PhysicalMemoryRegistry { get; }
        internal SupportBufferUpdater SupportBufferUpdater { get; }
        internal Capabilities Capabilities;

        public event Action<ShaderCacheState, int, int> ShaderCacheStateChanged;

        private Thread _gpuThread;
        private bool _pendingSync;
        private long _modifiedSequence;
        private readonly ulong _firstTimestamp;
        private readonly ManualResetEvent _gpuReadyEvent;
        
        // 性能监控字段
        private long _lastFrameTime;
        private int _consecutiveSlowFrames;
        private int _commandBatchSize = MobileCommandBatchSize;
        
        // ARM 优化字段
        private int _timeAccelerationFactorShift = 8;  // log2(256)

        public GpuContext(IRenderer renderer)
        {
            Renderer = renderer;
            GPFifo = new GPFifoDevice(this);
            Synchronization = new SynchronizationManager();
            Window = new Window(this);
            HostInitalized = new ManualResetEvent(false);
            _gpuReadyEvent = new ManualResetEvent(false);
            SyncActions = new List<ISyncActionHandler>();
            SyncpointActions = new List<ISyncActionHandler>();
            BufferMigrations = new List<BufferMigration>();
            DeferredActions = new Queue<Action>();
            PhysicalMemoryRegistry = new ConcurrentDictionary<ulong, PhysicalMemory>();
            SupportBufferUpdater = new SupportBufferUpdater(renderer);
            _firstTimestamp = ConvertNanosecondsToTicks((ulong)PerformanceCounter.ElapsedNanoseconds);
        }

        public GpuChannel CreateChannel()
        {
            return new GpuChannel(this);
        }

        public MemoryManager CreateMemoryManager(ulong pid, ulong cpuMemorySize)
        {
            if (!PhysicalMemoryRegistry.TryGetValue(pid, out var physicalMemory))
            {
                throw new ArgumentException("The PID is invalid or the process was not registered", nameof(pid));
            }

            return new MemoryManager(physicalMemory, cpuMemorySize);
        }

        public DeviceMemoryManager CreateDeviceMemoryManager(ulong pid)
        {
            if (!PhysicalMemoryRegistry.TryGetValue(pid, out var physicalMemory))
            {
                throw new ArgumentException("The PID is invalid or the process was not registered", nameof(pid));
            }

            return physicalMemory.CreateDeviceMemoryManager();
        }

        public void RegisterProcess(ulong pid, Cpu.IVirtualMemoryManagerTracked cpuMemory)
        {
            var physicalMemory = new PhysicalMemory(this, cpuMemory);
            if (!PhysicalMemoryRegistry.TryAdd(pid, physicalMemory))
            {
                throw new ArgumentException("The PID was already registered", nameof(pid));
            }

            physicalMemory.ShaderCache.ShaderCacheStateChanged += ShaderCacheStateUpdate;
        }

        public void UnregisterProcess(ulong pid)
        {
            if (PhysicalMemoryRegistry.TryRemove(pid, out var physicalMemory))
            {
                physicalMemory.ShaderCache.ShaderCacheStateChanged -= ShaderCacheStateUpdate;
                physicalMemory.Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ConvertNanosecondsToTicks(ulong nanoseconds)
        {
            // 使用优化的整数运算避免浮点数
            ulong divided = nanoseconds / NsToTicksFractionDenominator;
            ulong rounded = divided * NsToTicksFractionDenominator;
            ulong errorBias = (nanoseconds - rounded) * NsToTicksFractionNumerator / NsToTicksFractionDenominator;
            return divided * NsToTicksFractionNumerator + errorBias;
        }

        internal long GetModifiedSequence()
        {
            return _modifiedSequence++;
        }

        internal ulong GetTimestamp()
        {
            ulong nanoseconds = GetHighPrecisionNanoseconds();
            ulong ticks = ConvertNanosecondsToTicks(nanoseconds) - _firstTimestamp;

            if (GraphicsConfig.FastGpuTime)
            {
                // ARM 优化：使用位移代替除法
                AdjustTimeAcceleration();
                ticks >>= _timeAccelerationFactorShift;
            }

            return ticks;
        }

        private ulong GetHighPrecisionNanoseconds()
        {
            // 移动设备使用更高精度的计时器
            if (IsMobileDevice)
            {
                long timestamp = Stopwatch.GetTimestamp();
                return (ulong)(timestamp * (1_000_000_000.0 / Stopwatch.Frequency));
            }
            
            return (ulong)PerformanceCounter.ElapsedNanoseconds;
        }

        private bool IsMobileDevice
        {
            get
            {
                try
                {
                    // 检测移动设备
                    string osDescription = RuntimeInformation.OSDescription;
                    return osDescription.Contains("Android", StringComparison.OrdinalIgnoreCase) || 
                           osDescription.Contains("iOS", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        }

        private void AdjustTimeAcceleration()
        {
            // 目标帧时间 (60FPS ≈ 16.67ms)
            long targetFrameTime = (long)(Stopwatch.Frequency / 60.0);
            
            if (_lastFrameTime > targetFrameTime * 1.5) // 帧率下降
            {
                // 增加时间加速
                if (_timeAccelerationFactorShift < 10) // 最大1024 (2^10)
                {
                    _timeAccelerationFactorShift++;
                }
            }
            else if (_lastFrameTime < targetFrameTime * 0.8) // 帧率过高
            {
                // 减少时间加速
                if (_timeAccelerationFactorShift > 6) // 最小64 (2^6)
                {
                    _timeAccelerationFactorShift--;
                }
            }
        }

        private void ShaderCacheStateUpdate(ShaderCacheState state, int current, int total)
        {
            ShaderCacheStateChanged?.Invoke(state, current, total);
        }

        public void InitializeShaderCache(CancellationToken cancellationToken)
        {
            HostInitalized.WaitOne();

            foreach (var physicalMemory in PhysicalMemoryRegistry.Values)
            {
                physicalMemory.ShaderCache.Initialize(cancellationToken);
            }

            _gpuReadyEvent.Set();
        }

        public void WaitUntilGpuReady()
        {
            _gpuReadyEvent.WaitOne();
        }

        public void SetGpuThread()
        {
            _gpuThread = Thread.CurrentThread;
            Capabilities = Renderer.GetCapabilities();
            
            // 移动设备初始优化
            _commandBatchSize = MobileCommandBatchSize;
        }

        public bool IsGpuThread()
        {
            return _gpuThread == Thread.CurrentThread;
        }

        public void ProcessShaderCacheQueue()
        {
            foreach (var physicalMemory in PhysicalMemoryRegistry.Values)
            {
                physicalMemory.ShaderCache.ProcessShaderCacheQueue();
            }
        }

        internal void AdvanceSequence()
        {
            SequenceNumber++;
        }

        internal void RegisterBufferMigration(BufferMigration migration)
        {
            BufferMigrations.Add(migration);
            _pendingSync = true;
        }

        internal void RegisterSyncAction(ISyncActionHandler action, bool syncpointOnly = false)
        {
            if (syncpointOnly)
            {
                SyncpointActions.Add(action);
            }
            else
            {
                SyncActions.Add(action);
                _pendingSync = true;
            }
        }

        // 动态命令批处理大小调整
        internal int GetCommandBatchSize()
        {
            // 性能下降时减少批处理大小
            if (_consecutiveSlowFrames > 5 && _commandBatchSize > 10)
            {
                _commandBatchSize = Math.Max(10, _commandBatchSize - 5);
            }
            // 性能恢复时增加批处理大小
            else if (_consecutiveSlowFrames == 0 && _commandBatchSize < MaxCommandBufferSize)
            {
                _commandBatchSize = Math.Min(MaxCommandBufferSize, _commandBatchSize + 2);
            }
            
            return _commandBatchSize;
        }

        // 帧时间监控
        internal void ReportFrameTime(long frameTime)
        {
            _lastFrameTime = frameTime;
            
            // 目标帧时间 (60FPS ≈ 16.67ms)
            long targetFrameTime = (long)(Stopwatch.Frequency / 60.0);
            
            if (frameTime > targetFrameTime * 2) // 低于30FPS
            {
                _consecutiveSlowFrames++;
            }
            else
            {
                _consecutiveSlowFrames = Math.Max(0, _consecutiveSlowFrames - 1);
            }
        }

        internal void CreateHostSyncIfNeeded(HostSyncFlags flags)
        {
            bool syncpoint = flags.HasFlag(HostSyncFlags.Syncpoint);
            bool strict = flags.HasFlag(HostSyncFlags.Strict);
            bool force = flags.HasFlag(HostSyncFlags.Force);

            if (BufferMigrations.Count > 0)
            {
                ulong currentSyncNumber = Renderer.GetCurrentSync();

                for (int i = 0; i < BufferMigrations.Count; i++)
                {
                    BufferMigration migration = BufferMigrations[i];
                    long diff = (long)(currentSyncNumber - migration.SyncNumber);

                    if (diff >= 0)
                    {
                        migration.Dispose();
                        BufferMigrations.RemoveAt(i--);
                    }
                }
            }

            if (force || _pendingSync || (syncpoint && SyncpointActions.Count > 0))
            {
                foreach (var action in SyncActions)
                {
                    action.SyncPreAction(syncpoint);
                }

                foreach (var action in SyncpointActions)
                {
                    action.SyncPreAction(syncpoint);
                }

                Renderer.CreateSync(SyncNumber, strict);
                SyncNumber++;

                SyncActions.RemoveAll(action => action.SyncAction(syncpoint));
                SyncpointActions.RemoveAll(action => action.SyncAction(syncpoint));
            }

            _pendingSync = false;
        }

        internal void RunDeferredActions()
        {
            while (DeferredActions.TryDequeue(out Action action))
            {
                action();
            }
        }

        public void Dispose()
        {
            GPFifo.Dispose();
            HostInitalized.Dispose();
            _gpuReadyEvent.Dispose();

            foreach (var physicalMemory in PhysicalMemoryRegistry.Values)
            {
                physicalMemory.Dispose();
            }

            SupportBufferUpdater.Dispose();
            PhysicalMemoryRegistry.Clear();
            RunDeferredActions();
            Renderer.Dispose();
        }
    }
}
