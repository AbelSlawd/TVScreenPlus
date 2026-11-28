package com.tvscreenplus

import android.content.Context
import android.content.SharedPreferences
import android.hardware.input.InputManager
import android.os.Build
import android.os.CombinedVibration
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent

/**
 * Gamepads connected to the TV (Bluetooth, USB, or Switch 2 through [Switch2]). Always tracked, so the menu can show
 * them; forwarded to the PC while [client] is set, where they appear as Xbox 360 controllers.
 * Upstream lines: "PADON;slot;name", "PADOFF;slot", and on every change of state
 * "PAD;slot;buttons (hex, XInput bits);lt;rt;lx;ly;rx;ry" (triggers 0-255, sticks -32768..32767, Y up).
 *
 * Joy-Cons are halves: a left and a right one make one controller; a lone Joy-Con is held sideways. They are split
 * (two players with one Joy-Con each) only from the controllers page: their "single" setting.
 * Per-controller settings (button layout, rumble strength, stick dead zone, Joy-Con pairing) live in [prefs].
 * Runs on the UI thread (key / motion events, device listener, [Switch2] callbacks).
 */
class Gamepads(context: Context, private val prefs: SharedPreferences) : InputManager.InputDeviceListener {
    /** A controller driven from outside Android's input stack (Switch 2 over Bluetooth LE). */
    interface External {
        fun rumble(large: Int, small: Int)
        fun led(slot: Int)
        fun disconnect()
    }

    /** One physical controller or Joy-Con, in Xbox terms as if held upright. Filled by key/motion events or by [Switch2]. */
    class Src(val side: Int, val name: String, val family: Int, val id: String) {
        var keys = 0                 // XInput bits from buttons / d-pad keys (by position: bottom button = A)
        var hat = 0                  // d-pad bits from the hat axes
        var keyLt = false
        var keyRt = false
        var lx = 0; var ly = 0; var rx = 0; var ry = 0; var lt = 0; var rt = 0
        var sl = false; var sr = false; var capture = false // Joy-Con only
        var battery = -1             // percent, -1 unknown (Switch 2)
        var pad: Pad? = null
            internal set
        internal var solo = false    // plays alone (sideways Joy-Con)
        internal var devId = -1      // Android InputDevice, or -1
        var ext: External? = null
            internal set
        internal var axes: Axes? = null
        val external get() = ext != null
        val buttons get() = keys or hat
        val ltValue get() = if (keyLt) 255 else lt
        val rtValue get() = if (keyRt) 255 else rt

        /**
         * Whether the TV can make it vibrate: Switch 2 always; others need a kernel driver with rumble (this TCL's
         * kernel has none for Nintendo controllers, so Android lists no vibrator for them).
         */
        fun canRumble(): Boolean {
            if (ext != null) return true
            val d = InputDevice.getDevice(devId) ?: return false
            @Suppress("DEPRECATION")
            return if (Build.VERSION.SDK_INT >= 31) d.vibratorManager.vibratorIds.isNotEmpty() else d.vibrator.hasVibrator()
        }

        /** Battery percent: Switch 2 reports, else Android (12+), -1 if unknown. */
        fun batteryPercent(): Int {
            if (battery >= 0 || devId < 0 || Build.VERSION.SDK_INT < 31) return battery
            val b = InputDevice.getDevice(devId)?.batteryState ?: return -1
            return if (b.isPresent && !b.capacity.isNaN()) (b.capacity * 100).toInt() else -1
        }
    }

    class Pad(slot: Int) {
        var slot = slot
            internal set
        val srcs = ArrayList<Src>(2)
        internal var sent = ""
        internal var large = 0; internal var small = 0 // rumble requested by the PC
    }

    /** Where an Android device puts its right stick and triggers. */
    internal class Axes(dev: InputDevice) {
        // Right stick: Z/RZ on most Android gamepads, RX/RY on some. Z/RZ are triggers (0..1) on a few others.
        val rightX = if (isStick(dev, MotionEvent.AXIS_Z)) MotionEvent.AXIS_Z else MotionEvent.AXIS_RX
        val rightY = if (isStick(dev, MotionEvent.AXIS_RZ)) MotionEvent.AXIS_RZ else MotionEvent.AXIS_RY
        val ltAxes = intArrayOf(MotionEvent.AXIS_LTRIGGER, MotionEvent.AXIS_BRAKE) + (if (isTrigger(dev, MotionEvent.AXIS_Z)) intArrayOf(MotionEvent.AXIS_Z) else intArrayOf())
        val rtAxes = intArrayOf(MotionEvent.AXIS_RTRIGGER, MotionEvent.AXIS_GAS) + (if (isTrigger(dev, MotionEvent.AXIS_RZ)) intArrayOf(MotionEvent.AXIS_RZ) else intArrayOf())
    }

