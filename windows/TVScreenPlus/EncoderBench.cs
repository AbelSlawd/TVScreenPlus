using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TVScreenPlus
{
    /// <summary>
    /// TVScreenPlus.exe --bench-encoder [seconds] [adapter/output] [kbps]: measures the in-house pipeline
    /// (Desktop Duplication -> video processor NV12 -> Media Foundation hardware encoder) without a TV.
    /// An animated window on the captured output makes the desktop change at every refresh.
    /// </summary>
    static unsafe class EncoderBench
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern nint GetStdHandle(int n);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();

        static int BenchFps = 60;

        static void Out(string s)
        {
            Console.WriteLine(s);
            Log.W("bench: " + s);
        }

        public static void Run(string[] args)
        {
            if (GetStdHandle(-11) == 0) AttachConsole(-1);
            int seconds = args.Length > 1 && int.TryParse(args[1], out int sec) ? Math.Max(2, sec) : 10;
            int adapter = 0, output = 0;
            if (args.Length > 2 && args[2].Contains("/"))
            {
                var p = args[2].Split('/');
                int.TryParse(p[0], out adapter); int.TryParse(p[1], out output);
            }
            int kbps = args.Length > 3 && int.TryParse(args[3], out int k) ? k : 40000;
            if (args.Length > 4 && uint.TryParse(args[4], out uint q)) MfEncoder.QualityVsSpeed = q;
            if (args.Contains("noflush")) DesktopCapture.FlushAfterConvert = false;
            if (args.Contains("coarse")) NativeSession.FineTimer = false;
            if (args.Contains("bufbits")) MfEncoder.BufferDivisor = 1;
            if (args.Contains("nodrain")) MfEncoder.AllowDrain = false;
            foreach (var a in args) if (a.StartsWith("fps=")) int.TryParse(a.Substring(4), out BenchFps);
            foreach (var a in args)
            {
                if (a.StartsWith("prio=")) int.TryParse(a.Substring(5), out DesktopCapture.GpuPriorityLevel);
                if (a.StartsWith("class=")) int.TryParse(a.Substring(6), out DesktopCapture.GpuClass);
                if (a.StartsWith("rc=")) uint.TryParse(a.Substring(3), out MfEncoder.RateControl);
            }
            bool[] codecs = args.Contains("hevc") ? new[] { true } : args.Contains("h264") ? new[] { false } : new[] { true, false };
            Out("TVScreenPlus " + Program.Version + " banc d'essai encodeur maison");
            foreach (var o in DesktopCapture.ListOutputs()) Out("  sortie " + o);

            Form anim = null;
            bool animate = !args.Contains("static");
            var ready = new ManualResetEventSlim();
            var ui = new Thread(() =>
            {
                anim = new AnimForm(args.Contains("heavy") || args.Contains("bursty"), args.Contains("bursty"));
                anim.Shown += (s, e) => ready.Set();
                Application.Run(anim);
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.IsBackground = true;
            if (animate) { ui.Start(); ready.Wait(5000); }
            try
            {
                foreach (bool hevc in codecs)
                {
                    try { Bench(adapter, output, hevc, seconds, kbps, anim); }
                    catch (Exception e) { Out((hevc ? "HEVC" : "H.264") + " : ÉCHEC " + e.Message); Log.W(e.ToString()); }
                }
            }
            finally { try { anim?.BeginInvoke(new Action(anim.Close)); } catch { } }
        }

        static void Bench(int adapter, int output, bool hevc, int seconds, int kbps, Form anim)
        {
            int fps = BenchFps;
            using (var s = new NativeSession(adapter, output, hevc, fps, kbps))
            {
                var cap = s.Cap; var enc = s.Enc;
                anim?.BeginInvoke(new Action(() => anim.Location = new Point(cap.Left + 40, cap.Top + 40)));
                long freq = Stopwatch.Frequency, start = Stopwatch.GetTimestamp();
                long measureFrom = start + freq, half = start + (seconds + 1) * freq / 2, end = start + seconds * freq;
                var lat = new List<double>(); var encT = new List<double>(); var evT = new List<double>();
                long[] bytes = new long[2]; int maxFrame = 0; var perSec = new List<long>(); int outFrames = 0, keys = 0, fresh = 0, repeats = 0; string idrNals = null;
                s.OnPacket = (b, len, key, acq, sub, isNew) =>
                {
                    long now = Stopwatch.GetTimestamp();
                    if (key) { keys++; if (idrNals == null) idrNals = NalTypes(b, 5, len, hevc); }
                    if (sub < measureFrom) return;
                    outFrames++; bytes[sub < half ? 0 : 1] += len;
                    if (sub < half) { maxFrame = Math.Max(maxFrame, len); int sec = (int)((sub - measureFrom) / freq); while (perSec.Count <= sec) perSec.Add(0); perSec[sec] += len; }
                    if (isNew) fresh++; else repeats++;
                    encT.Add((now - sub) * 1000.0 / freq);
                    evT.Add((enc.EventQpc - sub) * 1000.0 / freq);
                    if (isNew) lat.Add((now - acq) * 1000.0 / freq);
                };
                // Live controls, checked half-way: bitrate halved and one forced IDR, without restarting.
                Out("  ICodecAPI : " + enc.Diag());
                s.Run(half);
                s.SetBitrate(kbps / 2); s.ForceKeyFrame();
                s.Run(end);
                Out("  ICodecAPI après : " + enc.Diag());
                Out(FormattableString.Invariant($"  1re moitié : pointe sur 1 s {(perSec.Count > 0 ? perSec.Max() * 8 / 1e6 : 0):0.0} Mb/s, plus grosse image {maxFrame / 1024} Ko (débit/60 = {kbps / 8 / 60} Ko)"));
                Thread.Sleep(100);
                double secs = (end - measureFrom) / (double)freq, h1 = (half - measureFrom) / (double)freq, h2 = (end - half) / (double)freq;
                lat.Sort(); encT.Sort(); evT.Sort();
                string H(List<double> l) => string.Join(" ", Enumerable.Range(0, 9).Select(i => l.Count(x => x >= i * 2 && (i == 8 || x < i * 2 + 2))));
                string P(List<double> l, double q) => l.Count == 0 ? "-" : l[Math.Min(l.Count - 1, (int)(l.Count * q))].ToString("0.0", CultureInfo.InvariantCulture);
                Out(string.Format(CultureInfo.InvariantCulture,
                    "{0} [{1}] q{2} {3}x{4} sur {5} : {6:0.0} i/s encodées (nouvelles {7:0.0}/s, répétées {8:0.0}/s, perdues {9})\n" +
                    "  capture->paquet moy {10:0.0} ms p50 {11} p95 {12} max {13} | encodage moy {14:0.0} ms p95 {15} (événement p50 {16} p95 {17})\n" +
                    "  histo capture->paquet par 2 ms : {18} | GPU {19}\n" +
                    "  débit {20:0.0} Mb/s puis {21:0.0} Mb/s (consigne {22} puis {23} Mb/s) | IDR {24} (attendu 2), NAL {25}",
                    hevc ? "HEVC" : "H.264", enc.Name, MfEncoder.QualityVsSpeed, cap.Width, cap.Height, cap.OutputName,
                    outFrames / secs, fresh / secs, repeats / secs, s.Dropped,
                    lat.Count > 0 ? lat.Average() : 0, P(lat, 0.5), P(lat, 0.95), P(lat, 1),
                    encT.Count > 0 ? encT.Average() : 0, P(encT, 0.95), P(evT, 0.5), P(evT, 0.95),
                    H(lat), cap.GpuPriority, bytes[0] * 8 / h1 / 1e6, bytes[1] * 8 / h2 / 1e6, kbps / 1000, kbps / 2000, keys, idrNals ?? "?"));
            }
        }

        /// <summary>NAL unit types of an Annex-B access unit, e.g. "32,33,34,19" (HEVC VPS,SPS,PPS,IDR).</summary>
        static string NalTypes(byte[] b, int off, int len, bool hevc)
        {
            var t = new List<int>();
            for (int i = off; i + 3 < off + len && t.Count < 8; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1)
                {
                    t.Add(hevc ? (b[i + 3] >> 1) & 0x3F : b[i + 3] & 0x1F);
                    i += 3;
                }
            return string.Join(",", t);
        }

        /// <summary>Changes on every desktop composition, like a game or a video.</summary>
        sealed class AnimForm : Form
        {
            readonly Stopwatch sw = Stopwatch.StartNew();
            int frame;
            readonly Bitmap[] noise;
            readonly bool bursty;
            public AnimForm(bool heavy, bool bursty)
            {
                this.bursty = bursty;
                if (heavy)
                {
                    // Unpredictable full-HD images: the encoder needs every bit it is allowed.
                    var rnd = new Random(1);
                    noise = new Bitmap[8];
                    for (int n = 0; n < noise.Length; n++)
                    {
                        var bmp = new Bitmap(1920, 1080, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                        var d = bmp.LockBits(new Rectangle(0, 0, 1920, 1080), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                        var px = new byte[d.Stride * 1080];
                        rnd.NextBytes(px);
                        Marshal.Copy(px, 0, d.Scan0, px.Length);
                        bmp.UnlockBits(d);
                        noise[n] = bmp;
                    }
                }
                FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
                TopMost = true; ShowInTaskbar = false; DoubleBuffered = true;
                Size = heavy ? new Size(1920, 1080) : new Size(960, 540); Location = new Point(40, 40);
                var t = new Thread(() =>
                {
                    while (!IsDisposed)
                    {
                        DwmFlush();
                        try { BeginInvoke(new Action(Invalidate)); } catch { return; }
                    }
                }) { IsBackground = true };
                Shown += (s, e) => t.Start();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                // bursty: still image, then 0.5 s of noise every 2 s (desktop -> video -> desktop).
                if (noise != null) { bool on = !bursty || sw.ElapsedMilliseconds % 2000 < 500; g.DrawImageUnscaled(noise[on ? frame++ % noise.Length : 0], 0, 0); return; }
                double s = sw.Elapsed.TotalSeconds;
                int w = ClientSize.Width, h = ClientSize.Height, off = (int)(s * 480) % 120;
                g.Clear(Color.FromArgb(20, 20, 30));
                for (int x = -120 + off; x < w; x += 120)
                    using (var br = new SolidBrush(Color.FromArgb((x * 7 + frame) & 255, 90, 200)))
                        g.FillRectangle(br, x, 0, 60, h);
                g.DrawString($"TVScreenPlus bench {++frame}", Font, Brushes.White, 10, 10);
            }
        }
    }
}
