using Ryujinx.Common.Collections;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Collections.Generic;

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

        private readonly TreeDictionary<ulong, ulong> _tree = new();
        private readonly Dictionary<ulong, LinkedListNode<ulong>> _dictionary = new();
        private readonly LinkedList<ulong> _list = new();

        public NvMemoryAllocator()
        {
            _tree.Add(PageSize, AddressSpaceSize);
            LinkedListNode<ulong> node = _list.AddFirst(PageSize);
            _dictionary[PageSize] = node;
        }

        public void AllocateRange(ulong va, ulong size, ulong referenceAddress = InvalidAddress)
        {
            lock (_tree)
            {
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
                        }
                        else
                        {
                            _tree.Remove(referenceAddress);
                        }

                        ulong rightSize = referenceEndAddress - endAddress;
                        if (rightSize > 0)
                        {
                            _tree.Add(endAddress, referenceEndAddress);
                            LinkedListNode<ulong> node = _list.AddAfter(_dictionary[referenceAddress], endAddress);
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
        }

        public void DeallocateRange(ulong va, ulong size)
        {
            lock (_tree)
            {
                ulong freeAddressStartPosition = _tree.Floor(va);
                if (freeAddressStartPosition != InvalidAddress)
                {
                    LinkedListNode<ulong> node = _dictionary[freeAddressStartPosition];
                    ulong expandedStart = va;
                    ulong expandedEnd = va + size;

                    // 合并左侧空闲块
                    LinkedListNode<ulong> prevNode = node.Previous;
                    while (prevNode != null)
                    {
                        ulong prevAddress = prevNode.Value;
                        ulong prevEnd = _tree.Get(prevAddress);
                        
                        if (prevEnd >= expandedStart)
                        {
                            expandedStart = prevAddress;
                            _tree.Remove(prevAddress);
                            _list.Remove(prevNode);
                            _dictionary.Remove(prevAddress);
                            prevNode = node.Previous;
                        }
                        else
                        {
                            break;
                        }
                    }

                    // 合并右侧空闲块
                    LinkedListNode<ulong> nextNode = node.Next;
                    while (nextNode != null)
                    {
                        ulong nextAddress = nextNode.Value;
                        ulong nextEnd = _tree.Get(nextAddress);
                        
                        if (nextAddress <= expandedEnd)
                        {
                            expandedEnd = Math.Max(expandedEnd, nextEnd);
                            _tree.Remove(nextAddress);
                            _list.Remove(nextNode);
                            _dictionary.Remove(nextAddress);
                            nextNode = node.Next;
                        }
                        else
                        {
                            break;
                        }
                    }

                    _tree.Add(expandedStart, expandedEnd);
                    LinkedListNode<ulong> newNode = _list.AddBefore(node, expandedStart);
                    _dictionary[expandedStart] = newNode;
                    _list.Remove(node);
                    _dictionary.Remove(freeAddressStartPosition);
                }
            }
        }

        public ulong GetFreeAddress(ulong size, out ulong freeAddressStartPosition, ulong alignment = 1, ulong start = DefaultStart)
        {
            lock (_tree)
            {
                ulong address = start;
                alignment = (alignment + PageMask) & ~PageMask;

                if (address < AddressSpaceSize)
                {
                    LinkedListNode<ulong> currentNode = _list.First;
                    while (currentNode != null)
                    {
                        ulong blockStart = currentNode.Value;
                        ulong blockEnd = _tree.Get(blockStart);

                        if (blockStart <= address && address < blockEnd)
                        {
                            ulong alignedAddress = (address + alignment - 1) & ~(alignment - 1);
                            ulong endAddress = alignedAddress + size;

                            if (endAddress <= blockEnd)
                            {
                                freeAddressStartPosition = blockStart;
                                return alignedAddress;
                            }
                        }
                        currentNode = currentNode.Next;
                    }

                    // 回退到从空闲块起始地址开始分配
                    currentNode = _list.First;
                    while (currentNode != null)
                    {
                        ulong blockStart = currentNode.Value;
                        ulong blockEnd = _tree.Get(blockStart);
                        ulong alignedAddress = (blockStart + alignment - 1) & ~(alignment - 1);
                        ulong endAddress = alignedAddress + size;

                        if (endAddress <= blockEnd)
                        {
                            freeAddressStartPosition = blockStart;
                            return alignedAddress;
                        }
                        currentNode = currentNode.Next;
                    }
                }

                freeAddressStartPosition = InvalidAddress;
                return PteUnmapped;
            }
        }

        public bool IsRegionInUse(ulong gpuVa, ulong size, out ulong freeAddressStartPosition)
        {
            lock (_tree)
            {
                ulong floorAddress = _tree.Floor(gpuVa);
                freeAddressStartPosition = floorAddress;
                
                if (floorAddress != InvalidAddress)
                {
                    ulong blockEnd = _tree.Get(floorAddress);
                    return !(gpuVa >= floorAddress && (gpuVa + size) <= blockEnd);
                }
                return true;
            }
        }
    }
}
