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
            const ulong timeout = 1_000_000_000; // 1秒
            
            while (true)
            {
                Result result = api.WaitForFences(device, fences, true, timeout);
                
                switch (result)
                {
                    case Result.Success:
                        return;
                    case Result.Timeout:
                        // 若日志不可用，暂时注释或替换为其他输出
                        // Logger.Warning?.Print(LogClass.Gpu, "Vulkan同步超时，尝试重置设备...");
                        ResetDevice(api, device);
                        break;
                    default:
                        throw new VulkanException(result);
                }
            }
        }

        private static void ResetDevice(Vk api, Device device)
        {
            // 1. 清理命令缓冲区（假设存在其他清理方法）
            // CommandBufferPool.ForceCleanup();

            // 2. 销毁逻辑设备
            api.DestroyDevice(device, allocator: null);

            // 3. 重新创建设备（需项目中提供逻辑，例如：）
            // device = VulkanRenderer.RecreateDevice();

            // 4. 重建资源（需项目中提供逻辑，例如：）
            // PipelineManager.Rebuild();
        }
    }
}
