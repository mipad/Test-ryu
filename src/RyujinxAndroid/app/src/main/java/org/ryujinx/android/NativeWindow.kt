package org.ryujinx.android

import android.view.SurfaceView

class NativeWindow(val surface: SurfaceView) {
    var nativePointer: Long
    private val nativeHelpers: NativeHelpers = NativeHelpers.instance
    private var _swapInterval: Int = 0
    private var _scalingFactor: Float = 1.0f // 添加缩放因子属性

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

    // 添加缩放因子属性
    var scalingFactor: Float
        get() {
            return _scalingFactor
        }
        set(value) {
            _scalingFactor = value
            updateVsyncBasedOnScalingFactor() // 当缩放因子变化时更新 VSync
        }

    init {
        nativePointer = nativeHelpers.getNativeWindow(surface.holder.surface)
        swapInterval = maxOf(1, minSwapInterval)
    }

    fun requeryWindowHandle(): Long {
        nativePointer = nativeHelpers.getNativeWindow(surface.holder.surface)
        swapInterval = swapInterval
        return nativePointer
    }

    // 添加根据缩放因子更新 VSync 的方法
    private fun updateVsyncBasedOnScalingFactor() {
        if (nativePointer == -1L) return
        
        // 根据缩放因子决定是否启用 VSync
        // 如果缩放因子不等于 1.0，禁用 VSync 以获得更高帧率
        // 如果缩放因子等于 1.0，启用 VSync 以获得更流畅的体验
        val newSwapInterval = if (_scalingFactor != 1.0f) 0 else 1
        
        // 设置交换间隔
        if (nativeHelpers.setSwapInterval(nativePointer, newSwapInterval) == 0) {
            _swapInterval = newSwapInterval
        }
        
        // 记录日志
        android.util.Log.d("Ryujinx", 
                          "VSync ${if (newSwapInterval == 0) "disabled" else "enabled"} " +
                          "based on scaling factor: $_scalingFactor")
    }
}