    private val im = context.getSystemService(Context.INPUT_SERVICE) as InputManager
    // Asynchronous messages: rumble doesn't wait while the UI draws a frame.
    private val ui = if (Build.VERSION.SDK_INT >= 28) Handler.createAsync(Looper.getMainLooper()) else Handler(Looper.getMainLooper())
    private val devs = HashMap<Int, Src>()     // Android InputDevice id -> source
    private val exts = HashMap<Any, Src>()     // external key -> source
    private val slots = arrayOfNulls<Pad>(MAX)
    private var rumbleError = false

    /** Controllers connected or regrouped (menu redraw). */
    var onChange: (() -> Unit)? = null
    /** A controller's buttons or sticks moved (live drawing). */
    var onInput: ((Pad) -> Unit)? = null

    /** Stream receiving the controllers, or null: they are then only tracked. */
    var client: StreamClient? = null
        set(c) {
            if (c === field) return
            if (c == null) for (p in pads) if (p.large > 0 || p.small > 0) { p.large = 0; p.small = 0; vibrate(p) }
            field = c
            if (c != null) pads.forEach { announce(it) }
        }

    val pads: List<Pad> get() = slots.filterNotNull()
    val count: Int get() = slots.count { it != null }

    fun start() {
        ui.postDelayed(ageTick, 10_000)
        im.registerInputDeviceListener(this, ui)
        // Controllers already paired.
        for (id in InputDevice.getDeviceIds()) InputDevice.getDevice(id)?.let { if (isGamepad(it)) add(it) }
    }

    fun stop() {
        client = null
        im.unregisterInputDeviceListener(this)
        ui.removeCallbacks(rumbleTick)
        ui.removeCallbacks(ageTick)
    }

    // ---------------- Android input ----------------

    /** True when the event came from a gamepad (its state is updated; the caller decides whether to consume it). */
    // Delay on the TV between the controller driver (Android's event time) and the state going out, for the PC log.
    private var ageSum = 0L; private var ageN = 0; private var ageMax = 0L
    private var keySum = 0L; private var keyN = 0

    private fun age(eventTime: Long, key: Boolean = false) {
        val a = android.os.SystemClock.uptimeMillis() - eventTime
        if (a in 0..1000) { ageSum += a; ageN++; if (a > ageMax) ageMax = a; if (key) { keySum += a; keyN++ } }
    }

    private val ageTick = object : Runnable {
        override fun run() {
            val c = client
            fun f(x: Double) = "%.1f".format(java.util.Locale.ROOT, x)
            if (c != null && ageN > 0) c.post("LOG;gamepad input on the TV: $ageN events, driver -> sent avg ${f(ageSum.toDouble() / ageN)} ms, max $ageMax ms" +
                (if (keyN > 0) ", buttons ${f(keySum.toDouble() / keyN)} ms ($keyN)" else ""))
            ageSum = 0; ageN = 0; ageMax = 0; keySum = 0; keyN = 0
            ui.postDelayed(this, 10_000)
        }
    }

    fun onKey(e: KeyEvent): Boolean {
        val s = srcFor(e.device) ?: return false
        age(e.eventTime, true)
        val down = e.action == KeyEvent.ACTION_DOWN
        if (s.family == NINTENDO && s.side == FULL && e.scanCode == 0x135) {
            s.capture = down // Pro Controller Capture: Android's layout makes it "mode"; drawn as Capture, sent as Guide
            changed(s)
            return true
        }
        if (!(s.side != FULL && joyConKey(s, e.scanCode, down))) {
            val k = e.keyCode
            val bit = bit(k)
            when {
                bit != 0 -> s.keys = if (down) s.keys or bit else s.keys and bit.inv()
                k == KeyEvent.KEYCODE_BUTTON_L2 -> s.keyLt = down
                k == KeyEvent.KEYCODE_BUTTON_R2 -> s.keyRt = down
                else -> return false // volume, media keys…: let the TV handle them
            }
        }
        changed(s)
        return true
    }

