package com.tvscreenplus

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.os.Build
import android.os.Process
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Plays the PC sound with the smallest stable delay.
 * Packets (flag 0x08, kind 1): [1][format 1 = s16le][channels][0][u32 rate][u32 PC time ms][PCM], ~10-20 ms each.
 * - AudioTrack in streaming mode, low-latency path on Android 8+.
 * - Its buffer is the jitter buffer: small at first, +10 ms after each underrun while sound is flowing,
 *   -5 ms after 15 s without trouble (Android 7+).
 * - Audio that piles up (network burst after a stall) is dropped instead of being played late.
 */
class AudioPlayer(private val log: (String) -> Unit) {
    private class Packet(val rate: Int, val channels: Int, val pcm: ByteArray)

    private val queue = ArrayBlockingQueue<Packet>(64)
    @Volatile private var running = true
    @Volatile private var track: AudioTrack? = null
    private val thread = Thread({ loop() }, "audio").apply { start() }

    // Statistics for the 2 s STAT line.
    private val statLock = Any()
    private var underruns = 0
    private var drops = 0
    private var latSumMs = 0L
    private var latCount = 0
    private var transitMin = Long.MAX_VALUE
    private var transitMax = Long.MIN_VALUE
    /** Last measured delay of the TV-side buffer (ms), for the on-screen statistics. */
    @Volatile var latencyMs = -1
        private set

    /** Network thread: queue a packet (never blocks). */
    fun offer(b: ByteArray, len: Int) {
        if (len <= 12 || b[1].toInt() != 1) return
        val channels = b[2].toInt() and 0xFF
        val rate = u32(b, 4).toInt()
        if (channels !in 1..2 || rate !in 8000..192000) return
        // Arrival time minus PC capture time: constant clock offset + network jitter.
        val transit = System.nanoTime() / 1_000_000 - u32(b, 8)
        synchronized(statLock) { transitMin = minOf(transitMin, transit); transitMax = maxOf(transitMax, transit) }
        val p = Packet(rate, channels, b.copyOfRange(12, len))
        if (!queue.offer(p)) {
            queue.poll(); synchronized(statLock) { drops++ }
            queue.offer(p)
        }
    }

    /** ";alat=..;aund=..;adrop=..;ajit=.." for the STAT line, and resets the window. */
    fun stats(): String = synchronized(statLock) {
        val lat = if (latCount > 0) (latSumMs / latCount).toInt() else -1
        val jit = if (transitMax >= transitMin) (transitMax - transitMin).toInt() else -1
        latSumMs = 0; latCount = 0; transitMin = Long.MAX_VALUE; transitMax = Long.MIN_VALUE
        ";alat=$lat;aund=$underruns;adrop=$drops;ajit=$jit"
    }

    fun release() {
        running = false
        try { track?.pause(); track?.flush() } catch (_: Exception) {} // unblocks a pending write
        try { thread.join(500) } catch (_: Exception) {}
    }

