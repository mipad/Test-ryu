using ARMeilleure.Memory;
using Ryujinx.Memory;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.Jit
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("android")]
    public class JitMemoryBlock : IJitMemoryBlock
    {
        private readonly MemoryBlock _impl;

        public IntPtr Pointer => _impl.Pointer;

        public JitMemoryBlock(ulong size, MemoryAllocationFlags flags)
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
            {
                throw new PlatformNotSupportedException();
            }

            _impl = new MemoryBlock(size, flags);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("android")]
        public void Commit(ulong offset, ulong size) => _impl.Commit(offset, size);

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("android")]
        [SupportedOSPlatform("macos")]
        public void MapAsRw(ulong offset, ulong size) => _impl.Reprotect(offset, size, MemoryPermission.ReadAndWrite);

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("android")]
        [SupportedOSPlatform("macos")]
        public void MapAsRx(ulong offset, ulong size) => _impl.Reprotect(offset, size, MemoryPermission.ReadAndExecute);

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("android")]
        [SupportedOSPlatform("macos")]
        public void MapAsRwx(ulong offset, ulong size) => _impl.Reprotect(offset, size, MemoryPermission.ReadWriteExecute);

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("android")]
        [SupportedOSPlatform("macos")]
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _impl.Dispose();
        }
    }
}
