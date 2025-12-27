// HardwareDecoderManager.cs
using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    public class HardwareDecoderManager : IDisposable
    {
        private static HardwareDecoderManager _instance;
        private static readonly object _lock = new object();
        
        private readonly Dictionary<HWCodecType, HardwareDecoder> _decoders;
        private readonly Dictionary<HWCodecType, int> _decoderRefCounts;
        private readonly Dictionary<IntPtr, HardwareDecoder> _handleToDecoder;
        private readonly List<HardwareDecoder> _activeDecoders;
        private readonly object _decodersLock = new object();
        
        private bool _initialized;
        private bool _disposed;
        
        private Thread _monitorThread;
        private bool _monitorRunning;
        private int _monitorIntervalMs = 1000; // 1秒
        
        // 统计信息
        private int _totalFramesDecoded;
        private int _totalFramesDropped;
        private int _totalFramesCorrupted;
        private long _totalBytesDecoded;
        private double _totalDecodeTimeMs;
        
        // 事件
        public event Action<string> LogMessage;
        public event Action<HWDecoderError, string> DecoderError;
        public event Action<HWCodecType> DecoderCreated;
        public event Action<HWCodecType> DecoderDestroyed;
        public event Action<HardwareDecoder> DecoderActivity;
        public event Action<Dictionary<string, object>> StatisticsUpdated;
        
        // 单例实例
        public static HardwareDecoderManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new HardwareDecoderManager();
                    }
                }
                return _instance;
            }
        }
        
        // 私有构造函数
        private HardwareDecoderManager()
        {
            _decoders = new Dictionary<HWCodecType, HardwareDecoder>();
            _decoderRefCounts = new Dictionary<HWCodecType, int>();
            _handleToDecoder = new Dictionary<IntPtr, HardwareDecoder>();
            _activeDecoders = new List<HardwareDecoder>();
            
            _initialized = false;
            _disposed = false;
            
            _totalFramesDecoded = 0;
            _totalFramesDropped = 0;
            _totalFramesCorrupted = 0;
            _totalBytesDecoded = 0;
            _totalDecodeTimeMs = 0;
            
            Initialize();
        }
        
        // 初始化管理器
        private void Initialize()
        {
            if (_initialized)
                return;
            
            try
            {
                // 设置全局日志级别
                HardwareDecoder.SetGlobalLogLevel(HWLogLevel.HW_LOG_INFO);
                
                // 启动监控线程
                _monitorRunning = true;
                _monitorThread = new Thread(MonitorThread);
                _monitorThread.Name = "HardwareDecoderMonitor";
                _monitorThread.IsBackground = true;
                _monitorThread.Start();
                
                _initialized = true;
                
                Log($"Hardware decoder manager initialized (Version: {HardwareDecoder.GetVersion()})");
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize hardware decoder manager: {ex.Message}");
                throw;
            }
        }
        
        // 监控线程
        private void MonitorThread()
        {
            while (_monitorRunning)
            {
                try
                {
                    Thread.Sleep(_monitorIntervalMs);
                    
                    // 收集统计信息
                    CollectStatistics();
                    
                    // 清理不活跃的解码器
                    CleanupInactiveDecoders();
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogError($"Monitor thread error: {ex.Message}");
                }
            }
        }
        
        // 收集统计信息
        private void CollectStatistics()
        {
            lock (_decodersLock)
            {
                int activeDecoderCount = _activeDecoders.Count;
                int totalDecoderCount = _decoders.Count;
                
                Dictionary<string, object> stats = new Dictionary<string, object>
                {
                    ["ActiveDecoders"] = activeDecoderCount,
                    ["TotalDecoders"] = totalDecoderCount,
                    ["TotalFramesDecoded"] = _totalFramesDecoded,
                    ["TotalFramesDropped"] = _totalFramesDropped,
                    ["TotalFramesCorrupted"] = _totalFramesCorrupted,
                    ["TotalBytesDecoded"] = _totalBytesDecoded,
                    ["TotalDecodeTimeMs"] = _totalDecodeTimeMs,
                    ["Timestamp"] = DateTime.UtcNow
                };
                
                StatisticsUpdated?.Invoke(stats);
            }
        }
        
        // 清理不活跃的解码器
        private void CleanupInactiveDecoders()
        {
            lock (_decodersLock)
            {
                List<HardwareDecoder> toRemove = new List<HardwareDecoder>();
                
                foreach (var decoder in _activeDecoders)
                {
                    // 检查解码器是否已释放
                    if (decoder.IsDisposed)
                    {
                        toRemove.Add(decoder);
                    }
                }
                
                foreach (var decoder in toRemove)
                {
                    _activeDecoders.Remove(decoder);
                    Log($"Removed disposed decoder from active list");
                }
            }
        }
        
        // 创建解码器
        public HardwareDecoder CreateDecoder(HWCodecType codecType, int width, int height, bool useHardware = true)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(HardwareDecoderManager));
            
            lock (_decodersLock)
            {
                // 检查是否已有该类型解码器
                if (_decoders.TryGetValue(codecType, out var existingDecoder))
                {
                    // 增加引用计数
                    _decoderRefCounts[codecType]++;
                    
                    Log($"Reusing existing {codecType} decoder (RefCount: {_decoderRefCounts[codecType]})");
                    return existingDecoder;
                }
                
                try
                {
                    // 创建新解码器
                    var decoder = new HardwareDecoder(codecType, 
                        new HWDecoderConfig(width, height), useHardware);
                    
                    // 订阅事件
                    decoder.FrameDecoded += (frame) => OnDecoderFrameDecoded(decoder, frame);
                    decoder.ErrorOccurred += (error, message) => OnDecoderError(decoder, error, message);
                    decoder.FormatChanged += (config) => OnDecoderFormatChanged(decoder, config);
                    decoder.BufferStatusChanged += (level, capacity) => OnDecoderBufferStatusChanged(decoder, level, capacity);
                    
                    // 添加到字典
                    _decoders[codecType] = decoder;
                    _decoderRefCounts[codecType] = 1;
                    _handleToDecoder[decoder.Handle] = decoder;
                    _activeDecoders.Add(decoder);
                    
                    DecoderCreated?.Invoke(codecType);
                    Log($"Created new {codecType} decoder (Hardware: {useHardware}, Handle: 0x{decoder.Handle:X})");
                    
                    return decoder;
                }
                catch (Exception ex)
                {
                    LogError($"Failed to create {codecType} decoder: {ex.Message}");
                    throw;
                }
            }
        }
        
        // 创建H264解码器
        public HardwareDecoder CreateH264Decoder(int width = 1920, int height = 1080, bool useHardware = true)
        {
            return CreateDecoder(HWCodecType.HW_CODEC_H264, width, height, useHardware);
        }
        
        // 创建VP8解码器
        public HardwareDecoder CreateVP8Decoder(int width = 1920, int height = 1080, bool useHardware = true)
        {
            return CreateDecoder(HWCodecType.HW_CODEC_VP8, width, height, useHardware);
        }
        
        // 创建VP9解码器
        public HardwareDecoder CreateVP9Decoder(int width = 1920, int height = 1080, bool useHardware = true)
        {
            return CreateDecoder(HWCodecType.HW_CODEC_VP9, width, height, useHardware);
        }
        
        // 释放解码器
        public void ReleaseDecoder(HardwareDecoder decoder)
        {
            if (decoder == null || _disposed)
                return;
            
            lock (_decodersLock)
            {
                var codecType = decoder.CodecType;
                
                if (_decoders.TryGetValue(codecType, out var registeredDecoder) && 
                    registeredDecoder == decoder)
                {
                    // 减少引用计数
                    _decoderRefCounts[codecType]--;
                    
                    if (_decoderRefCounts[codecType] <= 0)
                    {
                        // 完全释放解码器
                        _decoders.Remove(codecType);
                        _decoderRefCounts.Remove(codecType);
                        _handleToDecoder.Remove(decoder.Handle);
                        _activeDecoders.Remove(decoder);
                        
                        decoder.Dispose();
                        
                        DecoderDestroyed?.Invoke(codecType);
                        Log($"Destroyed {codecType} decoder");
                    }
                    else
                    {
                        Log($"Decremented {codecType} decoder ref count to {_decoderRefCounts[codecType]}");
                    }
                }
            }
        }
        
        // 通过句柄获取解码器
        public HardwareDecoder GetDecoderByHandle(IntPtr handle)
        {
            lock (_decodersLock)
            {
                return _handleToDecoder.TryGetValue(handle, out var decoder) ? decoder : null;
            }
        }
        
        // 获取所有解码器
        public List<HardwareDecoder> GetAllDecoders()
        {
            lock (_decodersLock)
            {
                return new List<HardwareDecoder>(_activeDecoders);
            }
        }
        
        // 获取解码器统计信息
        public Dictionary<HWCodecType, int> GetDecoderStatistics()
        {
            lock (_decodersLock)
            {
                var stats = new Dictionary<HWCodecType, int>();
                foreach (var kvp in _decoderRefCounts)
                {
                    stats[kvp.Key] = kvp.Value;
                }
                return stats;
            }
        }
        
        // 检查硬件支持
        public bool IsHardwareSupported(HWCodecType codecType)
        {
            return HardwareDecoder.IsHardwareSupported(codecType);
        }
        
        // 获取所有支持的编解码器
        public List<HWCodecType> GetSupportedCodecs()
        {
            var supported = new List<HWCodecType>();
            
            // 测试所有编解码器类型
            var allCodecs = Enum.GetValues(typeof(HWCodecType));
            foreach (HWCodecType codec in allCodecs)
            {
                if (IsHardwareSupported(codec))
                {
                    supported.Add(codec);
                }
            }
            
            return supported;
        }
        
        // 解码器事件处理
        private void OnDecoderFrameDecoded(HardwareDecoder decoder, HWFrameData frame)
        {
            DecoderActivity?.Invoke(decoder);
            
            // 更新统计
            Interlocked.Increment(ref _totalFramesDecoded);
            
            // 记录解码统计
            decoder.GetStats(out var stats);
            _totalFramesDropped += (int)stats.frames_dropped;
            _totalFramesCorrupted += (int)stats.frames_corrupted;
            _totalBytesDecoded += stats.bytes_decoded;
            _totalDecodeTimeMs += stats.decode_time_ms;
        }
        
        private void OnDecoderError(HardwareDecoder decoder, HWDecoderError error, string message)
        {
            LogError($"{decoder.CodecType} decoder error [{error}]: {message}");
            DecoderError?.Invoke(error, message);
        }
        
        private void OnDecoderFormatChanged(HardwareDecoder decoder, HWDecoderConfig config)
        {
            Log($"{decoder.CodecType} decoder format changed: {config.width}x{config.height}");
        }
        
        private void OnDecoderBufferStatusChanged(HardwareDecoder decoder, int level, int capacity)
        {
            // 可以在这里实现缓冲区管理逻辑
        }
        
        // 日志辅助方法
        private void Log(string message)
        {
            Logger.Info?.Print(LogClass.FFmpeg, $"[HardwareDecoderManager] {message}");
            LogMessage?.Invoke(message);
        }
        
        private void LogError(string message)
        {
            Logger.Error?.Print(LogClass.FFmpeg, $"[HardwareDecoderManager] {message}");
            LogMessage?.Invoke($"ERROR: {message}");
        }
        
        // 全局设置
        public void SetMonitorInterval(int milliseconds)
        {
            if (milliseconds > 0)
            {
                _monitorIntervalMs = milliseconds;
                Log($"Monitor interval set to {milliseconds}ms");
            }
        }
        
        public void SetGlobalLogLevel(HWLogLevel level)
        {
            HardwareDecoder.SetGlobalLogLevel(level);
            Log($"Global log level set to {level}");
        }
        
        // 重置统计信息
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalFramesDecoded, 0);
            Interlocked.Exchange(ref _totalFramesDropped, 0);
            Interlocked.Exchange(ref _totalFramesCorrupted, 0);
            Interlocked.Exchange(ref _totalBytesDecoded, 0);
            Interlocked.Exchange(ref _totalDecodeTimeMs, 0);
            
            Log("Statistics reset");
        }
        
        // 获取全局统计信息
        public Dictionary<string, object> GetGlobalStatistics()
        {
            return new Dictionary<string, object>
            {
                ["TotalFramesDecoded"] = _totalFramesDecoded,
                ["TotalFramesDropped"] = _totalFramesDropped,
                ["TotalFramesCorrupted"] = _totalFramesCorrupted,
                ["TotalBytesDecoded"] = _totalBytesDecoded,
                ["TotalDecodeTimeMs"] = _totalDecodeTimeMs,
                ["ActiveDecoders"] = _activeDecoders.Count,
                ["Timestamp"] = DateTime.UtcNow
            };
        }
        
        // 资源清理
        public void Dispose()
        {
            if (_disposed)
                return;
            
            _disposed = true;
            
            // 停止监控线程
            _monitorRunning = false;
            if (_monitorThread != null && _monitorThread.IsAlive)
            {
                _monitorThread.Interrupt();
                _monitorThread.Join(1000);
            }
            
            // 清理所有解码器
            lock (_decodersLock)
            {
                foreach (var decoder in _activeDecoders)
                {
                    try
                    {
                        decoder.Dispose();
                    }
                    catch
                    {
                        // 忽略清理错误
                    }
                }
                
                _decoders.Clear();
                _decoderRefCounts.Clear();
                _handleToDecoder.Clear();
                _activeDecoders.Clear();
            }
            
            Log("Hardware decoder manager disposed");
        }
        
        ~HardwareDecoderManager()
        {
            Dispose();
        }
    }
}
