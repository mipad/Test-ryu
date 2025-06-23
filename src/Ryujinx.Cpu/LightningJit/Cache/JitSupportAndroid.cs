using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LightningJit.Cache
{
    [SupportedOSPlatform("android")]
    internal static class JitSupportAndroid
    {
        // Linux/Android 系统调用常量
        private const int PROT_READ = 0x1;
        private const int PROT_WRITE = 0x2;
        private const int PROT_EXEC = 0x4;
        private const int CACHEFLUSH_FLAGS = 0; // 0 表示同时失效指令和数据缓存

        // 内存复制（使用 Span 实现，无需依赖外部库）
        public static unsafe void Copy(IntPtr dst, IntPtr src, ulong n)
        {
            var srcSpan = new Span<byte>(src.ToPointer(), (int)n);
            var dstSpan = new Span<byte>(dst.ToPointer(), (int)n);
            srcSpan.CopyTo(dstSpan);
        }

        // 失效指令缓存（Android/Linux 实现）
        [DllImport("libc", EntryPoint = "cacheflush", SetLastError = true)]
        public static extern void SysIcacheInvalidate(IntPtr start, IntPtr len);

        // 可选：动态设置内存保护（如需 JIT 写保护）
        [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
        private static extern int MProtect(IntPtr addr, IntPtr size, int prot);

        public static void SetWriteProtect(IntPtr address, ulong size, bool enable)
        {
            int prot = enable ? (PROT_READ | PROT_EXEC) : (PROT_READ | PROT_WRITE | PROT_EXEC);
            int result = MProtect(address, (IntPtr)size, prot);
            if (result != 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}
