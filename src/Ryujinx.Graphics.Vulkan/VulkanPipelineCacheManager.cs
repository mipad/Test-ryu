using System;
using System.IO;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;

public class VulkanPipelineCacheManager
{
    private readonly string _cacheDirectory;
    private const string CacheFileName = "vulkan_pipeline_cache.bin";

    // 构造函数：确定缓存文件路径
    public VulkanPipelineCacheManager()
    {
        // 示例路径：Ryujinx/游戏ID/cache/shader/vulkan_pipeline_cache.bin
        _cacheDirectory = Path.Combine(
            AppDataManager.GamesDirPath,
            GraphicsConfig.TitleId,
            "cache",
            "shader"
        );

        // 如果目录不存在，则创建它
        Directory.CreateDirectory(_cacheDirectory);
    }

    // 加载缓存数据
    public byte[] LoadPipelineCache()
    {
        string cachePath = Path.Combine(_cacheDirectory, CacheFileName);
        if (File.Exists(cachePath))
        {
            try
            {
                return File.ReadAllBytes(cachePath);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"加载Vulkan管线缓存失败: {ex.Message}");
                return null;
            }
        }
        return null; // 没有缓存文件
    }

    // 保存缓存数据
    public void SavePipelineCache(byte[] data)
    {
        if (data == null || data.Length == 0) return;

        string cachePath = Path.Combine(_cacheDirectory, CacheFileName);
        try
        {
            File.WriteAllBytes(cachePath, data);
        }
        catch (Exception ex)
        {
            Logger.Warning?.Print(LogClass.Gpu, $"保存Vulkan管线缓存失败: {ex.Message}");
        }
    }
}
