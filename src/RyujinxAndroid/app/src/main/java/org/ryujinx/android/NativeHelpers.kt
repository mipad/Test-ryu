package org.ryujinx.android

import android.view.Surface

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

    // =============== Oboe Audio JNI 方法 ===============
    external fun initOboeAudio(sampleRate: Int, channelCount: Int): Boolean
    external fun shutdownOboeAudio()
    external fun writeOboeAudio(audioData: ShortArray, numFrames: Int): Boolean
    external fun setOboeVolume(volume: Float)
    external fun isOboeInitialized(): Boolean
    external fun isOboePlaying(): Boolean
    external fun getOboeBufferedFrames(): Int
    external fun resetOboeAudio()

    // =============== 设备信息 JNI 方法 ===============
    external fun getAndroidDeviceModel(): String
    external fun getAndroidDeviceBrand(): String

    // =============== FFmpeg 硬件解码器 JNI 方法 ===============

    /**
     * 初始化 FFmpeg 硬件解码器
     */
    external fun initFFmpegHardwareDecoder()

    /**
     * 清理 FFmpeg 硬件解码器资源
     */
    external fun cleanupFFmpegHardwareDecoder()

    /**
     * 检查是否支持指定的硬件解码器类型
     */
    external fun isHardwareDecoderSupported(decoderType: String): Boolean

    /**
     * 获取指定 codec 的硬件解码器名称
     */
    external fun getHardwareDecoderName(codecName: String): String

    /**
     * 检查硬件解码器是否可用
     */
    external fun isHardwareDecoderAvailable(codecName: String): Boolean

    /**
     * 获取硬件解码器的像素格式
     */
    external fun getHardwarePixelFormat(decoderName: String): Int

    /**
     * 获取支持的硬件解码器列表
     */
    external fun getSupportedHardwareDecoders(): Array<String>

    /**
     * 获取支持的硬件设备类型列表
     */
    external fun getHardwareDeviceTypes(): Array<String>

    /**
     * 初始化硬件设备上下文
     */
    external fun initHardwareDeviceContext(deviceType: String): Long

    /**
     * 释放硬件设备上下文
     */
    external fun freeHardwareDeviceContext(deviceCtxPtr: Long)

    /**
     * 创建硬件解码器上下文
     */
    external fun createHardwareDecoderContext(codecName: String): Long

    /**
     * 解码视频帧
     * @param contextId 解码器上下文ID
     * @param inputData 输入数据
     * @param inputSize 输入数据大小
     * @param frameInfo 返回帧信息数组 [width, height, format, linesize0, linesize1, linesize2]
     * @param planeData 返回平面数据数组 [Y平面, U平面, V平面]
     * @return 解码结果 (0=成功, 负数=错误码)
     */
    external fun decodeVideoFrame(
        contextId: Long,
        inputData: ByteArray,
        inputSize: Int,
        frameInfo: IntArray,
        planeData: Array<ByteArray>
    ): Int

    /**
     * 刷新解码器
     */
    external fun flushDecoder(contextId: Long)

    /**
     * 销毁硬件解码器
     */
    external fun destroyHardwareDecoder(contextId: Long)

    /**
     * 获取 FFmpeg 版本信息
     */
    external fun getFFmpegVersion(): String

    /**
     * 检查硬件解码是否支持
     */
    external fun isHardwareDecodingSupported(): Boolean

    // =============== 便捷方法 ===============

    /**
     * 检查 MediaCodec 是否可用
     */
    fun isMediaCodecSupported(): Boolean {
        return isHardwareDecoderSupported("mediacodec")
    }

    /**
     * 创建硬件解码器实例
     */
    fun createHardwareDecoder(codecName: String): HardwareDecoderInstance? {
        val contextId = createHardwareDecoderContext(codecName)
        return if (contextId != 0L) {
            HardwareDecoderInstance(contextId, codecName)
        } else {
            null
        }
    }

    /**
     * 硬件解码器实例类
     */
    class HardwareDecoderInstance(
        private val contextId: Long,
        private val codecName: String
    ) {
        private var initialized = true

        /**
         * 解码视频帧
         */
        fun decodeFrame(inputData: ByteArray, frameInfo: IntArray, planeData: Array<ByteArray>): Int {
            if (!initialized) return -1
            return NativeHelpers.instance.decodeVideoFrame(contextId, inputData, inputData.size, frameInfo, planeData)
        }

        /**
         * 刷新解码器
         */
        fun flush() {
            if (initialized) {
                NativeHelpers.instance.flushDecoder(contextId)
            }
        }

        /**
         * 释放资源
         */
        fun release() {
            if (initialized) {
                NativeHelpers.instance.destroyHardwareDecoder(contextId)
                initialized = false
            }
        }

        /**
         * 获取上下文ID
         */
        fun getContextId(): Long = contextId

        /**
         * 获取编解码器名称
         */
        fun getCodecName(): String = codecName

        /**
         * 检查是否已初始化
         */
        fun isInitialized(): Boolean = initialized

        protected fun finalize() {
            release()
        }
    }

    /**
     * 帧信息数据类
     */
    data class FrameInfo(
        val width: Int,
        val height: Int,
        val format: Int,
        val linesize0: Int,
        val linesize1: Int,
        val linesize2: Int
    ) {
        companion object {
            fun fromArray(info: IntArray): FrameInfo? {
                return if (info.size >= 6) {
                    FrameInfo(info[0], info[1], info[2], info[3], info[4], info[5])
                } else {
                    null
                }
            }
        }

        override fun toString(): String {
            return "FrameInfo(width=$width, height=$height, format=$format, " +
                   "linesize=[$linesize0, $linesize1, $linesize2])"
        }
    }

    /**
     * 获取硬件解码器状态信息
     */
    fun getHardwareDecoderStatus(): String {
        val status = StringBuilder()
        
        status.append("FFmpeg Version: ").append(getFFmpegVersion()).append("\n")
        status.append("MediaCodec Supported: ").append(isMediaCodecSupported()).append("\n")
        
        val supportedDecoders = getSupportedHardwareDecoders()
        status.append("Supported Hardware Decoders: ").append(supportedDecoders.size).append("\n")
        for (decoder in supportedDecoders) {
            status.append("  - ").append(decoder).append("\n")
        }
        
        val deviceTypes = getHardwareDeviceTypes()
        status.append("Available Hardware Device Types: ").append(deviceTypes.size).append("\n")
        for (deviceType in deviceTypes) {
            status.append("  - ").append(deviceType).append("\n")
        }
        
        return status.toString()
    }
}