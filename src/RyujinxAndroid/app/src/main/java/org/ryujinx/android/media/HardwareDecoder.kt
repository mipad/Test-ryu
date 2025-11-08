package org.ryujinx.android.media

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

/**
 * 硬件解码器类 - 使用Android MediaCodec进行硬件加速解码
 */
class HardwareDecoder {
    companion object {
        private const val TAG = "HardwareDecoder"
        
        // 编解码器类型
        const val CODEC_H264 = "video/avc"
        const val CODEC_H265 = "video/hevc"
        const val CODEC_VP8 = "video/x-vnd.on2.vp8"
        const val CODEC_VP9 = "video/x-vnd.on2.vp9"
        const val CODEC_AV1 = "video/av01"
        
        init {
            System.loadLibrary("ryujinxjni")
        }
    }
    
    private var mediaCodec: MediaCodec? = null
    private var handler: Handler = Handler(Looper.getMainLooper())
    private var isInitialized = false
    private var surface: Surface? = null
    
    // Native回调接口
    interface DecoderCallback {
        fun onFrameDecoded(data: ByteArray, width: Int, height: Int, format: Int)
        fun onDecoderError(error: String)
        fun onDecoderInitialized()
    }
    
    private var callback: DecoderCallback? = null
    
    /**
     * 初始化硬件解码器
     */
    fun initialize(codecMime: String, width: Int, height: Int, surface: Surface? = null, callback: DecoderCallback? = null): Boolean {
        return try {
            Log.i(TAG, "Initializing hardware decoder: $codecMime, ${width}x$height")
            
            this.surface = surface
            this.callback = callback
            
            // 创建MediaCodec
            mediaCodec = MediaCodec.createDecoderByType(codecMime)
            
            // 配置MediaFormat
            val mediaFormat = MediaFormat.createVideoFormat(codecMime, width, height)
            
            // 设置解码器参数
            configureMediaFormat(mediaFormat, codecMime)
            
            // 配置MediaCodec
            mediaCodec?.configure(mediaFormat, surface, null, 0)
            
            // 启动解码器
            mediaCodec?.start()
            
            isInitialized = true
            Log.i(TAG, "Hardware decoder initialized successfully")
            
            // 回调初始化完成
            callback?.onDecoderInitialized()
            
            true
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize hardware decoder: ${e.message}")
            callback?.onDecoderError("Initialization failed: ${e.message}")
            release()
            false
        }
    }
    
