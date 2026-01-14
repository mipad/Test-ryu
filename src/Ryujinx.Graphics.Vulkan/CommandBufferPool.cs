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
            }
        }

        public void AddTimelineSignal(Semaphore semaphore, ulong value)
        {
            if (!_supportsTimelineSemaphores || semaphore.Handle == 0)
            {
                return;
            }

            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        entry.TimelineSignals.Add(new TimelineSignal { Semaphore = semaphore, Value = value });
                    }
                }
            }
        }

        public void AddInUseTimelineSignal(Semaphore semaphore, ulong value)
        {
            if (!_supportsTimelineSemaphores || semaphore.Handle == 0)
            {
                return;
            }

            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse)
                    {
                        entry.TimelineSignals.Add(new TimelineSignal { Semaphore = semaphore, Value = value });
                    }
                }
            }
        }

        public void AddWaitTimelineSemaphore(Semaphore semaphore, ulong value, PipelineStageFlags stage = PipelineStageFlags.AllCommandsBit)
        {
            if (!_supportsTimelineSemaphores || semaphore.Handle == 0)
            {
                return;
            }

            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        entry.TimelineWaits.Add(new TimelineWait { Semaphore = semaphore, Value = value, Stage = stage });
                    }
                }
            }
        }

        public void AddInUseWaitTimelineSemaphore(Semaphore semaphore, ulong value, PipelineStageFlags stage = PipelineStageFlags.AllCommandsBit)
        {
            if (!_supportsTimelineSemaphores || semaphore.Handle == 0)
            {
                return;
            }

            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse)
                    {
                        entry.TimelineWaits.Add(new TimelineWait { Semaphore = semaphore, Value = value, Stage = stage });
                    }
                }
            }
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

                        _inUseCount++;

                        CommandBufferBeginInfo commandBufferBeginInfo = new()
                        {
                            SType = StructureType.CommandBufferBeginInfo,
                        };

                        _api.BeginCommandBuffer(entry.CommandBuffer, in commandBufferBeginInfo).ThrowOnError();

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

                CommandBuffer commandBuffer = entry.CommandBuffer;

                _api.EndCommandBuffer(commandBuffer).ThrowOnError();

                // 准备时间线信号量提交信息
                TimelineSemaphoreSubmitInfo timelineInfo = default;
                ulong* pSignalSemaphoreValues = null;
                ulong* pWaitSemaphoreValues = null;
                
                if (_supportsTimelineSemaphores && (entry.TimelineSignals.Count > 0 || entry.TimelineWaits.Count > 0))
                {
                    // 收集所有时间线信号量
                    var allSignalSemaphores = new List<Semaphore>();
                    var allSignalValues = new List<ulong>();
                    var allWaitSemaphores = new List<Semaphore>();
                    var allWaitValues = new List<ulong>();
                    var allWaitStages = new List<PipelineStageFlags>();

                    // 添加额外传入的信号量
                    if (!signalSemaphores.IsEmpty)
                    {
                        foreach (var semaphore in signalSemaphores)
                        {
                            allSignalSemaphores.Add(semaphore);
                            allSignalValues.Add(0); // 二进制信号量值为0
                        }
                    }

                    // 添加时间线信号
                    foreach (var timelineSignal in entry.TimelineSignals)
                    {
                        allSignalSemaphores.Add(timelineSignal.Semaphore);
                        allSignalValues.Add(timelineSignal.Value);
                    }

                    // 添加额外传入的等待信号量
                    if (!waitSemaphores.IsEmpty)
                    {
                        for (int i = 0; i < waitSemaphores.Length; i++)
                        {
                            allWaitSemaphores.Add(waitSemaphores[i]);
                            allWaitValues.Add(0); // 二进制信号量值为0
                            allWaitStages.Add(waitDstStageMask.IsEmpty ? PipelineStageFlags.AllCommandsBit : waitDstStageMask[i]);
                        }
                    }

                    // 添加时间线等待
                    foreach (var timelineWait in entry.TimelineWaits)
                    {
                        allWaitSemaphores.Add(timelineWait.Semaphore);
                        allWaitValues.Add(timelineWait.Value);
                        allWaitStages.Add(timelineWait.Stage);
                    }

                    // 分配内存
                    if (allSignalSemaphores.Count > 0)
                    {
                        // 修复：添加显式类型转换
                        pSignalSemaphoreValues = (ulong*)stackalloc ulong[allSignalSemaphores.Count];
                        for (int i = 0; i < allSignalValues.Count; i++)
                        {
                            pSignalSemaphoreValues[i] = allSignalValues[i];
                        }
                    }
                    
                    if (allWaitSemaphores.Count > 0)
                    {
                        // 修复：添加显式类型转换
                        pWaitSemaphoreValues = (ulong*)stackalloc ulong[allWaitSemaphores.Count];
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

                        lock (_queueLock)
                        {
                            _api.QueueSubmit(_queue, 1, in sInfo, entry.Fence.GetUnsafe()).ThrowOnError();
                        }
                    }
                }
                else
                {
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
            }
        }

        private void WaitAndDecrementRef(int cbIndex, bool refreshFence = true)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

            if (entry.InConsumption)
            {
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

            entry.Dependants.Clear();
            entry.Waitables.Clear();
            entry.TimelineSignals.Clear();
            entry.TimelineWaits.Clear();
            entry.Fence?.Dispose();

            if (refreshFence)
            {
                entry.Fence = new FenceHolder(_api, _device, _concurrentFenceWaitUnsupported);
            }
            else
            {
                entry.Fence = null;
            }
        }

        public unsafe void Dispose()
        {
            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                WaitAndDecrementRef(i, refreshFence: false);
            }

            _api.DestroyCommandPool(_device, _pool, null);
        }
    }
}