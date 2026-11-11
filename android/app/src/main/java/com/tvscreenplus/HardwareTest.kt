package com.tvscreenplus

import android.graphics.Color
import android.graphics.ImageFormat
import android.graphics.Paint
import android.media.ImageReader
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.util.Log

/**
 * Measures what the TV can really decode: for each codec and resolution (4K → 720p), a short clip is
 * encoded on the TV itself, then decoded as fast as possible (throughput) and at 60 fps pacing
 * (per-frame latency, the delay the decoder adds to the stream). Results are sent to the PC.
 */
object HardwareTest {
    /** report: one human-readable line per step, sent to the PC log on every connection. */
    class Result(val hevc: Int, val avc: Int, val latencyMs: Int, val report: List<String>)

    private class Clip(val csd: ByteArray?, val frames: List<Pair<ByteArray, Int>>)

    fun run(progress: (String) -> Unit): Result {
        val report = ArrayList<String>()
        val (avc, avcLat) = testCodec(MediaFormat.MIMETYPE_VIDEO_AVC, "H.264", progress, report)
        val (hevc, hevcLat) = testCodec(MediaFormat.MIMETYPE_VIDEO_HEVC, "HEVC", progress, report)
        val lat = listOf(hevcLat, avcLat).filter { it >= 0 }.minOrNull() ?: -1
        report += "result: HEVC ${hevc}p, H.264 ${avc}p, latency ${if (lat >= 0) "$lat ms" else "unknown"}"
        return Result(hevc, avc, lat, report)
    }

    private fun why(e: Exception) = "${e.javaClass.simpleName}: ${e.message ?: ""}".take(160)

    /** Returns (max height decodable at ≥ 60 fps, latency in ms or -1). */
    private fun testCodec(mime: String, label: String, progress: (String) -> Unit, report: MutableList<String>): Pair<Int, Int> {
        fun note(s: String) { report += "$label $s"; Log.i(StreamClient.TAG, "hwtest $label $s") }
        val declared = Codecs.declaredMaxHeight(mime)
        note("declared ${declared}p60")
        if (declared == 0) return 0 to -1
        for (h in listOf(2160, 1440, 1080, 720)) {
            if (h > maxOf(declared, 1080)) continue // far above what the chip claims: skip
            progress("$label ${h}p")
            val w = Codecs.width(h)
            val clip = try { encodeClip(mime, w, h) } catch (e: Exception) { note("${h}p: no test clip (${why(e)})"); null }
            if (clip != null && clip.frames.size < 20) note("${h}p: test clip too short (${clip.frames.size} frames)")
            if (clip == null || clip.frames.size < 20) {
                if (declared >= h) { note("${h}p: kept as declared, NOT measured"); return h to -1 } // trust the declared capability
                continue
            }
            val fps = try { throughput(mime, w, h, clip) } catch (e: Exception) { note("${h}p: decode error (${why(e)})"); 0.0 }
            note("${h}p: decodes %.1f fps (clip ${clip.frames.size} frames)".format(fps))
            if (fps >= 66) {
                val lat = try { latency(mime, w, h, clip) } catch (e: Exception) { note("${h}p: latency not measured (${why(e)})"); -1 }
                if (lat >= 0) note("${h}p: latency $lat ms (75th percentile)")
                return h to lat
            }
        }
        return 0 to -1
    }