    /**
     * 配置MediaFormat参数
     */
    private fun configureMediaFormat(format: MediaFormat, codecMime: String) {
        // 使用常量值而不是 MediaCodecInfo 引用
        when (codecMime) {
            CODEC_H264 -> {
                // H.264特定配置 - 使用常量值
                format.setInteger(MediaFormat.KEY_PROFILE, 8) // AVCProfileHigh = 8
                format.setInteger(MediaFormat.KEY_LEVEL, 512) // AVCLevel52 = 512
            }
            CODEC_H265 -> {
                // H.265特定配置 - 使用常量值
                format.setInteger(MediaFormat.KEY_PROFILE, 1) // HEVCProfileMain = 1
                format.setInteger(MediaFormat.KEY_LEVEL, 32768) // HEVCHighTierLevel51 = 32768
            }
        }
        
        // 通用配置
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 1920 * 1080 * 3) // 最大输入大小
        format.setInteger(MediaFormat.KEY_FRAME_RATE, 60) // 帧率
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1) // I帧间隔
        format.setInteger(MediaFormat.KEY_BIT_RATE, 8000000) // 比特率
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT, 2135033992) // COLOR_FormatYUV420Flexible = 2135033992
    }
    
    /**
     * 解码视频帧
     */
    fun decodeFrame(frameData: ByteArray, frameSize: Int, presentationTimeUs: Long = System.nanoTime() / 1000) {
        if (!isInitialized) {
            Log.w(TAG, "Decoder not initialized")
            return
        }
        
        handler.post {
            try {
                val codec = mediaCodec ?: return@post
                
                // 获取输入缓冲区
                val inputBufferIndex = codec.dequeueInputBuffer(10000) // 10ms超时
                if (inputBufferIndex >= 0) {
                    val inputBuffer = codec.getInputBuffer(inputBufferIndex)
                    inputBuffer?.clear()
                    inputBuffer?.put(frameData, 0, frameSize)
                    
                    // 提交到解码器
                    codec.queueInputBuffer(inputBufferIndex, 0, frameSize, presentationTimeUs, 0)
                    Log.d(TAG, "Queued input buffer: $inputBufferIndex, size: $frameSize")
                } else {
                    Log.w(TAG, "No input buffer available")
                }
                
                // 处理输出
                processOutput()
                
            } catch (e: Exception) {
                Log.e(TAG, "Error decoding frame: ${e.message}")
                callback?.onDecoderError("Decode error: ${e.message}")
            }
        }
    }
    
    /**
     * 处理解码输出
     */
    private fun processOutput() {
        val codec = mediaCodec ?: return
        
        val bufferInfo = MediaCodec.BufferInfo()
        var outputBufferIndex = codec.dequeueOutputBuffer(bufferInfo, 10000) // 10ms超时
        
        while (outputBufferIndex >= 0) {
            when (outputBufferIndex) {
                MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    val newFormat = codec.outputFormat
                    Log.i(TAG, "Output format changed: $newFormat")
                }
                MediaCodec.INFO_TRY_AGAIN_LATER -> {
                    // 没有输出可用
                }
                else -> {
                    val outputBuffer = codec.getOutputBuffer(outputBufferIndex)
                    if (outputBuffer != null && bufferInfo.size > 0) {
                        // 处理解码后的数据
                        handleDecodedData(outputBuffer, bufferInfo)
                    }
                    
                    // 释放输出缓冲区
                    codec.releaseOutputBuffer(outputBufferIndex, true)
                    
                    Log.d(TAG, "Released output buffer: $outputBufferIndex, size: ${bufferInfo.size}")
                }
            }
            
            outputBufferIndex = codec.dequeueOutputBuffer(bufferInfo, 0)
        }
    }
    
    /**
     * 处理解码后的数据
     */
    private fun handleDecodedData(outputBuffer: ByteBuffer, bufferInfo: MediaCodec.BufferInfo) {
        // 如果设置了Surface，数据会自动渲染到Surface
        // 如果没有Surface，我们可以获取原始YUV数据
        if (surface == null) {
            val data = ByteArray(bufferInfo.size)
            outputBuffer.get(data)
            
            // 回调解码数据
            callback?.onFrameDecoded(data, 0, 0, 0)
        }
    }
    
    /**
     * 刷新解码器
     */
    fun flush() {
        try {
            mediaCodec?.flush()
            Log.i(TAG, "Decoder flushed")
        } catch (e: Exception) {
            Log.e(TAG, "Error flushing decoder: ${e.message}")
        }
    }
    
    /**
     * 释放解码器资源
     */
    fun release() {
        try {
            mediaCodec?.stop()
            mediaCodec?.release()
            mediaCodec = null
            isInitialized = false
            Log.i(TAG, "Hardware decoder released")
        } catch (e: Exception) {
            Log.e(TAG, "Error releasing decoder: ${e.message}")
        }
    }
    
    /**
     * 检查是否支持指定的编解码器
     */
    fun isCodecSupported(codecMime: String): Boolean {
        return try {
            MediaCodec.createDecoderByType(codecMime)?.let {
                it.release()
                true
            } ?: false
        } catch (e: Exception) {
            Log.w(TAG, "Codec not supported: $codecMime - ${e.message}")
            false
        }
    }
    
    /**
     * 获取支持的编解码器列表
     */
    fun getSupportedCodecs(): List<String> {
        val supportedCodecs = mutableListOf<String>()
        val codecList = arrayOf(CODEC_H264, CODEC_H265, CODEC_VP8, CODEC_VP9, CODEC_AV1)
        
        for (codec in codecList) {
            if (isCodecSupported(codec)) {
                supportedCodecs.add(codec)
            }
        }
        
        return supportedCodecs
    }
}