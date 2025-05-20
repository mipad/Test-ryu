using Silk.NET.Vulkan;
using System;

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

        // FenceHelper.cs
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
                Logger.Warning?.Print(LogClass.Gpu, "Vulkan同步超时，尝试重置设备...");
                ResetDevice(device);
                break;
            default:
                throw new VulkanException(result);
        }
    }
}

private static void ResetDevice(Device device)
{
    // 1. 清理所有待处理命令
    CommandBufferPool.ForceCleanup();
    
    // 2. 重置逻辑设备
    vkResetDevice(device);
    
    // 3. 重建关键资源（管道、描述符池等）
    RebuildPipelines();
}
    }
}
