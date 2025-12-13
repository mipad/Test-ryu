using LibHac.Account;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using LibHac.Loader;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu.Nce;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.Loaders.Executables;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Arp;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using ApplicationId = LibHac.Ncm.ApplicationId;

namespace Ryujinx.HLE.Loaders.Processes
{
    static class ProcessLoaderHelper
    {
        // NOTE: If you want to change this value make sure to increment the InternalVersion of Ptc and PtcProfiler.
        //       You also need to add a new migration path and adjust the existing ones.
        // TODO: Remove this workaround when ASLR is implemented.
        private const ulong CodeStartOffset = 0x500000UL;

        public static LibHac.Result RegisterProgramMapInfo(Switch device, IFileSystem partitionFileSystem)
        {
            ulong applicationId = 0;
            int programCount = 0;

            Span<bool> hasIndex = stackalloc bool[0x10];

            foreach (DirectoryEntryEx fileEntry in partitionFileSystem.EnumerateEntries("/", "*.nca"))
            {
                Nca nca = partitionFileSystem.GetNca(device.FileSystem.KeySet, fileEntry.FullPath);

                if (!nca.IsProgram())
                {
                    continue;
                }

                ulong currentMainProgramId = nca.GetProgramIdBase();

                if (applicationId == 0 && currentMainProgramId != 0)
                {
                    applicationId = currentMainProgramId;
                }

                if (applicationId != currentMainProgramId)
                {
                    // Currently there aren't any known multi-application game cards containing multi-program applications,
                    // so because multi-application game cards are the only way we could run into multiple applications
                    // we'll just return that there's a single program.
                    programCount = 1;

                    break;
                }

                hasIndex[nca.GetProgramIndex()] = true;
            }

            if (programCount == 0)
            {
                for (int i = 0; i < hasIndex.Length && hasIndex[i]; i++)
                {
                    programCount++;
                }
            }

            if (programCount <= 0)
            {
                return LibHac.Result.Success;
            }

            Span<ProgramIndexMapInfo> mapInfo = stackalloc ProgramIndexMapInfo[0x10];

            for (int i = 0; i < programCount; i++)
            {
                mapInfo[i].ProgramId = new ProgramId(applicationId + (uint)i);
                mapInfo[i].MainProgramId = new ApplicationId(applicationId);
                mapInfo[i].ProgramIndex = (byte)i;
            }

            return device.System.LibHacHorizonManager.NsClient.Fs.RegisterProgramIndexMapInfo(mapInfo[..programCount]);
        }

        public static LibHac.Result EnsureSaveData(Switch device, ApplicationId applicationId, BlitStruct<ApplicationControlProperty> applicationControlProperty)
        {
            Logger.Info?.Print(LogClass.Application, "Ensuring required savedata exists.");

            ref ApplicationControlProperty control = ref applicationControlProperty.Value;

            if (LibHac.Common.Utilities.IsZeros(applicationControlProperty.ByteSpan))
            {
                // If the current application doesn't have a loaded control property, create a dummy one and set the savedata sizes so a user savedata will be created.
                control = ref new BlitStruct<ApplicationControlProperty>(1).Value;

                // The set sizes don't actually matter as long as they're non-zero because we use directory savedata.
                control.UserAccountSaveDataSize = 0x4000;
                control.UserAccountSaveDataJournalSize = 0x4000;
                control.SaveDataOwnerId = applicationId.Value;

                Logger.Warning?.Print(LogClass.Application, "No control file was found for this game. Using a dummy one instead. This may cause inaccuracies in some games.");
            }

            LibHac.Result resultCode = device.System.LibHacHorizonManager.RyujinxClient.Fs.EnsureApplicationCacheStorage(out _, out _, applicationId, in control);
            if (resultCode.IsFailure())
            {
                Logger.Error?.Print(LogClass.Application, $"Error calling EnsureApplicationCacheStorage. Result code {resultCode.ToStringWithName()}");

                return resultCode;
            }

            Uid userId = device.System.AccountManager.LastOpenedUser.UserId.ToLibHacUid();

            resultCode = device.System.LibHacHorizonManager.RyujinxClient.Fs.EnsureApplicationSaveData(out _, applicationId, in control, in userId);
            if (resultCode.IsFailure())
            {
                Logger.Error?.Print(LogClass.Application, $"Error calling EnsureApplicationSaveData. Result code {resultCode.ToStringWithName()}");
            }

            return resultCode;
        }

