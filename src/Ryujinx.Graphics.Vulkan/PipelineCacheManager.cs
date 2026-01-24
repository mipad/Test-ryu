using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Reflection;

namespace Ryujinx.Graphics.Vulkan
{
    unsafe class PipelineCacheManager : IDisposable
    {
        private const uint CacheMagic = 0x4B4E5552; // "RGNX" (Ryujinx)
        private const uint CacheVersion = 4; // 版本升级
        
        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly string _globalCacheDir;
        private readonly string _gameSpecificCacheDir;
        private readonly object _cacheLock = new();
        private bool _enableDiskCache = true;
        
        private PipelineCache _pipelineCache;
        private string _currentGameId;
        private bool _cacheLoaded;
        private CacheStatistics _stats;

        // 缓存统计信息
        private class CacheStatistics
        {
            public int TotalShaders;
            public int CacheHits;
            public int CacheMisses;
            public long CacheSizeBytes;
            public DateTime LastSaveTime;
        }

        public PipelineCacheManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            
            try
            {
                // 基础缓存目录
                string basePath = GetBaseCachePath();
                
                Logger.Info?.Print(LogClass.Gpu, $"Pipeline cache base path: {basePath}");
                
                if (string.IsNullOrEmpty(basePath))
                {
                    throw new InvalidOperationException("Failed to determine cache base path");
                }
                
                // 确保目录存在
                try
                {
                    Directory.CreateDirectory(basePath);
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Failed to create base directory {basePath}: {ex.Message}");
                    throw;
                }
                
                // 设置具体目录
                _globalCacheDir = Path.Combine(basePath, "vulkan", "global");
                _gameSpecificCacheDir = Path.Combine(basePath, "vulkan", "games");
                
                // 创建子目录
                Directory.CreateDirectory(_globalCacheDir);
                Directory.CreateDirectory(_gameSpecificCacheDir);
                
                Logger.Info?.Print(LogClass.Gpu, $"Pipeline cache directories created successfully");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Failed to initialize pipeline cache directories: {ex.Message}");
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Cache will be memory-only, performance may be affected");
                
                // 设置为空，使用内存缓存
                _globalCacheDir = null;
                _gameSpecificCacheDir = null;
                
                // 标记为无磁盘缓存
                _enableDiskCache = false;
            }
            
            _stats = new CacheStatistics();
        }

        private string GetBaseCachePath()
        {
            // 只在Android平台运行
            Logger.Info?.Print(LogClass.Gpu, "Detected Android platform, using Android-specific cache paths");
            
            // 尝试多个可能的缓存目录（优先级从高到低）
            string[] possiblePaths = new string[]
            {
                // 1. 主应用数据目录（如果AppDataManager可用）
                TryGetAppDataPath(),
                // 2. Android标准应用数据目录
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "cache"),
                // 3. Ryujinx的Android特定目录（参考mods路径）
                "/storage/emulated/0/Android/data/org.karyujinx.android/files/cache",
                "/storage/emulated/0/Android/data/com.ryujinx.android/files/cache",
                // 4. 外部存储的通用目录
                "/storage/emulated/0/Android/data/org.ryujinx/cache",
                "/data/data/com.ryujinx.ryujinx/files/cache",
                "/data/user/0/com.ryujinx.ryujinx/files/cache",
                // 5. 当前工作目录下的cache子目录
                Path.Combine(Environment.CurrentDirectory, "cache"),
            };
            
            foreach (var path in possiblePaths)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    Logger.Info?.Print(LogClass.Gpu, $"Trying cache path: {path}");
                    
