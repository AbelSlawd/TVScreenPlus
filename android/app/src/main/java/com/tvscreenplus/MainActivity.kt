package com.tvscreenplus

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
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
import android.widget.ProgressBar
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
    private lateinit var root: FrameLayout
    /** Quick menu over the video while streaming (Menu key), or null. */
    private var quickMenu: View? = null
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
    private var spinner: ProgressBar? = null
    private var list: LinearLayout? = null
    private var updateButton: Button? = null
    private var inSettings = false
    private var surfaceReady = false
    @Volatile private var client: StreamClient? = null
    /** Gamepads connected to the TV: always tracked (menu), forwarded to the PC while streaming if it takes them. */
    private val gamepads by lazy { Gamepads(this, prefs) }
    // Controllers page: selected player slot, test mode (gamepad input stays on the page), live drawings.
    private var padsPage = false
    private var padSel = -1
    private var padTest = false
    private var padsShown = ""
    private var padBig: PadView? = null
    private val padMinis = HashMap<Int, PadView>()
    private var padBar: LinearLayout? = null
    private var autoConnect = true
    @Volatile private var discovering = false
    /** Bumped at each start: a discovery thread still waiting on its socket after a quick pause/resume stops. */
    @Volatile private var discoveryRun = 0
    private var discoveryAt = 0L
    private var wifiLock: WifiManager.WifiLock? = null
    private val prefs by lazy { getSharedPreferences("tvsp", Context.MODE_PRIVATE) }
    private val pairing by lazy { Pairing(prefs) }
    /** Switch 2 controllers (Bluetooth LE), kept across streams; scanning while the setting is on. */
    private val switch2 by lazy {
        Switch2(this, { msg -> client?.post("LOG;$msg") ?: android.util.Log.i(StreamClient.TAG, msg) },
            { msg -> Toast.makeText(this, msg, Toast.LENGTH_SHORT).show() }).also { it.pads = gamepads }
    }
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

        root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        surfaceView = SurfaceView(this)
        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(h: SurfaceHolder) { surfaceReady = true; tryAutoConnect() }
            override fun surfaceChanged(h: SurfaceHolder, f: Int, w: Int, ht: Int) {}
            override fun surfaceDestroyed(h: SurfaceHolder) { surfaceReady = false; disconnect(null) }
        })
        root.addView(surfaceView, FrameLayout.LayoutParams(-1, -1))
        applyZoom()
        cursorView = ImageView(this).apply { scaleType = ImageView.ScaleType.FIT_XY; visibility = View.GONE }
        root.addView(cursorView, FrameLayout.LayoutParams(1, 1, Gravity.TOP or Gravity.START))
        overlay = FrameLayout(this).apply { background = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(BG_TOP, BG_BOTTOM)) }
        root.addView(overlay, FrameLayout.LayoutParams(-1, -1))
        // Live statistics, toggled with OK / Menu while streaming.
        stats = TextView(this).apply {
            textSize = 14f; setTextColor(Color.WHITE); typeface = Typeface.MONOSPACE; setLineSpacing(0f, 1.15f)
            background = rounded(Color.argb(180, 8, 16, 28), 14)
            setPadding(dp(18), dp(14), dp(18), dp(14)); visibility = View.GONE
        }
        root.addView(stats, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.START).apply { setMargins(dp(24), dp(24), 0, 0) })
        setContentView(root)
        // Ask the TV for its "game" picture/sound path (ALLM): less processing, so less image and sound delay.
        if (Build.VERSION.SDK_INT >= 30) try { window.setPreferMinimalPostProcessing(true) } catch (_: Exception) {}
        gamepads.onChange = { padsChanged() }
        gamepads.onInput = { pad -> padMinis[pad.slot]?.invalidate(); if (padsPage && pad.slot == padSel) padBig?.invalidate() }
        gamepads.start()
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

    /** [busy] shows the spinner next to the status (searching, connecting, downloading). */
    private fun setStatus(s: String, busy: Boolean = false) {
        status?.text = s
        spinner?.visibility = if (busy) View.VISIBLE else View.GONE
    }

    // ---------------- pages ----------------
    /** Header: logo, title and version chip on the left, gear on the right. */
    private fun header(page: LinearLayout, title: String, gear: Boolean) {
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        row.addView(ImageView(this).apply { setImageResource(R.drawable.ic_launcher) },
            LinearLayout.LayoutParams(dp(56), dp(56)).apply { marginEnd = dp(18) })
        row.addView(TextView(this).apply { text = title; textSize = 30f; setTextColor(Color.WHITE); typeface = MEDIUM })
        row.addView(TextView(this).apply {
            text = "v$versionName"; textSize = 13f; setTextColor(ACCENT)
            background = rounded(CARD, 12); setPadding(dp(10), dp(3), dp(10), dp(3))
        }, LinearLayout.LayoutParams(-2, -2).apply { marginStart = dp(14) })
        row.addView(View(this), LinearLayout.LayoutParams(0, 1, 1f))
        if (gear) {
            row.addView(button(L.t("pads_title"), wide = false, icon = R.drawable.ic_gamepad) { showPads() })
            row.addView(button(L.t("settings"), wide = false, icon = R.drawable.ic_settings) { showSettings() })
        }
        else row.addView(button(L.t("back"), wide = false, icon = R.drawable.ic_back) { showMain() })
        page.addView(row, LinearLayout.LayoutParams(-1, -2))
    }

    private fun page(): LinearLayout {
        overlay.removeAllViews()
        val scroll = object : ScrollView(this) {
            // Scrolls so the focused item (and its focus zoom) stays clear of the edge, not flush with it.
            override fun computeScrollDeltaToGetChildRectOnScreen(rect: android.graphics.Rect): Int =
                super.computeScrollDeltaToGetChildRectOnScreen(android.graphics.Rect(rect).apply { inset(0, -dp(48)) })
        }.apply { isFillViewport = true }
        val p = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; clipChildren = false // focus zoom of rows and cards
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(48), dp(32), dp(48), dp(24))
            alpha = 0f; animate().alpha(1f).setDuration(200).start()
        }
        scroll.addView(p)
        overlay.addView(scroll, FrameLayout.LayoutParams(-1, -1))
        return p
    }

    private fun showMain() {
        inSettings = false; padsPage = false; padTest = false; padBar = null
        val p = page()
        header(p, "TVScreenPlus", gear = true)
        val statusRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER; setPadding(0, dp(28), 0, dp(8)) }
        spinner = ProgressBar(this).apply { isIndeterminate = true; indeterminateTintList = ColorStateList.valueOf(ACCENT); visibility = View.GONE }
        statusRow.addView(spinner, LinearLayout.LayoutParams(dp(26), dp(26)).apply { marginEnd = dp(12) })
        status = text("", 20f, ACCENT).also { statusRow.addView(it) }
        p.addView(statusRow)
        updateButton = button(release?.let { L.t("update_to", it.name) } ?: "") { startUpdate() }.apply {
            visibility = if (release != null && release!!.code > versionCode) View.VISIBLE else View.GONE
        }
        p.addView(updateButton, LinearLayout.LayoutParams(dp(640), -2))
        // Side padding: room for the focus zoom of a card (it would be cut at the list's edges), cards stay 640 dp wide.
        list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(16), dp(8), dp(16), dp(16)); clipToPadding = false }
        p.addView(list, LinearLayout.LayoutParams(dp(672), -2))

        val manual = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        val field = rounded(CARD, 12)
        val ipField = EditText(this).apply {
            hint = L.t("ip_hint"); setSingleLine(); textSize = 18f; setTextColor(Color.WHITE); setHintTextColor(MUTED)
            background = field; setPadding(dp(20), dp(14), dp(20), dp(14))
            setOnFocusChangeListener { _, f -> field.setStroke(dp(2), if (f) ACCENT else Color.TRANSPARENT) }
            setText(prefs.getString("manual", ""))
        }
        manual.addView(ipField, LinearLayout.LayoutParams(0, -2, 1f).apply { marginEnd = dp(8) })
        manual.addView(button(L.t("connect"), wide = false) {
            val ip = ipField.text.toString().trim()
            if (ip.isNotEmpty()) { prefs.edit().putString("manual", ip).apply(); autoConnect = true; connect(ip, ip) }
        })
        p.addView(manual, LinearLayout.LayoutParams(dp(640), -2))
        p.addView(text(L.t("help"), 15f, MUTED).apply {
            background = rounded(Color.argb(24, 255, 255, 255), 14); setPadding(dp(24), dp(16), dp(24), dp(16)); setLineSpacing(0f, 1.2f)
        }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(36) })
        // Controller chips at the bottom left (pushed down by the spacer when the page is short).
        p.addView(View(this), LinearLayout.LayoutParams(1, 0, 1f))
        padBar = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; clipChildren = false; clipToPadding = false }
        p.addView(padBar, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(20) })
        padBar()
        refreshList()
    }

    // ---------------- setting rows (settings and controllers pages) ----------------
    private fun section(p: LinearLayout, w: Int, title: String) = p.addView(text(title.uppercase(), 13f, ACCENT).apply {
        gravity = Gravity.START; typeface = MEDIUM; letterSpacing = 0.12f; setPadding(dp(8), dp(26), 0, dp(4))
    }, LinearLayout.LayoutParams(w, -2))

    private fun note(p: LinearLayout, w: Int, s: String) =
        text(s, 14f, MUTED).apply { gravity = Gravity.START; setPadding(dp(8), dp(2), 0, dp(4)) }.also { p.addView(it, LinearLayout.LayoutParams(w, -2)) }

    /** A setting row: label on the left, current value on the right; OK changes it. */
    private fun row(p: LinearLayout, w: Int, label: String, value: String, onClick: (TextView) -> Unit): LinearLayout {
        val v = TextView(this).apply { text = value; textSize = 18f; setTextColor(onFocus(ACCENT)); isDuplicateParentStateEnabled = true; typeface = MEDIUM }
        val r = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(22), dp(16), dp(22), dp(16))
            focusStyle(this)
            setOnClickListener { onClick(v) }
        }
        r.addView(TextView(this).apply { text = label; textSize = 18f; setTextColor(Color.WHITE) }, LinearLayout.LayoutParams(0, -2, 1f))
        r.addView(v)
        p.addView(r, LinearLayout.LayoutParams(w, -2).apply { setMargins(dp(4), dp(5), dp(4), dp(5)) })
        return r
    }

    /** Cycles through options stored under [key]. */
    private fun choice(p: LinearLayout, w: Int, label: String, values: List<String>, names: List<String>, key: String, def: String, onChange: (String) -> Unit = {}) {
        var cur = values.indexOf(prefs.getString(key, def)).coerceAtLeast(0)
        row(p, w, label, names[cur]) {
            cur = (cur + 1) % values.size
            prefs.edit().putString(key, values[cur]).apply()
            it.text = names[cur]
            onChange(values[cur])
        }
    }

    private fun toggle(p: LinearLayout, w: Int, label: String, key: String, def: Boolean, onChange: (Boolean) -> Unit = {}) {
        fun lbl(v: Boolean) = L.t(if (v) "on" else "off")
        row(p, w, label, lbl(prefs.getBoolean(key, def))) {
            val v = !prefs.getBoolean(key, def)
            prefs.edit().putBoolean(key, v).apply()
            it.text = lbl(v); onChange(v)
        }
    }

    // ---------------- controllers ----------------
    /** Main page: the connected controllers, small, at the bottom left; OK opens their page. */
    private fun padBar() {
        padMinis.clear()
        val bar = padBar ?: return
        bar.removeAllViews()
        for (pad in gamepads.pads) {
            val chip = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(10), dp(6), dp(14), dp(6))
                focusStyle(this)
                setOnClickListener { showPads(pad.slot) }
            }
            chip.addView(PadView(this).apply { show(pad) }.also { padMinis[pad.slot] = it }, LinearLayout.LayoutParams(dp(64), dp(40)))
            chip.addView(TextView(this).apply { text = L.t("player_short", pad.slot + 1); textSize = 15f; setTextColor(Color.WHITE); typeface = MEDIUM },
                LinearLayout.LayoutParams(-2, -2).apply { marginStart = dp(6) })
            bar.addView(chip, LinearLayout.LayoutParams(-2, -2).apply { marginEnd = dp(10) })
        }
    }

    /** A controller came, left or was regrouped. */
    private fun padsChanged() {
        if (padBar != null && !inSettings) {
            val focused = padBar?.findFocus() != null
            padBar()
            if (focused) (padBar?.getChildAt(0) ?: list?.getChildAt(0))?.requestFocus()
        }
        if (padsPage && padsSignature() != padsShown) showPads(padSel, padFocusPlayer)
        padFocusPlayer = false
    }

    /** Which controllers are shown (the page is rebuilt only when this changes, not for a setting). */
    private fun padsSignature() = gamepads.pads.joinToString { p -> "${p.slot}:" + p.srcs.joinToString("+") { it.id } }

    private fun stopPadTest() {
        padTest = false
        if (padsPage) showPads(padSel)
    }

    /** Controllers page: every connected controller, the selected one drawn live with its settings, global settings. */
    private fun showPads(select: Int = padSel, focusPlayer: Boolean = false) {
        inSettings = true; padsPage = true; padTest = false; padBar = null; padMinis.clear()
        status = null; spinner = null; list = null; updateButton = null
        val p = page()
        header(p, L.t("pads_title"), gear = false)
        val w = dp(820)
        val pads = gamepads.pads
        padsShown = padsSignature()
        padSel = pads.firstOrNull { it.slot == select }?.slot ?: pads.firstOrNull()?.slot ?: -1
        padBig = null

        if (pads.isEmpty()) {
            p.addView(text(L.t("pads_none"), 18f, ACCENT).apply { setPadding(0, dp(24), 0, dp(8)) }, LinearLayout.LayoutParams(w, -2))
        } else {
            // No clipping: the focused card's zoom and frame spill over the row's edges.
            val cards = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER; setPadding(0, dp(18), 0, dp(6)); clipChildren = false; clipToPadding = false
            }
            val detail = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; clipChildren = false; clipToPadding = false }
            val cardViews = HashMap<Int, View>()
            /** The selected card keeps an accent outline while the focus is down in its settings. */
            fun outline(v: View, slot: Int) {
                if (!v.isFocused) (v.background as? GradientDrawable)?.setStroke(dp(2), if (slot == padSel) ACCENT else Color.TRANSPARENT)
            }
            for (pad in pads) {
                val card = LinearLayout(this).apply {
                    orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER_HORIZONTAL
                    setPadding(dp(12), dp(12), dp(12), dp(10))
                    focusStyle(this)
                    // The controller under the focus is the one shown below.
                    val style = onFocusChangeListener
                    setOnFocusChangeListener { v, f ->
                        style.onFocusChange(v, f)
                        // A card of a controller that just left or was regrouped is skipped (the page is being rebuilt).
                        if (f && padSel != pad.slot && pad.srcs.isNotEmpty() && pad in gamepads.pads) {
                            padSel = pad.slot; detail.removeAllViews(); padDetail(detail, w, pad)
                            cardViews.forEach { (slot, c) -> outline(c, slot) }
                        }
                        outline(v, pad.slot)
                    }
                }
                cardViews[pad.slot] = card
                card.addView(PadView(this).apply { show(pad) }.also { padMinis[pad.slot] = it }, LinearLayout.LayoutParams(dp(130), dp(78)))
                card.addView(TextView(this).apply { text = L.t("player", pad.slot + 1); textSize = 16f; setTextColor(Color.WHITE); typeface = MEDIUM; gravity = Gravity.CENTER },
                    LinearLayout.LayoutParams(-2, -2).apply { topMargin = dp(6) })
                card.addView(TextView(this).apply {
                    text = displayName(pad); textSize = 13f; setTextColor(onFocus(MUTED)); isDuplicateParentStateEnabled = true
                    gravity = Gravity.CENTER; maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END
                }, LinearLayout.LayoutParams(dp(150), -2))
                cards.addView(card, LinearLayout.LayoutParams(-2, -2).apply { setMargins(dp(8), 0, dp(8), 0) })
            }
            p.addView(cards)
            p.addView(detail, LinearLayout.LayoutParams(w, -2))
            pads.firstOrNull { it.slot == padSel }?.let { padDetail(detail, w, it) }
            cardViews.forEach { (slot, c) -> outline(c, slot) }
            ui.post { (if (focusPlayer) padPlayerRow else cardViews[padSel])?.requestFocus() }
        }

        section(p, w, L.t("pads_all"))
        toggle(p, w, L.t("pads"), "pads", true)
        note(p, w, L.t("pads_note"))
        toggle(p, w, L.t("sw2"), "switch2", false) { on ->
            if (!on) switch2.close()
            else switch2.missingPermissions().let {
                if (it.isEmpty()) switch2.startScan(byUser = true)
                else if (Build.VERSION.SDK_INT >= 23) requestPermissions(it, PERM_SWITCH2)
            }
        }
        note(p, w, L.t("sw2_note"))

        section(p, w, L.t("pads_supported"))
        val gallery = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        fun demo(label: String, family: Int, vararg sides: Int) = gallery.addView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER_HORIZONTAL
            addView(PadView(context).apply { demo(family, *sides) }, LinearLayout.LayoutParams(dp(130), dp(78)))
            addView(text(label, 13f, MUTED).apply { setLineSpacing(0f, 1.1f) }, LinearLayout.LayoutParams(dp(150), -2))
        })
        demo("Xbox 360 · One · Series", Gamepads.XBOX, Gamepads.FULL)
        demo("DualShock · DualSense", Gamepads.PLAYSTATION, Gamepads.FULL)
        demo(L.t("pad_pro"), Gamepads.NINTENDO, Gamepads.FULL)
        demo("Joy-Con · Joy-Con 2", Gamepads.NINTENDO, Gamepads.JOYCON_L, Gamepads.JOYCON_R)
        demo(L.t("pad_other"), Gamepads.OTHER, Gamepads.FULL)
        p.addView(gallery, LinearLayout.LayoutParams(w, -2))
    }

    /** Short, readable name: "Nintendo Switch Pro Controller" -> "Pro Controller". */
    private fun displayName(pad: Gamepads.Pad) = Gamepads.padName(pad).removePrefix("Nintendo Switch ").trim()

    private var padPlayerRow: View? = null
    private var padFocusPlayer = false

    /** The selected controller: live drawing and what it is on the left, its settings and actions on the right. */
    private fun padDetail(p: LinearLayout, w: Int, pad: Gamepads.Pad) {
        // No clipping: the focus zoom of a row may spill over the panel's padding.
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.TOP; clipChildren = false; clipToPadding = false
            background = rounded(Color.argb(24, 255, 255, 255), 18); setPadding(dp(20), dp(20), dp(20), dp(20))
        }
        val lw = dp(320)
        val left = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER_HORIZONTAL }
        left.addView(PadView(this).apply { show(pad) }.also { padBig = it }, LinearLayout.LayoutParams(lw, dp(192)))
        left.addView(TextView(this).apply {
            text = displayName(pad); textSize = 20f; setTextColor(Color.WHITE); typeface = MEDIUM; gravity = Gravity.CENTER
            maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END
        }, LinearLayout.LayoutParams(lw, -2).apply { topMargin = dp(10) })
        for (s in pad.srcs) {
            val kind = when {
                s.side == Gamepads.JOYCON_L -> L.t("pad_jc_l")
                s.side == Gamepads.JOYCON_R -> L.t("pad_jc_r")
                s.family == Gamepads.XBOX -> "Xbox"
                s.family == Gamepads.PLAYSTATION -> "PlayStation"
                s.family == Gamepads.NINTENDO -> "Nintendo"
                else -> L.t("pad_other")
            }
            val parts = mutableListOf(kind, if (s.external) "Bluetooth LE" else L.t("pad_android"))
            s.batteryPercent().takeIf { it >= 0 }?.let { parts += L.t("pad_battery", it) }
            left.addView(TextView(this).apply { text = parts.joinToString("  ·  "); textSize = 14f; setTextColor(MUTED); gravity = Gravity.CENTER },
                LinearLayout.LayoutParams(lw, -2).apply { topMargin = dp(4) })
        }
        val sideways = pad.srcs.size == 1 && pad.srcs[0].side != Gamepads.FULL
        val hint = TextView(this).apply {
            textSize = 14f; gravity = Gravity.CENTER; setLineSpacing(0f, 1.15f)
            text = if (padTest) L.t("pad_testing") else if (sideways) L.t(if (pad.srcs[0].external) "pad_sideways" else "pad_sideways1") else ""
            setTextColor(ACCENT); visibility = if (text.isEmpty()) View.GONE else View.VISIBLE
        }
        left.addView(hint, LinearLayout.LayoutParams(lw, -2).apply { topMargin = dp(10) })
        box.addView(left, LinearLayout.LayoutParams(lw, -2))

        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; clipChildren = false; clipToPadding = false }
        val cw = w - lw - dp(40 + 28)
        /** A per-controller choice, applied to every half (Joy-Con pair). */
        fun option(label: String, key: String, def: String, values: List<String>, names: List<String>) {
            var cur = values.indexOf(gamepads.option(pad.srcs[0], key, def)).coerceAtLeast(0)
            row(col, cw, label, names[cur]) {
                cur = (cur + 1) % values.size
                pad.srcs.forEach { s -> gamepads.setOption(s, key, values[cur]) }
                it.text = names[cur]
            }
        }
        /** An action: same row look as the settings, with a chevron. */
        fun action(label: String, onClick: () -> Unit) = row(col, cw, label, "›") { onClick() }
        padPlayerRow = row(col, cw, L.t("pad_player"), L.t("player", pad.slot + 1)) {
            // The regrouping redraws the page once (padsChanged), on this controller, focus back on this row.
            padSel = (pad.slot + 1) % Gamepads.MAX
            padFocusPlayer = true
            gamepads.moveTo(pad, padSel)
        }
        val held = pad.srcs.size == 2 || pad.srcs[0].side == Gamepads.FULL
        if (held && pad.srcs.any { it.family == Gamepads.NINTENDO })
            option(L.t("pad_layout"), Gamepads.LAYOUT, "pos", listOf("pos", "label"), listOf(L.t("pad_layout_pos"), L.t("pad_layout_label")))
        val rumble = pad.srcs.any { it.canRumble() }
        if (rumble) option(L.t("pad_rumble"), Gamepads.RUMBLE, "100", listOf("0", "50", "100", "150"),
            listOf(L.t("off"), L.t("pad_low"), L.t("pad_normal"), L.t("pad_strong")))
        else row(col, cw, L.t("pad_rumble"), L.t("pad_no_rumble")) {}.apply {
            isFocusable = false; alpha = 0.6f // information only
        }
        option(L.t("pad_deadzone"), Gamepads.DEADZONE, "0", listOf("0", "5", "10", "15", "20"),
            listOf(L.t("pad_none"), "5 %", "10 %", "15 %", "20 %"))
        if (pad.srcs.size == 2 && gamepads.count < Gamepads.MAX) action(L.t("pad_split")) { gamepads.split(pad) } // needs a free place
        else if (gamepads.canJoin(pad.srcs[0])) action(L.t("pad_join")) { gamepads.join(pad.srcs[0]) }
        action(L.t("pad_test")) {
            padTest = true
            hint.text = L.t("pad_testing"); hint.visibility = View.VISIBLE
        }
        if (rumble) action(L.t("pad_identify")) { gamepads.identify(pad) }
        val ext = pad.srcs.mapNotNull { it.ext }
        if (ext.isNotEmpty()) action(L.t("pad_disconnect")) { ext.forEach { it.disconnect() } }
        else action(L.t("pad_bt")) { try { startActivity(Intent(android.provider.Settings.ACTION_BLUETOOTH_SETTINGS)) } catch (_: Exception) {} }
        box.addView(col, LinearLayout.LayoutParams(cw, -2).apply { marginStart = dp(28) })
        p.addView(box, LinearLayout.LayoutParams(w, -2).apply { topMargin = dp(14) })
    }

    private fun showSettings() {
        inSettings = true; padsPage = false; padTest = false; padBar = null; padMinis.clear()
        status = null; spinner = null; list = null; updateButton = null
        val p = page()
        header(p, L.t("settings"), gear = false)
        val w = dp(720)

        fun section(title: String) = section(p, w, title)
        fun note(s: String) = note(p, w, s)
        fun choice(label: String, values: List<String>, names: List<String>, key: String, def: String, onChange: (String) -> Unit = {}) =
            choice(p, w, label, values, names, key, def, onChange)
        fun toggle(label: String, key: String, def: Boolean, onChange: (Boolean) -> Unit = {}) = toggle(p, w, label, key, def, onChange)

        section(L.t("lang"))
        choice(L.t("lang"), listOf("system") + L.CODES, listOf(L.t("lang_system")) + L.NAMES, "lang", "system") {
            L.choice = it
            ui.post { showSettings() } // rebuild in the new language
        }

        section(L.t("priority"))
        choice(L.t("priority"), listOf("auto", "smooth", "quality"), listOf(L.t("prio_auto"), L.t("prio_smooth"), L.t("prio_quality")), "pref", "auto")
        choice(L.t("maxres"), listOf("0", "720", "1080", "1440", "2160"), listOf(L.t("auto"), "720p", "1080p", "1440p", "4K"), "maxh", "0")
        choice(L.t("zoom"), listOf("100", "98", "96", "94", "92", "90"), listOf("100 %", "98 %", "96 %", "94 %", "92 %", "90 %"), "zoom", "100") { applyZoom() }
        note(L.t("zoom_note"))
        toggle(L.t("autoconnect"), "autoconnect", true) { autoConnect = it }
        toggle(L.t("showstats"), "showstats", false)

        section(L.t("audio_pads"))
        toggle(L.t("audio"), "audio", true)
        var sync: Button? = null
        choice(L.t("sound_out"), listOf(AudioPlayer.FAST, AudioPlayer.STABLE, AudioPlayer.QUALITY),
            listOf(L.t("snd_fast"), L.t("snd_stable"), L.t("snd_quality")), "sound", AudioPlayer.FAST) {
            sync?.text = L.t("av_offset", prefs.getInt(offsetKey(), 0)) // each output keeps its own correction
        }
        note(L.t("sound_note"))
        val syncButton = button(L.t("av_offset", prefs.getInt(offsetKey(), 0))) {}
        sync = syncButton
        syncButton.setOnClickListener {
            prefs.edit().putInt(offsetKey(), 0).apply()
            syncButton.text = L.t("av_offset", 0)
        }
        p.addView(syncButton, LinearLayout.LayoutParams(w, -2))
        note(L.t("av_offset_note"))
        p.addView(button(L.t("pads_title"), icon = R.drawable.ic_gamepad) { showPads() }, LinearLayout.LayoutParams(w, -2))

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
        var forgotten: TextView? = null
        p.addView(button(L.t("forget")) {
            prefs.edit().remove("last").remove("manual").apply()
            prefs.edit().apply { prefs.all.keys.filter { it.startsWith("mac_") }.forEach { remove(it) } }.apply() // Wake-on-LAN
            pairing.forgetAll() // each PC will ask to accept this TV again
            pcs.clear()
            if (forgotten == null) forgotten = note(L.t("forgotten"))
        }, LinearLayout.LayoutParams(w, -2))
        note(L.t("about_text", versionName, Build.MODEL, Build.VERSION.RELEASE, localIp() ?: "?", networkLabel()))
        overlay.post { p.getChildAt(2)?.requestFocus() } // first setting
    }

    /**
     * Sound sync correction, kept per sound output: the TV's own sound processing (QUALITY) can add a delay its
     * timestamps don't show (86 ms on a TCL/MediaTek TV), which the other outputs don't have.
     */
    private fun offsetKey() = if (prefs.getString("sound", AudioPlayer.FAST) == AudioPlayer.QUALITY) "av_offset_q" else "av_offset"

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
        setStatus(L.t("dl_update", 0), busy = true)
        Thread({
            try {
                Updater.install(this, r) { p -> ui.post { setStatus(L.t("dl_update", p), busy = true) } }
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
                    (gamepads.count.takeIf { it > 0 && gamepads.client != null }?.let { "\n" + L.t("stats_pads", it) } ?: "")
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
        if (prefs.getBoolean("switch2", false)) switch2.startScan()
    }

    override fun onPause() {
        super.onPause()
        discovering = false
        disconnect(null)
        if (prefs.getBoolean("switch2", false)) switch2.stopScan()
    }

    override fun onDestroy() {
        if (prefs.getBoolean("switch2", false)) switch2.close()
        gamepads.stop()
        super.onDestroy()
    }

    override fun onRequestPermissionsResult(code: Int, permissions: Array<out String>, results: IntArray) {
        super.onRequestPermissionsResult(code, permissions, results)
        if (code != PERM_SWITCH2) return
        if (results.isNotEmpty() && results.all { it == PackageManager.PERMISSION_GRANTED }) switch2.startScan(byUser = true)
        else {
            prefs.edit().putBoolean("switch2", false).apply()
            Toast.makeText(this, L.t("sw2_denied"), Toast.LENGTH_LONG).show()
            if (inSettings) showSettings()
        }
    }

    // ---------------- discovery ----------------
    private fun startDiscovery() {
        if (discovering) return
        discovering = true
        val run = ++discoveryRun
        discoveryAt = System.currentTimeMillis()
        ui.postDelayed({ refreshList() }, 4500) // then sleeping PCs show up
        Thread({
            try {
                DatagramSocket().use { s ->
                    s.broadcast = true
                    s.soTimeout = 300
                    val q = "TVSP?".toByteArray()
                    val buf = ByteArray(512)
                    var lastSend = 0L
                    while (discovering && run == discoveryRun) {
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
                            ui.post { found(ip, kv["name"] ?: ip, kv["busy"] == "1", kv["mac"]) }
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

    private fun found(ip: String, name: String, busy: Boolean, mac: String? = null) {
        // Wake-on-LAN: remember the PC's network card (sent by PC 1.2.2+) to wake it up later.
        if (mac != null && mac.matches(Regex("[0-9A-Fa-f]{12}")) && prefs.getString("mac_$ip", null) != "$mac|$name") {
            val e = prefs.edit()
            // The same PC under a new address (DHCP): forget the old one.
            prefs.all.forEach { (k, v) -> if (k.startsWith("mac_") && (v as? String)?.substringBefore('|') == mac) e.remove(k) }
            e.putString("mac_$ip", "$mac|$name").apply()
        }
        val pc = pcs[ip]
        // Add first, then redraw: `.also { refreshList() }` ran before the assignment and drew an empty list,
        // which stayed empty until the PC's state changed (PC "not found" after Back, 13/11).
        if (pc == null) { pcs[ip] = Pc(ip, name, busy, System.currentTimeMillis()); refreshList() }
        else { pc.seen = System.currentTimeMillis(); if (pc.busy != busy || pc.name != name) { pc.busy = busy; pc.name = name; refreshList() } }
        tryAutoConnect()
    }

    private fun expire() {
        val now = System.currentTimeMillis()
        if (pcs.values.removeAll { now - it.seen > 5000 }) refreshList()
    }

    private fun refreshList() {
        val l = list ?: return
        // Keep the focus in the list across the rebuild (removing the focused card sends it to the gear);
        // the first PC found takes it from anywhere, so OK connects at once.
        val focusList = l.childCount == 0 || l.findFocus() != null || currentFocus == null
        l.removeAllViews()
        for (pc in pcs.values) l.addView(pcCard(pc))
        // PCs seen before (with their network card) that don't answer: offer to wake them, after a few seconds of search.
        if (System.currentTimeMillis() - discoveryAt > 4000) for ((ip, mac, name) in sleepingPcs()) l.addView(sleepCard(ip, mac, name))
        if (client == null) setStatus(L.t(if (pcs.isEmpty()) "searching" else "choose"), busy = pcs.isEmpty())
        if (focusList) l.getChildAt(0)?.requestFocus()
    }

    /** A found PC: screen icon, name, IP below, "busy" badge on the right. */
    private fun pcCard(pc: Pc) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
        setPadding(dp(22), dp(14), dp(22), dp(14))
        focusStyle(this)
        setOnClickListener { autoConnect = true; connect(pc.ip, pc.name) }
        addView(ImageView(context).apply { setImageResource(R.drawable.ic_pc) }, LinearLayout.LayoutParams(dp(40), dp(40)).apply { marginEnd = dp(18) })
        addView(LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL; isDuplicateParentStateEnabled = true
            addView(TextView(context).apply { text = pc.name; textSize = 22f; setTextColor(Color.WHITE); typeface = MEDIUM })
            addView(TextView(context).apply { text = pc.ip; textSize = 14f; setTextColor(onFocus(MUTED)); isDuplicateParentStateEnabled = true })
        }, LinearLayout.LayoutParams(0, -2, 1f))
        if (pc.busy) addView(TextView(context).apply {
            text = L.t("busy"); textSize = 14f; setTextColor(Color.WHITE); typeface = MEDIUM
            background = rounded(Color.rgb(230, 126, 34), 12); setPadding(dp(12), dp(4), dp(12), dp(4))
        })
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { setMargins(dp(4), dp(6), dp(4), dp(6)) }
    }

    /** Remembered PCs (ip, mac, name) not answering the discovery. */
    private fun sleepingPcs() = prefs.all.keys.filter { it.startsWith("mac_") }.mapNotNull { k ->
        val ip = k.removePrefix("mac_")
        val v = prefs.getString(k, null)?.split('|', limit = 2) ?: return@mapNotNull null
        // Not shown if it answers, even under another address (older PC version without the mac field).
        if (pcs.containsKey(ip) || v.size < 2 || pcs.values.any { it.name == v[1] }) null else Triple(ip, v[0], v[1])
    }

    /** A PC that doesn't answer: dimmed card, OK sends the Wake-on-LAN packet and connects once it answers. */
    private fun sleepCard(ip: String, mac: String, name: String) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
        setPadding(dp(22), dp(14), dp(22), dp(14))
        focusStyle(this)
        alpha = 0.75f
        setOnClickListener {
            wake(mac)
            prefs.edit().putString("last", ip).apply()
            autoConnect = true // tryAutoConnect picks it as soon as it answers
            setStatus(L.t("waking", name), busy = true)
        }
        addView(ImageView(context).apply { setImageResource(R.drawable.ic_pc); alpha = 0.6f }, LinearLayout.LayoutParams(dp(40), dp(40)).apply { marginEnd = dp(18) })
        addView(LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL; isDuplicateParentStateEnabled = true
            addView(TextView(context).apply { text = name; textSize = 22f; setTextColor(Color.WHITE); typeface = MEDIUM })
            addView(TextView(context).apply { text = L.t("asleep", ip); textSize = 14f; setTextColor(onFocus(MUTED)); isDuplicateParentStateEnabled = true })
        }, LinearLayout.LayoutParams(0, -2, 1f))
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { setMargins(dp(4), dp(6), dp(4), dp(6)) }
    }

    /** Wake-on-LAN "magic packet" (6 x FF, then the card's address 16 times), broadcast a few times. */
    private fun wake(mac: String) {
        val m = ByteArray(6) { mac.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
        val packet = ByteArray(102) { if (it < 6) 0xFF.toByte() else m[(it - 6) % 6] }
        Thread({
            try {
                DatagramSocket().use { s ->
                    s.broadcast = true
                    repeat(3) {
                        for (a in broadcastAddresses()) for (port in intArrayOf(9, 7))
                            try { s.send(DatagramPacket(packet, packet.size, a, port)) } catch (_: Exception) {}
                        Thread.sleep(300)
                    }
                }
            } catch (_: Exception) {}
        }, "wake").start()
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
        return "TVSP1;v=2;auth=1;id=${pairing.tvId};app=$versionCode;lang=${L.lang};name=${Build.MODEL.replace(';', ' ')};w=$w;h=$h;hz=${refreshHz()}" +
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
        setStatus(L.t("connecting", name), busy = true)
        val hz = refreshHz()
        lateinit var c: StreamClient
        c = StreamClient(ip, hello(), diagnostics(), surfaceView.holder.surface, object : StreamClient.Listener {
            override fun onStatus(text: String) {
                ui.post { if (client === c && overlay.visibility == View.VISIBLE) setStatus("$name : $text", busy = true) }
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
                    if (c.pads) gamepads.client = c
                    android.util.Log.i(StreamClient.TAG, "streaming $info from $name")
                }
            }

            override fun onRumble(slot: Int, large: Int, small: Int) {
                if (client === c) gamepads.rumble(slot, large, small)
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
                    hideQuickMenu()
                    client = null
                    stopGamepads()
                    releaseWifiLock()
                    stats.visibility = View.GONE
                    showOverlay()
                    setStatus(if (error != null) L.t("disconnected_err", error) else L.t("disconnected"))
                    list?.getChildAt(0)?.requestFocus()
                    // Stuck stream (no image for 6 s): reconnect to the same PC at once, whatever the settings.
                    if (c.stalled) ui.postDelayed({ if (client == null && !inSettings) connect(ip, name) }, 1500)
                    else if (c.refused) autoConnect = false // refused on the PC: wait until a PC is chosen again
                    else if (autoConnect) ui.postDelayed({ tryAutoConnect() }, 2000)
                }
            }
        }, pairing)
        c.userOffsetMs = prefs.getInt(offsetKey(), 0)
        c.soundMode = prefs.getString("sound", AudioPlayer.FAST) ?: AudioPlayer.FAST
        client = c
        c.start()
    }

    private fun disconnect(reason: String?) {
        hideQuickMenu()
        val c = client ?: return
        client = null
        stopGamepads()
        c.stop()
        releaseWifiLock()
        stats.visibility = View.GONE
        showOverlay()
        setStatus(reason ?: L.t("disconnected"))
    }

    /** Sound sync correction (ms), remembered per sound output; returns the value kept. */
    private fun setSync(c: StreamClient, ms: Int): Int {
        val v = ms.coerceIn(-200, 300)
        prefs.edit().putInt(offsetKey(), v).apply()
        c.userOffsetMs = v
        return v
    }

    /** Picture size (TVs that crop the edges): the video surface gets margins; 100 % = whole screen. */
    private fun applyZoom() {
        val k = (prefs.getString("zoom", "100")?.toIntOrNull() ?: 100).coerceIn(80, 100) / 100f
        val dm = resources.displayMetrics
        val mx = Math.round(dm.widthPixels * (1 - k) / 2); val my = Math.round(dm.heightPixels * (1 - k) / 2)
        surfaceView.layoutParams = FrameLayout.LayoutParams(-1, -1).apply { setMargins(mx, my, mx, my) }
    }

    /** Menu key while streaming: mode, sound sync, statistics, disconnect, over the video. */
    private fun showQuickMenu() {
        val c = client ?: return
        val panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; clipChildren = false; clipToPadding = false
            background = rounded(Color.argb(235, 12, 24, 40), 20); setPadding(dp(16), dp(18), dp(16), dp(14))
        }
        val w = dp(440)
        panel.addView(text(L.t("qm_title").uppercase(), 13f, ACCENT).apply {
            gravity = Gravity.START; typeface = MEDIUM; letterSpacing = 0.12f; setPadding(dp(10), 0, 0, dp(6))
        })
        fun modeName(game: Boolean) = L.t(if (game) "qm_game" else "qm_fluid")
        if (c.modeSupported) row(panel, w, L.t("qm_mode"), modeName(c.gameMode)) {
            val game = !c.gameMode
            c.requestMode(game)
            it.text = modeName(game)
        }
        // Sound sync: ◀ ▶ on the row move it by 10 ms, OK puts it back to 0.
        val sync = row(panel, w, L.t("qm_sync"), L.t("qm_ms", prefs.getInt(offsetKey(), 0))) { it.text = L.t("qm_ms", setSync(c, 0)) }
        sync.setOnKeyListener { v, code, e ->
            if (e.action != KeyEvent.ACTION_DOWN || (code != KeyEvent.KEYCODE_DPAD_LEFT && code != KeyEvent.KEYCODE_DPAD_RIGHT)) return@setOnKeyListener false
            val ms = setSync(c, prefs.getInt(offsetKey(), 0) + if (code == KeyEvent.KEYCODE_DPAD_RIGHT) 10 else -10)
            ((v as LinearLayout).getChildAt(1) as TextView).text = L.t("qm_ms", ms)
            true
        }
        panel.addView(text(L.t("qm_sync_note"), 13f, MUTED).apply { gravity = Gravity.START; setPadding(dp(10), 0, dp(10), dp(6)) },
            LinearLayout.LayoutParams(w, -2))
        fun statsName() = L.t(if (stats.visibility == View.VISIBLE) "on" else "off")
        row(panel, w, L.t("showstats"), statsName()) {
            stats.visibility = if (stats.visibility == View.VISIBLE) View.GONE else View.VISIBLE
            ui.removeCallbacks(statsTick); statsTick.run()
            it.text = statsName()
        }
        row(panel, w, L.t("qm_disconnect"), "›") {
            autoConnect = false // user wants out: no auto-reconnect until they pick a PC
            disconnect(L.t("disconnected_back"))
            list?.getChildAt(0)?.requestFocus()
        }
        root.addView(panel, FrameLayout.LayoutParams(-2, -2, Gravity.END or Gravity.CENTER_VERTICAL).apply { marginEnd = dp(48) })
        quickMenu = panel
        panel.alpha = 0f; panel.animate().alpha(1f).setDuration(150).start()
        panel.getChildAt(1)?.requestFocus()
    }

    private fun hideQuickMenu() {
        quickMenu?.let { root.removeView(it) }
        quickMenu = null
    }

    private fun stopGamepads() { gamepads.client = null }

    /** While streaming, gamepad input goes to the PC first; the remote keeps its keys (Back, OK, Menu). */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        // Gamepad state is always tracked (menu drawings); the input goes to the PC while streaming, or stays on the
        // controllers page while testing (Start + Select, or the remote's Back, ends the test).
        if (gamepads.onKey(event)) {
            // Start + Select on one controller (a Joy-Con pair has them on two halves).
            if (padTest && gamepads.pads.any { p -> p.srcs.fold(0) { k, s -> k or s.buttons } and (Gamepads.START or Gamepads.BACK) == Gamepads.START or Gamepads.BACK }) {
                stopPadTest(); return true
            }
            if (padTest || (gamepads.client != null && overlay.visibility == View.GONE)) return true
        }
        return super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        if (gamepads.onMotion(event) && (padTest || (gamepads.client != null && overlay.visibility == View.GONE))) return true
        return super.dispatchGenericMotionEvent(event)
    }

    override fun onKeyLongPress(keyCode: Int, event: KeyEvent): Boolean {
        if (client != null && overlay.visibility == View.GONE && quickMenu == null &&
            (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER)) { showQuickMenu(); return true }
        return super.onKeyLongPress(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean {
        if (client != null && overlay.visibility == View.GONE && quickMenu == null && event.isTracking &&
            event.flags and KeyEvent.FLAG_CANCELED_LONG_PRESS == 0 &&
            (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER || keyCode == KeyEvent.KEYCODE_INFO)) {
            stats.visibility = if (stats.visibility == View.VISIBLE) View.GONE else View.VISIBLE
            ui.removeCallbacks(statsTick)
            statsTick.run()
            return true
        }
        return super.onKeyUp(keyCode, event)
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (quickMenu != null && (keyCode == KeyEvent.KEYCODE_BACK || keyCode == KeyEvent.KEYCODE_MENU)) { hideQuickMenu(); return true }
        if (quickMenu != null) return super.onKeyDown(keyCode, event) // focus moves in the menu
        if (client != null && overlay.visibility == View.GONE && keyCode == KeyEvent.KEYCODE_MENU) { showQuickMenu(); return true }
        if (keyCode == KeyEvent.KEYCODE_BACK && client != null) {
            autoConnect = false // user wants out: no auto-reconnect until they pick a PC
            disconnect(L.t("disconnected_back"))
            list?.getChildAt(0)?.requestFocus()
            return true
        }
        if (keyCode == KeyEvent.KEYCODE_BACK && padTest) { stopPadTest(); return true }
        if (keyCode == KeyEvent.KEYCODE_BACK && inSettings) { showMain(); return true }
        if (client != null && overlay.visibility == View.GONE &&
            (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER || keyCode == KeyEvent.KEYCODE_INFO)) {
            // Short press (on release) = statistics, long press = quick menu (most Google TV remotes have no Menu key).
            if (event.repeatCount == 0) event.startTracking()
            return true
        }
        val c = client
        if (c != null && overlay.visibility == View.GONE &&
            (keyCode == KeyEvent.KEYCODE_DPAD_LEFT || keyCode == KeyEvent.KEYCODE_DPAD_RIGHT)) {
            // Fine lip-sync by ear: < sound earlier, > sound later (10 ms steps, remembered).
            val v = setSync(c, prefs.getInt(offsetKey(), 0) + if (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) 10 else -10)
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
        cursorView.translationX = surfaceView.left + (curX - curHotX) * sx
        cursorView.translationY = surfaceView.top + (curY - curHotY) * sy
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

    private fun button(label: String, wide: Boolean = true, icon: Int = 0, onClick: () -> Unit) = Button(this).apply {
        text = label; isAllCaps = false; textSize = 18f; setTextColor(Color.WHITE); typeface = MEDIUM
        if (icon != 0) {
            // A drawn icon sits on the text's centre line (font arrows / emoji don't).
            val d = getDrawable(icon)!!.mutate().apply { setBounds(0, 0, dp(24), dp(24)) }
            setCompoundDrawablesRelative(d, null, null, null)
            compoundDrawablePadding = dp(12)
        }
        gravity = if (wide) Gravity.START or Gravity.CENTER_VERTICAL else Gravity.CENTER
        setPadding(dp(22), dp(12), dp(22), dp(12))
        stateListAnimator = null // the platform press "lift" fights the focus animation
        focusStyle(this)
        setOnClickListener { onClick() }
        layoutParams = LinearLayout.LayoutParams(if (wide) -1 else -2, -2).apply { setMargins(dp(4), dp(6), dp(4), dp(6)) }
    }

    private fun rounded(color: Int, radiusDp: Int) = GradientDrawable().apply { cornerRadius = dp(radiusDp).toFloat(); setColor(color) }

    /** Card look for anything focusable with the remote: accent fill, light outline, shadow and slight zoom when focused. */
    private fun focusStyle(v: View) {
        val d = rounded(CARD, 14).apply { setStroke(dp(2), Color.TRANSPARENT) }
        v.background = d
        v.isFocusable = true
        v.setOnFocusChangeListener { _, f ->
            d.setColor(if (f) FOCUS else CARD)
            d.setStroke(dp(2), if (f) Color.argb(220, 255, 255, 255) else Color.TRANSPARENT)
            v.elevation = if (f) dp(10).toFloat() else 0f
            v.animate().scaleX(if (f) 1.03f else 1f).scaleY(if (f) 1.03f else 1f).setDuration(140).start()
        }
    }

    /** Text colour that turns white while its card (parent) has the focus, for contrast on the accent fill. */
    private fun onFocus(color: Int) = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_focused), intArrayOf()), intArrayOf(Color.WHITE, color))

    private fun showOverlay() {
        if (overlay.visibility != View.VISIBLE) { overlay.alpha = 0f; overlay.animate().alpha(1f).setDuration(250).start() }
        overlay.visibility = View.VISIBLE
    }

    companion object {
        private val BG_TOP = Color.rgb(18, 38, 62)
        private val BG_BOTTOM = Color.rgb(7, 14, 24)
        private val CARD = Color.rgb(28, 52, 80)
        private val FOCUS = Color.rgb(30, 136, 229)
        private val ACCENT = Color.rgb(144, 202, 249)
        private val MUTED = Color.rgb(150, 168, 190)
        private val MEDIUM: Typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        private const val PERM_SWITCH2 = 7
    }
}
