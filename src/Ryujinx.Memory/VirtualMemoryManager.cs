using Ryujinx.Common.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Memory
{
    public class VirtualMemoryManager : VirtualMemoryManagerBase, IVirtualMemoryManager
    {
        private readonly MemoryBlock _backingMemory;
        private readonly Dictionary<ulong, MemoryPermission> _currentProtections = new();
        private readonly MemoryTracking _memoryTracking;

        public bool UsesPrivateAllocations => false;

        public VirtualMemoryManager(ulong addressSpaceSize, MemoryTracking memoryTracking = null)
        {
            _backingMemory = new MemoryBlock(addressSpaceSize);
            _memoryTracking = memoryTracking;
        }

        protected override ulong AddressSpaceSize { get; }

        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            // Implementation for mapping virtual to physical memory
            // Would typically involve page table manipulation
        }

        public void MapForeign(ulong va, nuint hostPointer, ulong size)
        {
            throw new NotSupportedException("Foreign mapping not supported");
        }

        public void Unmap(ulong va, ulong size)
        {
            // Implementation for unmapping memory
        }

        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
        {
            return _backingMemory.GetMemory((ulong)pa, size);
        }

        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
        {
            return _backingMemory.GetSpan((ulong)pa, size);
        }

        protected override nuint TranslateVirtualAddressChecked(ulong va)
        {
            if (!IsMapped(va))
            {
                throw new InvalidMemoryRegionException($"Virtual address 0x{va:X} is not mapped");
            }
            return TranslateVirtualAddressUnchecked(va);
        }

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
        {
            // Simple identity mapping for this implementation
            return (nuint)va;
        }

        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            _memoryTracking?.SignalMemoryTracking(va, size, write, precise, exemptId);
        }

        public void Reprotect(ulong va, ulong size, MemoryPermission protection)
        {
            // Actual protection change implementation
            _backingMemory.Reprotect(va, size, protection.ToMemoryProtection());
        }

        public void TrackingReprotect(ulong va, ulong size, MemoryPermission protection, bool guest)
        {
            if (_currentProtections.TryGetValue(va, out var current) && current == protection)
            {
                return;
            }

            Reprotect(va, size, protection);
            _currentProtections[va] = protection;
        }

        public IEnumerable<HostMemoryRange> GetHostRegions(ulong va, ulong size)
        {
            yield return new HostMemoryRange((nuint)va, size);
        }

        public IEnumerable<MemoryRange> GetPhysicalRegions(ulong va, ulong size)
        {
            yield return new MemoryRange(va, size);
        }

        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            return ref MemoryMarshal.GetReference(GetSpan(va, Unsafe.SizeOf<T>()));
        }

        public bool IsRangeMappedSafe(ulong va, ulong size)
        {
            for (ulong offset = 0; offset < size; offset += PageSize)
            {
                if (!IsMapped(va + offset))
                {
                    return false;
                }
            }
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _backingMemory.Dispose();
            }
        }
    }
}
