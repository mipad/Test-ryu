#ifndef RYUJINX_FFMPEG_HW_JNI_H
#define RYUJINX_FFMPEG_HW_JNI_H

#include <jni.h>

#ifdef __cplusplus
extern "C" {
#endif

// JNI 函数声明
JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved);
JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved);

// 硬件解码器函数
JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_isHardwareDecoderSupported(JNIEnv*, jclass, jstring);
JNIEXPORT jstring JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_getHardwareDecoderName(JNIEnv*, jclass, jstring);
JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_isHardwareDecoderAvailable(JNIEnv*, jclass, jstring);
JNIEXPORT jint JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_getHardwarePixelFormat(JNIEnv*, jclass, jstring);
JNIEXPORT jobjectArray JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_getSupportedHardwareDecoders(JNIEnv*, jclass);
JNIEXPORT jobjectArray JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_getHardwareDeviceTypes(JNIEnv*, jclass);
JNIEXPORT jlong JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_initHardwareDeviceContext(JNIEnv*, jclass, jstring);
JNIEXPORT void JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_freeHardwareDeviceContext(JNIEnv*, jclass, jlong);
JNIEXPORT jlong JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_createHardwareDecoder(JNIEnv*, jclass, jstring, jlong);
JNIEXPORT jint JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_decodeFrame(JNIEnv*, jclass, jlong, jbyteArray, jint, jbyteArray);
JNIEXPORT jintArray JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_getFrameInfo(JNIEnv*, jclass, jlong);
JNIEXPORT void JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_flushDecoder(JNIEnv*, jclass, jlong);
JNIEXPORT void JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_destroyHardwareDecoder(JNIEnv*, jclass, jlong);
JNIEXPORT jstring JNICALL Java_org_ryujinx_android_FFmpegHardwareDecoder_getFFmpegVersion(JNIEnv*, jclass);

#ifdef __cplusplus
}
#endif

#endif // RYUJINX_FFMPEG_HW_JNI_H