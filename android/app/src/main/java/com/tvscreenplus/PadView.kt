package com.tvscreenplus

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.Typeface
import android.view.View
import com.tvscreenplus.Gamepads.Companion.A
import com.tvscreenplus.Gamepads.Companion.B
import com.tvscreenplus.Gamepads.Companion.BACK
import com.tvscreenplus.Gamepads.Companion.DOWN
import com.tvscreenplus.Gamepads.Companion.GUIDE
import com.tvscreenplus.Gamepads.Companion.LB
import com.tvscreenplus.Gamepads.Companion.LEFT
import com.tvscreenplus.Gamepads.Companion.LS
import com.tvscreenplus.Gamepads.Companion.RB
import com.tvscreenplus.Gamepads.Companion.RIGHT
import com.tvscreenplus.Gamepads.Companion.RS
import com.tvscreenplus.Gamepads.Companion.START
import com.tvscreenplus.Gamepads.Companion.UP

/**
 * Drawing of a controller with its live state: pressed buttons lit, sticks moving, triggers filling.
 * Silhouettes after the real ones: Xbox (Series), PlayStation (DualSense), Nintendo Switch Pro, other (Xbox shape,
 * grey), Joy-Con left / right / pair. Drawn in a 200 x 120 box scaled to the view.
 */
class PadView(context: Context) : View(context) {
    private class Part(val side: Int, val family: Int, val src: Gamepads.Src?)

