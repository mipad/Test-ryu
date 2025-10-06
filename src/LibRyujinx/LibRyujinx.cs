using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS;
using Ryujinx.Input.HLE;
using Ryujinx.HLE;
using System.Linq;
using System;
using System.Runtime.InteropServices;
using Ryujinx.Common.Configuration;
using LibHac.Tools.FsSystem;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Audio.Backends.Dummy;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.UI.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Audio.Integration;
using Ryujinx.Audio.Backends.SDL2;
using System.IO;
using LibHac.Common.Keys;
using LibHac.Common;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Fs;
using Path = System.IO.Path;
using LibHac;
using OpenTK.Audio.OpenAL;
using Ryujinx.HLE.Loaders.Npdm;
using System.Globalization;
using Ryujinx.UI.Common.Configuration.System;
using Ryujinx.Common.Logging.Targets;
using System.Collections.Generic;
using System.Text;
using Ryujinx.HLE.UI;
using LibRyujinx.Android;
using System.IO.Compression; // 添加ZIP压缩支持

namespace LibRyujinx
{
    public static partial class LibRyujinx
    {
        internal static IHardwareDeviceDriver AudioDriver { get; set; } = new DummyHardwareDeviceDriver();

        private static readonly TitleUpdateMetadataJsonSerializerContext _titleSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());
        public static SwitchDevice? SwitchDevice { get; set; }

        // 添加静态字段来存储画面比例
        private static AspectRatio _currentAspectRatio = AspectRatio.Stretched;

        // 添加静态字段来存储内存配置
        private static MemoryConfiguration _currentMemoryConfiguration = MemoryConfiguration.MemoryConfiguration8GiB;

        // 添加静态字段来存储系统时间偏移
        private static long _systemTimeOffset = 0;

        public static bool Initialize(string? basePath)
        {
            if (SwitchDevice != null)
            {
                return false;
            }

            try
            {
                AppDataManager.Initialize(basePath);

                ConfigurationState.Initialize();
                LoggerModule.Initialize();

                string logDir = Path.Combine(AppDataManager.BaseDirPath, "Logs");
                FileStream logFile = FileLogTarget.PrepareLogFile(logDir);
                Logger.AddTarget(new AsyncLogTargetWrapper(
                    new FileLogTarget("file", logFile),
                    1000,
                    AsyncLogTargetOverflowAction.Block
                ));

                Logger.Notice.Print(LogClass.Application, "Initializing...");
                Logger.Notice.Print(LogClass.Application, $"Using base path: {AppDataManager.BaseDirPath}");

                SwitchDevice = new SwitchDevice();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
            
            OpenALLibraryNameContainer.OverridePath = "libopenal.so";

            return true;
        }

        // 添加设置画面比例的方法
        public static void SetAspectRatio(AspectRatio aspectRatio)
        {
            _currentAspectRatio = aspectRatio;
            Logger.Info?.Print(LogClass.Emulation, $"Aspect ratio set to: {_currentAspectRatio}");
            
            // 如果设备已初始化，立即应用新的画面比例
            if (SwitchDevice?.EmulationContext != null)
            {
                Logger.Info?.Print(LogClass.Emulation, $"Applying aspect ratio change to running emulation: {_currentAspectRatio}");
                // 这里需要重新配置模拟器上下文以应用新的画面比例
                // 可能需要重启模拟器才能完全生效
            }
        }

        // 添加获取画面比例的方法
        public static AspectRatio GetAspectRatio()
        {
            return _currentAspectRatio;
        }

        // 添加设置内存配置的方法
        public static void SetMemoryConfiguration(MemoryConfiguration memoryConfig)
        {
            _currentMemoryConfiguration = memoryConfig;
            Logger.Info?.Print(LogClass.Emulation, $"Memory configuration set to: {_currentMemoryConfiguration}");
            
            // 如果设备已初始化，记录需要重启才能生效
            if (SwitchDevice?.EmulationContext != null)
            {
                Logger.Info?.Print(LogClass.Emulation, "Memory configuration change requires emulation restart to take effect");
            }
        }

        // 添加获取内存配置的方法
        public static MemoryConfiguration GetMemoryConfiguration()
        {
            return _currentMemoryConfiguration;
        }

        // 添加设置系统时间偏移的方法
        public static void SetSystemTimeOffset(long offset)
        {
            _systemTimeOffset = offset;
            Logger.Info?.Print(LogClass.Emulation, $"System time offset set to: {offset} seconds");
            
            // 如果设备已初始化，记录需要重启才能生效
            if (SwitchDevice?.EmulationContext != null)
            {
                Logger.Info?.Print(LogClass.Emulation, "System time offset change requires emulation restart to take effect");
            }
        }

        // 添加获取系统时间偏移的方法
        public static long GetSystemTimeOffset()
        {
            return _systemTimeOffset;
        }

        public static void InitializeAudio()
        {
            AudioDriver = new SDL2HardwareDeviceDriver();
        }

        public static GameStats GetGameStats()
        {
            if (SwitchDevice?.EmulationContext == null)
                return new GameStats();

            var context = SwitchDevice.EmulationContext;

            return new GameStats
            {
                Fifo = context.Statistics.GetFifoPercent(),
                GameFps = context.Statistics.GetGameFrameRate(),
                GameTime = context.Statistics.GetGameFrameTime()
            };
        }


        public static GameInfo? GetGameInfo(string? file)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return new GameInfo();
            }

