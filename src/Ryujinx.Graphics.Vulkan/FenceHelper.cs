using Silk.NET.Vulkan;
using System;
// 添加日志命名空间（如果存在）
// using Ryujinx.Common.Logging; 

namespace Ryujinx.Graphics.Vulkan
{
    static class FenceHelper
    {
        private const ulong DefaultTimeout = 100000000; // 100ms

        public static bool AnySignaled(Vk api, Device device, ReadOnlySpan<Fence> fences, ulong timeout = 0)
        {
            return api.WaitForFences(device, (uint)fences.Length, fences, false, timeout) == Result.Success;
        }

        public static bool AllSignaled(Vk api, Device device, ReadOnlySpan<Fence> fences, ulong timeout = 0)
        {
            return api.WaitForFences(device, (uint)fences.Length, fences, true, timeout) == Result.Success;
        }
        
         public static void WaitAllIndefinitely(Vk api, Device device, ReadOnlySpan<Fence> fences)
{
    const ulong timeout = 1_000_000_000; // 1秒（单位：纳秒）
    
    while (true)
    {
        Result result = api.WaitForFences(device, fences, true, timeout);
        
        switch (result)
        {
            case Result.Success:
                return;
            case Result.Timeout:
                // 1. 记录超时警告
                //Logger.Warning?.Print(LogClass.Gpu, "Vulkan同步超时，尝试重置围栏...");
                api.ResetFences(device, (uint)fences.Length, fences);
                break;
            case Result.ErrorDeviceLost:
                // 2. 设备丢失时主动抛出异常，触发设备重置
                //Logger.Error?.Print(LogClass.Gpu, "Vulkan设备丢失，需要重建设备！");
                throw new VulkanException(result);
            default:
                throw new VulkanException(result);
        }
    }
         }
}      
}