    fun onMotion(e: MotionEvent): Boolean {
        if (e.action != MotionEvent.ACTION_MOVE) return false
        if (!e.isFromSource(InputDevice.SOURCE_JOYSTICK) && !e.isFromSource(InputDevice.SOURCE_GAMEPAD)) return false
        val dev = e.device ?: return false
        val s = srcFor(dev) ?: return false
        val a = s.axes ?: return false
        age(e.eventTime)
        s.lx = stick(axis(e, dev, MotionEvent.AXIS_X))
        s.ly = stick(-axis(e, dev, MotionEvent.AXIS_Y))
        s.rx = stick(axis(e, dev, a.rightX))
        s.ry = stick(-axis(e, dev, a.rightY))
        s.lt = trigger(a.ltAxes.maxOf { axis(e, dev, it) })
        s.rt = trigger(a.rtAxes.maxOf { axis(e, dev, it) })
        val hx = e.getAxisValue(MotionEvent.AXIS_HAT_X)
        val hy = e.getAxisValue(MotionEvent.AXIS_HAT_Y)
        s.hat = (if (hy < -0.5f) UP else 0) or (if (hy > 0.5f) DOWN else 0) or (if (hx < -0.5f) LEFT else 0) or (if (hx > 0.5f) RIGHT else 0)
        changed(s)
        return true
    }

    override fun onInputDeviceAdded(id: Int) {
        InputDevice.getDevice(id)?.let { if (isGamepad(it) && !devs.containsKey(id)) add(it) }
    }

    override fun onInputDeviceRemoved(id: Int) {
        devs.remove(id)?.let { remove(it) }
    }

    override fun onInputDeviceChanged(id: Int) {}

    private fun srcFor(dev: InputDevice?): Src? {
        if (dev == null) return null
        return devs[dev.id] ?: if (isGamepad(dev)) add(dev) else null
    }

    private fun add(dev: InputDevice): Src {
        val s = Src(sideOf(dev), dev.name, familyOf(dev), "dev_" + dev.descriptor)
        s.devId = dev.id
        s.axes = Axes(dev)
        devs[dev.id] = s
        attach(s)
        return s
    }

    // ---------------- external controllers (Switch 2) ----------------

    fun extAdd(key: Any, side: Int, name: String, id: String, ext: External): Src {
        exts[key]?.let { return it }
        val s = Src(side, name, NINTENDO, id)
        s.ext = ext
        exts[key] = s
        attach(s)
        return s
    }

    /** The source's fields were updated: send the new state. */
    fun extChanged(key: Any) { exts[key]?.let { changed(it) } }

    fun extRemove(key: Any) { exts.remove(key)?.let { remove(it) } }

    // ---------------- settings ----------------

    fun option(s: Src, key: String, def: String): String = prefs.getString("pad_${s.id}_$key", def) ?: def

    /** Saves a setting of this controller and applies it at once. */
    fun setOption(s: Src, key: String, value: String) {
        prefs.edit().putString("pad_${s.id}_$key", value).apply()
        if (key == PAIRING) quiet { remove(s); attach(s) } // regroup the Joy-Cons
        else { s.pad?.let { it.sent = ""; send(it) }; onChange?.invoke() }
    }

    /** A lone Joy-Con whose other half is connected and alone too. */
    fun canJoin(s: Src) = s.side != FULL && s.pad?.srcs?.size == 1 && pads.any { it.srcs.size == 1 && it.srcs[0].side == -s.side }

    /** Splits a Joy-Con pair into two controllers (remembered: "single" for both halves). */
    fun split(p: Pad) {
        if (p.srcs.size != 2) return
        val halves = p.srcs.toList()
        halves.forEach { prefs.edit().putString("pad_${it.id}_$PAIRING", "solo").apply() }
        quiet { halves.forEach { remove(it) }; halves.forEach { attach(it) } }
    }

    /** Puts a lone Joy-Con back together with the other half (both stop being "single"). */
    fun join(s: Src) {
        val other = pads.firstOrNull { it.srcs.size == 1 && it.srcs[0].side == -s.side }?.srcs?.get(0) ?: return
        for (h in listOf(s, other)) { prefs.edit().putString("pad_${h.id}_$PAIRING", "auto").apply(); h.solo = false }
        quiet { remove(s); attach(s) }
    }

    /** Several regroupings, one redraw of the menu at the end. */
    private fun quiet(work: () -> Unit) {
        val listener = onChange
        onChange = null
        try { work() } finally { onChange = listener; listener?.invoke() }
    }

