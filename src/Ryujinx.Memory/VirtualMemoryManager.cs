using Ryujinx.Common.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Memory
{
    public class VirtualMemoryManager : VirtualMemoryManagerBase, IVirtualMemoryManager, IDisposable
    {
        private readonly MemoryBlock _backingMemory;
        private readonly Dictionary<ulong, MemoryPermission> _currentProtections = new();
        private readonly object _memoryTracking;
        private bool _disposed;

        public bool UsesPrivateAllocations => false;

        public VirtualMemoryManager(ulong addressSpaceSize, object memoryTracking = null)
        {
            AddressSpaceSize = addressSpaceSize;
            _backingMemory = new MemoryBlock(addressSpaceSize, MemoryAllocationFlags.Reserve);
            _memoryTracking = memoryTracking;
        }

        protected override ulong AddressSpaceSize { get; }

        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            // In this simple implementation, we just commit the memory
            _backingMemory.Commit(va, size);
        }

        public void MapForeign(ulong va, nuint hostPointer, ulong size)
        {
            throw new NotSupportedException("Foreign mapping not supported in this implementation");
        }

        public void Unmap(ulong va, ulong size)
        {
            // In this simple implementation, we just decommit the memory
            _backingMemory.Decommit(va, size);
            _currentProtections.Remove(va);
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
            return (nuint)va;
        }

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
        {
            return (nuint)va;
        }

        public override bool IsMapped(ulong va)
        {
            // Simple check - in real implementation would need to track mapped regions
            return _backingMemory.GetPointer(va, 1) != IntPtr.Zero;
        }

        public bool IsRangeMapped(ulong va, ulong size)
        {
            // Simple check - in real implementation would need to check each page
            return _backingMemory.GetPointer(va, size) != IntPtr.Zero;
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

        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            if (!IsRangeMappedSafe(va, size))
            {
                return;
            }
            // Memory tracking logic would go here
        }

        public void Reprotect(ulong va, ulong size, MemoryPermission protection)
        {
            // Convert MemoryPermission to MemoryBlock protection
           // MemoryBlockProtection blockProtection = ConvertToMemoryBlockProtection(protection);
            _backingMemory.Reprotect(va, size, protection, true);
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
            if (!IsMapped(va))
            {
                throw new InvalidMemoryRegionException();
            }
            return ref _backingMemory.GetRef<T>(va);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _backingMemory.Dispose();
                    _currentProtections.Clear();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void Fill(ulong va, ulong size, byte value)
        {
            const int MaxChunkSize = 1 << 24;

            for (ulong subOffset = 0; subOffset < size; subOffset += MaxChunkSize)
            {
                int copySize = (int)Math.Min(MaxChunkSize, size - subOffset);

                using var writableRegion = GetWritableRegion(va + subOffset, copySize);

                writableRegion.Memory.Span.Fill(value);
            }
        }

        public bool WriteWithRedundancyCheck(ulong va, ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                return false;
            }

            if (IsContiguousAndMapped(va, data.Length))
            {
                SignalMemoryTracking(va, (ulong)data.Length, false);

                nuint pa = TranslateVirtualAddressChecked(va);

                var target = GetPhysicalAddressSpan(pa, data.Length);

                bool changed = !data.SequenceEqual(target);

                if (changed)
                {
                    data.CopyTo(target);
                }

                return changed;
            }
            else
            {
                Write(va, data);
                return true;
            }
        }

        private static MemoryBlockProtection ConvertToMemoryBlockProtection(MemoryPermission permission)
        {
            return permission switch
            {
                MemoryPermission.None => MemoryBlockProtection.None,
                MemoryPermission.Read => MemoryBlockProtection.ReadOnly,
                MemoryPermission.Write => MemoryBlockProtection.ReadWrite, // Write alone not typically available
                MemoryPermission.Execute => MemoryBlockProtection.Execute,
                MemoryPermission.ReadAndWrite => MemoryBlockProtection.ReadWrite,
                MemoryPermission.ReadAndExecute => MemoryBlockProtection.ReadExecute,
                MemoryPermission.ReadWriteExecute => MemoryBlockProtection.ReadWriteExecute,
                _ => throw new ArgumentException($"Invalid memory permission: {permission}")
            };
        }
    }
}