                    try
                    {
                        // 检查路径是否存在或可以创建
                        string parentDir = Path.GetDirectoryName(path);
                        if (parentDir != null && CanCreateDirectory(parentDir))
                        {
                            // 尝试创建目录
                            Directory.CreateDirectory(path);
                            
                            // 尝试写入测试文件确保有写入权限
                            string testFile = Path.Combine(path, ".test_write");
                            File.WriteAllText(testFile, "test");
                            File.Delete(testFile);
                            
                            Logger.Info?.Print(LogClass.Gpu, $"Using cache directory: {path}");
                            return path;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"Failed to use cache path {path}: {ex.Message}");
                        continue;
                    }
                }
            }
            
            // 如果所有路径都失败，返回一个临时目录（不保证可写）
            string fallback = Path.Combine(Path.GetTempPath(), "ryujinx_cache");
            Logger.Warning?.Print(LogClass.Gpu, $"All cache paths failed, using fallback: {fallback}");
            return fallback;
        }

        // 尝试从AppDataManager获取路径
        private string TryGetAppDataPath()
        {
            try
            {
                // 使用反射尝试获取AppDataManager.BaseDirPath
                var appDataManagerType = Type.GetType("Ryujinx.Common.Configuration.AppDataManager, Ryujinx.Common");
                if (appDataManagerType != null)
                {
                    var baseDirPathProperty = appDataManagerType.GetProperty("BaseDirPath", 
                        BindingFlags.Public | BindingFlags.Static);
                    if (baseDirPathProperty != null)
                    {
                        string baseDir = baseDirPathProperty.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(baseDir))
                        {
                            Logger.Info?.Print(LogClass.Gpu, $"Found AppDataManager.BaseDirPath: {baseDir}");
                            string cachePath = Path.Combine(baseDir, "cache");
                            return cachePath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Failed to get AppDataManager path: {ex.Message}");
            }
            
            return null;
        }

        // 检查是否可以创建目录
        private bool CanCreateDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    return true;
                    
                // 尝试创建目录（包括父目录）
                Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Cannot create directory {path}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 设置当前游戏ID，切换游戏缓存
        /// </summary>
        public void SetCurrentGame(string gameId, string gameTitle = null)
        {
            if (string.IsNullOrEmpty(gameId))
                return;
            
            lock (_cacheLock)
            {
                if (_currentGameId == gameId)
                    return;
                
                // 保存当前游戏的缓存（如果有）
                SavePipelineCache();
                
                // 卸载当前缓存
                if (_pipelineCache.Handle != 0)
                {
                    _gd.Api.DestroyPipelineCache(_device, _pipelineCache, null);
                    _pipelineCache = default;
                    _cacheLoaded = false;
                }
                
                // 设置新游戏ID
                _currentGameId = gameId;
                _stats = new CacheStatistics();
                
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Switching to game cache: {gameId} {(string.IsNullOrEmpty(gameTitle) ? "" : $"({gameTitle})")}");
            }
        }

        /// <summary>
        /// 获取游戏特定的缓存文件路径
        /// </summary>
        private string GetGameCacheFilePath(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(_gameSpecificCacheDir))
                return null;
            
            try
            {
                // 使用游戏ID的MD5哈希作为文件名，避免特殊字符问题
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(gameId));
                var hashString = BitConverter.ToString(hash).Replace("-", "").ToLower();
                
                return Path.Combine(_gameSpecificCacheDir, $"pipeline_cache_{hashString}.bin");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Failed to generate game cache file path: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取通用全局缓存文件路径（用于跨游戏共享的基础着色器）
        /// </summary>
        private string GetGlobalCacheFilePath()
        {
            if (string.IsNullOrEmpty(_globalCacheDir))
                return null;
            
            return Path.Combine(_globalCacheDir, "global_pipeline_cache.bin");
        }

        /// <summary>
        /// 获取或创建PipelineCache
        /// </summary>
        public unsafe PipelineCache GetOrCreatePipelineCache()
        {
            if (_pipelineCache.Handle != 0)
            {
                return _pipelineCache;
            }

            lock (_cacheLock)
            {
                if (_pipelineCache.Handle != 0)
                {
                    return _pipelineCache;
                }

                byte[] cacheData = null;
                string cachePath = null;

                // 首先尝试加载游戏特定缓存
                if (!string.IsNullOrEmpty(_currentGameId))
                {
                    cachePath = GetGameCacheFilePath(_currentGameId);
                    if (cachePath != null)
                    {
                        cacheData = TryLoadCacheData(cachePath);
                    }
                }

                // 如果没有游戏缓存，尝试加载全局缓存
                if (cacheData == null)
                {
                    cachePath = GetGlobalCacheFilePath();
                    if (cachePath != null)
                    {
                        cacheData = TryLoadCacheData(cachePath);
                        
                        if (cacheData != null)
                        {
                            Logger.Info?.Print(LogClass.Gpu, 
                                "Using global pipeline cache (no game-specific cache found)");
                        }
                    }
                }

                var pipelineCacheCreateInfo = new PipelineCacheCreateInfo
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                };

                if (cacheData != null && cacheData.Length > 0)
                {
                    pipelineCacheCreateInfo.PInitialData = (void*)Marshal.AllocHGlobal(cacheData.Length);
                    Marshal.Copy(cacheData, 0, (IntPtr)pipelineCacheCreateInfo.PInitialData, cacheData.Length);
                    pipelineCacheCreateInfo.InitialDataSize = (nuint)cacheData.Length;
                }

                // 修复：使用指针而不是fixed语句
                PipelineCacheCreateInfo* pPipelineCacheCreateInfo = &pipelineCacheCreateInfo;
                var result = _gd.Api.CreatePipelineCache(_device, pPipelineCacheCreateInfo, null, out _pipelineCache);
                
                if (pipelineCacheCreateInfo.PInitialData != null)
                {
                    Marshal.FreeHGlobal((IntPtr)pipelineCacheCreateInfo.PInitialData);
                }
                
                result.ThrowOnError();
                
                _cacheLoaded = cacheData != null;

                if (_cacheLoaded && cachePath != null)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Loaded pipeline cache ({cacheData.Length / 1024} KB) from {Path.GetFileName(cachePath)}");
                }
                else
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        "Created new pipeline cache (no valid cache found)");
                }

                return _pipelineCache;
            }
        }

        private byte[] TryLoadCacheData(string cachePath)
        {
            // 如果禁用了磁盘缓存或者路径为空，直接返回null
            if (!_enableDiskCache || string.IsNullOrEmpty(cachePath))
            {
                Logger.Debug?.Print(LogClass.Gpu, "Disk cache disabled or path invalid, skipping load");
                return null;
            }

            try
            {
                if (!File.Exists(cachePath))
                {
                    Logger.Debug?.Print(LogClass.Gpu, $"Cache file does not exist: {cachePath}");
                    return null;
                }

                byte[] cacheData = File.ReadAllBytes(cachePath);
                
                if (!ValidateCacheData(cacheData))
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Cache validation failed for {Path.GetFileName(cachePath)}");
                    return null;
                }

                Logger.Info?.Print(LogClass.Gpu, $"Successfully loaded cache from {cachePath} ({cacheData.Length} bytes)");
                return cacheData;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Failed to load cache from {cachePath}: {ex.Message}");
                return null;
            }
        }

        private bool ValidateCacheData(byte[] cacheData)
        {
            if (cacheData.Length < 32)
            {
                Logger.Warning?.Print(LogClass.Gpu, "Cache data too small");
                return false;
            }

            // 检查魔法数字和版本
            uint magic = BitConverter.ToUInt32(cacheData, 0);
            if (magic != CacheMagic)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Invalid cache magic: {magic:X8}");
                return false;
            }
            
            uint version = BitConverter.ToUInt32(cacheData, 4);
            if (version < 3 || version > CacheVersion) // 只接受版本3-4
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Unsupported cache version: {version}");
                return false;
            }

            // 获取当前设备的VendorID和DeviceID
            _gd.Api.GetPhysicalDeviceProperties(_gd.GetPhysicalDevice().PhysicalDevice, out var properties);
            
            // 检查VendorID和DeviceID
            uint cachedVendorId = BitConverter.ToUInt32(cacheData, 24);
            uint cachedDeviceId = BitConverter.ToUInt32(cacheData, 28);
            
            if (cachedVendorId != properties.VendorID || cachedDeviceId != properties.DeviceID)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Cache device mismatch: cached ({cachedVendorId:X}:{cachedDeviceId:X}) != current ({properties.VendorID:X}:{properties.DeviceID:X})");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 保存当前PipelineCache到磁盘
        /// </summary>
        public unsafe void SavePipelineCache(bool force = false)
        {
            // 如果禁用了磁盘缓存，直接返回
            if (!_enableDiskCache)
            {
                Logger.Debug?.Print(LogClass.Gpu, "Disk cache disabled, skipping save");
                return;
            }

            if (_pipelineCache.Handle == 0)
                return;

            // 如果不是强制保存，检查是否需要保存（距离上次保存时间）
            if (!force && _stats.LastSaveTime > DateTime.Now.AddMinutes(-5))
                return;

            lock (_cacheLock)
            {
                try
                {
                    nuint dataSize = 0;
                    _gd.Api.GetPipelineCacheData(_device, _pipelineCache, &dataSize, null);

                    if (dataSize == 0)
                        return;

                    byte[] cacheData = new byte[dataSize];
                    
                    // 修复：使用局部指针变量，避免fixed表达式问题
                    fixed (byte* pCacheData = cacheData)
                    {
                        _gd.Api.GetPipelineCacheData(_device, _pipelineCache, &dataSize, pCacheData);
                    }

                    // 添加自定义头信息
                    byte[] finalData = AddCacheHeader(cacheData);
                    
                    // 确定保存路径
                    string cachePath;
                    if (string.IsNullOrEmpty(_currentGameId))
                    {
                        cachePath = GetGlobalCacheFilePath();
                        if (cachePath == null)
                        {
                            Logger.Warning?.Print(LogClass.Gpu, "Global cache path is null, cannot save");
                            return;
                        }
                    }
                    else
                    {
                        cachePath = GetGameCacheFilePath(_currentGameId);
                        if (cachePath == null)
                        {
                            Logger.Warning?.Print(LogClass.Gpu, "Game cache path is null, cannot save");
                            return;
                        }
                    }

                    // 确保目录存在
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"Failed to create cache directory: {ex.Message}");
                        return;
                    }
                    
                    // 保存文件
                    File.WriteAllBytes(cachePath, finalData);
                    
                    // 更新统计
                    _stats.CacheSizeBytes = finalData.Length;
                    _stats.LastSaveTime = DateTime.Now;
                    
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Saved pipeline cache ({finalData.Length / 1024} KB) to {Path.GetFileName(cachePath)}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Failed to save pipeline cache: {ex.Message}");
                }
            }
        }

        private byte[] AddCacheHeader(byte[] vulkanCacheData)
        {
            // 获取设备属性
            _gd.Api.GetPhysicalDeviceProperties(_gd.GetPhysicalDevice().PhysicalDevice, out var properties);
            
            // 版本4头结构：
            // [0-3]   魔法数字 (0x4B4E5552 = "RGNX")
            // [4-7]   版本号 (4)
            // [8-23]  预留（原PipelineCache UUID位置，现在不验证）
            // [24-27] VendorID
            // [28-31] DeviceID
            // [32-39] DriverVersion
            // [40-47] 保存时间戳（Unix时间）
            // [48-55] 游戏ID哈希（可选，全局缓存为0）
            // [56-]   Vulkan原始缓存数据
            
            int headerSize = 56;
            byte[] finalData = new byte[headerSize + vulkanCacheData.Length];
            
            // 魔法数字和版本
            BitConverter.GetBytes(CacheMagic).CopyTo(finalData, 0);
            BitConverter.GetBytes(CacheVersion).CopyTo(finalData, 4);
            
            // 预留8-23字节为0（原UUID位置）
            for (int i = 8; i < 24; i++)
            {
                finalData[i] = 0;
            }
            
            // VendorID和DeviceID
            BitConverter.GetBytes(properties.VendorID).CopyTo(finalData, 24);
            BitConverter.GetBytes(properties.DeviceID).CopyTo(finalData, 28);
            
            // DriverVersion
            BitConverter.GetBytes(properties.DriverVersion).CopyTo(finalData, 32);
            
            // 时间戳
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BitConverter.GetBytes(timestamp).CopyTo(finalData, 40);
            
            // 游戏ID哈希
            ulong gameHash = GetGameIdHash(_currentGameId);
            BitConverter.GetBytes(gameHash).CopyTo(finalData, 48);
            
            // 复制Vulkan缓存数据
            vulkanCacheData.CopyTo(finalData, headerSize);
            
            return finalData;
        }

        private ulong GetGameIdHash(string gameId)
        {
            if (string.IsNullOrEmpty(gameId))
                return 0;
            
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(gameId));
            return BitConverter.ToUInt64(hash, 0);
        }

        /// <summary>
        /// 获取缓存统计信息
        /// </summary>
        public string GetStatistics()
        {
            return $"Cache hits: {_stats.CacheHits}, misses: {_stats.CacheMisses}, " +
                   $"size: {_stats.CacheSizeBytes / 1024} KB, " +
                   $"last saved: {_stats.LastSaveTime:yyyy-MM-dd HH:mm:ss}, " +
                   $"disk cache enabled: {_enableDiskCache}";
        }

        /// <summary>
        /// 记录缓存命中
        /// </summary>
        public void RecordCacheHit()
        {
            _stats.CacheHits++;
        }

        /// <summary>
        /// 记录缓存未命中
        /// </summary>
        public void RecordCacheMiss()
        {
            _stats.CacheMisses++;
        }

        /// <summary>
        /// 清理特定游戏的缓存
        /// </summary>
        public void ClearGameCache(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || !_enableDiskCache)
                return;
            
            string cachePath = GetGameCacheFilePath(gameId);
            if (cachePath != null && File.Exists(cachePath))
            {
                try
                {
                    File.Delete(cachePath);
                    Logger.Info?.Print(LogClass.Gpu, $"Cleared cache for game: {gameId}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Failed to clear cache for game {gameId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 清理所有游戏缓存
        /// </summary>
        public void ClearAllGameCaches()
        {
            if (!_enableDiskCache || string.IsNullOrEmpty(_gameSpecificCacheDir))
                return;
            
            try
            {
                if (!Directory.Exists(_gameSpecificCacheDir))
                {
                    Logger.Info?.Print(LogClass.Gpu, "Game cache directory does not exist, nothing to clear");
                    return;
                }
                
                foreach (var file in Directory.GetFiles(_gameSpecificCacheDir, "pipeline_cache_*.bin"))
                {
                    File.Delete(file);
                }
                
                Logger.Info?.Print(LogClass.Gpu, "Cleared all game-specific pipeline caches");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Failed to clear game caches: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取所有游戏缓存的大小信息
        /// </summary>
        public (string GameId, long Size, DateTime Modified)[] GetGameCacheInfos()
        {
            if (!_enableDiskCache || string.IsNullOrEmpty(_gameSpecificCacheDir) || !Directory.Exists(_gameSpecificCacheDir))
                return Array.Empty<(string, long, DateTime)>();
            
            var files = Directory.GetFiles(_gameSpecificCacheDir, "pipeline_cache_*.bin");
            var result = new (string, long, DateTime)[files.Length];
            
            for (int i = 0; i < files.Length; i++)
            {
                var fileInfo = new FileInfo(files[i]);
                result[i] = (Path.GetFileNameWithoutExtension(files[i]), 
                           fileInfo.Length, 
                           fileInfo.LastWriteTime);
            }
            
            return result;
        }

        public void Dispose()
        {
            SavePipelineCache(true); // 强制保存
            
            if (_pipelineCache.Handle != 0)
            {
                _gd.Api.DestroyPipelineCache(_device, _pipelineCache, null);
                _pipelineCache = default;
            }
            
            // 打印统计信息
            Logger.Info?.Print(LogClass.Gpu, $"Pipeline cache statistics: {GetStatistics()}");
        }
    }
}