    /** Gives the controller of [p] the player number [slot] (swapped with the controller that had it). */
    fun moveTo(p: Pad, slot: Int) {
        if (slot !in 0 until MAX || slots[p.slot] !== p || p.slot == slot) return
        val other = slots[slot]
        val c = client
        if (c != null) { c.post("PADOFF;${p.slot}"); if (other != null) c.post("PADOFF;$slot") }
        slots[p.slot] = other; other?.slot = p.slot
        slots[slot] = p; p.slot = slot
        announce(p); other?.let { announce(it) }
        onChange?.invoke()
    }

    /** Short vibration (and player LEDs) to find which controller this is. */
    fun identify(p: Pad) {
        val srcs = p.srcs.toList() // the Joy-Cons may be regrouped meanwhile
        srcs.forEach { s -> s.ext?.led(p.slot); vibrate(s, 200, 200) }
        ui.postDelayed({ srcs.forEach { s -> vibrate(s, s.pad?.large ?: 0, s.pad?.small ?: 0) } }, 400)
    }

    // ---------------- pads ----------------

    /** Gives a new source a slot: the free half of a Joy-Con pair, else a new controller. */
    private fun attach(s: Src) {
        if (s.pad != null) return
        s.solo = s.side != FULL && option(s, PAIRING, "auto") == "solo"
        val partner = if (s.side == FULL || s.solo) null else slots.firstOrNull { p ->
            p != null && p.srcs.size == 1 && p.srcs[0].side == -s.side && !p.srcs[0].solo
        }
        if (partner != null) {
            partner.srcs.add(s); s.pad = partner
            announce(partner)
        } else {
            val slot = slots.indexOfFirst { it == null }
            if (slot < 0) return // XInput: 4 controllers
            val p = Pad(slot)
            p.srcs.add(s); s.pad = p
            slots[slot] = p
            announce(p)
        }
        onChange?.invoke()
    }

    private fun remove(s: Src) {
        val p = s.pad ?: return
        s.pad = null
        p.srcs.remove(s)
        if (p.srcs.isEmpty()) {
            slots[p.slot] = null
            client?.post("PADOFF;${p.slot}")
            // A controller that came while all 4 places were taken gets the one just freed.
            (devs.values + exts.values).firstOrNull { it.pad == null && it !== s }?.let { attach(it) }
        } else announce(p) // the other Joy-Con plays on, sideways
        onChange?.invoke()
    }

    private fun announce(p: Pad) {
        p.srcs.forEach { it.ext?.led(p.slot) }
        val c = client ?: return
        c.post("PADON;${p.slot};${padName(p).replace(';', ' ').replace('\n', ' ').take(40)}")
        p.sent = ""
        send(p)
    }

    private fun changed(s: Src) {
        val p = s.pad ?: return
        send(p)
        onInput?.invoke(p)
    }

