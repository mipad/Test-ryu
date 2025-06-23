using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ARMeilleure.Native
{
    [SupportedOSPlatform("android")]
    internal static class JitSupportAndroid
    {
        // Linux/Android 系统调用常量
        private const int PROT_READ = 0x1;
        private const int PROT_WRITE = 0x2;
        private const int PROT_EXEC = 0x4;
        private const int CACHEFLUSH_FLAGS = 0; // 0 表示同时失效指令和数据缓存

        [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
        private static extern int MProtect(IntPtr addr, IntPtr size, int prot);

        [DllImport("libc", EntryPoint = "cacheflush", SetLastError = true)]
        private static extern int CacheFlush(IntPtr start, IntPtr length, int flags);

        /// <summary>
        /// 复制内存并确保指令缓存一致性（Android 实现）。
        /// </summary>
        public static unsafe void Copy(IntPtr dst, IntPtr src, ulong n)
        {
            // 临时禁用目标内存的写保护
            SetWriteProtect(dst, n, enable: false);

            // 执行内存复制
            var srcSpan = new Span<byte>(src.ToPointer(), (int)n);
            var dstSpan = new Span<byte>(dst.ToPointer(), (int)n);
            srcSpan.CopyTo(dstSpan);

            // 恢复写保护
            SetWriteProtect(dst, n, enable: true);

            // 失效指令缓存
            InvalidateCache(dst, n);
        }

        /// <summary>
        /// 设置内存区域的写保护状态。
        /// </summary>
        public static void SetWriteProtect(IntPtr address, ulong size, bool enable)
        {
            int prot = enable ? (PROT_READ | PROT_EXEC) : (PROT_READ | PROT_WRITE | PROT_EXEC);
            int result = MProtect(address, (IntPtr)size, prot);
            if (result != 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        /// <summary>
        /// 失效指定内存区域的指令缓存。
        /// </summary>
        public static void InvalidateCache(IntPtr start, ulong length)
        {
            int result = CacheFlush(start, (IntPtr)length, CACHEFLUSH_FLAGS);
            if (result != 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}
