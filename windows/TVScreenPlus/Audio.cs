using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TVScreenPlus
{
    /// <summary>
    /// PC sound on the TV: WASAPI loopback ("what you hear") on the default playback device, converted to
    /// 16-bit stereo PCM and sent as soon as it is captured, in packets of about 10 ms.
    /// Raw PCM rather than Opus: ~1.5 Mb/s is a few % of the video budget, costs no CPU, adds no codec delay
    /// and needs no decoder on the TV (AudioTrack plays it directly).
    /// Packet payload (flag 0x08): [u8 kind=1][u8 format=1 (s16le)][u8 channels][u8 0][u32 BE rate][u32 BE pts ms][PCM].
    /// </summary>
    sealed class AudioLoopback : IDisposable
    {
        public const int Kbps = 1600; // 48 kHz s16 stereo (1536 kb/s) + headers: reserved in the bitrate budget
        public const byte Kind = 1;
        readonly Action<byte[]> onPacket;
        readonly Thread thread;
        volatile bool running = true;
        readonly SessionStats stats;
        long bytes; readonly Stopwatch window = Stopwatch.StartNew();

        readonly int chunkMs;

        /// <param name="chunkMs">Largest packet (ms of sound): 20 in the TCP stream, 5 over UDP (one Ethernet frame).</param>
        public AudioLoopback(Action<byte[]> onPacket, SessionStats stats, int chunkMs = 20)
        {
            this.onPacket = onPacket; this.stats = stats; this.chunkMs = chunkMs;
            thread = new Thread(Loop) { IsBackground = true, Name = "audio", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        public void Dispose()
        {
            running = false;
            try { thread.Join(1500); } catch { }
        }

        /// <summary>A previous instance muted the PC and was killed: give the sound back.</summary>
        public static void RestoreMuteAfterCrash()
        {
            if (Settings.Get("pc_muted", "0") != "1") return;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDefaultAudioEndpoint(0, 1, out IMMDevice dev) == 0)
                {
                    var vol = Volume(dev);
                    if (vol != null) { var g = Guid.Empty; vol.SetMute(false, ref g); Log.W("PC sound unmuted (left muted by a previous run)"); }
                }
            }
            catch (Exception e) { Log.W("unmute: " + e.Message); }
            Settings.Set("pc_muted", null);
        }

        void Loop()
        {
            timeBeginPeriod(1); // 5 ms polling really is 5 ms
            IAudioEndpointVolume muted = null;
            string lastError = null;
            try
            {
                while (running)
                {
                    try { Capture(ref muted); lastError = null; continue; } // default device changed: reopen at once
                    catch (Exception e)
                    {
                        // No playback device, device unplugged, exclusive mode…: retry quietly.
                        if (e.Message != lastError) Log.W("audio: " + e.Message);
                        lastError = e.Message;
                        stats.Audio = "aucun périphérique de sortie utilisable";
                    }
                    for (int i = 0; i < 20 && running; i++) Thread.Sleep(50);
                }
            }
            finally
            {
                if (muted != null) { SetMute(muted, false); Settings.Set("pc_muted", null); Log.W("PC sound restored"); }
                timeEndPeriod(1);
            }
        }

        /// <summary>Captures the current default device until it changes, fails or capture stops.</summary>
        void Capture(ref IAudioEndpointVolume muted)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            Check(en.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out IMMDevice dev), "GetDefaultAudioEndpoint");
            dev.GetId(out string id);
            var iid = IID_IAudioClient;
            Check(dev.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out object o), "Activate");
            var ac = (IAudioClient)o;
            Check(ac.GetMixFormat(out IntPtr pf), "GetMixFormat");
            Format f;
            try
            {
                f = Format.Parse(pf);
                Check(ac.Initialize(0 /*shared*/, 0x00020000 /*AUDCLNT_STREAMFLAGS_LOOPBACK*/, 2000000 /*200 ms*/, 0, pf, IntPtr.Zero), "Initialize");
            }
            finally { Marshal.FreeCoTaskMem(pf); }
            var iidc = IID_IAudioCaptureClient;
            Check(ac.GetService(ref iidc, out object co), "GetService");
            var cc = (IAudioCaptureClient)co;
            int decim = f.Rate > 48000 && f.Rate % 2 == 0 ? (f.Rate % 48000 == 0 ? f.Rate / 48000 : f.Rate % 44100 == 0 ? f.Rate / 44100 : 1) : 1;
            int outRate = f.Rate / decim;
            var conv = new Converter(f, decim);
            Log.W($"Audio loopback: {f} -> {outRate} Hz s16 stereo");
            stats.Audio = $"actif · {outRate / 1000.0:0.#} kHz stéréo";
            Check(ac.Start(), "Start");
            var tick = Stopwatch.StartNew();
            var raw = new byte[0];
            var pcm = new short[outRate / 5 * 2];   // up to 200 ms of output
            try
            {
                while (running)
                {
                    Thread.Sleep(5);
                    if (tick.ElapsedMilliseconds >= 1000)
                    {
                        tick.Restart();
                        // Follow the user's device: headphones plugged in, output changed in Windows…
                        if (en.GetDefaultAudioEndpoint(0, 1, out IMMDevice d2) == 0)
                        {
                            d2.GetId(out string id2);
                            Marshal.ReleaseComObject(d2);
                            if (id2 != id) { Log.W("Audio: default device changed"); return; }
                        }
                        // "Couper le son du PC" can be toggled while streaming.
                        if (Settings.MutePc && muted == null)
                        {
                            var v = Volume(dev);
                            if (v != null && v.GetMute(out bool m) == 0 && !m) { SetMute(v, true); muted = v; Settings.Set("pc_muted", "1"); Log.W("PC sound muted"); }
                        }
                        else if (!Settings.MutePc && muted != null) { SetMute(muted, false); muted = null; Settings.Set("pc_muted", null); Log.W("PC sound restored"); }
                        double secs = window.Elapsed.TotalSeconds;
                        stats.AudioKbps = Interlocked.Exchange(ref bytes, 0) * 8 / secs / 1000;
                        window.Restart();
                    }

                    int n = 0; // output frames in pcm
                    while (true)
                    {
                        int hr = cc.GetNextPacketSize(out uint next);
                        if (hr < 0) throw new Exception("GetNextPacketSize 0x" + hr.ToString("X")); // device invalidated
                        if (next == 0) break;
                        Check(cc.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _), "GetBuffer");
                        int len = (int)frames * f.BlockAlign;
                        if (raw.Length < len) raw = new byte[len * 2];
                        if ((flags & 2 /*SILENT*/) != 0) Array.Clear(raw, 0, len); else Marshal.Copy(data, raw, 0, len);
                        cc.ReleaseBuffer(frames);
                        int need = (n + (int)frames / decim + 1) * 2;
                        if (need > pcm.Length) Array.Resize(ref pcm, need * 2);
                        n += conv.Convert(raw, (int)frames, pcm, n);
                    }
                    if (n == 0) continue;
                    // Fell behind (network stalled the sender)? Old sound is useless: keep the last 40 ms.
                    int start = 0, max = outRate / 10;
                    if (n > max) { start = n - outRate / 25; stats.AudioSkipped++; }
                    Send(pcm, start, n - start, outRate);
                }
            }
            finally
            {
                try { ac.Stop(); } catch { }
                Marshal.ReleaseComObject(cc); Marshal.ReleaseComObject(ac);
                Marshal.ReleaseComObject(dev); Marshal.ReleaseComObject(en); // 'muted' holds its own reference
            }
        }

        void Send(short[] pcm, int startFrame, int frames, int rate)
        {
            // Small packets: one never waits long behind a video frame (TCP) and each fits in one frame (UDP).
            int chunk = rate * chunkMs / 1000;
            for (int o = 0; o < frames; o += chunk)
            {
                int c = Math.Min(chunk, frames - o);
                var p = new byte[12 + c * 4];
                p[0] = Kind; p[1] = 1; p[2] = 2;
                p[4] = (byte)(rate >> 24); p[5] = (byte)(rate >> 16); p[6] = (byte)(rate >> 8); p[7] = (byte)rate;
                // Capture time on the same clock as the video frames and pings (ms since 1970 mod 2^31):
                // the TV measures the real capture -> speaker delay with it.
                uint pts = (uint)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (frames - o) * 1000L / rate) & 0x7FFFFFFF);
                p[8] = (byte)(pts >> 24); p[9] = (byte)(pts >> 16); p[10] = (byte)(pts >> 8); p[11] = (byte)pts;
                Buffer.BlockCopy(pcm, (startFrame + o) * 4, p, 12, c * 4);
                Interlocked.Add(ref bytes, p.Length + 5);
                onPacket(p);
            }
        }

        static IAudioEndpointVolume Volume(IMMDevice dev)
        {
            var iid = IID_IAudioEndpointVolume;
            return dev.Activate(ref iid, 0x17, IntPtr.Zero, out object o) == 0 ? o as IAudioEndpointVolume : null;
        }

        static void SetMute(IAudioEndpointVolume v, bool on)
        {
            try { var g = Guid.Empty; v.SetMute(on, ref g); } catch (Exception e) { Log.W("mute: " + e.Message); }
        }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new Exception(what + " 0x" + hr.ToString("X8"));
        }

        /// <summary>Shared-mode mix format of the device.</summary>
        sealed class Format
        {
            public int Rate, Channels, BlockAlign, Bits; public bool Float; public uint Mask;
            public static Format Parse(IntPtr p)
            {
                var f = new Format
                {
                    Channels = Marshal.ReadInt16(p, 2), Rate = Marshal.ReadInt32(p, 4),
                    BlockAlign = Marshal.ReadInt16(p, 12), Bits = Marshal.ReadInt16(p, 14)
                };
                int tag = Marshal.ReadInt16(p, 0) & 0xFFFF;
                if (tag == 0xFFFE && Marshal.ReadInt16(p, 16) >= 22)
                {
                    f.Mask = (uint)Marshal.ReadInt32(p, 20);
                    var sub = new byte[16]; Marshal.Copy(p + 24, sub, 0, 16);
                    f.Float = new Guid(sub) == new Guid("00000003-0000-0010-8000-00aa00389b71");
                }
                else f.Float = tag == 3;
                if (f.Float && f.Bits != 32) throw new Exception("format audio non pris en charge (" + f.Bits + " bits flottant)");
                if (!f.Float && f.Bits != 16 && f.Bits != 24 && f.Bits != 32) throw new Exception("format audio non pris en charge (" + f.Bits + " bits)");
                return f;
            }
            public override string ToString() => $"{Rate} Hz {Channels} ch {Bits} bits {(Float ? "float" : "int")} mask 0x{Mask:X}";
        }

        /// <summary>Any mix format -> 16-bit stereo: surround downmix, integer-ratio decimation (96/192 kHz).</summary>
        sealed class Converter
        {
            readonly Format f;
            readonly float[] wl, wr;
            readonly int decim;
            float accL, accR; int accN;

            public Converter(Format f, int decim)
            {
                this.f = f; this.decim = decim;
                wl = new float[f.Channels]; wr = new float[f.Channels];
                if (f.Channels == 1) { wl[0] = wr[0] = 1; return; }
                uint mask = f.Mask != 0 ? f.Mask : f.Channels == 2 ? 0x3u : f.Channels == 4 ? 0x33u : f.Channels == 6 ? 0x3Fu : f.Channels == 8 ? 0x63Fu : 0;
                if (mask == 0) { wl[0] = 1; wr[1] = 1; return; }
                int c = 0;
                for (int bit = 0; bit < 18 && c < f.Channels; bit++)
                {
                    if ((mask & (1u << bit)) == 0) continue;
                    switch (1u << bit)
                    {
                        case 0x1: case 0x40: wl[c] = 1; break;                        // front left (of center)
                        case 0x2: case 0x80: wr[c] = 1; break;                        // front right (of center)
                        case 0x4: wl[c] = wr[c] = 0.707f; break;                      // center
                        case 0x10: case 0x200: wl[c] = 0.707f; break;                 // back / side left
                        case 0x20: case 0x400: wr[c] = 0.707f; break;                 // back / side right
                        case 0x100: wl[c] = wr[c] = 0.5f; break;                      // back center
                    }                                                                 // LFE and top speakers dropped
                    c++;
                }
            }

            float Sample(byte[] b, int o)
            {
                if (f.Float) return BitConverter.ToSingle(b, o);
                switch (f.Bits)
                {
                    case 16: return BitConverter.ToInt16(b, o) / 32768f;
                    case 24: return ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f;
                    default: return BitConverter.ToInt32(b, o) / 2147483648f;
                }
            }

            /// <summary>Returns the number of stereo frames written at outFrame.</summary>
            public int Convert(byte[] src, int frames, short[] dst, int outFrame)
            {
                int bps = f.BlockAlign / f.Channels, written = 0;
                for (int i = 0; i < frames; i++)
                {
                    float l = 0, r = 0;
                    int o = i * f.BlockAlign;
                    for (int c = 0; c < f.Channels; c++)
                    {
                        if (wl[c] == 0 && wr[c] == 0) continue;
                        float s = Sample(src, o + c * bps);
                        l += s * wl[c]; r += s * wr[c];
                    }
                    accL += l; accR += r;
                    if (++accN < decim) continue;
                    int k = (outFrame + written) * 2;
                    dst[k] = Clip(accL / decim); dst[k + 1] = Clip(accR / decim);
                    accL = accR = 0; accN = 0;
                    written++;
                }
                return written;
            }

            static short Clip(float v) => (short)(v >= 1f ? 32767 : v <= -1f ? -32768 : v * 32767f);
        }

        // ---------- Core Audio COM interop ----------
        static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr h);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);
    }
}
