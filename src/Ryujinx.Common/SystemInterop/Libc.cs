using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ryujinx.Common.System
{
    public static class Libc
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ref ulong mask);

        public static void SetAffinity(int pid, long affinityMask)
        {
            // Android 上需要特殊处理
            if (OperatingSystem.IsAndroid())
            {
                // Android 使用不同的系统调用
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

        [DllImport("libc", SetLastError = true, EntryPoint = "sched_setaffinity")]
        private static extern int sched_setaffinity_android(int pid, IntPtr cpusetsize, byte[] mask);

        private static void SetAffinityAndroid(int pid, long affinityMask)
        {
            // Android 使用字节数组而不是位掩码
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

            int result = sched_setaffinity_android(pid, (IntPtr)mask.Length, mask);
            if (result != 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"sched_setaffinity failed on Android (Error: {error})");
            }
        }
    }
}
