using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap
{
    internal class NvMapDeviceFile : NvDeviceFile
    {
        private const int FlagNotFreedYet = 1;
        private const uint PageSize = 0x1000; // 4KB 页大小常量

        private static readonly NvMapIdDictionary _maps = new();

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
                    case 0x02:
                    case 0x06:
                    case 0x07:
                    case 0x08:
                    case 0x0a:
                    case 0x0c:
                    case 0x0d:
                    case 0x0f:
                    case 0x10:
                    case 0x11:
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

            uint size = BitUtils.AlignUp(arguments.Size, PageSize); // 使用PageSize常量

            arguments.Handle = CreateHandleFromMap(new NvMapHandle(size));

            Logger.Debug?.Print(LogClass.ServiceNv, $"Created map {arguments.Handle} with size 0x{size:x8}!");

            return NvInternalResult.Success;
        }

        private NvInternalResult FromId(ref NvMapFromId arguments)
        {
            NvMapHandle map = GetMapFromHandle(Owner, arguments.Id);

            if (map == null)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{arguments.Handle:x8}!");

                return NvInternalResult.InvalidInput;
            }

            map.IncrementRefCount();

            arguments.Handle = arguments.Id;

            return NvInternalResult.Success;
        }

        private NvInternalResult Alloc(ref NvMapAlloc arguments)
        {
            NvMapHandle map = GetMapFromHandle(Owner, arguments.Handle);

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

            if ((uint)arguments.Align < PageSize) // 使用PageSize常量
            {
                arguments.Align = (int)PageSize;
            }

            NvInternalResult result = NvInternalResult.Success;

            if (!map.Allocated)
            {
                map.Allocated = true;

                map.Align = arguments.Align;
                map.Kind = (byte)arguments.Kind;

                uint size = BitUtils.AlignUp(map.Size, PageSize); // 使用PageSize常量

                ulong address = arguments.Address;

                if (address == 0)
                {
                    try 
                    {
                        // 分配内存并显式转换为 ulong
                        nint allocatedMemory = MemoryManagement.Allocate((nint)(long)size, false);
                        address = (ulong)allocatedMemory; // 显式转换
                        
                        Logger.Debug?.Print(LogClass.ServiceNv, 
                            $"Allocated physical memory: 0x{address:X} for map {arguments.Handle}");
                    }
                    catch (OutOfMemoryException)
                    {
                        Logger.Error?.Print(LogClass.ServiceNv, 
                            $"Failed to allocate physical memory for map {arguments.Handle}");
                        return NvInternalResult.OutOfMemory;
                    }
                }

                // 验证地址有效性
                if (address == 0)
                {
                    Logger.Error?.Print(LogClass.ServiceNv, 
                        "Rejected NULL physical address allocation!");
                    return NvInternalResult.InvalidAddress;
                }

                map.Size = size;
                map.Address = address; // 设置有效物理地址
            }

            return result;
        }

        private NvInternalResult Free(ref NvMapFree arguments)
        {
            NvMapHandle map = GetMapFromHandle(Owner, arguments.Handle);

            if (map == null)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{arguments.Handle:x8}!");

                return NvInternalResult.InvalidInput;
            }

            bool freed = DecrementMapRefCount(Owner, arguments.Handle);
            
            if (freed)
            {
                arguments.Address = map.Address;
                arguments.Flags = 0;
            }
            else
            {
                arguments.Address = 0;
                arguments.Flags = FlagNotFreedYet;
            }

            arguments.Size = map.Size;

            return NvInternalResult.Success;
        }

        private NvInternalResult Param(ref NvMapParam arguments)
        {
            NvMapHandle map = GetMapFromHandle(Owner, arguments.Handle);

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

                // 注意：不支持Base参数
                default:
                    return NvInternalResult.InvalidInput;
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult GetId(ref NvMapGetId arguments)
        {
            NvMapHandle map = GetMapFromHandle(Owner, arguments.Handle);

            if (map == null)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{arguments.Handle:x8}!");

                return NvInternalResult.InvalidInput;
            }

            arguments.Id = arguments.Handle;

            return NvInternalResult.Success;
        }

        public override void Close()
        {
            // TODO: 实现引用计数
        }

        private int CreateHandleFromMap(NvMapHandle map)
        {
            return _maps.Add(map);
        }

        private static bool DeleteMapWithHandle(ulong pid, int handle)
        {
            NvMapHandle map = _maps.Delete(handle);
            
            if (map != null)
            {
                // 释放物理内存
                if (map.Address != 0)
                {
                    nint ptr = (nint)(long)map.Address;
                    MemoryManagement.Free(ptr, (ulong)map.Size);
                    Logger.Debug?.Print(LogClass.ServiceNv, $"Freed physical memory: 0x{map.Address:X} for map {handle}");
                }
                return true;
            }
            
            return false;
        }

        public static void IncrementMapRefCount(ulong pid, int handle)
        {
            GetMapFromHandle(pid, handle)?.IncrementRefCount();
        }

        public static bool DecrementMapRefCount(ulong pid, int handle)
        {
            NvMapHandle map = GetMapFromHandle(pid, handle);

            if (map == null)
            {
                return false;
            }

            if (map.DecrementRefCount() <= 0)
            {
                DeleteMapWithHandle(pid, handle);

                Logger.Debug?.Print(LogClass.ServiceNv, $"Deleted map {handle}!");

                return true;
            }
            else
            {
                return false;
            }
        }

        public static NvMapHandle GetMapFromHandle(ulong pid, int handle)
        {
            NvMapHandle map = _maps.Get(handle);
            
            // 验证映射地址有效性
            if (map != null && map.Address == 0)
            {
                Logger.Error?.Print(LogClass.ServiceNv, 
                    $"NvMap handle 0x{handle:X} has NULL physical address!");
                return null;
            }
            
            return map;
        }
    }
}
