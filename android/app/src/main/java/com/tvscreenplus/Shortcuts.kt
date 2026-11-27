package com.tvscreenplus

import android.annotation.TargetApi
import android.app.Activity
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.PackageInstaller
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Rect
import android.graphics.RectF
import android.graphics.Shader
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.android.apksig.ApkSigner
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream

/**
 * TV-home shortcuts to a PC application. Google TV doesn't let an app add icons to its home, so each shortcut is a
 * tiny app of its own: the template in the assets (module :shortcut) with its package, name and images rewritten,
 * signed with a key kept in this TV's keystore, and installed with the person's OK. Opening it opens this app with
 * its package (ACTION_OPEN), which connects to the PC and launches the application there.
 */
object Shortcuts {
    const val ACTION_OPEN = "com.tvscreenplus.SHORTCUT"
    const val ACTION_STATUS = "com.tvscreenplus.SHORTCUT_STATUS"
    const val EXTRA = "shortcut"
    private const val TOKEN = "token"
    private val token = java.util.UUID.randomUUID().toString() // same reason as in Updater
    // Placeholders of the template (see shortcut/build.gradle.kts and its manifest), stored as UTF-16 strings.
    private val PKG = "com.tvscreenplus.sc.x" + "a".repeat(31)
    private const val LABEL_MAX = 40
    private const val KEY = "shortcuts"

    /** Signing with a key of the keystore and the v2 scheme only. */
    val supported get() = Build.VERSION.SDK_INT >= 24

    /** A new package "<this app>.sc.x<random hex>", as long as the placeholder (rewritten in place). */
    fun newPackage(creator: String): String {
        val prefix = "$creator.sc.x"
        val hex = java.util.UUID.randomUUID().toString().replace("-", "") + java.util.UUID.randomUUID().toString().replace("-", "")
        return prefix + hex.take(PKG.length - prefix.length)
    }

