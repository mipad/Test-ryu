// FFmpegAdapter.java
package org.ryujinx.android;

public class FFmpegAdapter {
    static {
        System.loadLibrary("ryujinxjni");
    }
    
    // 只需要几个辅助函数
    public static native int avcodecVersion();
    public static native boolean supportsHardwareDecoding();
    
    // 常量定义
    public static final int AV_CODEC_ID_H264 = 27;
    public static final int AV_CODEC_ID_HEVC = 173;
    public static final int AV_CODEC_ID_VP9 = 167;
}