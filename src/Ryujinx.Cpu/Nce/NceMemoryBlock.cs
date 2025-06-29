using ARMeilleure.Memory;
using Ryujinx.Memory;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.Nce
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("android")]
    class NceMemoryBlock : IJitMemoryBlock
    {
        private readonly MemoryBlock _impl;

        public IntPtr Pointer => _impl.Pointer;

        public NceMemoryBlock(ulong size, MemoryAllocationFlags flags)
        {
            if (!OperatingSystem.IsWindows() && 
                !OperatingSystem.IsLinux() && 
                !OperatingSystem.IsMacOS() && 
                !OperatingSystem.IsAndroid())
            {
                throw new PlatformNotSupportedException();
            }

            _impl = new MemoryBlock(size, flags);
        }

        public void Commit(ulong offset, ulong size) => _impl.Commit(offset, size);
        public void MapAsRw(ulong offset, ulong size) => _impl.Reprotect(offset, size, MemoryPermission.ReadAndWrite);
        public void MapAsRx(ulong offset, ulong size) => _impl.Reprotect(offset, size, MemoryPermission.ReadAndExecute);
        public void MapAsRwx(ulong offset, ulong size) => _impl.Reprotect(offset, size, MemoryPermission.ReadWriteExecute);

        public void Dispose() => _impl.Dispose();
    }
}
