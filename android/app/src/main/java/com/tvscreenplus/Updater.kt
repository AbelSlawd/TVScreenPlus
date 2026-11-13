package com.tvscreenplus

import android.app.Activity
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import java.net.HttpURLConnection
import java.net.URL

/** Self-update from GitHub Releases: download the APK and hand it to the system installer. */
object Updater {
    private const val API = "https://api.github.com/repos/AbelSlawd/TVScreenPlus/releases/latest"
    private const val DOWNLOAD = "https://github.com/AbelSlawd/TVScreenPlus/releases/download/"
    const val ACTION_STATUS = "com.tvscreenplus.INSTALL_STATUS"
    private const val TOKEN = "token"
    /**
     * Random per process, carried by the installer callback we create. MainActivity is exported: without it, any app
     * could send ACTION_STATUS with an intent of its choice that we would start (STATUS_PENDING_USER_ACTION).
     */
    private val token = java.util.UUID.randomUUID().toString()

    class Release(val code: Int, val name: String, val apkUrl: String)

    private fun open(url: String) = (URL(url).openConnection() as HttpURLConnection).apply {
        connectTimeout = 8000; readTimeout = 20000; instanceFollowRedirects = true
        setRequestProperty("User-Agent", "TVScreenPlus-Updater")
    }

    /** Latest published release (tag v1.0.N, N = versionCode), or null. Network: call off the main thread. */
    fun latest(): Release? {
        val json = open(API).run { try { inputStream.bufferedReader().readText() } finally { disconnect() } }
        val tag = Regex("\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"").find(json)?.groupValues?.get(1) ?: return null
        // Only this repository's own release files (Android also refuses an APK not signed with our key).
        val apk = Regex("\"browser_download_url\"\\s*:\\s*\"(" + Regex.escape(DOWNLOAD) + "v[0-9.]+/TVScreenPlus-TV\\.apk)\"")
            .find(json)?.groupValues?.get(1) ?: return null
        val code = tag.substringAfterLast('.').toIntOrNull() ?: return null
        // Shown version: the release title "TVScreenPlus 1.1.0" (the tag's last number is only the update code).
        val name = Regex("\"name\"\\s*:\\s*\"TVScreenPlus ([0-9.]+)\"").find(json)?.groupValues?.get(1) ?: tag
        return Release(code, name, apk)
    }

    /** Streams the APK into a PackageInstaller session; the result comes back to MainActivity.onNewIntent. */
    fun install(activity: Activity, r: Release, progress: (Int) -> Unit) {
        val installer = activity.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        if (Build.VERSION.SDK_INT >= 31) params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        val id = installer.createSession(params)
        installer.openSession(id).use { session ->
            val c = open(r.apkUrl)
            try {
                val total = c.contentLength.toLong()
                c.inputStream.use { input ->
                    session.openWrite("app.apk", 0, if (total > 0) total else -1).use { out ->
                        val buf = ByteArray(64 shl 10)
                        var done = 0L
                        while (true) {
                            val n = input.read(buf)
                            if (n < 0) break
                            out.write(buf, 0, n)
                            done += n
                            if (total > 0) progress((done * 100 / total).toInt())
                        }
                        session.fsync(out)
                    }
                }
            } finally { c.disconnect() }
            val intent = Intent(activity, MainActivity::class.java).setAction(ACTION_STATUS).putExtra(TOKEN, token)
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
            session.commit(PendingIntent.getActivity(activity, 1, intent, flags).intentSender)
        }
    }

    /** Handles the installer callback. Returns a message to show, or null. */
    fun onStatus(activity: Activity, intent: Intent): String? {
        if (intent.getStringExtra(TOKEN) != token) return null
        return when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, -999)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                (intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT))?.let {
                    it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    activity.startActivity(it)
                }
                L.t("confirm_install")
            }
            PackageInstaller.STATUS_SUCCESS -> L.t("installed")
            PackageInstaller.STATUS_FAILURE_CONFLICT, PackageInstaller.STATUS_FAILURE_INCOMPATIBLE ->
                L.t("sig_conflict")
            PackageInstaller.STATUS_FAILURE_ABORTED -> L.t("cancelled")
            else -> L.t("update_failed", intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "?")
        }
    }
}
