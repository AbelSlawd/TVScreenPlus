package com.tvscreenplus

import android.content.SharedPreferences
import android.util.Base64
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Pairing with PCs (same scheme as the PC's Pairing.cs): the first time, an ECDH P-256 exchange gives both sides
 * the same key and the same 6-digit code, shown here and on the PC where the person accepts. Then each connection
 * answers the PC's random challenge with HMAC-SHA256(key). One key per PC, kept in the app's private settings.
 */
class Pairing(private val prefs: SharedPreferences) {
    /** Random identifier of this TV, sent in the hello (the PC keeps one key per TV). */
    val tvId: String
        get() = prefs.getString(ID, null)?.takeIf { it.length == 32 }
            ?: hex(random(16)).also { prefs.edit().putString(ID, it).apply() }

    fun keyFor(pcId: ByteArray): ByteArray? =
        prefs.getString(KEY + hex(pcId), null)?.let { try { Base64.decode(it, Base64.NO_WRAP) } catch (_: Exception) { null } }

    fun trust(pcId: ByteArray, key: ByteArray) = prefs.edit().putString(KEY + hex(pcId), Base64.encodeToString(key, Base64.NO_WRAP)).apply()

    /** New identity, no PC key: every PC asks to accept this TV again. */
    fun forgetAll() = prefs.edit().apply { prefs.all.keys.filter { it.startsWith(KEY) || it == ID }.forEach { remove(it) } }.apply()

    /** One pairing attempt: our public key (X || Y, 64 bytes), and the shared key once the PC's is known. */
    class Exchange {
        private val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        private val pub = pair.public as ECPublicKey
        val publicKey: ByteArray = fixed32(pub.w.affineX) + fixed32(pub.w.affineY)

        /** SHA-256 of the ECDH secret (the PC computes the same). Throws if the PC's point is invalid. */
        fun key(pcPublic: ByteArray): ByteArray {
            require(pcPublic.size == 64) { "invalid public key" }
            val point = ECPoint(BigInteger(1, pcPublic.copyOfRange(0, 32)), BigInteger(1, pcPublic.copyOfRange(32, 64)))
            val other = KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(point, pub.params))
            val z = KeyAgreement.getInstance("ECDH").apply { init(pair.private); doPhase(other, true) }.generateSecret()
            return sha256(z)
        }
    }

    companion object {
        private const val ID = "tv_id"
        private const val KEY = "pckey_"

        fun proof(key: ByteArray, nonce: ByteArray): ByteArray =
            Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal("tvsp-auth".toByteArray() + nonce)

        /** The 6 digits shown on both screens: depend on the key and both public keys. */
        fun code(key: ByteArray, pcPublic: ByteArray, tvPublic: ByteArray): String {
            val h = sha256("tvsp-code".toByteArray() + key + pcPublic + tvPublic)
            val v = ((h[0].toLong() and 0xFF) shl 24) or ((h[1].toLong() and 0xFF) shl 16) or ((h[2].toLong() and 0xFF) shl 8) or (h[3].toLong() and 0xFF)
            return String.format(java.util.Locale.ROOT, "%06d", v % 1_000_000)
        }

        fun hex(b: ByteArray) = b.joinToString("") { String.format(java.util.Locale.ROOT, "%02x", it) }

        private fun random(n: Int) = ByteArray(n).also { SecureRandom().nextBytes(it) }
        private fun sha256(b: ByteArray) = MessageDigest.getInstance("SHA-256").digest(b)

        /** Big-endian, exactly 32 bytes (BigInteger adds a sign byte or drops leading zeros). */
        private fun fixed32(v: BigInteger): ByteArray {
            val b = v.toByteArray()
            return when {
                b.size == 32 -> b
                b.size > 32 -> b.copyOfRange(b.size - 32, b.size)
                else -> ByteArray(32 - b.size) + b
            }
        }
    }
}
