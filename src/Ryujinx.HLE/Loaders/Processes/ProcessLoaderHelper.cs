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

            return device.System.LibHacHorizonManager.NsClient.Fs.RegisterProgramMapInfo(mapInfo[..programCount]);
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

                // ===== 关键修正：NSO基址计算 =====
                if (index == 0)
                {
                    // 第一个NSO：根据模式使用不同的偏移
                    if (codePatch != null)
                    {
                        // NCE模式：codeStart + 补丁大小
                        nsoBase[index] = codeStart + (codePatch.Size > 0 ? codePatch.Size : 0x1000UL);
                        Logger.Info?.Print(LogClass.Loader, 
                            $"NSO[0] NCE模式: 补丁大小=0x{codePatch.Size:X}, nsoBase=0x{nsoBase[index]:X}");
                    }
                    else
                    {
                        // JIT模式：codeStart + 0x4000
                        nsoBase[index] = codeStart + 0x4000UL;
                        Logger.Info?.Print(LogClass.Loader, 
                            $"NSO[0] JIT模式: nsoBase=0x{nsoBase[index]:X}");
                    }
                    
                    // 为第一个NSO添加补丁大小到codeSize
                    if (codePatch != null && codePatch.Size > 0)
                    {
                        codeSize += codePatch.Size;
                    }
                }
                else
                {
                    // 后续NSO：基于前一个NSO的结束地址
                    IExecutable prevExe = executables[index - 1];
                    nsoBase[index] = nsoBase[index - 1] + GetNsoAlignedSize(prevExe);
                }

                codeSize += nsoSize;

                if (arguments != null && argsSize == 0)
                {
                    argsStart = codeSize;

                    argsSize = (uint)BitUtils.AlignDown(arguments.Length * 2 + ProcessConst.NsoArgsTotalSize - 1, KPageTableBase.PageSize);

                    codeSize += argsSize;
                }
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

            // ===== 获取实际内存区域信息 =====
            ulong actualAslrBase = process.MemoryManager.CodeRegionStart;
            ulong actualHeapBase = process.MemoryManager.HeapRegionStart;
            ulong actualAliasBase = process.MemoryManager.AliasRegionStart;

            // 记录调试信息
            Logger.Info?.Print(LogClass.Loader, $"实际内存地址 - ASLR: 0x{actualAslrBase:X}, 堆: 0x{actualHeapBase:X}, 别名: 0x{actualAliasBase:X}");
            Logger.Info?.Print(LogClass.Loader, $"原始codeStart: 0x{codeStart:X}");
            Logger.Info?.Print(LogClass.Loader, $"ReservedSize: 0x{process.Context.ReservedSize:X}");

            // 检查是否为NCE模式
            bool isNceMode = nsoPatch[0] != null;
            ulong[] loadedNsoBase = new ulong[executables.Length];

            // 计算实际加载地址
            for (int index = 0; index < executables.Length; index++)
            {
                loadedNsoBase[index] = nsoBase[index] + process.Context.ReservedSize;
                
                Logger.Info?.Print(LogClass.Loader, 
                    $"NSO[{index}] 加载: " +
                    $"nsoBase=0x{nsoBase[index]:X}, " +
                    $"ReservedSize=0x{process.Context.ReservedSize:X}, " +
                    $"最终地址=0x{loadedNsoBase[index]:X}, " +
                    $"模式={(isNceMode ? "NCE" : "JIT")}");
            }
            
            if (isNceMode)
            {
                Logger.Info?.Print(LogClass.Loader, 
                    $"NCE模式检测: ASLR基址=0x{actualAslrBase:X}, " +
                    $"补丁大小={nsoPatch[0]?.Size ?? 0:X}, " +
                    $"主NSO地址=0x{loadedNsoBase[0]:X}");
            }

            // 加载NSO到内存
            for (int index = 0; index < executables.Length; index++)
            {
                Logger.Info?.Print(LogClass.Loader, $"加载镜像 {index} 到地址 0x{loadedNsoBase[index]:x16}...");

                result = LoadIntoMemory(process, executables[index], loadedNsoBase[index], nsoPatch[index]);

                if (result != Result.Success)
                {
                    Logger.Error?.Print(LogClass.Loader, $"进程初始化返回错误 \"{result}\".");

                    return ProcessResult.Failed;
                }
            }

            process.DefaultCpuCore = meta.DefaultCpuId;
            context.Processes.TryAdd(process.Pid, process);

            // 主NSO基址（第一个NSO的加载地址）
            ulong mainNsoBase = loadedNsoBase[0];
            
            // 计算偏移用于调试
            ulong offsetFromAslr = mainNsoBase - actualAslrBase;
            
            // 记录详细的地址信息
            Logger.Info?.Print(LogClass.Loader, 
                $"进程加载完成: " +
                $"PID={process.Pid}, " +
                $"主NSO基址=0x{mainNsoBase:X}, " +
                $"ASLR基址=0x{actualAslrBase:X}, " +
                $"偏移ASLR=0x{offsetFromAslr:X}, " +
                $"原始codeStart=0x{codeStart:X}, " +
                $"ReservedSize=0x{process.Context.ReservedSize:X}, " +
                $"NCE模式={isNceMode}");
            
            // 创建ProcessTamperInfo
            ProcessTamperInfo tamperInfo = new(
                process,
                buildIds,
                loadedNsoBase,  // 使用加载地址
                actualHeapBase,
                actualAliasBase,
                actualAslrBase,
                mainNsoBase,  // 传递主NSO基址
                codeStart);   // 传递原始codeStart用于偏移计算

            // 加载金手指
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

        private static uint GetNsoAlignedSize(IExecutable nso)
        {
            uint textEnd = nso.TextOffset + (uint)nso.Text.Length;
            uint roEnd = nso.RoOffset + (uint)nso.Ro.Length;
            uint dataEnd = nso.DataOffset + (uint)nso.Data.Length + nso.BssSize;
            
            uint nsoSize = Math.Max(Math.Max(textEnd, roEnd), dataEnd);
            return BitUtils.AlignUp<uint>(nsoSize, KPageTableBase.PageSize);
        }

        private static Result LoadIntoMemory(KProcess process, IExecutable image, ulong baseAddress, NceCpuCodePatch codePatch = null)
        {
            ulong textStart = baseAddress + image.TextOffset;
            ulong roStart = baseAddress + image.RoOffset;
            ulong dataStart = baseAddress + image.DataOffset;
            ulong bssStart = baseAddress + image.BssOffset;

            ulong end = dataStart + (ulong)image.Data.Length;

            if (image.BssSize != 0)
            {
                end = bssStart + image.BssSize;
            }

            try
            {
                // ===== 关键修正：在NCE模式下，需要先设置补丁区域权限 =====
                if (codePatch != null && codePatch.Size > 0)
                {
                    // 设置补丁区域为可写
                    ulong patchAddress = baseAddress - codePatch.Size;
                    Logger.Info?.Print(LogClass.Loader, 
                        $"NCE补丁: 地址=0x{patchAddress:X}, 大小=0x{codePatch.Size:X}, " +
                        $"镜像基址=0x{baseAddress:X}");
                    
                    Result patchResult = SetProcessMemoryPermission(process, patchAddress, codePatch.Size, KMemoryPermission.ReadAndWrite);
                    if (patchResult != Result.Success)
                    {
                        Logger.Error?.Print(LogClass.Loader, 
                            $"设置补丁区域权限失败: 地址=0x{patchAddress:X}, 大小=0x{codePatch.Size:X}");
                        return patchResult;
                    }
                    
                    // 写入补丁
                    codePatch.Write(process.CpuMemory, patchAddress, textStart);
                }

                // 写入NSO内容
                process.CpuMemory.Write(textStart, image.Text);
                process.CpuMemory.Write(roStart, image.Ro);
                process.CpuMemory.Write(dataStart, image.Data);

                process.CpuMemory.Fill(bssStart, image.BssSize, 0);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Loader, 
                    $"写入内存失败: 地址=0x{baseAddress:X}, 错误={ex.Message}");
                return KernelResult.InvalidAddress;
            }

            // 设置内存权限
            Result result = SetProcessMemoryPermission(process, textStart, (ulong)image.Text.Length, KMemoryPermission.ReadAndExecute);
            if (result != Result.Success)
            {
                return result;
            }

            result = SetProcessMemoryPermission(process, roStart, (ulong)image.Ro.Length, KMemoryPermission.Read);
            if (result != Result.Success)
            {
                return result;
            }

            return SetProcessMemoryPermission(process, dataStart, end - dataStart, KMemoryPermission.ReadAndWrite);
        }

        private static Result SetProcessMemoryPermission(KProcess process, ulong address, ulong size, KMemoryPermission permission)
        {
            if (size == 0)
            {
                return Result.Success;
            }

            size = BitUtils.AlignUp<ulong>(size, KPageTableBase.PageSize);

            return process.MemoryManager.SetProcessMemoryPermission(address, size, permission);
        }
    }
}
