package com.tvscreenplus

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Process
import android.util.Log
import android.view.Surface
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Connects to a PC, receives [u32 len][u8 flags][payload] packets and decodes the video with the
 * hardware decoder straight onto the Surface (zero copy, low-latency mode).
 * flags: 0x80 filler (bandwidth test), 0x40 ping (answer "ACK"), 0x20 config, 0x10 status text,
 * otherwise an Annex-B access unit (1 = keyframe).
 */
class StreamClient(
    private val host: String,
    private val hello: String,
    private val surface: Surface,
    private val listener: Listener,
) {
    interface Listener {
        fun onStatus(text: String)
        fun onStarted(info: String)
        fun onEnded(error: String?)
    }

    @Volatile private var running = true
    private var socket: Socket? = null
    private var out: OutputStream? = null
    @Volatile private var decoder: Decoder? = null

    // Live statistics (overlay + reports to the PC).
    @Volatile var info = ""
    @Volatile var fps = 0
    @Volatile var kbps = 0
    @Volatile var dropped = 0
    private var bytesWindow = 0L
    @Volatile private var rendered = 0

    fun start() = Thread({ run() }, "net-decode").start()

    fun stop() {
        running = false
        try { socket?.close() } catch (_: Exception) {}
    }

    private fun send(line: String) {
        val o = out ?: return
        synchronized(o) { o.write((line + "\n").toByteArray()); o.flush() }
    }

    private fun run() {
        boost()
        var error: String? = null
        try {
            val s = Socket()
            socket = s
            s.tcpNoDelay = true
            s.receiveBufferSize = 4 shl 20
            s.connect(InetSocketAddress(host, PORT), 4000)
            out = s.getOutputStream()
            send(hello)
            val input = DataInputStream(BufferedInputStream(s.getInputStream(), 256 shl 10))
            startStats()

            // Old PC versions answer with a text line "OK;...", new ones only send packets.
            input.mark(1)
            val first = input.read()
            input.reset()
            if (first == 'O'.code) configure(parse(readLine(input).removePrefix("OK;")))

            var buf = ByteArray(1 shl 20)
            while (running) {
                val len = input.readInt()
                val flags = input.readUnsignedByte()
                if (len < 0 || len > (64 shl 20)) throw Exception("Flux corrompu")
                if (len > buf.size) buf = ByteArray(len * 2)
                input.readFully(buf, 0, len)
                bytesWindow += len + 5
                when {
                    flags and 0x80 != 0 -> {}                                   // bandwidth test filler
                    flags and 0x40 != 0 -> send("ACK")
                    flags and 0x20 != 0 -> configure(parse(String(buf, 0, len)))
                    flags and 0x10 != 0 -> String(buf, 0, len, Charsets.UTF_8).let { listener.onStatus(it) }
                    else -> decoder?.feed(buf, len, flags and 1 != 0)
                }
            }
        } catch (e: Exception) {
            if (running) error = e.message ?: e.javaClass.simpleName
            Log.w(TAG, "stream ended", e)
        } finally {
            running = false
            try { socket?.close() } catch (_: Exception) {}
            decoder?.release()
            listener.onEnded(error)
        }
    }

    private fun configure(c: Map<String, String>) {
        val w = c["w"]?.toIntOrNull() ?: 1920
        val h = c["h"]?.toIntOrNull() ?: 1080
        val hevc = c["codec"] == "hevc"
        decoder?.release()
        decoder = Decoder(if (hevc) MediaFormat.MIMETYPE_VIDEO_HEVC else MediaFormat.MIMETYPE_VIDEO_AVC, w, h, hevc)
        val rate = c["kbps"]?.toIntOrNull()?.let { " · cible %.1f Mb/s".format(it / 1000.0) } ?: ""
        info = "${w}x$h ${c["fps"] ?: "60"} i/s ${if (hevc) "HEVC" else "H.264"}$rate · ${c["enc"] ?: ""}"
        listener.onStarted(info)
    }

    private fun startStats() = Thread({
        var lastRendered = 0
        while (running) {
            try { Thread.sleep(2000) } catch (_: InterruptedException) {}
            val r = rendered
            fps = (r - lastRendered) / 2
            lastRendered = r
            kbps = (bytesWindow * 8 / 2000).toInt()
            bytesWindow = 0
            if (decoder != null) try { send("STAT;fps=$fps;kbps=$kbps;drop=$dropped") } catch (_: Exception) {}
        }
    }, "stats").start()

    /** One hardware decoder instance + its render thread. */
    private inner class Decoder(mime: String, w: Int, h: Int, private val hevc: Boolean) {
        @Volatile private var alive = true
        private val codec = createDecoder(mime, w, h)
        private val render = Thread({ drain() }, "render").apply { start() }
        private var gotKey = false

        fun feed(buf: ByteArray, len: Int, key: Boolean) {
            if (!gotKey) {
                if (!key) return
                gotKey = true
                // First keyframe: hand parameter sets to the decoder as codec config.
                val ps = paramSetsLength(buf, len, hevc)
                if (ps in 1 until len) {
                    queue(buf, 0, ps, MediaCodec.BUFFER_FLAG_CODEC_CONFIG)
                    queue(buf, ps, len - ps, MediaCodec.BUFFER_FLAG_KEY_FRAME)
                    return
                }
            }
            queue(buf, 0, len, if (key) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
        }

        private fun queue(data: ByteArray, off: Int, len: Int, flags: Int) {
            val idx = codec.dequeueInputBuffer(200_000)
            if (idx < 0) { dropped++; return } // decoder saturated
            val b = codec.getInputBuffer(idx)!!
            b.clear()
            if (len > b.capacity()) { codec.queueInputBuffer(idx, 0, 0, 0, 0); dropped++; return }
            b.put(data, off, len)
            codec.queueInputBuffer(idx, 0, len, System.nanoTime() / 1000, flags)
        }

        /** Render every decoded frame immediately; SurfaceFlinger keeps only the newest per vsync. */
        private fun drain() {
            boost()
            val info = MediaCodec.BufferInfo()
            try {
                while (alive) {
                    val idx = codec.dequeueOutputBuffer(info, 50_000)
                    if (idx >= 0) { codec.releaseOutputBuffer(idx, System.nanoTime()); rendered++ }
                }
            } catch (e: Exception) {
                if (alive) { Log.w(TAG, "decoder", e); stop() }
            }
        }

        fun release() {
            alive = false
            try { render.join(500) } catch (_: Exception) {}
            try { codec.stop() } catch (_: Exception) {}
            try { codec.release() } catch (_: Exception) {}
        }

        private fun createDecoder(mime: String, w: Int, h: Int): MediaCodec {
            fun format(extended: Boolean) = MediaFormat.createVideoFormat(mime, w, h).apply {
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(w * h, 2 shl 20))
                if (Build.VERSION.SDK_INT >= 30) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                if (extended) {
                    if (Build.VERSION.SDK_INT >= 23) {
                        setInteger(MediaFormat.KEY_PRIORITY, 0) // realtime
                        setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE.toInt())
                    }
                    // Vendor low-latency switches (Qualcomm, MediaTek, HiSilicon, Amlogic/others).
                    setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
                    setInteger("vendor.qti-ext-dec-picture-order.enable", 1)
                    setInteger("vendor.rtc-ext-dec-low-latency.enable", 1)
                    setInteger("vendor.low-latency.enable", 1)
                    setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-req", 1)
                    setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-rdy", -1)
                }
            }
            for (extended in listOf(true, false)) {
                val dec = MediaCodec.createDecoderByType(mime)
                try {
                    dec.configure(format(extended), surface, null, 0)
                    dec.start()
                    Log.i(TAG, "decoder ${dec.name} extended=$extended")
                    return dec
                } catch (e: Exception) {
                    Log.w(TAG, "configure extended=$extended failed", e)
                    dec.release()
                }
            }
            throw Exception("Décodeur vidéo indisponible")
        }
    }

    companion object {
        const val TAG = "TVScreenPlus"
        const val PORT = 47800

        fun boost() = try { Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY) } catch (_: Exception) {}

        private fun parse(s: String) = s.trim().split(';').associate {
            val i = it.indexOf('='); if (i < 0) it to "" else it.substring(0, i) to it.substring(i + 1)
        }

        private fun readLine(i: DataInputStream): String {
            val sb = StringBuilder()
            while (true) {
                val c = i.read()
                if (c < 0 || c == '\n'.code) break
                sb.append(c.toChar())
                if (sb.length > 1024) break
            }
            return sb.toString()
        }

        /** Byte length of the leading VPS/SPS/PPS NAL units of an Annex-B access unit. */
        fun paramSetsLength(b: ByteArray, len: Int, hevc: Boolean): Int {
            var i = 0
            while (true) {
                var sc = -1
                var j = i
                while (j + 3 < len) {
                    if (b[j].toInt() == 0 && b[j + 1].toInt() == 0 &&
                        (b[j + 2].toInt() == 1 || (b[j + 2].toInt() == 0 && b[j + 3].toInt() == 1))) { sc = j; break }
                    j++
                }
                if (sc < 0) return if (i == 0) 0 else len
                val hdr = sc + if (b[sc + 2].toInt() == 1) 3 else 4
                if (hdr >= len) return i
                val n = b[hdr].toInt() and 0xFF
                val isPs = if (hevc) ((n shr 1) and 0x3F) in 32..34 else (n and 0x1F) in 7..8
                if (!isPs) return sc
                i = hdr + 1
            }
        }
    }
}
