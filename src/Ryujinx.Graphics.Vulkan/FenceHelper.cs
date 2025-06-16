using Silk.NET.Vulkan;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan
{
    static class FenceHelper
    {
        private const ulong DefaultTimeout = 100000000; // 100ms
        private static readonly Dictionary<Fence, long> _fenceTimestamps = new();
        private static bool _isMobileDevice = false;
        
        public static void Initialize(uint vendorId)
        {
            _isMobileDevice = vendorId == 0x13B5;
            Logger.Info?.Print(LogClass.Gpu, 
                $"FenceHelper initialized for {(_isMobileDevice ? "Mobile (Mali)" : "Desktop")} device");
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
            // 修复：添加 UL 后缀确保字面量为 ulong 类型
            ulong baseTimeout = _isMobileDevice ? 30_000_000_000UL : 10_000_000_000UL;
            
            int attempt = 0;
            while (true)
            {
                ulong currentTimeout = CalculateExponentialTimeout(baseTimeout, attempt);
                
                long startTime = Stopwatch.GetTimestamp();
                
                Result result = api.WaitForFences(device, (uint)fences.Length, fences, true, currentTimeout);
                
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
                        
                        try
                        {
                            api.ResetFences(device, (uint)fences.Length, fences);
                            Logger.Debug?.Print(LogClass.Gpu, 
                                $"Reset {fences.Length} fences successfully");
                        }
                        catch (VulkanException ex)
                        {
                            Logger.Error?.Print(LogClass.Gpu, 
                                $"Fence reset failed: {ex.Message}");
                        }
                        
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

        private static ulong CalculateExponentialTimeout(ulong baseTimeout, int attempt)
        {
            const int maxShift = 62;
            
            if (attempt > maxShift)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Exponential timeout capped at {maxShift} attempts");
                return ulong.MaxValue;
            }
            
            ulong multiplier = 1UL << Math.Min(attempt, maxShift);
            
            if (multiplier > ulong.MaxValue / baseTimeout)
            {
                return ulong.MaxValue;
            }
            
            return baseTimeout * multiplier;
        }
        
        public static void TrackFenceSubmission(Fence fence)
        {
            lock (_fenceTimestamps)
            {
                _fenceTimestamps[fence] = Stopwatch.GetTimestamp();
            }
        }
        
        public static void StartFenceMonitor(Vk api, Device device)
        {
            var monitorThread = new Thread(() =>
            {
                const long TimeoutTicks = 15 * 10_000_000;
                
                while (true)
                {
                    Thread.Sleep(5000);
                    
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
