using Silk.NET.Vulkan;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using Ryujinx.Common.Logging; // 确保添加日志支持

namespace Ryujinx.Graphics.Vulkan
{
    static class FenceHelper
    {
        private const ulong DefaultTimeout = 100000000; // 100ms
        private static readonly Dictionary<Fence, long> _fenceTimestamps = new();
        private static bool _isMobileDevice = false;
        
        // 初始化设备类型（应在渲染器初始化时调用）
        public static void Initialize(uint vendorId)
        {
            // ARM Mali 的设备厂商ID是 0x13B5
            _isMobileDevice = vendorId == 0x13B5;
            Logger.Info?.Print(LogClass.Gpu, 
                $"FenceHelper initialized for {(isMobileDevice ? "Mobile (Mali)" : "Desktop")} device");
        }

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
            // 根据设备类型设置基础超时（移动设备使用更长超时）
            ulong baseTimeout = _isMobileDevice ? 30_000_000_000 : 10_000_000_000; // 30s/10s
            
            int attempt = 0;
            while (true)
            {
                // 指数退避策略：每次超时后等待时间翻倍
                ulong timeout = baseTimeout * (ulong)Math.Pow(2, attempt);
                
                // 记录等待开始时间
                long startTime = Stopwatch.GetTimestamp();
                
                Result result = api.WaitForFences(device, (uint)fences.Length, fences, true, timeout);
                
                // 计算实际等待时间（毫秒）
                double elapsedMs = (Stopwatch.GetTimestamp() - startTime) * 1000.0 / Stopwatch.Frequency;
                
                switch (result)
                {
                    case Result.Success:
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"Fences signaled after {elapsedMs:F2}ms");
                        return;
                        
                    case Result.Timeout:
                        attempt++;
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"VK Fence timeout (attempt {attempt}, waited {elapsedMs:F2}ms)");
                        
                        // 重置所有围栏避免死锁
                        try
                        {
                            api.ResetFences(device, (uint)fences.Length, fences);
                            Logger.Debug?.Print(LogClass.Gpu, 
                                $"Reset {fences.Length} fences successfully");
                        }
                        catch (VulkanException ex)
                        {
                            Logger.Error?.Print(LogClass.Gpu, 
                                $"Fence reset failed: {ex.Result}");
                        }
                        
                        // 移动设备允许更多重试次数
                        int maxAttempts = _isMobileDevice ? 5 : 3;
                        if (attempt >= maxAttempts)
                        {
                            Logger.Error?.Print(LogClass.Gpu, 
                                $"Abandoning fence wait after {maxAttempts} attempts!");
                            return;
                        }
                        break;
                        
                    case Result.ErrorDeviceLost:
                        Logger.Error?.Print(LogClass.Gpu, 
                            "Vulkan device lost during fence wait!");
                        throw new VulkanException(result);
                        
                    default:
                        Logger.Error?.Print(LogClass.Gpu, 
                            $"Unexpected Vulkan result: {result}");
                        throw new VulkanException(result);
                }
            }
        }
        
        // 记录围栏提交时间（在命令缓冲区提交时调用）
        public static void TrackFenceSubmission(Fence fence)
        {
            lock (_fenceTimestamps)
            {
                _fenceTimestamps[fence] = Stopwatch.GetTimestamp();
            }
        }
        
        // 围栏监控线程（在渲染器初始化时启动）
        public static void StartFenceMonitor(Vk api, Device device)
        {
            var monitorThread = new Thread(() =>
            {
                const long TimeoutTicks = 15 * 10_000_000; // 15秒（以100ns ticks计）
                
                while (true)
                {
                    Thread.Sleep(5000); // 每5秒检查一次
                    
                    lock (_fenceTimestamps)
                    {
                        long now = Stopwatch.GetTimestamp();
                        List<Fence> expiredFences = new();
                        
                        foreach (var kvp in _fenceTimestamps)
                        {
                            if (now - kvp.Value > TimeoutTicks)
                            {
                                Logger.Warning?.Print(LogClass.Gpu, 
                                    $"Fence 0x{kvp.Key.Handle:X} stuck for >15s, force resetting!");
                                expiredFences.Add(kvp.Key);
                            }
                        }
                        
                        foreach (var fence in expiredFences)
                        {
                            try
                            {
                                api.ResetFences(device, 1, fence);
                                _fenceTimestamps.Remove(fence);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error?.Print(LogClass.Gpu, 
                                    $"Force reset failed: {ex.Message}");
                            }
                        }
                    }
                }
            })
            {
                Name = "Vulkan.FenceMonitor",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            
            monitorThread.Start();
            Logger.Info?.Print(LogClass.Gpu, "Fence monitoring thread started");
        }
    }      
}
