using ARMeilleure.Memory;
using Ryujinx.Memory;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.Nce
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("android")]
    [SupportedOSPlatform("windows")]
    class NceMemoryAllocator : IJitMemoryAllocator
    {
        public IJitMemoryBlock Allocate(ulong size) => new NceMemoryBlock(size, MemoryAllocationFlags.None);
        public IJitMemoryBlock Reserve(ulong size) => new NceMemoryBlock(size, MemoryAllocationFlags.Reserve);

        public ulong GetPageSize() => MemoryBlock.GetPageSize();
    }
}
