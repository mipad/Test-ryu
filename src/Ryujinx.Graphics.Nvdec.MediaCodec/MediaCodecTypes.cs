using System;
using System.Collections.Concurrent;

namespace Ryujinx.Graphics.Nvdec.MediaCodec
{
    // H.264 参数集
    public class H264ParameterSets
    {
        public byte[] Sps { get; set; }
        public byte[] Pps { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ProfileIdc { get; set; }
        public int LevelIdc { get; set; }
        
        public bool IsValid => Sps != null && Sps.Length > 0 && Pps != null && Pps.Length > 0;
        
        public void ParseSps(byte[] spsData)
        {
            if (spsData == null || spsData.Length < 8)
                return;
                
            Sps = spsData;
            
            // 简化的 SPS 解析 - 实际项目中应该完整解析
            try
            {
                // SPS 的第 1-3 个字节是 NALU 类型 (0x67) 和参数
                // 这里简单地从固定位置解析宽度和高度
                if (spsData.Length > 10)
                {
                    // 实际解析应该更复杂，这里只是示例
                    Width = (spsData[6] << 8) | spsData[7];
                    Height = (spsData[8] << 8) | spsData[9];
                    
                    ProfileIdc = spsData[1];
                    LevelIdc = spsData[3];
                }
            }
            catch
            {
                // 解析失败时使用默认值
            }
        }
        
        public void ParsePps(byte[] ppsData)
        {
            if (ppsData == null || ppsData.Length < 1)
                return;
                
            Pps = ppsData;
        }
        
        public byte[] CreateCsdData()
        {
            // 创建 CSD (Codec Specific Data) 用于配置 MediaCodec
            if (!IsValid)
                return null;
                
            // CSD-0 是 SPS，CSD-1 是 PPS
            // 实际格式：以 0x00 0x00 0x00 0x01 开头
            return Sps;
        }
    }

    // 解码帧
    public class DecodedFrame
    {
        public byte[] Data { get; set; }
        public long Timestamp { get; set; }
        public int Flags { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ColorFormat { get; set; }
        public byte[] YuvData { get; set; }
        
        public bool IsKeyFrame => (Flags & 0x1) != 0;
        public bool IsEos => (Flags & 0x4) != 0;
        
        public override string ToString()
        {
            return $"Frame[{Width}x{Height}], Timestamp:{Timestamp}, KeyFrame:{IsKeyFrame}, Size:{Data?.Length ?? 0}";
        }
    }

    // 解码配置
    public class DecoderConfig
    {
        public string MimeType { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int FrameRate { get; set; } = 30;
        public int ColorFormat { get; set; }
        public int BitrateMode { get; set; }
        public H264ParameterSets ParameterSets { get; set; }
        
        public bool IsValid => 
            !string.IsNullOrEmpty(MimeType) && 
            Width > 0 && Height > 0 &&
            ParameterSets?.IsValid == true;
    }

    // 帧队列包装类
    public class FrameQueue<T>
    {
        private readonly BlockingCollection<T> _queue;
        
        public FrameQueue(int capacity = 4)
        {
            _queue = new BlockingCollection<T>(new ConcurrentQueue<T>(), capacity);
        }
        
        public void Enqueue(T item)
        {
            try
            {
                _queue.TryAdd(item, TimeSpan.FromMilliseconds(100));
            }
            catch
            {
                // 添加失败，队列可能已满
            }
        }
        
        public bool TryDequeue(out T item, int timeoutMs = 100)
        {
            return _queue.TryTake(out item, timeoutMs);
        }
        
        public void Clear()
        {
            while (_queue.TryTake(out _))
            {
                // 清空队列
            }
        }
        
        public int Count => _queue.Count;
        
        public void Dispose()
        {
            _queue.Dispose();
        }
    }

    // 错误处理类
    public static class ErrorHandler
    {
        public static void LogError(string message, Exception ex = null)
        {
            Console.WriteLine($"[MediaCodec Error] {message}");
            if (ex != null)
            {
                Console.WriteLine($"Exception: {ex.Message}");
                Console.WriteLine($"StackTrace: {ex.StackTrace}");
            }
        }
        
        public static void LogWarning(string message)
        {
            Console.WriteLine($"[MediaCodec Warning] {message}");
        }
        
        public static void LogInfo(string message)
        {
            Console.WriteLine($"[MediaCodec Info] {message}");
        }
    }
}
