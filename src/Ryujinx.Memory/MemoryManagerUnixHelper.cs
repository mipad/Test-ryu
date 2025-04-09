using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Memory
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("ios")]
    [SupportedOSPlatform("android")] // 显式声明支持 Android 平台
    public static partial class MemoryManagerUnixHelper
    {
        [Flags]
        public enum MmapProts : uint
        {
            PROT_NONE = 0,
            PROT_READ = 1,
            PROT_WRITE = 2,
            PROT_EXEC = 4,
        }

        [Flags]
        public enum MmapFlags : uint
        {
            MAP_SHARED = 1,
            MAP_PRIVATE = 2,
            MAP_ANONYMOUS = 4,
            MAP_NORESERVE = 8,
            MAP_FIXED = 16,
            MAP_UNLOCKED = 32,
            MAP_JIT_DARWIN = 0x800,
        }

        [Flags]
        public enum OpenFlags : uint
        {
            O_RDONLY = 0,
            O_WRONLY = 1,
            O_RDWR = 2,
            O_CREAT = 4,
            O_EXCL = 8,
            O_NOCTTY = 16,
            O_TRUNC = 32,
            O_APPEND = 64,
            O_NONBLOCK = 128,
            O_SYNC = 256,
        }

        public const IntPtr MAP_FAILED = -1;

        private const int MAP_ANONYMOUS_LINUX_GENERIC = 0x20;
        private const int MAP_NORESERVE_LINUX_GENERIC = 0x4000;
        private const int MAP_UNLOCKED_LINUX_GENERIC = 0x80000;

        private const int MAP_NORESERVE_DARWIN = 0x40;
        private const int MAP_ANONYMOUS_DARWIN = 0x1000;

        public const int MADV_DONTNEED = 4;
        public const int MADV_REMOVE = 9;

        [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
        private static partial IntPtr Internal_mmap(IntPtr address, ulong length, MmapProts prot, int flags, int fd, long offset);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int mprotect(IntPtr address, ulong length, MmapProts prot);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int munmap(IntPtr address, ulong length);

        [LibraryImport("libc", SetLastError = true)]
        public static partial IntPtr mremap(IntPtr old_address, ulong old_size, ulong new_size, int flags, IntPtr new_address);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int madvise(IntPtr address, ulong size, int advice);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int mkstemp(IntPtr template);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int unlink(IntPtr pathname);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int ftruncate(int fildes, IntPtr length);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int close(int fd);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int shm_open(IntPtr name, int oflag, uint mode);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int shm_unlink(IntPtr name);

        [DllImport("android")]
        internal static extern int ASharedMemory_create(IntPtr name, nuint size);

        private static int MmapFlagsToSystemFlags(MmapFlags flags)
        {
            int result = 0;

            // 处理 MAP_SHARED 和 MAP_PRIVATE（通用逻辑）
            if (flags.HasFlag(MmapFlags.MAP_SHARED))
            {
                result |= (int)MmapFlags.MAP_SHARED;
            }
            if (flags.HasFlag(MmapFlags.MAP_PRIVATE))
            {
                result |= (int)MmapFlags.MAP_PRIVATE;
            }

            // 处理 MAP_FIXED（Android 禁用此标志）
            if (flags.HasFlag(MmapFlags.MAP_FIXED) && !OperatingSystem.IsAndroid())
            {
                result |= (int)MmapFlags.MAP_FIXED;
            }

            // 处理 MAP_ANONYMOUS（明确区分 Android）
            if (flags.HasFlag(MmapFlags.MAP_ANONYMOUS))
            {
                if (OperatingSystem.IsAndroid())
                {
                    // Android 使用与 Linux 相同的标志（需验证 Bionic 实现）
                    result |= MAP_ANONYMOUS_LINUX_GENERIC;
                }
                else if (OperatingSystem.IsLinux())
                {
                    result |= MAP_ANONYMOUS_LINUX_GENERIC;
                }
                else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    result |= MAP_ANONYMOUS_DARWIN;
                }
                else
                {
                    throw new NotImplementedException("Unsupported platform for MAP_ANONYMOUS");
                }
            }

            // 处理 MAP_NORESERVE（Android 禁用）
            if (flags.HasFlag(MmapFlags.MAP_NORESERVE))
            {
                if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
                {
                    result |= MAP_NORESERVE_LINUX_GENERIC;
                }
                else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    result |= MAP_NORESERVE_DARWIN;
                }
                else
                {
                    throw new NotImplementedException("Unsupported platform for MAP_NORESERVE");
                }
            }

            // 处理 MAP_UNLOCKED（Android 禁用）
            if (flags.HasFlag(MmapFlags.MAP_UNLOCKED) && !OperatingSystem.IsAndroid())
            {
                if (OperatingSystem.IsLinux())
                {
                    result |= MAP_UNLOCKED_LINUX_GENERIC;
                }
                else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    // Darwin 无此标志
                }
                else
                {
                    throw new NotImplementedException("Unsupported platform for MAP_UNLOCKED");
                }
            }

            // 处理 MAP_JIT_DARWIN（仅 macOS）
            if (flags.HasFlag(MmapFlags.MAP_JIT_DARWIN) && OperatingSystem.IsMacOSVersionAtLeast(10, 14))
            {
                result |= (int)MmapFlags.MAP_JIT_DARWIN;
            }

            return result;
        }

        public static IntPtr Mmap(IntPtr address, ulong length, MmapProts prot, MmapFlags flags, int fd, long offset)
        {
#if ANDROID
            // Android 专用实现：使用 ASharedMemory_create
            return (IntPtr)ASharedMemory_create(IntPtr.Zero, (nuint)length);
#else
            // 其他平台保持原有逻辑
            return Internal_mmap(address, length, prot, MmapFlagsToSystemFlags(flags), fd, offset);
#endif
        }
    }
}
