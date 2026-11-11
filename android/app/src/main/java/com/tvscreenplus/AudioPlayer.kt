package com.tvscreenplus

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTimestamp
import android.media.AudioTrack
import android.os.Build
import android.os.Process
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Plays the PC sound with the smallest stable delay, and measures that delay for the A/V sync.
 * Packets (flag 0x08, kind 1): [1][format 1 = s16le][channels][0][u32 rate][u32 PC time ms][PCM], ~5-20 ms each.
 *
 * - The amount of sound waiting in the AudioTrack is regulated to a target = jitter buffer + extra delay:
 *   the jitter buffer starts at 30 ms (getMinBufferSize is very conservative: 96 ms on a TCL/MediaTek TV),
 *   grows 10 ms after an underrun (max 80) and shrinks 2 ms after 15 s without trouble (never below 25 ms:
 *   going lower caused regular underruns on a busy 100 Mb/s link). Each underrun or trim is an audible click,
 *   so the buffer settles where it stops happening rather than chasing the smallest value. Sound piling up above the target
 *   is trimmed instead of being played late.
 * - The extra delay is set by the A/V sync when the sound would otherwise be ahead of the image.
 * - [outputMs] is the real write -> speaker delay (AudioTrack.getTimestamp), TV audio hardware included.
 */
class AudioPlayer(private val log: (String) -> Unit) {
    private class Packet(val rate: Int, val channels: Int, val pcm: ByteArray)

    private val queue = ArrayBlockingQueue<Packet>(64)
    @Volatile private var running = true
    @Volatile private var track: AudioTrack? = null
    private val thread = Thread({ loop() }, "audio").apply { start() }

    /** Delay added on purpose so that the sound matches the image (ms, set by the A/V sync). */
    @Volatile var extraDelayMs = 0
    /** Write -> speaker delay measured by the TV (ms, smoothed; includes the extra delay); -1 = not known yet. */
    @Volatile var outputMs = -1
        private set
    /** Current jitter buffer target, without the extra delay (ms). */
    @Volatile var bufferMs = 0
        private set
    /** Sound waiting in the AudioTrack right now (ms), for the on-screen statistics. */
    @Volatile var latencyMs = -1
        private set

    // Statistics for the 2 s STAT line.
    private val statLock = Any()
    private var underruns = 0
    private var drops = 0
    private var transitMin = Long.MAX_VALUE
    private var transitMax = Long.MIN_VALUE

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

    /** ";alat=..;aund=..;adrop=..;ajit=..;aout=..;abuf=..;aext=.." for the STAT line, and resets the window. */
    fun stats(): String = synchronized(statLock) {
        val jit = if (transitMax >= transitMin) (transitMax - transitMin).toInt() else -1
        transitMin = Long.MAX_VALUE; transitMax = Long.MIN_VALUE
        ";alat=$latencyMs;aund=$underruns;adrop=$drops;ajit=$jit;aout=$outputMs;abuf=$bufferMs;aext=$extraDelayMs"
    }

    fun release() {
        running = false
        try { track?.pause(); track?.flush() } catch (_: Exception) {} // unblocks a pending write
        try { thread.join(500) } catch (_: Exception) {}
    }

