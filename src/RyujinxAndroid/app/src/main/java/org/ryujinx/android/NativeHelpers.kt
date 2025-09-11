package org.ryujinx.android

import android.view.Surface
import android.content.Context

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

    // 音频相关方法
    external fun initOboeAudio()
    external fun shutdownOboeAudio()
    external fun writeOboeAudio(audioData: FloatArray, numFrames: Int, inputChannels: Int, inputSampleRate: Int)
    external fun setOboeBufferSize(bufferSize: Int)
    external fun setOboeVolume(volume: Float)
    external fun setOboeNoiseShapingEnabled(enabled: Boolean)
    external fun setOboeChannelCount(channelCount: Int)
    external fun isOboeInitialized(): Boolean
    external fun getOboeBufferedFrames(): Int
    external fun getAndroidDeviceModel(): String
    external fun getAndroidDeviceBrand(): String
    
    // 新增：获取音频设备信息
    external fun getAudioDevices(context: Context): String
}
