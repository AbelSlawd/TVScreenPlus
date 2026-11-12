// Low-latency sound output through AAudio (MMAP when the TV offers it): the AudioTrack path of a TCL/MediaTek
// TV measured 196-198 ms from write to speaker (86 ms more than its own timestamps admit), AAudio 17-19 ms.
#include <jni.h>
#include <aaudio/AAudio.h>
#include <stdlib.h>

#define S(h) ((AAudioStream *)(uintptr_t)(h))

// Returns the stream handle, or a negative AAudio error code.
JNIEXPORT jlong JNICALL
Java_com_tvscreenplus_AAudioOut_nOpen(JNIEnv *env, jclass cls, jint rate, jint channels, jint game, jint capacity) {
    AAudioStreamBuilder *b = NULL; AAudioStream *s = NULL;
    aaudio_result_t r = AAudio_createStreamBuilder(&b);
    if (r != AAUDIO_OK) return r;
    AAudioStreamBuilder_setDirection(b, AAUDIO_DIRECTION_OUTPUT);
    AAudioStreamBuilder_setPerformanceMode(b, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
    AAudioStreamBuilder_setSharingMode(b, AAUDIO_SHARING_MODE_EXCLUSIVE); // falls back to shared by itself
    AAudioStreamBuilder_setFormat(b, AAUDIO_FORMAT_PCM_I16);
    AAudioStreamBuilder_setChannelCount(b, channels);
    AAudioStreamBuilder_setSampleRate(b, rate);
    AAudioStreamBuilder_setBufferCapacityInFrames(b, capacity);
    if (__builtin_available(android 28, *)) {
        AAudioStreamBuilder_setUsage(b, game ? AAUDIO_USAGE_GAME : AAUDIO_USAGE_MEDIA);
        AAudioStreamBuilder_setContentType(b, game ? AAUDIO_CONTENT_TYPE_MUSIC : AAUDIO_CONTENT_TYPE_MOVIE);
    }
    r = AAudioStreamBuilder_openStream(b, &s);
    AAudioStreamBuilder_delete(b);
    if (r != AAUDIO_OK) return r;
    if (AAudioStream_getSampleRate(s) != rate || AAudioStream_getFormat(s) != AAUDIO_FORMAT_PCM_I16) {
        AAudioStream_close(s);
        return AAUDIO_ERROR_INVALID_FORMAT;
    }
    r = AAudioStream_requestStart(s);
    if (r != AAUDIO_OK) { AAudioStream_close(s); return r; }
    return (jlong)(uintptr_t)s; // unsigned: a 32-bit address is not an error code
}

// 0 performance mode is low latency, 1 exclusive, 2 burst frames, 3 capacity frames.
JNIEXPORT jint JNICALL
Java_com_tvscreenplus_AAudioOut_nInfo(JNIEnv *env, jclass cls, jlong h, jint what) {
    switch (what) {
        case 0: return AAudioStream_getPerformanceMode(S(h)) == AAUDIO_PERFORMANCE_MODE_LOW_LATENCY;
        case 1: return AAudioStream_getSharingMode(S(h)) == AAUDIO_SHARING_MODE_EXCLUSIVE;
        case 2: return AAudioStream_getFramesPerBurst(S(h));
        default: return AAudioStream_getBufferCapacityInFrames(S(h));
    }
}

// Frames written (blocks up to timeoutMs for room), or a negative AAudio error (e.g. disconnected).
JNIEXPORT jint JNICALL
Java_com_tvscreenplus_AAudioOut_nWrite(JNIEnv *env, jclass cls, jlong h, jbyteArray a, jint off, jint frames, jint frameBytes, jint timeoutMs) {
    jbyte *p = (*env)->GetPrimitiveArrayCritical(env, a, NULL);
    if (p == NULL) return AAUDIO_ERROR_NO_MEMORY;
    aaudio_result_t r = AAudioStream_write(S(h), p + off, frames, (int64_t)timeoutMs * 1000000LL);
    (*env)->ReleasePrimitiveArrayCritical(env, a, p, JNI_ABORT);
    return r;
}

JNIEXPORT jlong JNICALL
Java_com_tvscreenplus_AAudioOut_nFramesRead(JNIEnv *env, jclass cls, jlong h) { return AAudioStream_getFramesRead(S(h)); }

JNIEXPORT jint JNICALL
Java_com_tvscreenplus_AAudioOut_nSetBuffer(JNIEnv *env, jclass cls, jlong h, jint frames) { return AAudioStream_setBufferSizeInFrames(S(h), frames); }

JNIEXPORT jint JNICALL
Java_com_tvscreenplus_AAudioOut_nXRuns(JNIEnv *env, jclass cls, jlong h) { return AAudioStream_getXRunCount(S(h)); }

// out = {frame position, CLOCK_MONOTONIC ns}; returns 0 when valid.
JNIEXPORT jint JNICALL
Java_com_tvscreenplus_AAudioOut_nTimestamp(JNIEnv *env, jclass cls, jlong h, jlongArray out) {
    int64_t v[2];
    aaudio_result_t r = AAudioStream_getTimestamp(S(h), CLOCK_MONOTONIC, &v[0], &v[1]);
    if (r == AAUDIO_OK) (*env)->SetLongArrayRegion(env, out, 0, 2, (jlong *)v);
    return r;
}

JNIEXPORT void JNICALL
Java_com_tvscreenplus_AAudioOut_nClose(JNIEnv *env, jclass cls, jlong h) {
    AAudioStream_requestStop(S(h));
    AAudioStream_close(S(h));
}
