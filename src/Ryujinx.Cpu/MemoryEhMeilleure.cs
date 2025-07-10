using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using Ryujinx.Memory.Tracking;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu
{
    public class MemoryEhMeilleure : IDisposable
    {
        public delegate ulong TrackingEventDelegate(ulong address, ulong size, bool write);

        private readonly MemoryTracking _tracking;
        private readonly TrackingEventDelegate _trackingEvent;
        private readonly MemoryBlock _backingMemory; // 新增：用于存储支持内存块的引用

        private readonly ulong _pageSize;
        private readonly ulong _baseAddress;
        private readonly ulong _mirrorAddress;

        /// <summary>
        /// 构造函数，初始化内存跟踪和信号处理
        /// </summary>
        public MemoryEhMeilleure(MemoryBlock addressSpace, MemoryBlock addressSpaceMirror, MemoryTracking tracking, TrackingEventDelegate trackingEvent = null)
        {
            _backingMemory = addressSpace; // 初始化支持内存块
            _baseAddress = (ulong)addressSpace.Pointer;
            ulong endAddress = _baseAddress + addressSpace.Size;

            _tracking = tracking;
            _trackingEvent = trackingEvent ?? VirtualMemoryEvent;
            _pageSize = MemoryBlock.GetPageSize();

            // 添加跟踪区域并注册信号处理委托
            bool added = NativeSignalHandler.AddTrackedRegion((nuint)_baseAddress, (nuint)endAddress, Marshal.GetFunctionPointerForDelegate(_trackingEvent));

            if (!added)
            {
                throw new InvalidOperationException("Number of allowed tracked regions exceeded.");
            }

            // Windows系统下处理镜像地址空间
            if (OperatingSystem.IsWindows() && addressSpaceMirror != null)
            {
                _mirrorAddress = (ulong)addressSpaceMirror.Pointer;
                ulong endAddressMirror = _mirrorAddress + addressSpaceMirror.Size;

                bool addedMirror = NativeSignalHandler.AddTrackedRegion((nuint)_mirrorAddress, (nuint)endAddressMirror, IntPtr.Zero);

                if (!addedMirror)
                {
                    throw new InvalidOperationException("Number of allowed tracked regions exceeded.");
                }
            }
        }

        /// <summary>
        /// 构造函数重载，用于无实际内存块的场景
        /// </summary>
        public MemoryEhMeilleure(ulong asSize, MemoryTracking tracking)
        {
            _tracking = tracking;
            _baseAddress = 0UL;
            ulong endAddress = asSize;

            _trackingEvent = VirtualMemoryEvent;
            _pageSize = MemoryBlock.GetPageSize();

            bool added = NativeSignalHandler.AddTrackedRegion((nuint)_baseAddress, (nuint)endAddress, Marshal.GetFunctionPointerForDelegate(_trackingEvent));

            if (!added)
            {
                throw new InvalidOperationException("Number of allowed tracked regions exceeded.");
            }
        }

        /// <summary>
        /// 虚拟内存事件处理，添加了边界检查
        /// </summary>
        private ulong VirtualMemoryEvent(ulong address, ulong size, bool write)
        {
            // 检查访问是否超出地址空间范围
            if (_backingMemory != null && (address < _baseAddress || address + size > _baseAddress + _backingMemory.Size))
            {
                Logger.Warning?.Print(LogClass.Cpu, $"Invalid memory access outside address space: 0x{address:X}, size 0x{size:X}");
                return 0;
            }

            // 按页大小对齐地址和大小
            ulong pageSize = _pageSize;
            ulong addressAligned = BitUtils.AlignDown(address, pageSize);
            ulong endAddressAligned = BitUtils.AlignUp(address + size, pageSize);
            ulong sizeAligned = endAddressAligned - addressAligned;

            // 触发虚拟内存事件
            if (_tracking.VirtualMemoryEvent(addressAligned, sizeAligned, write))
            {
                return _baseAddress + address;
            }

            return 0;
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);

            NativeSignalHandler.RemoveTrackedRegion((nuint)_baseAddress);

            if (_mirrorAddress != 0)
            {
                NativeSignalHandler.RemoveTrackedRegion((nuint)_mirrorAddress);
            }
        }
    }
}
