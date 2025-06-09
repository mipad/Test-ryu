using Ryujinx.Common.Collections;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.HLE.HOS.Services.Nv
{
    class NvMemoryAllocator
    {
        private const ulong AddressSpaceSize = 1UL << 40;
        private const ulong DefaultStart = 1UL << 32;
        private const ulong InvalidAddress = 0;
        private const ulong PageSize = MemoryManager.PageSize;
        private const ulong PageMask = MemoryManager.PageMask;
        public const ulong PteUnmapped = MemoryManager.PteUnmapped;
        
        // 配置参数
        private const ulong MinBlockSize = 0x10000; // 64KB 最小块大小
        private const ulong SmallAllocThreshold = 0x4000; // 16KB 小对象阈值
        private const int CompactionThreshold = 100; // 每100次分配尝试压缩
        private const float FragmentationWarningThreshold = 0.7f; // 碎片率警告阈值
        
        // 原有数据结构
        private readonly TreeDictionary<ulong, ulong> _tree = new();
        private readonly Dictionary<ulong, LinkedListNode<ulong>> _dictionary = new();
        private readonly LinkedList<ulong> _list = new();
        
        // 伙伴系统数据结构
        private class BuddyBlock
        {
            public ulong Start;
            public ulong Size;
            public bool Free = true;
            public BuddyBlock Left;
            public BuddyBlock Right;
            public bool IsSplit => Left != null;
        }

        private BuddyBlock _buddyRoot;
        private readonly Dictionary<ulong, BuddyBlock> _allocatedBlocks = new();
        private readonly Dictionary<ulong, ulong> _fragments = new();
        
        // 性能监控
        private int _allocationCount;
        private int _deallocationCount;

        public NvMemoryAllocator()
        {
            // 初始化原有结构
            _tree.Add(PageSize, AddressSpaceSize);
            var node = _list.AddFirst(PageSize);
            _dictionary[PageSize] = node;
            
            // 初始化伙伴系统
            _buddyRoot = new BuddyBlock
            {
                Start = PageSize,
                Size = AddressSpaceSize - PageSize
            };
        }

        #region 伙伴系统实现
        
        /// <summary>
        /// 使用伙伴系统分配内存
        /// </summary>
        private ulong AllocateBuddy(ulong size, ulong alignment)
        {
            // 调整大小到最小块的倍数
            ulong actualSize = AlignUp(size, MinBlockSize);
            BuddyBlock block = FindFreeBlock(_buddyRoot, actualSize);
            
            if (block != null)
            {
                SplitBlock(block, actualSize);
                block.Free = false;
                _allocatedBlocks[block.Start] = block;
                
                // 处理对齐要求
                ulong alignedStart = AlignUp(block.Start, alignment);
                if (alignedStart != block.Start)
                {
                    _fragments[block.Start] = alignedStart - block.Start;
                }

                // 在原有分配器中标记这块内存已分配
                AllocateRangeLocked(block.Start, block.Size);
                
                return alignedStart;
            }
            return PteUnmapped;
        }
        
        /// <summary>
        /// 查找合适的空闲块
        /// </summary>
        private BuddyBlock FindFreeBlock(BuddyBlock current, ulong size)
        {
            if (current == null || !current.Free) return null;
            
            // 当前块正好满足要求
            if (current.Size == size) return current;
            
            // 当前块太大需要分割
            if (current.Size > size)
            {
                // 如果未分割，先分割
                if (!current.IsSplit)
                {
                    if (current.Size / 2 < size) 
                        return current; // 不能再分割但满足大小
                    
                    SplitBlock(current, current.Size / 2);
                }
                
                // 检查左子树
                var leftBlock = FindFreeBlock(current.Left, size);
                if (leftBlock != null) return leftBlock;
                
                // 检查右子树
                return FindFreeBlock(current.Right, size);
            }
            
            return null;
        }
        
        /// <summary>
        /// 分割内存块
        /// </summary>
        private void SplitBlock(BuddyBlock block, ulong size)
        {
            if (block.Left != null || block.Size < size * 2) 
                return;

            block.Left = new BuddyBlock 
            {
                Start = block.Start,
                Size = block.Size / 2
            };
            
            block.Right = new BuddyBlock 
            {
                Start = block.Start + block.Size / 2,
                Size = block.Size / 2
            };
        }
        
        /// <summary>
        /// 释放伙伴系统分配的内存
        /// </summary>
        private void DeallocateBuddy(ulong address)
        {
            if (_allocatedBlocks.TryGetValue(address, out var block))
            {
                // 先在原有分配器中标记这块内存为空闲
                DeallocateRangeLocked(block.Start, block.Size);
                
                block.Free = true;
                _allocatedBlocks.Remove(address);
                
                // 清理对齐产生的碎片记录
                if (_fragments.ContainsKey(address))
                {
                    _fragments.Remove(address);
                }
                
                MergeBuddies(block);
            }
        }
        
        /// <summary>
        /// 合并相邻的空闲块
        /// </summary>
        private void MergeBuddies(BuddyBlock block)
        {
            BuddyBlock current = block;
            
            while (current != null)
            {
                BuddyBlock buddy = GetBuddy(current);
                if (buddy != null && buddy.Free && !buddy.IsSplit)
                {
                    // 找到父块
                    BuddyBlock parent = current.Start < buddy.Start ? 
                        FindParent(_buddyRoot, current) : 
                        FindParent(_buddyRoot, buddy);
                    
                    if (parent != null)
                    {
                        parent.Left = null;
                        parent.Right = null;
                        parent.Free = true;
                        current = parent;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
        }
        
        /// <summary>
        /// 获取伙伴块
        /// </summary>
        private BuddyBlock GetBuddy(BuddyBlock block)
        {
            ulong buddyAddress = block.Start ^ block.Size;
            return FindBlock(_buddyRoot, buddyAddress);
        }
        
        /// <summary>
        /// 查找指定地址的内存块
        /// </summary>
        private BuddyBlock FindBlock(BuddyBlock current, ulong address)
        {
            if (current == null) return null;
            if (current.Start == address) return current;
            
            if (current.Left != null && address >= current.Left.Start && 
                address < current.Left.Start + current.Left.Size)
            {
                return FindBlock(current.Left, address);
            }
            
            if (current.Right != null && address >= current.Right.Start && 
                address < current.Right.Start + current.Right.Size)
            {
                return FindBlock(current.Right, address);
            }
            
            return null;
        }
        
        /// <summary>
        /// 查找父块
        /// </summary>
        private BuddyBlock FindParent(BuddyBlock parent, BuddyBlock target)
        {
            if (parent == null) return null;
            if (parent.Left == target || parent.Right == target) return parent;
            
            var leftParent = FindParent(parent.Left, target);
            if (leftParent != null) return leftParent;
            
            return FindParent(parent.Right, target);
        }
        
        #endregion
        
        #region 内存压缩与监控
        
        /// <summary>
        /// 计算当前内存碎片率
        /// </summary>
        public float CalculateFragmentation()
        {
            lock (_tree)
            {
                if (_tree.Count == 0) return 0f;
                
                ulong totalFree = 0;
                ulong maxBlockSize = 0;
                
                foreach (var block in _tree)
                {
                    ulong blockSize = block.Value - block.Key;
                    totalFree += blockSize;
                    if (blockSize > maxBlockSize) maxBlockSize = blockSize;
                }
                
                if (totalFree == 0) return 1f;
                return 1f - (maxBlockSize / (float)totalFree);
            }
        }
        
        #endregion
        
        #region 公共接口
        
        public void AllocateRange(ulong va, ulong size, ulong referenceAddress = InvalidAddress)
        {
            lock (_tree)
            {
                AllocateRangeLocked(va, size, referenceAddress);
            }
        }
        
        private void AllocateRangeLocked(ulong va, ulong size, ulong referenceAddress = InvalidAddress)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"Allocating range from 0x{va:X} to 0x{(va + size):X}.");
            
            if (referenceAddress != InvalidAddress)
            {
                ulong endAddress = va + size;
                ulong referenceEndAddress = _tree.Get(referenceAddress);
                
                if (va >= referenceAddress)
                {
                    if (va > referenceAddress)
                    {
                        ulong leftEndAddress = va;
                        _tree.Add(referenceAddress, leftEndAddress);
                        Logger.Debug?.Print(LogClass.ServiceNv, 
                            $"Created smaller address range from 0x{referenceAddress:X} to 0x{leftEndAddress:X}.");
                    }
                    else
                    {
                        _tree.Remove(referenceAddress);
                    }

                    ulong rightSize = referenceEndAddress - endAddress;
                    if (rightSize > 0)
                    {
                        Logger.Debug?.Print(LogClass.ServiceNv, 
                            $"Created smaller address range from 0x{endAddress:X} to 0x{referenceEndAddress:X}.");
                        _tree.Add(endAddress, referenceEndAddress);

                        var node = _list.AddAfter(_dictionary[referenceAddress], endAddress);
                        _dictionary[endAddress] = node;
                    }

                    if (va == referenceAddress)
                    {
                        _list.Remove(_dictionary[referenceAddress]);
                        _dictionary.Remove(referenceAddress);
                    }
                }
            }
        }
        
        public void DeallocateRange(ulong va, ulong size)
        {
            lock (_tree)
            {
                // 优先尝试伙伴系统释放
                if (size <= SmallAllocThreshold)
                {
                    DeallocateBuddy(va);
                    _deallocationCount++;
                    return;
                }
                
                _deallocationCount++;
                DeallocateRangeLocked(va, size);
            }
        }
        
        private void DeallocateRangeLocked(ulong va, ulong size)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"Deallocating address range from 0x{va:X} to 0x{(va + size):X}.");

            ulong freeAddressStartPosition = _tree.Floor(va);
            if (freeAddressStartPosition != InvalidAddress)
            {
                LinkedListNode<ulong> node = _dictionary[freeAddressStartPosition];
                ulong targetPrevAddress = node.Previous != null ? node.Previous.Value : InvalidAddress;
                ulong targetNextAddress = node.Next != null ? node.Next.Value : InvalidAddress;
                ulong expandedStart = va;
                ulong expandedEnd = va + size;

                while (targetPrevAddress != InvalidAddress)
                {
                    ulong prevAddress = targetPrevAddress;
                    ulong prevEndAddress = _tree.Get(targetPrevAddress);
                    if (prevEndAddress >= expandedStart)
                    {
                        expandedStart = targetPrevAddress;
                        LinkedListNode<ulong> prevPtr = _dictionary[prevAddress];
                        if (prevPtr.Previous != null)
                        {
                            targetPrevAddress = prevPtr.Previous.Value;
                        }
                        else
                        {
                            targetPrevAddress = InvalidAddress;
                        }
                        node = node.Previous;
                        _tree.Remove(prevAddress);
                        _list.Remove(_dictionary[prevAddress]);
                        _dictionary.Remove(prevAddress);
                    }
                    else
                    {
                        break;
                    }
                }

                while (targetNextAddress != InvalidAddress)
                {
                    ulong nextAddress = targetNextAddress;
                    ulong nextEndAddress = _tree.Get(targetNextAddress);
                    if (nextAddress <= expandedEnd)
                    {
                        expandedEnd = Math.Max(expandedEnd, nextEndAddress);
                        LinkedListNode<ulong> nextPtr = _dictionary[nextAddress];
                        if (nextPtr.Next != null)
                        {
                            targetNextAddress = nextPtr.Next.Value;
                        }
                        else
                        {
                            targetNextAddress = InvalidAddress;
                        }
                        _tree.Remove(nextAddress);
                        _list.Remove(_dictionary[nextAddress]);
                        _dictionary.Remove(nextAddress);
                    }
                    else
                    {
                        break;
                    }
                }

                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"Deallocation resulted in new free range from 0x{expandedStart:X} to 0x{expandedEnd:X}.");

                _tree.Add(expandedStart, expandedEnd);
                LinkedListNode<ulong> nodePtr = _list.AddAfter(node, expandedStart);
                _dictionary[expandedStart] = nodePtr;
            }
        }
        
        public ulong GetFreeAddress(ulong size, out ulong freeAddressStartPosition, 
                                   ulong alignment = 1, ulong start = DefaultStart)
        {
            lock (_tree)
            {
                freeAddressStartPosition = 0;
                _allocationCount++;
                
                // 定期检查碎片率
                if (_allocationCount % CompactionThreshold == 0)
                {
                    float frag = CalculateFragmentation();
                    if (frag > FragmentationWarningThreshold)
                    {
                        Logger.Warning?.Print(LogClass.ServiceNv, 
                            $"High fragmentation detected: {frag:P}. Suggest compaction");
                    }
                }
                
                // 小对象分配使用伙伴系统
                if (size <= SmallAllocThreshold)
                {
                    ulong address = AllocateBuddy(size, alignment);
                    if (address != PteUnmapped)
                    {
                        return address;
                    }
                }
                
                // 大对象使用原有分配器
                return OriginalGetFreeAddress(size, out freeAddressStartPosition, alignment, start);
            }
        }
        
        private ulong OriginalGetFreeAddress(ulong size, out ulong freeAddressStartPosition, 
                                            ulong alignment, ulong start)
        {
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"Searching for a free address @ 0x{start:X} of size 0x{size:X}.");
            ulong address = start;

            if (alignment == 0)
            {
                alignment = 1;
            }

            alignment = (alignment + PageMask) & ~PageMask;
            if (address < AddressSpaceSize)
            {
                bool reachedEndOfAddresses = false;
                ulong targetAddress;
                if (start == DefaultStart)
                {
                    // 优化：优先使用低地址空间
                    targetAddress = _tree.Ceiling(PageSize);
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"Using FIRST-FIT from 0x{targetAddress:X}");
                }
                else
                {
                    targetAddress = _tree.Floor(address);
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"Target address set to floor of 0x{address:X}; resulted in 0x{targetAddress:X}.");
                    if (targetAddress == InvalidAddress)
                    {
                        targetAddress = _tree.Ceiling(address);
                        Logger.Debug?.Print(LogClass.ServiceNv, 
                            $"Target address was invalid, set to ceiling of 0x{address:X}; resulted in 0x{targetAddress:X}");
                    }
                }
                while (address < AddressSpaceSize)
                {
                    if (targetAddress != InvalidAddress)
                    {
                        if (address >= targetAddress)
                        {
                            if (address + size <= _tree.Get(targetAddress))
                            {
                                Logger.Debug?.Print(LogClass.ServiceNv, 
                                    $"Found a suitable free address range from 0x{targetAddress:X} to 0x{_tree.Get(targetAddress):X} for 0x{address:X}.");
                                freeAddressStartPosition = targetAddress;
                                return address;
                            }
                            else
                            {
                                Logger.Debug?.Print(LogClass.ServiceNv, 
                                    "Address requirements exceeded the available space in the target range.");
                                LinkedListNode<ulong> nextPtr = _dictionary[targetAddress];
                                if (nextPtr.Next != null)
                                {
                                    targetAddress = nextPtr.Next.Value;
                                    Logger.Debug?.Print(LogClass.ServiceNv, 
                                        $"Moved search to successor range starting at 0x{targetAddress:X}.");
                                }
                                else
                                {
                                    if (reachedEndOfAddresses)
                                    {
                                        Logger.Debug?.Print(LogClass.ServiceNv, 
                                            "Exiting loop, a full pass has already been completed w/ no suitable free address range.");
                                        break;
                                    }
                                    else
                                    {
                                        reachedEndOfAddresses = true;
                                        address = start;
                                        targetAddress = _tree.Floor(address);
                                        Logger.Debug?.Print(LogClass.ServiceNv, 
                                            $"Reached the end of the available free ranges, restarting loop @ 0x{targetAddress:X} for 0x{address:X}.");
                                    }
                                }
                            }
                        }
                        else
                        {
                            address += PageSize * (targetAddress / PageSize - (address / PageSize));

                            ulong remainder = address % alignment;

                            if (remainder != 0)
                            {
                                address = (address - remainder) + alignment;
                            }

                            Logger.Debug?.Print(LogClass.ServiceNv, 
                                $"Reset and aligned address to {address:X}.");

                            if (address + size > AddressSpaceSize && !reachedEndOfAddresses)
                            {
                                reachedEndOfAddresses = true;
                                address = start;
                                targetAddress = _tree.Floor(address);
                                Logger.Debug?.Print(LogClass.ServiceNv, 
                                    $"Address requirements exceeded the capacity of available address space, restarting loop @ 0x{targetAddress:X} for 0x{address:X}.");
                            }
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
            Logger.Debug?.Print(LogClass.ServiceNv, 
                $"No suitable address range found; returning: 0x{InvalidAddress:X}.");
            freeAddressStartPosition = InvalidAddress;

            return PteUnmapped;
        }
        
        public bool IsRegionInUse(ulong gpuVa, ulong size, out ulong freeAddressStartPosition)
        {
            lock (_tree)
            {
                // 首先检查伙伴系统
                foreach (var block in _allocatedBlocks.Values)
                {
                    if (!block.Free && 
                        gpuVa < block.Start + block.Size && 
                        gpuVa + size > block.Start)
                    {
                        freeAddressStartPosition = 0;
                        return true;
                    }
                }
                
                // 然后检查原有分配器
                ulong floorAddress = _tree.Floor(gpuVa);
                freeAddressStartPosition = floorAddress;
                if (floorAddress != InvalidAddress)
                {
                    return !(gpuVa >= floorAddress && ((gpuVa + size) <= _tree.Get(floorAddress)));
                }
                return true;
            }
        }
        
        #endregion
        
        #region 辅助方法
        
        private static ulong AlignUp(ulong value, ulong alignment)
        {
            if (alignment == 0) return value;
            return (value + alignment - 1) & ~(alignment - 1);
        }
        
        public void DumpMemoryState()
        {
            lock (_tree)
            {
                Logger.Debug?.Print(LogClass.ServiceNv, "=== Memory Allocator State Dump ===");
                
                // 伙伴系统分配
                Logger.Debug?.Print(LogClass.ServiceNv, "[Buddy Allocations]");
                foreach (var block in _allocatedBlocks.Values.Where(b => !b.Free))
                {
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"  Block: 0x{block.Start:X}-0x{(block.Start + block.Size):X} " +
                        $"(Size: 0x{block.Size:X})");
                }
                
                // 原有分配器状态
                Logger.Debug?.Print(LogClass.ServiceNv, "[Tree Allocations]");
                foreach (var range in _tree)
                {
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"  Free: 0x{range.Key:X}-0x{range.Value:X} " +
                        $"(Size: 0x{range.Value - range.Key:X})");
                }
                
                // 碎片统计
                float fragRate = CalculateFragmentation();
                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"Fragmentation: {fragRate:P}, Allocs: {_allocationCount}, Deallocs: {_deallocationCount}");
            }
        }
        
        #endregion
    }
}
