// ShaderLoader.cs
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan.Effects
{
    internal static class ShaderLoader
    {
        [DllImport("libryujinxjni", EntryPoint = "loadShaderFromAssets")]
        private static extern IntPtr LoadShaderFromAssetsNative(string shaderPath, out int length);

        [DllImport("libryujinxjni", EntryPoint = "freeShaderData")]
        private static extern void FreeShaderDataNative(IntPtr data);

        public static byte[] LoadShaderFromAssets(string shaderPath)
        {
            try
            {
                IntPtr dataPtr = LoadShaderFromAssetsNative(shaderPath, out int length);
                if (dataPtr == IntPtr.Zero || length == 0)
                {
                    return null;
                }

                byte[] data = new byte[length];
                Marshal.Copy(dataPtr, data, 0, length);
                FreeShaderDataNative(dataPtr);

                return data;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Failed to load shader from assets {shaderPath}: {ex.Message}");
                return null;
            }
        }
    }
}