    private var parts = listOf(Part(Gamepads.FULL, Gamepads.OTHER, null))
    private val p = Paint(Paint.ANTI_ALIAS_FLAG)
    private val line = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND }
    private val text = Paint(Paint.ANTI_ALIAS_FLAG).apply { typeface = Typeface.DEFAULT_BOLD; textAlign = Paint.Align.CENTER }
    private val r = RectF()

    /** Draws this controller and its state (call [invalidate] when it changes). */
    fun show(pad: Gamepads.Pad) {
        parts = pad.srcs.sortedBy { it.side }.map { Part(it.side, it.family, it) }
        invalidate()
    }

    /** Draws a controller of this kind, nothing pressed. */
    fun demo(family: Int, vararg sides: Int) {
        parts = sides.map { Part(it, family, null) }
        invalidate()
    }

    override fun onDraw(c: Canvas) {
        if (parts.isEmpty()) return
        val k = minOf(width / 200f, height / 120f)
        c.save()
        c.translate((width - 200 * k) / 2, (height - 120 * k) / 2)
        c.scale(k, k)
        val first = parts[0]
        when {
            first.side == Gamepads.FULL -> if (first.family == Gamepads.PLAYSTATION) dualSense(c, first.src) else xboxShape(c, first)
            parts.size == 1 -> {
                // A lone Joy-Con is held sideways, rail up: the left one turned left, the right one turned right.
                c.save(); c.rotate(if (first.side == Gamepads.JOYCON_L) -90f else 90f, 100f, 60f)
                joyCon(c, first, 74f)
                c.restore()
            }
            else -> { joyCon(c, parts[0], 47f); joyCon(c, parts[1], 101f) }
        }
        c.restore()
    }

    // ---------------- Xbox, Switch Pro, other ----------------

    /** Xbox Series silhouette (also other gamepads, in grey), or the Switch Pro Controller's: sticks offset. */
    private fun xboxShape(c: Canvas, part: Part) {
        val s = part.src
        val keys = s?.buttons ?: 0
        val nintendo = part.family == Gamepads.NINTENDO
        val body = when (part.family) {
            Gamepads.XBOX -> Color.rgb(46, 49, 56)
            Gamepads.NINTENDO -> Color.rgb(38, 38, 44)
            else -> Color.rgb(78, 90, 108)
        }
        // Triggers behind the bumpers, bumpers behind the body: only their top edge shows, as on the real thing.
        trigger(c, 38f, 8f, 60f, 17f, s?.ltValue ?: 0, 3.5f)
        trigger(c, 140f, 8f, 162f, 17f, s?.rtValue ?: 0, 3.5f)
        bumper(c, false, keys and LB != 0, shade(body, 0.75f))
        bumper(c, true, keys and RB != 0, shade(body, 0.75f))
        // Front view, ~1.45 wide for 1 high; rounded grips and a deep notch between them.
        val outline = if (nintendo) mirrored(24f, arrayOf(
            floatArrayOf(114f, 22f, 130f, 17f, 146f, 17f),
            floatArrayOf(162f, 17f, 171f, 26f, 172f, 36f),
            floatArrayOf(174f, 56f, 180f, 82f, 176f, 98f),
            floatArrayOf(172f, 116f, 156f, 120f, 150f, 112f),
            floatArrayOf(143f, 102f, 136f, 90f, 126f, 88f),
            floatArrayOf(116f, 87f, 108f, 86f, 100f, 86f),
        )) else mirrored(24f, arrayOf(
            floatArrayOf(114f, 23f, 130f, 16f, 146f, 16f),
            floatArrayOf(160f, 16f, 169f, 26f, 171f, 38f),
            floatArrayOf(173f, 52f, 178f, 80f, 175f, 96f),
            floatArrayOf(172f, 114f, 158f, 120f, 148f, 110f),
            floatArrayOf(140f, 100f, 134f, 88f, 124f, 86f),
            floatArrayOf(116f, 84.5f, 108f, 84f, 100f, 84f),
        ))
        fill(c, outline, body, 0.14f)

        val btn = Color.rgb(24, 25, 30)
        val cy = if (nintendo) 45f else 46f   // upper row: left stick, face buttons
        val ly = if (nintendo) 70f else 71f   // lower row: d-pad, right stick
        stick(c, 57f, cy, 15f, s?.lx ?: 0, s?.ly ?: 0, keys and LS != 0)
        dpad(c, 80f, ly, keys, when (part.family) { Gamepads.XBOX -> Color.rgb(84, 88, 98); Gamepads.NINTENDO -> Color.rgb(64, 66, 74); else -> btn },
            round = part.family == Gamepads.XBOX)
        face(c, 143f, cy, keys, part.family, 11.5f, 6.5f)
        stick(c, 120f, ly, 13.5f, s?.rx ?: 0, s?.ry ?: 0, keys and RS != 0)
        if (nintendo) {
            label(c, 88f, 33f, "−", keys and BACK != 0, Color.rgb(200, 200, 206))
            label(c, 112f, 33f, "+", keys and START != 0, Color.rgb(200, 200, 206))
            rrect(c, 87f, 43f, 93f, 49f, 1.3f, if (s?.capture == true) LIT else btn) // capture
            ringButton(c, 110f, 46f, 4f, keys and GUIDE != 0)                      // home
        } else {
            pill(c, 89f, 46f, 4.5f, 3f, keys and BACK != 0, btn)   // view
            pill(c, 111f, 46f, 4.5f, 3f, keys and START != 0, btn) // menu
            if (part.family == Gamepads.XBOX) {
                pill(c, 100f, 57f, 3.5f, 2.2f, false, btn) // share
                // Guide: the glowing Xbox button.
                p.color = if (keys and GUIDE != 0) LIT else Color.rgb(205, 208, 214)
                c.drawCircle(100f, 31f, 6.5f, p)
                line.color = body; line.strokeWidth = 1.6f
                c.drawLine(96.7f, 27.7f, 103.3f, 34.3f, line); c.drawLine(103.3f, 27.7f, 96.7f, 34.3f, line)
            } else dot(c, 100f, 31f, 5f, keys and GUIDE != 0, btn)
        }
    }

    /** Bumper following a top shoulder, drawn before the body so that only its edge shows. */
    private fun bumper(c: Canvas, right: Boolean, on: Boolean, color: Int, ps: Boolean = false) {
        val path = Path().apply {
            if (ps) { moveTo(128f, 20f); cubicTo(136f, 18.5f, 144f, 17f, 150f, 17f); cubicTo(164f, 17.5f, 173f, 24f, 176f, 32f) }
            else { moveTo(124f, 19f); cubicTo(132f, 16f, 140f, 14.5f, 146f, 14.5f); cubicTo(160f, 14.5f, 170f, 21f, 173f, 30f) }
            if (!right) transform(MIRROR)
        }
        line.color = if (on) LIT else color; line.strokeWidth = 6f
        c.drawPath(path, line)
    }

    // ---------------- DualSense ----------------

    private fun dualSense(c: Canvas, s: Gamepads.Src?) {
        val keys = s?.buttons ?: 0
        val white = Color.rgb(236, 238, 242)
        val black = Color.rgb(30, 31, 36)
        trigger(c, 40f, 10f, 62f, 19f, s?.ltValue ?: 0, 3.5f, black)
        trigger(c, 138f, 10f, 160f, 19f, s?.rtValue ?: 0, 3.5f, black)
        bumper(c, false, keys and LB != 0, black, ps = true)
        bumper(c, true, keys and RB != 0, black, ps = true)
        val outline = mirrored(20f, arrayOf(
            floatArrayOf(118f, 20f, 136f, 18f, 150f, 19f),
            floatArrayOf(164f, 20f, 174f, 26f, 177f, 36f),
            floatArrayOf(180f, 56f, 188f, 86f, 184f, 104f),
            floatArrayOf(181f, 116f, 166f, 119f, 160f, 110f),
            floatArrayOf(152f, 100f, 144f, 88f, 134f, 86f),
            floatArrayOf(124f, 85f, 112f, 86f, 100f, 86f),
        ))
        fill(c, outline, white, 0.08f)
        // Black centre plate under the touchpad, around the sticks.
        val plate = mirrored(44f, arrayOf(
            floatArrayOf(114f, 44f, 128f, 44f, 132f, 48f),
            floatArrayOf(138f, 52f, 144f, 58f, 144f, 66f),
            floatArrayOf(144f, 78f, 142f, 86f, 132f, 86f),
            floatArrayOf(120f, 86f, 110f, 86f, 100f, 86f),
        ))
        fill(c, plate, black, 0.1f)
        // Touchpad with the light bar along its sides.
        rrect(c, 70f, 20f, 130f, 50f, 6f, Color.rgb(222, 225, 231))
        line.color = Color.rgb(200, 204, 212); line.strokeWidth = 0.8f
        r.set(70f, 20f, 130f, 50f); c.drawRoundRect(r, 6f, 6f, line)
        line.color = Color.rgb(90, 160, 255); line.strokeWidth = 1.5f
        c.drawLine(70.5f, 29f, 70.5f, 44f, line); c.drawLine(129.5f, 29f, 129.5f, 44f, line)

        val btn = Color.rgb(58, 60, 68)
        psDpad(c, 46f, 46f, keys, Color.rgb(176, 181, 190))
        face(c, 154f, 46f, keys, Gamepads.PLAYSTATION, 11f, 6f)
        stick(c, 78f, 70f, 12.5f, s?.lx ?: 0, s?.ly ?: 0, keys and LS != 0)
        stick(c, 122f, 70f, 12.5f, s?.rx ?: 0, s?.ry ?: 0, keys and RS != 0)
        pill(c, 64f, 26f, 1.8f, 4.5f, keys and BACK != 0, btn)   // create
        pill(c, 136f, 26f, 1.8f, 4.5f, keys and START != 0, btn) // options
        dot(c, 100f, 72f, 4f, keys and GUIDE != 0, Color.rgb(70, 72, 80)) // PS
        pill(c, 100f, 80f, 3.5f, 1.2f, false, Color.rgb(90, 92, 100))     // mute
    }

    /** DualSense d-pad: four separate arrow-shaped buttons. */
    private fun psDpad(c: Canvas, cx: Float, cy: Float, keys: Int, base: Int) {
        val dirs = arrayOf(UP to 0f, RIGHT to 90f, DOWN to 180f, LEFT to 270f)
        for ((bit, angle) in dirs) {
            c.save(); c.rotate(angle, cx, cy)
            val arrow = Path().apply {
                moveTo(cx - 4f, cy - 14f); lineTo(cx + 4f, cy - 14f); lineTo(cx + 4f, cy - 6f); lineTo(cx, cy - 2.4f); lineTo(cx - 4f, cy - 6f); close()
            }
            p.color = if (keys and bit != 0) LIT else base
            c.drawPath(arrow, p)
            c.restore()
        }
    }

    // ---------------- Joy-Cons ----------------

    /** One Joy-Con, upright, its left edge at [x] (52 wide): round outer side, flat rail side. */
    private fun joyCon(c: Canvas, part: Part, x: Float) {
        val s = part.src
        val keys = s?.buttons ?: 0
        val left = part.side == Gamepads.JOYCON_L
        val body = if (left) Color.rgb(0, 174, 239) else Color.rgb(255, 82, 76)
        val btn = Color.rgb(28, 29, 34)
        val w = 52f
        // ZL / ZR behind the outer top corner, L / R along it.
        val zx = if (left) x + 6 else x + w - 30
        trigger(c, zx, 2f, zx + 24, 20f, if (left) s?.ltValue ?: 0 else s?.rtValue ?: 0, 6f)
        val shoulder = Path().apply {
            if (left) { moveTo(x + 40, 9f); cubicTo(x + 24, 7f, x + 10, 10f, x + 5, 22f) }
            else { moveTo(x + w - 40, 9f); cubicTo(x + w - 24, 7f, x + w - 10, 10f, x + w - 5, 22f) }
        }
        line.color = if (keys and (if (left) LB else RB) != 0) LIT else shade(body, 0.7f); line.strokeWidth = 6f
        c.drawPath(shoulder, line)
        // Body: big radius on the outer corners, small on the rail side.
        val outer = 24f; val inner = 3f
        val radii = if (left) floatArrayOf(outer, outer, inner, inner, inner, inner, outer, outer)
        else floatArrayOf(inner, inner, outer, outer, outer, outer, inner, inner)
        val shape = Path().apply { r.set(x, 9f, x + w, 117f); addRoundRect(r, radii, Path.Direction.CW) }
        fill(c, shape, body, 0.12f)
        // Rail, SL / SR on it (sideways, SL is on the left: top of the left Joy-Con's rail, bottom of the right one's).
        val railX = if (left) x + w - 5 else x
        rrect(c, railX, 12f, railX + 5, 114f, 1.5f, Color.rgb(38, 40, 46))
        val top = if (left) s?.sl else s?.sr
        val bottom = if (left) s?.sr else s?.sl
        rrect(c, railX + 0.8f, 30f, railX + 4.2f, 46f, 1.5f, if (top == true) LIT else Color.rgb(96, 100, 110))
        rrect(c, railX + 0.8f, 80f, railX + 4.2f, 96f, 1.5f, if (bottom == true) LIT else Color.rgb(96, 100, 110))

        val cx = if (left) x + 24 else x + 28 // centre line of the controls, away from the rail
        if (left) {
            label(c, x + 38, 23f, "−", keys and BACK != 0, btn)
            stick(c, cx, 40f, 11.5f, s?.lx ?: 0, s?.ly ?: 0, keys and LS != 0)
            arrowButton(c, cx, 63f, 0f, keys and UP != 0, btn)
            arrowButton(c, cx + 11, 74f, 90f, keys and RIGHT != 0, btn)
            arrowButton(c, cx, 85f, 180f, keys and DOWN != 0, btn)
            arrowButton(c, cx - 11, 74f, 270f, keys and LEFT != 0, btn)
            rrect(c, cx + 5, 99f, cx + 12, 106f, 1.2f, if (s?.capture == true) LIT else btn) // capture
            p.color = shade(btn, 2f); c.drawCircle(cx + 8.5f, 102.5f, 1.6f, p)
        } else {
            label(c, x + 14, 23f, "+", keys and START != 0, btn)
            face(c, cx, 40f, keys, Gamepads.NINTENDO, 10f, 5f, btn)
            stick(c, cx, 74f, 11.5f, s?.rx ?: 0, s?.ry ?: 0, keys and RS != 0)
            ringButton(c, cx - 8, 101f, 4f, keys and GUIDE != 0)
        }
    }

    /** Round Joy-Con direction button with its arrow. */
    private fun arrowButton(c: Canvas, cx: Float, cy: Float, angle: Float, on: Boolean, base: Int) {
        dot(c, cx, cy, 5f, on, base)
        c.save(); c.rotate(angle, cx, cy)
        p.color = if (on) Color.rgb(20, 20, 24) else Color.rgb(150, 154, 164)
        c.drawPath(Path().apply { moveTo(cx, cy - 2.2f); lineTo(cx + 2f, cy + 1.3f); lineTo(cx - 2f, cy + 1.3f); close() }, p)
        c.restore()
    }

    // ---------------- pieces ----------------

    /** Face buttons by position: bottom = A bit, right = B, left = X, top = Y; names and colours by family. */
    private fun face(c: Canvas, cx: Float, cy: Float, keys: Int, family: Int, d: Float, rad: Float, base: Int = Color.rgb(24, 25, 30)) {
        val names = if (family == Gamepads.NINTENDO) arrayOf("B", "A", "Y", "X") else arrayOf("A", "B", "X", "Y")
        val colors = when (family) {
            Gamepads.XBOX -> intArrayOf(Color.rgb(110, 200, 90), Color.rgb(235, 80, 70), Color.rgb(70, 140, 235), Color.rgb(245, 205, 50))
            Gamepads.PLAYSTATION -> intArrayOf(Color.rgb(140, 170, 225), Color.rgb(225, 120, 130), Color.rgb(210, 150, 205), Color.rgb(100, 200, 180))
            else -> intArrayOf(Color.rgb(225, 225, 230), Color.rgb(225, 225, 230), Color.rgb(225, 225, 230), Color.rgb(225, 225, 230))
        }
        val bg = if (family == Gamepads.PLAYSTATION) Color.rgb(30, 31, 36) else base
        val pos = arrayOf(floatArrayOf(0f, d), floatArrayOf(d, 0f), floatArrayOf(-d, 0f), floatArrayOf(0f, -d))
        val bits = intArrayOf(A, B, Gamepads.X, Gamepads.Y) // X, Y alone are View.X / View.Y here
        for (i in 0..3) {
            val x = cx + pos[i][0]; val y = cy + pos[i][1]
            val on = keys and bits[i] != 0
            dot(c, x, y, rad, on, bg)
            if (!on) { p.color = Color.argb(40, 255, 255, 255); c.drawCircle(x - rad * 0.2f, y - rad * 0.25f, rad * 0.6f, p) } // shine
            val col = if (on) Color.rgb(20, 20, 24) else colors[i]
            if (family == Gamepads.PLAYSTATION) symbol(c, i, x, y, rad * 0.48f, col)
            else { text.color = col; text.textSize = rad * 1.25f; c.drawText(names[i], x, y + rad * 0.44f, text) }
        }
    }

    /** ✕ ○ □ △ drawn (fonts may lack them). */
    private fun symbol(c: Canvas, i: Int, x: Float, y: Float, s: Float, col: Int) {
        line.color = col; line.strokeWidth = s * 0.42f
        when (i) {
            0 -> { c.drawLine(x - s, y - s, x + s, y + s, line); c.drawLine(x - s, y + s, x + s, y - s, line) }
            1 -> c.drawCircle(x, y, s, line)
            2 -> c.drawRect(x - s * 0.9f, y - s * 0.9f, x + s * 0.9f, y + s * 0.9f, line)
            else -> c.drawPath(Path().apply { moveTo(x, y - s * 1.05f); lineTo(x + s, y + s * 0.75f); lineTo(x - s, y + s * 0.75f); close() }, line)
        }
    }

    /** Cross d-pad (Xbox: on a round well). */
    private fun dpad(c: Canvas, cx: Float, cy: Float, keys: Int, base: Int, round: Boolean) {
        if (round) { p.color = Color.rgb(28, 30, 35); c.drawCircle(cx, cy, 14.5f, p) }
        val a = 7.5f
        rrect(c, cx - a / 2, cy - a / 2, cx + a / 2, cy + a / 2, 0f, base)
        rrect(c, cx - a / 2, cy - a * 1.5f, cx + a / 2, cy - a / 2 + 0.5f, 1.5f, if (keys and UP != 0) LIT else base)
        rrect(c, cx - a / 2, cy + a / 2 - 0.5f, cx + a / 2, cy + a * 1.5f, 1.5f, if (keys and DOWN != 0) LIT else base)
        rrect(c, cx - a * 1.5f, cy - a / 2, cx - a / 2 + 0.5f, cy + a / 2, 1.5f, if (keys and LEFT != 0) LIT else base)
        rrect(c, cx + a / 2 - 0.5f, cy - a / 2, cx + a * 1.5f, cy + a / 2, 1.5f, if (keys and RIGHT != 0) LIT else base)
    }

    /** Stick: dark well, concave cap with a rim, moved by the stick (Y up), lit when clicked. */
    private fun stick(c: Canvas, cx: Float, cy: Float, rad: Float, x: Int, y: Int, click: Boolean) {
        p.color = Color.rgb(16, 17, 21); c.drawCircle(cx, cy, rad, p)
        val off = rad * 0.4f
        val sx = cx + x / 32767f * off; val sy = cy - y / 32767f * off
        p.color = if (click) LIT else Color.rgb(54, 57, 65); c.drawCircle(sx, sy, rad * 0.7f, p)
        p.color = if (click) shade(LIT, 0.85f) else Color.rgb(38, 40, 47); c.drawCircle(sx, sy, rad * 0.5f, p)
    }

    /** Trigger, filled from the bottom as it is pressed (0-255). */
    private fun trigger(c: Canvas, l: Float, t: Float, rt: Float, b: Float, v: Int, rad: Float, base: Int = Color.rgb(30, 32, 38)) {
        rrect(c, l, t, rt, b, rad, base)
        if (v > 0) {
            c.save(); c.clipRect(l, t + (b - t) * (1 - v / 255f), rt, b)
            rrect(c, l, t, rt, b, rad, LIT)
            c.restore()
        }
    }

    /** Nintendo HOME button: dark disc with a light ring. */
    private fun ringButton(c: Canvas, cx: Float, cy: Float, rad: Float, on: Boolean) {
        p.color = Color.rgb(150, 154, 164); c.drawCircle(cx, cy, rad, p)
        p.color = if (on) LIT else Color.rgb(24, 25, 30); c.drawCircle(cx, cy, rad * 0.72f, p)
    }

    private fun pill(c: Canvas, cx: Float, cy: Float, hw: Float, hh: Float, on: Boolean, base: Int) =
        rrect(c, cx - hw, cy - hh, cx + hw, cy + hh, minOf(hw, hh), if (on) LIT else base)

    private fun dot(c: Canvas, cx: Float, cy: Float, rad: Float, on: Boolean, base: Int) {
        p.color = if (on) LIT else base
        c.drawCircle(cx, cy, rad, p)
    }

    private fun label(c: Canvas, cx: Float, cy: Float, s: String, on: Boolean, color: Int) {
        text.color = if (on) LIT else color; text.textSize = 11f
        c.drawText(s, cx, cy + 4f, text)
    }

    private fun rrect(c: Canvas, l: Float, t: Float, rt: Float, b: Float, rad: Float, color: Int) {
        p.color = color; r.set(l, t, rt, b); c.drawRoundRect(r, rad, rad, p)
    }

    /** Fills a shape with a soft top-lit gradient ([light] = how much lighter the top is). */
    private fun fill(c: Canvas, path: Path, color: Int, light: Float) {
        path.computeBounds(r, true)
        p.shader = LinearGradient(0f, r.top, 0f, r.bottom, shade(color, 1 + light), shade(color, 1 - light), Shader.TileMode.CLAMP)
        c.drawPath(path, p)
        p.shader = null
    }

    private fun shade(col: Int, k: Float) = Color.rgb(
        (Color.red(col) * k).toInt().coerceIn(0, 255), (Color.green(col) * k).toInt().coerceIn(0, 255), (Color.blue(col) * k).toInt().coerceIn(0, 255))

    companion object {
        /** Pressed: amber, readable on every body colour. */
        private val LIT = Color.rgb(255, 196, 40)
        private val MIRROR = android.graphics.Matrix().apply { setScale(-1f, 1f, 100f, 0f) }

        /**
         * Symmetric outline: [segs] are the cubic curves of the right half, from the top centre (100, [top]) to the
         * bottom centre; the left half is their mirror image, drawn back up.
         */
        private fun mirrored(top: Float, segs: Array<FloatArray>): Path {
            val path = Path()
            path.moveTo(100f, top)
            for (s in segs) path.cubicTo(s[0], s[1], s[2], s[3], s[4], s[5])
            for (i in segs.indices.reversed()) {
                val s = segs[i]
                val sx = if (i == 0) 100f else segs[i - 1][4]
                val sy = if (i == 0) top else segs[i - 1][5]
                path.cubicTo(200 - s[2], s[3], 200 - s[0], s[1], 200 - sx, sy)
            }
            path.close()
            return path
        }
    }
}
