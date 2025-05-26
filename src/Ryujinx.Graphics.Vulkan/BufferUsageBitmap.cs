namespace Ryujinx.Graphics.Vulkan
{
    internal class BufferUsageBitmap
    {
        private readonly BitMap _bitmap;
        private readonly int _size;
        private readonly int _granularity;
        private readonly int _bits;
        private readonly int _writeBitOffset;

        private readonly int _intsPerCb;
        private readonly int _bitsPerCb;

        public BufferUsageBitmap(int size, int granularity)
        {  // 初始化逻辑中需使用 CommandBufferPool.MaxCommandBuffers
            // 正确引用 CommandBufferPool 的静态属性 MaxCommandBuffers
            int maxCommandBuffers = CommandBufferPool.MaxCommandBuffers;
            _tracking = new bool[maxCommandBuffers, (size + granularity - 1) / granularity];

            _size = size;
            _granularity = granularity;

            // 计算位图参数
            int bits = (size + granularity - 1) / granularity;
            _writeBitOffset = bits;
            _bits = bits * 2; // 读和写各占一半

            _intsPerCb = (_bits + 31) / 32; // 假设 BitMap.IntSize 是 32
            _bitsPerCb = _intsPerCb * 32;

            _bitmap = new BitMap(maxCommandBuffers * _bitsPerCb);
        }

        public void Add(int cbIndex, int offset, int size, bool write)
        {
            if (size == 0)
            {
                return;
            }

            // Some usages can be out of bounds (vertex buffer on amd), so bound if necessary.
            if (offset + size > _size)
            {
                size = _size - offset;
            }

            int cbBase = cbIndex * _bitsPerCb + (write ? _writeBitOffset : 0);
            int start = cbBase + offset / _granularity;
            int end = cbBase + (offset + size - 1) / _granularity;

            _bitmap.SetRange(start, end);
        }

           public bool OverlapsWith(int offset, int size, bool write)
{
    // 检查所有命令缓冲区
    for (int i = 0; i < CommandBufferPool.MaxCommandBuffers; i++)
    {
        if (OverlapsWith(i, offset, size, write)) // 调用重载方法
        {
            return true;
        }
    }
    return false;
}

public bool OverlapsWith(int cbIndex, int offset, int size, bool write)
{
    // 检查指定命令缓冲区（cbIndex）的范围
    int cbBase = cbIndex * _bitsPerCb + (write ? _writeBitOffset : 0);
    int start = cbBase + offset / _granularity;
    int end = cbBase + (offset + size - 1) / _granularity;

    return _bitmap.IsSet(start, end);
}
        public bool OverlapsWith(int offset, int size, bool write)
        {
            for (int i = 0; i < CommandBufferPool.MaxCommandBuffers; i++)
            {
                if (OverlapsWith(i, offset, size, write))
                {
                    return true;
                }
            }

            return false;
        }

        public void Clear(int cbIndex)
        {
            _bitmap.ClearInt(cbIndex * _intsPerCb, (cbIndex + 1) * _intsPerCb - 1);
        }
    }
}
