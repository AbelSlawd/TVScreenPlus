package com.tvscreenplus

import android.content.Context
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
 * Gamepads paired with the TV (Bluetooth or USB) forwarded to the PC, where they appear as Xbox 360 controllers.
 * Only gamepad / joystick devices are captured: the remote keeps its role (Back = disconnect, OK = statistics).
 * Upstream lines: "PADON;slot;name", "PADOFF;slot", and on every change of state
 * "PAD;slot;buttons (hex, XInput bits);lt;rt;lx;ly;rx;ry" (triggers 0-255, sticks -32768..32767, Y up).
 * Runs on the UI thread (key / motion events, device listener).
 */
class Gamepads(context: Context, private val client: StreamClient) : InputManager.InputDeviceListener {
    private class Pad(val id: Int, val slot: Int, dev: InputDevice) {
        var keys = 0                 // XInput bits from buttons / d-pad keys
        var hat = 0                  // d-pad bits from the hat axes
        var keyLt = false
        var keyRt = false
        var lx = 0; var ly = 0; var rx = 0; var ry = 0; var lt = 0; var rt = 0
        var sent = ""
        var large = 0; var small = 0 // rumble requested by the PC
        // Right stick: Z/RZ on most Android gamepads, RX/RY on some. Z/RZ are triggers (0..1) on a few others.
        val rightX = if (isStick(dev, MotionEvent.AXIS_Z)) MotionEvent.AXIS_Z else MotionEvent.AXIS_RX
        val rightY = if (isStick(dev, MotionEvent.AXIS_RZ)) MotionEvent.AXIS_RZ else MotionEvent.AXIS_RY
        val ltAxes = intArrayOf(MotionEvent.AXIS_LTRIGGER, MotionEvent.AXIS_BRAKE) + (if (isTrigger(dev, MotionEvent.AXIS_Z)) intArrayOf(MotionEvent.AXIS_Z) else intArrayOf())
        val rtAxes = intArrayOf(MotionEvent.AXIS_RTRIGGER, MotionEvent.AXIS_GAS) + (if (isTrigger(dev, MotionEvent.AXIS_RZ)) intArrayOf(MotionEvent.AXIS_RZ) else intArrayOf())
    }

    private val im = context.getSystemService(Context.INPUT_SERVICE) as InputManager
    private val ui = Handler(Looper.getMainLooper())
    private val pads = HashMap<Int, Pad>() // by InputDevice id
    private var started = false
    private var rumbleError = false

    val count: Int get() = pads.size

    fun start() {
        if (started) return
        started = true
        im.registerInputDeviceListener(this, ui)
        // Controllers already paired: announced right away so games see them at launch.
        for (id in InputDevice.getDeviceIds()) InputDevice.getDevice(id)?.let { if (isGamepad(it)) add(it) }
    }

    fun stop() {
        if (!started) return
        started = false
        im.unregisterInputDeviceListener(this)
        ui.removeCallbacks(rumbleTick)
        for (p in pads.values) if (p.large > 0 || p.small > 0) { p.large = 0; p.small = 0; vibrate(p) }
        pads.clear()
    }

    /** True when the event came from a forwarded gamepad (consumed: it must not reach the TV UI). */
    fun onKey(e: KeyEvent): Boolean {
        if (!started) return false
        val pad = padFor(e.device) ?: return false
        val k = e.keyCode
        val bit = bit(k)
        when {
            bit != 0 -> pad.keys = if (e.action == KeyEvent.ACTION_DOWN) pad.keys or bit else pad.keys and bit.inv()
            k == KeyEvent.KEYCODE_BUTTON_L2 -> pad.keyLt = e.action == KeyEvent.ACTION_DOWN
            k == KeyEvent.KEYCODE_BUTTON_R2 -> pad.keyRt = e.action == KeyEvent.ACTION_DOWN
            else -> return false // volume, media keys…: let the TV handle them
        }
        send(pad)
        return true
    }

