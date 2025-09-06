// oboe_jni.cpp
#include "oboe_audio_renderer.h"
#include <jni.h>

extern "C" {

JNIEXPORT void JNICALL
Java_com_ryujinx_audio_backends_oboe_OboeAudioDriver_initOboeAudio(JNIEnv*, jclass) {
    OboeAudioRenderer::getInstance().initialize();
}

JNIEXPORT void JNICALL
Java_com_ryujinx_audio_backends_oboe_OboeAudioDriver_shutdownOboeAudio(JNIEnv*, jclass) {
    OboeAudioRenderer::getInstance().shutdown();
}

JNIEXPORT void JNICALL
Java_com_ryujinx_audio_backends_oboe_OboeAudioDriver_writeOboeAudio(JNIEnv* env, jclass, jfloatArray data, jint numFrames) {
    if (numFrames <= 0) return;

    jfloat* buffer = env->GetFloatArrayElements(data, nullptr);
    if (buffer) {
        OboeAudioRenderer::getInstance().writeAudio(buffer, numFrames);
        env->ReleaseFloatArrayElements(data, buffer, JNI_ABORT);
    }
}

JNIEXPORT void JNICALL
Java_com_ryujinx_audio_backends_oboe_OboeAudioDriver_setOboeSampleRate(JNIEnv*, jclass, jint sampleRate) {
    OboeAudioRenderer::getInstance().setSampleRate(sampleRate);
}

JNIEXPORT void JNICALL
Java_com_ryujinx_audio_backends_oboe_OboeAudioDriver_setOboeBufferSize(JNIEnv*, jclass, jint bufferSize) {
    OboeAudioRenderer::getInstance().setBufferSize(bufferSize);
}

JNIEXPORT void JNICALL
Java_com_ryujinx_audio_backends_oboe_OboeAudioDriver_setOboeVolume(JNIEnv*, jclass, jfloat volume) {
    OboeAudioRenderer::getInstance().setVolume(volume);
}

} // extern "C"