    private fun loop() {
        try { Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO) } catch (_: Exception) {}
        var t: AudioTrack? = null
        var rate = 0
        var channels = 0
        var frameBytes = 4
        var written = 0            // frames written (wraps like playbackHeadPosition)
        var lastWrite = 0L
        var lastUnderruns = 0
        var steadySince = 0L
        var minFrames = 0          // smallest effective buffer
        var maxFrames = 0          // allocated buffer
        var failed = false
        try {
            while (running) {
                val p = queue.poll(200, TimeUnit.MILLISECONDS) ?: continue
                if (t == null || p.rate != rate || p.channels != channels) {
                    t?.let { try { it.stop() } catch (_: Exception) {}; it.release() }
                    t = null; track = null
                    rate = p.rate; channels = p.channels; frameBytes = 2 * channels
                    val n = try { create(rate, channels) } catch (e: Exception) {
                        if (!failed) log("audio: AudioTrack ${rate} Hz ${channels} ch failed: ${e.javaClass.simpleName}: ${e.message}")
                        failed = true; null
                    } ?: continue
                    t = n.first; maxFrames = n.second; minFrames = n.third
                    track = t
                    written = 0; lastUnderruns = 0; steadySince = System.nanoTime()
                    t.play()
                }
                val tr = t ?: continue
                val now = System.nanoTime()
                // Late audio is useless: if more than ~60 ms wait in the queue, keep only the newest.
                if (queue.size * p.pcm.size / frameBytes > rate * 60 / 1000) {
                    var d = 0
                    while (queue.size > 1) { queue.poll(); d++ }
                    synchronized(statLock) { drops += d + 1 }
                    continue
                }
                val flowing = now - lastWrite < 60_000_000L
                if (!flowing) {
                    // Sound resumes after a silence (the PC sends nothing then): add a small cushion against jitter.
                    val cushion = ByteArray(minFrames / 2 * frameBytes)
                    written += tr.write(cushion, 0, cushion.size).coerceAtLeast(0) / frameBytes
                }
                val r = tr.write(p.pcm, 0, p.pcm.size)
                if (r < 0) throw IllegalStateException("AudioTrack.write $r")
                written += r / frameBytes
                lastWrite = System.nanoTime()

                // Underruns while sound is flowing = buffer too small for this network: grow it.
                val queued = written - tr.playbackHeadPosition
                val underrun = if (Build.VERSION.SDK_INT >= 24) {
                    val u = tr.underrunCount
                    val grew = u > lastUnderruns && flowing
                    lastUnderruns = u
                    grew
                } else flowing && queued <= r / frameBytes
                if (underrun) {
                    synchronized(statLock) { underruns++ }
                    steadySince = lastWrite
                    if (Build.VERSION.SDK_INT >= 24 && tr.bufferSizeInFrames < maxFrames)
                        tr.setBufferSizeInFrames(minOf(maxFrames, tr.bufferSizeInFrames + rate / 100))
                } else if (Build.VERSION.SDK_INT >= 24 && lastWrite - steadySince > 15_000_000_000L && tr.bufferSizeInFrames > minFrames) {
                    steadySince = lastWrite
                    tr.setBufferSizeInFrames(maxOf(minFrames, tr.bufferSizeInFrames - rate / 200))
                }
                val ms = (queued.coerceAtLeast(0) * 1000L / rate).toInt()
                latencyMs = ms
                synchronized(statLock) { latSumMs += ms; latCount++ }
            }
        } catch (e: Exception) {
            if (running) log("audio error: ${e.javaClass.simpleName}: ${e.message}")
        } finally {
            track = null
            t?.let { try { it.stop() } catch (_: Exception) {}; it.release() }
        }
    }

    /** (track, allocated frames, starting effective frames). */
    private fun create(rate: Int, channels: Int): Triple<AudioTrack, Int, Int> {
        val mask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val frameBytes = 2 * channels
        val min = AudioTrack.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT)
        if (min <= 0) throw IllegalArgumentException("getMinBufferSize $min")
        // Android 7+ can shrink/grow the used part of the buffer: allocate room to grow (150 ms).
        val adaptive = Build.VERSION.SDK_INT >= 24
        val bytes = if (adaptive) maxOf(min * 2, rate * frameBytes * 150 / 1000) else maxOf(min, rate * frameBytes * 50 / 1000)
        val attrs = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
            .build()
        val format = AudioFormat.Builder().setSampleRate(rate).setChannelMask(mask).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build()
        val t = if (Build.VERSION.SDK_INT >= 26) {
            AudioTrack.Builder()
                .setAudioAttributes(attrs)
                .setAudioFormat(format)
                .setBufferSizeInBytes(bytes)
                .setTransferMode(AudioTrack.MODE_STREAM)
                .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                .build()
        } else {
            AudioTrack(attrs, format, bytes, AudioTrack.MODE_STREAM, AudioManager.AUDIO_SESSION_ID_GENERATE)
        }
        if (t.state != AudioTrack.STATE_INITIALIZED) { t.release(); throw IllegalStateException("not initialized") }
        val alloc = bytes / frameBytes
        var start = alloc
        if (Build.VERSION.SDK_INT >= 24) {
            // Start at ~30 ms (or the device minimum): playback only begins once this much is queued.
            start = maxOf(min / frameBytes, rate * 30 / 1000)
            start = t.setBufferSizeInFrames(start).takeIf { it > 0 } ?: alloc
        }
        val mode = if (Build.VERSION.SDK_INT >= 26) (if (t.performanceMode == AudioTrack.PERFORMANCE_MODE_LOW_LATENCY) " low-latency" else " normal") else ""
        log("audio: AudioTrack $rate Hz ${channels} ch$mode, min ${min / frameBytes * 1000 / rate} ms, buffer ${start * 1000 / rate}/${alloc * 1000 / rate} ms")
        return Triple(t, alloc, start)
    }

    private fun u32(b: ByteArray, o: Int): Long =
        ((b[o].toLong() and 0xFF) shl 24) or ((b[o + 1].toLong() and 0xFF) shl 16) or ((b[o + 2].toLong() and 0xFF) shl 8) or (b[o + 3].toLong() and 0xFF)
}
