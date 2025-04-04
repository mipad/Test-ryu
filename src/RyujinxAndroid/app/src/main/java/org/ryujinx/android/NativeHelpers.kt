package org.ryujinx.android

import android.view.Surface

class NativeHelpers {

    companion object {
        const val ASPECT_RATIO_STRETCH = 0
        const val ASPECT_RATIO_16_9 = 1

        val instance = NativeHelpers()

        init {
            System.loadLibrary("ryujinxjni")
        }
    }

    // 新增接口
    external fun setAspectRatio(nativeWindow: Long, ratioMode: Int)
    external fun getCurrentAspectRatio(): Int

    // 原有接口保持不变
    external fun releaseNativeWindow(window: Long)
    external fun getCreateSurfacePtr(): Long
    external fun getNativeWindow(surface: Surface): Long
    external fun loadDriver(
        nativeLibPath: String,
        privateAppsPath: String,
        driverName: String
    ): Long
    external fun setTurboMode(enable: Boolean)
    external fun getMaxSwapInterval(nativeWindow: Long): Int
    external fun getMinSwapInterval(nativeWindow: Long): Int
    external fun setSwapInterval(nativeWindow: Long, swapInterval: Int): Int
    external fun getStringJava(ptr: Long): String
    external fun setIsInitialOrientationFlipped(isFlipped: Boolean)
}
