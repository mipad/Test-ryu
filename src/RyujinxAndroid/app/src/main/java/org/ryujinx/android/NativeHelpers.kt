package org.ryujinx.android

import android.view.Surface

class NativeHelpers {

    companion object {
        val instance = NativeHelpers()

        init {
            System.loadLibrary("ryujinxjni")
        }
    }

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
    
    // 新增：FFmpeg JNI 支持
    external fun setupFFmpegJNI()
    
    // 新增：硬件解码器检测
    external fun isFFmpegHardwareDecoderAvailable(codecName: String): Boolean
    
    // 新增：Oboe 音频支持
    external fun initOboeAudio(sampleRate: Int, channelCount: Int): Boolean
    external fun shutdownOboeAudio()
    external fun writeOboeAudio(audioData: ShortArray, numFrames: Int): Boolean
    external fun setOboeVolume(volume: Float)
    external fun isOboeInitialized(): Boolean
    external fun isOboePlaying(): Boolean
    external fun getOboeBufferedFrames(): Int
    external fun resetOboeAudio()
    
    // 新增：设备信息
    external fun getAndroidDeviceModel(): String
    external fun getAndroidDeviceBrand(): String
}