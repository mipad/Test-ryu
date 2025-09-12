package org.ryujinx.android

import android.view.SurfaceView
import android.util.Log

class NativeWindow(val surface: SurfaceView) {
    var nativePointer: Long
    private val nativeHelpers: NativeHelpers = NativeHelpers.instance
    private var _swapInterval: Int = 0
    private var _scalingFactor: Float = 1.0f
    private var _lastScalingFactor: Float = 1.0f // 添加上次的缩放因子用于比较

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
            _lastScalingFactor = _scalingFactor
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
        
        // 无论缩放因子是多少，都强制切换到开启 VSync 状态
        // 这样帧率变化才能生效
        val newSwapInterval = 1 // 总是设置为 1（开启 VSync）
        
        // 设置交换间隔
        if (nativeHelpers.setSwapInterval(nativePointer, newSwapInterval) == 0) {
            _swapInterval = newSwapInterval
        }
        
        // 记录日志
        Log.d("Ryujinx", 
             "VSync enabled (swapInterval = $newSwapInterval) " +
             "based on scaling factor change: $_lastScalingFactor -> $_scalingFactor")
        
        // 如果需要，可以在这里添加额外的逻辑来处理不同的缩放因子
        when {
            _scalingFactor < 1.0f -> {
                Log.d("Ryujinx", "Low scaling factor ($_scalingFactor) - reducing frame rate")
                // 可以在这里添加降低帧率的逻辑
            }
            _scalingFactor > 1.0f -> {
                Log.d("Ryujinx", "High scaling factor ($_scalingFactor) - increasing frame rate")
                // 可以在这里添加提高帧率的逻辑
            }
            else -> {
                Log.d("Ryujinx", "Normal scaling factor (1.0) - using default frame rate")
                // 可以在这里添加默认帧率的逻辑
            }
        }
    }
    
    // 添加一个方法来强制切换 VSync 状态
    fun forceVsyncToggle() {
        if (nativePointer == -1L) return
        
        // 先关闭 VSync
        if (nativeHelpers.setSwapInterval(nativePointer, 0) == 0) {
            _swapInterval = 0
            Log.d("Ryujinx", "VSync disabled")
            
            // 短暂延迟后重新开启 VSync
            android.os.Handler().postDelayed({
                if (nativeHelpers.setSwapInterval(nativePointer, 1) == 0) {
                    _swapInterval = 1
                    Log.d("Ryujinx", "VSync re-enabled after toggle")
                }
            }, 100) // 100ms 延迟
        }
    }
}
