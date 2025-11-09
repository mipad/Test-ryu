package org.ryujinx.android;

import java.nio.ByteBuffer;

public class FFmpegHardwareDecoder {
    
    static {
        System.loadLibrary("ffmpeg_hw_jni");
    }
    
    // 硬件解码器类型
    public static final String HW_TYPE_MEDIACODEC = "mediacodec";
    public static final String HW_TYPE_VIDEOTOOLBOX = "videotoolbox";
    public static final String HW_TYPE_VAAPI = "vaapi";
    public static final String HW_TYPE_CUDA = "cuda";
    public static final String HW_TYPE_VULKAN = "vulkan";
    
    // 像素格式常量 (与 FFmpeg 保持一致)
    public static final int PIX_FMT_NONE = -1;
    public static final int PIX_FMT_YUV420P = 0;
    public static final int PIX_FMT_NV12 = 23;
    public static final int PIX_FMT_MEDIACODEC = 165;
    
    /**
     * 检查是否支持指定的硬件解码器类型
     */
    public static native boolean isHardwareDecoderSupported(String decoderType);
    
    /**
     * 获取指定 codec 的硬件解码器名称
     */
    public static native String getHardwareDecoderName(String codecName);
    
    /**
     * 检查硬件解码器是否可用
     */
    public static native boolean isHardwareDecoderAvailable(String codecName);
    
    /**
     * 获取硬件解码器的像素格式
     */
    public static native int getHardwarePixelFormat(String decoderName);
    
    /**
     * 获取支持的硬件解码器列表
     */
    public static native String[] getSupportedHardwareDecoders();
    
    /**
     * 获取支持的硬件设备类型列表
     */
    public static native String[] getHardwareDeviceTypes();
    
    /**
     * 初始化硬件设备上下文
     */
    public static native long initHardwareDeviceContext(String deviceType);
    
    /**
     * 释放硬件设备上下文
     */
    public static native void freeHardwareDeviceContext(long deviceCtxPtr);
    
    /**
     * 创建硬件解码器
     */
    public static native long createHardwareDecoder(String decoderName, long deviceCtxPtr);
    
    /**
     * 解码帧（硬件解码）
     */
    public static native int decodeFrame(long contextId, byte[] inputData, int inputSize, byte[] outputData);
    
    /**
     * 获取解码帧信息
     */
    public static native int[] getFrameInfo(long contextId);
    
    /**
     * 刷新解码器
     */
    public static native void flushDecoder(long contextId);
    
    /**
     * 销毁硬件解码器
     */
    public static native void destroyHardwareDecoder(long contextId);
    
    /**
     * 获取 FFmpeg 版本信息
     */
    public static native String getFFmpegVersion();
    
    /**
     * 检查 mediacodec 是否可用
     */
    public static boolean isMediaCodecSupported() {
        return isHardwareDecoderSupported(HW_TYPE_MEDIACODEC);
    }
    
    /**
     * 获取硬件解码器状态信息
     */
    public static String getHardwareDecoderStatus() {
        StringBuilder status = new StringBuilder();
        status.append("FFmpeg Version: ").append(getFFmpegVersion()).append("\n");
        status.append("MediaCodec Supported: ").append(isMediaCodecSupported()).append("\n");
        
        String[] supportedDecoders = getSupportedHardwareDecoders();
        status.append("Supported Hardware Decoders: ").append(supportedDecoders.length).append("\n");
        for (String decoder : supportedDecoders) {
            status.append("  - ").append(decoder).append("\n");
        }
        
        String[] deviceTypes = getHardwareDeviceTypes();
        status.append("Available Hardware Device Types: ").append(deviceTypes.length).append("\n");
        for (String deviceType : deviceTypes) {
            status.append("  - ").append(deviceType).append("\n");
        }
        
        return status.toString();
    }
    
    /**
     * 帧信息包装类
     */
    public static class FrameInfo {
        public int width;
        public int height;
        public int format;
        public int linesize0;
        public int linesize1;
        public int linesize2;
        
        public FrameInfo(int[] info) {
            if (info != null && info.length >= 6) {
                this.width = info[0];
                this.height = info[1];
                this.format = info[2];
                this.linesize0 = info[3];
                this.linesize1 = info[4];
                this.linesize2 = info[5];
            }
        }
        
        @Override
        public String toString() {
            return String.format("FrameInfo{width=%d, height=%d, format=%d, linesize=[%d, %d, %d]}",
                    width, height, format, linesize0, linesize1, linesize2);
        }
    }
    
    /**
     * 硬件解码器实例
     */
    public static class HardwareDecoderInstance {
        private long deviceContextPtr;
        private long decoderContextId;
        private boolean initialized;
        
        public HardwareDecoderInstance(String deviceType, String decoderName) {
            this.deviceContextPtr = initHardwareDeviceContext(deviceType);
            if (this.deviceContextPtr != 0) {
                this.decoderContextId = createHardwareDecoder(decoderName, this.deviceContextPtr);
                this.initialized = (this.decoderContextId != 0);
            }
        }
        
        public int decodeFrame(byte[] inputData, byte[] outputData) {
            if (!initialized) return -1;
            return FFmpegHardwareDecoder.decodeFrame(decoderContextId, inputData, inputData.length, outputData);
        }
        
        public FrameInfo getFrameInfo() {
            if (!initialized) return null;
            // 修复：明确调用外部类的静态方法
            int[] info = FFmpegHardwareDecoder.getFrameInfo(decoderContextId);
            return info != null ? new FrameInfo(info) : null;
        }
        
        public void flush() {
            if (initialized) {
                FFmpegHardwareDecoder.flushDecoder(decoderContextId);
            }
        }
        
        public void release() {
            if (decoderContextId != 0) {
                FFmpegHardwareDecoder.destroyHardwareDecoder(decoderContextId);
                decoderContextId = 0;
            }
            if (deviceContextPtr != 0) {
                FFmpegHardwareDecoder.freeHardwareDeviceContext(deviceContextPtr);
                deviceContextPtr = 0;
            }
            initialized = false;
        }
        
        public boolean isInitialized() {
            return initialized;
        }
        
        @Override
        protected void finalize() throws Throwable {
            release();
            super.finalize();
        }
    }
}
