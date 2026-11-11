package com.tvscreenplus

import android.graphics.Bitmap
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.Process
import android.util.Log
import android.view.Surface
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executors

/**
 * Connects to a PC, receives [u32 len][u8 flags][payload] packets and decodes the video with the
 * hardware decoder straight onto the Surface (zero copy, low-latency mode).
 * flags: 0x80 filler (bandwidth test), 0x40 ping (answer "ACK"), 0x20 config, 0x10 status text,
 * 0x08 extension (payload[0]: 1 = PC sound, 2 = gamepad rumble; only sent when the hello announced
 * audio=1 / pads=1), otherwise an Annex-B access unit (1 = keyframe).
 * Upstream: "ACK", "STAT;fps=;kbps=;drop=;dec=;decmax=;wait=[;alat=;aund=;adrop=;ajit=]" every 2 s,
 * "LOG;<text>" lines that the PC writes to its log (diagnostics, decoder in use, errors),
 * and "PADON / PADOFF / PAD" gamepad lines (see Gamepads).
 */
class StreamClient(
    private val host: String,
    private val hello: String,
    private val diagnostics: List<String>,
    private val surface: Surface,
    private val listener: Listener,
) {
    interface Listener {
        fun onStatus(text: String)
        fun onStarted(info: String)
        fun onEnded(error: String?)
        /** Network thread: a PC game set the rumble motors of the gamepad in this slot. */
        fun onRumble(slot: Int, large: Int, small: Int) {}
        /** Cursor drawn by the TV (the PC leaves it out of the video): new shape, hotspot in bitmap pixels. */
        fun onCursorShape(bitmap: Bitmap, hotX: Int, hotY: Int) {}
        /** Cursor position in stream pixels (stream size given), or hidden. Called often, from network threads. */
        fun onCursorMove(x: Int, y: Int, visible: Boolean, streamW: Int, streamH: Int) {}
    }

    @Volatile private var running = true
    private var socket: Socket? = null
    private var out: OutputStream? = null
    @Volatile private var decoder: Decoder? = null
    /** The PC accepted our gamepads / plays its sound here (from its config packet). */
    @Volatile var pads = false
    @Volatile var audioOn = false
    @Volatile private var audio: AudioPlayer? = null
    /** Upstream writes from the UI thread (gamepads) must not touch the socket there. */
    private val upstreamThread = Executors.newSingleThreadExecutor()
    private val padLines = arrayOfNulls<String>(Gamepads.MAX)
    private var padFlushQueued = false
    /** Delay of the TV audio buffer (ms), -1 when no sound is playing. */
    val audioLatencyMs: Int get() = audio?.latencyMs ?: -1
    /** Measured write -> speaker delay of the sound (ms), -1 when unknown. */
    val audioOutputMs: Int get() = audio?.outputMs ?: -1
    val audioExtraMs: Int get() = audio?.extraDelayMs ?: 0

    // ---- A/V sync ----
    /** User correction from the remote (ms): + = sound later, - = sound earlier. */
    @Volatile var userOffsetMs = 0
    /** PC-side delay of the image compared with the sound (capture + encoding), from the PC config; -1 = old PC. */
    @Volatile private var pcVideoMs = -1
    @Volatile private var pcAudioMs = 5
    /** Latency mode of the PC (from its config): game = minimum latency, else fluid. Older PCs don't say. */
    @Volatile var gameMode = false
    @Volatile var modeSupported = false

    /** Remote up/down: ask the PC for fluid or game mode (it restarts its encoder and sends a new config). */
    fun requestMode(game: Boolean) {
        gameMode = game
        post("MODE;" + if (game) "game" else "fluid")
    }

    /** The PC lets us delay the image for the sound (avsync=1); 0 in its game mode: image latency first. */
    @Volatile private var syncImage = true
    /** Sound from the PC over UDP: never stuck behind a large video frame in the TCP stream. */
    private var udp: DatagramSocket? = null
    private val audioLock = Any()
    @Volatile private var frameMs = 17
    /** Smoothed decoder delay (us). */
    @Volatile private var decAvgUs = 12_000.0
    /** Delay applied to the image so that it matches the sound (ms), target and current value. */
    @Volatile var videoDelayMs = 0
        private set
    @Volatile private var appliedDelayNs = 0L
    private class VFrame(val data: ByteArray, val key: Boolean, val arrival: Long, val cap: Long)

    // ---- Measured latency (PC clock = ms since 1970 mod 2^31, from pings, frames and sound packets) ----
    /** TV time (nanoTime ms) minus PC time, from the fastest recent pings: one-way network time included (< 1 ms on a LAN). */
    @Volatile private var clockOffset = Long.MIN_VALUE
    private val offsetSamples = ArrayDeque<Long>()
    private val e2eLock = Any()
    private var e2eSum = 0L
    private var e2eCount = 0
    private var e2eMax = 0L
    /** Smoothed capture -> screen delay of the image (ms, video delay line included), -1 = unknown. */
    @Volatile private var e2eAvg = -1.0
    /** Smoothed capture -> arrival delay of the sound packets (ms), -1 = unknown. */
    @Volatile private var audioTransit = -1.0
    /** How much the PC's own sound output lags its loopback capture (the player syncs its video to that output). */
    @Volatile private var pcPlaybackMs = 15
    private val capMap = LinkedHashMap<Long, Long>()
    private var renderThread: HandlerThread? = null
    @Volatile private var streamW = 1920
    @Volatile private var streamH = 1080

    /** Ping from the PC carrying its clock: keep the smallest TV - PC difference of the last 30 s. */
    private fun clockSample(pc: Long) {
        val d = System.nanoTime() / 1_000_000 - pc
        synchronized(offsetSamples) {
            val ref = if (clockOffset == Long.MIN_VALUE) d else clockOffset
            offsetSamples.addLast(ref + wrap31(d - ref))
            while (offsetSamples.size > 30) offsetSamples.removeFirst()
            clockOffset = offsetSamples.minOrNull() ?: d
        }
    }

    /** PC time -> TV time (ms); null until the first ping. */
    private fun ageMs(pcTime: Long, tvNowMs: Long): Long? {
        val off = clockOffset
        if (off == Long.MIN_VALUE) return null
        return wrap31(tvNowMs - off - pcTime)
    }

    /** Last time (nanoTime) a frame's capture -> screen delay was measured, and rejected samples (diagnosis). */
    @Volatile private var e2eAt = 0L
    @Volatile private var audioAt = 0L
    private var e2eRejected = 0
    // Latency measurement diagnosis, logged once 10 s after each new stream config.
    @Volatile private var dFramesTimed = 0
    @Volatile private var dQueued = 0
    @Volatile private var dRendered = 0
    @Volatile private var dMatched = 0
    @Volatile private var dOk = 0
    @Volatile private var diagAt = 0L

    /** A frame reached the screen (TV compositor timestamp). */
    private fun onRendered(ptsUs: Long, nanoTime: Long) {
        dRendered++
        val cap = synchronized(capMap) { capMap.remove(ptsUs) } ?: return
        dMatched++
        val e2e = ageMs(cap, nanoTime / 1_000_000) ?: return
        if (e2e !in 0L..2000L) {
            if (e2eRejected++ % 300 == 0) upstream { log("latency sample rejected: $e2e ms (clock offset $clockOffset)") }
            return
        }
        dOk++
        synchronized(e2eLock) { e2eSum += e2e; e2eCount++; e2eMax = maxOf(e2eMax, e2e) }
        // Image delay WITHOUT our own delay line, frame by frame: the A/V sync must never feed on its own output
        // (v1.0.21 on Wi-Fi: a stale average minus a growing delay ran the image delay up to 250 ms).
        val base = e2e - appliedDelayNs / 1_000_000
        e2eAvg = if (e2eAvg < 0) base.toDouble() else e2eAvg * 0.9 + base * 0.1
        e2eAt = System.nanoTime()
    }

    /** Sound packet [1][fmt][ch][0][u32 rate][u32 PC capture ms]…: measure capture -> arrival, then play it. */
    private fun audioPacket(b: ByteArray, len: Int) {
        if (len > 12) ageMs(u32(b, 8), System.nanoTime() / 1_000_000)?.let { t ->
            if (t in 0L..1000L) { audioTransit = if (audioTransit < 0) t.toDouble() else audioTransit * 0.95 + t * 0.05; audioAt = System.nanoTime() }
        }
        player().offer(b, len)
    }

    /** Cursor packets: 3 = position [3][visible][u16 x][u16 y][u32 PC ms], 4 = shape [4][u16 w][u16 h][u16 hx][u16 hy][BGRA]. */
    private fun cursorPacket(b: ByteArray, len: Int) {
        if (!cursorOn) return
        if (b[0].toInt() == 3 && len >= 6) {
            listener.onCursorMove(u16(b, 2), u16(b, 4), b[1].toInt() and 1 != 0, streamW, streamH)
        } else if (b[0].toInt() == 4 && len >= 9) {
            val w = u16(b, 1); val h = u16(b, 3)
            if (w !in 1..256 || h !in 1..256 || len < 9 + w * h * 4) return
            val px = IntArray(w * h)
            var o = 9
            for (i in px.indices) {
                px[i] = ((b[o + 3].toInt() and 0xFF) shl 24) or ((b[o + 2].toInt() and 0xFF) shl 16) or
                    ((b[o + 1].toInt() and 0xFF) shl 8) or (b[o].toInt() and 0xFF)
                o += 4
            }
            listener.onCursorShape(Bitmap.createBitmap(px, w, h, Bitmap.Config.ARGB_8888), u16(b, 5), u16(b, 7))
        }
    }

    /** The PC leaves the cursor out of the video and sends it separately (cursor=1 in its config). */
    @Volatile private var cursorOn = false
    private val vq = ArrayDeque<VFrame>()
    private val vLock = java.lang.Object()
    private var feeding = false
    val appliedVideoDelayMs: Int get() = (appliedDelayNs / 1_000_000).toInt()

    // Live statistics (overlay + reports to the PC).
    @Volatile var info = ""
    @Volatile var fps = 0
    @Volatile var kbps = 0
    @Volatile var dropped = 0
    private var bytesWindow = 0L
    @Volatile private var rendered = 0

    // Live decoder measurements over the current 2 s window (guarded by decLock).
    private val decLock = Any()
    private var decSumUs = 0L
    private var decCount = 0
    private var decMaxUs = 0L
    private var waitNs = 0L // time spent waiting for a free decoder input buffer (decoder saturated)

    fun start() = Thread({ run() }, "net-decode").start()

    fun stop() {
        running = false
        try { socket?.close() } catch (_: Exception) {}
    }

    private fun send(line: String) {
        val o = out ?: return
        synchronized(o) { o.write((line + "\n").toByteArray()); o.flush() }
    }

    /** Line for the PC log (single line, short). */
    private fun log(text: String) = try { send("LOG;" + text.replace('\n', ' ').take(900)) } catch (_: Exception) {}

    /** Any thread: send a line from the upstream thread, in order. */
    fun post(line: String) = upstream { send(line) }

    /** Any thread: latest state of a gamepad; states not yet sent are replaced (never a backlog of stale input). */
    fun padState(slot: Int, line: String) {
        synchronized(padLines) {
            padLines[slot] = line
            if (padFlushQueued) return
            padFlushQueued = true
        }
        upstream {
            val lines = synchronized(padLines) {
                padFlushQueued = false
                val l = padLines.filterNotNull()
                padLines.fill(null)
                l
            }
            if (lines.isNotEmpty()) send(lines.joinToString("\n")) // one write for all pads
        }
    }

    private fun upstream(task: () -> Unit) {
        if (!running) return
        try { upstreamThread.execute { try { task() } catch (_: Exception) {} } } catch (_: Exception) {} // rejected after shutdown
    }

    private fun run() {
        boost()
        var error: String? = null
        try {
            val s = Socket()
            socket = s
            s.tcpNoDelay = true
            s.receiveBufferSize = 512 shl 10 // ~80 ms at 50 Mb/s: a stalled decoder can't hide seconds of video
            s.connect(InetSocketAddress(host, PORT), 4000)
            out = s.getOutputStream()
            Thread({ feedLoop() }, "video-delay").start()
            val port = startUdp()
            renderThread = HandlerThread("frame-rendered").apply { start() }
            // ts=1: frames carry their capture time; cursor=1: we draw the cursor.
            send((if (port > 0) "$hello;audioudp=$port" else hello) + ";ts=1;cursor=1")
            diagnostics.forEach { log(it) }
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
                if (len < 0 || len > (64 shl 20)) throw Exception(L.t("corrupt"))
                if (len > buf.size) buf = ByteArray(len * 2)
                input.readFully(buf, 0, len)
                bytesWindow += len + 5
                when {
                    flags and 0x80 != 0 -> {}                                   // bandwidth test filler
                    flags and 0x40 != 0 -> { if (len >= 4) clockSample(u32(buf, 0)); send("ACK") }
                    flags and 0x20 != 0 -> configure(parse(String(buf, 0, len)))
                    flags and 0x10 != 0 -> String(buf, 0, len, Charsets.UTF_8).let { listener.onStatus(it) }
                    flags and 0x08 != 0 -> extension(buf, len)
                    else -> {
                        // 0x04: the payload starts with the frame's capture time (PC clock).
                        if (flags and 0x04 != 0 && len > 4) {
                            val cap = u32(buf, 0)
                            System.arraycopy(buf, 4, buf, 0, len - 4)
                            video(buf, len - 4, flags and 1 != 0, cap)
                        } else video(buf, len, flags and 1 != 0, -1)
                    }
                }
            }
        } catch (e: Exception) {
            if (running) { error = e.message ?: e.javaClass.simpleName; log("stream error: ${e.javaClass.simpleName}: ${e.message}") }
            Log.w(TAG, "stream ended", e)
        } finally {
            running = false
            try { socket?.close() } catch (_: Exception) {}
            upstreamThread.shutdownNow()
            try { renderThread?.quitSafely() } catch (_: Exception) {}
            listener.onCursorMove(0, 0, false, streamW, streamH)
            decoder?.release()
            audio?.release()
            audio = null
            synchronized(vLock) { vq.clear(); vLock.notifyAll() }
            try { udp?.close() } catch (_: Exception) {}
            listener.onEnded(error)
        }
    }

    /**
     * Video frame from the network. Fed to the decoder at once, unless the A/V sync delays the image:
     * then the compressed frame waits in [vq] (cheap: no decoder buffer is held) and the feeder thread
     * hands it over when due. The delay moves by at most 2 ms per frame, so changes are invisible.
     */
    private fun video(buf: ByteArray, len: Int, key: Boolean, cap: Long) {
        if (cap >= 0) dFramesTimed++
        synchronized(vLock) {
            if (videoDelayMs > 0 || appliedDelayNs > 0 || vq.isNotEmpty() || feeding) {
                vq.addLast(VFrame(buf.copyOf(len), key, System.nanoTime(), cap))
                vLock.notifyAll()
                return
            }
        }
        decoder?.feed(buf, len, key, cap)
    }

    private fun feedLoop() {
        boost()
        while (running) {
            val f = synchronized(vLock) {
                if (vq.isEmpty()) { vLock.wait(50); return@synchronized null }
                val head = vq.first()
                val waitNs = head.arrival + appliedDelayNs - System.nanoTime()
                if (waitNs > 500_000) { vLock.wait(waitNs / 1_000_000, (waitNs % 1_000_000).toInt()); return@synchronized null }
                feeding = true
                // Slew the applied delay toward the target: at most 2 ms per frame.
                val target = videoDelayMs * 1_000_000L
                appliedDelayNs = when {
                    appliedDelayNs < target -> minOf(target, appliedDelayNs + 2_000_000L)
                    appliedDelayNs > target -> maxOf(target, appliedDelayNs - 2_000_000L)
                    else -> appliedDelayNs
                }
                vq.removeFirst()
            } ?: continue
            try { decoder?.feed(f.data, f.data.size, f.key, f.cap) } catch (_: Exception) {} // decoder replaced meanwhile
            synchronized(vLock) { feeding = false }
        }
    }

    /**
     * Every 250 ms: make the sound and the image leave the TV together.
     * image = PC capture/encoding delay (from the PC) + decoder + one frame to reach the screen;
     * sound = PC capture delay + measured write -> speaker delay (TV audio hardware included).
     * Sound later -> delay the image; sound earlier -> delay the sound. Plus the user's correction.
     */
    /** The sound player, created on first use (TCP or UDP thread). */
    private fun player(): AudioPlayer = synchronized(audioLock) { audio ?: AudioPlayer { log(it) }.also { it.game = gameMode; audio = it } }

    /** UDP socket for the sound; returns its port (0 = unavailable: the PC then keeps the sound in TCP). */
    private fun startUdp(): Int = try {
        val u = DatagramSocket(0)
        u.receiveBufferSize = 256 shl 10
        udp = u
        Thread({
            boost()
            val b = ByteArray(8192)
            val pk = DatagramPacket(b, b.size)
            try {
                while (running) {
                    pk.length = b.size // receive() shrinks it to the last datagram's size
                    u.receive(pk)
                    if (!running) break
                    when (b[0].toInt()) {
                        1 -> if (pk.length > 12 && audioOn) audioPacket(b, pk.length)
                        3 -> cursorPacket(b, pk.length)
                    }
                }
            } catch (_: Exception) {}
        }, "audio-udp").start()
        u.localPort
    } catch (e: Exception) { log("audio: no UDP socket (${e.javaClass.simpleName}), sound stays in TCP"); 0 }

    private fun syncTick() {
        if (!audioOn) { videoDelayMs = 0; return } // no sound: never delay the image
        val a = audio ?: return
        val out = a.outputMs
        if (out < 0) return
        val late: Int
        val e2e = e2eAvg; val transit = audioTransit
        val now = System.nanoTime()
        val measured = e2e >= 0 && transit >= 0
        if (measured && (now - e2eAt > 1_500_000_000L || now - audioAt > 1_500_000_000L)) return // stale: hold
        if (measured) {
            // Measured: capture -> screen for the image (without our own delay line), capture -> speaker for the
            // sound (without our extra delay). The sound captured pcPlaybackMs before a frame belongs to it.
            val imageMs = e2e
            val soundMs = transit + (out - a.extraDelayMs).coerceAtLeast(0)
            late = (soundMs - pcPlaybackMs - imageMs).toInt() - userOffsetMs
        } else {
            // Older PC: estimates (PC delays from its config).
            val pcVideo = pcVideoMs.coerceAtLeast(0)
            val imageMs = pcVideo + (decAvgUs / 1000).toInt() + frameMs
            val soundMs = pcAudioMs + (out - a.extraDelayMs).coerceAtLeast(0)
            late = soundMs - imageMs - userOffsetMs // > 0: sound behind the image
        }
        val newVideo = if (syncImage) late.coerceIn(0, 150) else 0
        val newExtra = (-late).coerceIn(0, 200)
        if (Math.abs(newVideo - videoDelayMs) >= 4) videoDelayMs = newVideo
        if (Math.abs(newExtra - a.extraDelayMs) >= 4) a.extraDelayMs = newExtra
    }

    /** Flag 0x08 packets: payload[0] = 1 PC sound, 2 gamepad rumble [slot][large motor][small motor]. */
    private fun extension(b: ByteArray, len: Int) {
        if (len < 1) return
        when (b[0].toInt()) {
            1 -> if (audioOn) audioPacket(b, len)
            3, 4 -> cursorPacket(b, len)
            2 -> if (len >= 4) listener.onRumble(b[1].toInt() and 0xFF, b[2].toInt() and 0xFF, b[3].toInt() and 0xFF)
        }
    }

    private fun configure(c: Map<String, String>) {
        val w = c["w"]?.toIntOrNull() ?: 1920
        val h = c["h"]?.toIntOrNull() ?: 1080
        val hevc = c["codec"] == "hevc"
        decoder?.release()
        decoder = try {
            Decoder(if (hevc) MediaFormat.MIMETYPE_VIDEO_HEVC else MediaFormat.MIMETYPE_VIDEO_AVC, w, h, hevc)
        } catch (e: Exception) { log("decoder creation failed ${w}x$h: ${e.javaClass.simpleName}: ${e.message}"); throw e }
        log("decoder ${Codecs.lastDecoder}")
        val rate = c["kbps"]?.toIntOrNull()?.let { " · ${L.t("target")} %.1f Mb/s".format(it / 1000.0) } ?: ""
        info = "${w}x$h ${c["fps"] ?: "60"} i/s ${if (hevc) "HEVC" else "H.264"}$rate · ${c["enc"] ?: ""}"
        // Older PC versions don't send these keys: no sound, gamepads keep driving the TV.
        audioOn = c["audio"] == "1"
        pcVideoMs = c["vpc"]?.toIntOrNull() ?: -1
        pcAudioMs = c["apc"]?.toIntOrNull() ?: 5
        syncImage = c["avsync"] != "0"
        modeSupported = c.containsKey("mode")
        gameMode = c["mode"] == "game"
        audio?.game = gameMode
        frameMs = 1000 / (c["fps"]?.toIntOrNull()?.coerceIn(10, 240) ?: 60)
        streamW = w; streamH = h
        dFramesTimed = 0; dQueued = 0; dRendered = 0; dMatched = 0; dOk = 0; diagAt = System.nanoTime() + 10_000_000_000L
        cursorOn = c["cursor"] == "1"
        pcPlaybackMs = c["lpc"]?.toIntOrNull() ?: 15
        if (!cursorOn) listener.onCursorMove(0, 0, false, w, h)
        synchronized(vLock) { vq.clear() } // frames of the previous stream must not reach the new decoder
        pads = c["pads"] == "1"
        listener.onStarted(info)
    }

    private fun startStats() = Thread({
        var lastRendered = 0
        while (running) {
            for (i in 0 until 8) {
                try { Thread.sleep(250) } catch (_: InterruptedException) {}
                try { syncTick() } catch (_: Exception) {}
            }
            val r = rendered
            fps = (r - lastRendered) / 2
            lastRendered = r
            kbps = (bytesWindow * 8 / 2000).toInt()
            bytesWindow = 0
            val (dec, decMax, wait) = synchronized(decLock) {
                val r = Triple(if (decCount > 0) decSumUs / decCount / 1000 else -1L, decMaxUs / 1000, waitNs * 100 / 2_000_000_000L)
                decSumUs = 0; decCount = 0; decMaxUs = 0; waitNs = 0
                r
            }
            if (diagAt != 0L && System.nanoTime() > diagAt) {
                diagAt = 0L
                log("latency diag (10 s): frames with capture time $dFramesTimed, queued $dQueued, rendered callbacks $dRendered, " +
                    "matched $dMatched, accepted $dOk, rejected $e2eRejected, clock ${if (clockOffset == Long.MIN_VALUE) "unknown" else "ok"}, " +
                    "image delay $appliedVideoDelayMs ms, API ${Build.VERSION.SDK_INT}")
            }
            val a = (audio?.stats() ?: "") + ";vdel=$appliedVideoDelayMs;avoff=$userOffsetMs"
            val e = synchronized(e2eLock) {
                val r = if (e2eCount > 0) ";e2e=${e2eSum / e2eCount};e2emax=$e2eMax" else ""
                e2eSum = 0; e2eCount = 0; e2eMax = 0
                r
            }
            if (decoder != null) try { send("STAT;fps=$fps;kbps=$kbps;drop=$dropped;dec=$dec;decmax=$decMax;wait=$wait$a$e") } catch (_: Exception) {}
        }
    }, "stats").start()

    /** One hardware decoder instance + its render thread. */
    private inner class Decoder(mime: String, w: Int, h: Int, private val hevc: Boolean) {
        @Volatile private var alive = true
        private val codec = Codecs.lowLatencyDecoder(mime, w, h, surface)
        private val render = Thread({ drain() }, "render").apply { start() }
        private var gotKey = false
        private var cap = -1L

        init {
            // When each frame really reaches the screen (Android 6+): measured capture -> screen latency.
            if (Build.VERSION.SDK_INT >= 23) try {
                val l = renderThread?.looper
                if (l != null) codec.setOnFrameRenderedListener({ _, ptsUs, nano -> onRendered(ptsUs, nano) }, Handler(l))
            } catch (_: Exception) {}
        }

        fun feed(buf: ByteArray, len: Int, key: Boolean, captureMs: Long) {
            cap = captureMs
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
            val t0 = System.nanoTime()
            val idx = codec.dequeueInputBuffer(200_000)
            val waited = System.nanoTime() - t0
            if (waited > 1_000_000) synchronized(decLock) { waitNs += waited }
            if (idx < 0) { dropped++; return } // decoder saturated
            val b = codec.getInputBuffer(idx)!!
            b.clear()
            if (len > b.capacity()) { codec.queueInputBuffer(idx, 0, 0, 0, 0); dropped++; return }
            b.put(data, off, len)
            val pts = System.nanoTime() / 1000
            codec.queueInputBuffer(idx, 0, len, pts, flags)
            if (cap >= 0 && flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0) synchronized(capMap) {
                capMap[pts] = cap
                dQueued++
                if (capMap.size > 120) capMap.remove(capMap.keys.first())
                Unit
            }
        }

        /** Render every decoded frame immediately; SurfaceFlinger keeps only the newest per vsync. */
        private fun drain() {
            boost()
            val info = MediaCodec.BufferInfo()
            var lastShown = 0L
            try {
                while (alive) {
                    val idx = codec.dequeueOutputBuffer(info, 50_000)
                    if (idx >= 0) {
                        // Image freeze diagnosis: a gap of more than 250 ms between two displayed frames.
                        val shownAt = System.nanoTime()
                        val gap = (shownAt - lastShown) / 1_000_000
                        if (lastShown != 0L && gap > 250) {
                            val q = synchronized(vLock) { vq.size }
                            upstream { log("video stall $gap ms (image delay $appliedVideoDelayMs ms, $q frames waiting, decoder ${(decAvgUs / 1000).toInt()} ms)") }
                        }
                        lastShown = shownAt
                        // presentationTimeUs = time the frame was queued: input -> output delay of the decoder.
                        val lat = System.nanoTime() / 1000 - info.presentationTimeUs
                        codec.releaseOutputBuffer(idx, System.nanoTime()); rendered++
                        if (lat in 0L..2_000_000L) {
                            synchronized(decLock) { decSumUs += lat; decCount++; decMaxUs = maxOf(decMaxUs, lat) }
                            decAvgUs = decAvgUs * 0.98 + lat * 0.02
                        }
                    }
                }
            } catch (e: Exception) {
                if (alive) { Log.w(TAG, "decoder", e); log("decoder error: ${e.javaClass.simpleName}: ${e.message}"); stop() }
            }
        }

        fun release() {
            alive = false
            try { render.join(500) } catch (_: Exception) {}
            try { codec.stop() } catch (_: Exception) {}
            try { codec.release() } catch (_: Exception) {}
        }
    }

    companion object {
        /** 31-bit clock difference as a small signed number (the PC clock wraps every 24.8 days). */
        fun wrap31(x: Long): Long { val v = x and 0x7FFFFFFF; return if (v >= 0x40000000) v - 0x80000000L else v }
        fun u32(b: ByteArray, o: Int): Long =
            ((b[o].toLong() and 0xFF) shl 24) or ((b[o + 1].toLong() and 0xFF) shl 16) or ((b[o + 2].toLong() and 0xFF) shl 8) or (b[o + 3].toLong() and 0xFF)
        fun u16(b: ByteArray, o: Int): Int = ((b[o].toInt() and 0xFF) shl 8) or (b[o + 1].toInt() and 0xFF)

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