        public static bool LoadKip(KernelContext context, KipExecutable kip)
        {
            uint endOffset = kip.DataOffset + (uint)kip.Data.Length;

            if (kip.BssSize != 0)
            {
                endOffset = kip.BssOffset + kip.BssSize;
            }

            uint codeSize = BitUtils.AlignUp<uint>(kip.TextOffset + endOffset, KPageTableBase.PageSize);
            int codePagesCount = (int)(codeSize / KPageTableBase.PageSize);
            ulong codeBaseAddress = kip.Is64BitAddressSpace ? 0x8000000UL : 0x200000UL;
            ulong codeAddress = codeBaseAddress + kip.TextOffset;

            ProcessCreationFlags flags = 0;

            if (ProcessConst.AslrEnabled)
            {
                // TODO: Randomization.

                flags |= ProcessCreationFlags.EnableAslr;
            }

            if (kip.Is64BitAddressSpace)
            {
                flags |= ProcessCreationFlags.AddressSpace64Bit;
            }

            if (kip.Is64Bit)
            {
                flags |= ProcessCreationFlags.Is64Bit;
            }

            ProcessCreationInfo creationInfo = new(kip.Name, kip.Version, kip.ProgramId, codeAddress, codePagesCount, flags, 0, 0);
            MemoryRegion memoryRegion = kip.UsesSecureMemory ? MemoryRegion.Service : MemoryRegion.Application;
            KMemoryRegionManager region = context.MemoryManager.MemoryRegions[(int)memoryRegion];

            Result result = region.AllocatePages(out KPageList pageList, (ulong)codePagesCount);
            if (result != Result.Success)
            {
                Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".");

                return false;
            }

            KProcess process = new(context);

            ArmProcessContextFactory processContextFactory = new(
                context.Device.System.TickSource,
                context.Device.Gpu,
                string.Empty,
                string.Empty,
                false,
                codeAddress,
                codeSize);

            result = process.InitializeKip(creationInfo, kip.Capabilities, pageList, context.ResourceLimit, memoryRegion, context.Device.Configuration.MemoryConfiguration, processContextFactory);
            if (result != Result.Success)
            {
                Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".");

                return false;
            }

            // TODO: Support NCE of KIPs too.
            result = LoadIntoMemory(process, kip, codeBaseAddress);

            if (result != Result.Success)
            {
                Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".");

