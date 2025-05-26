using Ryujinx.Common.Memory;
using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    class MultiFenceHolder
    {
        private const int BufferUsageTrackingGranularity = 4096;

        private readonly FenceHolder[] _fences;
        private readonly BufferUsageBitmap _bufferUsageBitmap;

        public MultiFenceHolder()
        {
            _fences = new FenceHolder[CommandBufferPool.MaxCommandBuffers];
        }

        public MultiFenceHolder(int size)
        {
            _fences = new FenceHolder[CommandBufferPool.MaxCommandBuffers];
            _bufferUsageBitmap = new BufferUsageBitmap(size, BufferUsageTrackingGranularity);
        }

        public void AddBufferUse(int cbIndex, int offset, int size, bool write)
        {
            // 显式标记读写操作
            _bufferUsageBitmap.Add(cbIndex, offset, size, isWrite: false); // 读操作
            if (write)
            {
                _bufferUsageBitmap.Add(cbIndex, offset, size, isWrite: true); // 写操作
            }
        }

        public void RemoveBufferUses(int cbIndex)
        {
            _bufferUsageBitmap?.Clear(cbIndex);
        }

        public bool IsBufferRangeInUse(int cbIndex, int offset, int size)
        {
            return _bufferUsageBitmap.OverlapsWith(cbIndex, offset, size);
        }

        public bool IsBufferRangeInUse(int offset, int size, bool write)
        {
            return _bufferUsageBitmap.OverlapsWith(offset, size, write);
        }

        public bool AddFence(int cbIndex, FenceHolder fence)
        {
            ref FenceHolder fenceRef = ref _fences[cbIndex];

            if (fenceRef == null)
            {
                fenceRef = fence;
                return true;
            }

            return false;
        }

        public void RemoveFence(int cbIndex)
        {
            _fences[cbIndex] = null;
        }

        public bool HasFence(int cbIndex)
        {
            return _fences[cbIndex] != null;
        }

        public void WaitForFences(Vk api, Device device)
        {
            WaitForFencesImpl(api, device, 0, 0, false, 0UL);
        }

        public void WaitForFences(Vk api, Device device, int offset, int size)
        {
            WaitForFencesImpl(api, device, offset, size, false, 0UL);
        }

        public bool WaitForFences(Vk api, Device device, ulong timeout)
        {
            return WaitForFencesImpl(api, device, 0, 0, true, timeout);
        }

        private bool WaitForFencesImpl(Vk api, Device device, int offset, int size, bool hasTimeout, ulong timeout)
        {
            using SpanOwner<FenceHolder> fenceHoldersOwner = SpanOwner<FenceHolder>.Rent(CommandBufferPool.MaxCommandBuffers);
            Span<FenceHolder> fenceHolders = fenceHoldersOwner.Span;

            int count = size != 0 ? GetOverlappingFences(fenceHolders, offset, size) : GetFences(fenceHolders);
            Span<Fence> fences = stackalloc Fence[count];

            int fenceCount = 0;

            for (int i = 0; i < fences.Length; i++)
            {
                if (fenceHolders[i] != null && fenceHolders[i].TryGet(out Fence fence))
                {
                    fences[fenceCount] = fence;

                    if (fenceCount < i)
                    {
                        fenceHolders[fenceCount] = fenceHolders[i];
                    }

                    fenceCount++;
                }
            }

            if (fenceCount == 0)
            {
                return true;
            }

            bool signaled = true;

            try
            {
                if (hasTimeout)
                {
                    signaled = FenceHelper.AllSignaled(api, device, fences[..fenceCount], timeout);
                }
                else
                {
                    FenceHelper.WaitAllIndefinitely(api, device, fences[..fenceCount]);
                }
            }
            finally
            {
                for (int i = 0; i < fenceCount; i++)
                {
                    fenceHolders[i]?.PutLock();
                }
            }

            return signaled;
        }

        private int GetFences(Span<FenceHolder> storage)
        {
            int count = 0;

            for (int i = 0; i < _fences.Length; i++)
            {
                var fence = _fences[i];

                if (fence != null)
                {
                    storage[count++] = fence;
                }
            }

            return count;
        }

        private int GetOverlappingFences(Span<FenceHolder> storage, int offset, int size)
        {
            int count = 0;

            for (int i = 0; i < _fences.Length; i++)
            {
                var fence = _fences[i];

                if (fence != null && _bufferUsageBitmap.OverlapsWith(i, offset, size))
                {
                    storage[count++] = fence;
                }
            }

            return count;
        }
    }
}