    private fun loop() {
        try { Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO) } catch (_: Exception) {}
        var t: AudioTrack? = null
        var rate = 48000
        var channels = 0
        var frameBytes = 4
        var capacity = 0           // allocated frames
        var written = 0            // frames written (wraps like playbackHeadPosition)
        var writtenTotal = 0L      // same, never wraps (compared with AudioTimestamp.framePosition)
        var lastWrite = 0L
        var lastUnderruns = 0
        var steadySince = 0L
        var baseMs = 30            // jitter buffer target
        var appliedTarget = -1     // frames: buffer size last given to the track
        var levelAvg = -1.0        // smoothed fill level after each write (frames)
        var lastTs = 0L
        var startedAt = 0L
        var reported = false
        val ts = AudioTimestamp()
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
                    t = n.first; capacity = n.second
                    track = t
                    written = 0; writtenTotal = 0; lastUnderruns = 0; steadySince = System.nanoTime()
                    appliedTarget = -1; levelAvg = -1.0; outputMs = -1; startedAt = System.nanoTime(); reported = false
                    t.play()
                }
                val tr = t ?: continue
                val now = System.nanoTime()

                // Target fill level = jitter buffer + extra delay for the A/V sync.
                val margin = rate * 30 / 1000
                val target = minOf(capacity - margin, rate * (baseMs + extraDelayMs) / 1000).coerceAtLeast(rate / 100)
                if (target != appliedTarget && Build.VERSION.SDK_INT >= 24) {
                    // Room above the target so that writes never block in normal operation.
                    tr.setBufferSizeInFrames(minOf(capacity, target + margin))
                }
                appliedTarget = target
                bufferMs = baseMs

                val pktFrames = p.pcm.size / frameBytes
                val queued = written - tr.playbackHeadPosition
                val flowing = now - lastWrite < 60_000_000L
                var skip = 0
                if (!flowing || queued <= 0) {
                    // Sound (re)starts or the track ran dry: refill to the target with silence first.
                    val fill = (target - queued.coerceAtLeast(0) - pktFrames).coerceAtLeast(0)
                    if (fill > 0) {
                        val r = tr.write(ByteArray(fill * frameBytes), 0, fill * frameBytes).coerceAtLeast(0) / frameBytes
                        written += r; writtenTotal += r
                    }
                    levelAvg = -1.0
                } else {
                    // Sound piling up (network burst, target lowered): trim it instead of playing it late.
                    val after = queued + pktFrames
                    levelAvg = if (levelAvg < 0) after.toDouble() else levelAvg * 0.9 + after * 0.1
                    if (levelAvg > target + maxOf(rate * 40 / 1000, target / 2)) {
                        skip = minOf(pktFrames, (levelAvg - target).toInt())
                        levelAvg -= skip
                        synchronized(statLock) { drops++ }
                    }
                }
                if (skip < pktFrames) {
                    val r = tr.write(p.pcm, skip * frameBytes, (pktFrames - skip) * frameBytes)
                    if (r < 0) throw IllegalStateException("AudioTrack.write $r")
                    written += r / frameBytes; writtenTotal += r / frameBytes
                }
                lastWrite = System.nanoTime()

                // Underruns while sound is flowing = jitter buffer too small for this network: grow it.
                val underrun = if (Build.VERSION.SDK_INT >= 24) {
                    val u = tr.underrunCount
                    val grew = u > lastUnderruns && flowing
                    lastUnderruns = u
                    grew
                } else flowing && queued <= 0
                // The first 2 s (track start-up) don't count; one burst must not keep the delay high for minutes.
                if (underrun && lastWrite - startedAt > 2_000_000_000L) {
                    synchronized(statLock) { underruns++ }
                    steadySince = lastWrite
                    baseMs = minOf(80, baseMs + 10)
                } else if (lastWrite - steadySince > 15_000_000_000L && baseMs > 25) {
                    steadySince = lastWrite
                    baseMs = maxOf(25, baseMs - 2)
                }
                latencyMs = ((written - tr.playbackHeadPosition).coerceAtLeast(0) * 1000L / rate).toInt()

                // Real delay until the speaker: frames not yet presented, per the audio hardware's own clock.
                if (lastWrite - lastTs > 250_000_000L && tr.getTimestamp(ts)) {
                    lastTs = lastWrite
                    val pending = writtenTotal - ts.framePosition
                    val ms = pending * 1000.0 / rate - (System.nanoTime() - ts.nanoTime) / 1e6
                    if (ms in 0.0..1000.0) outputMs = if (outputMs < 0) ms.toInt() else (outputMs * 0.8 + ms * 0.2).toInt()
                }
                if (!reported && lastWrite - startedAt > 4_000_000_000L) {
                    reported = true
                    log("audio: output delay $outputMs ms (timestamp), buffer $baseMs ms + extra $extraDelayMs ms, queued $latencyMs ms")
                }
            }
        } catch (e: Exception) {
            if (running) log("audio error: ${e.javaClass.simpleName}: ${e.message}")
        } finally {
            track = null
            t?.let { try { it.stop() } catch (_: Exception) {}; it.release() }
        }
    }

    /** (track, allocated frames). */
    private fun create(rate: Int, channels: Int): Pair<AudioTrack, Int> {
        val mask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val frameBytes = 2 * channels
        val min = AudioTrack.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT)
        if (min <= 0) throw IllegalArgumentException("getMinBufferSize $min")
        // Room for the jitter buffer (up to 80 ms) + an A/V sync delay (up to 200 ms) + margin.
        val bytes = maxOf(min * 2, rate * frameBytes * 320 / 1000)
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
        if (Build.VERSION.SDK_INT >= 24) t.setBufferSizeInFrames(rate * 50 / 1000) // the loop then follows its target
        val mode = if (Build.VERSION.SDK_INT >= 26) (if (t.performanceMode == AudioTrack.PERFORMANCE_MODE_LOW_LATENCY) " low-latency" else " normal") else ""
        log("audio: AudioTrack $rate Hz ${channels} ch$mode, min ${min / frameBytes * 1000 / rate} ms, capacity ${alloc * 1000 / rate} ms")
        return t to alloc
    }

    private fun u32(b: ByteArray, o: Int): Long =
        ((b[o].toLong() and 0xFF) shl 24) or ((b[o + 1].toLong() and 0xFF) shl 16) or ((b[o + 2].toLong() and 0xFF) shl 8) or (b[o + 3].toLong() and 0xFF)
}