                return false;
            }

            process.DefaultCpuCore = kip.IdealCoreId;

            result = process.Start(kip.Priority, (ulong)kip.StackSize);
            if (result != Result.Success)
            {
                Logger.Error?.Print(LogClass.Loader, $"Process start returned error \"{result}\".");

                return false;
            }

            context.Processes.TryAdd(process.Pid, process);

            return true;
        }

        public static ProcessResult LoadNsos(
            Switch device,
            KernelContext context,
            MetaLoader metaLoader,
            BlitStruct<ApplicationControlProperty> applicationControlProperties,
            bool diskCacheEnabled,
            bool allowCodeMemoryForJit,
            string name,
            ulong programId,
            byte programIndex,
            byte[] arguments = null,
            params IExecutable[] executables)
        {
            context.Device.System.ServiceTable.WaitServicesReady();

            LibHac.Result resultCode = metaLoader.GetNpdm(out var npdm);

            if (resultCode.IsFailure())
            {
                Logger.Error?.Print(LogClass.Loader, $"Process initialization failed getting npdm. Result Code {resultCode.ToStringWithName()}");

                return ProcessResult.Failed;
            }

            ref readonly var meta = ref npdm.Meta;

            ulong argsStart = 0;
            uint argsSize = 0;
            ulong codeStart = ((meta.Flags & 1) != 0 ? 0x8000000UL : 0x200000UL) + CodeStartOffset;
            ulong codeSize = 0;

            var buildIds = executables.Select(e => (e switch
            {
                NsoExecutable nso => Convert.ToHexString(nso.BuildId),
                NroExecutable nro => Convert.ToHexString(nro.Header.BuildId),
                _ => string.Empty
            }).ToUpper());

            NceCpuCodePatch[] nsoPatch = new NceCpuCodePatch[executables.Length];
            ulong[] nsoBase = new ulong[executables.Length];

            // 先创建所有补丁，判断是否使用NCE模式
            bool isNceMode = false;
            for (int index = 0; index < executables.Length; index++)
            {
                IExecutable nso = executables[index];

                uint textEnd = nso.TextOffset + (uint)nso.Text.Length;
                uint roEnd = nso.RoOffset + (uint)nso.Ro.Length;
                uint dataEnd = nso.DataOffset + (uint)nso.Data.Length + nso.BssSize;

                uint nsoSize = textEnd;

                if (nsoSize < roEnd)
                {
                    nsoSize = roEnd;
                }

                if (nsoSize < dataEnd)
                {
                    nsoSize = dataEnd;
                }

                nsoSize = BitUtils.AlignUp<uint>(nsoSize, KPageTableBase.PageSize);

                bool for64Bit = ((ProcessCreationFlags)meta.Flags).HasFlag(ProcessCreationFlags.Is64Bit);

                NceCpuCodePatch codePatch = ArmProcessContextFactory.CreateCodePatchForNce(context, for64Bit, nso.Text);
                nsoPatch[index] = codePatch;

                if (codePatch != null)
                {
                    isNceMode = true;
                    codeSize += codePatch.Size;
                }

                nsoBase[index] = codeStart + codeSize;

                codeSize += nsoSize;

                if (arguments != null && argsSize == 0)
                {
                    argsStart = codeSize;

                    argsSize = (uint)BitUtils.AlignDown(arguments.Length * 2 + ProcessConst.NsoArgsTotalSize - 1, KPageTableBase.PageSize);

                    codeSize += argsSize;
                }
            }

            // 如果是NCE模式，调整地址计算
            if (isNceMode)
            {
                // NCE模式下，ReservedSize是地址空间的基地址，不是偏移量
                // 所以我们需要调整地址计算
                Logger.Info?.Print(LogClass.Loader, "NCE mode detected, adjusting address calculations");
                
                // 对于NCE模式，我们将codeStart设为0，因为ReservedSize已经是基地址
                ulong oldCodeStart = codeStart;
                codeStart = 0;
                
                // 重新计算nsoBase，使其相对于0
                codeSize = 0;
                for (int index = 0; index < executables.Length; index++)
                {
                    if (nsoPatch[index] != null)
                    {
                        codeSize += nsoPatch[index].Size;
                    }
                    
                    nsoBase[index] = codeSize;
                    
                    IExecutable nso = executables[index];
                    uint textEnd = nso.TextOffset + (uint)nso.Text.Length;
                    uint roEnd = nso.RoOffset + (uint)nso.Ro.Length;
                    uint dataEnd = nso.DataOffset + (uint)nso.Data.Length + nso.BssSize;
                    
                    uint nsoSize = textEnd;
                    if (nsoSize < roEnd) nsoSize = roEnd;
                    if (nsoSize < dataEnd) nsoSize = dataEnd;
                    nsoSize = BitUtils.AlignUp<uint>(nsoSize, KPageTableBase.PageSize);
                    
                    codeSize += nsoSize;
                }
                
                Logger.Info?.Print(LogClass.Loader, $"NCE mode: codeStart={oldCodeStart:X} -> {codeStart:X}");
            }

            int codePagesCount = (int)(codeSize / KPageTableBase.PageSize);
            int personalMmHeapPagesCount = (int)(meta.SystemResourceSize / KPageTableBase.PageSize);

            ProcessCreationInfo creationInfo = new(
                name,
                (int)meta.Version,
                programId,
                codeStart,
                codePagesCount,
                (ProcessCreationFlags)meta.Flags | ProcessCreationFlags.IsApplication,
                0,
                personalMmHeapPagesCount);

            context.Device.System.LibHacHorizonManager.InitializeApplicationClient(new ProgramId(programId), in npdm);

            Result result;

            KResourceLimit resourceLimit = new(context);

            long applicationRgSize = (long)context.MemoryManager.MemoryRegions[(int)MemoryRegion.Application].Size;

            result = resourceLimit.SetLimitValue(LimitableResource.Memory, applicationRgSize);

            if (result.IsSuccess)
            {
                result = resourceLimit.SetLimitValue(LimitableResource.Thread, 608);
            }

            if (result.IsSuccess)
            {
                result = resourceLimit.SetLimitValue(LimitableResource.Event, 700);
            }

            if (result.IsSuccess)
            {
                result = resourceLimit.SetLimitValue(LimitableResource.TransferMemory, 128);
            }

            if (result.IsSuccess)
            {
                result = resourceLimit.SetLimitValue(LimitableResource.Session, 894);
            }

            if (result != Result.Success)
            {
                Logger.Error?.Print(LogClass.Loader, "Process initialization failed setting resource limit values.");

                return ProcessResult.Failed;
            }

            KProcess process = new(context, allowCodeMemoryForJit);

            // NOTE: This field doesn't exists one firmware pre-5.0.0, a workaround have to be found.
            MemoryRegion memoryRegion = (MemoryRegion)(npdm.Acid.Flags >> 2 & 0xf);
            if (memoryRegion > MemoryRegion.NvServices)
            {
                Logger.Error?.Print(LogClass.Loader, "Process initialization failed due to invalid ACID flags.");

                return ProcessResult.Failed;
            }

            string displayVersion;

            if (metaLoader.GetProgramId() > 0x0100000000007FFF)
            {
                displayVersion = applicationControlProperties.Value.DisplayVersionString.ToString();
            }
            else
            {
                displayVersion = device.System.ContentManager.GetCurrentFirmwareVersion()?.VersionString ?? string.Empty;
            }

            var processContextFactory = new ArmProcessContextFactory(
                context.Device.System.TickSource,
                context.Device.Gpu,
                $"{programId:x16}",
                displayVersion,
                diskCacheEnabled,
                codeStart,
                codeSize);

            result = process.Initialize(
                creationInfo,
                MemoryMarshal.Cast<byte, uint>(npdm.KernelCapabilityData),
                resourceLimit,
                memoryRegion,
                context.Device.Configuration.MemoryConfiguration,
                processContextFactory,
                entrypointOffset: nsoPatch[0]?.Size ?? 0UL);

            if (result != Result.Success)
            {
                Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".");

                return ProcessResult.Failed;
            }

            // 检测是否是NCE内存管理器
            bool usingNceMemoryManager = false;
            using (process.Context as IArmProcessContext)
            {
                var armProcessContext = process.Context as IArmProcessContext;
                if (armProcessContext != null && armProcessContext.AddressSpace is Ryujinx.Cpu.Nce.MemoryManagerNative)
                {
                    usingNceMemoryManager = true;
                    Logger.Info?.Print(LogClass.Loader, 
                        $"NCE MemoryManager detected: ReservedSize=0x{process.Context.ReservedSize:X}, " +
                        $"AddressSpaceSize=0x{process.Context.AddressSpaceSize:X}");
                }
            }

            for (int index = 0; index < executables.Length; index++)
            {
                // 关键修复：根据内存管理器类型计算地址
                ulong nsoBaseAddress;
                
                if (usingNceMemoryManager)
                {
                    // NCE模式：ReservedSize是基地址，直接加上相对地址
                    nsoBaseAddress = process.Context.ReservedSize + nsoBase[index];
                    
                    // 验证地址是否有效
                    if (nsoBaseAddress < process.Context.ReservedSize)
                    {
                        Logger.Error?.Print(LogClass.Loader, 
                            $"NCE address calculation overflow: 0x{process.Context.ReservedSize:X} + 0x{nsoBase[index]:X}");
                        return ProcessResult.Failed;
                    }
                }
                else
                {
                    // 非NCE模式：使用原来的计算方式
                    nsoBaseAddress = process.Context.ReservedSize + nsoBase[index];
                }

                Logger.Info?.Print(LogClass.Loader, 
                    $"Loading image {index} at 0x{nsoBaseAddress:x16} " +
                    $"(NCE={usingNceMemoryManager}, base=0x{nsoBase[index]:x16})...");

                result = LoadIntoMemory(process, executables[index], nsoBaseAddress, nsoPatch[index]);

                if (result != Result.Success)
                {
                    Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".");

                    return ProcessResult.Failed;
                }
            }

            process.DefaultCpuCore = meta.DefaultCpuId;

            context.Processes.TryAdd(process.Pid, process);

            // Keep the build ids because the tamper machine uses them to know which process to associate a
            // tamper to and also keep the starting address of each executable inside a process because some
            // memory modifications are relative to this address.
            ProcessTamperInfo tamperInfo = new(
                process,
                buildIds,
                nsoBase,
                process.MemoryManager.HeapRegionStart,
                process.MemoryManager.AliasRegionStart,
                process.MemoryManager.CodeRegionStart);

            // Once everything is loaded, we can load cheats.
            device.Configuration.VirtualFileSystem.ModLoader.LoadCheats(programId, tamperInfo, device.TamperMachine);

            ProcessResult processResult = new(
                metaLoader,
                applicationControlProperties,
                diskCacheEnabled,
                allowCodeMemoryForJit,
                processContextFactory.DiskCacheLoadState,
                process.Pid,
                meta.MainThreadPriority,
                meta.MainThreadStackSize,
                device.System.State.DesiredTitleLanguage);

            // Register everything in arp service.
            device.System.ServiceTable.ArpWriter.AcquireRegistrar(out IRegistrar registrar);
            registrar.SetApplicationControlProperty(MemoryMarshal.Cast<byte, Horizon.Sdk.Ns.ApplicationControlProperty>(applicationControlProperties.ByteSpan)[0]);
            // TODO: Handle Version and StorageId when it will be needed.
            registrar.SetApplicationLaunchProperty(new ApplicationLaunchProperty()
            {
                ApplicationId = new Horizon.Sdk.Ncm.ApplicationId(programId),
                Version = 0x00,
                Storage = Horizon.Sdk.Ncm.StorageId.BuiltInUser,
                PatchStorage = Horizon.Sdk.Ncm.StorageId.None,
                ApplicationKind = ApplicationKind.Application,
            });

            device.System.ServiceTable.ArpReader.GetApplicationInstanceId(out ulong applicationInstanceId, process.Pid);
            device.System.ServiceTable.ArpWriter.AcquireApplicationProcessPropertyUpdater(out IUpdater updater, applicationInstanceId);
            updater.SetApplicationProcessProperty(process.Pid, new ApplicationProcessProperty() { ProgramIndex = programIndex });

            return processResult;
        }

        private static Result LoadIntoMemory(KProcess process, IExecutable image, ulong baseAddress, NceCpuCodePatch codePatch = null)
        {
            // 检测是否是NCE内存管理器
            bool isNceMode = false;
            ulong reservedSize = process.Context.ReservedSize;
            
            using (process.Context as IArmProcessContext)
            {
                var armProcessContext = process.Context as IArmProcessContext;
                if (armProcessContext != null && armProcessContext.AddressSpace is Ryujinx.Cpu.Nce.MemoryManagerNative)
                {
                    isNceMode = true;
                }
            }

            Logger.Info?.Print(LogClass.Loader, 
                $"LoadIntoMemory: base=0x{baseAddress:X}, isNce={isNceMode}, reserved=0x{reservedSize:X}");

            // NCE模式下验证地址
            if (isNceMode && baseAddress < reservedSize)
            {
                Logger.Error?.Print(LogClass.Loader, 
                    $"NCE base address 0x{baseAddress:X} below reserved start 0x{reservedSize:X}");
                return KernelResult.InvalidAddress;
            }

            ulong patchAddress = 0;
            if (codePatch != null)
            {
                // 计算补丁地址
                patchAddress = baseAddress - codePatch.Size;
                
                // NCE模式下验证补丁地址
                if (isNceMode && patchAddress < reservedSize)
                {
                    Logger.Error?.Print(LogClass.Loader, 
                        $"NCE patch address 0x{patchAddress:X} below reserved start 0x{reservedSize:X}");
                    
                    // 调整：将补丁放在reservedSize处，NSO放在补丁之后
                    patchAddress = reservedSize;
                    baseAddress = patchAddress + codePatch.Size;
                    
                    Logger.Info?.Print(LogClass.Loader, 
                        $"Adjusted: patch at 0x{patchAddress:X}, NSO at 0x{baseAddress:X}");
                }

                Logger.Info?.Print(LogClass.Loader, 
                    $"Writing NCE patch at 0x{patchAddress:X}, size: {codePatch.Size}");

                codePatch.Write(process.CpuMemory, patchAddress, baseAddress + image.TextOffset);
            }

            ulong textStart = baseAddress + image.TextOffset;
            ulong roStart = baseAddress + image.RoOffset;
            ulong dataStart = baseAddress + image.DataOffset;
            ulong bssStart = baseAddress + image.BssOffset;

            ulong end = dataStart + (ulong)image.Data.Length;

            if (image.BssSize != 0)
            {
                end = bssStart + image.BssSize;
            }

            process.CpuMemory.Write(textStart, image.Text);
            process.CpuMemory.Write(roStart, image.Ro);
            process.CpuMemory.Write(dataStart, image.Data);

            process.CpuMemory.Fill(bssStart, image.BssSize, 0);

            // 定义设置内存权限的辅助函数
            Result SetProcessMemoryPermissionLocal(ulong address, ulong size, KMemoryPermission permission)
            {
                if (size == 0)
                {
                    return Result.Success;
                }

                size = BitUtils.AlignUp<ulong>(size, KPageTableBase.PageSize);
                
                // NCE模式下验证地址
                if (isNceMode && address < reservedSize)
                {
                    Logger.Error?.Print(LogClass.Loader, 
                        $"Setting permission for address 0x{address:X} below reserved start 0x{reservedSize:X}");
                    return KernelResult.InvalidAddress;
                }

                return process.MemoryManager.SetProcessMemoryPermission(address, size, permission);
            }

            // 如果是NCE补丁，需要设置补丁区域的内存权限
            if (codePatch != null)
            {
                ulong patchSize = BitUtils.AlignUp<ulong>((ulong)codePatch.Size, KPageTableBase.PageSize);
                Result patchResult = SetProcessMemoryPermissionLocal(patchAddress, patchSize, KMemoryPermission.ReadAndExecute);
                if (patchResult != Result.Success)
                {
                    Logger.Error?.Print(LogClass.Loader, 
                        $"Failed to set NCE patch memory permission: {patchResult}");
                    return patchResult;
                }
            }

            Result textResult = SetProcessMemoryPermissionLocal(textStart, (ulong)image.Text.Length, KMemoryPermission.ReadAndExecute);
            if (textResult != Result.Success)
            {
                return textResult;
            }

            Result roResult = SetProcessMemoryPermissionLocal(roStart, (ulong)image.Ro.Length, KMemoryPermission.Read);
            if (roResult != Result.Success)
            {
                return roResult;
            }

            return SetProcessMemoryPermissionLocal(dataStart, end - dataStart, KMemoryPermission.ReadAndWrite);
        }
    }
}
