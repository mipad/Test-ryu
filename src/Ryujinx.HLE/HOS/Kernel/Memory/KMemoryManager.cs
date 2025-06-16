using Ryujinx.HLE.HOS.Kernel.Common;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Ryujinx.HLE.HOS.Kernel.Memory
{
    class KMemoryManager
    {
        private readonly object _globalLock = new object();
        private readonly ConcurrentDictionary<ulong, KMemoryRegionManager> _regionCache = new ConcurrentDictionary<ulong, KMemoryRegionManager>();
        public KMemoryRegionManager[] MemoryRegions { get; }

        public KMemoryManager(MemorySize size, MemoryArrange arrange)
        {
            MemoryRegions = KernelInit.GetMemoryRegions(size, arrange);
        }

        private KMemoryRegionManager GetMemoryRegion(ulong address)
        {
            for (int i = 0; i < MemoryRegions.Length; i++)
            {
                var region = MemoryRegions[i];

                if (address >= region.Address && address < region.EndAddr)
                {
                    return region;
                }
            }

            return null;
        }

        private KMemoryRegionManager GetCachedRegion(ulong address)
        {
            // 使用地址的高44位作为缓存键（按16KB页对齐）
            ulong cacheKey = address >> 14;
            
            return _regionCache.GetOrAdd(cacheKey, key => 
            {
                // 如果缓存未命中，执行实际查找
                return GetMemoryRegion(address);
            });
        }

        public void IncrementPagesReferenceCount(ulong address, ulong pagesCount)
        {
            IncrementOrDecrementPagesReferenceCount(address, pagesCount, true);
        }

        public void DecrementPagesReferenceCount(ulong address, ulong pagesCount)
        {
            IncrementOrDecrementPagesReferenceCount(address, pagesCount, false);
        }

        private void IncrementOrDecrementPagesReferenceCount(ulong address, ulong pagesCount, bool increment)
        {
            lock (_globalLock)
            {
                // 使用字典按区域分组操作
                var regionOperations = new Dictionary<KMemoryRegionManager, List<(ulong Address, ulong Count)>>();
                
                ulong remaining = pagesCount;
                ulong currentAddr = address;
                
                // 批量收集操作并按区域分组
                while (remaining != 0)
                {
                    var region = GetCachedRegion(currentAddr);
                    if (region == null)
                    {
                        throw new InvalidOperationException($"Address 0x{currentAddr:X} is not mapped to any memory region");
                    }

                    ulong count = Math.Min(remaining, region.GetPageOffsetFromEnd(currentAddr));
                    
                    // 按区域分组操作
                    if (!regionOperations.TryGetValue(region, out var operations))
                    {
                        operations = new List<(ulong, ulong)>();
                        regionOperations[region] = operations;
                    }
                    
                    operations.Add((currentAddr, count));
                    
                    remaining -= count;
                    currentAddr += count * KPageTableBase.PageSize;
                }
                
                // 按区域批量执行操作
                foreach (var kvp in regionOperations)
                {
                    var region = kvp.Key;
                    var operations = kvp.Value;
                    
                    // 每个区域只锁定一次
                    lock (region)
                    {
                        foreach (var op in operations)
                        {
                            if (increment)
                                region.IncrementPagesReferenceCount(op.Address, op.Count);
                            else
                                region.DecrementPagesReferenceCount(op.Address, op.Count);
                        }
                    }
                }
            }
        }
    }
}
