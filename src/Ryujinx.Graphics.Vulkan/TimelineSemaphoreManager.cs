using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// 统一管理时间线信号量的值分配、提交和等待
    /// </summary>
    internal class TimelineSemaphoreManager : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly object _lock = new object();
        private ulong _nextValue = 1;
        private bool _disposed = false;

        // 用于跟踪待处理的信号
        private readonly List<PendingSignal> _pendingSignals = new List<PendingSignal>();
        private readonly List<PendingWait> _pendingWaits = new List<PendingWait>();

        // 记录每个命令缓冲区已添加的信号量和等待
        private readonly Dictionary<int, HashSet<ulong>> _cbSignalValues = new Dictionary<int, HashSet<ulong>>();
        private readonly Dictionary<int, HashSet<ulong>> _cbWaitValues = new Dictionary<int, HashSet<ulong>>();

        // 性能监控
        private long _totalSignals = 0;
        private long _totalWaits = 0;
        private Stopwatch _perfTimer = Stopwatch.StartNew();

        public ulong CurrentValue { get; private set; } = 0;

        private struct PendingSignal
        {
            public ulong Value;
            public bool IsStrict;
            public Action OnComplete;
            public string DebugInfo;
        }

        private struct PendingWait
        {
            public ulong Value;
            public PipelineStageFlags Stage;
            public Action OnComplete;
            public string DebugInfo;
        }

        public TimelineSemaphoreManager(VulkanRenderer renderer)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"TimelineSemaphoreManager初始化: 支持时间线信号量 = {_renderer.SupportsTimelineSemaphores}");
        }

        /// <summary>
        /// 获取下一个单调递增的时间线值
        /// </summary>
        public ulong GetNextValue()
        {
            lock (_lock)
            {
                ulong value = _nextValue++;
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"获取下一个时间线值: {value} (前一个: {value - 1})");
                return value;
            }
        }

        /// <summary>
        /// 预定一个未来的时间线信号（批处理提交）
        /// </summary>
        public ulong ScheduleSignal(bool isStrict = false, string debugInfo = null)
        {
            if (!_renderer.SupportsTimelineSemaphores || _renderer.TimelineSemaphore.Handle == 0)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                    $"时间线信号量不支持，无法预定信号");
                return 0;
            }

            ulong value = GetNextValue();
            
            lock (_lock)
            {
                _pendingSignals.Add(new PendingSignal
                {
                    Value = value,
                    IsStrict = isStrict,
                    DebugInfo = debugInfo
                });
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"预定时间线信号: 值={value}, 严格模式={isStrict}, 调试信息={debugInfo}");
            }
            
            return value;
        }

        /// <summary>
        /// 预定一个未来的时间线等待
        /// </summary>
        public void ScheduleWait(ulong value, PipelineStageFlags stage = PipelineStageFlags.AllCommandsBit, string debugInfo = null)
        {
            if (!_renderer.SupportsTimelineSemaphores || _renderer.TimelineSemaphore.Handle == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"时间线信号量不支持，跳过等待预定");
                return;
            }

            if (value == 0)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                    $"无效的时间线等待值: 0");
                return;
            }

            lock (_lock)
            {
                _pendingWaits.Add(new PendingWait
                {
                    Value = value,
                    Stage = stage,
                    DebugInfo = debugInfo
                });
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"预定时间线等待: 值={value}, 阶段={stage}, 调试信息={debugInfo}");
            }
        }

        /// <summary>
        /// 立即添加信号量到当前命令缓冲区（用于严格模式）
        /// </summary>
        public bool AddSignalImmediate(ulong value, bool flushCommands = true, string debugInfo = null)
        {
            if (!_renderer.SupportsTimelineSemaphores || _renderer.TimelineSemaphore.Handle == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"时间线信号量不支持，跳过立即信号");
                return false;
            }

            if (value == 0)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                    $"无效的时间线信号值: 0");
                return false;
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"添加立即时间线信号: 值={value}, 刷新命令={flushCommands}, 调试信息={debugInfo}");

            try
            {
                // 如果需要刷新命令，先刷新
                if (flushCommands)
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"立即信号: 刷新所有命令");
                    _renderer.FlushAllCommands();
                }

                // 添加到当前命令缓冲区
                int cbIndex = _renderer.GetCurrentCommandBufferIndex();
                if (cbIndex >= 0)
                {
                    // 检查是否重复添加
                    if (_cbSignalValues.TryGetValue(cbIndex, out var signalSet) && signalSet.Contains(value))
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, 
                            $"命令缓冲区 {cbIndex} 已包含时间线信号值 {value}，跳过");
                        return true;
                    }

                    // 记录到命令缓冲区
                    _renderer.CommandBufferPool.AddTimelineSignalToBuffer(
                        cbIndex, 
                        _renderer.TimelineSemaphore, 
                        value
                    );

                    // 更新跟踪
                    if (!_cbSignalValues.ContainsKey(cbIndex))
                        _cbSignalValues[cbIndex] = new HashSet<ulong>();
                    _cbSignalValues[cbIndex].Add(value);

                    _totalSignals++;
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"立即时间线信号 {value} 已添加到命令缓冲区 {cbIndex}");
                }
                else
                {
                    // 回退：添加到所有使用中的命令缓冲区
                    _renderer.CommandBufferPool.AddInUseTimelineSignal(
                        _renderer.TimelineSemaphore, 
                        value
                    );
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"立即时间线信号 {value} 已添加到使用中的命令缓冲区（回退）");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, 
                    $"添加立即时间线信号失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 处理所有待定的信号和等待（批处理）
        /// </summary>
        public void ProcessPending(bool flushBeforeSignals = false)
        {
            if (!_renderer.SupportsTimelineSemaphores || _renderer.TimelineSemaphore.Handle == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"时间线信号量不支持，跳过处理待定项");
                return;
            }

            List<PendingSignal> signalsToProcess;
            List<PendingWait> waitsToProcess;

            lock (_lock)
            {
                if (_pendingSignals.Count == 0 && _pendingWaits.Count == 0)
                {
                    return;
                }

                signalsToProcess = new List<PendingSignal>(_pendingSignals);
                waitsToProcess = new List<PendingWait>(_pendingWaits);
                
                _pendingSignals.Clear();
                _pendingWaits.Clear();
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"处理待定时间线操作: {signalsToProcess.Count} 个信号, {waitsToProcess.Count} 个等待");

            // 如果有任何严格模式信号，需要先刷新
            bool hasStrictSignal = signalsToProcess.Exists(s => s.IsStrict);
            if (hasStrictSignal || flushBeforeSignals)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"有严格模式信号，刷新所有命令");
                _renderer.FlushAllCommands();
            }

            try
            {
                // 获取当前命令缓冲区
                var cbs = _renderer.CommandBufferPool.Rent();
                int cbIndex = cbs.CommandBufferIndex;

                // 处理等待
                foreach (var wait in waitsToProcess)
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"添加时间线等待到命令缓冲区 {cbIndex}: 值={wait.Value}, 阶段={wait.Stage}");
                    
                    // 检查重复
                    if (_cbWaitValues.TryGetValue(cbIndex, out var waitSet) && waitSet.Contains(wait.Value))
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, 
                            $"命令缓冲区 {cbIndex} 已包含时间线等待值 {wait.Value}，跳过");
                        continue;
                    }

                    _renderer.CommandBufferPool.AddWaitTimelineSemaphore(
                        _renderer.TimelineSemaphore, 
                        wait.Value, 
                        wait.Stage
                    );

                    // 更新跟踪
                    if (!_cbWaitValues.ContainsKey(cbIndex))
                        _cbWaitValues[cbIndex] = new HashSet<ulong>();
                    _cbWaitValues[cbIndex].Add(wait.Value);

                    _totalWaits++;
                    wait.OnComplete?.Invoke();
                }

                // 处理信号
                foreach (var signal in signalsToProcess)
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"添加时间线信号到命令缓冲区 {cbIndex}: 值={signal.Value}, 严格模式={signal.IsStrict}");
                    
                    // 检查重复
                    if (_cbSignalValues.TryGetValue(cbIndex, out var signalSet) && signalSet.Contains(signal.Value))
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, 
                            $"命令缓冲区 {cbIndex} 已包含时间线信号值 {signal.Value}，跳过");
                        continue;
                    }

                    _renderer.CommandBufferPool.AddTimelineSignalToBuffer(
                        cbIndex,
                        _renderer.TimelineSemaphore,
                        signal.Value
                    );

                    // 更新跟踪
                    if (!_cbSignalValues.ContainsKey(cbIndex))
                        _cbSignalValues[cbIndex] = new HashSet<ulong>();
                    _cbSignalValues[cbIndex].Add(signal.Value);

                    _totalSignals++;
                    signal.OnComplete?.Invoke();
                }

                // 提交命令缓冲区
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"提交批处理命令缓冲区 {cbIndex}");
                _renderer.CommandBufferPool.Return(cbs);
                
                // 更新当前值
                if (signalsToProcess.Count > 0)
                {
                    var maxSignal = signalsToProcess.Max(s => s.Value);
                    CurrentValue = Math.Max(CurrentValue, maxSignal);
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"当前时间线值更新为: {CurrentValue}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, 
                    $"处理待定时间线操作失败: {ex.Message}");
                
                // 重新添加未处理的项目
                lock (_lock)
                {
                    _pendingSignals.AddRange(signalsToProcess);
                    _pendingWaits.AddRange(waitsToProcess);
                }
                throw;
            }
        }

        /// <summary>
        /// 等待时间线信号量达到指定值
        /// </summary>
        public unsafe bool WaitForValue(ulong value, ulong timeout = 1000000000)
        {
            if (!_renderer.SupportsTimelineSemaphores || _renderer.TimelineSemaphore.Handle == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"时间线信号量不支持，跳过等待");
                return false;
            }

            if (value == 0)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                    $"无效的等待值: 0");
                return true;
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"等待时间线信号量值: {value}, 超时={timeout}ns");

            try
            {
                var semaphore = _renderer.TimelineSemaphore;
                var waitInfo = new SemaphoreWaitInfo
                {
                    SType = StructureType.SemaphoreWaitInfo,
                    SemaphoreCount = 1,
                    PSemaphores = &semaphore,
                    PValues = &value
                };

                var result = _renderer.TimelineSemaphoreApi.WaitSemaphores(
                    _renderer.Device, 
                    &waitInfo, 
                    timeout
                );

                bool success = result == Result.Success;
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"等待结果: {result}, 成功={success}");

                if (success)
                {
                    CurrentValue = Math.Max(CurrentValue, value);
                }

                return success;
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, 
                    $"等待时间线信号量失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取当前时间线信号量的值
        /// </summary>
        public unsafe ulong GetCurrentValue()
        {
            if (!_renderer.SupportsTimelineSemaphores || _renderer.TimelineSemaphore.Handle == 0)
            {
                return 0;
            }

            try
            {
                ulong currentValue;
                var semaphore = _renderer.TimelineSemaphore;
                _renderer.TimelineSemaphoreApi.GetSemaphoreCounterValue(
                    _renderer.Device, 
                    semaphore, 
                    &currentValue
                );
                
                CurrentValue = currentValue;
                return currentValue;
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, 
                    $"获取时间线信号量当前值失败: {ex.Message}");
                return CurrentValue;
            }
        }

        /// <summary>
        /// 清理指定命令缓冲区的跟踪数据
        /// </summary>
        public void ClearCommandBufferTracking(int cbIndex)
        {
            lock (_lock)
            {
                if (_cbSignalValues.ContainsKey(cbIndex))
                {
                    _cbSignalValues[cbIndex].Clear();
                }
                
                if (_cbWaitValues.ContainsKey(cbIndex))
                {
                    _cbWaitValues[cbIndex].Clear();
                }
            }
            
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"清理命令缓冲区 {cbIndex} 的时间线跟踪数据");
        }

        /// <summary>
        /// 获取性能统计信息
        /// </summary>
        public (long TotalSignals, long TotalWaits, TimeSpan Uptime) GetStatistics()
        {
            lock (_lock)
            {
                return (_totalSignals, _totalWaits, _perfTimer.Elapsed);
            }
        }

        /// <summary>
        /// 重置性能计数器
        /// </summary>
        public void ResetStatistics()
        {
            lock (_lock)
            {
                _totalSignals = 0;
                _totalWaits = 0;
                _perfTimer.Restart();
            }
            
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"重置时间线信号量性能计数器");
        }

        /// <summary>
        /// 验证时间线值的单调性
        /// </summary>
        public bool ValidateMonotonic(int cbIndex, ulong newValue, bool isSignal)
        {
            if (newValue == 0)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                    $"验证失败: 时间线值不能为0");
                return false;
            }

            lock (_lock)
            {
                if (isSignal)
                {
                    if (_cbSignalValues.TryGetValue(cbIndex, out var signalSet) && signalSet.Count > 0)
                    {
                        ulong maxValue = signalSet.Max();
                        if (newValue <= maxValue)
                        {
                            Logger.Error?.PrintMsg(LogClass.Gpu, 
                                $"时间线信号值单调性验证失败: 新值={newValue}, 最大值={maxValue}");
                            return false;
                        }
                    }
                }
                else
                {
                    if (_cbWaitValues.TryGetValue(cbIndex, out var waitSet) && waitSet.Count > 0)
                    {
                        ulong maxValue = waitSet.Max();
                        if (newValue <= maxValue)
                        {
                            Logger.Error?.PrintMsg(LogClass.Gpu, 
                                $"时间线等待值单调性验证失败: 新值={newValue}, 最大值={maxValue}");
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 获取下一个值但不递增（用于查看）
        /// </summary>
        public ulong PeekNextValue()
        {
            lock (_lock)
            {
                return _nextValue;
            }
        }

        /// <summary>
        /// 强制设置下一个值（谨慎使用）
        /// </summary>
        public void SetNextValue(ulong value)
        {
            lock (_lock)
            {
                if (value < _nextValue)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, 
                        $"强制设置时间线值可能破坏单调性: 新值={value}, 当前值={_nextValue}");
                }
                _nextValue = value;
                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"强制设置时间线值为: {value}");
            }
        }

        /// <summary>
        /// 获取待处理信号和等待的数量
        /// </summary>
        public (int PendingSignals, int PendingWaits) GetPendingCounts()
        {
            lock (_lock)
            {
                return (_pendingSignals.Count, _pendingWaits.Count);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"销毁TimelineSemaphoreManager");

            lock (_lock)
            {
                _pendingSignals.Clear();
                _pendingWaits.Clear();
                _cbSignalValues.Clear();
                _cbWaitValues.Clear();
                
                // 输出最终统计
                var stats = GetStatistics();
                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"时间线信号量最终统计: 信号={stats.TotalSignals}, 等待={stats.TotalWaits}, 运行时间={stats.Uptime}");
            }

            _disposed = true;
        }
    }
}