using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    using Native;

    internal static class AndroidHardwareConfig
    {
        // Android MediaCodec 支持的编解码器
        internal static readonly HashSet<AVCodecID> SupportedCodecs = new()
        {
            AVCodecID.AV_CODEC_ID_H264,
            AVCodecID.AV_CODEC_ID_VP8,
        };

        // 检查是否支持硬件解码
        internal static bool IsHardwareDecodingSupported(AVCodecID codecId)
        {
            return SupportedCodecs.Contains(codecId);
        }

        // 获取推荐的硬件加速模式
        internal static HardwareAccelerationMode GetRecommendedAccelerationMode(AVCodecID codecId)
        {
            if (IsHardwareDecodingSupported(codecId))
            {
                return HardwareAccelerationMode.Auto;
            }
            
            return HardwareAccelerationMode.Software;
        }

        // 检查设备是否支持特定编解码器的硬件解码
        internal static bool CheckDeviceCapability(AVCodecID codecId)
        {
            try
            {
                // 这里可以添加更详细的设备能力检查
                // 例如检查 Android 版本、GPU 型号等
                
                // 简化的检查：假设 Android 5.0+ 支持基本硬件解码
                var androidVersion = GetAndroidVersion();
                if (androidVersion >= 21) // Android 5.0 Lollipop
                {
                    return true;
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static int GetAndroidVersion()
        {
            try
            {
                // 通过 Android 运行时获取版本
                using (var versionClass = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    return versionClass.GetStatic<int>("SDK_INT");
                }
            }
            catch
            {
                // 如果无法获取，返回一个保守的版本
                return 19; // Android 4.4
            }
        }
    }
}
