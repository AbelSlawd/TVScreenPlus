package com.tvscreenplus

import android.media.MediaCodec
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import android.view.Surface

object Codecs {
    /** Name and settings of the last decoder created (for the PC log). */
    @Volatile var lastDecoder = ""

    private class Variant(val label: String, val std: Boolean, val priority: Boolean, val rate: Boolean, val vendor: Boolean)

    private val VARIANTS = listOf(
        Variant("all options", true, true, true, true),
        Variant("no operating-rate", true, true, false, true),
        Variant("no operating-rate/priority", true, false, false, true),
        Variant("vendor keys only", false, false, false, true),
        Variant("plain", true, false, false, false),
    )

    /** Hardware decoder configured for minimum latency (richest config first, progressively plainer fallbacks). */
    fun lowLatencyDecoder(mime: String, w: Int, h: Int, surface: Surface?): MediaCodec {
        fun format(v: Variant) = MediaFormat.createVideoFormat(mime, w, h).apply {
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(w * h, 2 shl 20))
            if (v.std && Build.VERSION.SDK_INT >= 30) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            if (Build.VERSION.SDK_INT >= 23) {
                if (v.priority) setInteger(MediaFormat.KEY_PRIORITY, 0) // realtime
                if (v.rate) setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE.toInt())
            }
            if (v.vendor) {
                // Vendor low-latency switches (Qualcomm, MediaTek, HiSilicon, Amlogic/others).
                setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
                setInteger("vendor.qti-ext-dec-picture-order.enable", 1)
                setInteger("vendor.rtc-ext-dec-low-latency.enable", 1)
                setInteger("vendor.low-latency.enable", 1)
                setInteger("vendor.mtk.ext.dec.low-latency.enable", 1)
                setInteger("vendor.mtk.vdec.lowlatency", 1)
                setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-req", 1)
                setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-rdy", -1)
            }
        }
        for ((i, v) in VARIANTS.withIndex()) {
            val dec = try {
                MediaCodec.createDecoderByType(mime)
            } catch (e: Exception) {
                Log.w(StreamClient.TAG, "createDecoderByType failed", e)
                break
            }
            try {
                dec.configure(format(v), surface, null, 0)
                dec.start()
                lastDecoder = "${dec.name} ${w}x$h low-latency=variant ${i + 1}/${VARIANTS.size} (${v.label})"
                Log.i(StreamClient.TAG, "decoder $lastDecoder")
                return dec
            } catch (e: Exception) {
                Log.w(StreamClient.TAG, "configure variant ${i + 1}/${VARIANTS.size} (${v.label}) failed", e)
                try { dec.release() } catch (_: Exception) {}
            }
        }
        throw Exception(L.t("no_decoder"))
    }

    fun isHardware(ci: android.media.MediaCodecInfo) =
        if (Build.VERSION.SDK_INT >= 29) ci.isHardwareAccelerated
        else !ci.name.startsWith("OMX.google.") && !ci.name.startsWith("c2.android.")

    /** Highest 16:9 height the hardware decoder claims at 60 fps (0 = no hardware decoder). */
    fun declaredMaxHeight(mime: String): Int = try {
        var best = 0
        for (ci in MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos) {
            if (ci.isEncoder || ci.supportedTypes.none { it.equals(mime, true) } || !isHardware(ci)) continue
            val vc = ci.getCapabilitiesForType(mime).videoCapabilities ?: continue
            val h = HEIGHTS.firstOrNull { h ->
                try { vc.areSizeAndRateSupported(width(h), h, 60.0) } catch (_: Exception) { false }
            } ?: 1080 // capabilities not reported: assume a standard 1080p60 TV decoder
            best = maxOf(best, h)
        }
        best
    } catch (_: Exception) { 0 }

    val HEIGHTS = listOf(2160, 1440, 1080, 900, 720)
    fun width(h: Int) = ((h * 16 / 9) + 7) and 7.inv()
}
