using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TVScreenPlus
{
    /// <summary>
    /// Encrypted, authenticated TV connection (AES-256-GCM). Keys come from the pairing key and this connection's
    /// challenge, which only the PC and the paired TV know: km = HMAC-SHA256(pairing key, "tvsp-sec" + challenge),
    /// then one key per direction, HMAC(km, "up" | "down" | "udp").
    /// TCP: each write becomes a record [u32 length][ciphertext + 16-byte tag]; the nonce is a counter per direction
    /// (12 bytes: 4 zeros + u64 big-endian), so a record replayed, dropped or changed ends the connection.
    /// UDP: [0xFF][u64 counter][ciphertext + tag]; a counter that does not increase is refused.
    /// Same format in the TV app (SecureChannel.kt).
    /// </summary>
    static class SecureChannel
    {
        public const byte UpBit = 1, DownBit = 2;

        public static byte[] Base(byte[] pairingKey, byte[] challenge)
        {
            using (var h = new HMACSHA256(pairingKey)) return h.ComputeHash(Encoding.ASCII.GetBytes("tvsp-sec").Concat(challenge).ToArray());
        }

        public static byte[] Key(byte[] baseKey, string use)
        {
            using (var h = new HMACSHA256(baseKey)) return h.ComputeHash(Encoding.ASCII.GetBytes(use));
        }

        /// <summary>
        /// Auto: encrypt the picture and the sound when the TV encrypts fast enough to add under ~1 ms per frame
        /// (its measured AES-GCM speed, MB/s); Wi-Fi, easier to listen to, accepts a slower TV.
        /// </summary>
        public static bool AutoDown(int tvMBps, bool wifi) => tvMBps >= (wifi ? 50 : 150);
    }

    /// <summary>AES-256-GCM through Windows CNG (.NET Framework 4.8 has no AesGcm class).</summary>
    sealed unsafe class Gcm : IDisposable
    {
        public const int TagSize = 16;
        IntPtr alg, key;
        readonly object gate = new object();

        public Gcm(byte[] secret)
        {
            try
            {
                Check(BCryptOpenAlgorithmProvider(out alg, "AES", null, 0), "open");
                var mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                Check(BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0), "mode");
                Check(BCryptGenerateSymmetricKey(alg, out key, IntPtr.Zero, 0, secret, secret.Length, 0), "key");
            }
            catch { Dispose(); throw; }
        }

        /// <summary>Encrypts src[so..so+len) into dst[d..d+len+16) (ciphertext then tag).</summary>
        public void Seal(ulong counter, byte[] src, int so, int len, byte[] dst, int d)
        {
            var nonce = Nonce(counter);
            lock (gate)
                fixed (byte* n = nonce) fixed (byte* s = src) fixed (byte* o = dst)
                {
                    var info = Info(n, o + d + len);
                    Check(BCryptEncrypt(key, len == 0 ? null : s + so, len, ref info, null, 0, len == 0 ? null : o + d, len, out int got, 0), "encrypt");
                }
        }

        /// <summary>Decrypts src[so..so+len+16) into dst[d..d+len); false if it was not sent by the other side as is.</summary>
        public bool Open(ulong counter, byte[] src, int so, int len, byte[] dst, int d)
        {
            var nonce = Nonce(counter);
            lock (gate)
                fixed (byte* n = nonce) fixed (byte* s = src) fixed (byte* o = dst)
                {
                    var info = Info(n, s + so + len);
                    return BCryptDecrypt(key, len == 0 ? null : s + so, len, ref info, null, 0, len == 0 ? null : o + d, len, out int got, 0) == 0;
                }
        }

        static byte[] Nonce(ulong c)
        {
            var n = new byte[12];
            for (int i = 0; i < 8; i++) n[4 + i] = (byte)(c >> (56 - 8 * i));
            return n;
        }

        static AuthInfo Info(byte* nonce, byte* tag) => new AuthInfo
        {
            cbSize = Marshal.SizeOf(typeof(AuthInfo)), dwInfoVersion = 1,
            pbNonce = (IntPtr)nonce, cbNonce = 12, pbTag = (IntPtr)tag, cbTag = TagSize
        };

        static void Check(int status, string what)
        {
            if (status != 0) throw new CryptographicException($"AES-GCM {what}: 0x{status:X8}");
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (key != IntPtr.Zero) BCryptDestroyKey(key);
                if (alg != IntPtr.Zero) BCryptCloseAlgorithmProvider(alg, 0);
                key = alg = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct AuthInfo
        {
            public int cbSize, dwInfoVersion;
            public IntPtr pbNonce; public int cbNonce;
            public IntPtr pbAuthData; public int cbAuthData;
            public IntPtr pbTag; public int cbTag;
            public IntPtr pbMacContext; public int cbMacContext;
            public int cbAAD; public long cbData; public int dwFlags;
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr alg, string id, string impl, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptSetProperty(IntPtr h, string prop, byte[] value, int size, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int objSize, byte[] secret, int size, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptEncrypt(IntPtr key, byte* input, int inSize, ref AuthInfo info, byte* iv, int ivSize, byte* output, int outSize, out int result, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDecrypt(IntPtr key, byte* input, int inSize, ref AuthInfo info, byte* iv, int ivSize, byte* output, int outSize, out int result, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr alg, int flags);
    }

    /// <summary>
    /// The TV connection after the switch to encryption: writes are sealed (if writeKey) and reads opened
    /// (if readKey); a null key leaves that direction in clear (picture and sound with encryption set to off).
    /// </summary>
    sealed class SecureStream : Stream
    {
        const int MaxRecord = 64 << 20;
        readonly Stream inner;
        readonly Gcm writer, reader;
        ulong sent, received;
        byte[] outBuf = new byte[64 << 10], inBuf = new byte[64 << 10], plain = new byte[64 << 10];
        int plainPos, plainLen;
        readonly byte[] head = new byte[4];

        public SecureStream(Stream inner, byte[] writeKey, byte[] readKey)
        {
            this.inner = inner;
            if (writeKey != null) writer = new Gcm(writeKey);
            if (readKey != null) reader = new Gcm(readKey);
        }

        public override void Write(byte[] b, int o, int l)
        {
            if (writer == null) { inner.Write(b, o, l); return; }
            lock (writer)
            {
                int n = 4 + l + Gcm.TagSize;
                if (outBuf.Length < n) outBuf = new byte[Math.Max(n, outBuf.Length * 2)];
                int c = l + Gcm.TagSize;
                outBuf[0] = (byte)(c >> 24); outBuf[1] = (byte)(c >> 16); outBuf[2] = (byte)(c >> 8); outBuf[3] = (byte)c;
                writer.Seal(sent++, b, o, l, outBuf, 4);
                inner.Write(outBuf, 0, n);
            }
        }

        public override int Read(byte[] b, int o, int l)
        {
            if (reader == null) return inner.Read(b, o, l);
            if (plainPos == plainLen && !NextRecord()) return 0;
            int n = Math.Min(l, plainLen - plainPos);
            Buffer.BlockCopy(plain, plainPos, b, o, n);
            plainPos += n;
            return n;
        }

        public override int ReadByte()
        {
            if (reader == null) return inner.ReadByte();
            if (plainPos == plainLen && !NextRecord()) return -1;
            return plain[plainPos++];
        }

        bool NextRecord()
        {
            do
            {
                if (!Fill(head, 4)) return false;
                int c = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
                if (c < Gcm.TagSize || c > MaxRecord) throw new IOException("encrypted record: bad length");
                if (inBuf.Length < c) inBuf = new byte[c];
                if (!Fill(inBuf, c)) throw new IOException("encrypted record cut");
                int len = c - Gcm.TagSize;
                if (plain.Length < len) plain = new byte[len];
                // Not sent by the paired TV, or changed / replayed / reordered on the way: end the connection.
                if (!reader.Open(received++, inBuf, 0, len, plain, 0)) throw new IOException("encrypted record refused (wrong key or changed on the network)");
                plainPos = 0; plainLen = len;
            } while (plainLen == 0);
            return true;
        }

        bool Fill(byte[] b, int n)
        {
            for (int got = 0; got < n;)
            {
                int r = inner.Read(b, got, n - got);
                if (r <= 0) { if (got == 0) return false; throw new IOException("connection cut inside a record"); }
                got += r;
            }
            return true;
        }

        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { writer?.Dispose(); reader?.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>UDP datagrams to the TV (sound, cursor), encrypted: [0xFF][u64 counter][ciphertext + tag].</summary>
    sealed class SecureDatagrams : IDisposable
    {
        readonly Gcm gcm;
        long counter;
        public SecureDatagrams(byte[] key) { gcm = new Gcm(key); }

        public byte[] Seal(byte[] p)
        {
            ulong c = (ulong)System.Threading.Interlocked.Increment(ref counter);
            var d = new byte[9 + p.Length + Gcm.TagSize];
            d[0] = 0xFF;
            for (int i = 0; i < 8; i++) d[1 + i] = (byte)(c >> (56 - 8 * i));
            gcm.Seal(c, p, 0, p.Length, d, 9);
            return d;
        }

        public void Dispose() => gcm.Dispose();
    }
}