            Logger.Info?.Print(LogClass.Application, $"Getting game info for file: {file}");

            using var stream = File.Open(file, FileMode.Open);

            return GetGameInfo(stream, new FileInfo(file).Extension.Remove('.'));
        }

        public static GameInfo? GetGameInfo(Stream gameStream, string extension)
        {
            if (SwitchDevice == null)
            {
                Logger.Error?.Print(LogClass.Application, "SwitchDevice is not initialized.");
                return null;
            }
            GameInfo gameInfo = GetDefaultInfo(gameStream);

            const Language TitleLanguage = Language.SimplifiedChinese;

            BlitStruct<ApplicationControlProperty> controlHolder = new(1);

            try
            {
                try
                {
                    if (extension == "nsp" || extension == "pfs0" || extension == "xci")
                    {
                        IFileSystem pfs;

                        bool isExeFs = false;

                        if (extension == "xci")
                        {
                            Xci xci = new(SwitchDevice.VirtualFileSystem.KeySet, gameStream.AsStorage());

                            pfs = xci.OpenPartition(XciPartitionType.Secure);
                        }
                        else
                        {
                            var pfsTemp = new PartitionFileSystem();
                            pfsTemp.Initialize(gameStream.AsStorage()).ThrowIfFailure();
                            pfs = pfsTemp;

                            // If the NSP doesn't have a main NCA, decrement the number of applications found and then continue to the next application.
                            bool hasMainNca = false;

                            foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*"))
                            {
                                if (Path.GetExtension(fileEntry.FullPath).ToLower() == ".nca")
                                {
                                    using UniqueRef<IFile> ncaFile = new();

                                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                    Nca nca = new(SwitchDevice.VirtualFileSystem.KeySet, ncaFile.Get.AsStorage());
                                    int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                                    // Some main NCAs don't have a data partition, so check if the partition exists before opening it
                                    if (nca.Header.ContentType == NcaContentType.Program && !(nca.SectionExists(NcaSectionType.Data) && nca.Header.GetFsHeader(dataIndex).IsPatchSection()))
                                    {
                                        hasMainNca = true;

                                        break;
                                    }
                                }
                                else if (Path.GetFileNameWithoutExtension(fileEntry.FullPath) == "main")
                                {
                                    isExeFs = true;
                                }
                            }

                            if (!hasMainNca && !isExeFs)
                            {
                                return null;
                            }
                        }

                        if (isExeFs)
                        {
                            using UniqueRef<IFile> npdmFile = new();

                            Result result = pfs.OpenFile(ref npdmFile.Ref, "/main.npdm".ToU8Span(), OpenMode.Read);

                            if (ResultFs.PathNotFound.Includes(result))
                            {
                                Npdm npdm = new(npdmFile.Get.AsStream());

                                gameInfo.TitleName = npdm.TitleName;
                                gameInfo.TitleId = npdm.Aci0.TitleId.ToString("x16");
                            }
                        }
                        else
                        {
                            GetControlFsAndTitleId(pfs, out IFileSystem? controlFs, out string? id);

                            gameInfo.TitleId = id;

                            if (controlFs == null)
                            {
                                Logger.Error?.Print(LogClass.Application, $"No control FS was returned. Unable to process game any further: {gameInfo.TitleName}");
                                return null;
                            }

                            // Check if there is an update available.
                            if (IsUpdateApplied(gameInfo.TitleId, out IFileSystem? updatedControlFs))
                            {
                                // Replace the original ControlFs by the updated one.
                                controlFs = updatedControlFs;
                            }

                            ReadControlData(controlFs, controlHolder.ByteSpan);

                            GetGameInformation(ref controlHolder.Value, out gameInfo.TitleName, out _, out gameInfo.Developer, out gameInfo.Version);

                            // Read the icon from the ControlFS and store it as a byte array
                            try
                            {
                                using UniqueRef<IFile> icon = new();

                                controlFs?.OpenFile(ref icon.Ref, $"/icon_{TitleLanguage}.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                using MemoryStream stream = new();

                                icon.Get.AsStream().CopyTo(stream);
                                gameInfo.Icon = stream.ToArray();
                            }
                            catch (HorizonResultException)
                            {
                                foreach (DirectoryEntryEx entry in controlFs.EnumerateEntries("/", "*"))
                                {
                                    if (entry.Name == "control.nacp")
                                    {
                                        continue;
                                    }

                                    using var icon = new UniqueRef<IFile>();

                                    controlFs?.OpenFile(ref icon.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                    using MemoryStream stream = new();

                                    icon.Get.AsStream().CopyTo(stream);
                                    gameInfo.Icon = stream.ToArray();

                                    if (gameInfo.Icon != null)
                                    {
                                        break;
                                    }
                                }

                            }
                        }
                    }
                    else if (extension == "nro")
                    {
                        BinaryReader reader = new(gameStream);

                        byte[] Read(long position, int size)
                        {
                            gameStream.Seek(position, SeekOrigin.Begin);

                            return reader.ReadBytes(size);
                        }

                        gameStream.Seek(24, SeekOrigin.Begin);

                        int assetOffset = reader.ReadInt32();

                        if (Encoding.ASCII.GetString(Read(assetOffset, 4)) == "ASET")
                        {
                            byte[] iconSectionInfo = Read(assetOffset + 8, 0x10);

                            long iconOffset = BitConverter.ToInt64(iconSectionInfo, 0);
                            long iconSize = BitConverter.ToInt64(iconSectionInfo, 8);

                            ulong nacpOffset = reader.ReadUInt64();
                            ulong nacpSize = reader.ReadUInt64();

                            // Reads and stores game icon as byte array
                            if (iconSize > 0)
                            {
                                gameInfo.Icon = Read(assetOffset + iconOffset, (int)iconSize);
                            }

                            // Read the NACP data
                            Read(assetOffset + (int)nacpOffset, (int)nacpSize).AsSpan().CopyTo(controlHolder.ByteSpan);

                            GetGameInformation(ref controlHolder.Value, out gameInfo.TitleName, out _, out gameInfo.Developer, out gameInfo.Version);
                        }
                    }
                }
                catch (MissingKeyException exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}");
                }
                catch (InvalidDataException exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. {exception}");
                }
                catch (Exception exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"The gameStream encountered was not of a valid type. Error: {exception}");

                    return null;
                }
            }
            catch (IOException exception)
            {
                Logger.Warning?.Print(LogClass.Application, exception.Message);
            }

            void ReadControlData(IFileSystem? controlFs, Span<byte> outProperty)
            {
                using UniqueRef<IFile> controlFile = new();

                controlFs?.OpenFile(ref controlFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                controlFile.Get.Read(out _, 0, outProperty, ReadOption.None).ThrowIfFailure();
            }

            void GetGameInformation(ref ApplicationControlProperty controlData, out string? titleName, out string titleId, out string? publisher, out string? version)
            {
                _ = Enum.TryParse(TitleLanguage.ToString(), out TitleLanguage desiredTitleLanguage);

                if (controlData.Title.Length > (int)desiredTitleLanguage)
                {
                    titleName = controlData.Title[(int)desiredTitleLanguage].NameString.ToString();
                    publisher = controlData.Title[(int)desiredTitleLanguage].PublisherString.ToString();
                }
                else
                {   
                    titleName = null;
                    publisher = null;
                }

                if (string.IsNullOrWhiteSpace(titleName))
                {
                    foreach (ref readonly var controlTitle in controlData.Title)
                    {
                        if (!controlTitle.NameString.IsEmpty())
                        {
                            titleName = controlTitle.NameString.ToString();

                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(publisher))
                {
                    foreach (ref readonly var controlTitle in controlData.Title)
                    {
                        if (!controlTitle.PublisherString.IsEmpty())
                        {
                            publisher = controlTitle.PublisherString.ToString();

                            break;
                        }
                    }
                }

                if (controlData.PresenceGroupId != 0)
                {
                    titleId = controlData.PresenceGroupId.ToString("x16");
                }
                else if (controlData.SaveDataOwnerId != 0)
                {
                    titleId = controlData.SaveDataOwnerId.ToString();
                }
                else if (controlData.AddOnContentBaseId != 0)
                {
                    titleId = (controlData.AddOnContentBaseId - 0x1000).ToString("x16");
                }
                else
                {
                    titleId = "0000000000000000";
                }

                version = controlData.DisplayVersionString.ToString();
            }

            void GetControlFsAndTitleId(IFileSystem pfs, out IFileSystem? controlFs, out string? titleId)
            {
                if (SwitchDevice == null)
                {
                    Logger.Error?.Print(LogClass.Application, "SwitchDevice is not initialized.");

                    controlFs = null;
                    titleId = null;
                    return;
                }
                (_, _, Nca? controlNca) = GetGameData(SwitchDevice.VirtualFileSystem, pfs, 0);

                if (controlNca == null)
                {
                    Logger.Warning?.Print(LogClass.Application, "Control NCA is null. Unable to load control FS.");
                }

                // Return the ControlFS
                controlFs = controlNca?.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                titleId = controlNca?.Header.TitleId.ToString("x16");
            }

            (Nca? mainNca, Nca? patchNca, Nca? controlNca) GetGameData(VirtualFileSystem fileSystem, IFileSystem pfs, int programIndex)
            {
                Nca? mainNca = null;
                Nca? patchNca = null;
                Nca? controlNca = null;

                fileSystem.ImportTickets(pfs);

                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                {
                    using var ncaFile = new UniqueRef<IFile>();

                    Logger.Info?.Print(LogClass.Application, $"Loading file from PFS: {fileEntry.FullPath}");

                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    Nca nca = new(fileSystem.KeySet, ncaFile.Release().AsStorage());

                    int ncaProgramIndex = (int)(nca.Header.TitleId & 0xF);

                    if (ncaProgramIndex != programIndex)
                    {
                        continue;
                    }

                    if (nca.Header.ContentType == NcaContentType.Program)
                    {
                        int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                        if (nca.SectionExists(NcaSectionType.Data) && nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                        {
                            patchNca = nca;
                        }
                        else
                        {
                            mainNca = nca;
                        }
                    }
                    else if (nca.Header.ContentType == NcaContentType.Control)
                    {
                        controlNca = nca;
                    }
                }

                return (mainNca, patchNca, controlNca);
            }

            bool IsUpdateApplied(string? titleId, out IFileSystem? updatedControlFs)
            {
                updatedControlFs = null;

                string? updatePath = "(unknown)";

                if (SwitchDevice?.VirtualFileSystem == null)
                {
                    Logger.Error?.Print(LogClass.Application, "SwitchDevice was not initialized.");
                    return false;
                }

                try
                {
                    (Nca? patchNca, Nca? controlNca) = GetGameUpdateData(SwitchDevice.VirtualFileSystem, titleId, 0, out updatePath);

                    if (patchNca != null && controlNca != null)
                    {
                        updatedControlFs = controlNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);

                        return true;
                    }
                }
                catch (InvalidDataException)
                {
                    Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. Errored File: {updatePath}");
                }
                catch (MissingKeyException exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}. Errored File: {updatePath}");
                }

                return false;
            }

            (Nca? patch, Nca? control) GetGameUpdateData(VirtualFileSystem fileSystem, string? titleId, int programIndex, out string? updatePath)
            {
                updatePath = null;

                if (ulong.TryParse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdBase))
                {
                    // Clear the program index part.
                    titleIdBase &= ~0xFUL;

                    // Load update information if exists.
                    string titleUpdateMetadataPath = Path.Combine(AppDataManager.GamesDirPath, titleIdBase.ToString("x16"), "updates.json");

                    if (File.Exists(titleUpdateMetadataPath))
                    {
                        updatePath = JsonHelper.DeserializeFromFile(titleUpdateMetadataPath, _titleSerializerContext.TitleUpdateMetadata).Selected;

                        if (File.Exists(updatePath))
                        {
                            FileStream file = new(updatePath, FileMode.Open, FileAccess.Read);
                            PartitionFileSystem nsp = new();
                            nsp.Initialize(file.AsStorage()).ThrowIfFailure();

                            return GetGameUpdateDataFromPartition(fileSystem, nsp, titleIdBase.ToString("x16"), programIndex);
                        }
                    }
                }

                return (null, null);
            }

            (Nca? patchNca, Nca? controlNca) GetGameUpdateDataFromPartition(VirtualFileSystem fileSystem, PartitionFileSystem pfs, string titleId, int programIndex)
            {
                Nca? patchNca = null;
                Nca? controlNca = null;

                fileSystem.ImportTickets(pfs);

                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                {
                    using var ncaFile = new UniqueRef<IFile>();

                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    Nca nca = new(fileSystem.KeySet, ncaFile.Release().AsStorage());

                    int ncaProgramIndex = (int)(nca.Header.TitleId & 0xF);

                    if (ncaProgramIndex != programIndex)
                    {
                        continue;
                    }

                    if ($"{nca.Header.TitleId.ToString("x16")[..^3]}000" != titleId)
                    {
                        break;
                    }

                    if (nca.Header.ContentType == NcaContentType.Program)
                    {
                        patchNca = nca;
                    }
                    else if (nca.Header.ContentType == NcaContentType.Control)
                    {
                        controlNca = nca;
                    }
                }

                return (patchNca, controlNca);
            }

            return gameInfo;
        }

        private static GameInfo GetDefaultInfo(Stream gameStream)
        {
            return new GameInfo
            {
                FileSize = gameStream.Length * 0.000000000931,
                TitleName = "Unknown",
                TitleId = "0000000000000000",
                Developer = "Unknown",
                Version = "0",
                Icon = null
            };
        }

        public static string GetDlcTitleId(string path, string ncaPath)
        {
            if (File.Exists(path))
            {
                using FileStream containerFile = File.OpenRead(path);

                PartitionFileSystem partitionFileSystem = new();
                partitionFileSystem.Initialize(containerFile.AsStorage()).ThrowIfFailure();

                SwitchDevice.VirtualFileSystem.ImportTickets(partitionFileSystem);

                using UniqueRef<IFile> ncaFile = new();

                partitionFileSystem.OpenFile(ref ncaFile.Ref, ncaPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                // 修改：使用条件性完整性检查
                IntegrityCheckLevel checkLevel = SwitchDevice.EnableFsIntegrityChecks ? 
                    IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None;
                
                Nca nca = TryOpenNca(ncaFile.Get.AsStorage(), ncaPath, checkLevel);
                if (nca != null)
                {
                    return nca.Header.TitleId.ToString("X16");
                }
            }
            return string.Empty;
        }

        // 修改 TryOpenNca 方法以接受完整性检查参数
        private static Nca TryOpenNca(IStorage ncaStorage, string containerPath, IntegrityCheckLevel checkLevel = IntegrityCheckLevel.None)
        {
            try
            {
                var nca = new Nca(SwitchDevice.VirtualFileSystem.KeySet, ncaStorage);
                // 如果需要，可以在这里打开文件系统进行验证
                // nca.OpenFileSystem(NcaSectionType.Data, checkLevel);
                return nca;
            }
            catch (Exception ex)
            {
                // 添加详细的错误日志
                Logger.Error?.Print(LogClass.Application, $"Failed to open NCA: {ex.Message}");
            }
            return null;
        }

        public static List<string> GetDlcContentList(string path, ulong titleId)
        {
            if (!File.Exists(path))
                return new List<string>();

            using FileStream containerFile = File.OpenRead(path);

            PartitionFileSystem partitionFileSystem = new();
            partitionFileSystem.Initialize(containerFile.AsStorage()).ThrowIfFailure();

            SwitchDevice.VirtualFileSystem.ImportTickets(partitionFileSystem);
            List<string> paths = new List<string>();

            foreach (DirectoryEntryEx fileEntry in partitionFileSystem.EnumerateEntries("/", "*.nca"))
            {
                using var ncaFile = new UniqueRef<IFile>();

                partitionFileSystem.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                Nca nca = TryOpenNca(ncaFile.Get.AsStorage(), path);
                if (nca == null)
                {
                    continue;
                }

                if (nca.Header.ContentType == NcaContentType.PublicData)
                {
                    if ((nca.Header.TitleId & 0xFFFFFFFFFFFFE000) != titleId)
                    {
                        break;
                    }

                    paths.Add(fileEntry.FullPath);
            }
            }

            return paths;
        }

        public static void SetupUiHandler()
        {
            if (SwitchDevice is { } switchDevice)
            {
                switchDevice.HostUiHandler = new AndroidUIHandler();
            }
        }

        public static void SetUiHandlerResponse(bool isOkPressed, string input)
        {
            if (SwitchDevice?.HostUiHandler is AndroidUIHandler uiHandler)
            {
                uiHandler.SetResponse(isOkPressed, input);
            }
        }

        public static List<string> GetCheats(string titleId, string gamePath)
        {
            var cheats = new List<string>();
            
            if (SwitchDevice?.VirtualFileSystem == null)
            {
                Logger.Warning?.Print(LogClass.Application, "VirtualFileSystem not initialized, cannot get cheats");
                return cheats;
            }
            
            // 获取金手指目录路径
            string modsBasePath = ModLoader.GetModsBasePath();
            string titleModsPath = ModLoader.GetApplicationDir(modsBasePath, titleId);
            string cheatsPath = Path.Combine(titleModsPath, "cheats");
            
            Logger.Info?.Print(LogClass.Application, $"Looking for cheats in: {cheatsPath}");
            
            if (!Directory.Exists(cheatsPath))
            {
                Logger.Info?.Print(LogClass.Application, $"Cheats directory does not exist: {cheatsPath}");
                return cheats;
            }
            
            // 读取金手指文件
            foreach (var file in Directory.GetFiles(cheatsPath, "*.txt"))
            {
                if (Path.GetFileName(file) == "enabled.txt") continue;
                
                string buildId = Path.GetFileNameWithoutExtension(file);
                var cheatInstructions = ModLoader.GetCheatsInFile(new FileInfo(file));
                
                foreach (var cheat in cheatInstructions)
                {
                    string cheatIdentifier = $"{buildId}-{cheat.Name}"; // 直接使用名称，不加 < >
                    cheats.Add(cheatIdentifier);
                }
            }
            
            return cheats;
        }

        public static List<string> GetEnabledCheats(string titleId)
        {
            var enabledCheats = new List<string>();
            
            // 获取已启用的金手指列表
            string modsBasePath = ModLoader.GetModsBasePath();
            string titleModsPath = ModLoader.GetApplicationDir(modsBasePath, titleId);
            string enabledCheatsPath = Path.Combine(titleModsPath, "cheats", "enabled.txt");
            
            if (File.Exists(enabledCheatsPath))
            {
                enabledCheats.AddRange(File.ReadAllLines(enabledCheatsPath));
            }
            
            return enabledCheats;
        }

        public static void SetCheatEnabled(string titleId, string cheatId, bool enabled)
        {
            // 这里需要修改enabled.txt文件，添加或移除金手指ID
            string modsBasePath = ModLoader.GetModsBasePath();
            string titleModsPath = ModLoader.GetApplicationDir(modsBasePath, titleId);
            string enabledCheatsPath = Path.Combine(titleModsPath, "cheats", "enabled.txt");
            
            var enabledCheats = new HashSet<string>();
            if (File.Exists(enabledCheatsPath))
            {
                // 确保读取时去除空行和空白字符
                var lines = File.ReadAllLines(enabledCheatsPath)
                              .Where(line => !string.IsNullOrWhiteSpace(line))
                              .Select(line => line.Trim());
                enabledCheats.UnionWith(lines);
            }
            
            if (enabled)
            {
                enabledCheats.Add(cheatId);
            }
            else
            {
                enabledCheats.Remove(cheatId);
            }
            
            Directory.CreateDirectory(Path.GetDirectoryName(enabledCheatsPath));
            File.WriteAllLines(enabledCheatsPath, enabledCheats);
            
            // 如果游戏正在运行，可能需要重新加载金手指
            if (SwitchDevice?.EmulationContext != null)
            {
                Logger.Info?.Print(LogClass.Emulation, "Cheat state changed but requires game restart to take effect");
            }
        }

        public static void SaveCheats(string titleId)
        {
            // 如果需要立即生效，可以在这里调用TamperMachine.EnableCheats
            // 但通常我们会在游戏启动时自动加载，所以这里可能不需要做任何事情
        }

        // ==================== 存档管理功能 ====================

        /// <summary>
        /// 获取所有存档文件夹的信息
        /// </summary>
        public static List<SaveDataInfo> GetSaveDataList()
        {
            var saveDataList = new List<SaveDataInfo>();
            
            if (SwitchDevice?.VirtualFileSystem == null)
                return saveDataList;

            try
            {
                string saveBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save");
                
                if (!Directory.Exists(saveBasePath))
                    return saveDataList;

                // 获取所有数字文件夹
                var saveDirs = Directory.GetDirectories(saveBasePath)
                    .Where(dir => Path.GetFileName(dir).All(char.IsDigit) && Path.GetFileName(dir).Length == 16)
                    .ToList();

                foreach (var saveDir in saveDirs)
                {
                    string saveId = Path.GetFileName(saveDir);
                    var saveInfo = GetSaveDataInfo(saveId);
                    if (saveInfo != null)
                    {
                        saveDataList.Add(saveInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error getting save data list: {ex.Message}");
            }

            return saveDataList;
        }

        /// <summary>
        /// 获取特定存档文件夹的详细信息
        /// </summary>
        private static SaveDataInfo GetSaveDataInfo(string saveId)
        {
            try
            {
                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                if (!Directory.Exists(savePath))
                    return null;

                // 从 ExtraData 文件中读取标题ID
                string titleId = ExtractTitleIdFromSaveData(savePath);
                string titleName = "Unknown Game";
                
                // 如果有标题ID，尝试获取游戏名称
                if (!string.IsNullOrEmpty(titleId) && titleId != "0000000000000000")
                {
                    // 这里可以调用现有的游戏信息获取方法
                    // titleName = GetGameNameByTitleId(titleId);
                }

                var directoryInfo = new DirectoryInfo(savePath);
                long totalSize = CalculateDirectorySize(savePath);

                return new SaveDataInfo
                {
                    SaveId = saveId,
                    TitleId = titleId ?? "0000000000000000",
                    TitleName = titleName,
                    LastModified = directoryInfo.LastWriteTime,
                    Size = totalSize
                };
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error getting save data info for {saveId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从 ExtraData 文件中提取标题ID
        /// </summary>
        private static string ExtractTitleIdFromSaveData(string savePath)
        {
            try
            {
                // 尝试读取 ExtraData0 或 ExtraData1 文件来获取标题ID
                string[] extraDataFiles = { "ExtraData0", "ExtraData1" };
                
                foreach (var fileName in extraDataFiles)
                {
                    string filePath = Path.Combine(savePath, fileName);
                    if (File.Exists(filePath))
                    {
                        using var fileStream = File.OpenRead(filePath);
                        if (fileStream.Length >= 8)
                        {
                            byte[] buffer = new byte[8];
                            fileStream.Read(buffer, 0, 8);
                            
                            // 将字节转换为十六进制字符串
                            string titleId = BitConverter.ToString(buffer).Replace("-", "").ToLower();
                            if (IsValidTitleId(titleId))
                            {
                                return titleId;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error extracting title ID from save data: {ex.Message}");
            }
            
            return null;
        }

        /// <summary>
        /// 验证标题ID格式
        /// </summary>
        private static bool IsValidTitleId(string titleId)
        {
            return !string.IsNullOrEmpty(titleId) && 
                   titleId.Length == 16 && 
                   titleId.All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f'));
        }

        /// <summary>
        /// 计算目录大小
        /// </summary>
        private static long CalculateDirectorySize(string path)
        {
            long size = 0;
            try
            {
                var directory = new DirectoryInfo(path);
                foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
                {
                    size += file.Length;
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error calculating directory size: {ex.Message}");
            }
            return size;
        }

        /// <summary>
        /// 根据游戏标题ID获取对应的存档文件夹ID
        /// </summary>
        public static string GetSaveIdByTitleId(string titleId)
        {
            var saveDataList = GetSaveDataList();
            var saveInfo = saveDataList.FirstOrDefault(s => s.TitleId == titleId);
            return saveInfo?.SaveId;
        }

        /// <summary>
        /// 导出存档为ZIP文件
        /// </summary>
        public static bool ExportSaveData(string titleId, string outputZipPath)
        {
            try
            {
                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    Logger.Error?.Print(LogClass.Application, $"No save data found for title ID: {titleId}");
                    return false;
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                if (!Directory.Exists(savePath))
                {
                    Logger.Error?.Print(LogClass.Application, $"Save directory not found: {savePath}");
                    return false;
                }

                // 使用 System.IO.Compression 创建ZIP文件
                ZipFile.CreateFromDirectory(savePath, outputZipPath);
                Logger.Info?.Print(LogClass.Application, $"Save data exported to: {outputZipPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error exporting save data: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从ZIP文件导入存档
        /// </summary>
        public static bool ImportSaveData(string titleId, string zipFilePath)
        {
            try
            {
                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    // 如果没有现有的存档文件夹，创建一个新的
                    saveId = FindNextAvailableSaveId();
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                
                // 如果目标目录存在，先备份然后删除
                if (Directory.Exists(savePath))
                {
                    string backupPath = savePath + "_backup_" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    Directory.Move(savePath, backupPath);
                }

                // 创建目录并解压ZIP文件
                Directory.CreateDirectory(savePath);
                ZipFile.ExtractToDirectory(zipFilePath, savePath);
                
                Logger.Info?.Print(LogClass.Application, $"Save data imported to: {savePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error importing save data: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 查找下一个可用的存档文件夹ID
        /// </summary>
        private static string FindNextAvailableSaveId()
        {
            string saveBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save");
            
            if (!Directory.Exists(saveBasePath))
                return "0000000000000001";

            var existingIds = Directory.GetDirectories(saveBasePath)
                .Where(dir => Path.GetFileName(dir).All(char.IsDigit) && Path.GetFileName(dir).Length == 16)
                .Select(dir => long.Parse(Path.GetFileName(dir)))
                .OrderBy(id => id)
                .ToList();

            long nextId = 1;
            if (existingIds.Any())
            {
                nextId = existingIds.Last() + 1;
            }

            return nextId.ToString("D16"); // 格式化为16位数字
        }

        /// <summary>
        /// 删除存档
        /// </summary>
        public static bool DeleteSaveData(string titleId)
        {
            try
            {
                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    Logger.Error?.Print(LogClass.Application, $"No save data found for title ID: {titleId}");
                    return false;
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                if (Directory.Exists(savePath))
                {
                    Directory.Delete(savePath, true);
                    Logger.Info?.Print(LogClass.Application, $"Save data deleted: {savePath}");
                    return true;
                }
                
                Logger.Warning?.Print(LogClass.Application, $"Save directory not found: {savePath}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error deleting save data: {ex.Message}");
                return false;
            }
        }
    }

    public class SwitchDevice : IDisposable
    {
        private readonly SystemVersion _firmwareVersion;
        public VirtualFileSystem VirtualFileSystem { get; set; }
        public ContentManager ContentManager { get; set; }
        public AccountManager AccountManager { get; set; }
        public LibHacHorizonManager LibHacHorizonManager { get; set; }
        public UserChannelPersistence UserChannelPersistence { get; set; }
        public InputManager? InputManager { get; set; }
        public Switch? EmulationContext { get; set; }
        public IHostUIHandler? HostUiHandler { get; set; }
        public bool EnableJitCacheEviction { get; set; }
        public bool EnableFsIntegrityChecks { get; set; }
        
        public void Dispose()
        {
            GC.SuppressFinalize(this);

            VirtualFileSystem.Dispose();
            InputManager?.Dispose();
            EmulationContext?.Dispose();
        }

        public SwitchDevice()
        {
            VirtualFileSystem = VirtualFileSystem.CreateInstance();
            LibHacHorizonManager = new LibHacHorizonManager();

            LibHacHorizonManager.InitializeFsServer(VirtualFileSystem);
            LibHacHorizonManager.InitializeArpServer();
            LibHacHorizonManager.InitializeBcatServer();
            LibHacHorizonManager.InitializeSystemClients();

            ContentManager = new ContentManager(VirtualFileSystem);
            AccountManager = new AccountManager(LibHacHorizonManager.RyujinxClient);
            UserChannelPersistence = new UserChannelPersistence();
            
            EnableFsIntegrityChecks = false;
            
            _firmwareVersion = ContentManager.GetCurrentFirmwareVersion();

            if (_firmwareVersion != null)
            {
                Logger.Notice.Print(LogClass.Application, $"System Firmware Version: {_firmwareVersion.VersionString}");
            }
            else
            {
                Logger.Notice.Print(LogClass.Application, $"System Firmware not installed");
            }
        }

        public bool InitializeContext(bool isHostMapped,
                                      bool useHypervisor,
                                      SystemLanguage systemLanguage,
                                      RegionCode regionCode,
                                      bool enableVsync,
                                      bool enableDockedMode,
                                      bool enablePtc,
                                      bool enableJitCacheEviction,
                                      bool enableInternetAccess,
                                      string? timeZone,
                                      bool ignoreMissingServices,
                                      MemoryConfiguration memoryConfiguration,
                                      long systemTimeOffset)  // 新增系统时间偏移参数
        {
            if (LibRyujinx.Renderer == null)
            {
                Logger.Error?.Print(LogClass.Application, "Renderer is not initialized. Cannot initialize device context.");
                return false;
            }

            // 记录初始化参数
            Logger.Info?.Print(LogClass.Application, $"Initializing device context with parameters:");
            Logger.Info?.Print(LogClass.Application, $"  - Memory Configuration: {memoryConfiguration}");
            Logger.Info?.Print(LogClass.Application, $"  - System Time Offset: {systemTimeOffset} seconds");
            Logger.Info?.Print(LogClass.Application, $"  - Aspect Ratio: {LibRyujinx.GetAspectRatio()}");
            Logger.Info?.Print(LogClass.Application, $"  - Host Mapped: {isHostMapped}");
            Logger.Info?.Print(LogClass.Application, $"  - Hypervisor: {useHypervisor}");
            Logger.Info?.Print(LogClass.Application, $"  - Vsync: {enableVsync}");
            Logger.Info?.Print(LogClass.Application, $"  - Docked Mode: {enableDockedMode}");
            Logger.Info?.Print(LogClass.Application, $"  - PTC: {enablePtc}");
            Logger.Info?.Print(LogClass.Application, $"  - JIT Cache Eviction: {enableJitCacheEviction}");
            Logger.Info?.Print(LogClass.Application, $"  - Internet Access: {enableInternetAccess}");
            Logger.Info?.Print(LogClass.Application, $"  - Time Zone: {timeZone ?? "Default"}");
            Logger.Info?.Print(LogClass.Application, $"  - Ignore Missing Services: {ignoreMissingServices}");

            var renderer = LibRyujinx.Renderer;
            BackendThreading threadingMode = LibRyujinx.GraphicsConfiguration.BackendThreading;

            bool threadedGAL = threadingMode == BackendThreading.On || (threadingMode == BackendThreading.Auto && renderer.PreferThreading);

            if (threadedGAL)
            {
                Logger.Info?.Print(LogClass.Application, "Using threaded renderer");
                renderer = new ThreadedRenderer(renderer);
            }
            else
            {
                Logger.Info?.Print(LogClass.Application, "Using non-threaded renderer");
            }

            HLEConfiguration configuration = new HLEConfiguration(VirtualFileSystem,
                                                                  LibHacHorizonManager,
                                                                  ContentManager,
                                                                  AccountManager,
                                                                  UserChannelPersistence,
                                                                  renderer,
                                                                  LibRyujinx.AudioDriver, //Audio
                                                                  memoryConfiguration, // 使用传入的内存配置参数
                                                                  HostUiHandler,
                                                                  systemLanguage,
                                                                  regionCode,
                                                                  enableVsync,
                                                                  enableDockedMode,
                                                                  enablePtc,
                                                                  enableJitCacheEviction,
                                                                  enableInternetAccess,
                                                                  IntegrityCheckLevel.None,
                                                                  0,
                                                                  systemTimeOffset,  // 传递系统时间偏移
                                                                  timeZone,
                                                                 // isHostMapped ? MemoryManagerMode.HostMappedUnsafe : MemoryManagerMode.SoftwarePageTable,
                                                                  MemoryManagerMode.HostMappedUnsafe,
                                                                  ignoreMissingServices,
                                                                   LibRyujinx.GetAspectRatio(),  // 使用 GetAspectRatio 方法获取当前画面比例
                                                                  100,
                                                                  useHypervisor,
                                                                  "",
                                                                  Ryujinx.Common.Configuration.Multiplayer.MultiplayerMode.Disabled);

            try
            {
                EmulationContext = new Switch(configuration);
                Logger.Info?.Print(LogClass.Application, "Device context initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Failed to initialize device context: {ex.Message}");
                return false;
            }
        }

        internal void ReloadFileSystem()
        {
            Logger.Info?.Print(LogClass.Application, "Reloading filesystem");
            VirtualFileSystem.ReloadKeySet();
            ContentManager = new ContentManager(VirtualFileSystem);
            AccountManager = new AccountManager(LibHacHorizonManager.RyujinxClient);
            Logger.Info?.Print(LogClass.Application, "Filesystem reloaded successfully");
        }

        internal void DisposeContext()
        {
            Logger.Info?.Print(LogClass.Application, "Disposing device context");
            EmulationContext?.Dispose();
            EmulationContext?.DisposeGpu();
            EmulationContext = null;
            LibRyujinx.Renderer = null;
            Logger.Info?.Print(LogClass.Application, "Device context disposed");
        }
    }

    public class GameInfo
    {
        public double FileSize;
        public string? TitleName;
        public string? TitleId;
        public string? Developer;
        public string? Version;
        public byte[]? Icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct GameInfoNative
    {
        public double FileSize;
        public char* TitleName;
        public char* TitleId;
        public char* Developer;
        public char* Version;
        public char* Icon;

        public GameInfoNative()
        {

        }

        public GameInfoNative(double fileSize, string? titleName, string? titleId, string? developer, string? version, byte[]? icon)
        {
            FileSize = fileSize;
            TitleId = (char*)Marshal.StringToHGlobalAnsi(titleId);
            Version = (char*)Marshal.StringToHGlobalAnsi(version);
            Developer = (char*)Marshal.StringToHGlobalAnsi(developer);
            TitleName = (char*)Marshal.StringToHGlobalAnsi(titleName);

            if (icon != null)
            {
                Icon = (char*)Marshal.StringToHGlobalAnsi(Convert.ToBase64String(icon));
            }
            else
            {
                Icon = (char*)0;
            }
        }

        public GameInfoNative(GameInfo info) : this(info.FileSize, info.TitleName, info.TitleId, info.Developer, info.Version, info.Icon){}
    }

    public class GameStats
    {
        public double Fifo;
        public double GameFps;
        public double GameTime;
    }

    // 存档信息类
    public class SaveDataInfo
    {
        public string SaveId { get; set; } = string.Empty; // 数字文件夹名，如 "0000000000000001"
        public string TitleId { get; set; } = string.Empty; // 游戏标题ID
        public string TitleName { get; set; } = string.Empty; // 游戏名称
        public DateTime LastModified { get; set; } // 最后修改时间
        public long Size { get; set; } // 存档大小
    }
}
