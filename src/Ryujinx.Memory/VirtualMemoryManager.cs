using Ryujinx.Common.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Memory
{
    /// <summary>
    /// 虚拟内存管理器，负责管理虚拟地址空间的映射和保护
    /// </summary>
    public class VirtualMemoryManager : VirtualMemoryManagerBase, IVirtualMemoryManager, IDisposable
    {
        private readonly MemoryBlock _backingMemory; // 后备内存块
        private readonly Dictionary<ulong, MemoryPermission> _currentProtections = new(); // 当前内存保护状态
        private readonly object _memoryTracking; // 内存跟踪对象
        private bool _disposed; // 是否已释放资源

        /// <summary>
        /// 是否使用私有分配
        /// </summary>
        public bool UsesPrivateAllocations => false;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="addressSpaceSize">地址空间大小</param>
        /// <param name="memoryTracking">内存跟踪对象</param>
        public VirtualMemoryManager(ulong addressSpaceSize, object memoryTracking = null)
        {
            AddressSpaceSize = addressSpaceSize;
            _backingMemory = new MemoryBlock(addressSpaceSize, MemoryAllocationFlags.Reserve);
            _memoryTracking = memoryTracking;
        }

        /// <summary>
        /// 地址空间大小
        /// </summary>
        protected override ulong AddressSpaceSize { get; }

        /// <summary>
        /// 映射虚拟地址到物理地址
        /// </summary>
        /// <param name="va">虚拟地址</param>
        /// <param name="pa">物理地址</param>
        /// <param name="size">大小</param>
        /// <param name="flags">映射标志</param>
        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            _backingMemory.Commit(va, size);
            // 为每个页面初始化保护状态
            for (ulong offset = 0; offset < size; offset += PageSize)
            {
                _currentProtections[va + offset] = MemoryPermission.ReadAndWrite;
            }
        }

        /// <summary>
        /// 映射外部内存
        /// </summary>
        public void MapForeign(ulong va, nuint hostPointer, ulong size)
        {
            throw new NotSupportedException("此实现不支持外部内存映射");
        }

        /// <summary>
        /// 取消映射
        /// </summary>
        public void Unmap(ulong va, ulong size)
        {
            _backingMemory.Decommit(va, size);
            // 清除每个页面的保护状态
            for (ulong offset = 0; offset < size; offset += PageSize)
            {
                _currentProtections.Remove(va + offset);
            }
        }

        /// <summary>
        /// 获取物理地址对应的Memory对象
        /// </summary>
        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
        {
            return _backingMemory.GetMemory((ulong)pa, size);
        }

        /// <summary>
        /// 获取物理地址对应的Span对象
        /// </summary>
        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
        {
            return _backingMemory.GetSpan((ulong)pa, size);
        }

        /// <summary>
        /// 检查并转换虚拟地址
        /// </summary>
        protected override nuint TranslateVirtualAddressChecked(ulong va)
        {
            if (!IsMapped(va))
            {
                throw new InvalidMemoryRegionException($"虚拟地址 0x{va:X} 未映射");
            }
            return (nuint)va;
        }

        /// <summary>
        /// 不检查直接转换虚拟地址
        /// </summary>
        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
        {
            return (nuint)va;
        }

        /// <summary>
        /// 检查地址是否已映射
        /// </summary>
        public override bool IsMapped(ulong va)
        {
            return _backingMemory.GetPointer(va, 1) != IntPtr.Zero;
        }

        /// <summary>
        /// 检查地址范围是否已映射
        /// </summary>
        public bool IsRangeMapped(ulong va, ulong size)
        {
            for (ulong offset = 0; offset < size; offset += PageSize)
            {
                if (_backingMemory.GetPointer(va + offset, 1) == IntPtr.Zero)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 安全检查地址范围是否已映射
        /// </summary>
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

        /// <summary>
        /// 触发内存跟踪
        /// </summary>
        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            if (!IsRangeMappedSafe(va, size))
            {
                return;
            }
            // 内存跟踪逻辑将在这里实现
        }

        /// <summary>
        /// 修改内存保护状态
        /// </summary>
        public void Reprotect(ulong va, ulong size, MemoryPermission protection)
        {
            _backingMemory.Reprotect(va, size, protection, true);
        }

        /// <summary>
        /// 跟踪并修改内存保护状态
        /// </summary>
        public void TrackingReprotect(ulong va, ulong size, MemoryPermission protection, bool guest)
        {
            for (ulong offset = 0; offset < size; offset += PageSize)
            {
                ulong pageVa = va + offset;
                if (!_currentProtections.TryGetValue(pageVa, out var current) || current != protection)
                {
                    _backingMemory.Reprotect(pageVa, PageSize, protection, guest);
                    _currentProtections[pageVa] = protection;
                }
            }
        }

        /// <summary>
        /// 获取主机内存区域
        /// </summary>
        public IEnumerable<HostMemoryRange> GetHostRegions(ulong va, ulong size)
        {
            yield return new HostMemoryRange((nuint)va, size);
        }

        /// <summary>
        /// 获取物理内存区域
        /// </summary>
        public IEnumerable<MemoryRange> GetPhysicalRegions(ulong va, ulong size)
        {
            yield return new MemoryRange(va, size);
        }

        /// <summary>
        /// 获取指定类型的引用
        /// </summary>
        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            if (!IsMapped(va))
            {
                throw new InvalidMemoryRegionException();
            }
            return ref _backingMemory.GetRef<T>(va);
        }

        /// <summary>
        /// 填充内存
        /// </summary>
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

        /// <summary>
        /// 带冗余检查的写入
        /// </summary>
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

        /// <summary>
        /// 释放资源
        /// </summary>
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

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 将内存权限转换为内存块保护
        /// </summary>
        private static MemoryBlockProtection ConvertToMemoryBlockProtection(MemoryPermission permission)
        {
            return permission switch
            {
                MemoryPermission.None => MemoryBlockProtection.None,
                MemoryPermission.Read => MemoryBlockProtection.ReadOnly,
                MemoryPermission.Write => MemoryBlockProtection.ReadWrite, // 通常不支持单独写权限
                MemoryPermission.Execute => MemoryBlockProtection.Execute,
                MemoryPermission.ReadAndWrite => MemoryBlockProtection.ReadWrite,
                MemoryPermission.ReadAndExecute => MemoryBlockProtection.ReadExecute,
                MemoryPermission.ReadWriteExecute => MemoryBlockProtection.ReadWriteExecute,
                _ => throw new ArgumentException($"无效的内存权限: {permission}")
            };
        }
    }
}
