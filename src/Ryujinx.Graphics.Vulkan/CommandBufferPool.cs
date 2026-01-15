using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Ryujinx.Graphics.Vulkan
{
    class CommandBufferPool : IDisposable
    {
        public const int MaxCommandBuffers = 16;

        private readonly int _totalCommandBuffers;
        private readonly int _totalCommandBuffersMask;

        private readonly Vk _api;
        private readonly Device _device;
        private readonly Queue _queue;
        private readonly object _queueLock;
        private readonly bool _concurrentFenceWaitUnsupported;
        private readonly CommandPool _pool;
        private readonly Thread _owner;
        private readonly bool _supportsTimelineSemaphores;
        private readonly VulkanRenderer _renderer;
        
        // 用于跟踪已添加的时间线信号量值，避免重复添加
        private readonly Dictionary<int, HashSet<ulong>> _addedTimelineSignals = new();
        
        // 跟踪每个命令缓冲区已添加的时间线等待值
        private readonly Dictionary<int, HashSet<ulong>> _addedTimelineWaits = new();

        // 跟踪每个命令缓冲区的TimelineFenceHolder
        private readonly Dictionary<int, List<TimelineFenceHolder>> _timelineFenceHolders = new();

        // 批量信号量值队列
        private readonly Dictionary<int, List<ulong>> _pendingTimelineSignals = new();
        private readonly object _batchLock = new object();

        public bool OwnedByCurrentThread => _owner == Thread.CurrentThread;

        private struct ReservedCommandBuffer
        {
            public bool InUse;
            public bool InConsumption;
            public int SubmissionCount;
            public CommandBuffer CommandBuffer;
            public FenceHolder Fence;

            public List<IAuto> Dependants;
            public List<MultiFenceHolder> Waitables;
            public List<TimelineSignal> TimelineSignals;
            public List<TimelineWait> TimelineWaits;

            public void Initialize(Vk api, Device device, CommandPool pool)
            {
                CommandBufferAllocateInfo allocateInfo = new()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandBufferCount = 1,
                    CommandPool = pool,
                    Level = CommandBufferLevel.Primary,
                };

                api.AllocateCommandBuffers(device, in allocateInfo, out CommandBuffer);

                Dependants = new List<IAuto>();
                Waitables = new List<MultiFenceHolder>();
                TimelineSignals = new List<TimelineSignal>();
                TimelineWaits = new List<TimelineWait>();
            }
        }

        private struct TimelineSignal
        {
            public Semaphore Semaphore;
            public ulong Value;
        }

        private struct TimelineWait
        {
            public Semaphore Semaphore;
            public ulong Value;
            public PipelineStageFlags Stage;
        }

        private readonly ReservedCommandBuffer[] _commandBuffers;

        private readonly int[] _queuedIndexes;
        private int _queuedIndexesPtr;
        private int _queuedCount;
        private int _inUseCount;
        
        // 当前活动的命令缓冲区索引
        private int _currentCommandBufferIndex = -1;

        public unsafe CommandBufferPool(
            Vk api,
            Device device,
            Queue queue,
            object queueLock,
            uint queueFamilyIndex,
            bool concurrentFenceWaitUnsupported,
            bool isLight = false)
            : this(api, device, queue, queueLock, queueFamilyIndex, concurrentFenceWaitUnsupported, false, null, isLight)
        {
        }

        public unsafe CommandBufferPool(
            Vk api,
            Device device,
            Queue queue,
            object queueLock,
            uint queueFamilyIndex,
            bool concurrentFenceWaitUnsupported,
            bool supportsTimelineSemaphores,
            VulkanRenderer renderer,
            bool isLight = false)
        {
            _api = api;
            _device = device;
            _queue = queue;
            _queueLock = queueLock;
            _concurrentFenceWaitUnsupported = concurrentFenceWaitUnsupported;
            _supportsTimelineSemaphores = supportsTimelineSemaphores;
            _renderer = renderer;
            _owner = Thread.CurrentThread;

            // 初始化已添加信号量跟踪
            int bufferCount = isLight ? 2 : MaxCommandBuffers;
            for (int i = 0; i < bufferCount; i++)
            {
                _addedTimelineSignals[i] = new HashSet<ulong>();
                _addedTimelineWaits[i] = new HashSet<ulong>();
                _pendingTimelineSignals[i] = new List<ulong>();
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"CommandBufferPool初始化: 时间线信号量支持 = {_supportsTimelineSemaphores}, 轻量模式 = {isLight}");

            CommandPoolCreateInfo commandPoolCreateInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                Flags = CommandPoolCreateFlags.TransientBit |
                        CommandPoolCreateFlags.ResetCommandBufferBit,
            };

            api.CreateCommandPool(device, in commandPoolCreateInfo, null, out _pool).ThrowOnError();

            // We need at least 2 command buffers to get texture data in some cases.
            _totalCommandBuffers = isLight ? 2 : MaxCommandBuffers;
            _totalCommandBuffersMask = _totalCommandBuffers - 1;

            _commandBuffers = new ReservedCommandBuffer[_totalCommandBuffers];

            _queuedIndexes = new int[_totalCommandBuffers];
            _queuedIndexesPtr = 0;
            _queuedCount = 0;

            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                _commandBuffers[i].Initialize(api, device, _pool);
                WaitAndDecrementRef(i);
            }
            
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"CommandBufferPool初始化完成: 总共命令缓冲区数量 = {_totalCommandBuffers}");
        }

        public void AddDependant(int cbIndex, IAuto dependant)
        {
            dependant.IncrementReferenceCount();
            _commandBuffers[cbIndex].Dependants.Add(dependant);
        }

        public void AddWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddInUseWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddWaitable(int cbIndex, MultiFenceHolder waitable)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];
            if (waitable.AddFence(cbIndex, entry.Fence))
            {
                entry.Waitables.Add(waitable);
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"添加等待对象到命令缓冲区 {cbIndex}");
            }
        }

        // 添加TimelineFenceHolder支持
        public void AddTimelineFenceHolder(int cbIndex, TimelineFenceHolder holder)
        {
            lock (_commandBuffers)
            {
                if (!_timelineFenceHolders.ContainsKey(cbIndex))
                {
                    _timelineFenceHolders[cbIndex] = new List<TimelineFenceHolder>();
                }
                
                // 添加当前时间线信号量值到holder
                var currentSignals = _commandBuffers[cbIndex].TimelineSignals;
                if (currentSignals.Count > 0)
                {
                    ulong[] values = new ulong[currentSignals.Count];
                    for (int i = 0; i < currentSignals.Count; i++)
                    {
                        values[i] = currentSignals[i].Value;
                    }
                    holder.AddSignals(cbIndex, values);
                }
                
                _timelineFenceHolders[cbIndex].Add(holder);
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"添加TimelineFenceHolder到命令缓冲区 {cbIndex}，当前信号数量={currentSignals.Count}");
            }
        }

        public void AddInUseTimelineFenceHolder(TimelineFenceHolder holder)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse)
                    {
                        AddTimelineFenceHolder(i, holder);
                    }
                }
            }
        }

        // 批量添加时间线信号量
        public void AddTimelineSignals(Semaphore semaphore, ulong[] values)
        {
            if (!_supportsTimelineSemaphores || semaphore.Handle == 0 || values == null || values.Length == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"批量添加时间线信号失败: 不支持或信号量无效");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"批量添加时间线信号: 信号量={semaphore.Handle:X}, 数量={values.Length}");

            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        foreach (var value in values)
                        {
                            // 检查是否已经添加过相同的信号量值
                            if (_addedTimelineSignals.ContainsKey(i) && _addedTimelineSignals[i].Contains(value))
                            {
                                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                                    $"检测到重复的时间线信号量值: 命令缓冲区={i}, 值={value}，跳过添加");
                                continue;
                            }
                            
                            entry.TimelineSignals.Add(new TimelineSignal { Semaphore = semaphore, Value = value });
                            
                            // 记录已添加的信号量值
                            if (!_addedTimelineSignals.ContainsKey(i))
                            {
                                _addedTimelineSignals[i] = new HashSet<ulong>();
                            }
                            _addedTimelineSignals[i].Add(value);
                            
                            // 更新所有关联的TimelineFenceHolder
                            if (_timelineFenceHolders.ContainsKey(i))
                            {
                                foreach (var holder in _timelineFenceHolders[i])
                                {
                                    holder.AddSignal(i, value);
                                }
                            }
                        }
                        
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"批量添加时间线信号到命令缓冲区 {i}，数量={values.Length}");
                    }
                }
            }
        }

        public void AddTimelineSignal(Semaphore semaphore, ulong value)
        {
            AddTimelineSignals(semaphore, new ulong[] { value });
        }

        // 批量添加时间线信号到指定缓冲区
        public void AddTimelineSignalsToBuffer(int cbIndex, Semaphore semaphore, ulong[] values)
        {
            if (!_supportsTimelineSemaphores || semaphore.Handle == 0 || values == null || values.Length == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"批量添加时间线信号到缓冲区失败: 不支持或信号量无效");
                return;
            }

            if (cbIndex < 0 || cbIndex >= _totalCommandBuffers)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, 
                    $"批量添加时间线信号失败: 无效的命令缓冲区索引 {cbIndex}");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"批量添加时间线信号到缓冲区 {cbIndex}: 信号量={semaphore.Handle:X}, 数量={values.Length}");

            lock (_commandBuffers)
            {
                ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

                foreach (var value in values)
                {
                    // 检查是否已经添加过相同的信号量值
                    if (_addedTimelineSignals.ContainsKey(cbIndex) && _addedTimelineSignals[cbIndex].Contains(value))
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, 
                            $"检测到重复的时间线信号量值: 命令缓冲区={cbIndex}, 值={value}，跳过添加");
                        continue;
                    }
                    
                    entry.TimelineSignals.Add(new TimelineSignal { Semaphore = semaphore, Value = value });
                    
                    // 记录已添加的信号量值
                    if (!_addedTimelineSignals.ContainsKey(cbIndex))
                    {
                        _addedTimelineSignals[cbIndex] = new HashSet<ulong>();
                    }
                    _addedTimelineSignals[cbIndex].Add(value);
                    
                    // 更新所有关联的TimelineFenceHolder
                    if (_timelineFenceHolders.ContainsKey(cbIndex))
                    {
                        foreach (var holder in _timelineFenceHolders[cbIndex])
                        {
                            holder.AddSignal(cbIndex, value);
                        }
                    }
                }
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"批量添加时间线信号到指定命令缓冲区 {cbIndex}，数量={values.Length}");
            }
        }

        public void AddTimelineSignalToBuffer(int cbIndex, Semaphore semaphore, ulong value)
        {
            AddTimelineSignalsToBuffer(cbIndex, semaphore, new ulong[] { value });
        }

        // 批量添加待处理的时间线信号量（延迟提交）
        public void AddPendingTimelineSignals(int cbIndex, ulong[] values)
        {
            lock (_batchLock)
            {
                if (!_pendingTimelineSignals.ContainsKey(cbIndex))
                {
                    _pendingTimelineSignals[cbIndex] = new List<ulong>();
                }
                
                var pendingList = _pendingTimelineSignals[cbIndex];
                foreach (var value in values)
                {
                    // 确保不添加重复值
                    if (!pendingList.Contains(value))
                    {
                        pendingList.Add(value);
                    }
                }
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"添加待处理时间线信号到缓冲区 {cbIndex}，数量={values.Length}，总计={pendingList.Count}");
            }
        }

        // 提交所有待处理的时间线信号量
        public void FlushPendingTimelineSignals(int cbIndex, Semaphore semaphore)
        {
            lock (_batchLock)
            {
                if (!_pendingTimelineSignals.ContainsKey(cbIndex) || _pendingTimelineSignals[cbIndex].Count == 0)
                {
                    return;
                }
                
                var pendingList = _pendingTimelineSignals[cbIndex];
                ulong[] values = pendingList.ToArray();
                pendingList.Clear();
                
                // 批量添加到命令缓冲区
                AddTimelineSignalsToBuffer(cbIndex, semaphore, values);
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"刷新待处理时间线信号: 缓冲区={cbIndex}，数量={values.Length}");
            }
        }

        // 批量添加使用中的时间线信号量
        public void AddInUseTimelineSignals(Semaphore semaphore, ulong[] values)
        {
            if (!_supportsTimelineSemaphores || semaphore.Handle == 0 || values == null || values.Length == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"批量添加使用中时间线信号失败: 不支持或信号量无效");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"批量添加使用中时间线信号: 信号量={semaphore.Handle:X}, 数量={values.Length}");

            lock (_commandBuffers)
            {
                // 只添加到当前活动的命令缓冲区，而不是所有使用中的命令缓冲区
                if (_currentCommandBufferIndex >= 0 && _currentCommandBufferIndex < _totalCommandBuffers)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[_currentCommandBufferIndex];
                    
                    if (entry.InUse)
                    {
                        foreach (var value in values)
                        {
                            // 检查是否已经添加过相同的信号量值
                            if (_addedTimelineSignals.ContainsKey(_currentCommandBufferIndex) && 
                                _addedTimelineSignals[_currentCommandBufferIndex].Contains(value))
                            {
                                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                                    $"检测到重复的时间线信号量值（使用中）: 命令缓冲区={_currentCommandBufferIndex}, 值={value}，跳过添加");
                                continue;
                            }
                            
                            entry.TimelineSignals.Add(new TimelineSignal { Semaphore = semaphore, Value = value });
                            
                            // 记录已添加的信号量值
                            if (!_addedTimelineSignals.ContainsKey(_currentCommandBufferIndex))
                            {
                                _addedTimelineSignals[_currentCommandBufferIndex] = new HashSet<ulong>();
                            }
                            _addedTimelineSignals[_currentCommandBufferIndex].Add(value);
                            
                            // 更新所有关联的TimelineFenceHolder
                            if (_timelineFenceHolders.ContainsKey(_currentCommandBufferIndex))
                            {
                                foreach (var holder in _timelineFenceHolders[_currentCommandBufferIndex])
                                {
                                    holder.AddSignal(_currentCommandBufferIndex, value);
                                }
                            }
                        }
                        
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"批量添加时间线信号到当前使用中命令缓冲区 {_currentCommandBufferIndex}，数量={values.Length}");
                    }
                    else
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, 
                            $"当前命令缓冲区 {_currentCommandBufferIndex} 不在使用中");
                        // 回退到批量添加到所有使用中的命令缓冲区
                        AddInUseTimelineSignalsToAll(semaphore, values);
                    }
                }
                else
                {
                    // 回退：如果没有当前命令缓冲区，则添加到所有使用中的命令缓冲区
                    AddInUseTimelineSignalsToAll(semaphore, values);
                }
            }
        }

        // 批量添加到所有使用中的命令缓冲区（回退方法）
        private void AddInUseTimelineSignalsToAll(Semaphore semaphore, ulong[] values)
        {
            bool added = false;
            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                if (entry.InUse)
                {
                    foreach (var value in values)
                    {
                        // 检查是否已经添加过相同的信号量值
                        if (_addedTimelineSignals.ContainsKey(i) && _addedTimelineSignals[i].Contains(value))
                        {
                            Logger.Warning?.PrintMsg(LogClass.Gpu, 
                                $"检测到重复的时间线信号量值（使用中）: 命令缓冲区={i}, 值={value}，跳过添加");
                            continue;
                        }
                        
                        entry.TimelineSignals.Add(new TimelineSignal { Semaphore = semaphore, Value = value });
                        
                        // 记录已添加的信号量值
                        if (!_addedTimelineSignals.ContainsKey(i))
                        {
                            _addedTimelineSignals[i] = new HashSet<ulong>();
                        }
                        _addedTimelineSignals[i].Add(value);
                        
                        // 更新所有关联的TimelineFenceHolder
                        if (_timelineFenceHolders.ContainsKey(i))
                        {
                            foreach (var holder in _timelineFenceHolders[i])
                            {
                                holder.AddSignal(i, value);
                            }
                        }
                    }
                    
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"批量添加时间线信号到使用中命令缓冲区 {i} (回退)，数量={values.Length}");
                    added = true;
                    break; // 只添加到一个命令缓冲区
                }
            }
            
            if (!added)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                    $"未找到任何使用中的命令缓冲区，无法添加时间线信号");
            }
        }

        public void AddInUseTimelineSignal(Semaphore semaphore, ulong value)
        {
            AddInUseTimelineSignals(semaphore, new ulong[] { value });
        }

        // 批量添加时间线等待
        public void AddWaitTimelineSemaphores(Semaphore semaphore, ulong[] values, PipelineStageFlags stage = PipelineStageFlags.AllCommandsBit)
        {
            if (!_supportsTimelineSemaphores || semaphore.Handle == 0 || values == null || values.Length == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"批量添加时间线等待失败: 不支持或信号量无效");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"批量添加时间线等待: 信号量={semaphore.Handle:X}, 数量={values.Length}, 阶段={stage}");

            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        foreach (var value in values)
                        {
                            // 检查是否已经添加过相同的等待值
                            if (_addedTimelineWaits.ContainsKey(i) && _addedTimelineWaits[i].Contains(value))
                            {
                                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                                    $"检测到重复的时间线等待值: 命令缓冲区={i}, 值={value}，跳过添加");
                                continue;
                            }
                            
                            entry.TimelineWaits.Add(new TimelineWait { Semaphore = semaphore, Value = value, Stage = stage });
                            
                            // 记录已添加的等待值
                            if (!_addedTimelineWaits.ContainsKey(i))
                            {
                                _addedTimelineWaits[i] = new HashSet<ulong>();
                            }
                            _addedTimelineWaits[i].Add(value);
                        }
                        
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"批量添加时间线等待到命令缓冲区 {i}，数量={values.Length}");
                    }
                }
            }
        }

        public void AddWaitTimelineSemaphore(Semaphore semaphore, ulong value, PipelineStageFlags stage = PipelineStageFlags.AllCommandsBit)
        {
            AddWaitTimelineSemaphores(semaphore, new ulong[] { value }, stage);
        }

        // 批量添加使用中的时间线等待
        public void AddInUseWaitTimelineSemaphores(Semaphore semaphore, ulong[] values, PipelineStageFlags stage = PipelineStageFlags.AllCommandsBit)
        {
            if (!_supportsTimelineSemaphores || semaphore.Handle == 0 || values == null || values.Length == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"批量添加使用中时间线等待失败: 不支持或信号量无效");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"批量添加使用中时间线等待: 信号量={semaphore.Handle:X}, 数量={values.Length}, 阶段={stage}");

            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse)
                    {
                        foreach (var value in values)
                        {
                            // 检查是否已经添加过相同的等待值
                            if (_addedTimelineWaits.ContainsKey(i) && _addedTimelineWaits[i].Contains(value))
                            {
                                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                                    $"检测到重复的时间线等待值: 命令缓冲区={i}, 值={value}，跳过添加");
                                continue;
                            }
                            
                            entry.TimelineWaits.Add(new TimelineWait { Semaphore = semaphore, Value = value, Stage = stage });
                            
                            // 记录已添加的等待值
                            if (!_addedTimelineWaits.ContainsKey(i))
                            {
                                _addedTimelineWaits[i] = new HashSet<ulong>();
                            }
                            _addedTimelineWaits[i].Add(value);
                        }
                        
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"批量添加时间线等待到使用中命令缓冲区 {i}，数量={values.Length}");
                    }
                }
            }
        }

        public void AddInUseWaitTimelineSemaphore(Semaphore semaphore, ulong value, PipelineStageFlags stage = PipelineStageFlags.AllCommandsBit)
        {
            AddInUseWaitTimelineSemaphores(semaphore, new ulong[] { value }, stage);
        }

        public bool HasWaitableOnRentedCommandBuffer(MultiFenceHolder waitable, int offset, int size)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse &&
                        waitable.HasFence(i) &&
                        waitable.IsBufferRangeInUse(i, offset, size))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public bool IsFenceOnRentedCommandBuffer(FenceHolder fence)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse && entry.Fence == fence)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public FenceHolder GetFence(int cbIndex)
        {
            return _commandBuffers[cbIndex].Fence;
        }

        public int GetSubmissionCount(int cbIndex)
        {
            return _commandBuffers[cbIndex].SubmissionCount;
        }

        private int FreeConsumed(bool wait)
        {
            int freeEntry = 0;

            while (_queuedCount > 0)
            {
                int index = _queuedIndexes[_queuedIndexesPtr];

                ref ReservedCommandBuffer entry = ref _commandBuffers[index];

                if (wait || !entry.InConsumption || entry.Fence.IsSignaled())
                {
                    WaitAndDecrementRef(index);

                    wait = false;
                    freeEntry = index;

                    _queuedCount--;
                    _queuedIndexesPtr = (_queuedIndexesPtr + 1) % _totalCommandBuffers;
                }
                else
                {
                    break;
                }
            }

            return freeEntry;
        }

        public CommandBufferScoped ReturnAndRent(CommandBufferScoped cbs)
        {
            Return(cbs);
            return Rent();
        }

        public CommandBufferScoped Rent()
        {
            lock (_commandBuffers)
            {
                int cursor = FreeConsumed(_inUseCount + _queuedCount == _totalCommandBuffers);

                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[cursor];

                    if (!entry.InUse && !entry.InConsumption)
                    {
                        entry.InUse = true;
                        _currentCommandBufferIndex = cursor; // 更新当前命令缓冲区索引

                        _inUseCount++;

                        CommandBufferBeginInfo commandBufferBeginInfo = new()
                        {
                            SType = StructureType.CommandBufferBeginInfo,
                        };

                        _api.BeginCommandBuffer(entry.CommandBuffer, in commandBufferBeginInfo).ThrowOnError();

                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"租用命令缓冲区 {cursor}，当前使用中={_inUseCount}，排队中={_queuedCount}");

                        return new CommandBufferScoped(this, entry.CommandBuffer, cursor);
                    }

                    cursor = (cursor + 1) & _totalCommandBuffersMask;
                }
            }

            throw new InvalidOperationException($"Out of command buffers (In use: {_inUseCount}, queued: {_queuedCount}, total: {_totalCommandBuffers})");
        }

        public void Return(CommandBufferScoped cbs)
        {
            Return(cbs, null, null, null);
        }

        public unsafe void Return(
            CommandBufferScoped cbs,
            ReadOnlySpan<Semaphore> waitSemaphores,
            ReadOnlySpan<PipelineStageFlags> waitDstStageMask,
            ReadOnlySpan<Semaphore> signalSemaphores)
        {
            lock (_commandBuffers)
            {
                int cbIndex = cbs.CommandBufferIndex;

                ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

                Debug.Assert(entry.InUse);
                Debug.Assert(entry.CommandBuffer.Handle == cbs.CommandBuffer.Handle);
                entry.InUse = false;
                entry.InConsumption = true;
                entry.SubmissionCount++;
                _inUseCount--;
                
                // 清除当前命令缓冲区索引
                if (_currentCommandBufferIndex == cbIndex)
                {
                    _currentCommandBufferIndex = -1;
                }

                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"返回命令缓冲区 {cbIndex}，提交次数={entry.SubmissionCount}，时间线信号={entry.TimelineSignals.Count}，时间线等待={entry.TimelineWaits.Count}");

                CommandBuffer commandBuffer = entry.CommandBuffer;

                _api.EndCommandBuffer(commandBuffer).ThrowOnError();

                // 准备时间线信号量提交信息
                TimelineSemaphoreSubmitInfo timelineInfo = default;
                ulong* pSignalSemaphoreValues = null;
                ulong* pWaitSemaphoreValues = null;
                
                if (_supportsTimelineSemaphores && (entry.TimelineSignals.Count > 0 || entry.TimelineWaits.Count > 0))
                {
                    Logger.Info?.PrintMsg(LogClass.Gpu, 
                        $"使用时间线信号量提交: 信号数量={entry.TimelineSignals.Count}，等待数量={entry.TimelineWaits.Count}");
                    
                    // 收集所有时间线信号量
                    var allSignalSemaphores = new List<Semaphore>();
                    var allSignalValues = new List<ulong>();
                    var allWaitSemaphores = new List<Semaphore>();
                    var allWaitValues = new List<ulong>();
                    var allWaitStages = new List<PipelineStageFlags>();

                    // 添加额外传入的信号量
                    if (!signalSemaphores.IsEmpty)
                    {
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"额外信号量数量: {signalSemaphores.Length}");
                        foreach (var semaphore in signalSemaphores)
                        {
                            allSignalSemaphores.Add(semaphore);
                            allSignalValues.Add(0); // 二进制信号量值为0
                        }
                    }

                    // 添加时间线信号（去重检查）
                    HashSet<ulong> addedSignalValues = new();
                    foreach (var timelineSignal in entry.TimelineSignals)
                    {
                        // 防止同一个命令缓冲区中重复的时间线信号量值
                        if (addedSignalValues.Contains(timelineSignal.Value))
                        {
                            Logger.Warning?.PrintMsg(LogClass.Gpu, 
                                $"命令缓冲区 {cbIndex} 中检测到重复的时间线信号量值: {timelineSignal.Value}，跳过");
                            continue;
                        }
                        
                        allSignalSemaphores.Add(timelineSignal.Semaphore);
                        allSignalValues.Add(timelineSignal.Value);
                        addedSignalValues.Add(timelineSignal.Value);
                        
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"时间线信号: 信号量={timelineSignal.Semaphore.Handle:X}，值={timelineSignal.Value}");
                    }

                    // 添加额外传入的等待信号量
                    if (!waitSemaphores.IsEmpty)
                    {
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"额外等待信号量数量: {waitSemaphores.Length}");
                        for (int i = 0; i < waitSemaphores.Length; i++)
                        {
                            allWaitSemaphores.Add(waitSemaphores[i]);
                            allWaitValues.Add(0); // 二进制信号量值为0
                            allWaitStages.Add(waitDstStageMask.IsEmpty ? PipelineStageFlags.AllCommandsBit : waitDstStageMask[i]);
                        }
                    }

                    // 添加时间线等待（去重检查）
                    HashSet<ulong> addedWaitValues = new();
                    foreach (var timelineWait in entry.TimelineWaits)
                    {
                        // 防止同一个命令缓冲区中重复的时间线等待值
                        if (addedWaitValues.Contains(timelineWait.Value))
                        {
                            Logger.Warning?.PrintMsg(LogClass.Gpu, 
                                $"命令缓冲区 {cbIndex} 中检测到重复的时间线等待值: {timelineWait.Value}，跳过");
                            continue;
                        }
                        
                        allWaitSemaphores.Add(timelineWait.Semaphore);
                        allWaitValues.Add(timelineWait.Value);
                        allWaitStages.Add(timelineWait.Stage);
                        addedWaitValues.Add(timelineWait.Value);
                        
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"时间线等待: 信号量={timelineWait.Semaphore.Handle:X}，值={timelineWait.Value}，阶段={timelineWait.Stage}");
                    }

                    // 分配内存
                    if (allSignalSemaphores.Count > 0)
                    {
                        // 修复：stackalloc返回的就是指针类型，不需要转换
                        ulong* signalValues = stackalloc ulong[allSignalSemaphores.Count];
                        pSignalSemaphoreValues = signalValues;
                        for (int i = 0; i < allSignalValues.Count; i++)
                        {
                            pSignalSemaphoreValues[i] = allSignalValues[i];
                        }
                    }
                    
                    if (allWaitSemaphores.Count > 0)
                    {
                        // 修复：stackalloc返回的就是指针类型，不需要转换
                        ulong* waitValues = stackalloc ulong[allWaitSemaphores.Count];
                        pWaitSemaphoreValues = waitValues;
                        for (int i = 0; i < allWaitValues.Count; i++)
                        {
                            pWaitSemaphoreValues[i] = allWaitValues[i];
                        }
                    }

                    timelineInfo = new TimelineSemaphoreSubmitInfo
                    {
                        SType = StructureType.TimelineSemaphoreSubmitInfo,
                        WaitSemaphoreValueCount = (uint)allWaitSemaphores.Count,
                        PWaitSemaphoreValues = pWaitSemaphoreValues,
                        SignalSemaphoreValueCount = (uint)allSignalSemaphores.Count,
                        PSignalSemaphoreValues = pSignalSemaphoreValues,
                    };

                    // 提交
                    fixed (Semaphore* pWaitSemaphores = allWaitSemaphores.Count > 0 ? allWaitSemaphores.ToArray() : null)
                    fixed (Semaphore* pSignalSemaphores = allSignalSemaphores.Count > 0 ? allSignalSemaphores.ToArray() : null)
                    fixed (PipelineStageFlags* pWaitDstStageMask = allWaitStages.Count > 0 ? allWaitStages.ToArray() : null)
                    {
                        SubmitInfo sInfo = new()
                        {
                            SType = StructureType.SubmitInfo,
                            PNext = &timelineInfo,
                            WaitSemaphoreCount = (uint)allWaitSemaphores.Count,
                            PWaitSemaphores = pWaitSemaphores,
                            PWaitDstStageMask = pWaitDstStageMask,
                            CommandBufferCount = 1,
                            PCommandBuffers = &commandBuffer,
                            SignalSemaphoreCount = (uint)allSignalSemaphores.Count,
                            PSignalSemaphores = pSignalSemaphores,
                        };

                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"队列提交: 等待信号量={allWaitSemaphores.Count}，信号信号量={allSignalSemaphores.Count}");

                        lock (_queueLock)
                        {
                            _api.QueueSubmit(_queue, 1, in sInfo, entry.Fence.GetUnsafe()).ThrowOnError();
                        }
                    }
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.Gpu, 
                        $"使用传统二进制信号量提交");
                    
                    // 传统提交方式
                    fixed (Semaphore* pWaitSemaphores = waitSemaphores, pSignalSemaphores = signalSemaphores)
                    {
                        fixed (PipelineStageFlags* pWaitDstStageMask = waitDstStageMask)
                        {
                            SubmitInfo sInfo = new()
                            {
                                SType = StructureType.SubmitInfo,
                                WaitSemaphoreCount = !waitSemaphores.IsEmpty ? (uint)waitSemaphores.Length : 0,
                                PWaitSemaphores = pWaitSemaphores,
                                PWaitDstStageMask = pWaitDstStageMask,
                                CommandBufferCount = 1,
                                PCommandBuffers = &commandBuffer,
                                SignalSemaphoreCount = !signalSemaphores.IsEmpty ? (uint)signalSemaphores.Length : 0,
                                PSignalSemaphores = pSignalSemaphores,
                            };

                            lock (_queueLock)
                            {
                                _api.QueueSubmit(_queue, 1, in sInfo, entry.Fence.GetUnsafe()).ThrowOnError();
                            }
                        }
                    }
                }

                int ptr = (_queuedIndexesPtr + _queuedCount) % _totalCommandBuffers;
                _queuedIndexes[ptr] = cbIndex;
                _queuedCount++;
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"命令缓冲区 {cbIndex} 已排队，排队数量={_queuedCount}");
            }
        }

        private void WaitAndDecrementRef(int cbIndex, bool refreshFence = true)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"等待并释放命令缓冲区 {cbIndex} 的引用");

            if (entry.InConsumption)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"等待命令缓冲区 {cbIndex} 的栅栏");
                entry.Fence.Wait();
                entry.InConsumption = false;
            }

            foreach (IAuto dependant in entry.Dependants)
            {
                dependant.DecrementReferenceCount(cbIndex);
            }

            foreach (MultiFenceHolder waitable in entry.Waitables)
            {
                waitable.RemoveFence(cbIndex);
                waitable.RemoveBufferUses(cbIndex);
            }
            
            // 清理TimelineFenceHolder
            if (_timelineFenceHolders.ContainsKey(cbIndex))
            {
                foreach (var holder in _timelineFenceHolders[cbIndex])
                {
                    holder.RemoveSignals(cbIndex);
                }
                _timelineFenceHolders[cbIndex].Clear();
            }

            entry.Dependants.Clear();
            entry.Waitables.Clear();
            entry.TimelineSignals.Clear();
            entry.TimelineWaits.Clear();
            entry.Fence?.Dispose();

            // 清理已添加信号量值的跟踪
            if (_addedTimelineSignals.ContainsKey(cbIndex))
            {
                _addedTimelineSignals[cbIndex].Clear();
            }
            
            if (_addedTimelineWaits.ContainsKey(cbIndex))
            {
                _addedTimelineWaits[cbIndex].Clear();
            }
            
            // 清理待处理信号量队列
            lock (_batchLock)
            {
                if (_pendingTimelineSignals.ContainsKey(cbIndex))
                {
                    _pendingTimelineSignals[cbIndex].Clear();
                }
            }

            if (refreshFence)
            {
                entry.Fence = new FenceHolder(_api, _device, _concurrentFenceWaitUnsupported);
            }
            else
            {
                entry.Fence = null;
            }
            
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"命令缓冲区 {cbIndex} 清理完成");
        }

        public unsafe void Dispose()
        {
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"销毁CommandBufferPool");
            
            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                WaitAndDecrementRef(i, refreshFence: false);
            }

            _api.DestroyCommandPool(_device, _pool, null);
            
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"CommandBufferPool销毁完成");
        }
        
        // 获取当前命令缓冲区索引（用于调试）
        public int GetCurrentCommandBufferIndex()
        {
            return _currentCommandBufferIndex;
        }
        
        // 刷新所有待处理的批量信号
        public void FlushAllPendingSignals(Semaphore semaphore)
        {
            lock (_batchLock)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    FlushPendingTimelineSignals(i, semaphore);
                }
            }
        }
        
        // 获取待处理的信号量数量
        public int GetPendingSignalCount(int cbIndex)
        {
            lock (_batchLock)
            {
                if (_pendingTimelineSignals.ContainsKey(cbIndex))
                {
                    return _pendingTimelineSignals[cbIndex].Count;
                }
                return 0;
            }
        }
    }
}