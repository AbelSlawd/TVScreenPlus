package com.tvscreenplus

import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Encrypted, authenticated PC connection (AES-256-GCM), same format as SecureChannel.cs on the PC.
 * km = HMAC-SHA256(pairing key, "tvsp-sec" + challenge of this connection), then one key per direction:
 * HMAC(km, "up" | "down" | "udp"). TCP: each write is a record [u32 length][ciphertext + 16-byte tag], the nonce is
 * a counter per direction (4 zeros + u64 big-endian). UDP: [0xFF][u64 counter][ciphertext + tag].
 */
object SecureChannel {
    const val UP = 1
    const val DOWN = 2
    const val TAG = 16

    fun base(pairingKey: ByteArray, challenge: ByteArray) = hmac(pairingKey, "tvsp-sec".toByteArray() + challenge)
    fun key(base: ByteArray, use: String) = hmac(base, use.toByteArray())

    private fun hmac(k: ByteArray, d: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run { init(SecretKeySpec(k, "HmacSHA256")); doFinal(d) }

    fun nonce(c: Long) = ByteArray(12).also { for (i in 0 until 8) it[4 + i] = (c ushr (56 - 8 * i)).toByte() }

    /** AES-GCM speed of this TV (MB/s), measured once: the PC decides from it whether the picture is encrypted. */
    val speedMBps: Int by lazy {
        try {
            val g = Gcm(ByteArray(32))
            val src = ByteArray(256 shl 10); val dst = ByteArray(src.size + TAG)
            g.seal(0, src, 0, src.size, dst, 0) // warm-up (provider loading, JIT)
            val t = System.nanoTime()
            for (i in 1..8) g.seal(i.toLong(), src, 0, src.size, dst, 0)
            val secs = (System.nanoTime() - t) / 1e9
            (8.0 * src.size / 1e6 / secs).toInt().coerceIn(1, 99_999)
        } catch (_: Exception) { 0 }
    }
}

class Gcm(key: ByteArray) {
    private val k = SecretKeySpec(key, "AES")
    private val enc = Cipher.getInstance("AES/GCM/NoPadding")
    private val dec = Cipher.getInstance("AES/GCM/NoPadding")

    /** src[off, off+len) -> dst[d, d+len+16) (ciphertext then tag). */
    fun seal(counter: Long, src: ByteArray, off: Int, len: Int, dst: ByteArray, d: Int) = synchronized(enc) {
        enc.init(Cipher.ENCRYPT_MODE, k, GCMParameterSpec(128, SecureChannel.nonce(counter)))
        enc.doFinal(src, off, len, dst, d)
    }

    /** src[off, off+len+16) -> dst[d, d+len); false if it was not sent as is by the PC. */
    fun open(counter: Long, src: ByteArray, off: Int, len: Int, dst: ByteArray, d: Int): Boolean = synchronized(dec) {
        try {
            dec.init(Cipher.DECRYPT_MODE, k, GCMParameterSpec(128, SecureChannel.nonce(counter)))
            dec.doFinal(src, off, len + SecureChannel.TAG, dst, d)
            true
        } catch (_: AEADBadTagException) { false }
    }
}

/** Reads the PC's encrypted records; anything changed, replayed or reordered ends the connection. */
class SecureIn(private val inner: InputStream, key: ByteArray) : InputStream() {
    private val gcm = Gcm(key)
    private var counter = 0L
    private var rec = ByteArray(64 shl 10)
    private var plain = ByteArray(64 shl 10)
    private var pos = 0
    private var len = 0
    private val head = ByteArray(4)

    override fun read(): Int {
        if (pos == len && !next()) return -1
        return plain[pos++].toInt() and 0xFF
    }

    override fun read(b: ByteArray, off: Int, n: Int): Int {
        if (n == 0) return 0
        if (pos == len && !next()) return -1
        val c = minOf(n, len - pos)
        System.arraycopy(plain, pos, b, off, c)
        pos += c
        return c
    }

    override fun available() = len - pos

    private fun next(): Boolean {
        do {
            if (!fill(head, 4)) return false
            val c = StreamClient.u32(head, 0).toInt()
            if (c < SecureChannel.TAG || c > (64 shl 20)) throw IOException("encrypted record: bad length")
            if (rec.size < c) rec = ByteArray(c)
            if (!fill(rec, c)) throw IOException("encrypted record cut")
            val n = c - SecureChannel.TAG
            if (plain.size < n) plain = ByteArray(n)
            if (!gcm.open(counter++, rec, 0, n, plain, 0)) throw IOException("encrypted record refused (changed on the network)")
            pos = 0; len = n
        } while (len == 0)
        return true
    }

    private fun fill(b: ByteArray, n: Int): Boolean {
        var got = 0
        while (got < n) {
            val r = inner.read(b, got, n - got)
            if (r < 0) { if (got == 0) return false; throw IOException("connection cut inside a record") }
            got += r
        }
        return true
    }
}

/** Each write becomes one encrypted record (callers write whole lines). */
class SecureOut(private val inner: OutputStream, key: ByteArray) : OutputStream() {
    private val gcm = Gcm(key)
    private var counter = 0L
    private var buf = ByteArray(8 shl 10)

    override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)

    override fun write(b: ByteArray, off: Int, n: Int) = synchronized(this) {
        val c = n + SecureChannel.TAG
        if (buf.size < 4 + c) buf = ByteArray(4 + c)
        buf[0] = (c ushr 24).toByte(); buf[1] = (c ushr 16).toByte(); buf[2] = (c ushr 8).toByte(); buf[3] = c.toByte()
        gcm.seal(counter++, b, off, n, buf, 4)
        inner.write(buf, 0, 4 + c)
    }

    override fun flush() = inner.flush()
}

/** Encrypted datagrams from the PC: [0xFF][u64 counter][ciphertext + tag]; replays refused (64-packet window). */
class SecureDatagrams(key: ByteArray) {
    private val gcm = Gcm(key)
    private var top = 0L
    private var seen = 0L // bit i = counter top - i received

    /** Plain payload, or null if the datagram is not the PC's (or already received). */
    fun open(b: ByteArray, len: Int): ByteArray? {
        if (len < 9 + SecureChannel.TAG || b[0] != 0xFF.toByte()) return null
        var c = 0L
        for (i in 1..8) c = (c shl 8) or (b[i].toLong() and 0xFF)
        // The PC's sound and cursor threads share the counter: a packet may arrive just behind a newer one.
        if (c <= 0 || c <= top - 64 || (c <= top && (seen ushr (top - c).toInt()) and 1L != 0L)) return null
        val n = len - 9 - SecureChannel.TAG
        val out = ByteArray(n)
        if (!gcm.open(c, b, 9, n, out, 0)) return null
        if (c > top) { val shift = c - top; seen = if (shift >= 64) 1L else (seen shl shift.toInt()) or 1L; top = c }
        else seen = seen or (1L shl (top - c).toInt())
        return out
    }
}
