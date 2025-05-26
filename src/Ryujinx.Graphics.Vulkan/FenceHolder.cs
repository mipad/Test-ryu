using Silk.NET.Vulkan;
using System;
using System.Threading;
using System.Diagnostics; // 新增命名空间用于Stopwatch

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

        public unsafe FenceHolder(Vk api, Device device, bool concurrentWaitUnsupported)
        {
            _api = api;
            _device = device;
            _concurrentWaitUnsupported = concurrentWaitUnsupported;

            var fenceCreateInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };

            api.CreateFence(device, in fenceCreateInfo, null, out _fence).ThrowOnError();

            _referenceCount = 1;
        }

        public Fence GetUnsafe()
        {
            return _fence;
        }

        public bool TryGet(out Fence fence)
        {
            // 新增_disposed检查
            if (_disposed)
            {
                fence = default;
                return false;
            }

            int lastValue;
            do
            {
                lastValue = _referenceCount;

                // 新增_disposed检查
                if (lastValue == 0 || _disposed)
                {
                    fence = default;
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _referenceCount, lastValue + 1, lastValue) != lastValue);

            if (_concurrentWaitUnsupported)
            {
                // 修改为带超时的锁获取
                if (!TryAcquireLock(1000)) // 超时1秒
                {
                    // 回滚引用计数
                    Interlocked.Decrement(ref _referenceCount);
                    fence = default;
                    return false;
                }
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
                if (!_disposed)
                {
                    _api.DestroyFence(_device, _fence, Span<AllocationCallbacks>.Empty);
                    _fence = default;
                }
            }
        }

        // 修改后的锁方法，带超时机制
        private bool TryAcquireLock(int timeoutMs = 1000)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (Interlocked.Exchange(ref _lock, 1) != 0)
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    return false;
                }
                Thread.SpinWait(32);
            }
            return true;
        }

        private void ReleaseLock()
        {
            Interlocked.Exchange(ref _lock, 0);
        }

        public void Wait()
        {
            if (_concurrentWaitUnsupported)
            {
                if (!TryAcquireLock(1000))
                {
                    throw new TimeoutException("Failed to acquire fence lock");
                }

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

        public bool IsSignaled()
        {
            if (_concurrentWaitUnsupported)
            {
                if (!TryAcquireLock(1000))
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

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true; // 先标记为已释放
                Put();
            }
        }
    }
}
