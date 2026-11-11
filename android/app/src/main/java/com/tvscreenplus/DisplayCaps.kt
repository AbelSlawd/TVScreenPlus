package com.tvscreenplus

import android.app.Activity
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.os.Build

/** Short single-line diagnostics (for the PC log) to know whether 120 Hz streaming is possible. */
object DisplayCaps {
    @Suppress("DEPRECATION")
    fun report(activity: Activity): List<String> {
        val out = ArrayList<String>()
        try {
            val d = activity.windowManager.defaultDisplay
            if (Build.VERSION.SDK_INT >= 23) {
                fun fmt(m: android.view.Display.Mode) = "${m.physicalWidth}x${m.physicalHeight}@${Math.round(m.refreshRate)}"
                out.add("display current: " + fmt(d.mode))
                val modes = d.supportedModes.map { fmt(it) }.distinct()
                out.add(("display modes: " + modes.joinToString(" ")).take(290))
            } else {
                out.add("display current: refresh ${Math.round(d.refreshRate)} Hz (modes need API 23)")
            }
        } catch (e: Throwable) {
            out.add("display: error ${e.javaClass.simpleName}")
        }
        try {
            val sizes = listOf(3840 to 2160, 2560 to 1440, 1920 to 1080)
            for (mime in listOf("video/hevc", "video/avc")) {
                for (ci in MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos) {
                    try {
                        if (ci.isEncoder || ci.supportedTypes.none { it.equals(mime, true) } || !Codecs.isHardware(ci)) continue
                        val vc = ci.getCapabilitiesForType(mime).videoCapabilities
                        if (vc == null) { out.add("$mime ${ci.name}: no video capabilities"); continue }
                        val parts = sizes.map { (w, h) ->
                            val ok = try { vc.areSizeAndRateSupported(w, h, 120.0) } catch (_: Throwable) { false }
                            var range = "?"
                            if (Build.VERSION.SDK_INT >= 23) {
                                try {
                                    val r = vc.getAchievableFrameRatesFor(w, h)
                                    if (r != null) range = "${r.lower.toInt()}-${r.upper.toInt()}"
                                } catch (_: Throwable) {}
                            }
                            "${w}x$h 120fps=${if (ok) "yes" else "no"} achievable=$range"
                        }
                        out.add("$mime ${ci.name}: ${parts.joinToString("; ")}".take(299))
                    } catch (_: Throwable) {}
                }
            }
        } catch (e: Throwable) {
            out.add("codecs: error ${e.javaClass.simpleName}")
        }
        return out
    }
}
