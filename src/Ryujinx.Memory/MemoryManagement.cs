using System;
using System.Runtime.Versioning;

namespace Ryujinx.Memory
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("ios")]
    [SupportedOSPlatform("android")]
    public static class MemoryManagement
    {
        private static bool IsUnixPlatform()
        {
            return OperatingSystem.IsLinux() || 
                   OperatingSystem.IsMacOS() || 
                   OperatingSystem.IsIOS() || 
                   OperatingSystem.IsAndroid() || // .NET 6+原生支持
                   Ryujinx.Common.PlatformInfo.IsBionic; // 兼容旧版本Android检测
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static IntPtr Allocate(ulong size, bool forJit)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.Allocate((IntPtr)size);
            }
            else if (IsUnixPlatform())
            {
                return MemoryManagementUnix.Allocate(size, forJit);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static IntPtr Reserve(ulong size, bool forJit, bool viewCompatible)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.Reserve((IntPtr)size, viewCompatible);
            }
            else if (IsUnixPlatform())
            {
                return MemoryManagementUnix.Reserve(size, forJit);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static void Commit(IntPtr address, ulong size, bool forJit)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.Commit(address, (IntPtr)size);
            }
            else if (IsUnixPlatform())
            {
                MemoryManagementUnix.Commit(address, size, forJit);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static void Decommit(IntPtr address, ulong size)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.Decommit(address, (IntPtr)size);
            }
            else if (IsUnixPlatform())
            {
                MemoryManagementUnix.Decommit(address, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static void MapView(IntPtr sharedMemory, ulong srcOffset, IntPtr address, ulong size, MemoryBlock owner)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.MapView(sharedMemory, srcOffset, address, (IntPtr)size, owner);
            }
            else if (IsUnixPlatform())
            {
                MemoryManagementUnix.MapView(sharedMemory, srcOffset, address, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static void UnmapView(IntPtr sharedMemory, IntPtr address, ulong size, MemoryBlock owner)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.UnmapView(sharedMemory, address, (IntPtr)size, owner);
            }
            else if (IsUnixPlatform())
            {
                MemoryManagementUnix.UnmapView(address, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static void Reprotect(IntPtr address, ulong size, MemoryPermission permission, bool forView, bool throwOnFail)
        {
            bool result;

            if (OperatingSystem.IsWindows())
            {
                result = MemoryManagementWindows.Reprotect(address, (IntPtr)size, permission, forView);
            }
            else if (IsUnixPlatform())
            {
                result = MemoryManagementUnix.Reprotect(address, size, permission);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            if (!result && throwOnFail)
            {
                throw new MemoryProtectionException(permission);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static bool Free(IntPtr address, ulong size)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.Free(address, (IntPtr)size);
            }
            else if (IsUnixPlatform())
            {
                return MemoryManagementUnix.Free(address);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static IntPtr CreateSharedMemory(ulong size, bool reserve)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.CreateSharedMemory((IntPtr)size, reserve);
            }
            else if (IsUnixPlatform())
            {
                return MemoryManagementUnix.CreateSharedMemory(size, reserve);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static void DestroySharedMemory(IntPtr handle)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.DestroySharedMemory(handle);
            }
            else if (IsUnixPlatform())
            {
                MemoryManagementUnix.DestroySharedMemory(handle);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static IntPtr MapSharedMemory(IntPtr handle, ulong size)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.MapSharedMemory(handle);
            }
            else if (IsUnixPlatform())
            {
                return MemoryManagementUnix.MapSharedMemory(handle, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("ios")]
        [SupportedOSPlatform("android")]
        public static void UnmapSharedMemory(IntPtr address, ulong size)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.UnmapSharedMemory(address);
            }
            else if (IsUnixPlatform())
            {
                MemoryManagementUnix.UnmapSharedMemory(address, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }
    }
}
