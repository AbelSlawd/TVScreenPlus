using System;
using System.Collections.Concurrent;
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
            foreach (var a in args)
            {
                if (a.StartsWith("prio=")) int.TryParse(a.Substring(5), out DesktopCapture.GpuPriorityLevel);
                if (a.StartsWith("class=")) int.TryParse(a.Substring(6), out DesktopCapture.GpuClass);
            }
            bool[] codecs = args.Contains("hevc") ? new[] { true } : args.Contains("h264") ? new[] { false } : new[] { true, false };
            Out("TVScreenPlus " + Program.Version + " banc d'essai encodeur maison");
            foreach (var o in DesktopCapture.ListOutputs()) Out("  sortie " + o);

            Form anim = null;
            var ready = new ManualResetEventSlim();
            var ui = new Thread(() =>
            {
                anim = new AnimForm();
                anim.Shown += (s, e) => ready.Set();
                Application.Run(anim);
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.IsBackground = true;
            ui.Start();
            ready.Wait(5000);
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
            const int fps = 60;
            using (var cap = new DesktopCapture(adapter, output))
            {
                anim?.BeginInvoke(new Action(() => anim.Location = new Point(cap.Left + 40, cap.Top + 40)));
                using (var enc = new MfEncoder(cap, hevc, fps, kbps, fps * 120))
                {
                    long freq = Stopwatch.Frequency, period = freq / fps;
                    long start = Stopwatch.GetTimestamp();
                    var pending = new ConcurrentDictionary<long, long[]>(); // sample time -> {acquired, submitted, new?}
                    var lat = new List<double>(); var encT = new List<double>(); var evT = new List<double>();
                    long bytes = 0; int outFrames = 0, keys = 0; string idrNals = null;
                    long measureFrom = start + freq; // skip the first second (encoder warm-up)
                    var done = new ManualResetEventSlim();
                    Exception err = null;
                    enc.OnError = e => { err = e; done.Set(); };
                    enc.OnOutput = (b, len, key, t) =>
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (!pending.TryRemove(t, out var info)) return;
                        if (key) { keys++; if (idrNals == null) idrNals = NalTypes(b, 5, len, hevc); }
                        if (info[1] < measureFrom) return;
                        outFrames++; bytes += len;
                        encT.Add((now - info[1]) * 1000.0 / freq);
                        evT.Add((enc.EventQpc - info[1]) * 1000.0 / freq);
                        if (info[2] == 1) lat.Add((now - info[0]) * 1000.0 / freq);
                    };

                    int slot = 0, newFrames = 0, repeats = 0, dropped = 0, newMeasured = 0, repMeasured = 0;
                    bool have = false, held = false;
                    long lastReal = 0, lastAny = 0, heldAcq = 0;
                    long end = start + seconds * freq;
                    while (Stopwatch.GetTimestamp() < end && !done.IsSet)
                    {
                        long now = Stopwatch.GetTimestamp();
                        long due = lastAny + period;
                        int wait = !have ? 100 : (int)Math.Max(0, (due - now) * 1000 / freq);
                        int writeSlot = held ? slot : (slot + 1) % cap.RingSize;
                        if (cap.Next(wait, writeSlot, out long acq, out _))
                        {
                            slot = writeSlot; held = true; heldAcq = acq; have = true;
                            newFrames++;
                        }
                        now = Stopwatch.GetTimestamp();
                        bool submitNew = held && now - lastReal >= period * 85 / 100;
                        bool repeat = !held && have && now >= lastAny + period;
                        if (held && !submitNew && now >= lastAny + period) submitNew = true; // held frame is due
                        if (!submitNew && !repeat) continue;
                        if (!enc.WaitInput(period * 1000 / freq > 0 ? (int)(period * 1000 / freq) : 16)) { dropped++; held = false; continue; }
                        now = Stopwatch.GetTimestamp();
                        long t100 = (now - start) * 10000000L / freq;
                        pending[t100] = new[] { submitNew ? heldAcq : now, now, submitNew ? 1L : 0L };
                        enc.Submit(cap.Ring[slot], t100);
                        if (now >= measureFrom) { if (submitNew) newMeasured++; else repMeasured++; }
                        if (submitNew) { held = false; lastReal = now; } else repeats++;
                        lastAny = now;
                    }
                    Thread.Sleep(100);
                    if (err != null) throw err;
                    double secs = (Stopwatch.GetTimestamp() - 100 * freq / 1000 - measureFrom) / (double)freq;
                    lat.Sort(); encT.Sort(); evT.Sort();
                    string H(List<double> l) => string.Join(" ", Enumerable.Range(0, 9).Select(i => l.Count(x => x >= i * 2 && (i == 8 || x < i * 2 + 2))));
                    string P(List<double> l, double q) => l.Count == 0 ? "-" : l[Math.Min(l.Count - 1, (int)(l.Count * q))].ToString("0.0", CultureInfo.InvariantCulture);
                    Out(string.Format(CultureInfo.InvariantCulture,
                        "{0} [{1}] q{18} {2}x{3} sur {4} : {5:0.0} i/s encodées (nouvelles {6:0.0}/s, répétées {7:0.0}/s, perdues {8}) | " +
                        "capture->paquet moy {9:0.0} ms p50 {10} p95 {11} max {12} | encodage moy {13:0.0} ms p95 {14} (événement p50 {19} p95 {20}) | {15:0.0} Mb/s | IDR {16}, NAL {17}\n  histo capture->paquet par 2 ms : {21} | GPU {22}",
                        hevc ? "HEVC" : "H.264", enc.Name, cap.Width, cap.Height, cap.OutputName,
                        outFrames / secs, newMeasured / secs, repMeasured / secs, dropped,
                        lat.Count > 0 ? lat.Average() : 0, P(lat, 0.5), P(lat, 0.95), P(lat, 1),
                        encT.Count > 0 ? encT.Average() : 0, P(encT, 0.95), bytes * 8 / secs / 1e6, keys, idrNals ?? "?", MfEncoder.QualityVsSpeed, P(evT, 0.5), P(evT, 0.95), H(lat), cap.GpuPriority));
                }
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
            public AnimForm()
            {
                FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
                TopMost = true; ShowInTaskbar = false; DoubleBuffered = true;
                Size = new Size(960, 540); Location = new Point(40, 40);
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
