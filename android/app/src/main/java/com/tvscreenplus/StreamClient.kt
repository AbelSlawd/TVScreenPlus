package com.tvscreenplus

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Process
import android.util.Log
import android.view.Surface
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Connects to a PC, receives [u32 len][u8 flags][Annex-B access unit] packets and decodes them
 * with the hardware decoder straight onto the Surface (zero copy, low-latency mode).
 */
class StreamClient(
    private val host: String,
    private val hello: String,
    private val surface: Surface,
    private val onStarted: (w: Int, h: Int, codec: String) -> Unit,
    private val onEnded: (error: String?) -> Unit,
) {
    @Volatile private var running = true
    private var socket: Socket? = null
    private var codec: MediaCodec? = null

    fun start() = Thread({ run() }, "net-decode").start()

    fun stop() {
        running = false
        try { socket?.close() } catch (_: Exception) {}
    }

    private fun run() {
        boost()
        var error: String? = null
        var output: Thread? = null
        try {
            val s = Socket()
            socket = s
            s.tcpNoDelay = true
            s.receiveBufferSize = 2 shl 20
            s.connect(InetSocketAddress(host, PORT), 4000)
            s.getOutputStream().apply { write((hello + "\n").toByteArray()); flush() }
            val input = DataInputStream(BufferedInputStream(s.getInputStream(), 256 shl 10))

            val ok = readLine(input).split(';').associate {
                val i = it.indexOf('='); if (i < 0) it to "" else it.substring(0, i) to it.substring(i + 1)
            }
            if (!ok.containsKey("OK")) throw Exception("Réponse invalide du PC")
            val w = ok["w"]?.toIntOrNull() ?: 1920
            val h = ok["h"]?.toIntOrNull() ?: 1080
            val hevc = ok["codec"] == "hevc"
            val dec = createDecoder(if (hevc) MediaFormat.MIMETYPE_VIDEO_HEVC else MediaFormat.MIMETYPE_VIDEO_AVC, w, h)
            codec = dec
            output = Thread({ drain(dec) }, "render").apply { start() }
            onStarted(w, h, if (hevc) "HEVC" else "H.264")

            var buf = ByteArray(1 shl 20)
            var gotKey = false
            while (running) {
                val len = input.readInt()
                val flags = input.readUnsignedByte()
                if (len <= 0 || len > (64 shl 20)) throw Exception("Flux corrompu")
                if (len > buf.size) buf = ByteArray(len * 2)
                input.readFully(buf, 0, len)
                val key = flags and 1 != 0
                if (!gotKey) {
                    if (!key) continue
                    gotKey = true
                    // First keyframe: hand parameter sets to the decoder as codec config.
                    val ps = paramSetsLength(buf, len, hevc)
                    if (ps in 1 until len) {
                        queue(dec, buf, 0, ps, MediaCodec.BUFFER_FLAG_CODEC_CONFIG)
                        queue(dec, buf, ps, len - ps, MediaCodec.BUFFER_FLAG_KEY_FRAME)
                        continue
                    }
                }
                queue(dec, buf, 0, len, if (key) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
            }
        } catch (e: Exception) {
            if (running) error = e.message ?: e.javaClass.simpleName
            Log.w(TAG, "stream ended", e)
        } finally {
            running = false
            try { socket?.close() } catch (_: Exception) {}
            output?.join(500)
            try { codec?.stop() } catch (_: Exception) {}
            try { codec?.release() } catch (_: Exception) {}
            onEnded(error)
        }
    }

    private fun queue(dec: MediaCodec, data: ByteArray, off: Int, len: Int, flags: Int) {
        val idx = dec.dequeueInputBuffer(200_000)
        if (idx < 0) return // decoder saturated: drop (should never happen at 60 fps)
        val b = dec.getInputBuffer(idx)!!
        b.clear()
        if (len > b.capacity()) { dec.queueInputBuffer(idx, 0, 0, 0, 0); return }
        b.put(data, off, len)
        dec.queueInputBuffer(idx, 0, len, System.nanoTime() / 1000, flags)
    }

    /** Render every decoded frame immediately; SurfaceFlinger keeps only the newest per vsync. */
    private fun drain(dec: MediaCodec) {
        boost()
        val info = MediaCodec.BufferInfo()
        try {
            while (running) {
                val idx = dec.dequeueOutputBuffer(info, 50_000)
                if (idx >= 0) {
                    if (Build.VERSION.SDK_INT >= 21) dec.releaseOutputBuffer(idx, System.nanoTime())
                    else dec.releaseOutputBuffer(idx, true)
                }
            }
        } catch (e: Exception) {
            if (running) { Log.w(TAG, "decoder", e); stop() }
        }
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

    companion object {
        const val TAG = "TVScreenPlus"
        const val PORT = 47800

        fun boost() = try { Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY) } catch (_: Exception) {}

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
                // find start code at i
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
