using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.Cpu.AppleHv;
using Ryujinx.Cpu.Jit;
using Ryujinx.Cpu.LightningJit;
using Ryujinx.Cpu.Nce;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS
{
    class ArmProcessContextFactory : IProcessContextFactory
    {
        private readonly ITickSource _tickSource;
        private readonly GpuContext _gpu;
        private readonly string _titleIdText;
        private readonly string _displayVersion;
        private readonly bool _diskCacheEnabled;
        private readonly ulong _codeAddress;
        private readonly ulong _codeSize;

        public IDiskCacheLoadState DiskCacheLoadState { get; private set; }

        public ArmProcessContextFactory(
            ITickSource tickSource,
            GpuContext gpu,
            string titleIdText,
            string displayVersion,
            bool diskCacheEnabled,
            ulong codeAddress,
            ulong codeSize)
        {
            _tickSource = tickSource;
            _gpu = gpu;
            _titleIdText = titleIdText;
            _displayVersion = displayVersion;
            _diskCacheEnabled = diskCacheEnabled;
            _codeAddress = codeAddress;
            _codeSize = codeSize;
        }

        public static NceCpuCodePatch CreateCodePatchForNce(KernelContext context, bool for64Bit, ReadOnlySpan<byte> textSection)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && for64Bit && context.Device.Configuration.UseHypervisor && !OperatingSystem.IsMacOS())
            {
                return NcePatcher.CreatePatch(textSection);
            }

            return null;
        }

        public IProcessContext Create(KernelContext context, ulong pid, ulong addressSpaceSize, InvalidAccessHandler invalidAccessHandler, bool for64Bit)
        {
            IArmProcessContext processContext;

            bool isArm64Host = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            bool isAndroid = OperatingSystem.IsAndroid();

            // 安卓平台特殊处理：使用更大的地址空间
            if (isAndroid)
            {
                Logger.Info?.Print(LogClass.Cpu, "Android platform detected, applying address space optimizations");
                
                // 在安卓上，尝试分配更大的地址空间
                addressSpaceSize = GetAndroidAddressSpaceSize(addressSpaceSize);
            }

            if (isArm64Host && for64Bit && context.Device.Configuration.UseHypervisor)
            {
                if (OperatingSystem.IsMacOS())
                {
                    var cpuEngine = new HvEngine(_tickSource);
                    var memoryManager = new HvMemoryManager(context.Memory, addressSpaceSize, invalidAccessHandler);
                    processContext = new ArmProcessContext<HvMemoryManager>(pid, cpuEngine, _gpu, memoryManager, addressSpaceSize, for64Bit);
                }
                else
                {
                    if (!AddressSpace.TryCreateWithoutMirror(addressSpaceSize, out var addressSpace))
                    {
                        // 如果分配失败，尝试更小的回退大小
                        Logger.Warning?.Print(LogClass.Cpu, $"Failed to allocate 0x{addressSpaceSize:X} address space, trying fallback sizes");
                        
                        ulong[] fallbackSizes = isAndroid ? 
                            new ulong[] { 0x4000000000UL, 0x3000000000UL, 0x2100000000UL } : // 256GB, 192GB, 132GB
                            new ulong[] { 0x2100000000UL, 0x1000000000UL }; // 132GB, 64GB
                        
                        foreach (ulong fallbackSize in fallbackSizes)
                        {
                            if (AddressSpace.TryCreateWithoutMirror(fallbackSize, out addressSpace))
                            {
                                Logger.Info?.Print(LogClass.Cpu, $"Successfully allocated fallback address space: 0x{fallbackSize:X}");
                                addressSpaceSize = fallbackSize;
                                break;
                            }
                        }
                        
                        if (addressSpace == null)
                        {
                            throw new Exception($"Address space creation failed for all sizes. Requested: 0x{addressSpaceSize:X}");
                        }
                    }
                    else
                    {
                        Logger.Info?.Print(LogClass.Cpu, $"Successfully allocated address space: 0x{addressSpace.Size:X}");
                    }

                    Logger.Info?.Print(LogClass.Cpu, $"NCE Base AS Address: 0x{addressSpace.Pointer.ToInt64():X} Size: 0x{addressSpace.Size:X}");

                    var cpuEngine = new NceEngine(_tickSource);
                    var memoryManager = new MemoryManagerNative(addressSpace, context.Memory, addressSpaceSize, invalidAccessHandler);
                    processContext = new ArmProcessContext<MemoryManagerNative>(pid, cpuEngine, _gpu, memoryManager, addressSpace.Size, for64Bit, memoryManager.ReservedSize);
                }
            }
            else
            {
                MemoryManagerMode mode = context.Device.Configuration.MemoryManagerMode;

                // 安卓平台：优化内存管理器选择
                if (isAndroid)
                {
                    // 在安卓上优先使用 SoftwarePageTable，因为它对地址空间要求较低
                    if (mode == MemoryManagerMode.HostMapped || mode == MemoryManagerMode.HostMappedUnsafe)
                    {
                        Logger.Info?.Print(LogClass.Cpu, "Android: Switching to SoftwarePageTable for better compatibility");
                        mode = MemoryManagerMode.SoftwarePageTable;
                    }
                }

                if (!MemoryBlock.SupportsFlags(MemoryAllocationFlags.ViewCompatible))
                {
                    Logger.Warning?.Print(LogClass.Cpu, "Host system doesn't support views, falling back to software page table");
                    mode = MemoryManagerMode.SoftwarePageTable;
                }

                ICpuEngine cpuEngine = isArm64Host && (mode == MemoryManagerMode.HostMapped || mode == MemoryManagerMode.HostMappedUnsafe)
                    ? new LightningJitEngine(_tickSource)
                    : new JitEngine(_tickSource);

                AddressSpace addressSpace = null;
                MemoryBlock asNoMirror = null;

                // 我们想要使用 host tracked 模式，如果主机页面大小 > 4KB
                if ((mode == MemoryManagerMode.HostMapped || mode == MemoryManagerMode.HostMappedUnsafe) && MemoryBlock.GetPageSize() <= 0x1000)
                {
                    if (!AddressSpace.TryCreate(context.Memory, addressSpaceSize, out addressSpace) &&
                        !AddressSpace.TryCreateWithoutMirror(addressSpaceSize, out asNoMirror))
                    {
                        Logger.Warning?.Print(LogClass.Cpu, "Address space creation failed, falling back to software page table");
                        mode = MemoryManagerMode.SoftwarePageTable;
                    }
                }

                switch (mode)
                {
                    case MemoryManagerMode.SoftwarePageTable:
                        {
                            var mm = new MemoryManager(context.Memory, addressSpaceSize, invalidAccessHandler);
                            processContext = new ArmProcessContext<MemoryManager>(pid, cpuEngine, _gpu, mm, addressSpaceSize, for64Bit);
                        }
                        break;

                    case MemoryManagerMode.HostMapped:
                    case MemoryManagerMode.HostMappedUnsafe:
                        if (addressSpace == null && asNoMirror == null)
                        {
                            var memoryManagerHostTracked = new MemoryManagerHostTracked(context.Memory, addressSpaceSize, mode == MemoryManagerMode.HostMappedUnsafe, invalidAccessHandler);
                            processContext = new ArmProcessContext<MemoryManagerHostTracked>(pid, cpuEngine, _gpu, memoryManagerHostTracked, addressSpaceSize, for64Bit);
                        }
                        else
                        {
                            bool unsafeMode = mode == MemoryManagerMode.HostMappedUnsafe;

                            if (addressSpace != null)
                            {
                                var mm = new MemoryManagerHostMapped(addressSpace, unsafeMode, invalidAccessHandler);
                                processContext = new ArmProcessContext<MemoryManagerHostMapped>(pid, cpuEngine, _gpu, mm, addressSpace.AddressSpaceSize, for64Bit);
                            }
                            else
                            {
                                var mm = new MemoryManagerHostNoMirror(asNoMirror, context.Memory, unsafeMode, invalidAccessHandler);
                                processContext = new ArmProcessContext<MemoryManagerHostNoMirror>(pid, cpuEngine, _gpu, mm, asNoMirror.Size, for64Bit);
                            }
                        }
                        break;

                    default:
                        throw new InvalidOperationException($"{nameof(mode)} contains an invalid value: {mode}");
                }

                if (addressSpaceSize != processContext.AddressSpaceSize)
                {
                    Logger.Warning?.Print(LogClass.Emulation, $"Allocated address space (0x{processContext.AddressSpaceSize:X}) is smaller than guest application requirements (0x{addressSpaceSize:X})");
                    
                    // 安卓平台：这通常不是致命错误，只是警告
                    if (isAndroid)
                    {
                        Logger.Info?.Print(LogClass.Cpu, "Android: Smaller address space is acceptable, continuing execution");
                    }
                }
            }

            DiskCacheLoadState = processContext.Initialize(_titleIdText, _displayVersion, _diskCacheEnabled, _codeAddress, _codeSize);

            return processContext;
        }

        /// <summary>
        /// 为安卓平台获取合适的地址空间大小
        /// </summary>
        private ulong GetAndroidAddressSpaceSize(ulong requestedSize)
        {
            // 在安卓上，我们尝试使用请求的大小，但如果太大则使用合理的最大值
            ulong maxAndroidSize = 0x4000000000UL; // 256GB
            
            if (requestedSize > maxAndroidSize)
            {
                Logger.Info?.Print(LogClass.Cpu, 
                    $"Android: Reducing requested address space from 0x{requestedSize:X} to 0x{maxAndroidSize:X}");
                return maxAndroidSize;
            }
            
            return requestedSize;
        }
    }
}
