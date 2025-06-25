using Ryujinx.Common.Logging;
using Ryujinx.Common; // 添加 PlatformInfo 引用
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text; // 添加字符串编码支持
using static Ryujinx.Memory.MemoryManagerUnixHelper;

// 禁用平台兼容性警告，因为我们在Unix系统上确保正确性
#pragma warning disable CA1416

namespace Ryujinx.Memory
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("android")]
    static class MemoryManagementUnix
    {
        private static readonly ConcurrentDictionary<IntPtr, ulong> _allocations = new();

        public static IntPtr Allocate(ulong size, bool forJit)
        {
            return AllocateInternal(size, MmapProts.PROT_READ | MmapProts.PROT_WRITE, forJit);
        }

        public static IntPtr Reserve(ulong size, bool forJit)
        {
            return AllocateInternal(size, MmapProts.PROT_NONE, forJit);
        }

        private static IntPtr AllocateInternal(ulong size, MmapProts prot, bool forJit, bool shared = false)
        {
            MmapFlags flags = MmapFlags.MAP_ANONYMOUS;

            if (shared)
            {
                flags |= MmapFlags.MAP_SHARED | MmapFlags.MAP_UNLOCKED;
            }
            else
            {
                flags |= MmapFlags.MAP_PRIVATE;
            }

            if (prot == MmapProts.PROT_NONE)
            {
                flags |= MmapFlags.MAP_NORESERVE;
            }

            if (OperatingSystem.IsMacOSVersionAtLeast(10, 14) && forJit)
            {
                flags |= MmapFlags.MAP_JIT_DARWIN;

                if (prot == (MmapProts.PROT_READ | MmapProts.PROT_WRITE))
                {
                    prot |= MmapProts.PROT_EXEC;
                }
            }

            IntPtr ptr = Mmap(IntPtr.Zero, size, prot, flags, -1, 0);

            if (ptr == MAP_FAILED)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            if (!_allocations.TryAdd(ptr, size))
            {
                // This should be impossible, kernel shouldn't return an already mapped address.
                throw new InvalidOperationException();
            }

            return ptr;
        }

        public static void Commit(IntPtr address, ulong size, bool forJit)
        {
            MmapProts prot = MmapProts.PROT_READ | MmapProts.PROT_WRITE;

            if (OperatingSystem.IsMacOSVersionAtLeast(10, 14) && forJit)
            {
                prot |= MmapProts.PROT_EXEC;
            }

            if (mprotect(address, size, prot) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }
        }

        public static void Decommit(IntPtr address, ulong size)
        {
            // Must be writable for madvise to work properly.
            if (mprotect(address, size, MmapProts.PROT_READ | MmapProts.PROT_WRITE) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            int advice;
            if (OperatingSystem.IsMacOS())
            {
                advice = MADV_FREE; // 使用新定义的 macOS 常量
            }
            else
            {
                advice = MADV_DONTNEED; // Linux/Android 使用 MADV_DONTNEED
            }

            if (madvise(address, size, advice) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            if (mprotect(address, size, MmapProts.PROT_NONE) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }
        }

        public static bool Reprotect(IntPtr address, ulong size, MemoryPermission permission)
        {
            return mprotect(address, size, GetProtection(permission)) == 0;
        }

        private static MmapProts GetProtection(MemoryPermission permission)
        {
            return permission switch
            {
                MemoryPermission.None => MmapProts.PROT_NONE,
                MemoryPermission.Read => MmapProts.PROT_READ,
                MemoryPermission.ReadAndWrite => MmapProts.PROT_READ | MmapProts.PROT_WRITE,
                MemoryPermission.ReadAndExecute => MmapProts.PROT_READ | MmapProts.PROT_EXEC,
                MemoryPermission.ReadWriteExecute => MmapProts.PROT_READ | MmapProts.PROT_WRITE | MmapProts.PROT_EXEC,
                MemoryPermission.Execute => MmapProts.PROT_EXEC,
                _ => throw new MemoryProtectionException(permission),
            };
        }

        public static bool Free(IntPtr address)
        {
            if (_allocations.TryRemove(address, out ulong size))
            {
                return munmap(address, size) == 0;
            }

            return false;
        }

        public static bool Unmap(IntPtr address, ulong size)
        {
            return munmap(address, size) == 0;
        }

        public unsafe static IntPtr CreateSharedMemory(ulong size, bool reserve)
        {
            // 使用常量提高可读性
            const int O_RDWR = 0x02;
            const int O_CREAT = 0x200;
            const int O_EXCL = 0x800;
            const int O_TRUNC = 0x400;
            const int S_IRUSR = 0x100;  // 用户读权限
            const int S_IWUSR = 0x80;   // 用户写权限
            const int S_IRGRP = 0x20;   // 组读权限
            const int S_IWGRP = 0x10;   // 组写权限
            
            int fd = -1;
            string tempName = $"/Kenji-NX-{Guid.NewGuid():N}"; // 生成唯一名称

            if (OperatingSystem.IsMacOS())
            {
                byte[] memName = Encoding.ASCII.GetBytes(tempName + '\0');

                fixed (byte* pMemName = memName)
                {
                    // 使用组合权限常量
                    int flags = O_RDWR | O_CREAT | O_EXCL | O_TRUNC;
                    uint mode = (uint)(S_IRUSR | S_IWUSR); // 修正为 uint 类型

                    fd = shm_open((IntPtr)pMemName, flags, mode);
                    if (fd == -1)
                    {
                        throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
                    }

                    if (shm_unlink((IntPtr)pMemName) != 0)
                    {
                        throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
                    }
                }
            }
            else if (PlatformInfo.IsBionic) // 使用 PlatformInfo
            {
                byte[] memName = Encoding.ASCII.GetBytes(tempName + '\0');

                Logger.Debug?.Print(LogClass.Cpu, $"Creating Android SharedMemory of size:{size}");

                fixed (byte* pMemName = memName)
                {
                    fd = ASharedMemory_create((IntPtr)pMemName, (nuint)size);
                    if (fd <= 0)
                    {
                        throw new OutOfMemoryException();
                    }
                }

                // ASharedMemory_create handle ftruncate for us.
                return (IntPtr)fd;
            }
            else
            {
                string fileName = $"/dev/shm/Kenji-NX-{Guid.NewGuid():N}";
                byte[] fileNameBytes = Encoding.ASCII.GetBytes(fileName + '\0');

                fixed (byte* pFileName = fileNameBytes)
                {
                    fd = mkstemp((IntPtr)pFileName);
                    if (fd == -1)
                    {
                        throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
                    }

                    if (unlink((IntPtr)pFileName) != 0)
                    {
                        throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
                    }
                }
            }

            if (ftruncate(fd, (IntPtr)size) != 0)
            {
                throw new SystemException(Marshal.GetLastPInvokeErrorMessage());
            }

            return fd;
        }

        public static void DestroySharedMemory(IntPtr handle)
        {
            close(handle.ToInt32());
        }

        public static IntPtr MapSharedMemory(IntPtr handle, ulong size)
        {
            return Mmap(IntPtr.Zero, size, MmapProts.PROT_READ | MmapProts.PROT_WRITE, MmapFlags.MAP_SHARED, handle.ToInt32(), 0);
        }

        public static void UnmapSharedMemory(IntPtr address, ulong size)
        {
            munmap(address, size);
        }

        public static void MapView(IntPtr sharedMemory, ulong srcOffset, IntPtr location, ulong size)
        {
            Mmap(location, size, MmapProts.PROT_READ | MmapProts.PROT_WRITE, MmapFlags.MAP_FIXED | MmapFlags.MAP_SHARED, sharedMemory.ToInt32(), (long)srcOffset);
        }

        public static void UnmapView(IntPtr location, ulong size)
        {
            Mmap(location, size, MmapProts.PROT_NONE, MmapFlags.MAP_FIXED | MmapFlags.MAP_PRIVATE | MmapFlags.MAP_ANONYMOUS | MmapFlags.MAP_NORESERVE, -1, 0);
        }
    }
}
