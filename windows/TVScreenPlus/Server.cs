using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TVScreenPlus
{
    /// <summary>
    /// Discovery (UDP 47801): TV broadcasts "TVSP?" -> PC answers "TVSP!\nname=..\nbusy=0|1\nver=..".
    /// Stream (TCP 47800): TV sends "TVSP1;v=2;name=..;w=..;h=..;hz=..;hevc=maxH;avc=maxH\n".
    ///  v1 TV: PC answers "OK;codec=..;w=..;h=..\n" then video packets.
    ///  v2 TV: only packets [u32 len][u8 flags][payload]; flags 0x80 filler, 0x40 ping (TV answers "ACK\n"),
    ///         0x20 config text, 0x10 status text, else video (1 = keyframe, 2 = new stream).
    ///         TV also sends "STAT;fps=..;kbps=..;drop=..\n" lines.
    /// </summary>
    sealed class Server
    {
        public const int TcpPort = 47800, UdpPort = 47801;
        readonly Action<string> status;
        readonly Action<string, string> notify;
        UdpClient udp;
        TcpListener tcp;
        volatile Session current;
        readonly object sessionLock = new object();
        public bool Busy => current != null;

        public Server(Action<string> status, Action<string, string> notify) { this.status = status; this.notify = notify; }

        public void Start()
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpPort)) { EnableBroadcast = true };
                    NoInherit(udp.Client);
                    tcp = new TcpListener(IPAddress.Any, TcpPort);
                    tcp.Start();
                    NoInherit(tcp.Server);
                    break;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    // Port still held: a previous instance (or a process that inherited its socket) is exiting.
                    try { udp?.Close(); } catch { }
                    try { tcp?.Stop(); } catch { }
                    Log.W($"Port busy (try {i + 1}): {e.Message}");
                    if (i == 2) Installer.KillStrayFfmpeg();
                    if (i >= 20) throw new Exception($"Les ports {TcpPort}/{UdpPort} sont utilisés par une autre application. Redémarrez le PC.");
                    Thread.Sleep(1000);
                }
            }
            new Thread(UdpLoop) { IsBackground = true, Name = "discovery" }.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "accept" }.Start();
            Log.W("Server listening");
        }

        /// <summary>
        /// .NET Framework sockets are inheritable: without this, ffmpeg and the next version started by the
        /// updater would keep a copy of our listening ports open and block them ("only one usage of each socket address").
        /// </summary>
        static void NoInherit(Socket s)
        {
            try { SetHandleInformation(s.Handle, 1 /*HANDLE_FLAG_INHERIT*/, 0); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetHandleInformation(IntPtr h, int mask, int flags);

        /// <summary>
        /// Drops the current TV session (the TV reconnects by itself) and re-opens the network
        /// listeners (useful after a network change or a stuck stream).
        /// </summary>
        public void Restart()
        {
            Log.W("Connection restart requested");
            current?.Close();
            try { udp?.Close(); } catch { }
            try { tcp?.Stop(); } catch { }
            Thread.Sleep(300);
            Start();
        }

        public void Stop()
        {
            try { udp?.Close(); } catch { }
            try { tcp?.Stop(); } catch { }
            current?.Close();
            VirtualDisplay.Detach();
        }

        void UdpLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                try
                {
                    var d = udp.Receive(ref ep);
                    if (d.Length >= 5 && Encoding.ASCII.GetString(d, 0, 5) == "TVSP?")
                    {
                        var r = Encoding.UTF8.GetBytes("TVSP!\nname=" + Environment.MachineName + "\nbusy=" + (current != null ? 1 : 0) + "\nver=" + Program.Version);
                        udp.Send(r, r.Length, ep);
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception e) { Log.W("udp: " + e.Message); Thread.Sleep(200); }
            }
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = tcp.AcceptTcpClient(); NoInherit(c.Client); }
                catch (Exception) { return; }
                new Thread(() =>
                {
                    // A new TV takes over: stop the previous session, then run this one.
                    current?.Close();
                    lock (sessionLock)
                    {
                        var s = new Session(c, status, notify);
                        current = s;
                        try { s.Run(); }
                        finally
                        {
                            current = null;
                            status("Prêt – en attente d'une TV (" + Environment.MachineName + ")");
                        }
                    }
                }) { IsBackground = true, Name = "session" }.Start();
            }
        }
    }

    sealed class Session
    {
        readonly TcpClient client;
        readonly Action<string> status;
        readonly Action<string, string> notify;
        readonly AutoResetEvent ack = new AutoResetEvent(false);
        readonly object sendLock = new object();
        NetworkStream ns;
        volatile bool closed;
        volatile Streamer streamer;
        bool v2;

        public Session(TcpClient c, Action<string> status, Action<string, string> notify)
        {
            client = c; this.status = status; this.notify = notify;
        }

        public void Close()
        {
            closed = true;
            try { client.Close(); } catch { }
            try { streamer?.Dispose(); } catch { }
        }

        void Send(int flags, byte[] payload)
        {
            var b = new byte[payload.Length + 5];
            b[0] = (byte)(payload.Length >> 24); b[1] = (byte)(payload.Length >> 16); b[2] = (byte)(payload.Length >> 8); b[3] = (byte)payload.Length;
            b[4] = (byte)flags;
            Buffer.BlockCopy(payload, 0, b, 5, payload.Length);
            lock (sendLock) ns.Write(b, 0, b.Length);
        }

        void TvStatus(string s) { status(s); if (v2) Send(0x10, Encoding.UTF8.GetBytes(s)); }

        public void Run()
        {
            DisplayTarget target = null;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;
                ns = client.GetStream();
                var hello = Parse(ReadLine(ns));
                if (!hello.ContainsKey("TVSP1")) throw new Exception("Handshake invalide");
                client.ReceiveTimeout = 0;
                v2 = Int(hello, "v", 1) >= 2;
                string tv = hello.TryGetValue("name", out var n) ? n : "TV";
                int tvW = Int(hello, "w", 1920), tvH = Int(hello, "h", 1080), hz = Int(hello, "hz", 60);
                // v1 sends hevc=0|1; v2 sends the max height decodable at 60 fps per codec.
                int hevcMax = Int(hello, "hevc", 0), avcMax = Int(hello, "avc", tvH);
                if (!v2 && hevcMax == 1) hevcMax = tvH;
                Log.W($"TV '{tv}' v{(v2 ? 2 : 1)} {client.Client.RemoteEndPoint} {tvW}x{tvH}@{hz} hevc≤{hevcMax} avc≤{avcMax}");
                StartReader();

                // ---- 1. Network: latency + throughput (v2 TVs) ----
                double mbps = 60, rtt = 0;
                if (v2)
                {
                    TvStatus("Analyse du réseau…");
                    mbps = MeasureMbps(out rtt);
                    Log.W($"Network: {mbps:0} Mb/s, RTT {rtt:0.0} ms");
                }
                bool auto = Settings.Res < 0, autoQ = Settings.Quality <= 0;

                // ---- 2. Resolution + bitrate budget ----
                bool hevcTv = hevcMax >= 720;
                int decMax = Math.Max(hevcTv ? hevcMax : 0, avcMax);
                int fps = hz >= 49 && hz <= 51 ? 50 : 60;  // match the TV refresh: no judder
                double budget = mbps * 1000 * 0.55;        // keep headroom for Wi-Fi fluctuations
                var heights = new List<int>();
                foreach (int hh in new[] { 2160, 1440, 1080, 900, 720 })
                    if (hh <= Math.Min(tvH, Math.Max(decMax, 720)) || hh == 720) heights.Add(hh);
                if (!auto)
                {
                    int want = Settings.Res == 0 ? tvH : Settings.Res;
                    heights.RemoveAll(hh => hh > want);
                    if (heights.Count == 0) heights.Add(720);
                }
                else
                {
                    // Highest resolution whose "high quality" bitrate fits in the measured budget.
                    while (heights.Count > 1 && Ideal(W(heights[0]), heights[0], fps, hevcTv) > budget) heights.RemoveAt(0);
                }

                // ---- 3. Virtual display + encoder + live calibration ----
                TvStatus("Préparation de l'écran…");
                target = VirtualDisplay.Attach(W(heights[0]), heights[0], fps);
                var enc = Streamer.Pick(target, hevcTv);
                if (!enc.Hardware) heights.RemoveAll(hh => hh > 1080 && heights.Count > 1);
                EncodePlan plan = null;
                for (int i = 0; i < heights.Count && plan == null; i++)
                {
                    if (target.Height != heights[i]) target = VirtualDisplay.Resize(target, W(heights[i]), heights[i], fps);
                    TvStatus($"Calibrage de l'encodeur ({heights[i]}p)…");
                    plan = Streamer.Calibrate(enc, target, i == heights.Count - 1 ? new[] { fps, 30 } : new[] { fps });
                }
                if (plan == null) plan = new EncodePlan { Enc = enc, Quality = false, Fps = 30 };
                bool hevc = enc.Codec == "hevc";
                double ideal = Ideal(target.Width, target.Height, plan.Fps, hevc);
                int kbps = (int)(autoQ ? Math.Max(ideal * 0.6, Math.Min(budget, ideal * 2.5)) : ideal * 1.4 * Settings.Quality);
                int maxKbps = kbps;
                client.SendBufferSize = Math.Max(64 << 10, kbps * 1000 / 8 / 25); // ~40 ms of video: low latency
                Log.W($"Plan: {target.Width}x{target.Height} {plan} {kbps} kb/s (budget {budget:0}, ideal {ideal:0})");

                if (v2) Send(0x20, Encoding.ASCII.GetBytes($"codec={enc.Codec};w={target.Width};h={target.Height};fps={plan.Fps};kbps={kbps};enc={plan.Enc.Name}"));
                else { var ok = Encoding.ASCII.GetBytes($"OK;codec={enc.Codec};w={target.Width};h={target.Height}\n"); ns.Write(ok, 0, ok.Length); }
                string info = $"{target.Width}x{target.Height} {plan.Fps} i/s {enc.Codec.ToUpper()} {kbps / 1000} Mb/s";
                status($"Diffusion vers {tv} – {info}");
                notify("TVScreenPlus", $"{tv} connectée : {info}");

                // ---- 4. Stream with congestion control ----
                int quickFails = 0, congested = 0;
                var window = Stopwatch.StartNew();
                var cleanSince = Stopwatch.StartNew();
                long blockedTicks = 0;
                while (!closed)
                {
                    bool restart = false;
                    int kbpsNow = kbps;
                    using (var st = new Streamer())
                    {
                        streamer = st;
                        try
                        {
                            st.Run(plan, target, kbpsNow, (buf, o, l) =>
                            {
                                long t0 = Stopwatch.GetTimestamp();
                                lock (sendLock) ns.Write(buf, o, l);
                                blockedTicks += Stopwatch.GetTimestamp() - t0;
                                if (window.ElapsedMilliseconds < 1000) return;
                                // >25 % of the time blocked on the socket = network can't keep up.
                                double blocked = blockedTicks / (double)Stopwatch.Frequency / window.Elapsed.TotalSeconds;
                                blockedTicks = 0; window.Restart();
                                congested = blocked > 0.25 ? congested + 1 : 0;
                                if (blocked > 0.1) cleanSince.Restart();
                                if (congested >= 2 && kbps > ideal * 0.35)
                                {
                                    kbps = (int)Math.Max(ideal * 0.35, kbps * 0.7);
                                    Log.W($"Congestion ({blocked:P0} blocked) -> {kbps} kb/s");
                                    restart = true; congested = 0; st.Dispose();
                                }
                                else if (kbps < maxKbps && cleanSince.Elapsed.TotalSeconds > 45)
                                {
                                    kbps = Math.Min(maxKbps, (int)(kbps * 1.25));
                                    Log.W($"Network OK -> {kbps} kb/s");
                                    restart = true; cleanSince.Restart(); st.Dispose();
                                }
                            });
                        }
                        catch (IOException) { if (!restart) break; }
                        catch (ObjectDisposedException) { if (!restart) break; }
                        if (closed) break;
                        if (restart) { status($"Diffusion vers {tv} – {target.Width}x{target.Height} {kbps / 1000} Mb/s"); continue; }
                        // ffmpeg ended (display change, secure desktop, driver reset…): restart it.
                        quickFails = (DateTime.UtcNow - st.Started).TotalSeconds < 3 ? quickFails + 1 : 0;
                    }
                    if (quickFails >= 3)
                    {
                        Log.W("Encoder keeps failing, re-probing");
                        Streamer.Forget(target);
                        target = Dxgi.Find(target.GdiName) ?? VirtualDisplay.Attach(target.Width, target.Height, plan.Fps);
                        var e2 = Streamer.Pick(target, hevcTv);
                        plan = new EncodePlan { Enc = e2, Quality = false, Fps = plan.Fps };
                        if (v2) Send(0x20, Encoding.ASCII.GetBytes($"codec={e2.Codec};w={target.Width};h={target.Height};fps={plan.Fps};kbps={kbps};enc={e2.Name}"));
                        quickFails = 0;
                    }
                    Thread.Sleep(300);
                }
            }
            catch (Exception e)
            {
                Log.W("Session ended: " + e.Message);
                if (!closed) notify("TVScreenPlus", "Erreur : " + e.Message);
            }
            finally
            {
                Close();
                VirtualDisplay.Detach();
                Log.W("Session closed");
            }
        }

        static int W(int h) => (h * 16 / 9 + 7) & ~7;

        /// <summary>Bitrate (kb/s) for visually transparent desktop quality.</summary>
        static double Ideal(int w, int h, int fps, bool hevc) => w * (double)h * fps * (hevc ? 0.10 : 0.15) / 1000;

        double MeasureMbps(out double rttMs)
        {
            var sw = Stopwatch.StartNew();
            Send(0x40, new byte[0]);
            if (!ack.WaitOne(3000)) throw new Exception("La TV ne répond pas");
            rttMs = sw.Elapsed.TotalMilliseconds;
            var filler = new byte[256 * 1024];
            long sent = 0;
            sw.Restart();
            while (sw.ElapsedMilliseconds < 1000 && sent < (96L << 20)) { Send(0x80, filler); sent += filler.Length + 5; }
            Send(0x40, new byte[0]);
            if (!ack.WaitOne(10000)) throw new Exception("Réseau trop lent");
            double secs = Math.Max(0.05, sw.Elapsed.TotalSeconds - rttMs / 2000);
            return Math.Min(800, sent * 8 / secs / 1e6);
        }

        /// <summary>Upstream messages from the TV; also detects disconnection.</summary>
        void StartReader()
        {
            new Thread(() =>
            {
                try
                {
                    while (!closed)
                    {
                        string l = ReadLine(ns, true);
                        if (l == null) break;
                        if (l == "ACK") ack.Set();
                        else if (l.StartsWith("STAT")) Log.W("TV " + l);
                    }
                }
                catch { }
                Close();
            }) { IsBackground = true, Name = "tv-reader" }.Start();
        }

        static int Int(Dictionary<string, string> d, string k, int def) => d.TryGetValue(k, out var v) && int.TryParse(v, out int i) ? i : def;

        static Dictionary<string, string> Parse(string line)
        {
            var d = new Dictionary<string, string>();
            foreach (var p in (line ?? "").Split(';'))
            {
                int i = p.IndexOf('=');
                if (i < 0) d[p.Trim()] = ""; else d[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim();
            }
            return d;
        }

        static string ReadLine(Stream s, bool nullOnEof = false)
        {
            var bytes = new List<byte>();
            int c;
            while ((c = s.ReadByte()) >= 0 && c != '\n') { if (bytes.Count < 1024) bytes.Add((byte)c); }
            if (c < 0 && nullOnEof) return null;
            return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
        }
    }
}
