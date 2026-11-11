using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TVScreenPlus
{
    /// <summary>
    /// Discovery (UDP 47801): TV broadcasts "TVSP?" -> PC answers "TVSP!\nname=..\nbusy=0|1".
    /// Stream (TCP 47800): TV sends "TVSP1;name=..;w=..;h=..;hevc=0|1\n", PC answers "OK;codec=..;w=..;h=..\n"
    /// then video packets [u32 len][u8 flags][Annex-B access unit].
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

        public Server(Action<string> status, Action<string, string> notify) { this.status = status; this.notify = notify; }

        public void Start()
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpPort)) { EnableBroadcast = true };
            new Thread(UdpLoop) { IsBackground = true, Name = "discovery" }.Start();
            tcp = new TcpListener(IPAddress.Any, TcpPort);
            tcp.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "accept" }.Start();
            Log.W("Server listening");
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
                        var r = Encoding.UTF8.GetBytes("TVSP!\nname=" + Environment.MachineName + "\nbusy=" + (current != null ? 1 : 0));
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
                try { c = tcp.AcceptTcpClient(); }
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
        volatile bool closed;
        Streamer streamer;

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

        public void Run()
        {
            DisplayTarget target = null;
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = 512 * 1024;
                client.ReceiveTimeout = 5000;
                var ns = client.GetStream();
                var hello = Parse(ReadLine(ns));
                if (!hello.ContainsKey("TVSP1")) throw new Exception("Handshake invalide");
                client.ReceiveTimeout = 0;
                string tv = hello.TryGetValue("name", out var n) ? n : "TV";
                int tvW = Int(hello, "w", 1920), tvH = Int(hello, "h", 1080);
                bool tvHevc = Int(hello, "hevc", 0) == 1;
                Log.W($"TV '{tv}' {client.Client.RemoteEndPoint} {tvW}x{tvH} hevc={tvHevc}");
                status("Connexion à " + tv + "…");

                // Resolution: user choice, never above the TV panel, 16:9.
                int h = Settings.Res == 0 ? tvH : Math.Min(Settings.Res, Math.Max(tvH, 720));
                if (h > 2160) h = 2160;
                int w = (h * 16 / 9 + 7) & ~7;
                const int fps = 60;

                target = VirtualDisplay.Attach(w, h, fps);
                var enc = Streamer.Pick(target, tvHevc, fps);
                bool hevc = enc.Codec == "hevc";
                // ~0.20 bit/pixel H.264, ~0.14 HEVC at 60 fps -> 1080p ≈ 25 / 17 Mb/s, × user quality.
                int kbps = (int)(w * (double)h * fps * (hevc ? 0.14 : 0.20) * Settings.Quality / 1000);
                Log.W($"Streaming {w}x{h}@{fps} {enc.Name} {kbps} kb/s");

                var ok = Encoding.ASCII.GetBytes($"OK;codec={enc.Codec};w={target.Width};h={target.Height}\n");
                ns.Write(ok, 0, ok.Length);
                status($"Diffusion vers {tv} – {w}x{h} {enc.Codec.ToUpper()} ({enc.Name})");
                notify("TVScreenPlus", tv + " est connectée comme écran secondaire.");

                // Detect TV disconnect.
                new Thread(() =>
                {
                    var b = new byte[64];
                    try { while (!closed && ns.Read(b, 0, b.Length) > 0) { } } catch { }
                    Close();
                }) { IsBackground = true }.Start();

                int quickFails = 0;
                while (!closed)
                {
                    using (streamer = new Streamer())
                    {
                        try { streamer.Run(enc, target, fps, kbps, (buf, o, l) => ns.Write(buf, o, l)); }
                        catch (IOException) { break; }
                        catch (ObjectDisposedException) { break; }
                        if (closed) break;
                        // ffmpeg ended (display change, secure desktop, driver reset…): restart it.
                        quickFails = (DateTime.UtcNow - streamer.Started).TotalSeconds < 3 ? quickFails + 1 : 0;
                    }
                    if (quickFails >= 3)
                    {
                        Log.W("Encoder keeps failing, re-probing");
                        Streamer.Forget(target);
                        target = Dxgi.Find(target.GdiName) ?? VirtualDisplay.Attach(w, h, fps);
                        enc = Streamer.Pick(target, tvHevc, fps);
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

        static int Int(Dictionary<string, string> d, string k, int def) => d.TryGetValue(k, out var v) && int.TryParse(v, out int i) ? i : def;

        static Dictionary<string, string> Parse(string line)
        {
            var d = new Dictionary<string, string>();
            foreach (var p in line.Split(';'))
            {
                int i = p.IndexOf('=');
                if (i < 0) d[p.Trim()] = ""; else d[p.Substring(0, i).Trim()] = p.Substring(i + 1).Trim();
            }
            return d;
        }

        static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            for (int c; (c = s.ReadByte()) >= 0 && c != '\n';)
            {
                sb.Append((char)c);
                if (sb.Length > 1024) break;
            }
            return Encoding.UTF8.GetString(Encoding.GetEncoding("ISO-8859-1").GetBytes(sb.ToString()));
        }
    }
}
