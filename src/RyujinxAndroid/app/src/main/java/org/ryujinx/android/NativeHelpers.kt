package org.ryujinx.android

import android.view.Surface
import android.util.Log

class NativeHelpers {

    companion object {
        val instance = NativeHelpers()

        init {
            System.loadLibrary("ryujinxjni")
        }
        
        // 从 C++ 调用此方法来记录日志
        @JvmStatic
        fun logFromNative(level: Int, tag: String, message: String) {
            when (level) {
                Log.ERROR -> LogToFile.log(tag, "ERROR: $message")
                Log.WARN -> LogToFile.log(tag, "WARN: $message")
                Log.INFO -> LogToFile.log(tag, "INFO: $message")
                Log.DEBUG -> LogToFile.log(tag, "DEBUG: $message")
                else -> LogToFile.log(tag, "UNKNOWN: $message")
            }
            // 同时输出到 Android 日志
            when (level) {
                Log.ERROR -> Log.e(tag, message)
                Log.WARN -> Log.w(tag, message)
                Log.INFO -> Log.i(tag, message)
                Log.DEBUG -> Log.d(tag, message)
                else -> Log.v(tag, message)
            }
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

    // Oboe 音频相关方法
    external fun initOboeAudio()
    external fun shutdownOboeAudio()
    external fun writeOboeAudio(audioData: FloatArray, numFrames: Int)
    external fun setOboeSampleRate(sampleRate: Int)
    external fun setOboeBufferSize(bufferSize: Int)
    external fun setOboeVolume(volume: Float)
    external fun isOboeInitialized(): Boolean
    external fun getOboeBufferedFrames(): Int
}
