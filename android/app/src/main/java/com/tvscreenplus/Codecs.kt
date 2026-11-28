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

    /** rate: operating rate asked (0 = none): the decoder's clock is raised for it. */
    private class Variant(val label: String, val std: Boolean, val priority: Boolean, val rate: Int, val vendor: Boolean)

    private val VARIANTS = listOf(
        Variant("all options", true, true, Short.MAX_VALUE.toInt(), true),
        Variant("no operating-rate", true, true, 0, true),
        Variant("no operating-rate/priority", true, false, 0, true),
        Variant("vendor keys only", false, false, 0, true),
        Variant("plain", true, false, 0, false),
    )

    /** Hardware decoder configured for minimum latency (richest config first, progressively plainer fallbacks). */
    fun lowLatencyDecoder(mime: String, w: Int, h: Int, surface: Surface?): MediaCodec {
        fun format(v: Variant, name: String) = MediaFormat.createVideoFormat(mime, w, h).apply {
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(w * h, 2 shl 20))
            if (v.std && Build.VERSION.SDK_INT >= 30) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            if (Build.VERSION.SDK_INT >= 23) {
                if (v.priority) setInteger(MediaFormat.KEY_PRIORITY, 0) // realtime
                if (v.rate > 0) setInteger(MediaFormat.KEY_OPERATING_RATE, v.rate)
            }
            if (v.vendor) {
                // Vendor low-latency switches: only the decoder's own maker (keys of another one can upset it).
                val n = name.lowercase()
                val mtk = "mtk" in n; val qti = "qti" in n || "qcom" in n; val hisi = "hisi" in n
                val unknown = !mtk && !qti && !hisi
                setInteger("vendor.low-latency.enable", 1)
                if (qti || unknown) {
                    setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
                    setInteger("vendor.qti-ext-dec-picture-order.enable", 1)
                }
                if (unknown) setInteger("vendor.rtc-ext-dec-low-latency.enable", 1)
                if (mtk || unknown) {
                    setInteger("vendor.mtk.ext.dec.low-latency.enable", 1)
                    setInteger("vendor.mtk.vdec.lowlatency", 1)
                }
                if (hisi || unknown) {
                    setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-req", 1)
                    setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-rdy", -1)
                }
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
                dec.configure(format(v, dec.name), surface, null, 0)
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
            } ?: 1080 // claims none of these at 60 fps (some decoders under-report): assume a standard 1080p60 TV decoder
            best = maxOf(best, h)
        }
        best
    } catch (_: Exception) { 0 }

    val HEIGHTS = listOf(2160, 1440, 1080, 900, 720)
    fun width(h: Int) = ((h * 16 / 9) + 7) and 7.inv()
}