    private fun encodeClip(mime: String, w: Int, h: Int): Clip {
        val enc = MediaCodec.createEncoderByType(mime)
        val surface: android.view.Surface
        try {
            val f = MediaFormat.createVideoFormat(mime, w, h).apply {
                setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                setInteger(MediaFormat.KEY_BIT_RATE, (w * h * 60L / 10).toInt().coerceAtMost(80_000_000))
                setInteger(MediaFormat.KEY_FRAME_RATE, 60)
                setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 10)
            }
            enc.configure(f, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            surface = enc.createInputSurface()
            enc.start()
        } catch (e: Exception) { enc.release(); throw e }

        var csd: ByteArray? = null
        val frames = ArrayList<Pair<ByteArray, Int>>()
        val info = MediaCodec.BufferInfo()
        fun drain(end: Boolean) {
            val deadline = System.currentTimeMillis() + 3000
            while (System.currentTimeMillis() < deadline) {
                val idx = enc.dequeueOutputBuffer(info, if (end) 10_000 else 0)
                if (idx < 0) { if (!end) return else continue }
                val buf = enc.getOutputBuffer(idx)!!
                val data = ByteArray(info.size).also { buf.position(info.offset); buf.get(it) }
                if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) csd = data
                else if (info.size > 0) frames += data to info.flags
                enc.releaseOutputBuffer(idx, false)
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) return
            }
        }
        try {
            val p = Paint().apply { textSize = h / 8f; isAntiAlias = true }
            for (i in 0 until 60) {
                val c = if (Build.VERSION.SDK_INT >= 23) surface.lockHardwareCanvas() else surface.lockCanvas(null)
                // Desktop-like content: flat background, moving window, text.
                c.drawColor(Color.rgb(20 + i, 40, 80))
                p.color = Color.WHITE
                c.drawRect((i * w / 80f), h / 4f, (i * w / 80f) + w / 3f, h * 3 / 4f, p)
                p.color = Color.BLACK
                c.drawText("TVScreenPlus $i", (i * w / 80f) + 20f, h / 2f, p)
                surface.unlockCanvasAndPost(c)
                drain(false)
            }
            enc.signalEndOfInputStream()
            drain(true)
        } finally {
            try { enc.stop() } catch (_: Exception) {}
            enc.release(); surface.release()
        }
        return Clip(csd, frames)
    }

    private fun output(w: Int, h: Int): Pair<ImageReader?, android.view.Surface?> {
        if (Build.VERSION.SDK_INT < 23) return null to null
        val r = ImageReader.newInstance(w, h, ImageFormat.PRIVATE, 4)
        return r to r.surface
    }

    /** Decode the clip 3 times as fast as possible; returns frames per second. */
    private fun throughput(mime: String, w: Int, h: Int, clip: Clip): Double {
        val (reader, surface) = output(w, h)
        val dec = Codecs.lowLatencyDecoder(mime, w, h, surface)
        val info = MediaCodec.BufferInfo()
        val inputs = ArrayList<Pair<ByteArray, Int>>()
        clip.csd?.let { inputs += it to MediaCodec.BUFFER_FLAG_CODEC_CONFIG }
        repeat(3) { inputs += clip.frames }
        val total = clip.frames.size * 3
        var next = 0; var out = 0
        val t0 = System.nanoTime()
        try {
            while (out < total && System.nanoTime() - t0 < 8_000_000_000L) {
                if (next < inputs.size) {
                    val i = dec.dequeueInputBuffer(0)
                    if (i >= 0) {
                        val (data, flags) = inputs[next]
                        dec.getInputBuffer(i)!!.apply { clear(); put(data) }
                        dec.queueInputBuffer(i, 0, data.size, next * 16_667L, flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG)
                        next++
                    }
                }
                val o = dec.dequeueOutputBuffer(info, 1000)
                if (o >= 0) { dec.releaseOutputBuffer(o, false); out++ }
            }
            return out / ((System.nanoTime() - t0) / 1e9)
        } finally {
            try { dec.stop() } catch (_: Exception) {}
            dec.release(); reader?.close()
        }
    }

    /** Feed frames at 60 fps like a real stream and measure input → output delay (ms). */
    private fun latency(mime: String, w: Int, h: Int, clip: Clip): Int {
        val (reader, surface) = output(w, h)
        val dec = Codecs.lowLatencyDecoder(mime, w, h, surface)
        val info = MediaCodec.BufferInfo()
        val queued = HashMap<Long, Long>()
        val samples = ArrayList<Long>()
        try {
            clip.csd?.let { c ->
                val i = dec.dequeueInputBuffer(100_000)
                if (i >= 0) { dec.getInputBuffer(i)!!.apply { clear(); put(c) }; dec.queueInputBuffer(i, 0, c.size, 0, MediaCodec.BUFFER_FLAG_CODEC_CONFIG) }
            }
            val start = System.nanoTime()
            for ((k, f) in clip.frames.withIndex()) {
                val due = start + k * 16_666_667L
                while (System.nanoTime() < due) {
                    val o = dec.dequeueOutputBuffer(info, 1000)
                    if (o >= 0) { queued.remove(info.presentationTimeUs)?.let { samples += System.nanoTime() - it }; dec.releaseOutputBuffer(o, false) }
                }
                val i = dec.dequeueInputBuffer(20_000)
                if (i < 0) continue
                val pts = (k + 1) * 16_667L
                dec.getInputBuffer(i)!!.apply { clear(); put(f.first) }
                queued[pts] = System.nanoTime()
                dec.queueInputBuffer(i, 0, f.first.size, pts, 0)
            }
            val end = System.nanoTime() + 300_000_000L
            while (queued.isNotEmpty() && System.nanoTime() < end) {
                val o = dec.dequeueOutputBuffer(info, 5000)
                if (o >= 0) { queued.remove(info.presentationTimeUs)?.let { samples += System.nanoTime() - it }; dec.releaseOutputBuffer(o, false) }
            }
            if (samples.size < 10) throw Exception("only ${samples.size} frames came out of the decoder")
            return (samples.drop(5).sorted()[samples.size * 3 / 4 - 5] / 1_000_000).toInt() // 75th percentile
        } finally {
            try { dec.stop() } catch (_: Exception) {}
            dec.release(); reader?.close()
        }
    }
}
