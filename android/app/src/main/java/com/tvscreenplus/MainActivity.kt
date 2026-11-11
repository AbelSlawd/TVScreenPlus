package com.tvscreenplus

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.media.MediaCodecList
import android.media.MediaFormat
import android.net.ConnectivityManager
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.TypedValue
import android.view.Gravity
import android.view.KeyEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
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
    private lateinit var overlay: LinearLayout
    private lateinit var status: TextView
    private lateinit var list: LinearLayout
    private var surfaceReady = false
    @Volatile private var client: StreamClient? = null
    private var autoConnect = true
    @Volatile private var discovering = false
    private var wifiLock: WifiManager.WifiLock? = null
    private val prefs by lazy { getSharedPreferences("tvsp", Context.MODE_PRIVATE) }
    private lateinit var stats: TextView
    private lateinit var updateButton: Button
    private var release: Updater.Release? = null
    private var lastUpdateCheck = 0L
    @Suppress("DEPRECATION")
    private val versionCode by lazy { packageManager.getPackageInfo(packageName, 0).versionCode }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
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

        overlay = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(48), dp(40), dp(48), dp(24))
            setBackgroundColor(Color.rgb(13, 27, 42))
        }
        overlay.addView(text("TVScreenPlus", 34f, Color.WHITE))
        status = text("Recherche des ordinateurs sur le réseau…", 18f, Color.rgb(144, 202, 249))
        overlay.addView(status)
        updateButton = button("Mettre à jour") { startUpdate() }.apply { visibility = View.GONE }
        overlay.addView(updateButton, LinearLayout.LayoutParams(dp(520), -2))
        list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(0, dp(24), 0, dp(24)) }
        overlay.addView(list, LinearLayout.LayoutParams(dp(520), -2))

        val manual = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        val ipField = EditText(this).apply {
            hint = "IP du PC (si non détecté)"; setSingleLine(); setTextColor(Color.WHITE); setHintTextColor(Color.GRAY)
            setText(prefs.getString("manual", ""))
        }
        manual.addView(ipField, LinearLayout.LayoutParams(dp(300), -2))
        manual.addView(button("Connecter") {
            val ip = ipField.text.toString().trim()
            if (ip.isNotEmpty()) { prefs.edit().putString("manual", ip).apply(); connect(ip, ip) }
        })
        overlay.addView(manual)
        overlay.addView(text("Installez TVScreenPlus sur le PC Windows (même réseau). Retour = déconnecter.", 14f, Color.GRAY))
        root.addView(overlay, FrameLayout.LayoutParams(-1, -1))
        // Live statistics, toggled with OK / Menu while streaming.
        stats = TextView(this).apply {
            textSize = 15f; setTextColor(Color.WHITE); setBackgroundColor(Color.argb(170, 0, 0, 0))
            setPadding(dp(14), dp(10), dp(14), dp(10)); visibility = View.GONE
        }
        root.addView(stats, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.START).apply { setMargins(dp(24), dp(24), 0, 0) })
        setContentView(root)
        val vn = try { packageManager.getPackageInfo(packageName, 0).versionName } catch (_: Exception) { "" }
        overlay.addView(text("Version $vn", 12f, Color.DKGRAY))
        handleInstallIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleInstallIntent(intent)
    }

    private fun handleInstallIntent(i: Intent?) {
        if (i?.action != Updater.ACTION_STATUS) return
        Updater.onStatus(this, i)?.let { status.text = it }
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
                updateButton.text = "Mettre à jour vers la version ${r.name}"
                updateButton.visibility = View.VISIBLE
                // Offer it once per version, only when not streaming.
                if (client == null && prefs.getInt("offered", 0) < r.code) {
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
        status.text = "Téléchargement de la mise à jour…"
        Thread({
            try {
                Updater.install(this, r) { p -> ui.post { status.text = "Téléchargement de la mise à jour… $p %" } }
            } catch (e: Exception) {
                ui.post { status.text = "Échec de la mise à jour : ${e.message}" }
            }
        }, "update").start()
    }

    private val statsTick = object : Runnable {
        override fun run() {
            val c = client
            if (c != null && stats.visibility == View.VISIBLE) {
                stats.text = "${c.info}\nReçu : %.1f Mb/s · Affiché : ${c.fps} i/s · Pertes : ${c.dropped}".format(c.kbps / 1000.0)
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
                ui.post { status.text = "Erreur réseau : ${e.message}" }
                discovering = false
            }
        }, "discovery").start()
    }

    private fun broadcastAddresses(): List<InetAddress> {
        val out = mutableListOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        try {
            val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
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
        list.removeAllViews()
        for (pc in pcs.values) list.addView(button("${pc.name}   (${pc.ip})${if (pc.busy) "  – occupé" else ""}") { autoConnect = true; connect(pc.ip, pc.name) })
        if (client == null) status.text = if (pcs.isEmpty()) "Recherche des ordinateurs sur le réseau…" else "Choisissez un ordinateur :"
        if (currentFocus == null || currentFocus?.parent === list) list.getChildAt(0)?.requestFocus()
    }

    /** Plug & play: reconnect to the last PC, or to the only PC found. */
    private fun tryAutoConnect() {
        if (!autoConnect || client != null || !surfaceReady) return
        val last = prefs.getString("last", null)
        val target = pcs[last] ?: pcs.values.singleOrNull()?.takeIf { !it.busy && last == null }
        if (target != null) connect(target.ip, target.name)
    }

    // ---------------- streaming ----------------
    private fun connect(ip: String, name: String) {
        if (!surfaceReady) return
        disconnect(null)
        status.text = "Connexion à $name…"
        val (w, h) = panelSize()
        @Suppress("DEPRECATION")
        val hz = Math.round(windowManager.defaultDisplay.refreshRate)
        val hello = "TVSP1;v=2;app=$versionCode;name=${Build.MODEL.replace(';', ' ')};w=$w;h=$h;hz=$hz" +
            ";hevc=${maxDecodeHeight(MediaFormat.MIMETYPE_VIDEO_HEVC)};avc=${maxDecodeHeight(MediaFormat.MIMETYPE_VIDEO_AVC)}"
        lateinit var c: StreamClient
        c = StreamClient(ip, hello, surfaceView.holder.surface, object : StreamClient.Listener {
            override fun onStatus(text: String) {
                ui.post { if (client === c && overlay.visibility == View.VISIBLE) status.text = "$name : $text" }
            }

            override fun onStarted(info: String) {
                ui.post {
                    if (client !== c) return@post
                    prefs.edit().putString("last", ip).apply()
                    overlay.visibility = View.GONE
                    acquireWifiLock()
                    if (Build.VERSION.SDK_INT >= 30) try { surfaceView.holder.surface.setFrameRate(hz.toFloat(), android.view.Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE) } catch (_: Exception) {}
                    android.util.Log.i(StreamClient.TAG, "streaming $info from $name")
                }
            }

            override fun onEnded(error: String?) {
                ui.post {
                    if (client !== c) return@post
                    client = null
                    releaseWifiLock()
                    stats.visibility = View.GONE
                    overlay.visibility = View.VISIBLE
                    status.text = if (error != null) "Déconnecté : $error" else "Déconnecté"
                    list.getChildAt(0)?.requestFocus()
                    if (autoConnect) ui.postDelayed({ tryAutoConnect() }, 2000)
                }
            }
        })
        client = c
        c.start()
    }

    private fun disconnect(reason: String?) {
        val c = client ?: return
        client = null
        c.stop()
        releaseWifiLock()
        stats.visibility = View.GONE
        overlay.visibility = View.VISIBLE
        status.text = reason ?: "Déconnecté"
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK && client != null) {
            autoConnect = false // user wants out: no auto-reconnect until they pick a PC
            disconnect("Déconnecté – choisissez un ordinateur pour reprendre")
            list.getChildAt(0)?.requestFocus()
            return true
        }
        if (client != null && overlay.visibility == View.GONE &&
            (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER || keyCode == KeyEvent.KEYCODE_MENU || keyCode == KeyEvent.KEYCODE_INFO)) {
            stats.visibility = if (stats.visibility == View.VISIBLE) View.GONE else View.VISIBLE
            ui.removeCallbacks(statsTick)
            statsTick.run()
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

    // ---------------- helpers ----------------
    private fun panelSize(): Pair<Int, Int> {
        @Suppress("DEPRECATION") val d = windowManager.defaultDisplay
        if (Build.VERSION.SDK_INT >= 23) {
            val m = d.mode
            return maxOf(m.physicalWidth, m.physicalHeight) to minOf(m.physicalWidth, m.physicalHeight)
        }
        val p = android.graphics.Point(); d.getRealSize(p)
        return maxOf(p.x, p.y) to minOf(p.x, p.y)
    }

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

    /** Highest 16:9 height the hardware decoder handles at 60 fps (0 = no hardware decoder). */
    private fun maxDecodeHeight(mime: String): Int = try {
        var best = 0
        for (ci in MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos) {
            if (ci.isEncoder || ci.supportedTypes.none { it.equals(mime, true) }) continue
            val hw = if (Build.VERSION.SDK_INT >= 29) ci.isHardwareAccelerated
                     else !ci.name.startsWith("OMX.google.") && !ci.name.startsWith("c2.android.")
            if (!hw) continue
            val vc = ci.getCapabilitiesForType(mime).videoCapabilities ?: continue
            val h = listOf(2160, 1440, 1080, 900, 720).firstOrNull { h ->
                try { vc.areSizeAndRateSupported(((h * 16 / 9) + 7) and 7.inv(), h, 60.0) } catch (_: Exception) { false }
            } ?: 1080 // capabilities not reported: assume a standard 1080p60 TV decoder
            best = maxOf(best, h)
        }
        best
    } catch (_: Exception) { 0 }

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

    private fun button(label: String, onClick: () -> Unit) = Button(this).apply {
        text = label; isAllCaps = false; textSize = 18f; setTextColor(Color.WHITE)
        isFocusable = true
        background = GradientDrawable().apply { cornerRadius = dp(10).toFloat(); setColor(Color.rgb(30, 60, 90)) }
        setOnFocusChangeListener { v, f ->
            (v.background as GradientDrawable).setColor(if (f) Color.rgb(30, 136, 229) else Color.rgb(30, 60, 90))
            v.scaleX = if (f) 1.04f else 1f; v.scaleY = v.scaleX
        }
        setOnClickListener { onClick() }
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { setMargins(0, dp(6), 0, dp(6)) }
    }
}
