using Silk.NET.Vulkan;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Vulkan
{
    class FenceHolder : IDisposable
    {
        private readonly Vk _api;
        private readonly Device _device;
        private Fence _fence;
        private int _referenceCount;
        private int _lock;
        private readonly bool _concurrentWaitUnsupported;
        private bool _disposed;

        // 添加诊断信息
        private readonly long _creationTime;
        private long _lastWaitStartTime;
        private int _waitCount;
        private long _totalWaitTicks;
        private int _asyncWaitCount;

        public unsafe FenceHolder(Vk api, Device device, bool concurrentWaitUnsupported)
        {
            _api = api;
            _device = device;
            _concurrentWaitUnsupported = concurrentWaitUnsupported;

            FenceCreateInfo fenceCreateInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
            };

            api.CreateFence(device, in fenceCreateInfo, null, out _fence).ThrowOnError();

            _referenceCount = 1;
            _creationTime = Stopwatch.GetTimestamp();
        }

        public Fence GetUnsafe()
        {
            return _fence;
        }

        public bool TryGet(out Fence fence)
        {
            // 使用MemoryBarrier确保读取顺序
            Thread.MemoryBarrier();
            
            int lastValue;
            do
            {
                lastValue = Volatile.Read(ref _referenceCount);

                if (lastValue == 0)
                {
                    fence = default;
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _referenceCount, lastValue + 1, lastValue) != lastValue);

            Thread.MemoryBarrier();
            
            if (_concurrentWaitUnsupported)
            {
                AcquireLock();
            }

            fence = _fence;
            return true;
        }

        public Fence Get()
        {
            Interlocked.Increment(ref _referenceCount);
            return _fence;
        }

        public void PutLock()
        {
            Put();

            if (_concurrentWaitUnsupported)
            {
                ReleaseLock();
            }
        }

        public void Put()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0)
            {
                _api.DestroyFence(_device, _fence, Span<AllocationCallbacks>.Empty);
                _fence = default;
            }
        }

        private void AcquireLock()
        {
            // 改进：使用指数退避策略减少CPU占用
            int spinCount = 0;
            while (!TryAcquireLock())
            {
                Thread.SpinWait(Math.Min(32 * (1 << spinCount), 1024));
                spinCount = Math.Min(spinCount + 1, 5); // 最大退避到1024次自旋
            }
        }

        private bool TryAcquireLock()
        {
            return Interlocked.Exchange(ref _lock, 1) == 0;
        }

        private void ReleaseLock()
        {
            Interlocked.Exchange(ref _lock, 0);
        }

        public void Wait()
        {
            _waitCount++;
            _lastWaitStartTime = Stopwatch.GetTimestamp();
            
            try
            {
                if (_concurrentWaitUnsupported)
                {
                    AcquireLock();

                    try
                    {
                        FenceHelper.WaitAllIndefinitely(_api, _device, stackalloc Fence[] { _fence });
                    }
                    finally
                    {
                        ReleaseLock();
                    }
                }
                else
                {
                    FenceHelper.WaitAllIndefinitely(_api, _device, stackalloc Fence[] { _fence });
                }
            }
            finally
            {
                _totalWaitTicks += Stopwatch.GetTimestamp() - _lastWaitStartTime;
            }
        }

        /// <summary>
        /// 异步等待栅栏信号
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>表示异步等待的任务</returns>
        public async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asyncWaitCount);
            _lastWaitStartTime = Stopwatch.GetTimestamp();
            
            try
            {
                if (_concurrentWaitUnsupported)
                {
                    if (!TryAcquireLock())
                    {
                        // 无法立即获取锁，使用异步等待
                        await WaitWithRetryAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    
                    try
                    {
                        await WaitFenceAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ReleaseLock();
                    }
                }
                else
                {
                    await WaitFenceAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _totalWaitTicks += Stopwatch.GetTimestamp() - _lastWaitStartTime;
            }
        }

        /// <summary>
        /// 带超时的等待
        /// </summary>
        /// <param name="timeout">超时时间（纳秒）</param>
        /// <returns>是否在超时前收到信号</returns>
        public bool Wait(ulong timeout)
        {
            _waitCount++;
            _lastWaitStartTime = Stopwatch.GetTimestamp();
            
            try
            {
                if (_concurrentWaitUnsupported)
                {
                    if (!TryAcquireLock())
                    {
                        // 无法获取锁，返回超时
                        return false;
                    }

                    try
                    {
                        return FenceHelper.AllSignaled(_api, _device, stackalloc Fence[] { _fence }, timeout);
                    }
                    finally
                    {
                        ReleaseLock();
                    }
                }
                else
                {
                    return FenceHelper.AllSignaled(_api, _device, stackalloc Fence[] { _fence }, timeout);
                }
            }
            finally
            {
                _totalWaitTicks += Stopwatch.GetTimestamp() - _lastWaitStartTime;
            }
        }

        /// <summary>
        /// 异步带超时的等待
        /// </summary>
        /// <param name="timeout">超时时间（纳秒）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>是否在超时前收到信号</returns>
        public async Task<bool> WaitAsync(ulong timeout, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asyncWaitCount);
            _lastWaitStartTime = Stopwatch.GetTimestamp();
            
            try
            {
                if (_concurrentWaitUnsupported)
                {
                    if (!TryAcquireLock())
                    {
                        // 无法立即获取锁，使用带超时的异步等待
                        return await WaitWithRetryAsync(timeout, cancellationToken).ConfigureAwait(false);
                    }

                    try
                    {
                        return await WaitFenceAsync(timeout, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ReleaseLock();
                    }
                }
                else
                {
                    return await WaitFenceAsync(timeout, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _totalWaitTicks += Stopwatch.GetTimestamp() - _lastWaitStartTime;
            }
        }

        private async Task WaitFenceAsync(CancellationToken cancellationToken)
        {
            // 使用异步轮询代替阻塞等待
            while (!IsSignaled())
            {
                // 检查取消令牌
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new TaskCanceledException();
                }

                // 使用短暂的延迟避免CPU占用过高
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<bool> WaitFenceAsync(ulong timeout, CancellationToken cancellationToken)
        {
            // 使用异步轮询实现带超时的等待
            long startTime = Stopwatch.GetTimestamp();
            long timeoutTicks = (long)(timeout * Stopwatch.Frequency / 1_000_000_000L); // 纳秒转换为ticks
            
            while (!IsSignaled())
            {
                // 检查取消令牌
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new TaskCanceledException();
                }

                // 检查超时
                long elapsedTicks = Stopwatch.GetTimestamp() - startTime;
                if (elapsedTicks >= timeoutTicks)
                {
                    return false;
                }

                // 计算剩余时间并等待
                long remainingTicks = timeoutTicks - elapsedTicks;
                long remainingMs = Math.Max(1, remainingTicks * 1000 / Stopwatch.Frequency);
                
                await Task.Delay((int)remainingMs, cancellationToken).ConfigureAwait(false);
            }
            
            return true;
        }

        private async Task WaitWithRetryAsync(CancellationToken cancellationToken)
        {
            // 重试获取锁并等待
            while (!cancellationToken.IsCancellationRequested)
            {
                if (TryAcquireLock())
                {
                    try
                    {
                        await WaitFenceAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    finally
                    {
                        ReleaseLock();
                    }
                }
                
                // 短暂延迟后重试
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            
            throw new TaskCanceledException();
        }

        private async Task<bool> WaitWithRetryAsync(ulong timeout, CancellationToken cancellationToken)
        {
            // 重试获取锁并等待（带超时）
            long startTime = Stopwatch.GetTimestamp();
            long timeoutTicks = (long)(timeout * Stopwatch.Frequency / 1_000_000_000L);
            
            while (!cancellationToken.IsCancellationRequested)
            {
                // 检查超时
                long elapsedTicks = Stopwatch.GetTimestamp() - startTime;
                if (elapsedTicks >= timeoutTicks)
                {
                    return false;
                }
                
                if (TryAcquireLock())
                {
                    try
                    {
                        // 计算剩余时间
                        long remainingTicks = timeoutTicks - elapsedTicks;
                        ulong remainingNs = (ulong)(remainingTicks * 1_000_000_000L / Stopwatch.Frequency);
                        
                        return await WaitFenceAsync(remainingNs, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ReleaseLock();
                    }
                }
                
                // 短暂延迟后重试
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            
            throw new TaskCanceledException();
        }

        /// <summary>
        /// 批量等待多个栅栏
        /// </summary>
        /// <param name="fences">栅栏数组</param>
        /// <param name="timeout">超时时间（纳秒）</param>
        /// <returns>是否所有栅栏都在超时前收到信号</returns>
        public static bool WaitMultiple(FenceHolder[] fences, ulong timeout = 0)
        {
            if (fences.Length == 0) return true;
            
            // 收集所有栅栏句柄
            Span<Fence> fenceHandles = fences.Length <= 64 ? stackalloc Fence[fences.Length] : new Fence[fences.Length];
            for (int i = 0; i < fences.Length; i++)
            {
                fenceHandles[i] = fences[i]._fence;
            }
            
            // 使用Vulkan的多栅栏等待API
            return FenceHelper.AllSignaled(
                fences[0]._api, 
                fences[0]._device, 
                fenceHandles, 
                timeout);
        }

        /// <summary>
        /// 异步批量等待多个栅栏
        /// </summary>
        /// <param name="fences">栅栏数组</param>
        /// <param name="timeout">超时时间（纳秒）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>是否所有栅栏都在超时前收到信号</returns>
        public static async Task<bool> WaitMultipleAsync(FenceHolder[] fences, ulong timeout = 0, CancellationToken cancellationToken = default)
        {
            if (fences.Length == 0) return true;
            
            // 检查是否所有栅栏都已经收到信号
            foreach (var fence in fences)
            {
                if (fence.IsSignaled())
                {
                    continue;
                }
                
                // 如果有栅栏未收到信号，使用异步等待
                break;
            }
            
            // 如果所有栅栏都已收到信号，立即返回
            if (timeout == 0)
            {
                // 无超时，等待所有栅栏
                Task[] waitTasks = new Task[fences.Length];
                for (int i = 0; i < fences.Length; i++)
                {
                    waitTasks[i] = fences[i].WaitAsync(cancellationToken);
                }
                
                try
                {
                    await Task.WhenAll(waitTasks).ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                // 带超时等待
                Task<bool>[] waitTasks = new Task<bool>[fences.Length];
                for (int i = 0; i < fences.Length; i++)
                {
                    waitTasks[i] = fences[i].WaitAsync(timeout, cancellationToken);
                }
                
                try
                {
                    var results = await Task.WhenAll(waitTasks).ConfigureAwait(false);
                    foreach (var result in results)
                    {
                        if (!result) return false;
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool IsSignaled()
        {
            if (_concurrentWaitUnsupported)
            {
                if (!TryAcquireLock())
                {
                    return false;
                }

                try
                {
                    return FenceHelper.AllSignaled(_api, _device, stackalloc Fence[] { _fence });
                }
                finally
                {
                    ReleaseLock();
                }
            }
            else
            {
                return FenceHelper.AllSignaled(_api, _device, stackalloc Fence[] { _fence });
            }
        }

        /// <summary>
        /// 重置栅栏（如果已触发）
        /// </summary>
        /// <returns>是否成功重置</returns>
        public unsafe bool Reset()
        {
            if (_concurrentWaitUnsupported)
            {
                if (!TryAcquireLock())
                {
                    return false;
                }
            }
            
            try
            {
                // 检查栅栏是否已触发
                if (FenceHelper.AllSignaled(_api, _device, stackalloc Fence[] { _fence }, 0))
                {
                    // 重置栅栏
                    _api.ResetFences(_device, 1, stackalloc Fence[] { _fence });
                    return true;
                }
                return false;
            }
            finally
            {
                if (_concurrentWaitUnsupported)
                {
                    ReleaseLock();
                }
            }
        }

        /// <summary>
        /// 获取诊断信息
        /// </summary>
        /// <returns>诊断信息</returns>
        public FenceDiagnostics GetDiagnostics()
        {
            return new FenceDiagnostics
            {
                ReferenceCount = _referenceCount,
                WaitCount = _waitCount,
                AsyncWaitCount = _asyncWaitCount,
                TotalWaitTimeMs = _totalWaitTicks * 1000 / Stopwatch.Frequency,
                AgeMs = (Stopwatch.GetTimestamp() - _creationTime) * 1000 / Stopwatch.Frequency,
                IsSignaled = IsSignaled(),
                LockStatus = _lock != 0
            };
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
                    // 释放托管资源（当前没有托管资源需要释放）
                }
                
                // 确保栅栏被正确销毁
                if (_fence.Handle != default)
                {
                    // 确保没有等待中的操作
                    int retryCount = 0;
                    while (_lock != 0 && retryCount < 10) // 最多重试10次
                    {
                        Thread.Yield();
                        retryCount++;
                    }
                    
                    if (_lock == 0 || retryCount >= 10) // 如果锁被释放或超时
                    {
                        try
                        {
                            _api.DestroyFence(_device, _fence, Span<AllocationCallbacks>.Empty);
                        }
                        catch
                        {
                            // 忽略销毁异常
                        }
                    }
                    
                    _fence = default;
                }
                
                _disposed = true;
            }
        }

        ~FenceHolder()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// 栅栏诊断信息
    /// </summary>
    public struct FenceDiagnostics
    {
        /// <summary>
        /// 当前引用计数
        /// </summary>
        public int ReferenceCount;
        
        /// <summary>
        /// 同步等待次数
        /// </summary>
        public int WaitCount;
        
        /// <summary>
        /// 异步等待次数
        /// </summary>
        public int AsyncWaitCount;
        
        /// <summary>
        /// 总等待时间（毫秒）
        /// </summary>
        public long TotalWaitTimeMs;
        
        /// <summary>
        /// 栅栏创建至今的时间（毫秒）
        /// </summary>
        public long AgeMs;
        
        /// <summary>
        /// 是否已收到信号
        /// </summary>
        public bool IsSignaled;
        
        /// <summary>
        /// 锁状态（true表示被锁定）
        /// </summary>
        public bool LockStatus;
        
        public override string ToString()
        {
            return $"FenceDiagnostics: RefCount={ReferenceCount}, WaitCount={WaitCount}, AsyncWaits={AsyncWaitCount}, " +
                   $"TotalWait={TotalWaitTimeMs}ms, Age={AgeMs}ms, Signaled={IsSignaled}, Locked={LockStatus}";
        }
    }
}