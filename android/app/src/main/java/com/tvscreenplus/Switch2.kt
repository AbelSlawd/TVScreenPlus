package com.tvscreenplus

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import java.util.UUID

/**
 * Switch 2 controllers (Joy-Con 2 left/right, Pro Controller 2, NSO GameCube controller): Bluetooth LE with
 * Nintendo's own GATT protocol, which Android doesn't drive by itself. Found while their SYNC button is pressed
 * (they only reconnect to the console they were paired with), then fed to [Gamepads] as external sources.
 * Protocol (public reverse engineering): ndeadly/switch2_controller_research, JoeGeC/joycon2android docs/protocol.md.
 * Everything runs on the main thread (GATT callbacks are posted to it).
 */
@SuppressLint("MissingPermission")
class Switch2(private val context: Context, private val log: (String) -> Unit, private val notify: (String) -> Unit) {
    private val ui = Handler(Looper.getMainLooper())
    private val adapter = (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter
    private val ctls = HashMap<String, Ctl>() // by Bluetooth address
    private var scanning = false

    /** Where connected controllers go (set once, before scanning). */
    var pads: Gamepads? = null

    /** Runtime permissions still to ask for (Android 12+: nearby devices; before: location, for BLE scans). */
    fun missingPermissions(): Array<String> {
        val need = when {
            Build.VERSION.SDK_INT >= 31 -> arrayOf(Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT)
            Build.VERSION.SDK_INT >= 23 -> arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)
            else -> arrayOf()
        }
        return need.filter { Build.VERSION.SDK_INT >= 23 && context.checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }.toTypedArray()
    }

