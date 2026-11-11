package com.tvscreenplus

import android.app.Activity
import android.content.Context
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
        setContentView(root)
    }

    override fun onResume() {
        super.onResume()
        hideSystemUi()
        startDiscovery()
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
        val hello = "TVSP1;name=${Build.MODEL.replace(';', ' ')};w=$w;h=$h;hevc=${if (hasHwDecoder(MediaFormat.MIMETYPE_VIDEO_HEVC)) 1 else 0}"
        lateinit var c: StreamClient
        c = StreamClient(ip, hello, surfaceView.holder.surface,
            onStarted = { sw, sh, codec ->
                ui.post {
                    if (client !== c) return@post
                    prefs.edit().putString("last", ip).apply()
                    overlay.visibility = View.GONE
                    acquireWifiLock()
                    if (Build.VERSION.SDK_INT >= 30) try { surfaceView.holder.surface.setFrameRate(60f, android.view.Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE) } catch (_: Exception) {}
                    android.util.Log.i(StreamClient.TAG, "streaming ${sw}x$sh $codec from $name")
                }
            },
            onEnded = { err ->
                ui.post {
                    if (client !== c) return@post
                    client = null
                    releaseWifiLock()
                    overlay.visibility = View.VISIBLE
                    status.text = if (err != null) "Déconnecté : $err" else "Déconnecté"
                    list.getChildAt(0)?.requestFocus()
                    if (autoConnect) ui.postDelayed({ tryAutoConnect() }, 2000)
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

    private fun hasHwDecoder(mime: String): Boolean = try {
        MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { ci ->
            !ci.isEncoder && ci.supportedTypes.any { it.equals(mime, true) } &&
                (if (Build.VERSION.SDK_INT >= 29) ci.isHardwareAccelerated
                 else !ci.name.startsWith("OMX.google.") && !ci.name.startsWith("c2.android."))
        }
    } catch (_: Exception) { false }

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
