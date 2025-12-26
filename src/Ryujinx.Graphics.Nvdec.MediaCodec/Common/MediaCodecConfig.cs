using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Graphics.Nvdec.MediaCodec.Common
{
    public static class MediaCodecConfig
    {
        public static class MimeTypes
        {
            public const string H264 = "video/avc";
            public const string VP8 = "video/x-vnd.on2.vp8";
            public const string VP9 = "video/x-vnd.on2.vp9";
            public const string HEVC = "video/hevc";
        }
        
        public static class ColorFormats
        {
            public const int YUV420Planar = 0x13;
            public const int YUV420SemiPlanar = 0x15;
            public const int YUV420PackedSemiPlanar = 0x27;
            public const int YUV420Flexible = 0x7F420888;
        }
        
        public static class Keys
        {
            public const string Width = "width";
            public const string Height = "height";
            public const string Mime = "mime";
            public const string FrameRate = "frame-rate";
            public const string IFrameInterval = "i-frame-interval";
            public const string ColorFormat = "color-format";
            public const string BitrateMode = "bitrate-mode";
            public const string Priority = "priority";
            public const string Profile = "profile";
            public const string Level = "level";
            public const string Csd0 = "csd-0"; // Codec Specific Data
            public const string Csd1 = "csd-1";
            public const string Csd2 = "csd-2";
        }
        
        public static class Values
        {
            public const int BitrateModeCQ = 0;  // Constant Quality
            public const int BitrateModeVBR = 1; // Variable Bitrate
            public const int BitrateModeCBR = 2; // Constant Bitrate
        }
        
        public static Dictionary<string, object> CreateDefaultConfig(
            string mimeType, int width, int height)
        {
            return new Dictionary<string, object>
            {
                [Keys.Mime] = mimeType,
                [Keys.Width] = width,
                [Keys.Height] = height,
                [Keys.FrameRate] = 30,
                [Keys.IFrameInterval] = 1,
                [Keys.ColorFormat] = ColorFormats.YUV420SemiPlanar,
                [Keys.BitrateMode] = Values.BitrateModeVBR,
                [Keys.Priority] = 0
            };
        }
        
        public static bool IsCodecSupported(string mimeType)
        {
            try
            {
                var codecs = AndroidJniWrapper.MediaCodec.GetCodecsList();
                foreach (var codec in codecs)
                {
                    string name = codec.Call<string>("getName");
                    string[] types = codec.Call<string[]>("getSupportedTypes");
                    
                    if (types.Contains(mimeType))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 忽略异常
            }
            
            return false;
        }
        
        public static string GetBestSupportedCodec(string mimeType)
        {
            try
            {
                var codecs = AndroidJniWrapper.MediaCodec.GetCodecsList();
                string bestCodec = null;
                
                foreach (var codec in codecs)
                {
                    string name = codec.Call<string>("getName");
                    string[] types = codec.Call<string[]>("getSupportedTypes");
                    
                    if (types.Contains(mimeType))
                    {
                        // 优先选择硬件解码器
                        if (name.Contains("omx") || name.Contains("c2"))
                        {
                            bestCodec = name;
                            if (IsHardwareCodec(name))
                            {
                                return name; // 立即返回硬件解码器
                            }
                        }
                    }
                }
                
                return bestCodec;
            }
            catch
            {
                return null;
            }
        }
        
        private static bool IsHardwareCodec(string name)
        {
            // 判断是否为硬件解码器
            string lowerName = name.ToLower();
            return lowerName.Contains("qcom") ||   // 高通
                   lowerName.Contains("exynos") || // 三星
                   lowerName.Contains("mtk") ||    // 联发科
                   lowerName.Contains("hi") ||     // 海思
                   lowerName.Contains("nvidia") || // NVIDIA
                   lowerName.Contains("intel") ||  // Intel
                   lowerName.Contains("amd");      // AMD
        }
    }
}
