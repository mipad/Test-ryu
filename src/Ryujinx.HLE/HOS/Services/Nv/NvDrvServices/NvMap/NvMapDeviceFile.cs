using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.Memory;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap
{
    // 将原来 Types.cs 中的定义移到这里
    [StructLayout(LayoutKind.Sequential)]
    public struct NvMapCreate
    {
        public uint Size;
        public int Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvMapFromId
    {
        public int Id;
        public int Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvMapAlloc
    {
        public int Handle;
        public int Align;
        public int Kind;
        public ulong Address;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvMapFree
    {
        public int Handle;
        public uint Size;
        public ulong Address;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvMapParam
    {
        public int Handle;
        public NvMapHandleParam Param;
        public int Result;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvMapGetId
    {
        public int Handle;
        public int Id;
    }

    public enum NvMapHandleParam
    {
        Size = 1,
        Align = 2,
        Heap = 3,
        Kind = 4,
        Compr = 5
    }

    public enum NvIoctl
    {
        NvMapCustomMagic = 0x42
    }

    public enum NvInternalResult
    {
        Success = 0,
        NotImplemented = -1,
        NotSupported = -2,
        InvalidInput = -3,
        InvalidAddress = -4,
        OutOfMemory = -5,
        GenericError = -6
    }

    internal class NvMapHandle
    {
        public uint Size { get; set; }
        public ulong Address { get; set; }
        public int Align { get; set; }
        public byte Kind { get; set; }
        public bool Allocated { get; set; }
        private int _refCount;

        public NvMapHandle(uint size)
        {
            Size = size;
            _refCount = 1;
        }

        public void IncrementRefCount() => Interlocked.Increment(ref _refCount);
        
        public int DecrementRefCount() => Interlocked.Decrement(ref _refCount);
    }

    internal class NvMapIdDictionary
    {
        private int _nextId = 1;
        private readonly Dictionary<int, NvMapHandle> _dictionary = new Dictionary<int, NvMapHandle>();

        public int Add(NvMapHandle map)
        {
            int id = _nextId++;
            _dictionary.Add(id, map);
            return id;
        }

        public NvMapHandle Get(int id)
        {
            _dictionary.TryGetValue(id, out var map);
            return map;
        }

        public NvMapHandle Delete(int id)
        {
            if (_dictionary.TryGetValue(id, out var map))
            {
                _dictionary.Remove(id);
                return map;
            }
            return null;
        }
    }

    internal class NvMapDeviceFile : NvDeviceFile
    {
        private const int FlagNotFreedYet = 1;
        private const uint PageSize = 0x1000;
        private const ulong InvalidAddress = 0;

        private static readonly NvMapIdDictionary _maps = new NvMapIdDictionary();
        private static readonly object _lock = new object();

        public NvMapDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner)
        {
        }

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvMapCustomMagic)
            {
                switch (command.Number)
                {
                    case 0x01:
                        result = CallIoctlMethod<NvMapCreate>(Create, arguments);
                        break;
                    case 0x03:
                        result = CallIoctlMethod<NvMapFromId>(FromId, arguments);
                        break;
                    case 0x04:
                        result = CallIoctlMethod<NvMapAlloc>(Alloc, arguments);
                        break;
                    case 0x05:
                        result = CallIoctlMethod<NvMapFree>(Free, arguments);
                        break;
                    case 0x09:
                        result = CallIoctlMethod<NvMapParam>(Param, arguments);
                        break;
                    case 0x0e:
                        result = CallIoctlMethod<NvMapGetId>(GetId, arguments);
                        break;
                    default:
                        Logger.Warning?.Print(LogClass.ServiceNv, $"Unsupported NvMap ioctl command: 0x{command.Number:x2}");
                        result = NvInternalResult.NotSupported;
                        break;
                }
            }

            return result;
        }

        private NvInternalResult Create(ref NvMapCreate arguments)
        {
            if (arguments.Size == 0)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid size 0x{arguments.Size:x8}!");
                return NvInternalResult.InvalidInput;
            }

            uint size = BitUtils.AlignUp(arguments.Size, PageSize);

            lock (_lock)
            {
                arguments.Handle = _maps.Add(new NvMapHandle(size));
            }

            Logger.Debug?.Print(LogClass.ServiceNv, $"Created map {arguments.Handle} with size 0x{size:x8}!");

            return NvInternalResult.Success;
        }

        private NvInternalResult FromId(ref NvMapFromId arguments)
        {
            lock (_lock)
            {
                NvMapHandle map = _maps.Get(arguments.Id);

                if (map == null)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{arguments.Handle:x8}!");
                    return NvInternalResult.InvalidInput;
                }

                map.IncrementRefCount();
                arguments.Handle = arguments.Id;
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult Alloc(ref NvMapAlloc arguments)
        {
            NvInternalResult result = NvInternalResult.Success;

            lock (_lock)
            {
                NvMapHandle map = _maps.Get(arguments.Handle);

                if (map == null)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{arguments.Handle:x8}!");
                    return NvInternalResult.InvalidInput;
                }

                if ((arguments.Align & (arguments.Align - 1)) != 0)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid alignment 0x{arguments.Align:x8}!");
                    return NvInternalResult.InvalidInput;
                }

                if ((uint)arguments.Align < PageSize)
                {
                    arguments.Align = (int)PageSize;
                }

                if (!map.Allocated)
                {
                    map.Allocated = true;
                    map.Align = arguments.Align;
                    map.Kind = (byte)arguments.Kind;

                    uint size = BitUtils.AlignUp(map.Size, PageSize);
                    ulong address = arguments.Address;

                    if (address == InvalidAddress)
                    {
                        try
                        {
                            IntPtr allocatedMemory = MemoryManagement.Allocate((IntPtr)(long)size, false);
                            if (allocatedMemory == IntPtr.Zero)
                            {
                                Logger.Error?.Print(LogClass.ServiceNv, $"Memory allocation failed for size 0x{size:X}");
                                return NvInternalResult.OutOfMemory;
                            }

                            address = unchecked((ulong)allocatedMemory.ToInt64());
                            Logger.Debug?.Print(LogClass.ServiceNv, 
                                $"Allocated physical memory: 0x{address:X} for map {arguments.Handle}");
                        }
                        catch (OutOfMemoryException ex)
                        {
                            Logger.Error?.Print(LogClass.ServiceNv, 
                                $"Failed to allocate physical memory for map {arguments.Handle}: {ex.Message}");
                            return NvInternalResult.OutOfMemory;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.ServiceNv, 
                                $"Unexpected error during memory allocation: {ex}");
                            return NvInternalResult.GenericError;
                        }
                    }

                    if (address == InvalidAddress)
                    {
                        Logger.Error?.Print(LogClass.ServiceNv, "Rejected NULL physical address allocation!");
                        return NvInternalResult.InvalidAddress;
                    }

                    map.Size = size;
                    map.Address = address;
                }
            }

            return result;
        }

        private NvInternalResult Free(ref NvMapFree arguments)
        {
            lock (_lock)
            {
                NvMapHandle map = _maps.Get(arguments.Handle);

                if (map == null)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{arguments.Handle:x8}!");
                    return NvInternalResult.InvalidInput;
                }

                if (map.DecrementRefCount() <= 0)
                {
                    if (map.Address != InvalidAddress)
                    {
                        try
                        {
                            IntPtr ptr = new IntPtr((long)map.Address);
                            MemoryManagement.Free(ptr, (ulong)map.Size);
                            Logger.Debug?.Print(LogClass.ServiceNv, 
                                $"Freed physical memory: 0x{map.Address:X} for map {arguments.Handle}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.ServiceNv, 
                                $"Error freeing memory for map {arguments.Handle}: {ex}");
                        }
                    }
                    
                    _maps.Delete(arguments.Handle);
                    arguments.Address = map.Address;
                    arguments.Flags = 0;
                    Logger.Debug?.Print(LogClass.ServiceNv, $"Deleted map {arguments.Handle}!");
                }
                else
                {
                    arguments.Address = 0;
                    arguments.Flags = FlagNotFreedYet;
                }

                arguments.Size = map.Size;
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult Param(ref NvMapParam arguments)
        {
            lock (_lock)
            {
                NvMapHandle map = _maps.Get(arguments.Handle);

                if (map == null)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{arguments.Handle:x8}!");
                    return NvInternalResult.InvalidInput;
                }

                switch (arguments.Param)
                {
                    case NvMapHandleParam.Size:
                        arguments.Result = (int)map.Size;
                        break;
                    case NvMapHandleParam.Align:
                        arguments.Result = map.Align;
                        break;
                    case NvMapHandleParam.Heap:
                        arguments.Result = 0x40000000;
                        break;
                    case NvMapHandleParam.Kind:
                        arguments.Result = map.Kind;
                        break;
                    case NvMapHandleParam.Compr:
                        arguments.Result = 0;
                        break;
                    default:
                        Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid param type: {arguments.Param}");
                        return NvInternalResult.InvalidInput;
                }
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult GetId(ref NvMapGetId arguments)
        {
            lock (_lock)
            {
                NvMapHandle map = _maps.Get(arguments.Handle);

                if (map == null)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{arguments.Handle:x8}!");
                    return NvInternalResult.InvalidInput;
                }

                arguments.Id = arguments.Handle;
            }

            return NvInternalResult.Success;
        }

        public override void Close()
        {
            Logger.Debug?.Print(LogClass.ServiceNv, $"NvMapDeviceFile closed for owner 0x{Owner:X}");
        }

        public static void IncrementMapRefCount(ulong pid, int handle)
        {
            lock (_lock)
            {
                _maps.Get(handle)?.IncrementRefCount();
            }
        }

        public static bool DecrementMapRefCount(ulong pid, int handle)
        {
            lock (_lock)
            {
                NvMapHandle map = _maps.Get(handle);

                if (map == null)
                {
                    return false;
                }

                if (map.DecrementRefCount() <= 0)
                {
                    if (map.Address != InvalidAddress)
                    {
                        try
                        {
                            IntPtr ptr = new IntPtr((long)map.Address);
                            MemoryManagement.Free(ptr, (ulong)map.Size);
                            Logger.Debug?.Print(LogClass.ServiceNv, 
                                $"Freed physical memory: 0x{map.Address:X} for map {handle}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.ServiceNv, 
                                $"Error freeing memory for map {handle}: {ex}");
                        }
                    }
                    
                    _maps.Delete(handle);
                    Logger.Debug?.Print(LogClass.ServiceNv, $"Deleted map {handle}!");
                    return true;
                }

                return false;
            }
        }

        public static NvMapHandle GetMapFromHandle(ulong pid, int handle)
        {
            NvMapHandle map = _maps.Get(handle);
            
            if (map != null && map.Address == InvalidAddress)
            {
                Logger.Error?.Print(LogClass.ServiceNv, 
                    $"NvMap handle 0x{handle:X} has NULL physical address!");
                return null;
            }
            
            return map;
        }
    }
}
