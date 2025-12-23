// ffmpeg_adapter.cpp - 简化版
#include <jni.h>
#include <android/log.h>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/avutil.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
}

#define LOG_TAG "FFmpegAdapter"

// 这个文件不需要实现任何函数，只需要包含头文件
// 链接静态库后，符号会自动导出

// 如果需要导出特定的 JNI 函数，可以添加在这里
extern "C" {

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_FFmpegAdapter_avcodecVersion(JNIEnv* env, jclass clazz) {
    return avcodec_version();
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_FFmpegAdapter_supportsHardwareDecoding(JNIEnv* env, jclass clazz) {
    const AVCodec* codec = avcodec_find_decoder(AV_CODEC_ID_H264);
    if (!codec) return JNI_FALSE;
    
    const AVCodecHWConfig* config = avcodec_get_hw_config(codec, 0);
    return config != nullptr ? JNI_TRUE : JNI_FALSE;
}

} // extern "C"