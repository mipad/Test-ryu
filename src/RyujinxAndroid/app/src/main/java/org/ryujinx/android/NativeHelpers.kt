package org.ryujinx.android

import android.view.Surface
import java.nio.ByteBuffer

class NativeHelpers {

    companion object {
        val instance = NativeHelpers()

        init {
            System.loadLibrary("ryujinxjni")
        }
    }

    // 原有的 JNI 方法
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

    // ========== 共享内存管理 ==========
    
    /**
     * 分配共享内存块
     * @param size 内存大小（字节）
     * @return 共享内存指针，0表示分配失败
     */
    external fun allocateSharedMemory(size: Int): Long

    /**
     * 释放共享内存块
     * @param ptr 共享内存指针
     */
    external fun freeSharedMemory(ptr: Long)

    /**
     * 创建 DirectByteBuffer
     * @param ptr 共享内存指针
     * @param size 内存大小
     * @return DirectByteBuffer 对象
     */
    external fun createDirectBuffer(ptr: Long, size: Int): ByteBuffer

    // ========== Oboe 音频渲染器多实例接口 ==========
    
    /**
     * 创建 Oboe 音频渲染器实例
     * @return 渲染器指针，0表示创建失败
     */
    external fun createOboeRenderer(): Long

    /**
     * 销毁 Oboe 音频渲染器实例
     * @param rendererPtr 渲染器指针
     */
    external fun destroyOboeRenderer(rendererPtr: Long)

    /**
     * 初始化 Oboe 音频渲染器
     * @param rendererPtr 渲染器指针
     * @param sampleRate 采样率
     * @param channelCount 声道数
     * @param sampleFormat 采样格式 (1=PCM_INT16, 2=PCM_INT24, 3=PCM_INT32, 4=PCM_FLOAT)
     * @return 是否初始化成功
     */
    external fun initOboeRenderer(rendererPtr: Long, sampleRate: Int, channelCount: Int, sampleFormat: Int): Boolean

    /**
     * 关闭 Oboe 音频渲染器
     * @param rendererPtr 渲染器指针
     */
    external fun shutdownOboeRenderer(rendererPtr: Long)

    /**
     * 写入音频数据 (short数组)
     * @param rendererPtr 渲染器指针
     * @param audioData 音频数据数组
     * @param numFrames 帧数
     * @return 是否写入成功
     */
    external fun writeOboeRendererAudio(rendererPtr: Long, audioData: ShortArray, numFrames: Int): Boolean

    /**
     * 写入原始音频数据 (byte数组)
     * @param rendererPtr 渲染器指针
     * @param audioData 原始音频数据
     * @param numFrames 帧数
     * @param sampleFormat 采样格式
     * @return 是否写入成功
     */
    external fun writeOboeRendererAudioRaw(rendererPtr: Long, audioData: ByteArray, numFrames: Int, sampleFormat: Int): Boolean

    /**
     * 使用 DirectByteBuffer 写入音频数据 (高性能版本)
     * @param rendererPtr 渲染器指针
     * @param directBuffer DirectByteBuffer
     * @param numFrames 帧数
     * @param sampleFormat 采样格式
     * @return 是否写入成功
     */
    external fun writeOboeRendererDirect(rendererPtr: Long, directBuffer: ByteBuffer, numFrames: Int, sampleFormat: Int): Boolean

    /**
     * 设置音频渲染器音量
     * @param rendererPtr 渲染器指针
     * @param volume 音量 (0.0 - 1.0)
     */
    external fun setOboeRendererVolume(rendererPtr: Long, volume: Float)

    /**
     * 检查音频渲染器是否已初始化
     * @param rendererPtr 渲染器指针
     * @return 是否已初始化
     */
    external fun isOboeRendererInitialized(rendererPtr: Long): Boolean

    /**
     * 检查音频渲染器是否正在播放
     * @param rendererPtr 渲染器指针
     * @return 是否正在播放
     */
    external fun isOboeRendererPlaying(rendererPtr: Long): Boolean

    /**
     * 获取音频渲染器缓冲帧数
     * @param rendererPtr 渲染器指针
     * @return 缓冲帧数
     */
    external fun getOboeRendererBufferedFrames(rendererPtr: Long): Int