    private fun send(p: Pad) {
        val c = client ?: return
        val l = p.srcs.firstOrNull { it.side == JOYCON_L }
        val r = p.srcs.firstOrNull { it.side == JOYCON_R }
        var keys: Int; val lt: Int; val rt: Int; val lx: Int; val ly: Int; val rx: Int; val ry: Int
        when {
            l != null && r != null -> {
                keys = l.buttons or r.buttons
                lt = l.ltValue; rt = r.rtValue
                l.leftStick().let { lx = it[0]; ly = it[1] }
                r.rightStick().let { rx = it[0]; ry = it[1] }
            }
            l != null -> { // sideways, SL/SR on top: the d-pad becomes the face buttons
                val d = l.buttons
                // Switch 1 Joy-Con: the TV's driver never reports SL/SR, so L / ZL are the shoulder buttons.
                val noSlSr = !l.external
                keys = (if (d and LEFT != 0) A else 0) or (if (d and DOWN != 0) B else 0) or
                    (if (d and UP != 0) X else 0) or (if (d and RIGHT != 0) Y else 0) or
                    (if (if (noSlSr) d and LB != 0 else l.sl) LB else 0) or (if (if (noSlSr) l.ltValue > 0 else l.sr) RB else 0) or
                    (if (d and BACK != 0) START else 0) or (if (l.capture) BACK else 0) or (d and LS)
                lt = if (!noSlSr && d and LB != 0) 255 else 0; rt = if (noSlSr) 0 else l.ltValue
                l.leftStick().let { lx = (-it[1]).coerceAtMost(32767); ly = it[0] }
                rx = 0; ry = 0
            }
            r != null -> {
                val d = r.buttons
                val noSlSr = !r.external // as above: R / ZR are the shoulder buttons
                keys = (if (d and B != 0) A else 0) or (if (d and Y != 0) B else 0) or
                    (if (d and A != 0) X else 0) or (if (d and X != 0) Y else 0) or
                    (if (if (noSlSr) d and RB != 0 else r.sl) LB else 0) or (if (if (noSlSr) r.rtValue > 0 else r.sr) RB else 0) or
                    (d and (START or GUIDE)) or (if (d and RS != 0) LS else 0)
                lt = if (!noSlSr && d and RB != 0) 255 else 0; rt = if (noSlSr) 0 else r.rtValue
                r.rightStick().let { lx = it[1]; ly = (-it[0]).coerceAtMost(32767) }
                rx = 0; ry = 0
            }
            else -> {
                val s = p.srcs[0]
                // Nintendo Capture acts as Guide: the TV keeps HOME for itself.
                keys = s.buttons or (if (s.family == NINTENDO && s.capture) GUIDE else 0)
                lt = s.ltValue; rt = s.rtValue
                s.leftStick().let { lx = it[0]; ly = it[1] }
                s.rightStick().let { rx = it[0]; ry = it[1] }
            }
        }
        // Nintendo letters instead of positions: A is the right button, B the bottom one, X top, Y left.
        val held = p.srcs.size == 2 || p.srcs[0].side == FULL
        if (held && p.srcs.any { it.family == NINTENDO && option(it, LAYOUT, "pos") == "label" }) keys = swapFace(keys)
        val line = "PAD;${p.slot};${Integer.toHexString(keys)};$lt;$rt;$lx;$ly;$rx;$ry"
        if (line == p.sent) return
        p.sent = line
        c.padState(p.slot, line)
    }

    private fun Src.leftStick() = deadZone(this, lx, ly)
    private fun Src.rightStick() = deadZone(this, rx, ry)

    /** Radial dead zone set for this controller (percent of the travel), the rest rescaled to full range. */
    private fun deadZone(s: Src, x: Int, y: Int): IntArray {
        val dz = (option(s, DEADZONE, "0").toIntOrNull() ?: 0) / 100f
        if (dz <= 0f) return intArrayOf(x, y)
        val m = Math.hypot(x.toDouble(), y.toDouble()).toFloat() / 32767f
        if (m <= dz) return intArrayOf(0, 0)
        val k = ((m - dz) / (1 - dz)).coerceAtMost(1f) / m
        return intArrayOf(stick(x / 32767f * k), stick(y / 32767f * k))
    }

    // ---------------- rumble ----------------

    /** Network thread: a game set the motors of the virtual controller of this slot. */
    fun rumble(slot: Int, large: Int, small: Int) = ui.post {
        val p = slots.getOrNull(slot) ?: return@post
        if (p.large == large && p.small == small) return@post
        p.large = large; p.small = small
        vibrate(p)
        ui.removeCallbacks(rumbleTick)
        if (slots.any { it != null && (it.large > 0 || it.small > 0) }) ui.postDelayed(rumbleTick, RUMBLE_REFRESH_MS)
    }

    /** XInput motors run until told otherwise; Android vibrations have a duration: renew them. */
    private val rumbleTick = object : Runnable {
        override fun run() {
            val on = pads.filter { it.large > 0 || it.small > 0 }
            on.forEach { p -> p.srcs.forEach { if (!it.external) vibrate(it, p.large, p.small) } }
            if (on.isNotEmpty() && client != null) ui.postDelayed(this, RUMBLE_REFRESH_MS)
        }
    }

    private fun vibrate(p: Pad) = p.srcs.forEach { vibrate(it, p.large, p.small) }

    /** Motors at the strength set for this controller. */
    private fun vibrate(s: Src, large: Int, small: Int) {
        val pct = option(s, RUMBLE, "100").toIntOrNull() ?: 100
        val l = (large * pct / 100).coerceIn(0, 255)
        val sm = (small * pct / 100).coerceIn(0, 255)
        val e = s.ext
        if (e != null) e.rumble(l, sm) else vibrate(s.devId, l, sm)
    }

