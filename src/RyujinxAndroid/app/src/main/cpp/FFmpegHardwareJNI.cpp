#include "FFmpegHardwareDecoder.h"
#include <jni.h>
#include <android/log.h>

#define LOG_TAG_JNI "FFmpegHwJNI"
#define LOGI_JNI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG_JNI, __VA_ARGS__)
#define LOGE_JNI(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG_JNI, __VA_ARGS__)

// JNI 接口实现
extern "C" {

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isHardwareDecoderSupported(
    JNIEnv* env,
    jclass clazz,
    jstring decoder_type) {
    
    const char* type_str = env->GetStringUTFChars(decoder_type, nullptr);
    if (!type_str) {
        return JNI_FALSE;
    }
    
    bool supported = FFmpegHardwareDecoder::GetInstance().IsHardwareDecoderSupported(type_str);
    env->ReleaseStringUTFChars(decoder_type, type_str);
    
    return supported ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getHardwareDecoderName(
    JNIEnv* env,
    jclass clazz,
    jstring codec_name) {
    
    const char* codec_str = env->GetStringUTFChars(codec_name, nullptr);
    if (!codec_str) {
        return env->NewStringUTF("");
    }
    
    const char* hw_name = FFmpegHardwareDecoder::GetInstance().GetHardwareDecoderName(codec_str);
    env->ReleaseStringUTFChars(codec_name, codec_str);
    
    return env->NewStringUTF(hw_name);
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isHardwareDecoderAvailable(
    JNIEnv* env,
    jclass clazz,
    jstring codec_name) {
    
    const char* codec_str = env->GetStringUTFChars(codec_name, nullptr);
    if (!codec_str) {
        return JNI_FALSE;
    }
    
    bool available = FFmpegHardwareDecoder::GetInstance().IsHardwareDecoderAvailable(codec_str);
    env->ReleaseStringUTFChars(codec_name, codec_str);
    
    return available ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getHardwarePixelFormat(
    JNIEnv* env,
    jclass clazz,
    jstring decoder_name) {
    
    const char* decoder_str = env->GetStringUTFChars(decoder_name, nullptr);
    if (!decoder_str) {
        return -1;
    }
    
    int format = FFmpegHardwareDecoder::GetInstance().GetHardwarePixelFormat(decoder_str);
    env->ReleaseStringUTFChars(decoder_name, decoder_str);
    
    return format;
}

JNIEXPORT jobjectArray JNICALL
Java_org_ryujinx_android_NativeHelpers_getSupportedHardwareDecoders(
    JNIEnv* env,
    jclass clazz) {
    
    std::vector<std::string> decoders = FFmpegHardwareDecoder::GetInstance().GetSupportedHardwareDecoders();
    
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray result = env->NewObjectArray(decoders.size(), stringClass, nullptr);
    
    for (size_t i = 0; i < decoders.size(); i++) {
        env->SetObjectArrayElement(result, i, env->NewStringUTF(decoders[i].c_str()));
    }
    
    return result;
}

JNIEXPORT jobjectArray JNICALL
Java_org_ryujinx_android_NativeHelpers_getHardwareDeviceTypes(
    JNIEnv* env,
    jclass clazz) {
    
    std::vector<std::string> device_types = FFmpegHardwareDecoder::GetInstance().GetHardwareDeviceTypes();
    
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray result = env->NewObjectArray(device_types.size(), stringClass, nullptr);
    
    for (size_t i = 0; i < device_types.size(); i++) {
        env->SetObjectArrayElement(result, i, env->NewStringUTF(device_types[i].c_str()));
    }
    
    return result;
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_initHardwareDeviceContext(
    JNIEnv* env,
    jclass clazz,
    jstring device_type) {
    
    const char* type_str = env->GetStringUTFChars(device_type, nullptr);
    if (!type_str) {
        return 0;
    }
    
    jlong result = FFmpegHardwareDecoder::GetInstance().InitHardwareDeviceContext(type_str);
    env->ReleaseStringUTFChars(device_type, type_str);
    
    return result;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_freeHardwareDeviceContext(
    JNIEnv* env,
    jclass clazz,
    jlong device_ctx_ptr) {
    
    FFmpegHardwareDecoder::GetInstance().FreeHardwareDeviceContext(device_ctx_ptr);
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_createHardwareDecoderContext(
    JNIEnv* env,
    jclass clazz,
    jstring codec_name) {
    
    const char* codec_str = env->GetStringUTFChars(codec_name, nullptr);
    if (!codec_str) {
        return 0;
    }
    
    jlong context_id = FFmpegHardwareDecoder::GetInstance().CreateHardwareDecoderContext(env, codec_str);
    env->ReleaseStringUTFChars(codec_name, codec_str);
    
    return context_id;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_decodeVideoFrame(
    JNIEnv* env,
    jclass clazz,
    jlong context_id,
    jbyteArray input_data,
    jint input_size,
    jintArray frame_info,
    jobjectArray plane_data) {
    
    return FFmpegHardwareDecoder::GetInstance().DecodeVideoFrame(
        context_id, input_data, input_size, frame_info, plane_data);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_flushDecoder(
    JNIEnv* env,
    jclass clazz,
    jlong context_id) {
    
    FFmpegHardwareDecoder::GetInstance().FlushDecoder(context_id);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_destroyHardwareDecoder(
    JNIEnv* env,
    jclass clazz,
    jlong context_id) {
    
    FFmpegHardwareDecoder::GetInstance().DestroyHardwareDecoderContext(context_id);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getFFmpegVersion(JNIEnv* env, jclass clazz) {
    const char* version = FFmpegHardwareDecoder::GetInstance().GetFFmpegVersion();
    return env->NewStringUTF(version);
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isHardwareDecodingSupported(JNIEnv *env, jobject thiz) {
    bool supported = FFmpegHardwareDecoder::GetInstance().IsHardwareDecoderSupported("mediacodec");
    return supported ? JNI_TRUE : JNI_FALSE;
}

// 初始化函数
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_initFFmpegHardwareDecoder(JNIEnv* env, jclass clazz) {
    bool success = FFmpegHardwareDecoder::GetInstance().Initialize();
    LOGI_JNI("FFmpeg hardware decoder initialization: %s", success ? "success" : "failed");
}

// 清理函数
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_cleanupFFmpegHardwareDecoder(JNIEnv* env, jclass clazz) {
    FFmpegHardwareDecoder::GetInstance().Cleanup();
    LOGI_JNI("FFmpeg hardware decoder cleaned up");
}

} // extern "C"