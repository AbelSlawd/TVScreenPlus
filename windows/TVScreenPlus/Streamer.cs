using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace TVScreenPlus
{
    /// <summary>An encoder pipeline: GPU capture (Desktop Duplication) -> encoder, zero-copy when possible.</summary>
    sealed class Encoder
    {
        public string Name, Codec, Filter;
        public bool Hardware;
        /// <summary>In-house pipeline (Desktop Duplication + Media Foundation encoder), no ffmpeg.</summary>
        public bool Native;
        readonly Func<bool, int, string> args; // (quality tier, kbps) -> encoder args

        Encoder(string name, string codec, string filter, bool hw, Func<bool, int, string> args)
        { Name = name; Codec = codec; Filter = filter; Hardware = hw; this.args = args; }

        /// <summary>Candidates from fastest (hardware, zero-copy GPU) to slowest (CPU).</summary>
        /// <param name="native">In-house encoder allowed (it does not draw the cursor: the TV must).</param>
        public static IEnumerable<Encoder> Candidates(bool hevc, bool native)
        {
            foreach (var c in hevc ? new[] { "hevc", "h264" } : new[] { "h264" })
            {
                // No pipelining: each image is encoded as soon as it is captured, at a steady frame rate.
                if (native) yield return new Encoder(c + "_mf", c, "", true, null) { Native = true };
                int cq = c == "hevc" ? 21 : 20;
                // Constant-quality VBR capped by maxrate: razor-sharp static desktop, bounded bitrate in motion.
                // -delay 0 (game mode) encodes each frame synchronously: lowest latency, but capture misses
                // ~5-10 % of the refreshes (measured 53-58 fps at 4K on an RTX 5080). -delay 2 pipelines two frames:
                // steady 60 fps for up to ~2 frames of extra latency.
                // Game mode uses p1 without spatial AQ: the shortest encode lets -delay 0 keep up (59.2 fps measured).
                Func<bool, int, string> nv = (q, k) => $"-c:v {c}_nvenc " + (Settings.GameMode ? "-preset p1 " : q ? "-preset p5 -spatial-aq 1 -aq-strength 6 " : "-preset p2 ") +
                    $"-tune ull -rc vbr -cq {cq} -b:v {k}k -maxrate {k}k -bufsize {k / 6}k -zerolatency 1 -delay {(Settings.GameMode ? 0 : 2)} -bf 0 -forced-idr 1";
                Func<bool, int, string> amf = (q, k) => $"-c:v {c}_amf -usage ultralowlatency " + (q ? "-quality balanced -vbaq true " : "-quality speed ") +
                    $"-rc vbr_peak -b:v {k * 3 / 4}k -maxrate {k}k -bufsize {k / 6}k -bf 0";
                Func<bool, int, string> qsv = (q, k) => $"-c:v {c}_qsv " + (q ? "-preset medium " : "-preset veryfast ") +
                    $"-b:v {k * 3 / 4}k -maxrate {k}k -bufsize {k / 6}k -async_depth 1 -low_delay_brc 1 -bf 0";
                yield return new Encoder(c + "_nvenc", c, "", true, nv);
                yield return new Encoder(c + "_amf", c, "", true, amf);
                yield return new Encoder(c + "_qsv", c, ",hwmap=derive_device=qsv,format=qsv,vpp_qsv=format=nv12", true, qsv);
                yield return new Encoder(c + "_nvenc_sys", c, ",hwdownload,format=bgra", true, nv);
                yield return new Encoder(c + "_amf_sys", c, ",hwdownload,format=bgra,format=nv12", true, amf);
                yield return new Encoder(c + "_qsv_sys", c, ",hwdownload,format=bgra,format=nv12", true, qsv);
            }
            yield return new Encoder("x264", "h264", ",hwdownload,format=bgra,scale=out_color_matrix=bt709,format=yuv420p", false,
                (q, k) => "-c:v libx264 " + (q ? "-preset superfast " : "-preset ultrafast ") +
                          $"-tune zerolatency -bf 0 -b:v {k}k -maxrate {k}k -bufsize {k / 6}k -colorspace bt709 -color_primaries bt709 -color_trc bt709");
        }

        /// <param name="drawMouse">false when the TV draws the cursor itself (overlay: much lower cursor latency).</param>
        /// <param name="stamp">Timestamps = wall-clock time right after capture (ms since 1970 mod 2^31 in the FLV
        /// output, with -copyts): lets the PC and the TV measure the real capture -> screen latency.</param>
        public string CommandLine(DisplayTarget t, int fps, int kbps, bool quality, string output, bool drawMouse = true, bool stamp = false) =>
            "-hide_banner -loglevel error -nostdin " +
            $"-init_hw_device d3d11va=d3d:{t.Adapter} -filter_hw_device d3d " +
            $"-f lavfi -i \"ddagrab=output_idx={t.Output}:framerate={fps}:draw_mouse={(drawMouse ? 1 : 0)}{(stamp ? ",setpts=RTCTIME/1000000/TB" : "")}{Filter}\" -an " +
            (stamp ? "-copyts " : "") + args(quality, kbps) + $" -g {fps * 120} " + output;
    }

    /// <summary>Encoder choice for one session: pipeline + tier + fps, validated by a live benchmark.</summary>
    sealed class EncodePlan
    {
        public Encoder Enc; public bool Quality; public int Fps;
        public override string ToString() => Enc.Native ? $"{Enc.Name} {Fps} i/s {(Settings.GameMode ? "mode jeu" : "fluide")}"
                                           : Settings.GameMode && Enc.Name.Contains("nvenc") ? $"{Enc.Name} p1 {Fps} i/s mode jeu"
                                                                                             : $"{Enc.Name} {(Quality ? "qualité" : "rapide")} {Fps} i/s {(Settings.GameMode ? "mode jeu" : "fluide")}";
    }

    /// <summary>Runs ffmpeg and turns its FLV output into Annex-B access units (one per frame).</summary>
    sealed class Streamer : IDisposable
    {
        Process proc;
        public DateTime Started;
        /// <summary>Run() has returned (ffmpeg ended or was stopped).</summary>
        public volatile bool Done;

        /// <summary>Find a working encoder for this display (result cached per GPU/codec).</summary>
        /// <param name="native">The in-house encoder may be used (chosen in the tray menu, and the TV draws the cursor).</param>
        public static Encoder Pick(DisplayTarget t, bool hevc, bool native)
        {
            var all = Encoder.Candidates(hevc, native).ToList();
            string key = "enc_" + t.Adapter + "_" + (hevc ? "hevc" : "h264") + (native ? "_mf" : "");
            var cached = all.FirstOrDefault(e => e.Name == Settings.Get(key, ""));
            if (cached != null) return cached;
            foreach (var e in all)
            {
                var sw = Stopwatch.StartNew();
                bool ok = RunTest(e, t, 60, false, 5) >= 0;
                Log.W($"probe {e.Name}: {(ok ? "OK" : "fail")} ({sw.ElapsedMilliseconds} ms)");
                if (ok) { Settings.Set(key, e.Name); return e; }
            }
            throw new Exception("Aucun encodeur vidéo utilisable");
        }

        public static void Forget(DisplayTarget t)
        {
            foreach (var c in new[] { "hevc", "h264", "hevc_mf", "h264_mf" }) Settings.Set("enc_" + t.Adapter + "_" + c, null);
        }

        /// <summary>
        /// Can this machine encode the current display size in real time? Measures real throughput
        /// (capture + encode) for ~3 s, quality tier first. Results are cached per encoder/size.
        /// </summary>
        public static EncodePlan Calibrate(Encoder e, DisplayTarget t, int[] fpsChoices)
        {
            foreach (int fps in fpsChoices)
                foreach (bool q in new[] { true, false })
                {
                    // "v2": measured after the encoder start-up (older results underestimated fast GPUs).
                    // v3 for the in-house encoder: its first results (before the 1 ms timer) were too low.
                    string key = $"bench_v{(e.Native ? 3 : 2)}_{e.Name}_{t.Width}x{t.Height}_{fps}_{(q ? "q" : "f")}{(Settings.GameMode ? "_game" : "")}";
                    string cached = Settings.Get(key, "");
                    double got = cached != "" ? double.Parse(cached, CultureInfo.InvariantCulture) : RunTest(e, t, fps, q, fps * 4);
                    if (cached == "" && got >= 0) Settings.Set(key, got.ToString(CultureInfo.InvariantCulture));
                    Log.W($"bench {key}: {got:0.0} fps");
                    if (got >= fps * 0.93) return new EncodePlan { Enc = e, Quality = q, Fps = fps };
                }
            return null;
        }

        /// <summary>
        /// Encodes 'frames' frames to null. Returns the steady-state fps, or -1 on failure.
        /// ffmpeg's own fps= figure includes process and encoder start-up (a 60 fps GPU reported ~56 over 3 s),
        /// so the rate is measured between progress reports, from the first encoded frame to the end.
        /// </summary>
        static double RunTest(Encoder e, DisplayTarget t, int fps, bool quality, int frames)
        {
            if (e.Native) return NativeTest(e, t, fps, frames);
            try
            {
                int kbps = (int)(t.Width * (double)t.Height * fps * 0.12 / 1000);
                var psi = new ProcessStartInfo(Setup.FFmpeg, e.CommandLine(t, fps, kbps, quality,
                    $"-frames:v {frames} -progress pipe:1 -stats_period 0.25 -nostats -f null -"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                using (new System.Threading.Timer(_ => { try { p.Kill(); } catch { } }, null, 25000, System.Threading.Timeout.Infinite))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var sw = Stopwatch.StartNew();
                    long f0 = -1, f1 = -1; double t0 = 0, t1 = 0, ffFps = -1;
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        if (line.StartsWith("fps=")) double.TryParse(line.Substring(4).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ffFps);
                        if (!line.StartsWith("frame=") || !long.TryParse(line.Substring(6).Trim(), out long f) || f <= 0) continue;
                        double now = sw.Elapsed.TotalSeconds;
                        if (f0 < 0) { f0 = f; t0 = now; } else { f1 = f; t1 = now; }
                    }
                    p.WaitForExit(5000);
                    if (!p.HasExited || p.ExitCode != 0) { Log.W("  " + err.Result.Trim().Replace("\n", " | ")); return -1; }
                    double steady = f1 > f0 && t1 - t0 >= 1.0 ? (f1 - f0) / (t1 - t0) : -1;
                    Log.W(FormattableString.Invariant($"  test {frames} frames: steady {steady:0.0} fps, ffmpeg overall {ffFps:0.0} fps"));
                    // The capture is paced at 'fps': anything above is report jitter.
                    return steady > 0 ? Math.Min(fps, steady) : ffFps > 0 ? ffFps : fps;
                }
            }
            catch (Exception ex) { Log.W("test error " + ex.Message); return -1; }
        }

        /// <summary>
        /// In-house encoder throughput: ~0.5 s warm-up then up to 2 s measured. A static desktop is re-encoded at
        /// the frame rate, so this measures real encodes even when nothing moves.
        /// </summary>
        static double NativeTest(Encoder e, DisplayTarget t, int fps, int frames)
        {
            try
            {
                int kbps = (int)(t.Width * (double)t.Height * fps * 0.12 / 1000);
                using (var s = new NativeSession(t.Adapter, t.Output, e.Codec == "hevc", fps, kbps, Settings.GameMode ? 0u : 50u))
                {
                    long freq = Stopwatch.Frequency, start = Stopwatch.GetTimestamp(), from = start + freq / 2;
                    long end = from + (long)(Math.Min(2.0, Math.Max(1.0, frames / (double)fps)) * freq);
                    int n = 0;
                    s.OnPacket = (b, len, key, acq, sub, fresh) => { if (sub >= from) Interlocked.Increment(ref n); };
                    s.Run(end);
                    Thread.Sleep(50);
                    double got = n / ((end - from) / (double)freq);
                    Log.W(FormattableString.Invariant($"  native test {e.Name} [{s.Enc.Name}, {s.Cap.AdapterName}]: {got:0.0} fps ({s.NewFrames} new, {s.Dropped} dropped)"));
                    return Math.Min(fps, got);
                }
            }
            catch (Exception ex) { Log.W($"native test {e.Name} error: {ex.Message}"); return -1; }
        }

        /// <summary>Changes the bitrate without restarting (in-house encoder only). false: start a new encoder.</summary>
        public bool SetBitrate(int kbps)
        {
            var s = native;
            if (s == null) return false;
            s.SetBitrate(kbps);
            return true;
        }

        /// <summary>Next frame becomes a keyframe (in-house encoder only).</summary>
        public bool ForceKeyFrame()
        {
            var s = native;
            if (s == null) return false;
            s.ForceKeyFrame();
            return true;
        }

        volatile NativeSession native;
        volatile bool disposed;

        void RunNative(EncodePlan plan, DisplayTarget t, int kbps, Action<byte[], int, int, uint> onFrame)
        {
            using (var s = new NativeSession(t.Adapter, t.Output, plan.Enc.Codec == "hevc", plan.Fps, kbps, Settings.GameMode ? 0u : 50u))
            {
                Log.W($"native encoder: {s.Enc.Name} on {s.Cap.AdapterName} {s.Cap.OutputName} {s.Cap.Width}x{s.Cap.Height} {kbps} kb/s | {s.Enc.Diag()} | GPU {s.Cap.GpuPriority}");
                bool first = true;
                int count = 0;
                s.OnPacket = (b, len, key, acq, sub, fresh) =>
                {
                    if (++count % 600 == 0) Log.W($"native: {s.NewFrames} new, {s.Repeats} repeated, {s.Dropped} dropped | {s.Stages()}");
                    // [u32 BE length][flags][Annex-B AU]: the encoder left 5 free bytes before the AU.
                    b[0] = (byte)(len >> 24); b[1] = (byte)(len >> 16); b[2] = (byte)(len >> 8); b[3] = (byte)len;
                    b[4] = (byte)((key ? 1 : 0) | (first ? 2 : 0));
                    first = false;
                    onFrame(b, 0, len + 5, WallClock.Ms(acq));
                };
                native = s;
                Started = DateTime.UtcNow;
                try { if (!disposed) s.Run(); }
                finally { native = null; }
            }
        }

        /// <summary>
        /// Streams until ffmpeg exits or onFrame throws. onFrame(buffer, offset, length) receives a ready-to-send
        /// packet: [u32 BE payload length][u8 flags][Annex-B access unit]. flags: 1 = keyframe, 2 = new stream.
        /// </summary>
        /// <param name="onFrame">(buffer, offset, length, capture time in ms since 1970 mod 2^31)</param>
        public void Run(EncodePlan plan, DisplayTarget t, int kbps, Action<byte[], int, int, uint> onFrame, bool drawMouse = true)
        {
            if (plan.Enc.Native) { RunNative(plan, t, kbps, onFrame); return; }
            // -fps_mode passthrough: ddagrab sometimes stamps two frames with the same time; FLV's default (vfr)
            // drops those, losing 3-5 fps (measured 55.5 -> 60.1 fps at 4K). The TV ignores timestamps anyway.
            string args = plan.Enc.CommandLine(t, plan.Fps, kbps, plan.Quality,
                "-fps_mode passthrough -bsf:v dump_extra=freq=keyframe -flush_packets 1 -f flv -flvflags no_duration_filesize pipe:1", drawMouse, true);
            Log.W("ffmpeg " + args);
            var psi = new ProcessStartInfo(Setup.FFmpeg, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            proc = Process.Start(psi);
            try { proc.PriorityClass = ProcessPriorityClass.High; } catch { }
            proc.ErrorDataReceived += (s, a) => { if (!string.IsNullOrWhiteSpace(a.Data)) Log.W("ffmpeg: " + a.Data); };
            proc.BeginErrorReadLine();
            Started = DateTime.UtcNow;
            Pump(proc.StandardOutput.BaseStream, onFrame);
        }

        static void Pump(Stream s, Action<byte[], int, int, uint> onFrame)
        {
            var hdr = new byte[15];
            if (!Read(s, hdr, 9)) return;                       // "FLV" header
            int skip = (hdr[5] << 24 | hdr[6] << 16 | hdr[7] << 8 | hdr[8]) - 9;
            if (skip > 0 && !Read(s, new byte[skip], skip)) return;
            var buf = new byte[4 << 20];
            bool first = true;
            while (Read(s, hdr, 15))                            // PreviousTagSize(4) + tag header(11)
            {
                int type = hdr[4], size = hdr[5] << 16 | hdr[6] << 8 | hdr[7];
                uint ts = (uint)(hdr[11] << 24 | hdr[8] << 16 | hdr[9] << 8 | hdr[10]) & 0x7FFFFFFF; // capture time (see CommandLine)
                if (size > buf.Length) buf = new byte[size * 2];
                if (!Read(s, buf, size)) return;
                if (type != 9 || size < 6) continue;            // video only

                int b0 = buf[0], off;
                bool key = ((b0 >> 4) & 7) == 1;
                if ((b0 & 0x80) != 0)
                {                                               // enhanced FLV (HEVC): [hdr][fourcc]([cts])
                    int pt = b0 & 0x0F;
                    if (pt != 1 && pt != 3) continue;           // 1 = CodedFrames, 3 = CodedFramesX
                    off = pt == 1 ? 8 : 5;
                }
                else
                {                                               // legacy FLV (AVC): [hdr][pkttype][cts x3]
                    if (buf[1] != 1) continue;
                    off = 5;
                }
                int end = size;
                if (end - off < 4) continue;

                // Length-prefixed NAL units -> Annex-B start codes, in place.
                bool annexB = buf[off] == 0 && buf[off + 1] == 0 && (buf[off + 2] == 1 || (buf[off + 2] == 0 && buf[off + 3] == 1));
                if (!annexB)
                {
                    for (int i = off; i + 4 <= end;)
                    {
                        int len = buf[i] << 24 | buf[i + 1] << 16 | buf[i + 2] << 8 | buf[i + 3];
                        buf[i] = 0; buf[i + 1] = 0; buf[i + 2] = 0; buf[i + 3] = 1;
                        if (len <= 0) break;
                        i += 4 + len;
                    }
                }
                // Our packet header goes in the 5 bytes just before the payload (off >= 5).
                int plen = end - off, h = off - 5;
                buf[h] = (byte)(plen >> 24); buf[h + 1] = (byte)(plen >> 16); buf[h + 2] = (byte)(plen >> 8); buf[h + 3] = (byte)plen;
                buf[h + 4] = (byte)((key ? 1 : 0) | (first ? 2 : 0));
                first = false;
                onFrame(buf, h, plen + 5, ts);
            }
        }

        static bool Read(Stream s, byte[] b, int n)
        {
            for (int o = 0; o < n;)
            {
                int r = s.Read(b, o, n - o);
                if (r <= 0) return false;
                o += r;
            }
            return true;
        }

        public void Dispose()
        {
            disposed = true;
            native?.Stop(); // the encoding thread releases everything when Run returns
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            try { proc?.Dispose(); } catch { }
        }
    }
}
