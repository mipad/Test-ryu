using Ryujinx.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryujinx.Memory
{
    public delegate void PageInitDelegate(Span<byte> page);

    public class SparseMemoryBlock : IDisposable
    {
        private const ulong MapGranularity = 1UL << 17; // 128KB mapping granularity
        
        // Android 平台特殊处理：限制保留内存大小为 1GB
        private const ulong AndroidMaxReserveSize = 1UL << 30; // 1GB for Android

        private readonly PageInitDelegate _pageInit;
        private readonly object _lock = new object();
        private readonly ulong _pageSize;
        private readonly MemoryBlock _reservedBlock;
        private readonly List<MemoryBlock> _mappedBlocks;
        private ulong _mappedBlockUsage;
        private readonly ulong[] _mappedPageBitmap;

        public MemoryBlock Block => _reservedBlock;

        public SparseMemoryBlock(ulong size, PageInitDelegate pageInit, MemoryBlock fill)
        {
            _pageSize = MemoryBlock.GetPageSize();
            _pageInit = pageInit;

            // Android 平台特殊处理：限制保留内存大小
            ulong reservedSize = size;
            
            #if ANDROID
            if (reservedSize > AndroidMaxReserveSize)
            {
                reservedSize = AndroidMaxReserveSize;
                Logger.Warning?.Print(LogClass.Memory, 
                    $"Reducing reserved memory from {size / (1024 * 1024)}MB to {reservedSize / (1024 * 1024)}MB for Android compatibility");
            }
            #endif
            
            // 创建保留内存块（虚拟地址空间）
            _reservedBlock = new MemoryBlock(reservedSize, MemoryAllocationFlags.Reserve | MemoryAllocationFlags.ViewCompatible);
            _mappedBlocks = new List<MemoryBlock>();

            // 初始化页映射位图
            int pages = (int)BitUtils.DivRoundUp(size, _pageSize);
            int bitmapEntries = BitUtils.DivRoundUp(pages, 64);
            _mappedPageBitmap = new ulong[bitmapEntries];

            if (fill != null)
            {
                // 使用填充块初始化内存
                if (fill.Size % _pageSize != 0)
                {
                    throw new ArgumentException("Fill memory block should be page aligned.", nameof(fill));
                }

                int repeats = (int)BitUtils.DivRoundUp(size, fill.Size);
                ulong offset = 0;
                for (int i = 0; i < repeats; i++)
                {
                    _reservedBlock.MapView(fill, 0, offset, Math.Min(fill.Size, size - offset));
                    offset += fill.Size;
                }
            }
        }

        private void MapPage(ulong pageOffset)
        {
            // 从最新的映射块中获取页面
            MemoryBlock block = _mappedBlocks.LastOrDefault();

            if (block == null || _mappedBlockUsage == MapGranularity)
            {
                // 需要映射更多内存
                block = new MemoryBlock(MapGranularity, MemoryAllocationFlags.Mirrorable);
                _mappedBlocks.Add(block);
                _mappedBlockUsage = 0;
            }

            // 初始化页面内容
            _pageInit(block.GetSpan(_mappedBlockUsage, (int)_pageSize));
            
            // 映射到保留块
            _reservedBlock.MapView(block, _mappedBlockUsage, pageOffset, _pageSize);

            _mappedBlockUsage += _pageSize;
        }

        public void EnsureMapped(ulong offset)
        {
            int pageIndex = (int)(offset / _pageSize);
            int bitmapIndex = pageIndex >> 6;

            ref ulong entry = ref _mappedPageBitmap[bitmapIndex];
            ulong bit = 1UL << (pageIndex & 63);

            if ((Volatile.Read(ref entry) & bit) == 0)
            {
                // 未映射，需要加锁处理
                lock (_lock)
                {
                    ulong lockedEntry = Volatile.Read(ref entry);
                    if ((lockedEntry & bit) == 0)
                    {
                        // 计算页面起始地址
                        ulong pageStart = offset & ~(_pageSize - 1);
                        MapPage(pageStart);

                        // 更新位图
                        lockedEntry |= bit;
                        Interlocked.Exchange(ref entry, lockedEntry);
                    }
                }
            }
        }

        public void Dispose()
        {
            // 释放所有内存块
            _reservedBlock.Dispose();

            foreach (MemoryBlock block in _mappedBlocks)
            {
                block.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }
}