    /** Low-power scan for controllers in SYNC mode (hardware-filtered on Nintendo's manufacturer id). */
    fun startScan(byUser: Boolean = false) {
        if (scanning || missingPermissions().isNotEmpty()) return
        val scanner = adapter?.takeIf { it.isEnabled }?.bluetoothLeScanner ?: run {
            log("switch2: Bluetooth off")
            if (byUser) notify(L.t("sw2_bt_off"))
            return
        }
        try {
            val filter = ScanFilter.Builder().setManufacturerData(NINTENDO, ByteArray(0)).build()
            val settings = ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_POWER).build()
            scanner.startScan(listOf(filter), settings, scan)
            scanning = true
            ui.postDelayed(renew, 25 * 60_000L)
        } catch (e: Exception) { log("switch2: scan failed: ${e.message}") }
    }

    fun stopScan() {
        ui.removeCallbacks(renew)
        if (!scanning) return
        scanning = false
        try { adapter?.bluetoothLeScanner?.stopScan(scan) } catch (_: Exception) {}
    }

    // Android turns a scan running over 30 min into an "opportunistic" one that finds nothing: start it again before.
    private val renew = Runnable { if (scanning) { stopScan(); startScan() } }

    /** Scan off and every controller disconnected. */
    fun close() {
        stopScan()
        ctls.values.toList().forEach { it.drop() }
    }

    private val scan = object : ScanCallback() {
        override fun onScanResult(type: Int, r: ScanResult) {
            val d = r.scanRecord?.getManufacturerSpecificData(NINTENDO) ?: return
            // [5..6] product id, [10..15] address of the paired console: zero while SYNC is held.
            if (d.size < 16 || (10..15).any { d[it] != 0.toByte() }) return
            val model = MODELS[(d[5].toInt() and 0xFF) or ((d[6].toInt() and 0xFF) shl 8)] ?: return
            ui.post {
                if (ctls.containsKey(r.device.address)) return@post
                log("switch2: ${model.name} found (${r.device.address})")
                ctls[r.device.address] = Ctl(r.device, model).also { it.connect() }
            }
        }

        override fun onScanFailed(code: Int) { ui.post { scanning = false; log("switch2: scan error $code") } }
    }

    private class Model(val name: String, val side: Int, val rumbleChar: UUID?, val rumbleGroups: Int)

    /** One controller: GATT operations go one at a time (Android drops a second one issued too early). */
    private inner class Ctl(val device: BluetoothDevice, val model: Model) : Gamepads.External {
        private var gatt: BluetoothGatt? = null
        private val ops = ArrayDeque<Pair<Long, () -> Boolean>>() // delay before, operation (false = nothing to wait for)
        private var busy = false
        private var write: BluetoothGattCharacteristic? = null
        private var vibration: BluetoothGattCharacteristic? = null
        var ready = false
        var src: Gamepads.Src? = null
        private var slot = -1
        private var large = 0; private var small = 0; private var rumbleCounter = 0
        // Stick calibration: rest position averaged over the first reports, travel (seeded just under the
        // smallest measured, ~1200) widened as seen.
        private val center = IntArray(4); private var samples = 0
        private val span = IntArray(8) { 1180 }

        fun connect() {
            gatt = if (Build.VERSION.SDK_INT >= 23) device.connectGatt(context, false, callback, BluetoothDevice.TRANSPORT_LE)
                else device.connectGatt(context, false, callback)
            ui.postDelayed({ if (!ready && ctls[device.address] === this) { log("switch2: ${model.name} init timeout"); drop() } }, 15000)
        }

        fun drop() {
            if (ctls[device.address] !== this) return
            ctls.remove(device.address)
            ops.clear(); ui.removeCallbacks(next); ui.removeCallbacks(rumbleTick); ui.removeCallbacks(inputWatch)
            try { gatt?.disconnect(); gatt?.close() } catch (_: Exception) {}
            gatt = null
            pads?.extRemove(this)
            if (ready) notify(L.t("sw2_off", model.name))
            src = null; ready = false
        }

        fun join(g: Gamepads) { src = g.extAdd(this, model.side, model.name, "sw2_" + device.address, this) }

        override fun disconnect() = drop()

        // ---- operation queue ----
        private fun enqueue(delayMs: Long = 0, op: () -> Boolean) { ops.addLast(delayMs to op); if (!busy) pump() }

        private fun pump() {
            val (delay, op) = ops.removeFirstOrNull() ?: return
            busy = true
            ui.postDelayed({
                val waits = try { op() } catch (e: Exception) { log("switch2: ${e.message}"); false }
                if (waits) ui.postDelayed(next, 1000) else next.run() // the callback calls next sooner
            }, delay)
        }

        /** The running operation finished (its callback, or a 1 s timeout): start the next one. */
        private val next = object : Runnable {
            override fun run() { ui.removeCallbacks(this); busy = false; pump() }
        }
        private fun done() { ui.post { if (busy) next.run() } }

        private fun writeCmd(c: BluetoothGattCharacteristic, v: ByteArray): Boolean {
            val g = gatt ?: return false
            return if (Build.VERSION.SDK_INT >= 33) {
                g.writeCharacteristic(c, v, BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE) == 0
            } else {
                @Suppress("DEPRECATION") run { c.writeType = BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE; c.value = v; g.writeCharacteristic(c) }
            }
        }

        private fun subscribe(c: BluetoothGattCharacteristic): Boolean {
            val g = gatt ?: return false
            g.setCharacteristicNotification(c, true)
            val d = c.getDescriptor(CCCD) ?: return false
            return if (Build.VERSION.SDK_INT >= 33) {
                g.writeDescriptor(d, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE) == 0
            } else {
                @Suppress("DEPRECATION") run { d.value = BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE; g.writeDescriptor(d) }
            }
        }

        // ---- GATT callbacks (binder thread) ----
        private val callback = object : BluetoothGattCallback() {
            override fun onConnectionStateChange(g: BluetoothGatt, status: Int, state: Int) {
                ui.post {
                    if (state == BluetoothProfile.STATE_CONNECTED && status == BluetoothGatt.GATT_SUCCESS) {
                        // 63-byte reports need a bigger MTU than the default 23, then the services.
                        enqueue { g.requestMtu(247) }
                        enqueue { g.discoverServices() }
                    } else {
                        log("switch2: ${model.name} disconnected (status $status)")
                        drop()
                    }
                }
            }

            override fun onMtuChanged(g: BluetoothGatt, mtu: Int, status: Int) = done()
            override fun onDescriptorWrite(g: BluetoothGatt, d: BluetoothGattDescriptor, status: Int) = done()
            override fun onCharacteristicWrite(g: BluetoothGatt, c: BluetoothGattCharacteristic, status: Int) = done()

            override fun onServicesDiscovered(g: BluetoothGatt, status: Int) {
                ui.post { if (status == BluetoothGatt.GATT_SUCCESS) init(g) else { log("switch2: ${model.name}: service discovery failed ($status)"); drop() } }
                done()
            }

            @Deprecated("API < 33")
            override fun onCharacteristicChanged(g: BluetoothGatt, c: BluetoothGattCharacteristic) {
                @Suppress("DEPRECATION")
                if (Build.VERSION.SDK_INT < 33 && c.uuid == INPUT) c.value?.copyOf()?.let { v -> ui.post { report(v) } }
            }

            override fun onCharacteristicChanged(g: BluetoothGatt, c: BluetoothGattCharacteristic, value: ByteArray) {
                if (c.uuid == INPUT) ui.post { report(value) }
            }
        }

        /** Services found: subscribe to the replies and the input reports, then turn the reports on. */
        private fun init(g: BluetoothGatt) {
            val all = g.services.flatMap { it.characteristics }
            fun find(u: UUID) = all.firstOrNull { it.uuid == u }
            val input = find(INPUT); val w = find(WRITE)
            if (input == null || w == null) { log("switch2: ${model.name}: unknown services"); drop(); return }
            write = w
            vibration = model.rumbleChar?.let { find(it) }
            find(RESPONSE)?.let { r -> enqueue { subscribe(r) } }
            input0 = input
            startInput()
            enqueue(300) {
                ready = true
                g.requestConnectionPriority(BluetoothGatt.CONNECTION_PRIORITY_HIGH) // ~15 ms reports instead of ~30
                log("switch2: ${model.name} ready")
                notify(L.t("sw2_on", model.name))
                pads?.let { join(it) }
                if (slot < 0) led(-1)
                ui.postDelayed(inputWatch, 2000)
                false
            }
        }

        private var input0: BluetoothGattCharacteristic? = null
        private var reports = 0
        private var restarts = 0

        /** Input notifications on, then every feature (buttons, sticks, motion…): select, then enable. */
        private fun startInput() {
            val w = write ?: return; val input = input0 ?: return
            enqueue { subscribe(input) }
            // A controller misses a command sent too soon after the previous one.
            enqueue(500) { writeCmd(w, command(0x0C, 0x02, byteArrayOf(0xFF.toByte(), 0, 0, 0))) }
            enqueue(500) { writeCmd(w, command(0x0C, 0x04, byteArrayOf(0xFF.toByte(), 0, 0, 0))) }
        }

        /**
         * The enable commands get no reply: with several controllers on the TV's radio one can be lost, and the
         * controller then stays connected (it vibrates) but never sends its buttons. No report yet: send them again.
         */
        private val inputWatch = object : Runnable {
            override fun run() {
                if (gatt == null) return
                if (reports > 0 || ctls[device.address] !== this@Ctl) return
                if (restarts++ >= 5) { log("switch2: ${model.name}: no input reports"); return }
                log("switch2: ${model.name}: no input reports yet, enabling again ($restarts)")
                startInput()
                ui.postDelayed(this, 2500)
            }
        }

        // ---- input ----
        private fun report(d: ByteArray) {
            if (reports++ == 0) log("switch2: ${model.name}: first input report")
            val s = src ?: return
            if (d.size < 0x10 || ctls[device.address] !== this) return
            val b = (d[3].toInt() and 0xFF) or ((d[4].toInt() and 0xFF) shl 8) or ((d[5].toInt() and 0xFF) shl 16) or ((d[6].toInt() and 0xFF) shl 24)
            fun on(mask: Int) = b and mask != 0
            fun bit(mask: Int, xinput: Int) = if (on(mask)) xinput else 0
            var keys = 0
            if (model.side != Gamepads.JOYCON_R) {
                keys = keys or bit(0x02000000, Gamepads.UP) or bit(0x01000000, Gamepads.DOWN) or
                    bit(0x08000000, Gamepads.LEFT) or bit(0x04000000, Gamepads.RIGHT) or
                    bit(0x40000000, Gamepads.LB) or bit(0x00010000, Gamepads.BACK) or bit(0x00080000, Gamepads.LS)
                s.keyLt = on(0x80000000.toInt())
                s.capture = on(0x00200000)
            }
            if (model.side != Gamepads.JOYCON_L) {
                // Positions, as Xbox games expect: Nintendo B (bottom) = A, A (right) = B, Y (left) = X, X (top) = Y.
                keys = keys or bit(0x00000400, Gamepads.A) or bit(0x00000800, Gamepads.B) or
                    bit(0x00000100, Gamepads.X) or bit(0x00000200, Gamepads.Y) or
                    bit(0x00004000, Gamepads.RB) or bit(0x00020000, Gamepads.START) or
                    bit(0x00040000, Gamepads.RS) or bit(0x00100000, Gamepads.GUIDE)
                s.keyRt = on(0x00008000)
            }
            s.keys = keys
            s.sl = on(if (model.side == Gamepads.JOYCON_L) 0x20000000 else 0x00002000)
            s.sr = on(if (model.side == Gamepads.JOYCON_L) 0x10000000 else 0x00001000)
            // A Joy-Con's bytes for the stick it doesn't have are garbage.
            val left = if (model.side != Gamepads.JOYCON_R) raw(d, 0x0A) else null
            val right = if (model.side != Gamepads.JOYCON_L) raw(d, 0x0D) else null
            if (samples < CENTER_SAMPLES) {
                // shortcut: assumes the sticks are at rest while the controller connects; SYNC again if one drifts.
                // The first reports after the enable command can carry a 0 stick (not powered yet): not a rest position.
                if ((left ?: right)?.let { it[0] == 0 || it[1] == 0 } == true) return
                left?.let { center[0] += it[0]; center[1] += it[1] }
                right?.let { center[2] += it[0]; center[3] += it[1] }
                if (++samples == CENTER_SAMPLES) {
                    for (k in 0..3) center[k] /= CENTER_SAMPLES
                    log("switch2: ${model.name} stick center ${if (left != null) "${center[0]},${center[1]}" else "${center[2]},${center[3]}"}")
                }
            } else {
                left?.let { s.lx = axis(it[0], 0); s.ly = axis(it[1], 1) }
                right?.let { s.rx = axis(it[0], 2); s.ry = axis(it[1], 3) }
            }
            if (model === GAMECUBE && d.size > 0x3D) {
                // Analog L/R: rest ~33, end of the analog travel ~180 (the click past it is the button bit).
                s.lt = ((d[0x3C].toInt() and 0xFF) - 40).coerceIn(0, 140) * 255 / 140
                s.rt = ((d[0x3D].toInt() and 0xFF) - 40).coerceIn(0, 140) * 255 / 140
            }
            if (d.size > 0x20) {
                // Battery voltage (mV), ~0.6 V under the cell's: Nintendo's Joy-Con thresholds shifted down.
                val v = ((d[0x1F].toInt() and 0xFF) or ((d[0x20].toInt() and 0xFF) shl 8)) / 1000f
                s.battery = batteryPercent(v)
            }
            pads?.extChanged(this)
        }

        /** 12-bit packed stick at [o]: x, y (y up). */
        private fun raw(d: ByteArray, o: Int): IntArray {
            val v = (d[o].toInt() and 0xFF) or ((d[o + 1].toInt() and 0xFF) shl 8) or ((d[o + 2].toInt() and 0xFF) shl 16)
            return intArrayOf(v and 0xFFF, (v shr 12) and 0xFFF)
        }

        /** Raw axis [k] (lx, ly, rx, ry) -> Xbox range, around the rest position; each direction has its own travel. */
        private fun axis(raw: Int, k: Int): Int {
            val delta = raw - center[k]
            val i = k * 2 + (if (delta < 0) 1 else 0)
            if (Math.abs(delta) > span[i]) span[i] = Math.abs(delta)
            return Gamepads.stick(delta.toFloat() / span[i])
        }

        // ---- output ----
        override fun led(slot: Int) {
            this.slot = slot
            val w = write ?: return
            // Player 1 = 1 LED … player 4 = 4 LEDs; not assigned yet = all four.
            val mask = if (slot < 0) 0x0F else (1 shl (slot + 1)) - 1
            enqueue { writeCmd(w, command(0x09, 0x07, byteArrayOf(mask.toByte(), 0, 0, 0, 0, 0, 0, 0))) }
        }

        override fun rumble(large: Int, small: Int) {
            this.large = large; this.small = small
            ui.removeCallbacks(rumbleTick)
            rumbleTick.run()
        }

        /** HD rumble: one packet holds ~12 ms of vibration, so it is streamed while a motor runs, then stopped once. */
        private val rumbleTick = object : Runnable {
            override fun run() {
                val c = vibration ?: return
                val stop = large == 0 && small == 0
                if (!busy) {
                    val packet = rumblePacket(rumbleCounter++, small * RUMBLE_CAP / 255, large * RUMBLE_CAP / 255, model.rumbleGroups)
                    enqueue { writeCmd(c, packet) }
                    if (stop) return
                }
                ui.postDelayed(this, 12) // running, or the stop packet still to send
            }
        }
    }

    companion object {
        private const val NINTENDO = 0x0553
        private const val CENTER_SAMPLES = 8
        // Rumble amplitude for a full-strength game motor (max 0x3FF; SDL caps at 0x1C4, felt too strong on Joy-Con 2).
        private const val RUMBLE_CAP = 0x0A0
        private val INPUT = UUID.fromString("ab7de9be-89fe-49ad-828f-118f09df7fd2")
        private val WRITE = UUID.fromString("649d4ac9-8eb7-4e6c-af44-1ea54fe5f005")
        private val RESPONSE = UUID.fromString("c765a961-d9d8-4d36-a20a-5315b111836a")
        private val CCCD = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")
        private val GAMECUBE = Model("GameCube (NSO)", Gamepads.FULL, null, 0)
        private val MODELS = mapOf(
            0x2067 to Model("Joy-Con 2 (L)", Gamepads.JOYCON_L, UUID.fromString("289326cb-a471-485d-a8f4-240c14f18241"), 1),
            0x2066 to Model("Joy-Con 2 (R)", Gamepads.JOYCON_R, UUID.fromString("fa19b0fb-cd1f-46a7-84a1-bbb09e00c149"), 1),
            0x2069 to Model("Pro Controller 2", Gamepads.FULL, UUID.fromString("cc483f51-9258-427d-a939-630c31f72b05"), 2),
            0x2073 to GAMECUBE,
        )

        private val VOLTS = floatArrayOf(2.7f, 3.0f, 3.16f, 3.3f, 3.6f)

        /** 0, 25, 50, 75, 100 % at [VOLTS], linear in between. */
        fun batteryPercent(v: Float): Int {
            if (v <= VOLTS[0]) return 0
            for (i in 1 until VOLTS.size) if (v < VOLTS[i]) return (25 * (i - 1 + (v - VOLTS[i - 1]) / (VOLTS[i] - VOLTS[i - 1]))).toInt()
            return 100
        }

        /** Command: id, 0x91 (request), 0x01 (Bluetooth), sub-command, 0, data length, 0, 0, data. */
        fun command(id: Int, sub: Int, data: ByteArray) =
            byteArrayOf(id.toByte(), 0x91.toByte(), 0x01, sub.toByte(), 0, data.size.toByte(), 0, 0) + data

        /**
         * 0x00, then per motor group: 0x50 | counter, three identical 5-byte samples, each 40 bits little-endian:
         * high frequency (10 bits), high amplitude (10), low frequency (10), low amplitude (10).
         */
        fun rumblePacket(counter: Int, high: Int, low: Int, groups: Int): ByteArray {
            val packed = 0x187L or (high.coerceIn(0, 0x3FF).toLong() shl 10) or (0x112L shl 20) or (low.coerceIn(0, 0x3FF).toLong() shl 30)
            val group = ByteArray(16)
            group[0] = (0x50 or (counter and 0x0F)).toByte()
            for (f in 0 until 3) for (k in 0 until 5) group[1 + f * 5 + k] = (packed shr (8 * k)).toByte()
            val out = ByteArray(1 + 16 * groups)
            for (g in 0 until groups) group.copyInto(out, 1 + g * 16)
            return out
        }
    }
}
