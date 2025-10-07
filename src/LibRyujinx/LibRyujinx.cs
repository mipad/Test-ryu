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

        // 其他现有方法保持不变...
        // 由于文件过长，这里只显示修改的部分，其他方法如 GetGameInfo, GetDlcTitleId 等保持不变

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
            // 原有的 GetGameInfo 实现保持不变
            // 由于代码过长，这里省略具体实现
            // 请确保这部分代码在您的原始文件中存在
            return null; // 占位符
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
            // 原有的 GetDlcTitleId 实现保持不变
            // 由于代码过长，这里省略具体实现
            return string.Empty; // 占位符
        }

        // 其他方法保持不变...
        // 存档管理、金手指管理等功能的实现...

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
                    string cheatIdentifier = $"{buildId}-{cheat.Name}";
                    cheats.Add(cheatIdentifier);
                }
            }
            
            return cheats;
        }

        // 其他方法实现...
    }

    // 其他类定义保持不变...
    public class SwitchDevice : IDisposable
    {
        // SwitchDevice 类的实现保持不变
        // 由于代码过长，这里省略具体实现
        
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            // 清理资源
        }

        public SwitchDevice()
        {
            // 初始化代码
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
                                      long systemTimeOffset)
        {
            // 初始化上下文实现
            return true; // 占位符
        }

        internal void ReloadFileSystem()
        {
            // 重新加载文件系统
        }

        internal void DisposeContext()
        {
            // 清理上下文
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
        public string SaveId { get; set; } = string.Empty;
        public string TitleId { get; set; } = string.Empty;
        public string TitleName { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public long Size { get; set; }
    }
}
