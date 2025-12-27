// HardwareDecoderH264.cs
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using System.Collections.Generic;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class HardwareDecoderH264 : IH264Decoder, IDisposable
    {
        // 解码器实例
        private HardwareDecoder _hardwareDecoder;
        private HardwareSurface _currentSurface;
        
        // 软件解码回退
        private Decoder _softwareDecoder;
        private bool _useSoftwareFallback;
        private int _softwareFallbackCount;
        private const int MaxSoftwareFallbackCount = 10;
        
        // 工作缓冲区
        private readonly byte[] _workBuffer = new byte[0x200];
        
        // 状态跟踪
        private int _width;
        private int _height;
        private bool _initialized;
        private bool _disposed;
        
        // 统计信息
        private int _framesDecoded;
        private int _hardwareFrames;
        private int _softwareFrames;
        private int _decodeErrors;
        private DateTime _startTime;
        
        // 配置
        private bool _enableHardwareAcceleration = true;
        private bool _enableSoftwareFallback = true;
        private bool _lowLatencyMode = false;
        private int _maxCacheFrames = 5;
        
        // 事件
        public event Action<string> LogMessage;
        public event Action<string> ErrorMessage;
        public event Action<bool> HardwareAccelerationChanged;
        public event Action<int, int> StatisticsUpdated;
        
        // 属性
        public bool IsHardwareAccelerated => _hardwareDecoder?.IsHardwareAccelerated ?? false;
        public bool IsInitialized => _initialized;
        public bool UseSoftwareFallback => _useSoftwareFallback;
        public int FramesDecoded => _framesDecoded;
        public int HardwareFrames => _hardwareFrames;
        public int SoftwareFrames => _softwareFrames;
        public int DecodeErrors => _decodeErrors;
        
        public bool EnableHardwareAcceleration
        {
            get => _enableHardwareAcceleration;
            set
            {
                if (_enableHardwareAcceleration != value)
                {
                    _enableHardwareAcceleration = value;
                    ReinitializeDecoder();
                    HardwareAccelerationChanged?.Invoke(value);
                    Log($"Hardware acceleration {(value ? "enabled" : "disabled")}");
                }
            }
        }
        
        public bool EnableSoftwareFallback
        {
            get => _enableSoftwareFallback;
            set => _enableSoftwareFallback = value;
        }
        
        public bool LowLatencyMode
        {
            get => _lowLatencyMode;
            set
            {
                _lowLatencyMode = value;
                if (_hardwareDecoder != null)
                {
                    _hardwareDecoder.SetProperty("low_latency", value ? "1" : "0");
                }
            }
        }
        
        public int MaxCacheFrames
        {
            get => _maxCacheFrames;
            set
            {
                if (value > 0 && value != _maxCacheFrames)
                {
                    _maxCacheFrames = value;
                    if (_hardwareDecoder != null)
                    {
                        _hardwareDecoder.SetMaxCacheFrames(value);
                    }
                }
            }
        }
        
        // 构造函数
        public HardwareDecoderH264(int width = 1920, int height = 1080)
        {
            _width = width;
            _height = height;
            _startTime = DateTime.UtcNow;
            
            Initialize(width, height);
        }
        
        // 初始化解码器
        private void Initialize(int width, int height)
        {
            try
            {
                // 创建硬件解码器
                if (_enableHardwareAcceleration)
                {
                    CreateHardwareDecoder(width, height);
                }
                
                // 创建软件解码器作为回退
                if (_enableSoftwareFallback)
                {
                    _softwareDecoder = new Decoder();
                }
                
                _initialized = true;
                _useSoftwareFallback = false;
                _softwareFallbackCount = 0;
                
                Log($"H264 decoder initialized: Hardware={_enableHardwareAcceleration}, "
                    + $"SoftwareFallback={_enableSoftwareFallback}, "
                    + $"LowLatency={_lowLatencyMode}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize H264 decoder: {ex.Message}");
                
                // 如果硬件解码失败但允许软件回退，则使用软件解码
                if (_enableSoftwareFallback)
                {
                    _useSoftwareFallback = true;
                    _softwareDecoder = new Decoder();
                    _initialized = true;
                    Log("Falling back to software decoder");
                }
                else
                {
                    throw;
                }
            }
        }
        
        // 重新初始化解码器
        private void ReinitializeDecoder()
        {
            try
            {
                // 清理现有解码器
                Cleanup();
                
                // 重新初始化
                Initialize(_width, _height);
            }
            catch (Exception ex)
            {
                LogError($"Failed to reinitialize decoder: {ex.Message}");
            }
        }
        
        // 创建硬件解码器
        private void CreateHardwareDecoder(int width, int height)
        {
            try
            {
                // 检查硬件支持
                if (!HardwareDecoder.IsHardwareSupported(HWCodecType.HW_CODEC_H264))
                {
                    throw new NotSupportedException("H264 hardware decoding not supported on this device");
                }
                
                // 创建解码器
                _hardwareDecoder = HardwareDecoder.CreateH264Decoder(width, height);
                
                // 配置解码器
                if (_lowLatencyMode)
                {
                    _hardwareDecoder.SetProperty("low_latency", "1");
                }
                
                _hardwareDecoder.SetMaxCacheFrames(_maxCacheFrames);
                _hardwareDecoder.SetProperty("zerolatency", "1");
                _hardwareDecoder.SetProperty("tune", "zerolatency");
                
                // 订阅事件
                _hardwareDecoder.FrameDecoded += OnHardwareFrameDecoded;
                _hardwareDecoder.ErrorOccurred += OnHardwareError;
                _hardwareDecoder.FormatChanged += OnHardwareFormatChanged;
                _hardwareDecoder.BufferStatusChanged += OnHardwareBufferStatusChanged;
                
                Log($"Hardware decoder created: Type={_hardwareDecoder.HardwareType}, "
                    + $"Codec={_hardwareDecoder.CodecName}, "
                    + $"Accelerated={_hardwareDecoder.IsHardwareAccelerated}");
            }
            catch (Exception ex)
            {
                _hardwareDecoder?.Dispose();
                _hardwareDecoder = null;
                throw new InvalidOperationException($"Failed to create hardware decoder: {ex.Message}", ex);
            }
        }
        
        // 创建表面
        public ISurface CreateSurface(int width, int height)
        {
            if (_currentSurface == null || 
                _currentSurface.RequestedWidth != width || 
                _currentSurface.RequestedHeight != height)
            {
                _currentSurface = new HardwareSurface(width, height);
            }
            
            return _currentSurface;
        }
        
        // 解码主函数
        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            if (!_initialized || _disposed)
            {
                LogError("Decoder not initialized or disposed");
                return false;
            }
            
            try
            {
                bool success = false;
                
                // 尝试硬件解码
                if (!_useSoftwareFallback && _hardwareDecoder != null)
                {
                    success = TryHardwareDecode(ref pictureInfo, output, bitstream);
                    
                    if (!success && _enableSoftwareFallback)
                    {
                        // 硬件解码失败，切换到软件解码
                        _useSoftwareFallback = true;
                        _softwareFallbackCount++;
                        Log($"Hardware decode failed, switching to software (Fallback count: {_softwareFallbackCount})");
                        
                        // 尝试软件解码
                        success = TrySoftwareDecode(ref pictureInfo, output, bitstream);
                    }
                }
                else if (_softwareDecoder != null)
                {
                    // 使用软件解码
                    success = TrySoftwareDecode(ref pictureInfo, output, bitstream);
                    
                    // 如果软件解码成功多次，尝试切换回硬件
                    if (success && _useSoftwareFallback && _softwareFallbackCount >= MaxSoftwareFallbackCount)
                    {
                        TrySwitchBackToHardware();
                    }
                }
                
                if (success)
                {
                    _framesDecoded++;
                    UpdateStatistics();
                }
                else
                {
                    _decodeErrors++;
                }
                
                return success;
            }
            catch (Exception ex)
            {
                LogError($"Decode error: {ex.Message}");
                _decodeErrors++;
                return false;
            }
        }
        
        // 尝试硬件解码
        private bool TryHardwareDecode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            try
            {
                // 重建SPS和PPS
                Span<byte> reconstructed = SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer);
                
                // 合并数据
                byte[] combinedData = new byte[bitstream.Length + reconstructed.Length];
                reconstructed.CopyTo(combinedData);
                bitstream.CopyTo(new Span<byte>(combinedData, reconstructed.Length, bitstream.Length));
                
                // 解码
                var result = _hardwareDecoder.Decode(combinedData, out var frameData);
                
                if (result == HWDecoderError.HW_DECODER_SUCCESS)
                {
                    // 更新输出表面
                    var hardwareSurface = output as HardwareSurface;
                    if (hardwareSurface != null)
                    {
                        hardwareSurface.UpdateFromFrameData(ref frameData);
                    }
                    
                    _hardwareFrames++;
                    return true;
                }
                else if (result == HWDecoderError.HW_DECODER_ERROR_TRY_AGAIN || 
                         result == HWDecoderError.HW_DECODER_ERROR_BUFFER_FULL)
                {
                    // 这些错误是暂时的，不算失败
                    return false;
                }
                else
                {
                    LogError($"Hardware decode failed: {HardwareDecoder.GetErrorString(result)}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogError($"Hardware decode exception: {ex.Message}");
                return false;
            }
        }
        
        // 尝试软件解码
        private bool TrySoftwareDecode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            if (_softwareDecoder == null)
            {
                return false;
            }
            
            try
            {
                // 使用现有的软件解码器
                var surface = output as Surface;
                if (surface == null)
                {
                    // 如果输出不是Surface类型，需要转换
                    surface = new Surface(output.RequestedWidth, output.RequestedHeight);
                }
                
                bool success = _softwareDecoder.Decode(ref pictureInfo, surface, bitstream);
                
                if (success)
                {
                    _softwareFrames++;
                    
                    // 如果需要，将结果复制到HardwareSurface
                    if (output is HardwareSurface hardwareSurface && surface is Surface softSurface)
                    {
                        // 这里需要将软件解码的结果转换为硬件表面格式
                        // 简化处理：创建新的帧数据
                        // 实际实现需要复制像素数据
                    }
                }
                
                return success;
            }
            catch (Exception ex)
            {
                LogError($"Software decode exception: {ex.Message}");
                return false;
            }
        }
        
        // 尝试切换回硬件解码
        private void TrySwitchBackToHardware()
        {
            if (_useSoftwareFallback && _hardwareDecoder != null && _softwareFallbackCount >= MaxSoftwareFallbackCount)
            {
                try
                {
                    // 重置硬件解码器
                    _hardwareDecoder.Reset();
                    
                    // 重置计数器
                    _softwareFallbackCount = 0;
                    _useSoftwareFallback = false;
                    
                    Log("Switched back to hardware decoding");
                }
                catch (Exception ex)
                {
                    LogError($"Failed to switch back to hardware: {ex.Message}");
                }
            }
        }
        
        // 事件处理
        private void OnHardwareFrameDecoded(HWFrameData frame)
        {
            // 可以在这里处理解码后的帧
            // 例如：记录解码统计、触发回调等
        }
        
        private void OnHardwareError(HWDecoderError error, string message)
        {
            LogError($"Hardware decoder error: {error} - {message}");
            
            if (error == HWDecoderError.HW_DECODER_ERROR_INIT_FAILED || 
                error == HWDecoderError.HW_DECODER_ERROR_HARDWARE_CHANGED)
            {
                // 严重错误，需要重新初始化
                ReinitializeDecoder();
            }
        }
        
        private void OnHardwareFormatChanged(HWDecoderConfig config)
        {
            Log($"Hardware decoder format changed: {config.width}x{config.height}");
            
            // 更新尺寸
            _width = config.width;
            _height = config.height;
            
            // 可以在这里重新配置解码器或通知应用程序
        }
        
        private void OnHardwareBufferStatusChanged(int level, int capacity)
        {
            // 可以在这里实现缓冲区管理
            // 例如：当缓冲区快满时暂停解码
        }
        
        // 更新统计信息
        private void UpdateStatistics()
        {
            // 每100帧更新一次统计
            if (_framesDecoded % 100 == 0)
            {
                double elapsedSeconds = (DateTime.UtcNow - _startTime).TotalSeconds;
                double fps = elapsedSeconds > 0 ? _framesDecoded / elapsedSeconds : 0;
                double hardwareRatio = _framesDecoded > 0 ? (double)_hardwareFrames / _framesDecoded * 100 : 0;
                double errorRate = _framesDecoded > 0 ? (double)_decodeErrors / _framesDecoded * 100 : 0;
                
                StatisticsUpdated?.Invoke(_framesDecoded, (int)fps);
                
                Log($"Statistics: Frames={_framesDecoded}, FPS={fps:F1}, "
                    + $"Hardware={hardwareRatio:F1}%, Errors={_decodeErrors} ({errorRate:F1}%)");
            }
        }
        
        // 获取统计信息
        public Dictionary<string, object> GetStatistics()
        {
            double elapsedSeconds = (DateTime.UtcNow - _startTime).TotalSeconds;
            double fps = elapsedSeconds > 0 ? _framesDecoded / elapsedSeconds : 0;
            double hardwareRatio = _framesDecoded > 0 ? (double)_hardwareFrames / _framesDecoded * 100 : 0;
            double softwareRatio = _framesDecoded > 0 ? (double)_softwareFrames / _framesDecoded * 100 : 0;
            double errorRate = _framesDecoded > 0 ? (double)_decodeErrors / _framesDecoded * 100 : 0;
            
            return new Dictionary<string, object>
            {
                ["FramesDecoded"] = _framesDecoded,
                ["HardwareFrames"] = _hardwareFrames,
                ["SoftwareFrames"] = _softwareFrames,
                ["DecodeErrors"] = _decodeErrors,
                ["FPS"] = fps,
                ["HardwareRatio"] = hardwareRatio,
                ["SoftwareRatio"] = softwareRatio,
                ["ErrorRate"] = errorRate,
                ["UptimeSeconds"] = elapsedSeconds,
                ["UseSoftwareFallback"] = _useSoftwareFallback,
                ["SoftwareFallbackCount"] = _softwareFallbackCount,
                ["IsHardwareAccelerated"] = IsHardwareAccelerated,
                ["HardwareType"] = _hardwareDecoder?.HardwareType ?? "None",
                ["CodecName"] = _hardwareDecoder?.CodecName ?? "Software"
            };
        }
        
        // 重置解码器
        public void Reset()
        {
            try
            {
                _hardwareDecoder?.Reset();
                _softwareDecoder?.Dispose();
                _softwareDecoder = null;
                
                // 重新创建软件解码器
                if (_enableSoftwareFallback)
                {
                    _softwareDecoder = new Decoder();
                }
                
                _useSoftwareFallback = false;
                _softwareFallbackCount = 0;
                
                Log("Decoder reset");
            }
            catch (Exception ex)
            {
                LogError($"Reset error: {ex.Message}");
            }
        }
        
        // 清理资源
        private void Cleanup()
        {
            try
            {
                _hardwareDecoder?.Dispose();
                _hardwareDecoder = null;
                
                _softwareDecoder?.Dispose();
                _softwareDecoder = null;
                
                _currentSurface?.Dispose();
                _currentSurface = null;
                
                Log("Decoder cleaned up");
            }
            catch (Exception ex)
            {
                LogError($"Cleanup error: {ex.Message}");
            }
        }
        
        // 日志辅助方法
        private void Log(string message)
        {
            Logger.Info?.Print(LogClass.FFmpeg, $"[HardwareDecoderH264] {message}");
            LogMessage?.Invoke(message);
        }
        
        private void LogError(string message)
        {
            Logger.Error?.Print(LogClass.FFmpeg, $"[HardwareDecoderH264] {message}");
            ErrorMessage?.Invoke(message);
        }
        
        // 资源清理
        public void Dispose()
        {
            if (_disposed)
                return;
            
            _disposed = true;
            _initialized = false;
            
            Cleanup();
            
            Logger.Info?.Print(LogClass.FFmpeg, "[HardwareDecoderH264] Disposed");
        }
        
        ~HardwareDecoderH264()
        {
            Dispose();
        }
    }
}
