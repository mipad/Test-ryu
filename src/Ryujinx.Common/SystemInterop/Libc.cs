using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ryujinx.Common.SystemInterop
{
    public static class Libc
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ref ulong mask);

        public static void SetAffinity(int pid, long affinityMask)
        {
            // 在Android上，特别是AOT环境，需要更谨慎的处理
            if (OperatingSystem.IsAndroid() || PlatformInfo.IsBionic)
            {
                // Android/Bionic 使用不同的系统调用和参数格式
                SetAffinityAndroid(pid, affinityMask);
                return;
            }

            ulong mask = (ulong)affinityMask;
            int result = sched_setaffinity(pid, (IntPtr)sizeof(ulong), ref mask);
            if (result != 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"sched_setaffinity failed (Error: {error})");
            }
        }

        // Android/Bionic 特定的实现
        [DllImport("libc", SetLastError = true, EntryPoint = "sched_setaffinity")]
        private static extern int sched_setaffinity_bionic(int pid, IntPtr cpusetsize, byte[] mask);

        private static void SetAffinityAndroid(int pid, long affinityMask)
        {
            try
            {
                int cpuCount = Environment.ProcessorCount;
                byte[] mask = new byte[(cpuCount + 7) / 8];
                
                // 将 affinityMask 转换为字节数组
                for (int i = 0; i < cpuCount; i++)
                {
                    if ((affinityMask & (1L << i)) != 0)
                    {
                        int byteIndex = i / 8;
                        int bitIndex = i % 8;
                        mask[byteIndex] |= (byte)(1 << bitIndex);
                    }
                }

                int result = sched_setaffinity_bionic(pid, (IntPtr)mask.Length, mask);
                if (result != 0)
                {
                    // 在Android上这通常是权限问题，不抛出异常而是记录日志
                    System.Diagnostics.Debug.WriteLine($"sched_setaffinity failed on Android: No permission or not supported");
                    return; // 静默失败，因为在Android上这很常见
                }
            }
            catch (Exception ex)
            {
                // 在AOT环境中，P/Invoke可能失败，记录但不抛出
                System.Diagnostics.Debug.WriteLine($"SetAffinity failed on Android: {ex.Message}");
            }
        }

        // 备用方案：使用syscall直接调用
        [DllImport("libc", SetLastError = true)]
        private static extern int syscall(int number, IntPtr pid, IntPtr cpusetsize, IntPtr mask);

        private static void SetAffinitySyscall(int pid, long affinityMask)
        {
            try
            {
                // sched_setaffinity 的系统调用号 (架构相关)
                int SYS_sched_setaffinity = 241; // 对于ARM64
                
                int cpuCount = Environment.ProcessorCount;
                byte[] maskBytes = new byte[(cpuCount + 7) / 8];
                
                for (int i = 0; i < cpuCount; i++)
                {
                    if ((affinityMask & (1L << i)) != 0)
                    {
                        int byteIndex = i / 8;
                        int bitIndex = i % 8;
                        maskBytes[byteIndex] |= (byte)(1 << bitIndex);
                    }
                }

                IntPtr maskPtr = Marshal.AllocHGlobal(maskBytes.Length);
                Marshal.Copy(maskBytes, 0, maskPtr, maskBytes.Length);
                
                int result = syscall(SYS_sched_setaffinity, (IntPtr)pid, (IntPtr)maskBytes.Length, maskPtr);
                
                Marshal.FreeHGlobal(maskPtr);
                
                if (result != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"syscall sched_setaffinity failed");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetAffinitySyscall failed: {ex.Message}");
            }
        }
    }
}
