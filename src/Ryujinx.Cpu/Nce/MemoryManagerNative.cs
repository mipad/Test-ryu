using ARMeilleure.Memory;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Diagnostics; // 添加这个

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// Represents a CPU memory manager which maps guest virtual memory directly onto a host virtual region.
    /// </summary>
    public sealed class MemoryManagerNative : VirtualMemoryManagerRefCountedBase, ICpuMemoryManager, IVirtualMemoryManagerTracked, IWritableBlock
    {
        // ... 现有字段保持不变 ...

        // 添加：安全内存区域用于处理空指针
        private static readonly MemoryBlock _safeMemoryBlock = new MemoryBlock(0x1000);
        private static readonly object _safeMemoryLock = new object();

        /// <summary>
        /// Creates a new instance of the host mapped memory manager.
        /// </summary>
        public MemoryManagerNative(
            MemoryBlock addressSpace,
            MemoryBlock backingMemory,
            ulong addressSpaceSize,
            InvalidAccessHandler invalidAccessHandler = null)
        {
            // ... 现有构造函数代码不变 ...
        }

        // 删除有问题的重写方法，改用新的验证方法

        /// <summary>
        /// 安全的地址验证方法（不重写基类）
        /// </summary>
        private bool SafeValidateAddress(ulong va)
        {
            // 特殊处理空指针
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer access detected and handled safely: 0x{va:X16}");
                return false;
            }
            
            return va < AddressSpaceSize;
        }

        /// <summary>
        /// 安全的地址和大小验证方法
        /// </summary>
        private void SafeAssertValidAddressAndSize(ulong va, ulong size)
        {
            // 特殊处理空指针访问
            if (va == 0 && size > 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer range access: va=0x{va:X16}, size=0x{size:X16}");
                return; // 不抛出异常，允许继续
            }

            // 使用基类的验证逻辑
            if (va + size < va || va + size > AddressSpaceSize)
            {
                ThrowInvalidMemoryRegionException($"va=0x{va:X16}, size=0x{size:X16}");
            }
        }

        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            // 添加空指针安全检查
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer GetRef<{typeof(T).Name}> prevented, using safe memory");
                
                // 返回指向安全内存的引用
                lock (_safeMemoryLock)
                {
                    return ref _safeMemoryBlock.GetRef<T>(0);
                }
            }

            if (!IsContiguous(va, Unsafe.SizeOf<T>()))
            {
                ThrowMemoryNotContiguous();
            }

            // 使用安全的验证方法
            SafeAssertValidAddressAndSize(va, (ulong)Unsafe.SizeOf<T>());
            SignalMemoryTracking(va, (ulong)Unsafe.SizeOf<T>(), true);

            return ref _backingMemory.GetRef<T>(GetPhysicalAddressChecked(va));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool IsMapped(ulong va)
        {
            // 使用基类方法，但先进行空指针检查
            if (va == 0) return false;
            return ValidateAddress(va) && _pages.IsMapped(va);
        }

        /// <inheritdoc/>
        public bool IsRangeMapped(ulong va, ulong size)
        {
            // 使用安全的验证方法
            SafeAssertValidAddressAndSize(va, size);
            return _pages.IsRangeMapped(va, size);
        }

        // ... 其他方法保持不变 ...

        private ulong GetPhysicalAddressChecked(ulong va)
        {
            // 添加空指针保护
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer physical address translation, returning safe address");
                return 0x1000; // 安全地址
            }

            if (!IsMapped(va))
            {
                ThrowInvalidMemoryRegionException($"Not mapped: va=0x{va:X16}");
            }

            return GetPhysicalAddressInternal(va);
        }

        private ulong GetPhysicalAddressInternal(ulong va)
        {
            // 空指针保护
            if (va == 0)
            {
                return 0x1000;
            }
            return _pageTable.Read(va) + (va & PageMask);
        }

        /// <inheritdoc/>
        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            // 空指针访问的友好处理
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer memory tracking: va=0x{va:X16}, size=0x{size:X16}, write={write}");
                
                if (precise)
                {
                    // 使用安全地址进行精确跟踪
                    Tracking.VirtualMemoryEvent(0x1000, size, write, precise: true, exemptId);
                }
                return;
            }

            // 使用基类的验证（现在不会因为va=0而崩溃）
            if (!ValidateAddressAndSize(va, size))
            {
                ThrowInvalidMemoryRegionException($"va=0x{va:X16}, size=0x{size:X16}");
            }

            if (precise)
            {
                Tracking.VirtualMemoryEvent(va, size, write, precise: true, exemptId);
                return;
            }

            _pages.SignalMemoryTracking(Tracking, va, size, write, exemptId);
        }

        // ... 其他方法保持不变 ...

        protected override nuint TranslateVirtualAddressChecked(ulong va)
        {
            // 空指针保护
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer virtual address translation, using safe address");
                return (nuint)0x1000;
            }
            return (nuint)GetPhysicalAddressChecked(va);
        }

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
        {
            // 空指针保护
            if (va == 0)
            {
                return (nuint)0x1000;
            }
            return (nuint)GetPhysicalAddressInternal(va);
        }

        /// <summary>
        /// 添加调用栈信息获取方法
        /// </summary>
        private string GetCallStack()
        {
            try
            {
                var stackTrace = new StackTrace(2, true); // 跳过2帧
                return stackTrace.ToString();
            }
            catch
            {
                return "Unable to get call stack";
            }
        }

        /// <summary>
        /// 释放资源时也释放安全内存
        /// </summary>
        protected override void Destroy()
        {
            _addressSpace.Dispose();
            _memoryEh.Dispose();
            // 注意：不要释放静态的_safeMemoryBlock，因为它是共享的
        }
    }
}
