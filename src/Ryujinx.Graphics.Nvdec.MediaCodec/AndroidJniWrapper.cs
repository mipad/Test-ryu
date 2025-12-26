using System;

namespace Ryujinx.Graphics.Nvdec.MediaCodec
{
    public static class AndroidJniWrapper
    {
        public static class MediaCodec
        {
            public static AndroidJavaObject CreateDecoderByType(string mimeType)
            {
                try
                {
                    return new AndroidJavaObject("android.media.MediaCodec", mimeType);
                }
                catch (Exception ex)
                {
                    ErrorHandler.LogError($"创建 MediaCodec 失败: {mimeType}", ex);
                    return null;
                }
            }
            
            public static bool IsCodecSupported(string mimeType)
            {
                try
                {
                    // 实际应该通过 JNI 调用 MediaCodecList
                    return true; // 简化实现
                }
                catch
                {
                    return false;
                }
            }
            
            public static string GetBestCodecForMime(string mimeType)
            {
                // 实际应该遍历 MediaCodecList 并选择最佳编解码器
                return "OMX.google.h264.decoder"; // 返回软件解码器作为默认
            }
        }
        
        public static class MediaFormat
        {
            public static AndroidJavaObject CreateVideoFormat(string mimeType, int width, int height)
            {
                try
                {
                    var mediaFormat = new AndroidJavaObject("android.media.MediaFormat");
                    mediaFormat.Call("setString", "mime", mimeType);
                    mediaFormat.Call("setInteger", "width", width);
                    mediaFormat.Call("setInteger", "height", height);
                    return mediaFormat;
                }
                catch (Exception ex)
                {
                    ErrorHandler.LogError($"创建 MediaFormat 失败", ex);
                    return null;
                }
            }
        }
        
        public static class SurfaceTexture
        {
            public static AndroidJavaObject Create(int textureId)
            {
                try
                {
                    return new AndroidJavaObject("android.graphics.SurfaceTexture", textureId);
                }
                catch (Exception ex)
                {
                    ErrorHandler.LogError($"创建 SurfaceTexture 失败", ex);
                    return null;
                }
            }
        }
        
        public static class Surface
        {
            public static AndroidJavaObject Create(AndroidJavaObject surfaceTexture)
            {
                try
                {
                    return new AndroidJavaObject("android.view.Surface", surfaceTexture);
                }
                catch (Exception ex)
                {
                    ErrorHandler.LogError($"创建 Surface 失败", ex);
                    return null;
                }
            }
        }
    }
}
