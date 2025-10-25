using Ryujinx.Memory;
using System;
using Ryujinx.Common.Logging;

namespace Ryujinx.Cpu
{
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

            // Android 特定优化：不减少地址空间大小，直接使用请求的大小
            if (OperatingSystem.IsAndroid() && asSize >= 0x8000000000UL)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Android address space override: Attempting to allocate full requested size 0x{asSize:X}");

                try
                {
                    MemoryBlock baseMemory = new MemoryBlock(asSize, AsFlags);
                    addressSpace = baseMemory;
                    Logger.Info?.Print(LogClass.Cpu, 
                        $"Android address space allocation successful: 0x{asSize:X}");
                }
                catch (SystemException ex)
                {
                    Logger.Warning?.Print(LogClass.Cpu, 
                        $"Android address space allocation failed for 0x{asSize:X}: {ex.Message}");
                    
                    // 如果直接分配失败，尝试使用较大的分配而不是逐步减小
                    ulong fallbackSize = 0x4000000000UL; // 256GB
                    Logger.Warning?.Print(LogClass.Cpu, 
                        $"Trying fallback size: 0x{fallbackSize:X}");
                    
                    try
                    {
                        MemoryBlock baseMemory = new MemoryBlock(fallbackSize, AsFlags);
                        addressSpace = baseMemory;
                        Logger.Info?.Print(LogClass.Cpu, 
                            $"Android address space fallback allocation successful: 0x{fallbackSize:X}");
                    }
                    catch (SystemException ex2)
                    {
                        Logger.Error?.Print(LogClass.Cpu, 
                            $"Android address space fallback allocation also failed: {ex2.Message}");
                    }
                }

                return addressSpace != null;
            }

            // 原有逻辑（非Android平台）
            ulong minAddressSpaceSize = Math.Min(asSize, 1UL << 36);

            // Attempt to create the address space with expected size or try to reduce it until it succeed.
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
            Base.MapView(_backingMemory, pa, va, size);
            Mirror.MapView(_backingMemory, pa, va, size);
        }

        public void Unmap(ulong va, ulong size)
        {
            Base.UnmapView(_backingMemory, va, size);
            Mirror.UnmapView(_backingMemory, va, size);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            Base.Dispose();
            Mirror.Dispose();
        }
    }
}
