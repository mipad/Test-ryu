using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineCacheManager : IDisposable
    {
        private const uint CacheMagic = 0x4B4E5552; // "RGNX" (Ryujinx)
        private const uint CacheVersion = 4; // 版本升级
        
        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly string _globalCacheDir;
        private readonly string _gameSpecificCacheDir;
        private readonly object _cacheLock = new();
        
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
            
            // 基础缓存目录
            string basePath = GetBaseCachePath();
            _globalCacheDir = Path.Combine(basePath, "vulkan", "global");
            _gameSpecificCacheDir = Path.Combine(basePath, "vulkan", "games");
            
            // 创建目录
            Directory.CreateDirectory(_globalCacheDir);
            Directory.CreateDirectory(_gameSpecificCacheDir);
            
            _stats = new CacheStatistics();
        }

        private string GetBaseCachePath()
        {
            // 使用AppDataManager获取基础路径
            string basePath = AppDataManager.BaseDirectoryPath;
            return Path.Combine(basePath, "cache");
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
            if (string.IsNullOrEmpty(gameId))
                return Path.Combine(_globalCacheDir, "global_pipeline_cache.bin");
            
            // 使用游戏ID的MD5哈希作为文件名，避免特殊字符问题
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(gameId));
            var hashString = BitConverter.ToString(hash).Replace("-", "").ToLower();
            
            return Path.Combine(_gameSpecificCacheDir, $"pipeline_cache_{hashString}.bin");
        }

        /// <summary>
        /// 获取通用全局缓存文件路径（用于跨游戏共享的基础着色器）
        /// </summary>
        private string GetGlobalCacheFilePath()
        {
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
                    cacheData = TryLoadCacheData(cachePath);
                }

                // 如果没有游戏缓存，尝试加载全局缓存
                if (cacheData == null)
                {
                    cachePath = GetGlobalCacheFilePath();
                    cacheData = TryLoadCacheData(cachePath);
                    
                    if (cacheData != null)
                    {
                        Logger.Info?.Print(LogClass.Gpu, 
                            "Using global pipeline cache (no game-specific cache found)");
                    }
                }

                var pipelineCacheCreateInfo = new PipelineCacheCreateInfo
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                };

                if (cacheData != null && cacheData.Length > 0)
                {
                    fixed (byte* pCacheData = cacheData)
                    {
                        pipelineCacheCreateInfo.PInitialData = pCacheData;
                        pipelineCacheCreateInfo.InitialDataSize = (nuint)cacheData.Length;
                    }
                }

                _gd.Api.CreatePipelineCache(_device, in pipelineCacheCreateInfo, null, out _pipelineCache).ThrowOnError();
                _cacheLoaded = cacheData != null;

                if (_cacheLoaded)
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
            try
            {
                if (!File.Exists(cachePath))
                    return null;

                byte[] cacheData = File.ReadAllBytes(cachePath);
                
                if (!ValidateCacheData(cacheData))
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Cache validation failed for {Path.GetFileName(cachePath)}");
                    return null;
                }

                return cacheData;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Failed to load cache from {cachePath}: {ex.Message}");
                return null;
            }
        }

        private unsafe bool ValidateCacheData(byte[] cacheData)
        {
            if (cacheData.Length < 32)
                return false;

            fixed (byte* pData = cacheData)
            {
                // 检查魔法数字和版本
                uint* pUint = (uint*)pData;
                if (pUint[0] != CacheMagic)
                    return false;
                
                uint version = pUint[1];
                if (version < 3 || version > CacheVersion) // 只接受版本3-4
                    return false;

                // 获取设备属性进行验证
                _gd.Api.GetPhysicalDeviceProperties(_gd._physicalDevice.PhysicalDevice, out var properties);
                
                // 检查UUID
                byte* pUuid = pData + 8;
                for (int i = 0; i < 16; i++)
                {
                    if (pUuid[i] != properties.PipelineCacheUUID[i])
                        return false;
                }

                // 版本4新增：检查驱动程序构建时间戳
                if (version >= 4)
                {
                    // 可选：可以在这里添加更严格的验证
                }

                return true;
            }
        }

        /// <summary>
        /// 保存当前PipelineCache到磁盘
        /// </summary>
        public unsafe void SavePipelineCache(bool force = false)
        {
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
                    _gd.Api.GetPipelineCacheData(_device, _pipelineCache, ref dataSize, null);

                    if (dataSize == 0)
                        return;

                    byte[] cacheData = new byte[dataSize];
                    fixed (byte* pCacheData = cacheData)
                    {
                        _gd.Api.GetPipelineCacheData(_device, _pipelineCache, ref dataSize, pCacheData);
                    }

                    // 添加自定义头信息
                    byte[] finalData = AddCacheHeader(cacheData);
                    
                    // 确定保存路径
                    string cachePath;
                    if (string.IsNullOrEmpty(_currentGameId))
                    {
                        cachePath = GetGlobalCacheFilePath();
                    }
                    else
                    {
                        cachePath = GetGameCacheFilePath(_currentGameId);
                    }

                    // 保存文件
                    File.WriteAllBytes(cachePath, finalData);
                    
                    // 更新统计
                    _stats.CacheSizeBytes = finalData.Length;
                    _stats.LastSaveTime = DateTime.Now;
                    
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"Saved pipeline cache ({finalData.Length / 1024} KB) to {Path.GetFileName(cachePath)}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Failed to save pipeline cache: {ex.Message}");
                }
            }
        }

        private unsafe byte[] AddCacheHeader(byte[] vulkanCacheData)
        {
            _gd.Api.GetPhysicalDeviceProperties(_gd._physicalDevice.PhysicalDevice, out var properties);
            
            // 版本4头结构：
            // [0-3]   魔法数字 (0x4B4E5552 = "RGNX")
            // [4-7]   版本号 (4)
            // [8-23]  PipelineCache UUID (16字节)
            // [24-27] VendorID
            // [28-31] DeviceID
            // [32-39] DriverVersion
            // [40-47] 保存时间戳（Unix时间）
            // [48-55] 游戏ID哈希（可选，全局缓存为0）
            // [56-]   Vulkan原始缓存数据
            
            int headerSize = 56;
            byte[] finalData = new byte[headerSize + vulkanCacheData.Length];
            
            fixed (byte* pFinalData = finalData)
            {
                // 魔法数字和版本
                uint* pMagic = (uint*)pFinalData;
                pMagic[0] = CacheMagic;
                pMagic[1] = CacheVersion;
                
                // UUID
                byte* pUuid = pFinalData + 8;
                for (int i = 0; i < 16; i++)
                {
                    pUuid[i] = properties.PipelineCacheUUID[i];
                }
                
                // VendorID和DeviceID
                uint* pIds = (uint*)(pFinalData + 24);
                pIds[0] = properties.VendorID;
                pIds[1] = properties.DeviceID;
                
                // DriverVersion
                ulong* pDriverVersion = (ulong*)(pFinalData + 32);
                *pDriverVersion = properties.DriverVersion;
                
                // 时间戳
                long* pTimestamp = (long*)(pFinalData + 40);
                *pTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                
                // 游戏ID哈希
                ulong* pGameHash = (ulong*)(pFinalData + 48);
                *pGameHash = GetGameIdHash(_currentGameId);
                
                // 复制Vulkan缓存数据
                Marshal.Copy(vulkanCacheData, 0, (IntPtr)(pFinalData + headerSize), vulkanCacheData.Length);
            }
            
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
                   $"last saved: {_stats.LastSaveTime:yyyy-MM-dd HH:mm:ss}";
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
            if (string.IsNullOrEmpty(gameId))
                return;
            
            string cachePath = GetGameCacheFilePath(gameId);
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
                Logger.Info?.Print(LogClass.Gpu, $"Cleared cache for game: {gameId}");
            }
        }

        /// <summary>
        /// 清理所有游戏缓存
        /// </summary>
        public void ClearAllGameCaches()
        {
            try
            {
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