    fun onMotion(e: MotionEvent): Boolean {
        if (!started || e.action != MotionEvent.ACTION_MOVE) return false
        if (!e.isFromSource(InputDevice.SOURCE_JOYSTICK) && !e.isFromSource(InputDevice.SOURCE_GAMEPAD)) return false
        val dev = e.device ?: return false
        val pad = padFor(dev) ?: return false
        pad.lx = stick(axis(e, dev, MotionEvent.AXIS_X))
        pad.ly = stick(-axis(e, dev, MotionEvent.AXIS_Y))
        pad.rx = stick(axis(e, dev, pad.rightX))
        pad.ry = stick(-axis(e, dev, pad.rightY))
        pad.lt = trigger(pad.ltAxes.maxOf { axis(e, dev, it) })
        pad.rt = trigger(pad.rtAxes.maxOf { axis(e, dev, it) })
        val hx = e.getAxisValue(MotionEvent.AXIS_HAT_X)
        val hy = e.getAxisValue(MotionEvent.AXIS_HAT_Y)
        pad.hat = (if (hy < -0.5f) UP else 0) or (if (hy > 0.5f) DOWN else 0) or (if (hx < -0.5f) LEFT else 0) or (if (hx > 0.5f) RIGHT else 0)
        send(pad)
        return true // consumed: no d-pad keys synthesized from the hat / stick for the TV UI
    }

    /** Network thread: a game set the motors of the virtual controller of this slot. */
    fun rumble(slot: Int, large: Int, small: Int) = ui.post {
        val p = pads.values.firstOrNull { it.slot == slot } ?: return@post
        if (p.large == large && p.small == small) return@post
        p.large = large; p.small = small
        vibrate(p)
        ui.removeCallbacks(rumbleTick)
        if (pads.values.any { it.large > 0 || it.small > 0 }) ui.postDelayed(rumbleTick, RUMBLE_REFRESH_MS)
    }

    /** XInput motors run until told otherwise; Android vibrations have a duration: renew them. */
    private val rumbleTick = object : Runnable {
        override fun run() {
            val on = pads.values.filter { it.large > 0 || it.small > 0 }
            on.forEach { vibrate(it) }
            if (on.isNotEmpty() && started) ui.postDelayed(this, RUMBLE_REFRESH_MS)
        }
    }

    override fun onInputDeviceAdded(id: Int) {
        InputDevice.getDevice(id)?.let { if (isGamepad(it) && !pads.containsKey(id)) add(it) }
    }

    override fun onInputDeviceRemoved(id: Int) {
        val p = pads.remove(id) ?: return
        client.post("PADOFF;${p.slot}")
    }

    override fun onInputDeviceChanged(id: Int) {}

    private fun padFor(dev: InputDevice?): Pad? {
        if (dev == null) return null
        return pads[dev.id] ?: if (isGamepad(dev)) add(dev) else null
    }

    private fun add(dev: InputDevice): Pad? {
        val slot = (0 until MAX).firstOrNull { s -> pads.values.none { it.slot == s } } ?: return null // XInput: 4 controllers
        val p = Pad(dev.id, slot, dev)
        pads[dev.id] = p
        val name = dev.name.replace(';', ' ').replace('\n', ' ').take(40)
        client.post("PADON;$slot;$name")
        send(p)
        return p
    }

    private fun send(p: Pad) {
        val lt = if (p.keyLt) 255 else p.lt
        val rt = if (p.keyRt) 255 else p.rt
        val line = "PAD;${p.slot};${Integer.toHexString(p.keys or p.hat)};$lt;$rt;${p.lx};${p.ly};${p.rx};${p.ry}"
        if (line == p.sent) return
        p.sent = line
        client.padState(p.slot, line)
    }