    /**
     * 重置音频渲染器
     * @param rendererPtr 渲染器指针
     */
    external fun resetOboeRenderer(rendererPtr: Long)

    // ========== 设备信息查询 ==========
    
    /**
     * 获取 Android 设备型号
     * @return 设备型号字符串
     */
    external fun getAndroidDeviceModel(): String

    /**
     * 获取 Android 设备品牌
     * @return 设备品牌字符串
     */
    external fun getAndroidDeviceBrand(): String

    // ========== 工具方法 ==========
    
    /**
     * 将采样格式枚举转换为整数格式
     */
    fun sampleFormatToInt(sampleFormat: Int): Int {
        return when (sampleFormat) {
            1 -> 1 // PCM_INT16
            2 -> 2 // PCM_INT24  
            3 -> 3 // PCM_INT32
            4 -> 4 // PCM_FLOAT
            else -> 1 // 默认 PCM_INT16
        }
    }

    /**
     * 计算最佳缓冲区大小
     * @param sampleRate 采样率
     * @param channelCount 声道数
     * @param bytesPerSample 每个采样的字节数
     * @param bufferMs 缓冲区时长（毫秒）
     * @return 缓冲区大小（字节）
     */
    fun calculateOptimalBufferSize(
        sampleRate: Int,
        channelCount: Int,
        bytesPerSample: Int,
        bufferMs: Int = 100
    ): Int {
        val framesPerBuffer = (sampleRate * bufferMs) / 1000
        return framesPerBuffer * channelCount * bytesPerSample
    }

    /**
     * 创建音频共享内存和 DirectByteBuffer
     * @param sampleRate 采样率
     * @param channelCount 声道数
     * @param sampleFormat 采样格式
     * @param bufferMs 缓冲区时长（毫秒）
     * @return SharedAudioBuffer 对象，包含内存指针和 DirectByteBuffer
     */
    fun createSharedAudioBuffer(
        sampleRate: Int,
        channelCount: Int,
        sampleFormat: Int,
        bufferMs: Int = 100
    ): SharedAudioBuffer? {
        val bytesPerSample = when (sampleFormat) {
            1 -> 2 // PCM_INT16
            2 -> 3 // PCM_INT24
            3 -> 4 // PCM_INT32
            4 -> 4 // PCM_FLOAT
            else -> 2
        }

        val bufferSize = calculateOptimalBufferSize(sampleRate, channelCount, bytesPerSample, bufferMs)
        val memoryPtr = allocateSharedMemory(bufferSize)
        
        if (memoryPtr == 0L) {
            return null
        }

        val directBuffer = createDirectBuffer(memoryPtr, bufferSize)
        if (directBuffer == null) {
            freeSharedMemory(memoryPtr)
            return null
        }

        return SharedAudioBuffer(memoryPtr, directBuffer, bufferSize, sampleFormat)
    }

    /**
     * 释放音频共享内存
     * @param sharedBuffer SharedAudioBuffer 对象
     */
    fun releaseSharedAudioBuffer(sharedBuffer: SharedAudioBuffer?) {
        if (sharedBuffer != null) {
            freeSharedMemory(sharedBuffer.memoryPtr)
        }
    }

    /**
     * 音频共享内存数据类
     */
    data class SharedAudioBuffer(
        val memoryPtr: Long,
        val directBuffer: ByteBuffer,
        val bufferSize: Int,
        val sampleFormat: Int
    ) {
        /**
         * 获取每个采样的字节数
         */
        val bytesPerSample: Int
            get() = when (sampleFormat) {
                1 -> 2 // PCM_INT16
                2 -> 3 // PCM_INT24
                3 -> 4 // PCM_INT32
                4 -> 4 // PCM_FLOAT
                else -> 2
            }

        /**
         * 计算帧数
         * @param dataSize 数据大小（字节）
         * @param channelCount 声道数
         * @return 帧数
         */
        fun calculateFrames(dataSize: Int, channelCount: Int): Int {
            return dataSize / (bytesPerSample * channelCount)
        }

        /**
         * 检查缓冲区是否足够容纳指定数据
         * @param dataSize 数据大小（字节）
         * @return 是否足够
         */
        fun isSufficient(dataSize: Int): Boolean {
            return dataSize <= bufferSize
        }
    }
}