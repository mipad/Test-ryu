using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// Manages memory pages with on-demand allocation (similar to yuzu).
    /// </summary>
    public class PageMemoryManager : IDisposable
    {
        private const ulong PageSize = 1UL << 12; // 4KB
        private const ulong PageMask = PageSize - 1;

        private readonly Dictionary<ulong, MemoryBlock> _mappedPages;
        private readonly object _lock = new();
        private bool _disposed;

        public PageMemoryManager(ulong addressSpaceSize)
        {
            _mappedPages = new Dictionary<ulong, MemoryBlock>();
        }

        /// <summary>
        /// Maps a virtual address range to physical memory (lazy allocation).
        /// </summary>
        public void Map(ulong va, ulong size)
        {
            lock (_lock)
            {
                ulong endVa = va + size;
                for (ulong currentVa = va; currentVa < endVa; currentVa += PageSize)
                {
                    if (!_mappedPages.ContainsKey(currentVa))
                    {
                        _mappedPages[currentVa] = new MemoryBlock(PageSize, MemoryAllocationFlags.Reserve);
                    }
                }
            }
        }

        /// <summary>
        /// Unmaps a virtual address range.
        /// </summary>
        public void Unmap(ulong va, ulong size)
        {
            lock (_lock)
            {
                ulong endVa = va + size;
                for (ulong currentVa = va; currentVa < endVa; currentVa += PageSize)
                {
                    if (_mappedPages.TryGetValue(currentVa, out var page))
                    {
                        page?.Dispose();
                        _mappedPages.Remove(currentVa);
                    }
                }
            }
        }

        /// <summary>
        /// Handles a page fault (commits physical memory on first access).
        /// </summary>
        public bool HandlePageFault(ulong va)
        {
            lock (_lock)
            {
                ulong alignedVa = va & ~PageMask;
                if (_mappedPages.TryGetValue(alignedVa, out var page) && page != null)
                {
                    try
                    {
                        page.Commit(0, PageSize);
                        return true;
                    }
                    catch (SystemException ex)
                    {
                        Logger.Error?.Print(LogClass.Cpu, $"Failed to commit memory: {ex.Message}");
                        return false;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Translates a virtual address to a physical address.
        /// </summary>
        public IntPtr Translate(ulong va)
        {
            lock (_lock)
            {
                ulong alignedVa = va & ~PageMask;
                if (_mappedPages.TryGetValue(alignedVa, out var page) && page != null)
                {
                    return page.GetPointer(0, PageSize);
                }
                return IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                foreach (var page in _mappedPages.Values)
                {
                    page?.Dispose();
                }
                _mappedPages.Clear();
            }
        }
    }
}
