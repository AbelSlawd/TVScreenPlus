package com.tvscreenplus

import android.os.Build

/** AAudio output stream (native, see jni/aaudio_out.c). Same role as an AudioTrack in [AudioPlayer]. */
class AAudioOut private constructor(private var h: Long, val frameBytes: Int) {
    companion object {
        /** AAudio with MMAP (shared) exists from Android 8.1; the library loads only there. */
        val available: Boolean = Build.VERSION.SDK_INT >= 27 && try { System.loadLibrary("tvsaudio"); true } catch (_: Throwable) { false }

        /** Low-latency stream, or null with the reason in [lastError]. */
        fun open(rate: Int, channels: Int, game: Boolean, capacityFrames: Int): AAudioOut? {
            val h = nOpen(rate, channels, if (game) 1 else 0, capacityFrames)
            if (h <= 0) { lastError = "open error $h"; return null }
            if (nInfo(h, 0) != 1) { nClose(h); lastError = "no low-latency mode"; return null }
            return AAudioOut(h, 2 * channels)
        }
        @Volatile var lastError = ""

        @JvmStatic private external fun nOpen(rate: Int, channels: Int, game: Int, capacity: Int): Long
        @JvmStatic private external fun nInfo(h: Long, what: Int): Int
        @JvmStatic private external fun nWrite(h: Long, b: ByteArray, off: Int, frames: Int, frameBytes: Int, timeoutMs: Int): Int
        @JvmStatic private external fun nFramesRead(h: Long): Long
        @JvmStatic private external fun nSetBuffer(h: Long, frames: Int): Int
        @JvmStatic private external fun nXRuns(h: Long): Int
        @JvmStatic private external fun nTimestamp(h: Long, out: LongArray): Int
        @JvmStatic private external fun nClose(h: Long)
    }

    private val tsOut = LongArray(2)
    val capacity: Int get() = nInfo(h, 3)
    val burst: Int get() = nInfo(h, 2)
    val exclusive: Boolean get() = nInfo(h, 1) == 1

    /** Bytes written (blocks while the buffer is full); throws when the stream is broken (device change). */
    fun write(b: ByteArray, off: Int, len: Int): Int {
        val r = nWrite(h, b, off, len / frameBytes, frameBytes, 200)
        if (r < 0) throw IllegalStateException("AAudio write $r")
        return r * frameBytes
    }
    /** Frames consumed by the device (like AudioTrack.playbackHeadPosition, without the wrap). */
    val framesRead: Long get() = nFramesRead(h)
    fun setBufferSizeInFrames(frames: Int) { nSetBuffer(h, frames) }
    val underrunCount: Int get() = nXRuns(h)
    /** (frame position, CLOCK_MONOTONIC ns) when the device can tell. */
    fun timestamp(): Pair<Long, Long>? = if (nTimestamp(h, tsOut) == 0) tsOut[0] to tsOut[1] else null
    fun close() { if (h != 0L) { nClose(h); h = 0 } }
}
