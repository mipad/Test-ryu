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

        // 新增缺失的 _tracking 字段
        private readonly bool[,] _tracking;

        public BufferUsageBitmap(int size, int granularity)
        {  
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

        
public bool OverlapsWith(int cbIndex, int offset, int size)
{
    // 同时检查读和写操作
    bool readOverlap = OverlapsWith(cbIndex, offset, size, false);
    bool writeOverlap = OverlapsWith(cbIndex, offset, size, true);
    return readOverlap || writeOverlap;
}
        // 保留唯一正确的 OverlapsWith 方法（删除重复定义）
        public bool OverlapsWith(int offset, int size, bool write)
        {
            for (int i = 0; i < CommandBufferPool.MaxCommandBuffers; i++)
            {
                if (OverlapsWith(i, offset, size, write)) // 调用重载方法
                {
                    return true;
                }
            }
            return false;
        }

        // 重载方法：检查指定命令缓冲区的范围
        public bool OverlapsWith(int cbIndex, int offset, int size, bool write)
        {
            int cbBase = cbIndex * _bitsPerCb + (write ? _writeBitOffset : 0);
            int start = cbBase + offset / _granularity;
            int end = cbBase + (offset + size - 1) / _granularity;

            return _bitmap.IsSet(start, end);
        }

        public void Clear(int cbIndex)
        {
            _bitmap.ClearInt(cbIndex * _intsPerCb, (cbIndex + 1) * _intsPerCb - 1);
        }
    }
}
