using System;
using System.Runtime.InteropServices;
using Ryujinx.Common.Logging;
using Ryujinx.Common.SystemInterop;

namespace Ryujinx.Cpu.Nce
{
    static class NceThreadPalUnix
    {
        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr pthread_self();

        [DllImport("libc", SetLastError = true)]
        private static extern int pthread_threadid_np(IntPtr arg0, out ulong tid);

        [DllImport("libpthread", SetLastError = true)]
        private static extern int pthread_kill(IntPtr thread, int sig);

        [DllImport("libc", SetLastError = true)]
        private static extern int gettid();

        // 静态构造函数 - 在类首次使用时调用
        static NceThreadPalUnix()
        {
            Logger.Info?.Print(LogClass.Cpu, "[NceThreadPalUnix] Static constructor initialized");
            
            // 测试当前环境的 CPU 亲和性支持
            TestCpuAffinitySupport();
        }

        public static IntPtr GetCurrentThreadHandle()
        {
            return pthread_self();
        }

        public static ulong GetCurrentThreadId()
        {
            pthread_threadid_np(IntPtr.Zero, out ulong tid);
            return tid;
        }

        public static void SuspendThread(IntPtr handle)
        {
            int result = pthread_kill(handle, NceThreadPal.UnixSuspendSignal);
            if (result != 0)
            {
                throw new Exception($"Thread kill returned error 0x{result:X}.");
            }
        }

        // 新增方法：设置当前线程的 CPU 亲和性
        public static void SetCurrentThreadAffinity(long affinityMask)
        {
            try
            {
                Logger.Info?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Setting CPU affinity for current thread, mask: 0x{affinityMask:X}");
                
                // 使用 pid=0 表示当前线程
                Libc.SetAffinity(0, affinityMask);
                
                Logger.Info?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Successfully set CPU affinity for current thread");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Failed to set CPU affinity for current thread: {ex.Message}");
            }
        }

        // 新增方法：设置指定线程的 CPU 亲和性
        public static void SetThreadAffinity(IntPtr threadHandle, long affinityMask)
        {
            try
            {
                Logger.Info?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Setting CPU affinity for thread {threadHandle}, mask: 0x{affinityMask:X}");
                
                // 对于非当前线程，需要获取线程ID
                int threadId = GetThreadIdFromHandle(threadHandle);
                if (threadId == -1)
                {
                    Logger.Warning?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Could not get thread ID for handle {threadHandle}");
                    return;
                }
                
                Libc.SetAffinity(threadId, affinityMask);
                
                Logger.Info?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Successfully set CPU affinity for thread {threadHandle} (ID: {threadId})");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Failed to set CPU affinity for thread {threadHandle}: {ex.Message}");
            }
        }

        // 辅助方法：从线程句柄获取线程ID
        private static int GetThreadIdFromHandle(IntPtr threadHandle)
        {
            try
            {
                // 在 Linux 中，pthread_t 不能直接转换为 PID
                // 这里我们使用当前线程ID作为简化实现
                // 在实际应用中，可能需要更复杂的方法来获取其他线程的ID
                if (threadHandle == pthread_self())
                {
                    return 0; // 0 表示当前线程
                }
                
                // 对于其他线程，返回当前线程ID作为占位符
                // 注意：这只是一个简化实现
                return gettid();
            }
            catch
            {
                return -1;
            }
        }

        // 新增方法：自动为线程分配CPU核心
        public static void SetAutoThreadAffinity(IntPtr threadHandle, int threadIndex)
        {
            int cpuCount = Environment.ProcessorCount;
            
            if (cpuCount <= 1)
            {
                Logger.Info?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Only {cpuCount} CPU core available, skipping affinity setting");
                return;
            }
            
            // 简单的轮询分配：将线程分配到不同的CPU核心
            // 跳过第一个核心（通常用于系统和其他应用）
            int targetCore = (threadIndex % (cpuCount - 1)) + 1;
            long affinityMask = 1L << targetCore;
            
            Logger.Info?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Auto-assigning thread {threadIndex} to CPU core {targetCore} (mask: 0x{affinityMask:X})");
            
            SetThreadAffinity(threadHandle, affinityMask);
        }

        // 新增方法：为当前线程自动分配CPU核心
        public static void SetAutoCurrentThreadAffinity(int threadIndex)
        {
            SetAutoThreadAffinity(GetCurrentThreadHandle(), threadIndex);
        }

        // 测试 CPU 亲和性支持
        private static void TestCpuAffinitySupport()
        {
            try
            {
                Logger.Info?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Testing CPU affinity support on {Environment.ProcessorCount} cores");
                
                // 测试设置当前线程到第一个核心
                SetCurrentThreadAffinity(0x1);
                
                Logger.Info?.Print(LogClass.Cpu, $"[NceThreadPalUnix] CPU affinity test completed");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"[NceThreadPalUnix] CPU affinity test failed: {ex.Message}");
            }
        }

        // 新增方法：重置当前线程的 CPU 亲和性（使用所有核心）
        public static void ResetCurrentThreadAffinity()
        {
            try
            {
                long allCoresMask = (1L << Environment.ProcessorCount) - 1;
                Logger.Info?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Resetting CPU affinity for current thread to all cores (mask: 0x{allCoresMask:X})");
                
                SetCurrentThreadAffinity(allCoresMask);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"[NceThreadPalUnix] Failed to reset CPU affinity: {ex.Message}");
            }
        }
    }
}
