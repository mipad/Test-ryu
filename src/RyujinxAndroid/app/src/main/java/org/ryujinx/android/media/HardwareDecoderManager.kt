package org.ryujinx.android.media

import android.util.Log

/**
 * 硬件解码器管理器 - 统一管理硬件解码器的创建和使用
 */
object HardwareDecoderManager {
    private const val TAG = "HardwareDecoderManager"
    
    private val decoders = mutableMapOf<String, HardwareDecoder>()
    
    /**
     * 创建硬件解码器
     */
    fun createDecoder(codecMime: String, width: Int, height: Int, callback: HardwareDecoder.DecoderCallback? = null): HardwareDecoder? {
        return try {
            Log.i(TAG, "Creating hardware decoder for: $codecMime, ${width}x$height")
            
            val decoder = HardwareDecoder()
            if (decoder.initialize(codecMime, width, height, null, callback)) {
                decoders[codecMime] = decoder
                decoder
            } else {
                Log.e(TAG, "Failed to create hardware decoder for: $codecMime")
                null
            }
        } catch (e: Exception) {
            Log.e(TAG, "Exception creating hardware decoder: ${e.message}")
            null
        }
    }
    
    /**
     * 获取硬件解码器
     */
    fun getDecoder(codecMime: String): HardwareDecoder? {
        return decoders[codecMime]
    }
    
    /**
     * 释放指定解码器
     */
    fun releaseDecoder(codecMime: String) {
        decoders[codecMime]?.release()
        decoders.remove(codecMime)
    }
    
    /**
     * 释放所有解码器
     */
    fun releaseAll() {
        decoders.values.forEach { it.release() }
        decoders.clear()
        Log.i(TAG, "All hardware decoders released")
    }
    
    /**
     * 检查是否支持硬件解码
     */
    fun isHardwareDecodingSupported(codecMime: String): Boolean {
        return try {
            HardwareDecoder().isCodecSupported(codecMime)
        } catch (e: Exception) {
            false
        }
    }
    
    /**
     * 获取支持的硬件编解码器列表
     */
    fun getSupportedHardwareCodecs(): List<String> {
        return HardwareDecoder().getSupportedCodecs()
    }
    
    /**
     * 打印硬件解码器信息
     */
    fun printHardwareDecoderInfo() {
        val supportedCodecs = getSupportedHardwareCodecs()
        Log.i(TAG, "Supported hardware codecs: $supportedCodecs")
        
        for (codec in supportedCodecs) {
            Log.i(TAG, "Hardware decoder available for: $codec")
        }
    }
}