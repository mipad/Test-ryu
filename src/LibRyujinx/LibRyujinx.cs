using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS;
using Ryujinx.Input.HLE;
using Ryujinx.HLE;
using Ryujinx.Common.Utilities;
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
using System.IO.Compression;
using LibHac.FsSrv;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibRyujinx
{
    public static partial class LibRyujinx
    {
        internal static IHardwareDeviceDriver AudioDriver { get; set; } = new DummyHardwareDeviceDriver();

        private static readonly TitleUpdateMetadataJsonSerializerContext _titleSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());
        
        // 添加 ModMetadata 序列化上下文
        [JsonSerializable(typeof(ModMetadata))]
        public partial class ModMetadataJsonSerializerContext : JsonSerializerContext
        {
        }
        
        private static readonly ModMetadataJsonSerializerContext _modSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());
        
        public static SwitchDevice? SwitchDevice { get; set; }

        // 添加静态字段来存储画面比例
        private static AspectRatio _currentAspectRatio = AspectRatio.Stretched;

        // 添加静态字段来存储内存配置
        private static MemoryConfiguration _currentMemoryConfiguration = MemoryConfiguration.MemoryConfiguration8GiB;

        // 添加静态字段来存储系统时间偏移
        private static long _systemTimeOffset = 0;

        // Mod 相关类型定义
        public class ModInfo
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
            
            [JsonPropertyName("path")]
            public string Path { get; set; } = string.Empty;
            
            [JsonPropertyName("enabled")]
            public bool Enabled { get; set; }
            
            [JsonPropertyName("inExternalStorage")]
            public bool InExternalStorage { get; set; }
            
            [JsonPropertyName("type")]
            public ModType Type { get; set; }
        }

        public enum ModType
        {
            RomFs,
            ExeFs
        }

        public class ModMetadata
        {
            [JsonPropertyName("mods")]
            public List<ModEntry> Mods { get; set; } = new List<ModEntry>();
        }

        public class ModEntry
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
            
            [JsonPropertyName("path")]
            public string Path { get; set; } = string.Empty;
            
            [JsonPropertyName("enabled")]
            public bool Enabled { get; set; }
        }

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
            
            // 如果设备已初始化，立即应用新的画面比例
            if (SwitchDevice?.EmulationContext != null)
            {
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
            
            // 如果设备已初始化，记录需要重启才能生效
            if (SwitchDevice?.EmulationContext != null)
            {
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
            
            // 如果设备已初始化，记录需要重启才能生效
            if (SwitchDevice?.EmulationContext != null)
            {
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

        // ==================== Mod 管理功能 ====================

        /// <summary>
        /// 获取指定标题ID的Mod列表
        /// </summary>
        public static List<ModInfo> GetMods(string titleId)
        {
            var mods = new List<ModInfo>();
            
            if (SwitchDevice?.VirtualFileSystem == null)
            {
                return mods;
            }

            try
            {
                string[] modsBasePaths = { 
                    Path.Combine(AppDataManager.BaseDirPath, "mods"),
                    "/storage/emulated/0/Android/data/org.ryujinx.android/files/mods"
                };

                foreach (var basePath in modsBasePaths)
                {
                    if (!Directory.Exists(basePath))
                        continue;

                    var inExternal = basePath.StartsWith("/storage/emulated/0");
                    var modCache = new ModLoader.ModCache();
                    var contentsDir = new DirectoryInfo(Path.Combine(basePath, "contents"));
                    
                    if (contentsDir.Exists)
                    {
                        // 使用 ulong 类型的 titleId
                        if (ulong.TryParse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdNum))
                        {
                            // 修复：传递空的DLC列表作为第四个参数
                            ModLoader.QueryContentsDir(modCache, contentsDir, titleIdNum, new ulong[0]);

                            // 处理 romfs 目录
                            foreach (var mod in modCache.RomfsDirs)
                            {
                                var modPath = mod.Path.Parent.FullName;
                                var modName = mod.Name;
                                
                                if (mods.All(x => x.Path != modPath))
                                {
                                    mods.Add(new ModInfo
                                    {
                                        Name = modName,
                                        Path = modPath,
                                        Enabled = mod.Enabled,
                                        InExternalStorage = inExternal,
                                        Type = ModType.RomFs
                                    });
                                }
                            }

                            // 处理 romfs 容器
                            foreach (var mod in modCache.RomfsContainers)
                            {
                                mods.Add(new ModInfo
                                {
                                    Name = mod.Name,
                                    Path = mod.Path.FullName,
                                    Enabled = mod.Enabled,
                                    InExternalStorage = inExternal,
                                    Type = ModType.RomFs
                                });
                            }

                            // 处理 exefs 目录
                            foreach (var mod in modCache.ExefsDirs)
                            {
                                var modPath = mod.Path.Parent.FullName;
                                var modName = mod.Name;
                                
                                if (mods.All(x => x.Path != modPath))
                                {
                                    mods.Add(new ModInfo
                                    {
                                        Name = modName,
                                        Path = modPath,
                                        Enabled = mod.Enabled,
                                        InExternalStorage = inExternal,
                                        Type = ModType.ExeFs
                                    });
                                }
                            }

                            // 处理 exefs 容器
                            foreach (var mod in modCache.ExefsContainers)
                            {
                                mods.Add(new ModInfo
                                {
                                    Name = mod.Name,
                                    Path = mod.Path.FullName,
                                    Enabled = mod.Enabled,
                                    InExternalStorage = inExternal,
                                    Type = ModType.ExeFs
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error getting mods: {ex.Message}");
            }

            return mods;
        }

        /// <summary>
        /// 设置Mod启用状态
        /// </summary>
        public static bool SetModEnabled(string titleId, string modPath, bool enabled)
        {
            try
            {
                string modJsonPath = Path.Combine(AppDataManager.GamesDirPath, titleId, "mods.json");
                
                ModMetadata modData = new ModMetadata();
                
                // 如果文件存在，读取现有数据
                if (File.Exists(modJsonPath))
                {
                    try
                    {
                        modData = JsonHelper.DeserializeFromFile(modJsonPath, _modSerializerContext.ModMetadata);
                    }
                    catch
                    {
                        modData = new ModMetadata();
                    }
                }

                // 查找并更新Mod状态
                var mod = modData.Mods.FirstOrDefault(m => m.Path == modPath);
                if (mod != null)
                {
                    mod.Enabled = enabled;
                }
                else
                {
                    // 如果Mod不存在，添加新条目
                    modData.Mods.Add(new ModEntry
                    {
                        Name = Path.GetFileName(modPath),
                        Path = modPath,
                        Enabled = enabled
                    });
                }

                // 保存到文件
                JsonHelper.SerializeToFile(modJsonPath, modData, _modSerializerContext.ModMetadata);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error setting mod enabled: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除Mod
        /// </summary>
        public static bool DeleteMod(string titleId, string modPath)
        {
            try
            {
                if (Directory.Exists(modPath))
                {
                    Directory.Delete(modPath, true);
                    
                    // 从mods.json中移除
                    RemoveModFromJson(titleId, modPath);
                    
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error deleting mod: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除所有Mod
        /// </summary>
        public static bool DeleteAllMods(string titleId)
        {
            try
            {
                var mods = GetMods(titleId);
                bool success = true;
                
                foreach (var mod in mods)
                {
                    if (!DeleteMod(titleId, mod.Path))
                    {
                        success = false;
                    }
                }
                
                return success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error deleting all mods: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从mods.json中移除Mod条目
        /// </summary>
        private static void RemoveModFromJson(string titleId, string modPath)
        {
            try
            {
                string modJsonPath = Path.Combine(AppDataManager.GamesDirPath, titleId, "mods.json");
                
                if (File.Exists(modJsonPath))
                {
                    var modData = JsonHelper.DeserializeFromFile(modJsonPath, _modSerializerContext.ModMetadata);
                    modData.Mods.RemoveAll(m => m.Path == modPath);
                    JsonHelper.SerializeToFile(modJsonPath, modData, _modSerializerContext.ModMetadata);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error removing mod from JSON: {ex.Message}");
            }
        }

        /// <summary>
        /// 启用所有Mod
        /// </summary>
        public static bool EnableAllMods(string titleId)
        {
            try
            {
                var mods = GetMods(titleId);
                bool success = true;
                
                foreach (var mod in mods)
                {
                    if (!SetModEnabled(titleId, mod.Path, true))
                    {
                        success = false;
                    }
                }
                
                return success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error enabling all mods: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 禁用所有Mod
        /// </summary>
        public static bool DisableAllMods(string titleId)
        {
            try
            {
                var mods = GetMods(titleId);
                bool success = true;
                
                foreach (var mod in mods)
                {
                    if (!SetModEnabled(titleId, mod.Path, false))
                    {
                        success = false;
                    }
                }
                
                return success;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error disabling all mods: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 添加Mod（从源目录复制到Mod目录）
        /// </summary>
        public static bool AddMod(string titleId, string sourcePath, string modName)
        {
            try
            {
                if (!Directory.Exists(sourcePath))
                {
                    return false;
                }

                // 确定目标路径
                string targetBasePath = Path.Combine(AppDataManager.BaseDirPath, "mods", "contents", titleId);
                string targetPath = Path.Combine(targetBasePath, modName);
                
                // 如果目标已存在，添加数字后缀
                if (Directory.Exists(targetPath))
                {
                    int counter = 1;
                    string newTargetPath;
                    do
                    {
                        newTargetPath = $"{targetPath}_{counter}";
                        counter++;
                    } while (Directory.Exists(newTargetPath));
                    targetPath = newTargetPath;
                }

                // 复制目录
                CopyDirectory(sourcePath, targetPath);
                
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error adding mod: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 复制目录及其所有内容
        /// </summary>
        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            
            if (!dir.Exists)
                return;

            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath, true);
            }

            foreach (DirectoryInfo subDir in dir.GetDirectories())
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir);
            }
        }

        public static GameInfo? GetGameInfo(string? file)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return new GameInfo();
            }

            using var stream = File.Open(file, FileMode.Open);

            return GetGameInfo(stream, new FileInfo(file).Extension.Remove('.'));
        }

        public static GameInfo? GetGameInfo(Stream gameStream, string extension)
        {
            if (SwitchDevice == null)
            {
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
                }
                catch (InvalidDataException exception)
                {
                }
                catch (Exception exception)
                {
                    return null;
                }
            }
            catch (IOException exception)
            {
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
                    controlFs = null;
                    titleId = null;
                    return;
                }
                (_, _, Nca? controlNca) = GetGameData(SwitchDevice.VirtualFileSystem, pfs, 0);

                if (controlNca == null)
                {
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
                }
                catch (MissingKeyException exception)
                {
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
                return nca;
            }
            catch (Exception ex)
            {
                return null;
            }
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
                return cheats;
            }
            
            // 获取金手指目录路径
            string modsBasePath = ModLoader.GetModsBasePath();
            string titleModsPath = ModLoader.GetApplicationDir(modsBasePath, titleId);
            string cheatsPath = Path.Combine(titleModsPath, "cheats");
            
            if (!Directory.Exists(cheatsPath))
            {
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
            }
        }

        public static void SaveCheats(string titleId)
        {
            // 如果需要立即生效，可以在这里调用TamperMachine.EnableCheats
            // 但通常我们会在游戏启动时自动加载，所以这里可能不需要做任何事情
        }

        // ==================== 改进的存档管理功能 ====================

        /// <summary>
        /// 检查指定标题ID的存档是否存在（改进版本）
        /// </summary>
        public static bool SaveDataExists(string titleId)
        {
            return !string.IsNullOrEmpty(GetSaveIdByTitleId(titleId));
        }

        /// <summary>
        /// 获取所有存档文件夹的信息（改进版本）
        /// </summary>
        public static List<SaveDataInfo> GetSaveDataList()
        {
            var saveDataList = new List<SaveDataInfo>();
            
            if (SwitchDevice?.VirtualFileSystem == null)
                return saveDataList;

            try
            {
                string saveBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save");
                string saveMetaBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "saveMeta");
                
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
                return saveDataList;
            }

            return saveDataList;
        }

        /// <summary>
        /// 获取特定存档文件夹的详细信息（改进版本）
        /// </summary>
        private static SaveDataInfo GetSaveDataInfo(string saveId)
        {
            try
            {
                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                if (!Directory.Exists(savePath))
                    return null;

                // 优先从 saveMeta 目录读取标题ID
                string titleId = GetTitleIdFromSaveMeta(saveId);
                
                // 如果 saveMeta 中没有找到，回退到 ExtraData 方法
                if (string.IsNullOrEmpty(titleId) || titleId == "0000000000000000")
                {
                    titleId = ExtractTitleIdFromExtraData(savePath);
                }

                string titleName = "Unknown Game";
                
                // 如果有标题ID，尝试获取游戏名称
                if (!string.IsNullOrEmpty(titleId) && titleId != "0000000000000000")
                {
                    titleName = $"Game [{titleId}]"; // 可以进一步改进为获取实际游戏名称
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
                return null;
            }
        }

        /// <summary>
        /// 从 saveMeta 目录读取标题ID（新增方法）
        /// </summary>
        private static string GetTitleIdFromSaveMeta(string saveId)
        {
            try
            {
                string saveMetaPath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "saveMeta", saveId);
                if (!Directory.Exists(saveMetaPath))
                {
                    return null;
                }

                string metaFilePath = Path.Combine(saveMetaPath, "00000001.meta");
                if (!File.Exists(metaFilePath))
                {
                    return null;
                }

                // 读取 meta 文件并解析标题ID
                using var fileStream = File.OpenRead(metaFilePath);
                if (fileStream.Length >= 8)
                {
                    byte[] buffer = new byte[8];
                    fileStream.Read(buffer, 0, 8);
                    
                    // meta 文件中的标题ID通常是小端序
                    ulong titleIdValue = BitConverter.ToUInt64(buffer, 0);
                    string titleId = titleIdValue.ToString("x16");
                    
                    if (IsValidTitleId(titleId))
                    {
                        return titleId;
                    }
                }
            }
            catch (Exception ex)
            {
            }
            
            return null;
        }

        /// <summary>
        /// 改进的 ExtraData 标题ID提取方法
        /// </summary>
        private static string ExtractTitleIdFromExtraData(string savePath)
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
                            
                            // 尝试两种字节序
                            ulong titleIdValue1 = BitConverter.ToUInt64(buffer, 0);
                            string titleId1 = titleIdValue1.ToString("x16");
                            
                            if (IsValidTitleId(titleId1))
                            {
                                return titleId1;
                            }
                            
                            // 如果是大端序，需要反转字节
                            Array.Reverse(buffer);
                            ulong titleIdValue2 = BitConverter.ToUInt64(buffer, 0);
                            string titleId2 = titleIdValue2.ToString("x16");
                            
                            if (IsValidTitleId(titleId2))
                            {
                                return titleId2;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }
            
            return null;
        }

        /// <summary>
        /// 改进的验证标题ID格式方法
        /// </summary>
        private static bool IsValidTitleId(string titleId)
        {
            if (string.IsNullOrEmpty(titleId) || titleId.Length != 16)
                return false;
            
            // 检查是否全是0（无效）
            if (titleId.All(c => c == '0'))
                return false;
                
            // 检查格式：16位十六进制
            return titleId.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
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
            }
            return size;
        }

        /// <summary>
        /// 根据游戏标题ID获取对应的存档文件夹ID（改进版本）
        /// </summary>
        public static string GetSaveIdByTitleId(string titleId)
        {
            if (string.IsNullOrEmpty(titleId) || titleId == "0000000000000000")
                return null;

            var saveDataList = GetSaveDataList();
            var saveInfo = saveDataList.FirstOrDefault(s => s.TitleId == titleId);
            
            if (saveInfo != null)
            {
                return saveInfo.SaveId;
            }
            
            return null;
        }

        /// <summary>
        /// 导出存档为ZIP文件（只导出0和1文件夹）
        /// </summary>
        public static bool ExportSaveData(string titleId, string outputZipPath)
        {
            try
            {
                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    return false;
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                if (!Directory.Exists(savePath))
                {
                    return false;
                }

                // 确保输出目录存在
                Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath));

                // 创建临时目录用于存放0和1文件夹
                string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempPath);

                try
                {
                    // 只复制0和1文件夹
                    string[] foldersToExport = { "0", "1" };
                    foreach (string folder in foldersToExport)
                    {
                        string sourceFolder = Path.Combine(savePath, folder);
                        string destFolder = Path.Combine(tempPath, folder);
                        
                        if (Directory.Exists(sourceFolder))
                        {
                            CopyDirectory(sourceFolder, destFolder);
                        }
                    }

                    // 使用 System.IO.Compression 创建ZIP文件
                    ZipFile.CreateFromDirectory(tempPath, outputZipPath, CompressionLevel.Optimal, false);
                    return true;
                }
                finally
                {
                    // 清理临时目录
                    if (Directory.Exists(tempPath))
                    {
                        Directory.Delete(tempPath, true);
                    }
                }
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 从ZIP文件导入存档（只导入0和1文件夹）
        /// </summary>
        public static bool ImportSaveData(string titleId, string zipFilePath)
        {
            try
            {
                if (!File.Exists(zipFilePath))
                {
                    return false;
                }

                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    // 如果没有现有的存档文件夹，创建一个新的
                    saveId = FindNextAvailableSaveId();
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                
                // 创建目标目录
                Directory.CreateDirectory(savePath);

                // 创建临时目录用于解压
                string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempPath);

                try
                {
                    // 解压ZIP文件到临时目录
                    ZipFile.ExtractToDirectory(zipFilePath, tempPath);
                    
                    // 只复制0和1文件夹到目标目录
                    string[] foldersToImport = { "0", "1" };
                    foreach (string folder in foldersToImport)
                    {
                        string sourceFolder = Path.Combine(tempPath, folder);
                        string destFolder = Path.Combine(savePath, folder);
                        
                        if (Directory.Exists(sourceFolder))
                        {
                            // 如果目标文件夹已存在，先删除
                            if (Directory.Exists(destFolder))
                            {
                                Directory.Delete(destFolder, true);
                            }
                            
                            CopyDirectory(sourceFolder, destFolder);
                        }
                    }
                    
                    return true;
                }
                finally
                {
                    // 清理临时目录
                    if (Directory.Exists(tempPath))
                    {
                        Directory.Delete(tempPath, true);
                    }
                }
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 创建saveMeta文件（新增方法）
        /// </summary>
        private static void CreateSaveMetaFile(string saveMetaPath, string titleId)
        {
            try
            {
                string metaFilePath = Path.Combine(saveMetaPath, "00000001.meta");
                
                if (ulong.TryParse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdValue))
                {
                    byte[] titleIdBytes = BitConverter.GetBytes(titleIdValue);
                    
                    // 确保是小端序
                    if (!BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(titleIdBytes);
                    }
                    
                    File.WriteAllBytes(metaFilePath, titleIdBytes);
                }
            }
            catch (Exception ex)
            {
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
                .Select(dir => {
                    if (long.TryParse(Path.GetFileName(dir), out long id))
                        return id;
                    return 0L;
                })
                .Where(id => id > 0)
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
        /// 删除存档（改进版本，同时删除saveMeta）
        /// </summary>
        public static bool DeleteSaveData(string titleId)
        {
            try
            {
                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    return false;
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                string saveMetaPath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "saveMeta", saveId);
                
                bool success = true;

                // 删除存档目录
                if (Directory.Exists(savePath))
                {
                    Directory.Delete(savePath, true);
                }
                else
                {
                    success = false;
                }

                // 删除存档元数据目录
                if (Directory.Exists(saveMetaPath))
                {
                    Directory.Delete(saveMetaPath, true);
                }
                
                return success;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 删除存档文件（只删除0和1文件夹中的文件，保留存档文件夹结构）
        /// </summary>
        public static bool DeleteSaveFiles(string titleId)
        {
            try
            {
                string saveId = GetSaveIdByTitleId(titleId);
                if (string.IsNullOrEmpty(saveId))
                {
                    return false;
                }

                string savePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save", saveId);
                if (!Directory.Exists(savePath))
                {
                    return false;
                }

                bool success = true;

                // 只删除0和1文件夹中的内容，保留文件夹结构
                string[] foldersToDelete = { "0", "1" };
                foreach (string folder in foldersToDelete)
                {
                    string folderPath = Path.Combine(savePath, folder);
                    if (Directory.Exists(folderPath))
                    {
                        try
                        {
                            // 删除文件夹中的所有内容，但保留文件夹本身
                            var directory = new DirectoryInfo(folderPath);
                            foreach (FileInfo file in directory.GetFiles())
                            {
                                file.Delete();
                            }
                            foreach (DirectoryInfo subDirectory in directory.GetDirectories())
                            {
                                subDirectory.Delete(true);
                            }
                        }
                        catch (Exception ex)
                        {
                            success = false;
                        }
                    }
                }
                
                return success;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 调试方法：显示所有存档文件夹的详细信息
        /// </summary>
        public static void DebugSaveData()
        {
            string saveBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "save");
            string saveMetaBasePath = Path.Combine(AppDataManager.BaseDirPath, "bis", "user", "saveMeta");
            
            if (!Directory.Exists(saveBasePath))
            {
                return;
            }
            
            var saveDirs = Directory.GetDirectories(saveBasePath)
                .Where(dir => Path.GetFileName(dir).All(char.IsDigit) && Path.GetFileName(dir).Length == 16)
                .ToList();
            
            foreach (var saveDir in saveDirs)
            {
                string saveId = Path.GetFileName(saveDir);
                
                // 检查 saveMeta
                string saveMetaPath = Path.Combine(saveMetaBasePath, saveId);
                if (Directory.Exists(saveMetaPath))
                {
                    string metaFile = Path.Combine(saveMetaPath, "00000001.meta");
                }
                
                // 检查 ExtraData 文件
                string[] extraDataFiles = { "ExtraData0", "ExtraData1" };
                foreach (var fileName in extraDataFiles)
                {
                    string filePath = Path.Combine(saveDir, fileName);
                }
                
                // 尝试获取标题ID
                string titleIdFromMeta = GetTitleIdFromSaveMeta(saveId);
                string titleIdFromExtra = ExtractTitleIdFromExtraData(saveDir);
            }
        }

        /// <summary>
        /// 强制刷新存档列表（新增方法）
        /// </summary>
        public static void RefreshSaveData()
        {
            // 强制重新扫描文件系统
            var freshList = GetSaveDataList();
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
                return false;
            }

            var renderer = LibRyujinx.Renderer;
            BackendThreading threadingMode = LibRyujinx.GraphicsConfiguration.BackendThreading;

            bool threadedGAL = threadingMode == BackendThreading.On || (threadingMode == BackendThreading.Auto && renderer.PreferThreading);

            if (threadedGAL)
            {
                renderer = new ThreadedRenderer(renderer);
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
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        internal void ReloadFileSystem()
        {
            VirtualFileSystem.ReloadKeySet();
            ContentManager = new ContentManager(VirtualFileSystem);
            AccountManager = new AccountManager(LibHacHorizonManager.RyujinxClient);
        }

        internal void DisposeContext()
        {
            EmulationContext?.Dispose();
            EmulationContext?.DisposeGpu();
            EmulationContext = null;
            LibRyujinx.Renderer = null;
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
