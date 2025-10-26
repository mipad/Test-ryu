using Ryujinx.Memory;
using System;

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

            bool isAndroid = OperatingSystem.IsAndroid();
            
            if (isAndroid)
            {
                Logger.Info?.Print(LogClass.Cpu, $"Android: Attempting to allocate address space 0x{asSize:X}");
            }

            // 安卓平台：使用更积极的分配策略
            ulong[] sizesToTry = isAndroid ? 
                new ulong[] { asSize, 0x4000000000UL, 0x3000000000UL, 0x2100000000UL, 0x1000000000UL } :
                new ulong[] { asSize, 0x2100000000UL, 0x1000000000UL };

            foreach (ulong addressSpaceSize in sizesToTry)
            {
                try
                {
                    MemoryBlock baseMemory = new MemoryBlock(addressSpaceSize, AsFlags);
                    addressSpace = baseMemory;
                    
                    if (isAndroid)
                    {
                        Logger.Info?.Print(LogClass.Cpu, 
                            $"Android: Successfully allocated address space: 0x{addressSpaceSize:X} " +
                            $"(requested: 0x{asSize:X})");
                    }
                    else
                    {
                        Logger.Info?.Print(LogClass.Cpu, 
                            $"Successfully allocated address space: 0x{addressSpaceSize:X} " +
                            $"(requested: 0x{asSize:X})");
                    }
                    
                    break;
                }
                catch (SystemException ex)
                {
                    if (isAndroid)
                    {
                        Logger.Debug?.Print(LogClass.Cpu, 
                            $"Android: Failed to allocate 0x{addressSpaceSize:X} address space: {ex.Message}");
                    }
                    else
                    {
                        Logger.Debug?.Print(LogClass.Cpu, 
                            $"Failed to allocate 0x{addressSpaceSize:X} address space: {ex.Message}");
                    }
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
