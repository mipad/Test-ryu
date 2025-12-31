// Ryujinx.Graphics.Nvdec.FFmpeg/AndroidJni.cs
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    internal static class AndroidJni
    {
        private const string LibRyuijnxJni = "ryujinxjni";

        [DllImport(LibRyuijnxJni, EntryPoint = "Java_org_ryujinx_android_NativeHelpers_getNativeWindow")]
        private static extern long GetNativeWindowInternal(IntPtr env, IntPtr instance, IntPtr surface);

        [DllImport(LibRyuijnxJni, EntryPoint = "Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow")]
        private static extern void ReleaseNativeWindowInternal(IntPtr env, IntPtr instance, long window);

        [DllImport(LibRyuijnxJni, EntryPoint = "Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval")]
        private static extern int GetMaxSwapIntervalInternal(IntPtr env, IntPtr instance, long native_window);

        [DllImport(LibRyuijnxJni, EntryPoint = "Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval")]
        private static extern int GetMinSwapIntervalInternal(IntPtr env, IntPtr instance, long native_window);

        [DllImport(LibRyuijnxJni, EntryPoint = "Java_org_ryujinx_android_NativeHelpers_setSwapInterval")]
        private static extern int SetSwapIntervalInternal(IntPtr env, IntPtr instance, long native_window, int swap_interval);

        private static IntPtr GetJniEnv()
        {
            // 这里需要实现获取 JNIEnv 的方法
            // 在实际应用中，应该通过 JNI 获取当前线程的 JNIEnv
            return IntPtr.Zero;
        }

        private static IntPtr GetNativeHelpersInstance()
        {
            // 这里需要获取 NativeHelpers 的实例
            // 在实际应用中，应该通过 JNI 获取 NativeHelpers 的实例
            return IntPtr.Zero;
        }

        public static long GetNativeWindow(IntPtr surface)
        {
            try
            {
                var env = GetJniEnv();
                var instance = GetNativeHelpersInstance();
                return GetNativeWindowInternal(env, instance, surface);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get native window: {ex.Message}");
                return -1;
            }
        }

        public static void ReleaseNativeWindow(long window)
        {
            try
            {
                var env = GetJniEnv();
                var instance = GetNativeHelpersInstance();
                ReleaseNativeWindowInternal(env, instance, window);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to release native window: {ex.Message}");
            }
        }

        public static int GetMaxSwapInterval(long nativeWindow)
        {
            try
            {
                var env = GetJniEnv();
                var instance = GetNativeHelpersInstance();
                return GetMaxSwapIntervalInternal(env, instance, nativeWindow);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get max swap interval: {ex.Message}");
                return 0;
            }
        }

        public static int GetMinSwapInterval(long nativeWindow)
        {
            try
            {
                var env = GetJniEnv();
                var instance = GetNativeHelpersInstance();
                return GetMinSwapIntervalInternal(env, instance, nativeWindow);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get min swap interval: {ex.Message}");
                return 0;
            }
        }

        public static int SetSwapInterval(long nativeWindow, int swapInterval)
        {
            try
            {
                var env = GetJniEnv();
                var instance = GetNativeHelpersInstance();
                return SetSwapIntervalInternal(env, instance, nativeWindow, swapInterval);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set swap interval: {ex.Message}");
                return -1;
            }
        }
    }
}
