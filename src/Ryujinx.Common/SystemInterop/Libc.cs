using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Ryujinx.Common.SystemInterop
{
    public static class Libc
    {
        private static readonly bool _isAndroid = OperatingSystem.IsAndroid() || PlatformInfo.IsBionic;

        [DllImport("libc", SetLastError = true)]
        private static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ref ulong mask);

        public static void SetAffinity(int pid, long affinityMask)
        {
            if (_isAndroid)
            {
                Debug.WriteLine($"[Libc] Android/Bionic detected, using Android-specific affinity setting");
                SetAffinityAndroid(pid, affinityMask);
                return;
            }

            Debug.WriteLine($"[Libc] Setting CPU affinity for PID {pid} with mask 0x{affinityMask:X}");

            try
            {
                ulong mask = (ulong)affinityMask;
                int result = sched_setaffinity(pid, (IntPtr)sizeof(ulong), ref mask);
                if (result != 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    string errorMsg = $"[Libc] sched_setaffinity failed for PID {pid} with mask 0x{affinityMask:X} (Error: {error})";
                    Debug.WriteLine(errorMsg);
                    throw new Win32Exception(error, errorMsg);
                }
                else
                {
                    Debug.WriteLine($"[Libc] Successfully set CPU affinity for PID {pid}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Libc] Exception in SetAffinity: {ex}");
                throw;
            }
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "sched_setaffinity")]
        private static extern int sched_setaffinity_bionic(int pid, IntPtr cpusetsize, byte[] mask);

        private static void SetAffinityAndroid(int pid, long affinityMask)
        {
            Debug.WriteLine($"[Libc] Setting Android CPU affinity for PID {pid} with mask 0x{affinityMask:X}");

            try
            {
                int cpuCount = Environment.ProcessorCount;
                Debug.WriteLine($"[Libc] Android CPU count: {cpuCount}");
                
                byte[] mask = new byte[(cpuCount + 7) / 8];
                Debug.WriteLine($"[Libc] Created mask byte array of length {mask.Length}");
                
                // 将 affinityMask 转换为字节数组
                bool hasSetCores = false;
                for (int i = 0; i < cpuCount; i++)
                {
                    if ((affinityMask & (1L << i)) != 0)
                    {
                        int byteIndex = i / 8;
                        int bitIndex = i % 8;
                        mask[byteIndex] |= (byte)(1 << bitIndex);
                        hasSetCores = true;
                        Debug.WriteLine($"[Libc] Set CPU {i} in mask (byteIndex: {byteIndex}, bitIndex: {bitIndex})");
                    }
                }

                if (!hasSetCores)
                {
                    Debug.WriteLine($"[Libc] Warning: No CPU cores were selected in affinity mask 0x{affinityMask:X}");
                    return;
                }

                // 记录mask内容用于调试
                Debug.Write($"[Libc] Mask bytes: ");
                foreach (byte b in mask)
                {
                    Debug.Write($"{b:X2} ");
                }
                Debug.WriteLine("");

                int result = sched_setaffinity_bionic(pid, (IntPtr)mask.Length, mask);
                if (result != 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    string errorMsg = $"[Libc] sched_setaffinity failed on Android for PID {pid} (Error: {error})";
                    Debug.WriteLine(errorMsg);
                    
                    // 在Android上这通常是权限问题，尝试使用syscall作为备用方案
                    Debug.WriteLine($"[Libc] Trying syscall fallback for Android...");
                    SetAffinitySyscall(pid, affinityMask);
                }
                else
                {
                    Debug.WriteLine($"[Libc] Successfully set Android CPU affinity for PID {pid}");
                }
            }
            catch (DllNotFoundException dllEx)
            {
                Debug.WriteLine($"[Libc] DllNotFoundException in SetAffinityAndroid: {dllEx.Message}");
                // 尝试使用syscall作为备用方案
                SetAffinitySyscall(pid, affinityMask);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Libc] Exception in SetAffinityAndroid: {ex}");
                // 尝试使用syscall作为备用方案
                SetAffinitySyscall(pid, affinityMask);
            }
        }

        // 备用方案：使用syscall直接调用
        [DllImport("libc", SetLastError = true)]
        private static extern int syscall(int number, IntPtr pid, IntPtr cpusetsize, IntPtr mask);

        private static void SetAffinitySyscall(int pid, long affinityMask)
        {
            Debug.WriteLine($"[Libc] Using syscall fallback for PID {pid} with mask 0x{affinityMask:X}");

            IntPtr maskPtr = IntPtr.Zero;
            
            try
            {
                // sched_setaffinity 的系统调用号 (架构相关)
                // 注意：不同架构的系统调用号不同，这里使用常见值
                int SYS_sched_setaffinity = 241; // 对于ARM64 Android
                Debug.WriteLine($"[Libc] Using syscall number: {SYS_sched_setaffinity}");
                
                int cpuCount = Environment.ProcessorCount;
                byte[] maskBytes = new byte[(cpuCount + 7) / 8];
                
                bool hasSetCores = false;
                for (int i = 0; i < cpuCount; i++)
                {
                    if ((affinityMask & (1L << i)) != 0)
                    {
                        int byteIndex = i / 8;
                        int bitIndex = i % 8;
                        maskBytes[byteIndex] |= (byte)(1 << bitIndex);
                        hasSetCores = true;
                    }
                }

                if (!hasSetCores)
                {
                    Debug.WriteLine($"[Libc] Warning: No CPU cores selected for syscall fallback");
                    return;
                }

                // 分配非托管内存
                maskPtr = Marshal.AllocHGlobal(maskBytes.Length);
                Marshal.Copy(maskBytes, 0, maskPtr, maskBytes.Length);
                
                int result = syscall(SYS_sched_setaffinity, (IntPtr)pid, (IntPtr)maskBytes.Length, maskPtr);
                
                if (result != 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"[Libc] syscall sched_setaffinity failed (Error: {error})");
                }
                else
                {
                    Debug.WriteLine($"[Libc] Successfully set CPU affinity via syscall for PID {pid}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Libc] Exception in SetAffinitySyscall: {ex}");
            }
            finally
            {
                // 确保释放非托管内存
                if (maskPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(maskPtr);
                    Debug.WriteLine($"[Libc] Freed unmanaged memory for syscall mask");
                }
            }
        }

        // 添加一个简单的检查方法，用于测试是否支持CPU亲和性设置
        public static bool TestAffinitySupport()
        {
            try
            {
                // 尝试设置当前进程到所有CPU（通常应该成功）
                long allCoresMask = (1L << Environment.ProcessorCount) - 1;
                SetAffinity(0, allCoresMask);
                Debug.WriteLine($"[Libc] CPU affinity test successful");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Libc] CPU affinity test failed: {ex.Message}");
                return false;
            }
        }
    }
}
