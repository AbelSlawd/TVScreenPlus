using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TVScreenPlus
{
    /// <summary>
    /// Which TVs may receive this PC's screen. A TV pairs once: an ECDH P-256 exchange gives both sides the same
    /// key and the same 6-digit code, shown on the TV and on the PC; the person accepts on the PC only if they match
    /// (a device in the middle would get a different code). Afterwards the TV proves it holds the key on every
    /// connection: HMAC-SHA256 of a fresh random challenge. Keys live in settings.txt (folder readable by admins only).
    /// The stream itself is not encrypted (TV CPU, latency): this keeps unknown devices out, not a LAN eavesdropper.
    /// </summary>
    static class Pairing
    {
        const string KeyPrefix = "tvkey_";

        /// <summary>Random identifier of this PC: the TV keeps one key per PC.</summary>
        public static byte[] PcId
        {
            get
            {
                lock (Lock)
                {
                    var hex = Settings.Get("pc_id", "");
                    if (hex.Length != 32) { hex = Hex(Random(16)); Settings.Set("pc_id", hex); }
                    return Unhex(hex);
                }
            }
        }
        static readonly object Lock = new object();

        /// <summary>Key of a paired TV, or null. TV ids are 32 hex digits (checked: they end up in settings keys).</summary>
        public static byte[] KeyOf(string tvId)
        {
            if (!ValidId(tvId)) return null;
            var v = Settings.Get(KeyPrefix + tvId, "");
            try { return v.Length > 0 ? Convert.FromBase64String(v.Split('|')[0]) : null; } catch { return null; }
        }

        public static bool ValidId(string id) => id != null && id.Length == 32 && id.All(Uri.IsHexDigit);

        public static void Trust(string tvId, string tvName, byte[] key) => Settings.Set(KeyPrefix + tvId, Convert.ToBase64String(key) + "|" + tvName);

        public static int Count => Settings.Keys(KeyPrefix).Count;

        public static void ForgetAll() { foreach (var k in Settings.Keys(KeyPrefix)) Settings.Set(k, null); }

        public static byte[] Random(int n) { var b = new byte[n]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }

        /// <summary>Expected answer to a challenge.</summary>
        public static byte[] Proof(byte[] key, byte[] nonce)
        {
            using (var h = new HMACSHA256(key)) return h.ComputeHash(Encoding.ASCII.GetBytes("tvsp-auth").Concat(nonce).ToArray());
        }

        /// <summary>Constant-time comparison (no timing hint on how many bytes matched).</summary>
        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        /// <summary>One pairing attempt: our public key (X || Y, 64 bytes) and, with the TV's, the shared key and code.</summary>
        public sealed class Exchange : IDisposable
        {
            readonly ECDiffieHellmanCng ecdh = new ECDiffieHellmanCng(ECCurve.NamedCurves.nistP256);
            public readonly byte[] PublicKey;

            public Exchange()
            {
                var q = ecdh.ExportParameters(false).Q;
                PublicKey = q.X.Concat(q.Y).ToArray();
            }

            /// <summary>Shared key = SHA-256 of the ECDH secret (the TV computes the same). Throws on an invalid point.</summary>
            public byte[] Key(byte[] tvPublic)
            {
                if (tvPublic == null || tvPublic.Length != 64) throw new CryptographicException("clé publique invalide");
                var p = new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = tvPublic.Take(32).ToArray(), Y = tvPublic.Skip(32).ToArray() },
                };
                using (var other = ECDiffieHellman.Create(p)) return ecdh.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
            }

            public void Dispose() => ecdh.Dispose();
        }

        /// <summary>6-digit code both screens show: depends on the key and both public keys.</summary>
        public static string Code(byte[] key, byte[] pcPublic, byte[] tvPublic)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.ASCII.GetBytes("tvsp-code").Concat(key).Concat(pcPublic).Concat(tvPublic).ToArray());
                uint v = (uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]);
                return (v % 1000000).ToString("000000");
            }
        }

        public static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

        public static byte[] Unhex(string s)
        {
            if (s == null || s.Length % 2 != 0 || !s.All(Uri.IsHexDigit)) return null;
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
