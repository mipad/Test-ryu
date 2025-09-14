using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostAsGpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrlGpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostDbgGpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostProfGpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Ryujinx.HLE.HOS.Services.Nv
{
    [Service("nvdrv")]
    [Service("nvdrv:a")]
    [Service("nvdrv:s")]
    [Service("nvdrv:t")]
    class INvDrvServices : IpcService
    {
        private static readonly List<string> _deviceFileDebugRegistry = new()
        {
            "/dev/nvhost-dbg-gpu",
            "/dev/nvhost-prof-gpu",
        };

        private static readonly Dictionary<string, Type> _deviceFileRegistry = new()
        {
            { "/dev/nvmap",           typeof(NvMapDeviceFile)         },
            { "/dev/nvhost-ctrl",     typeof(NvHostCtrlDeviceFile)    },
            { "/dev/nvhost-ctrl-gpu", typeof(NvHostCtrlGpuDeviceFile) },
            { "/dev/nvhost-as-gpu",   typeof(NvHostAsGpuDeviceFile)   },
            { "/dev/nvhost-gpu",      typeof(NvHostGpuDeviceFile)     },
            //{ "/dev/nvhost-msenc",    typeof(NvHostChannelDeviceFile) },
            { "/dev/nvhost-nvdec",    typeof(NvHostChannelDeviceFile) },
            //{ "/dev/nvhost-nvjpg",    typeof(NvHostChannelDeviceFile) },
            { "/dev/nvhost-vic",      typeof(NvHostChannelDeviceFile) },
            //{ "/dev/nvhost-display",  typeof(NvHostChannelDeviceFile) },
            { "/dev/nvhost-dbg-gpu",  typeof(NvHostDbgGpuDeviceFile)  },
            { "/dev/nvhost-prof-gpu", typeof(NvHostProfGpuDeviceFile) },
        };

        // 添加一个缓存来存储构造函数信息
        private static readonly Dictionary<Type, ConstructorInfo> _constructorCache = new();

        public static IdDictionary DeviceFileIdRegistry = new();

        private IVirtualMemoryManager _clientMemory;
        private ulong _owner;

        private bool _transferMemInitialized = false;

        // TODO: This should call set:sys::GetDebugModeFlag
        private readonly bool _debugModeEnabled = false;

        public INvDrvServices(ServiceCtx context) : base(context.Device.System.NvDrvServer)
        {
            _owner = 0;
        }

        private NvResult Open(ServiceCtx context, string path, out int fd)
        {
            fd = -1;

            if (!_debugModeEnabled && _deviceFileDebugRegistry.Contains(path))
            {
                return NvResult.NotSupported;
            }

            if (_deviceFileRegistry.TryGetValue(path, out Type deviceFileClass))
            {
                // 使用缓存的构造函数信息
                if (!_constructorCache.TryGetValue(deviceFileClass, out ConstructorInfo constructor))
                {
                    // 如果没有缓存，则获取并缓存构造函数
                    constructor = deviceFileClass.GetConstructor(
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(ServiceCtx), typeof(IVirtualMemoryManager), typeof(ulong) },
                        null);
                    
                    _constructorCache[deviceFileClass] = constructor;
                }

                if (constructor != null)
                {
                    NvDeviceFile deviceFile = (NvDeviceFile)constructor.Invoke(new object[] { context, _clientMemory, _owner });

                    deviceFile.Path = path;

                    fd = DeviceFileIdRegistry.Add(deviceFile);

                    return NvResult.Success;
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Cannot find constructor for device file \"{path}\"!");
                    return NvResult.FileOperationFailed;
                }
            }

            Logger.Warning?.Print(LogClass.ServiceNv, $"Cannot find file device \"{path}\"!");

            return NvResult.FileOperationFailed;
        }

        // ... 其余代码保持不变 ...

        public static void Destroy()
        {
            NvHostChannelDeviceFile.Destroy();

            foreach (object entry in DeviceFileIdRegistry.Values)
            {
                NvDeviceFile deviceFile = (NvDeviceFile)entry;

                deviceFile.Close();
            }

            DeviceFileIdRegistry.Clear();
        }
    }
}