    private fun vibrate(p: Pad) {
        val dev = InputDevice.getDevice(p.id) ?: return
        try {
            if (Build.VERSION.SDK_INT >= 31) {
                val vm = dev.vibratorManager
                val ids = vm.vibratorIds
                if (ids.isEmpty()) return
                if (p.large == 0 && p.small == 0) { vm.cancel(); return }
                val c = CombinedVibration.startParallel()
                if (ids.size >= 2) {
                    // Like an Xbox controller: first motor = large (low frequency), second = small (high frequency).
                    if (p.large > 0) c.addVibrator(ids[0], VibrationEffect.createOneShot(RUMBLE_MS, p.large))
                    if (p.small > 0) c.addVibrator(ids[1], VibrationEffect.createOneShot(RUMBLE_MS, p.small))
                } else c.addVibrator(ids[0], VibrationEffect.createOneShot(RUMBLE_MS, maxOf(p.large, p.small)))
                vm.vibrate(c.combine())
            } else {
                @Suppress("DEPRECATION") val v = dev.vibrator
                if (!v.hasVibrator()) return
                val level = maxOf(p.large, p.small)
                if (level == 0) { v.cancel(); return }
                if (Build.VERSION.SDK_INT >= 26) {
                    v.vibrate(VibrationEffect.createOneShot(RUMBLE_MS, if (v.hasAmplitudeControl()) level else VibrationEffect.DEFAULT_AMPLITUDE))
                } else {
                    @Suppress("DEPRECATION") v.vibrate(RUMBLE_MS)
                }
            }
        } catch (e: Exception) {
            if (!rumbleError) { rumbleError = true; client.post("LOG;gamepad rumble failed: ${e.javaClass.simpleName}: ${e.message}") }
        }
    }

    companion object {
        const val MAX = 4
        private const val RUMBLE_MS = 2000L
        private const val RUMBLE_REFRESH_MS = 1500L
        private const val UP = 0x0001
        private const val DOWN = 0x0002
        private const val LEFT = 0x0004
        private const val RIGHT = 0x0008

        /** A real gamepad / joystick (not the remote, not a virtual device). */
        fun isGamepad(d: InputDevice): Boolean {
            if (d.isVirtual) return false
            // TV makers inject the remote as uinput devices that claim to be gamepads
            // (TCL: "virtual-remote", "virtual-search"): no USB/Bluetooth ids, telling names.
            val name = d.name.lowercase()
            if ("virtual" in name || "remote" in name || "search" in name || "keyboard" in name) return false
            if (d.vendorId == 0 && d.productId == 0) return false
            val s = d.sources
            val joystick = (s and InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK &&
                d.getMotionRange(MotionEvent.AXIS_X) != null && d.getMotionRange(MotionEvent.AXIS_Y) != null
            val gamepad = (s and InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD &&
                d.hasKeys(KeyEvent.KEYCODE_BUTTON_A)[0]
            return joystick || gamepad
        }

        /** Android key -> XInput button bit. */
        private fun bit(k: Int) = when (k) {
            KeyEvent.KEYCODE_DPAD_UP -> UP
            KeyEvent.KEYCODE_DPAD_DOWN -> DOWN
            KeyEvent.KEYCODE_DPAD_LEFT -> LEFT
            KeyEvent.KEYCODE_DPAD_RIGHT -> RIGHT
            KeyEvent.KEYCODE_BUTTON_START, KeyEvent.KEYCODE_MENU -> 0x0010
            KeyEvent.KEYCODE_BUTTON_SELECT, KeyEvent.KEYCODE_BACK -> 0x0020
            KeyEvent.KEYCODE_BUTTON_THUMBL -> 0x0040
            KeyEvent.KEYCODE_BUTTON_THUMBR -> 0x0080
            KeyEvent.KEYCODE_BUTTON_L1 -> 0x0100
            KeyEvent.KEYCODE_BUTTON_R1 -> 0x0200
            KeyEvent.KEYCODE_BUTTON_MODE -> 0x0400
            KeyEvent.KEYCODE_BUTTON_A, KeyEvent.KEYCODE_DPAD_CENTER -> 0x1000
            KeyEvent.KEYCODE_BUTTON_B -> 0x2000
            KeyEvent.KEYCODE_BUTTON_X -> 0x4000
            KeyEvent.KEYCODE_BUTTON_Y -> 0x8000
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

        private fun stick(v: Float) = (v * 32767f).toInt().coerceIn(-32768, 32767)
        private fun trigger(v: Float) = (v * 255f).toInt().coerceIn(0, 255)
    }
}
