package org.ryujinx.android

import android.view.SurfaceView
import android.util.Log

class NativeWindow(val surface: SurfaceView) {
    var nativePointer: Long
    private val nativeHelpers: NativeHelpers = NativeHelpers.instance
    private var _swapInterval: Int = 0

    var maxSwapInterval: Int = 0
        get() {
            return if (nativePointer == -1L) 0 else nativeHelpers.getMaxSwapInterval(nativePointer)
        }

    var minSwapInterval: Int = 0
        get() {
            return if (nativePointer == -1L) 0 else nativeHelpers.getMinSwapInterval(nativePointer)
        }

    var swapInterval: Int
        get() {
            return _swapInterval
        }
        set(value) {
            if (nativePointer == -1L || nativeHelpers.setSwapInterval(nativePointer, value) == 0)
                _swapInterval = value
        }

    init {
        nativePointer = nativeHelpers.getNativeWindow(surface.holder.surface)
        swapInterval = maxOf(1, minSwapInterval)
        Log.d("NativeWindow", "NativeWindow initialized with pointer: $nativePointer, swapInterval: $swapInterval")
    }

    fun requeryWindowHandle(): Long {
        nativePointer = nativeHelpers.getNativeWindow(surface.holder.surface)
        swapInterval = swapInterval
        Log.d("NativeWindow", "NativeWindow requeried with pointer: $nativePointer")
        return nativePointer
    }

    /**
     * 释放原生窗口资源
     * 将 nativePointer 设置为 -1，避免后续使用已释放的窗口
     */
    fun release() {
        try {
            Log.d("NativeWindow", "Releasing native window, current pointer: $nativePointer")
            // 将指针设置为无效值，防止后续操作使用已释放的窗口
            nativePointer = -1L
            _swapInterval = 0
            Log.d("NativeWindow", "Native window released successfully")
        } catch (e: Exception) {
            // 释放过程中出现异常不影响主要关闭流程
            Log.e("NativeWindow", "Error releasing native window", e)
        }
    }

    /**
     * 检查原生窗口是否有效
     */
    fun isValid(): Boolean {
        return nativePointer != -1L
    }
}
