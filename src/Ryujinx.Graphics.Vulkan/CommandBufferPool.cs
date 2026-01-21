// CommandBufferPool.cs - 移除时间线信号量版本
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
        private readonly VulkanRenderer _renderer;
        
        // 当前活动的命令缓冲区索引
        private int _currentCommandBufferIndex = -1;

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
            }
        }

        private readonly ReservedCommandBuffer[] _commandBuffers;

        private readonly int[] _queuedIndexes;
        private int _queuedIndexesPtr;
        private int _queuedCount;
        private int _inUseCount;

        public CommandBufferPool(
            Vk api,
            Device device,
            Queue queue,
            object queueLock,
            uint queueFamilyIndex,
            bool concurrentFenceWaitUnsupported,
            bool isLight = false)
            : this(api, device, queue, queueLock, queueFamilyIndex, concurrentFenceWaitUnsupported, null, isLight)
        {
        }

        public CommandBufferPool(
            Vk api,
            Device device,
            Queue queue,
            object queueLock,
            uint queueFamilyIndex,
            bool concurrentFenceWaitUnsupported,
            VulkanRenderer renderer,
            bool isLight = false)
        {
            _api = api;
            _device = device;
            _queue = queue;
            _queueLock = queueLock;
            _concurrentFenceWaitUnsupported = concurrentFenceWaitUnsupported;
            _renderer = renderer;
            _owner = Thread.CurrentThread;

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"CommandBufferPool初始化: 轻量模式 = {isLight}");

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
                    $"返回命令缓冲区 {cbIndex}，提交次数={entry.SubmissionCount}");

                CommandBuffer commandBuffer = entry.CommandBuffer;

                _api.EndCommandBuffer(commandBuffer).ThrowOnError();
                
                // 传统提交方式（使用二进制信号量）
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

            entry.Dependants.Clear();
            entry.Waitables.Clear();
            entry.Fence?.Dispose();

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
    }
}