    private fun vibrate(id: Int, large: Int, small: Int) {
        val dev = InputDevice.getDevice(id) ?: return
        try {
            if (Build.VERSION.SDK_INT >= 31) {
                val vm = dev.vibratorManager
                val ids = vm.vibratorIds
                if (ids.isEmpty()) return
                if (large == 0 && small == 0) { vm.cancel(); return }
                val c = CombinedVibration.startParallel()
                if (ids.size >= 2) {
                    // Like an Xbox controller: first motor = large (low frequency), second = small (high frequency).
                    if (large > 0) c.addVibrator(ids[0], VibrationEffect.createOneShot(RUMBLE_MS, large))
                    if (small > 0) c.addVibrator(ids[1], VibrationEffect.createOneShot(RUMBLE_MS, small))
                } else c.addVibrator(ids[0], VibrationEffect.createOneShot(RUMBLE_MS, maxOf(large, small)))
                vm.vibrate(c.combine())
            } else {
                @Suppress("DEPRECATION") val v = dev.vibrator
                if (!v.hasVibrator()) return
                val level = maxOf(large, small)
                if (level == 0) { v.cancel(); return }
                if (Build.VERSION.SDK_INT >= 26) {
                    v.vibrate(VibrationEffect.createOneShot(RUMBLE_MS, if (v.hasAmplitudeControl()) level else VibrationEffect.DEFAULT_AMPLITUDE))
                } else {
                    @Suppress("DEPRECATION") v.vibrate(RUMBLE_MS)
                }
            }
        } catch (e: Exception) {
            if (!rumbleError) { rumbleError = true; client?.post("LOG;gamepad rumble failed: ${e.javaClass.simpleName}: ${e.message}") }
        }
    }

