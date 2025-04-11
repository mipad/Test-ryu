using Ryujinx.Memory;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("android")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    public class AddressSpace : IDisposable
    {
        private const MemoryAllocationFlags AsFlags = MemoryAllocationFlags.Reserve | MemoryAllocationFlags.ViewCompatible;

        private readonly MemoryBlock _backingMemory;

        public MemoryBlock Base { get; }
        public MemoryBlock Mirror { get; }

        public ulong AddressSpaceSize { get; }

        public AddressSpace(MemoryBlock backingMemory, MemoryBlock baseMemory, MemoryBlock mirrorMemory, ulong addressSpaceSize)
        {
            _backingMemory = backingMemory;
            Base = baseMemory;
            Mirror = mirrorMemory;
            AddressSpaceSize = addressSpaceSize;
        }

        public static bool TryCreate(MemoryBlock backingMemory, ulong asSize, out AddressSpace addressSpace)
     {
         addressSpace = null;

         if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() && 
             !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
         {
             return false;
         }

         // 保持参数对齐
         MemoryBlock baseMemory = null;
         MemoryBlock mirrorMemory = null;

         try
         {
             baseMemory = new MemoryBlock(asSize, AsFlags);
             mirrorMemory = new MemoryBlock(asSize, AsFlags);
             addressSpace = new AddressSpace(backingMemory, baseMemory, mirrorMemory, asSize);
         }
         catch (SystemException)
         {
             baseMemory?.Dispose();
             mirrorMemory?.Dispose();
         }

         return addressSpace != null;
     }

        public static bool TryCreateWithoutMirror(ulong asSize, out MemoryBlock addressSpace)
     {
         addressSpace = null;

         // 仅在入口检查一次平台
         if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() && 
             !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
         {
             return false;
         }

         ulong minAddressSpaceSize = Math.Min(asSize, 1UL << 36);

         for (ulong addressSpaceSize = asSize; addressSpaceSize >= minAddressSpaceSize; addressSpaceSize -= 0x100000000UL)
         {
             try
             {
                 MemoryBlock baseMemory = new MemoryBlock(addressSpaceSize, AsFlags);
                 addressSpace = baseMemory;
                 break;
             }
             catch (SystemException)
             {
             }
         }

         return addressSpace != null;
     }

        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() || OperatingSystem.IsWindows())
            {
                Base.MapView(_backingMemory, pa, va, size);
                Mirror.MapView(_backingMemory, pa, va, size);
            }
            else
            {
                throw new PlatformNotSupportedException("MemoryBlock.MapView is not supported on this platform.");
            }
        }

        public void Unmap(ulong va, ulong size)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() || OperatingSystem.IsWindows())
            {
                Base.UnmapView(_backingMemory, va, size);
                Mirror.UnmapView(_backingMemory, va, size);
            }
            else
            {
                throw new PlatformNotSupportedException("MemoryBlock.UnmapView is not supported on this platform.");
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() || OperatingSystem.IsMacOS())
            {
                Base.Dispose();
                Mirror.Dispose();
            }
            else
            {
                throw new PlatformNotSupportedException("MemoryBlock.Dispose is not supported on this platform.");
            }
        }
    }
}
