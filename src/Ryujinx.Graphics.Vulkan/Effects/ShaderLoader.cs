// ShaderLoader.cs
using System;
using System.IO;
using System.Runtime.InteropServices;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan.Effects
{
    internal static class ShaderLoader
    {
        [DllImport("libryujinxjni", EntryPoint = "loadShaderFromAssets")]
        private static extern IntPtr LoadShaderFromAssetsNative(string shaderPath, out int length);

        [DllImport("libryujinxjni", EntryPoint = "freeShaderData")]
        private static extern void FreeShaderDataNative(IntPtr data);

        [DllImport("libryujinxjni", EntryPoint = "Java_org_ryujinx_android_NativeHelpers_initAssetManager")]
        private static extern void InitAssetManagerNative(IntPtr assetManager);

        public static byte[] LoadShaderFromAssets(string shaderPath)
        {
            try
            {
                IntPtr dataPtr = LoadShaderFromAssetsNative(shaderPath, out int length);
                if (dataPtr == IntPtr.Zero || length == 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Shader not found in assets: {shaderPath}");
                    return null;
                }

                byte[] data = new byte[length];
                Marshal.Copy(dataPtr, data, 0, length);
                FreeShaderDataNative(dataPtr);

                Logger.Info?.Print(LogClass.Gpu, $"Successfully loaded shader from assets: {shaderPath}, size: {length} bytes");
                return data;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to load shader from assets {shaderPath}: {ex.Message}");
                return null;
            }
        }

        // 可选：如果需要从Java层初始化AssetManager
        public static void InitAssetManager(IntPtr assetManager)
        {
            try
            {
                InitAssetManagerNative(assetManager);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to initialize asset manager: {ex.Message}");
            }
        }
    }
}
