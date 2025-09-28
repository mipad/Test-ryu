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

    // Oboe 音频函数
    external fun initOboeAudio()
    external fun shutdownOboeAudio()
    external fun writeOboeAudio(audioData: FloatArray, numFrames: Int)
    external fun setOboeSampleRate(sampleRate: Int)
    external fun setOboeBufferSize(bufferSize: Int)
    external fun setOboeVolume(volume: Float)
    external fun isOboeInitialized(): Boolean
    external fun getOboeBufferedFrames(): Int
    
    // 新增：初始化AssetManager
    external fun initAssetManager(assetManager: Any)

    // 新增：从assets加载着色器
    external fun loadShaderFromAssets(shaderPath: String): ByteArray?
}