    companion object {
        const val MAX = 4
        const val FULL = 0
        const val JOYCON_L = -1 // opposite sides: side == -other.side
        const val JOYCON_R = 1
        // Families (drawing, button names)
        const val OTHER = 0; const val XBOX = 1; const val PLAYSTATION = 2; const val NINTENDO = 3
        // Settings
        const val LAYOUT = "layout"; const val RUMBLE = "rumble"; const val DEADZONE = "dz"; const val PAIRING = "pair"
        private const val RUMBLE_MS = 2000L
        private const val RUMBLE_REFRESH_MS = 1500L
        // XInput button bits
        const val UP = 0x0001; const val DOWN = 0x0002; const val LEFT = 0x0004; const val RIGHT = 0x0008
        const val START = 0x0010; const val BACK = 0x0020; const val LS = 0x0040; const val RS = 0x0080
        const val LB = 0x0100; const val RB = 0x0200; const val GUIDE = 0x0400
        const val A = 0x1000; const val B = 0x2000; const val X = 0x4000; const val Y = 0x8000

        fun swapFace(k: Int): Int {
            val face = k and (A or B or X or Y)
            val swapped = (if (face and A != 0) B else 0) or (if (face and B != 0) A else 0) or
                (if (face and X != 0) Y else 0) or (if (face and Y != 0) X else 0)
            return (k and (A or B or X or Y).inv()) or swapped
        }

        fun padName(p: Pad) = if (p.srcs.size == 2) "Joy-Con (L+R)" else p.srcs[0].name

        /** A real gamepad / joystick (not the remote, not a virtual device). */
        fun isGamepad(d: InputDevice): Boolean {
            if (d.isVirtual) return false
            // TV makers inject the remote as uinput devices that claim to be gamepads
            // (TCL: "virtual-remote", "virtual-search"): no USB/Bluetooth ids, telling names.
            // Motion sensors and touchpads of PlayStation / Nintendo controllers are separate devices.
            val name = d.name.lowercase()
            if (EXCLUDED.any { it in name }) return false
            if (d.vendorId == 0 && d.productId == 0) return false
            val s = d.sources
            val joystick = (s and InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK &&
                d.getMotionRange(MotionEvent.AXIS_X) != null && d.getMotionRange(MotionEvent.AXIS_Y) != null
            val gamepad = (s and InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD &&
                d.hasKeys(KeyEvent.KEYCODE_BUTTON_A)[0]
            return joystick || gamepad
        }

        private val EXCLUDED = listOf("virtual", "remote", "search", "keyboard", "(imu)", "motion sensors", "touchpad")

        private fun familyOf(d: InputDevice): Int {
            val n = d.name.lowercase()
            return when {
                d.vendorId == 0x045e || "xbox" in n -> XBOX
                d.vendorId == 0x054c || "dualsense" in n || "dualshock" in n || "playstation" in n -> PLAYSTATION
                d.vendorId == 0x057e || "joy-con" in n || "pro controller" in n -> NINTENDO
                else -> OTHER
            }
        }

        /** Switch 1 Joy-Cons (kernel hid-nintendo driver): each one is its own device. */
        private fun sideOf(d: InputDevice): Int {
            if (d.vendorId != 0x057e) return FULL
            val name = d.name.lowercase()
            return when {
                d.productId == 0x2006 || "left joy-con" in name -> JOYCON_L
                d.productId == 0x2007 || "right joy-con" in name -> JOYCON_R
                else -> FULL
            }
        }

        /**
         * Joy-Con buttons by Linux key code (hid-nintendo): Android has no key layout for them, so the d-pad
         * comes as unknown keys. False for a code not listed (handled by key code instead).
         */
        private fun joyConKey(s: Src, scan: Int, down: Boolean): Boolean {
            fun set(bit: Int) { s.keys = if (down) s.keys or bit else s.keys and bit.inv() }
            if (s.side == JOYCON_L) when (scan) {
                0x220 -> set(UP); 0x221 -> set(DOWN); 0x222 -> set(LEFT); 0x223 -> set(RIGHT)
                0x136 -> set(LB)                 // L
                0x138 -> s.keyLt = down          // ZL
                0x13a -> set(BACK)               // -
                0x13d -> set(LS)
                0x135 -> s.capture = down
                0x137 -> s.sl = down             // SL, SR: reported as the triggers this half lacks
                0x139 -> s.sr = down
                else -> return false
            } else when (scan) {
                0x130 -> set(A)                  // Nintendo B (bottom)
                0x131 -> set(B)                  // Nintendo A (right)
                0x133 -> set(Y)                  // Nintendo X (top)
                0x134 -> set(X)                  // Nintendo Y (left)
                0x137 -> set(RB)                 // R
                0x139 -> s.keyRt = down          // ZR
                0x13b -> set(START)              // +
                0x13c -> set(GUIDE)              // Home
                0x13e -> set(RS)
                0x136 -> s.sl = down
                0x138 -> s.sr = down
                else -> return false
            }
            return true
        }

        /** Android key -> XInput button bit. */
        private fun bit(k: Int) = when (k) {
            KeyEvent.KEYCODE_DPAD_UP -> UP
            KeyEvent.KEYCODE_DPAD_DOWN -> DOWN
            KeyEvent.KEYCODE_DPAD_LEFT -> LEFT
            KeyEvent.KEYCODE_DPAD_RIGHT -> RIGHT
            KeyEvent.KEYCODE_BUTTON_START, KeyEvent.KEYCODE_MENU -> START
            KeyEvent.KEYCODE_BUTTON_SELECT, KeyEvent.KEYCODE_BACK -> BACK
            KeyEvent.KEYCODE_BUTTON_THUMBL -> LS
            KeyEvent.KEYCODE_BUTTON_THUMBR -> RS
            KeyEvent.KEYCODE_BUTTON_L1 -> LB
            KeyEvent.KEYCODE_BUTTON_R1 -> RB
            KeyEvent.KEYCODE_BUTTON_MODE -> GUIDE
            KeyEvent.KEYCODE_BUTTON_A, KeyEvent.KEYCODE_DPAD_CENTER -> A
            KeyEvent.KEYCODE_BUTTON_B -> B
            KeyEvent.KEYCODE_BUTTON_X -> X
            KeyEvent.KEYCODE_BUTTON_Y -> Y
            else -> 0
        }

        private fun isStick(d: InputDevice, axis: Int) = d.getMotionRange(axis)?.let { it.min < -0.5f } == true
        private fun isTrigger(d: InputDevice, axis: Int) = d.getMotionRange(axis)?.let { it.min >= -0.01f } == true

        /** Axis value with the device's dead zone ("flat") applied. */
        private fun axis(e: MotionEvent, d: InputDevice, axis: Int): Float {
            val r = d.getMotionRange(axis, e.source) ?: return 0f
            val v = e.getAxisValue(axis)
            return if (Math.abs(v) > r.flat) v else 0f
        }

        fun stick(v: Float) = (v * 32767f).toInt().coerceIn(-32768, 32767)
        private fun trigger(v: Float) = (v * 255f).toInt().coerceIn(0, 255)
    }
}
