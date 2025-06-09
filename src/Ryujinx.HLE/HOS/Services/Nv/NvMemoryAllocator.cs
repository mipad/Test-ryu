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

        // Key   --> Start Address of Region
        // Value --> End Address of Region
        private readonly TreeDictionary<ulong, ulong> _tree = new();

        private readonly Dictionary<ulong, LinkedListNode<ulong>> _dictionary = new();
        private readonly LinkedList<ulong> _list = new();

        public NvMemoryAllocator()
        {
            _tree.Add(PageSize, AddressSpaceSize);
            LinkedListNode<ulong> node = _list.AddFirst(PageSize);
            _dictionary[PageSize] = node;
        }

        /// <summary>
        /// Marks a range of memory as consumed by removing it from the tree.
        /// This function will split memory regions if there is available space.
        /// </summary>
        /// <param name="va">Virtual address at which to allocate</param>
        /// <param name="size">Size of the allocation in bytes</param>
        /// <param name="referenceAddress">Reference to the address of memory where the allocation can take place</param>
        #region Memory Allocation
        public void AllocateRange(ulong va, ulong size, ulong referenceAddress = InvalidAddress)
        {
            lock (_tree)
            {
                Logger.Debug?.Print(LogClass.ServiceNv, $"Allocating range from 0x{va:X} to 0x{(va + size):X}.");
                
                if (referenceAddress == InvalidAddress)
                {
                    referenceAddress = _tree.Floor(va);
                    if (referenceAddress == InvalidAddress)
                    {
                        Logger.Error?.Print(LogClass.ServiceNv, $"Allocation failed: address 0x{va:X} is not in any free block.");
                        return;
                    }
                }

                ulong endAddress = va + size;
                ulong referenceEndAddress = _tree.Get(referenceAddress);

                if (va >= referenceAddress && endAddress <= referenceEndAddress)
                {
                    // Need Left Node
                    if (va > referenceAddress)
                    {
                        ulong leftEndAddress = va;

                        // Overwrite existing block with its new smaller range.
                        _tree.Add(referenceAddress, leftEndAddress);
                        Logger.Debug?.Print(LogClass.ServiceNv, $"Created smaller address range from 0x{referenceAddress:X} to 0x{leftEndAddress:X}.");
                    }
                    else
                    {
                        // We need to get rid of the large chunk.
                        _tree.Remove(referenceAddress);
                    }

                    ulong rightSize = referenceEndAddress - endAddress;
                    // If leftover space, create a right node.
                    if (rightSize > 0)
                    {
                        Logger.Debug?.Print(LogClass.ServiceNv, $"Created smaller address range from 0x{endAddress:X} to 0x{referenceEndAddress:X}.");
                        _tree.Add(endAddress, referenceEndAddress);

                        LinkedListNode<ulong> node = _dictionary[referenceAddress];
                        if (node.Next != null)
                        {
                            LinkedListNode<ulong> newNode = _list.AddAfter(node, endAddress);
                            _dictionary[endAddress] = newNode;
                        }
                        else
                        {
                            LinkedListNode<ulong> newNode = _list.AddLast(endAddress);
                            _dictionary[endAddress] = newNode;
                        }
                    }

                    if (va == referenceAddress)
                    {
                        _list.Remove(_dictionary[referenceAddress]);
                        _dictionary.Remove(referenceAddress);
                    }
                }
                else
                {
                    Logger.Error?.Print(LogClass.ServiceNv, $"Allocation failed: address range [0x{va:X}-0x{endAddress:X}] " +
                        $"is not within free block [0x{referenceAddress:X}-0x{referenceEndAddress:X}].");
                }
            }
        }

        /// <summary>
        /// Marks a range of memory as free by adding it to the tree.
        /// This function will automatically compact the tree when it determines there are multiple ranges of free memory adjacent to each other.
        /// </summary>
        /// <param name="va">Virtual address at which to deallocate</param>
        /// <param name="size">Size of the allocation in bytes</param>
        public void DeallocateRange(ulong va, ulong size)
        {
            lock (_tree)
            {
                Logger.Debug?.Print(LogClass.ServiceNv, $"Deallocating address range from 0x{va:X} to 0x{(va + size):X}.");

                ulong freeAddressStartPosition = _tree.Floor(va);
                if (freeAddressStartPosition != InvalidAddress)
                {
                    LinkedListNode<ulong> node = _dictionary[freeAddressStartPosition];
                    ulong expandedStart = va;
                    ulong expandedEnd = va + size;

                    // Check previous node for merging
                    if (node.Previous != null)
                    {
                        ulong prevAddress = node.Previous.Value;
                        ulong prevEndAddress = _tree.Get(prevAddress);
                        if (prevEndAddress == expandedStart)
                        {
                            expandedStart = prevAddress;
                            _tree.Remove(prevAddress);
                            _list.Remove(_dictionary[prevAddress]);
                            _dictionary.Remove(prevAddress);
                        }
                    }

                    // Check next node for merging
                    if (node.Next != null)
                    {
                        ulong nextAddress = node.Next.Value;
                        ulong nextEndAddress = _tree.Get(nextAddress);
                        if (nextAddress == expandedEnd)
                        {
                            expandedEnd = nextEndAddress;
                            _tree.Remove(nextAddress);
                            _list.Remove(_dictionary[nextAddress]);
                            _dictionary.Remove(nextAddress);
                        }
                    }

                    Logger.Debug?.Print(LogClass.ServiceNv, $"Deallocation resulted in new free range from 0x{expandedStart:X} to 0x{expandedEnd:X}.");

                    // Remove the original node if it was modified
                    if (expandedStart != freeAddressStartPosition)
                    {
                        _tree.Remove(freeAddressStartPosition);
                        _list.Remove(node);
                        _dictionary.Remove(freeAddressStartPosition);
                        node = null;
                    }

                    // Add new merged block
                    _tree.Add(expandedStart, expandedEnd);
                    
                    if (node != null)
                    {
                        // Update existing node
                        node.Value = expandedStart;
                        _dictionary[expandedStart] = node;
                    }
                    else
                    {
                        // Create new node
                        LinkedListNode<ulong> newNode;
                        if (_list.First == null)
                        {
                            newNode = _list.AddFirst(expandedStart);
                        }
                        else
                        {
                            // Find insertion point to keep list sorted
                            LinkedListNode<ulong> current = _list.First;
                            while (current != null && current.Value < expandedStart)
                            {
                                current = current.Next;
                            }
                            
                            if (current == null)
                            {
                                newNode = _list.AddLast(expandedStart);
                            }
                            else
                            {
                                newNode = _list.AddBefore(current, expandedStart);
                            }
                        }
                        _dictionary[expandedStart] = newNode;
                    }
                }
                else
                {
                    // No existing free block before, create new
                    expandedStart = va;
                    expandedEnd = va + size;

                    // Find insertion point to keep list sorted
                    LinkedListNode<ulong> newNode;
                    if (_list.First == null)
                    {
                        newNode = _list.AddFirst(expandedStart);
                    }
                    else
                    {
                        LinkedListNode<ulong> current = _list.First;
                        while (current != null && current.Value < expandedStart)
                        {
                            current = current.Next;
                        }
                        
                        if (current == null)
                        {
                            newNode = _list.AddLast(expandedStart);
                        }
                        else
                        {
                            newNode = _list.AddBefore(current, expandedStart);
                        }
                    }
                    
                    _tree.Add(expandedStart, expandedEnd);
                    _dictionary[expandedStart] = newNode;
                    Logger.Debug?.Print(LogClass.ServiceNv, $"Created new free range from 0x{expandedStart:X} to 0x{expandedEnd:X}.");
                }
            }
        }

        /// <summary>
        /// Gets the address of an unused (free) region of the specified size.
        /// </summary>
        /// <param name="size">Size of the region in bytes</param>
        /// <param name="freeAddressStartPosition">Position at which memory can be allocated</param>
        /// <param name="alignment">Required alignment of the region address in bytes</param>
        /// <param name="start">Start address of the search on the address space</param>
        /// <returns>GPU virtual address of the allocation, or an all ones mask in case of failure</returns>
        public ulong GetFreeAddress(ulong size, out ulong freeAddressStartPosition, ulong alignment = 1, ulong start = DefaultStart)
        {
            lock (_tree)
            {
                Logger.Debug?.Print(LogClass.ServiceNv, $"Searching for a free address @ 0x{start:X} of size 0x{size:X}.");
                freeAddressStartPosition = InvalidAddress;

                if (alignment == 0)
                {
                    alignment = 1;
                }

                alignment = (alignment + PageMask) & ~PageMask;
                ulong address = start;

                foreach (var block in _tree)
                {
                    ulong blockStart = block.Key;
                    ulong blockEnd = block.Value;
                    
                    // Calculate aligned address within this block
                    ulong alignedStart = (blockStart > address) ? blockStart : address;
                    alignedStart = (alignedStart + alignment - 1) & ~(alignment - 1);
                    
                    ulong blockSize = blockEnd - alignedStart;
                    
                    if (blockSize >= size)
                    {
                        freeAddressStartPosition = blockStart;
                        Logger.Debug?.Print(LogClass.ServiceNv, $"Found suitable block: 0x{blockStart:X}-0x{blockEnd:X} " +
                            $"(aligned: 0x{alignedStart:X}, size: 0x{size:X})");
                        return alignedStart;
                    }
                }

                Logger.Error?.Print(LogClass.ServiceNv, $"No suitable free block found for size 0x{size:X} with alignment 0x{alignment:X}");
                return PteUnmapped;
            }
        }

        /// <summary>
        /// Checks if a given memory region is mapped or reserved.
        /// </summary>
        /// <param name="gpuVa">GPU virtual address of the page</param>
        /// <param name="size">Size of the allocation in bytes</param>
        /// <param name="freeAddressStartPosition">Nearest lower address that memory can be allocated</param>
        /// <returns>True if the page is mapped or reserved, false otherwise</returns>
        public bool IsRegionInUse(ulong gpuVa, ulong size, out ulong freeAddressStartPosition)
        {
            lock (_tree)
            {
                freeAddressStartPosition = _tree.Floor(gpuVa);
                
                if (freeAddressStartPosition != InvalidAddress)
                {
                    ulong blockEnd = _tree.Get(freeAddressStartPosition);
                    return !(gpuVa >= freeAddressStartPosition && (gpuVa + size) <= blockEnd);
                }
                return true;
            }
        }
        #endregion
    }
}
