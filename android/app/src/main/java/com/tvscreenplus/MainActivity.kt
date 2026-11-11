package com.tvscreenplus

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.media.MediaFormat
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.TypedValue
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.ImageView
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.SocketTimeoutException

class MainActivity : Activity() {
    private class Pc(val ip: String, var name: String, var busy: Boolean, var seen: Long)

    private val ui = Handler(Looper.getMainLooper())
    private val pcs = linkedMapOf<String, Pc>()
    private lateinit var surfaceView: SurfaceView
    private lateinit var overlay: FrameLayout
    private lateinit var stats: TextView
    /** Mouse cursor drawn by the TV over the video (positions straight from the PC: no video latency). */
    private lateinit var cursorView: ImageView
    @Volatile private var curX = 0
    @Volatile private var curY = 0
    @Volatile private var curVisible = false
    @Volatile private var curStreamW = 1920
    @Volatile private var curStreamH = 1080
    private var curHotX = 0
    private var curHotY = 0
    private var curBmpW = 0
    private var curBmpH = 0
    private val cursorPending = java.util.concurrent.atomic.AtomicBoolean(false)
    private val cursorTick = Runnable { cursorPending.set(false); applyCursor() }
    // Main page widgets (rebuilt when the language changes).
    private var status: TextView? = null
    private var list: LinearLayout? = null
    private var updateButton: Button? = null
    private var inSettings = false
    private var surfaceReady = false
    @Volatile private var client: StreamClient? = null
    /** Gamepads forwarded to the PC while streaming (null when the PC doesn't take them). */
    @Volatile private var gamepads: Gamepads? = null
    private var autoConnect = true
    @Volatile private var discovering = false
    private var wifiLock: WifiManager.WifiLock? = null
    private val prefs by lazy { getSharedPreferences("tvsp", Context.MODE_PRIVATE) }
    private var release: Updater.Release? = null
    private var lastUpdateCheck = 0L
    @Volatile private var testing = false
    @Suppress("DEPRECATION")
    private val versionCode by lazy { packageManager.getPackageInfo(packageName, 0).versionCode }
    private val versionName by lazy { try { packageManager.getPackageInfo(packageName, 0).versionName ?: "" } catch (_: Exception) { "" } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        L.choice = prefs.getString("lang", "system") ?: "system"
        autoConnect = prefs.getBoolean("autoconnect", true)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        preferBestDisplayMode()

        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        surfaceView = SurfaceView(this)
        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(h: SurfaceHolder) { surfaceReady = true; tryAutoConnect() }
            override fun surfaceChanged(h: SurfaceHolder, f: Int, w: Int, ht: Int) {}
            override fun surfaceDestroyed(h: SurfaceHolder) { surfaceReady = false; disconnect(null) }
        })
        root.addView(surfaceView, FrameLayout.LayoutParams(-1, -1))
        cursorView = ImageView(this).apply { scaleType = ImageView.ScaleType.FIT_XY; visibility = View.GONE }
        root.addView(cursorView, FrameLayout.LayoutParams(1, 1, Gravity.TOP or Gravity.START))
        overlay = FrameLayout(this).apply { setBackgroundColor(BG) }
        root.addView(overlay, FrameLayout.LayoutParams(-1, -1))
        // Live statistics, toggled with OK / Menu while streaming.
        stats = TextView(this).apply {
            textSize = 15f; setTextColor(Color.WHITE); setBackgroundColor(Color.argb(170, 0, 0, 0))
            setPadding(dp(14), dp(10), dp(14), dp(10)); visibility = View.GONE
        }
        root.addView(stats, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.START).apply { setMargins(dp(24), dp(24), 0, 0) })
        setContentView(root)
        // Ask the TV for its "game" picture/sound path (ALLM): less processing, so less image and sound delay.
        if (Build.VERSION.SDK_INT >= 30) try { window.setPreferMinimalPostProcessing(true) } catch (_: Exception) {}
        showMain()
        handleInstallIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleInstallIntent(intent)
    }

    private fun handleInstallIntent(i: Intent?) {
        if (i?.action != Updater.ACTION_STATUS) return
        Updater.onStatus(this, i)?.let { setStatus(it) }
    }

    private fun setStatus(s: String) { status?.text = s }

    // ---------------- pages ----------------
    /** Header: title + version on the left, gear on the right. */
    private fun header(page: LinearLayout, title: String, gear: Boolean) {
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        row.addView(TextView(this).apply { text = title; textSize = 30f; setTextColor(Color.WHITE) })
        row.addView(TextView(this).apply {
            text = "  v$versionName"; textSize = 16f; setTextColor(Color.rgb(144, 202, 249))
        }, LinearLayout.LayoutParams(0, -2, 1f))
        if (gear) row.addView(button("⚙  " + L.t("settings"), wide = false) { showSettings() })
        else row.addView(button("←  " + L.t("back"), wide = false) { showMain() })
        page.addView(row, LinearLayout.LayoutParams(-1, -2))
    }

    private fun page(): LinearLayout {
        overlay.removeAllViews()
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val p = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(48), dp(32), dp(48), dp(24))
        }
        scroll.addView(p)
        overlay.addView(scroll, FrameLayout.LayoutParams(-1, -1))
        return p
    }

    private fun showMain() {
        inSettings = false
        val p = page()
        header(p, "TVScreenPlus", gear = true)
        status = text(L.t("searching"), 18f, Color.rgb(144, 202, 249)).also { p.addView(it) }
        updateButton = button(release?.let { L.t("update_to", it.name) } ?: "") { startUpdate() }.apply {
            visibility = if (release != null && release!!.code > versionCode) View.VISIBLE else View.GONE
        }
        p.addView(updateButton, LinearLayout.LayoutParams(dp(560), -2))
        list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(0, dp(16), 0, dp(16)) }
        p.addView(list, LinearLayout.LayoutParams(dp(560), -2))

        val manual = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        val ipField = EditText(this).apply {
            hint = L.t("ip_hint"); setSingleLine(); setTextColor(Color.WHITE); setHintTextColor(Color.GRAY)
            setText(prefs.getString("manual", ""))
        }
        manual.addView(ipField, LinearLayout.LayoutParams(dp(320), -2))
        manual.addView(button(L.t("connect"), wide = false) {
            val ip = ipField.text.toString().trim()
            if (ip.isNotEmpty()) { prefs.edit().putString("manual", ip).apply(); autoConnect = true; connect(ip, ip) }
        })
        p.addView(manual)
        p.addView(text(L.t("help"), 14f, Color.GRAY))
        refreshList()
    }

    private fun showSettings() {
        inSettings = true
        status = null; list = null; updateButton = null
        val p = page()
        header(p, L.t("settings"), gear = false)
        val w = dp(720)

        fun section(title: String) = p.addView(text(title, 15f, Color.rgb(144, 202, 249)).apply { gravity = Gravity.START; setPadding(dp(4), dp(18), 0, dp(2)) }, LinearLayout.LayoutParams(w, -2))
        fun note(s: String) = text(s, 14f, Color.LTGRAY).apply { gravity = Gravity.START; setPadding(dp(4), 0, 0, dp(4)) }.also { p.addView(it, LinearLayout.LayoutParams(w, -2)) }
        /** A button that cycles through options; label shows "Setting : value". */
        fun choice(label: String, values: List<String>, names: List<String>, key: String, def: String, onChange: (String) -> Unit = {}) {
            var cur = values.indexOf(prefs.getString(key, def)).coerceAtLeast(0)
            val b = button("$label :  ${names[cur]}") {}
            b.setOnClickListener {
                cur = (cur + 1) % values.size
                prefs.edit().putString(key, values[cur]).apply()
                b.text = "$label :  ${names[cur]}"
                onChange(values[cur])
            }
            p.addView(b, LinearLayout.LayoutParams(w, -2))
        }
        fun toggle(label: String, key: String, def: Boolean, onChange: (Boolean) -> Unit = {}) {
            fun lbl(v: Boolean) = "$label :  ${L.t(if (v) "on" else "off")}"
            val b = button(lbl(prefs.getBoolean(key, def))) {}
            b.setOnClickListener {
                val v = !prefs.getBoolean(key, def)
                prefs.edit().putBoolean(key, v).apply()
                b.text = lbl(v); onChange(v)
            }
            p.addView(b, LinearLayout.LayoutParams(w, -2))
        }

        section(L.t("lang"))
        choice(L.t("lang"), listOf("system") + L.CODES, listOf(L.t("lang_system")) + L.NAMES, "lang", "system") {
            L.choice = it
            ui.post { showSettings() } // rebuild in the new language
        }

        section(L.t("priority"))
        choice(L.t("priority"), listOf("auto", "smooth", "quality"), listOf(L.t("prio_auto"), L.t("prio_smooth"), L.t("prio_quality")), "pref", "auto")
        choice(L.t("maxres"), listOf("0", "720", "1080", "1440", "2160"), listOf(L.t("auto"), "720p", "1080p", "1440p", "4K"), "maxh", "0")
        toggle(L.t("autoconnect"), "autoconnect", true) { autoConnect = it }
        toggle(L.t("showstats"), "showstats", false)

        section(L.t("audio_pads"))
        toggle(L.t("audio"), "audio", true)
        toggle(L.t("pads"), "pads", true)
        note(L.t("pads_note"))
        val sync = button(L.t("av_offset", prefs.getInt("av_offset", 0))) {}
        sync.setOnClickListener {
            prefs.edit().putInt("av_offset", 0).apply()
            sync.text = L.t("av_offset", 0)
        }
        p.addView(sync, LinearLayout.LayoutParams(w, -2))
        note(L.t("av_offset_note"))

        section(L.t("hwtest"))
        val testNote = note(hwSummary())
        val testBtn = button(L.t("hwtest_run")) {}
        testBtn.setOnClickListener {
            if (testing) return@setOnClickListener
            testing = true
            disconnect(null)
            Thread({
                val r = try {
                    HardwareTest.run { step -> ui.post { testNote.text = L.t("hwtest_running", step) } }
                } catch (e: Exception) { null }
                ui.post {
                    testing = false
                    if (r != null) prefs.edit().putInt("hw_hevc", r.hevc).putInt("hw_avc", r.avc)
                        .putInt("hw_lat", r.latencyMs).putInt("hw_ver", versionCode)
                        .putString("hw_log", r.report.joinToString("\n")).apply()
                    testNote.text = (if (r != null) L.t("hwtest_done") + "\n" else "") + hwSummary()
                }
            }, "hwtest").start()
        }
        p.addView(testBtn, LinearLayout.LayoutParams(w, -2))

        section(L.t("check_updates"))
        val updNote = note("")
        p.addView(button(L.t("check_updates")) {
            updNote.text = L.t("checking")
            Thread({
                val r = try { Updater.latest() } catch (_: Exception) { null }
                ui.post {
                    when {
                        r == null -> updNote.text = L.t("update_check_failed")
                        r.code <= versionCode -> updNote.text = L.t("uptodate", versionName)
                        else -> { release = r; showMain(); startUpdate() }
                    }
                }
            }, "update-check").start()
        }, LinearLayout.LayoutParams(w, -2))

        section(L.t("about"))
        p.addView(button(L.t("forget")) {
            prefs.edit().remove("last").remove("manual").apply()
            pcs.clear()
            note(L.t("forgotten"))
        }, LinearLayout.LayoutParams(w, -2))
        note(L.t("about_text", versionName, Build.MODEL, Build.VERSION.RELEASE, localIp() ?: "?", networkLabel()))
        overlay.post { p.getChildAt(2)?.requestFocus() } // first setting
    }

    private fun hwSummary(): String {
        if (prefs.getInt("hw_ver", 0) == 0) return L.t("hwtest_never")
        fun res(h: Int) = when (h) { 0 -> L.t("none"); 2160 -> "4K"; else -> "${h}p" } + if (h > 0) " 60 i/s" else ""
        val lat = prefs.getInt("hw_lat", -1)
        return L.t("hwtest_result", res(prefs.getInt("hw_hevc", 0)), res(prefs.getInt("hw_avc", 0)), if (lat >= 0) "$lat" else "?")
    }

    // ---------------- updates ----------------
    private fun checkUpdate() {
        if (System.currentTimeMillis() - lastUpdateCheck < 6 * 3600_000L) return
        lastUpdateCheck = System.currentTimeMillis()
        Thread({
            val r = try { Updater.latest() } catch (e: Exception) { null }
            if (r == null || r.code <= versionCode) return@Thread
            ui.post {
                release = r
                updateButton?.apply { text = L.t("update_to", r.name); visibility = View.VISIBLE }
                // Offer it once per version, only when not streaming.
                if (client == null && !inSettings && prefs.getInt("offered", 0) < r.code) {
                    prefs.edit().putInt("offered", r.code).apply()
                    startUpdate()
                }
            }
        }, "update-check").start()
    }

    private fun startUpdate() {
        val r = release ?: return
        disconnect(null)
        autoConnect = false
        setStatus(L.t("dl_update", 0))
        Thread({
            try {
                Updater.install(this, r) { p -> ui.post { setStatus(L.t("dl_update", p)) } }
            } catch (e: Exception) {
                ui.post { setStatus(L.t("update_failed", e.message ?: "?")) }
            }
        }, "update").start()
    }

    private val statsTick = object : Runnable {
        override fun run() {
            val c = client
            if (c != null && stats.visibility == View.VISIBLE) {
                val alat = c.audioLatencyMs
                val extra = (if (alat >= 0) "\n" + L.t("stats_audio", alat) else "") +
                    (if (c.audioOutputMs >= 0) "\n" + L.t("stats_sync", c.audioOutputMs, c.appliedVideoDelayMs, c.audioExtraMs, c.userOffsetMs) else "") +
                    (gamepads?.count?.takeIf { it > 0 }?.let { "\n" + L.t("stats_pads", it) } ?: "")
                stats.text = c.info + "\n" + L.t("stats", c.kbps / 1000.0, c.fps, c.dropped) + extra
                ui.postDelayed(this, 1000)
            }
        }
    }

    override fun onResume() {
        super.onResume()
        hideSystemUi()
        startDiscovery()
        checkUpdate()
    }

    override fun onPause() {
        super.onPause()
        discovering = false
        disconnect(null)
    }

    // ---------------- discovery ----------------
    private fun startDiscovery() {
        if (discovering) return
        discovering = true
        Thread({
            try {
                DatagramSocket().use { s ->
                    s.broadcast = true
                    s.soTimeout = 300
                    val q = "TVSP?".toByteArray()
                    val buf = ByteArray(512)
                    var lastSend = 0L
                    while (discovering) {
                        val now = System.currentTimeMillis()
                        if (now - lastSend > 1000 && client == null) {
                            lastSend = now
                            for (a in broadcastAddresses()) try { s.send(DatagramPacket(q, q.size, a, 47801)) } catch (_: Exception) {}
                        }
                        try {
                            val p = DatagramPacket(buf, buf.size)
                            s.receive(p)
                            val lines = String(p.data, 0, p.length).split('\n')
                            if (lines[0] != "TVSP!") continue
                            val kv = lines.drop(1).associate { val i = it.indexOf('='); it.substring(0, maxOf(i, 0)) to it.substring(i + 1) }
                            val ip = p.address.hostAddress ?: continue
                            ui.post { found(ip, kv["name"] ?: ip, kv["busy"] == "1") }
                        } catch (_: SocketTimeoutException) {}
                        ui.post { expire() }
                    }
                }
            } catch (e: Exception) {
                ui.post { setStatus(L.t("net_error", e.message ?: "?")) }
                discovering = false
            }
        }, "discovery").start()
    }

    private fun broadcastAddresses(): List<InetAddress> {
        val out = mutableListOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        try {
            val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            @Suppress("DEPRECATION")
            val nets = if (Build.VERSION.SDK_INT >= 23) listOfNotNull(cm.activeNetwork) else cm.allNetworks.toList()
            for (n in nets) for (la in cm.getLinkProperties(n)?.linkAddresses.orEmpty()) {
                val a = la.address as? Inet4Address ?: continue
                val ip = a.address.fold(0) { acc, b -> (acc shl 8) or (b.toInt() and 0xFF) }
                val mask = if (la.prefixLength == 0) 0 else -1 shl (32 - la.prefixLength)
                val bc = ip or mask.inv()
                out += InetAddress.getByAddress(byteArrayOf((bc ushr 24).toByte(), (bc ushr 16).toByte(), (bc ushr 8).toByte(), bc.toByte()))
            }
        } catch (_: Exception) {}
        return out
    }

    private fun found(ip: String, name: String, busy: Boolean) {
        val pc = pcs[ip]
        if (pc == null) pcs[ip] = Pc(ip, name, busy, System.currentTimeMillis()).also { refreshList() }
        else { pc.seen = System.currentTimeMillis(); if (pc.busy != busy || pc.name != name) { pc.busy = busy; pc.name = name; refreshList() } }
        tryAutoConnect()
    }

    private fun expire() {
        val now = System.currentTimeMillis()
        if (pcs.values.removeAll { now - it.seen > 5000 }) refreshList()
    }

    private fun refreshList() {
        val l = list ?: return
        l.removeAllViews()
        for (pc in pcs.values) l.addView(button("${pc.name}   (${pc.ip})${if (pc.busy) "  – " + L.t("busy") else ""}") { autoConnect = true; connect(pc.ip, pc.name) })
        if (client == null) setStatus(L.t(if (pcs.isEmpty()) "searching" else "choose"))
        if (currentFocus == null || currentFocus?.parent === l) l.getChildAt(0)?.requestFocus()
    }

    /** Plug & play: reconnect to the last PC, or to the only PC found. */
    private fun tryAutoConnect() {
        if (!autoConnect || client != null || !surfaceReady || inSettings || testing) return
        val last = prefs.getString("last", null)
        val target = pcs[last] ?: pcs.values.singleOrNull()?.takeIf { !it.busy && last == null }
        if (target != null) connect(target.ip, target.name)
    }

    // ---------------- streaming ----------------
    /** Everything the PC needs to adapt: panel, refresh rate, decoder limits (tested or declared), network, preferences. */
    private fun hello(): String {
        val (w, h) = panelSize()
        val tested = prefs.getInt("hw_ver", 0) > 0
        val hevc = if (tested) prefs.getInt("hw_hevc", 0) else Codecs.declaredMaxHeight(MediaFormat.MIMETYPE_VIDEO_HEVC)
        val avc = if (tested) prefs.getInt("hw_avc", 0) else Codecs.declaredMaxHeight(MediaFormat.MIMETYPE_VIDEO_AVC)
        val (net, band, link) = network()
        return "TVSP1;v=2;app=$versionCode;lang=${L.lang};name=${Build.MODEL.replace(';', ' ')};w=$w;h=$h;hz=${refreshHz()}" +
            ";hevc=$hevc;avc=$avc;tested=${if (tested) 1 else 0};lat=${prefs.getInt("hw_lat", -1)}" +
            ";net=$net;band=$band;link=$link;pref=${prefs.getString("pref", "auto")};maxh=${prefs.getString("maxh", "0")}" +
            ";audio=${if (prefs.getBoolean("audio", true)) 1 else 0};pads=${if (prefs.getBoolean("pads", true)) 1 else 0}"
    }

    /** Diagnostic lines written to the PC log on every connection (no ADB needed to see what the TV does). */
    private fun diagnostics(): List<String> {
        val soc = if (Build.VERSION.SDK_INT >= 31) " SoC ${Build.SOC_MANUFACTURER} ${Build.SOC_MODEL}" else ""
        val lines = arrayListOf(
            "device ${Build.MANUFACTURER} ${Build.MODEL} (${Build.DEVICE}) Android ${Build.VERSION.RELEASE} SDK ${Build.VERSION.SDK_INT}$soc app $versionName",
            "panel ${panelSize().let { "${it.first}x${it.second}" }} @${refreshHz()} Hz, declared HEVC ${Codecs.declaredMaxHeight(MediaFormat.MIMETYPE_VIDEO_HEVC)}p60, H.264 ${Codecs.declaredMaxHeight(MediaFormat.MIMETYPE_VIDEO_AVC)}p60",
        )
        lines += DisplayCaps.report(this) // display modes + 120 fps decoding: is 120 Hz streaming possible?
        if (Build.VERSION.SDK_INT >= 30) lines += "display minimal post-processing (game mode) supported: ${try { windowManager.defaultDisplay.isMinimalPostProcessingSupported } catch (_: Exception) { "?" }}"
        val hwVer = prefs.getInt("hw_ver", 0)
        if (hwVer == 0) lines += "hwtest never run"
        else {
            val log = prefs.getString("hw_log", null)
            if (log.isNullOrEmpty()) lines += "hwtest run with app build $hwVer (no details: run the test again)"
            else log.split('\n').forEach { lines += "hwtest $it" }
        }
        return lines
    }

    @Suppress("DEPRECATION")
    private fun refreshHz() = Math.round(windowManager.defaultDisplay.refreshRate)

    private fun connect(ip: String, name: String) {
        if (!surfaceReady || testing) return
        if (inSettings) showMain()
        disconnect(null)
        setStatus(L.t("connecting", name))
        val hz = refreshHz()
        lateinit var c: StreamClient
        c = StreamClient(ip, hello(), diagnostics(), surfaceView.holder.surface, object : StreamClient.Listener {
            override fun onStatus(text: String) {
                ui.post { if (client === c && overlay.visibility == View.VISIBLE) setStatus("$name : $text") }
            }

            override fun onStarted(info: String) {
                ui.post {
                    if (client !== c) return@post
                    prefs.edit().putString("last", ip).apply()
                    overlay.visibility = View.GONE
                    acquireWifiLock()
                    if (Build.VERSION.SDK_INT >= 30) try { surfaceView.holder.surface.setFrameRate(hz.toFloat(), android.view.Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE) } catch (_: Exception) {}
                    if (prefs.getBoolean("showstats", false)) { stats.visibility = View.VISIBLE; ui.removeCallbacks(statsTick); statsTick.run() }
                    // Gamepads go to the PC only if it takes them (older PC versions: they keep driving the TV).
                    if (c.pads && gamepads == null) gamepads = Gamepads(this@MainActivity, c).also { it.start() }
                    android.util.Log.i(StreamClient.TAG, "streaming $info from $name")
                }
            }

            override fun onRumble(slot: Int, large: Int, small: Int) {
                if (client === c) gamepads?.rumble(slot, large, small)
            }

            override fun onCursorShape(bitmap: android.graphics.Bitmap, hotX: Int, hotY: Int) {
                ui.post {
                    if (client !== c) return@post
                    cursorView.setImageBitmap(bitmap)
                    curHotX = hotX; curHotY = hotY; curBmpW = bitmap.width; curBmpH = bitmap.height
                    applyCursor()
                }
            }

            override fun onCursorMove(x: Int, y: Int, visible: Boolean, streamW: Int, streamH: Int) {
                curX = x; curY = y; curVisible = visible && client === c; curStreamW = streamW; curStreamH = streamH
                if (cursorPending.compareAndSet(false, true)) ui.post(cursorTick) // latest position only
            }

            override fun onEnded(error: String?) {
                ui.post {
                    if (client !== c) return@post
                    client = null
                    stopGamepads()
                    releaseWifiLock()
                    stats.visibility = View.GONE
                    overlay.visibility = View.VISIBLE
                    setStatus(if (error != null) L.t("disconnected_err", error) else L.t("disconnected"))
                    list?.getChildAt(0)?.requestFocus()
                    // Stuck stream (no image for 6 s): reconnect to the same PC at once, whatever the settings.
                    if (c.stalled) ui.postDelayed({ if (client == null && !inSettings) connect(ip, name) }, 1500)
                    else if (autoConnect) ui.postDelayed({ tryAutoConnect() }, 2000)
                }
            }
        })
        c.userOffsetMs = prefs.getInt("av_offset", 0)
        client = c
        c.start()
    }

    private fun disconnect(reason: String?) {
        val c = client ?: return
        client = null
        stopGamepads()
        c.stop()
        releaseWifiLock()
        stats.visibility = View.GONE
        overlay.visibility = View.VISIBLE
        setStatus(reason ?: L.t("disconnected"))
    }

    private fun stopGamepads() { gamepads?.stop(); gamepads = null }

    /** While streaming, gamepad input goes to the PC first; the remote keeps its keys (Back, OK, Menu). */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val g = gamepads
        if (g != null && client != null && overlay.visibility == View.GONE && g.onKey(event)) return true
        return super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        val g = gamepads
        if (g != null && client != null && overlay.visibility == View.GONE && g.onMotion(event)) return true
        return super.dispatchGenericMotionEvent(event)
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK && client != null) {
            autoConnect = false // user wants out: no auto-reconnect until they pick a PC
            disconnect(L.t("disconnected_back"))
            list?.getChildAt(0)?.requestFocus()
            return true
        }
        if (keyCode == KeyEvent.KEYCODE_BACK && inSettings) { showMain(); return true }
        if (client != null && overlay.visibility == View.GONE &&
            (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER || keyCode == KeyEvent.KEYCODE_MENU || keyCode == KeyEvent.KEYCODE_INFO)) {
            stats.visibility = if (stats.visibility == View.VISIBLE) View.GONE else View.VISIBLE
            ui.removeCallbacks(statsTick)
            statsTick.run()
            return true
        }
        val c = client
        if (c != null && overlay.visibility == View.GONE &&
            (keyCode == KeyEvent.KEYCODE_DPAD_LEFT || keyCode == KeyEvent.KEYCODE_DPAD_RIGHT)) {
            // Fine lip-sync by ear: < sound earlier, > sound later (10 ms steps, remembered).
            val v = (prefs.getInt("av_offset", 0) + if (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) 10 else -10).coerceIn(-200, 300)
            prefs.edit().putInt("av_offset", v).apply()
            c.userOffsetMs = v
            Toast.makeText(this, L.t("av_offset_now", v), Toast.LENGTH_SHORT).show()
            return true
        }
        if (c != null && overlay.visibility == View.GONE &&
            (keyCode == KeyEvent.KEYCODE_DPAD_UP || keyCode == KeyEvent.KEYCODE_DPAD_DOWN)) {
            // Latency mode: up = fluid (steady 60 fps, sound in sync), down = game (minimum latency).
            if (!c.modeSupported) {
                Toast.makeText(this, L.t("mode_unsupported"), Toast.LENGTH_LONG).show()
            } else {
                val game = keyCode == KeyEvent.KEYCODE_DPAD_DOWN
                if (game != c.gameMode) c.requestMode(game)
                Toast.makeText(this, L.t(if (game) "mode_game" else "mode_fluid"), Toast.LENGTH_SHORT).show()
            }
            return true
        }
        if (client == null && keyCode == KeyEvent.KEYCODE_MENU && !inSettings) { showSettings(); return true }
        return super.onKeyDown(keyCode, event)
    }

    /** Places the cursor over the video: stream pixels -> view pixels, hotspot on the pointed spot. */
    private fun applyCursor() {
        if (!curVisible || client == null || overlay.visibility == View.VISIBLE || curBmpW == 0 || surfaceView.width == 0) {
            cursorView.visibility = View.GONE; return
        }
        val sx = surfaceView.width.toFloat() / curStreamW.coerceAtLeast(1)
        val sy = surfaceView.height.toFloat() / curStreamH.coerceAtLeast(1)
        val w = Math.round(curBmpW * sx).coerceAtLeast(1)
        val h = Math.round(curBmpH * sy).coerceAtLeast(1)
        val lp = cursorView.layoutParams
        if (lp.width != w || lp.height != h) { lp.width = w; lp.height = h; cursorView.layoutParams = lp }
        cursorView.translationX = (curX - curHotX) * sx
        cursorView.translationY = (curY - curHotY) * sy
        cursorView.visibility = View.VISIBLE
    }

    // ---------------- helpers ----------------
    private fun panelSize(): Pair<Int, Int> {
        @Suppress("DEPRECATION") val d = windowManager.defaultDisplay
        if (Build.VERSION.SDK_INT >= 23) {
            val m = d.mode
            return maxOf(m.physicalWidth, m.physicalHeight) to minOf(m.physicalWidth, m.physicalHeight)
        }
        val p = android.graphics.Point()
        d.getRealSize(p)
        return maxOf(p.x, p.y) to minOf(p.x, p.y)
    }

    /** (eth|wifi|other, Wi-Fi band GHz or 0, link speed Mb/s or 0). */
    private fun network(): Triple<String, String, Int> = try {
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val caps = if (Build.VERSION.SDK_INT >= 23) cm.getNetworkCapabilities(cm.activeNetwork) else null
        when {
            caps?.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) == true -> Triple("eth", "0", 0)
            caps == null || caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> {
                val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                @Suppress("DEPRECATION") val wi = wm.connectionInfo
                val f = if (Build.VERSION.SDK_INT >= 21) wi.frequency else 0
                val band = when { f >= 5925 -> "6"; f >= 4900 -> "5"; f > 0 -> "2.4"; else -> "0" }
                Triple("wifi", band, maxOf(wi.linkSpeed, 0))
            }
            else -> Triple("other", "0", 0)
        }
    } catch (_: Exception) { Triple("other", "0", 0) }

    private fun networkLabel(): String {
        val (net, band, link) = network()
        return when (net) { "eth" -> L.t("ethernet"); "wifi" -> L.t("wifi", band, link); else -> "?" }
    }

    private fun localIp(): String? = try {
        java.net.NetworkInterface.getNetworkInterfaces().toList().flatMap { it.inetAddresses.toList() }
            .firstOrNull { it is Inet4Address && !it.isLoopbackAddress }?.hostAddress
    } catch (_: Exception) { null }

    /** Ask for the native-resolution 60 Hz mode (TVs often idle at 50 Hz → judder). */
    private fun preferBestDisplayMode() {
        if (Build.VERSION.SDK_INT < 23) return
        @Suppress("DEPRECATION") val d = windowManager.defaultDisplay
        val cur = d.mode
        val best = d.supportedModes
            .filter { it.physicalWidth == cur.physicalWidth && it.physicalHeight == cur.physicalHeight }
            .minByOrNull { Math.abs(it.refreshRate - 60f) } ?: return
        window.attributes = window.attributes.apply { preferredDisplayModeId = best.modeId }
    }

    private fun acquireWifiLock() {
        if (wifiLock != null) return
        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            @Suppress("DEPRECATION")
            val mode = if (Build.VERSION.SDK_INT >= 29) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
            wifiLock = wm.createWifiLock(mode, "tvsp").apply { setReferenceCounted(false); acquire() }
        } catch (_: Exception) {}
    }

    private fun releaseWifiLock() { try { wifiLock?.release() } catch (_: Exception) {}; wifiLock = null }

    @Suppress("DEPRECATION")
    private fun hideSystemUi() {
        window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
    }

    private fun dp(v: Int) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), resources.displayMetrics).toInt()

    private fun text(s: String, size: Float, color: Int) = TextView(this).apply {
        text = s; textSize = size; setTextColor(color); gravity = Gravity.CENTER; setPadding(0, dp(6), 0, dp(6))
    }

    private fun button(label: String, wide: Boolean = true, onClick: () -> Unit) = Button(this).apply {
        text = label; isAllCaps = false; textSize = 18f; setTextColor(Color.WHITE)
        isFocusable = true
        gravity = if (wide) Gravity.START or Gravity.CENTER_VERTICAL else Gravity.CENTER
        setPadding(dp(20), dp(10), dp(20), dp(10))
        background = GradientDrawable().apply { cornerRadius = dp(10).toFloat(); setColor(BTN) }
        setOnFocusChangeListener { v, f ->
            (v.background as GradientDrawable).setColor(if (f) BTN_FOCUS else BTN)
            v.scaleX = if (f) 1.03f else 1f; v.scaleY = v.scaleX
        }
        setOnClickListener { onClick() }
        layoutParams = LinearLayout.LayoutParams(if (wide) -1 else -2, -2).apply { setMargins(dp(4), dp(6), dp(4), dp(6)) }
    }

    companion object {
        private val BG = Color.rgb(13, 27, 42)
        private val BTN = Color.rgb(30, 60, 90)
        private val BTN_FOCUS = Color.rgb(30, 136, 229)
    }
}
