using Ryujinx.Common;
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
        private readonly bool _ignoreNullAccess;

        private readonly ulong _pageSize;
        private readonly ulong _baseAddress;
        private readonly ulong _mirrorAddress;

        public MemoryEhMeilleure(MemoryBlock addressSpace, MemoryBlock addressSpaceMirror, MemoryTracking tracking, 
                               TrackingEventDelegate trackingEvent = null, bool ignoreNullAccess = false)
        {
            _baseAddress = (ulong)addressSpace.Pointer;
            ulong endAddress = _baseAddress + addressSpace.Size;

            _tracking = tracking;
            _trackingEvent = trackingEvent ?? VirtualMemoryEvent;
            _ignoreNullAccess = ignoreNullAccess;
            _pageSize = MemoryBlock.GetPageSize();

            bool added = NativeSignalHandler.AddTrackedRegion((nuint)_baseAddress, (nuint)endAddress, 
                           Marshal.GetFunctionPointerForDelegate(_trackingEvent));

            if (!added)
            {
                throw new InvalidOperationException("Number of allowed tracked regions exceeded.");
            }

            if (OperatingSystem.IsWindows() && addressSpaceMirror != null)
            {
                _mirrorAddress = (ulong)addressSpaceMirror.Pointer;
                ulong endAddressMirror = _mirrorAddress + addressSpace.Size;

                bool addedMirror = NativeSignalHandler.AddTrackedRegion((nuint)_mirrorAddress, 
                                  (nuint)endAddressMirror, IntPtr.Zero);

                if (!addedMirror)
                {
                    throw new InvalidOperationException("Number of allowed tracked regions exceeded.");
                }
            }
        }

        public MemoryEhMeilleure(ulong asSize, MemoryTracking tracking, bool ignoreNullAccess = false)
        {
            _tracking = tracking;
            _baseAddress = 0UL;
            ulong endAddress = asSize;
            _ignoreNullAccess = ignoreNullAccess;

            _trackingEvent = VirtualMemoryEvent;
            _pageSize = MemoryBlock.GetPageSize();

            bool added = NativeSignalHandler.AddTrackedRegion((nuint)_baseAddress, (nuint)endAddress, 
                           Marshal.GetFunctionPointerForDelegate(_trackingEvent));

            if (!added)
            {
                throw new InvalidOperationException("Number of allowed tracked regions exceeded.");
            }
        }

        private ulong VirtualMemoryEvent(ulong address, ulong size, bool write)
        {
            // 新增：NULL访问特殊处理
            if (address == 0 && _ignoreNullAccess)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"Ignored NULL access at 0x0, size 0x{size:X}");
                return _baseAddress; // 返回基地址而非0
            }

            ulong pageSize = _pageSize;
            ulong addressAligned = BitUtils.AlignDown(address, pageSize);
            ulong endAddressAligned = BitUtils.AlignUp(address + size, pageSize);
            ulong sizeAligned = endAddressAligned - addressAligned;

            if (_tracking.VirtualMemoryEvent(addressAligned, sizeAligned, write))
            {
                return _baseAddress + address;
            }

            return 0;
        }

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