    /** Makes and signs the shortcut app (off the main thread), then hands it to the system installer. */
    @TargetApi(24)
    fun install(activity: Activity, pkg: String, label: String, icon: Bitmap?) {
        require(pkg.length == PKG.length)
        val dir = File(activity.cacheDir, "shortcut").apply { mkdirs() }
        val unsigned = File(dir, "unsigned.apk"); val signed = File(dir, "signed.apk")
        ZipInputStream(activity.assets.open("shortcut.apk")).use { zin ->
            ZipOutputStream(unsigned.outputStream()).use { zout ->
                while (true) {
                    val e = zin.nextEntry ?: break
                    if (e.name.startsWith("META-INF/")) continue // the old signature data, if any
                    var data = zin.readBytes()
                    when {
                        e.name == "AndroidManifest.xml" -> data = setLabel(replace(data, PKG, pkg), label)
                        e.name == "resources.arsc" -> data = replace(data, PKG, pkg)
                        // The two images (names shortened by the build): the square icon and the wide banner.
                        e.name.endsWith(".png") -> {
                            val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                            BitmapFactory.decodeByteArray(data, 0, data.size, o)
                            data = png(if (o.outWidth == o.outHeight) iconImage(icon, o.outWidth) else bannerImage(icon, label, o.outWidth, o.outHeight))
                        }
                    }
                    zout.putNextEntry(ZipEntry(e.name).apply {
                        // Android 11+ wants it uncompressed (and aligned: apksig does it) in apps targeting 30 or more.
                        if (e.name == "resources.arsc") {
                            method = ZipEntry.STORED; size = data.size.toLong(); compressedSize = size
                            crc = java.util.zip.CRC32().apply { update(data) }.value
                        }
                    })
                    zout.write(data)
                    zout.closeEntry()
                }
            }
        }
        val (key, cert) = signingKey()
        ApkSigner.Builder(listOf(ApkSigner.SignerConfig.Builder("tvsp", key, listOf(cert)).build()))
            .setInputApk(unsigned).setOutputApk(signed)
            .setV1SigningEnabled(false).setV2SigningEnabled(true).setV3SigningEnabled(false)
            .build().sign()
        unsigned.delete()

        val installer = activity.packageManager.packageInstaller
        val id = installer.createSession(PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL))
        try { installer.openSession(id).use { session ->
            signed.inputStream().use { input -> session.openWrite("shortcut.apk", 0, signed.length()).use { out -> input.copyTo(out); session.fsync(out) } }
            val intent = Intent(activity, MainActivity::class.java).setAction(ACTION_STATUS).putExtra(TOKEN, token)
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
            session.commit(PendingIntent.getActivity(activity, 2, intent, flags).intentSender)
        } } catch (e: Exception) {
            try { installer.abandonSession(id) } catch (_: Exception) {}
            throw e
        } finally { signed.delete() }
    }

    /** Installer callback: a message to show, or null (not ours). */
    fun onStatus(activity: Activity, intent: Intent): String? {
        if (intent.getStringExtra(TOKEN) != token) return null
        return when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, -999)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)?.let { it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK); activity.startActivity(it) }
                null
            }
            PackageInstaller.STATUS_SUCCESS -> L.t("sc_done")
            PackageInstaller.STATUS_FAILURE_ABORTED -> L.t("sc_cancelled")
            else -> L.t("sc_failed", intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "?")
        }
    }

    /** Every occurrence of the UTF-16 string [from] replaced by [to] (same length). */
    private fun replace(data: ByteArray, from: String, to: String): ByteArray {
        val a = from.toByteArray(Charsets.UTF_16LE); val b = to.toByteArray(Charsets.UTF_16LE)
        var i = indexOf(data, a, 0)
        check(i >= 0) { "template: $from not found" }
        while (i >= 0) { b.copyInto(data, i); i = indexOf(data, a, i + a.size) }
        return data
    }

    /**
     * The 40-character label placeholder in the manifest's UTF-16 string pool: its length (the u16 just before) is set
     * to the new name's, the name written, then the terminator; the bytes left over are unused.
     */
    private fun setLabel(data: ByteArray, label: String): ByteArray {
        val p = indexOf(data, "_".repeat(LABEL_MAX).toByteArray(Charsets.UTF_16LE), 0)
        check(p >= 2) { "template: label not found" }
        var name = label.trim().ifEmpty { "TVScreenPlus" }
        if (name.length > LABEL_MAX) name = name.substring(0, if (Character.isHighSurrogate(name[LABEL_MAX - 1])) LABEL_MAX - 1 else LABEL_MAX)
        val chars = name.toByteArray(Charsets.UTF_16LE)
        data[p - 2] = name.length.toByte(); data[p - 1] = 0
        java.util.Arrays.fill(data, p, p + LABEL_MAX * 2 + 2, 0)
        chars.copyInto(data, p)
        return data
    }

    private fun indexOf(data: ByteArray, what: ByteArray, from: Int): Int {
        outer@ for (i in from..data.size - what.size) {
            for (j in what.indices) if (data[i + j] != what[j]) continue@outer
            return i
        }
        return -1
    }

    /** RSA key made once in this TV's keystore (it never leaves it), with its self-signed certificate. */
    @TargetApi(24)
    private fun signingKey(): Pair<PrivateKey, X509Certificate> {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        if (!ks.containsAlias(KEY)) KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_RSA, "AndroidKeyStore").apply {
            initialize(KeyGenParameterSpec.Builder(KEY, KeyProperties.PURPOSE_SIGN)
                .setKeySize(2048)
                .setDigests(KeyProperties.DIGEST_SHA256)
                .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1)
                .setCertificateSubject(javax.security.auth.x500.X500Principal("CN=TVScreenPlus shortcuts"))
                .build())
        }.generateKeyPair()
        return ks.getKey(KEY, null) as PrivateKey to ks.getCertificate(KEY) as X509Certificate
    }

    private fun png(b: Bitmap) = ByteArrayOutputStream().also { b.compress(Bitmap.CompressFormat.PNG, 100, it) }.toByteArray()

    private val BG1 = Color.rgb(16, 34, 58)
    private val BG2 = Color.rgb(8, 16, 28)

    private fun background(c: Canvas, w: Int, h: Int) {
        c.drawRect(0f, 0f, w.toFloat(), h.toFloat(), Paint().apply { shader = LinearGradient(0f, 0f, 0f, h.toFloat(), BG1, BG2, Shader.TileMode.CLAMP) })
    }

    /** Square icon (Google TV shows it in a circle): the application's icon in the middle. */
    private fun iconImage(icon: Bitmap?, size: Int): Bitmap {
        val b = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val c = Canvas(b)
        background(c, size, size)
        val s = size * 0.56f; val o = (size - s) / 2
        icon?.let { c.drawBitmap(it, null, RectF(o, o, o + s, o + s), Paint(Paint.FILTER_BITMAP_FLAG)) }
        return b
    }

    /** Wide banner (Android TV home): the icon on the left, the name on the right. */
    private fun bannerImage(icon: Bitmap?, label: String, w: Int, h: Int): Bitmap {
        val b = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
        val c = Canvas(b)
        background(c, w, h)
        val s = h * 0.5f; val left = h * 0.18f
        icon?.let { c.drawBitmap(it, null, RectF(left, (h - s) / 2, left + s, (h + s) / 2), Paint(Paint.FILTER_BITMAP_FLAG)) }
        val p = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; textSize = h * 0.15f; typeface = android.graphics.Typeface.DEFAULT_BOLD }
        val x = left + s + h * 0.12f; val room = w - x - h * 0.1f
        // Up to two lines, cut to the room left.
        val words = label.trim().split(Regex("\\s+")); val lines = ArrayList<String>(); var cur = ""
        for (word in words) {
            val t = if (cur.isEmpty()) word else "$cur $word"
            if (p.measureText(t) <= room || cur.isEmpty()) cur = t else { lines += cur; cur = word }
        }
        if (cur.isNotEmpty()) lines += cur
        val shown = lines.take(2).map { l -> var t = l; while (t.length > 1 && p.measureText(t) > room) t = t.dropLast(2) + "…"; t }
        val r = Rect(); p.getTextBounds("Ag", 0, 2, r)
        val lh = r.height() * 1.35f
        var y = h / 2f - (shown.size - 1) * lh / 2 + r.height() / 2f
        for (l in shown) { c.drawText(l, x, y, p); y += lh }
        return b
    }
}
