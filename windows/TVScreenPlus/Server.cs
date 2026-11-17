using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TVScreenPlus
{
    /// <summary>
    /// Discovery (UDP 47801): TV broadcasts "TVSP?" -> PC answers "TVSP!\nname=..\nbusy=0|1\nver=..\nmac=.." (mac: the
    /// network card on the TV's subnet, 12 hex digits, for Wake-on-LAN; absent if unknown).
    /// Stream (TCP 47800): TV sends "TVSP1;v=2;auth=1;id=..;name=..;w=..;h=..;hz=..;hevc=maxH;avc=maxH\n".
    ///  Authentication first (see Pairing): PC sends extension 5 [5][pc id 16][nonce 32], the TV answers "AUTH;hex"
    ///  (HMAC of the nonce with the pairing key) or "AUTH;none"; an unknown TV pairs: extension 6 [6][pc id][PC public
    ///  key 64], TV answers "PAIR;hex" (its public key) and shows the code, extension 7 [7][1|0] = accepted or not.
    ///  TV apps without auth=1 (before 1.2) are refused with a status text asking to update them.
    ///  Then only packets [u32 len][u8 flags][payload]; flags 0x80 filler, 0x40 ping (TV answers "ACK\n"),
    ///         0x20 config text, 0x10 status text, else video (1 = keyframe, 2 = new stream).
    ///         TV also sends "STAT;fps=..;kbps=..;drop=..;dec=..;decmax=..;wait=..\n" every 2 s
    ///         (dec/decmax = live decoder latency in ms, wait = % of time the decoder input was full)
    ///         and "LOG;text\n" diagnostic lines, written to the PC log.
    ///  Optional features, used only when the TV announces them in its hello line (older TV apps are unaffected):
    ///   audio=1: PC sound in flag 0x08 packets whose payload starts with 1 (see AudioLoopback);
    ///   pads=1:  "PADON/PADOFF/PAD" lines from the TV, rumble in flag 0x08 packets starting with 2 (see PadHub).
    ///   sec=1;aes=MB/s: right after authentication the PC sends extension [11][1 commands | 2 picture and sound],
    ///          the TV answers "SEC" in clear and the connection continues encrypted (see SecureChannel);
    ///   remote=1: mouse, keyboard, windows and applications from the TV, only when encrypted (see RemoteControl).
    ///   The PC confirms what it accepted with "audio=0|1;pads=0|1;remote=1;sec=1|3" in its config packet (0x20).
    /// </summary>
    sealed class Server
    {
        public const int TcpPort = 47800, UdpPort = 47801;
        readonly Action<string> status;
        readonly Action<string, string> notify;
        readonly Func<string, string, string, bool> approve;
        int handshaking;              // connections not yet authenticated (bounded: they hold a thread each)
        readonly Dictionary<string, int> handshakesFrom = new Dictionary<string, int>();
        UdpClient udp;
        TcpListener tcp;
        volatile Session current;
        readonly object sessionLock = new object();
        public bool Busy => current != null;
        /// <summary>Live statistics of the current session (null when no TV is connected).</summary>
        public SessionStats Stats => current?.Stats;
        /// <summary>"Son sur la TV" toggled in the tray: applies to the current session right away.</summary>
        public void SetAudio(bool on) => current?.SetAudio(on);
        /// <summary>Applies Settings.GameMode to the running stream (encoder restart, no reconnection).</summary>
        public void SwitchMode() => current?.SwitchMode();

        /// <param name="approve">(TV name, TV address, 6-digit code): asks the person at the PC whether a new TV may pair.</param>
        public Server(Action<string> status, Action<string, string> notify, Func<string, string, string, bool> approve)
        {
            this.status = status; this.notify = notify; this.approve = approve;
        }

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
                    if (i >= 20) throw new Exception(L.T("ports_busy", TcpPort, UdpPort));
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
        public static void NoInherit(Socket s)
        {
            try { SetHandleInformation(s.Handle, 1 /*HANDLE_FLAG_INHERIT*/, 0); } catch { }
        }

        /// <summary>
        /// Private, link-local or loopback IPv4: a TV on the home network. Nothing else is answered, even if the
        /// firewall rule were missing (VPN peers, port forwarding, other subnets).
        /// </summary>
        public static bool Lan(IPAddress a)
        {
            if (IPAddress.IsLoopback(a)) return true;
            var b = a.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
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
                    if (d.Length >= 5 && Lan(ep.Address) && Encoding.ASCII.GetString(d, 0, 5) == "TVSP?")
                    {
                        string mac = MacFacing(ep.Address);
                        var r = Encoding.UTF8.GetBytes("TVSP!\nname=" + Environment.MachineName + "\nbusy=" + (current != null ? 1 : 0) + "\nver=" + Program.Version +
                                                       (mac != null ? "\nmac=" + mac : ""));
                        udp.Send(r, r.Length, ep);
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception e) { Log.W("udp: " + e.Message); Thread.Sleep(200); }
            }
        }

        /// <summary>Hardware address of the network card on the same subnet as this address (Wake-on-LAN), or null.</summary>
        static string MacFacing(IPAddress remote)
        {
            try
            {
                if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
                byte[] r = remote.GetAddressBytes();
                if (r.Length != 4) return null;
                // Ethernet first: a PC on both Ethernet and Wi-Fi wakes from its wired card.
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1))
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] a = ua.Address.GetAddressBytes(), m = ua.IPv4Mask.GetAddressBytes();
                        bool same = true;
                        for (int i = 0; i < 4; i++) if ((a[i] & m[i]) != (r[i] & m[i])) { same = false; break; }
                        if (!same) continue;
                        string mac = nic.GetPhysicalAddress().ToString();
                        return mac.Length == 12 ? mac : null;
                    }
                }
            }
            catch { }
            return null;
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = tcp.AcceptTcpClient(); NoInherit(c.Client); }
                catch (Exception) { return; }
                var from = c.Client.RemoteEndPoint as IPEndPoint;
                if (from == null || !Lan(from.Address))
                {
                    Log.W($"Connection from {from} refused: not on the home network");
                    c.Close();
                    continue;
                }
                // At most 2 handshakes per address and 6 in all (each holds a thread until it ends or times out).
                var addr = from.Address.ToString();
                lock (handshakesFrom)
                {
                    handshakesFrom.TryGetValue(addr, out int fromAddr);
                    if (handshaking >= 6 || fromAddr >= 2)
                    {
                        Log.W($"Connection from {from} refused: too many at once");
                        c.Close();
                        continue;
                    }
                    handshaking++;
                    handshakesFrom[addr] = fromAddr + 1;
                }
                new Thread(() =>
                {
                    var s = new Session(c, status, notify);
                    bool ok;
                    try { ok = s.Handshake(approve); }
                    finally
                    {
                        lock (handshakesFrom)
                        {
                            handshaking--;
                            if (--handshakesFrom[addr] == 0) handshakesFrom.Remove(addr);
                        }
                    }
                    if (!ok) { s.Refuse(); return; }
                    // Only an authenticated TV takes over: stop the previous session, then run this one.
                    current?.Close();
                    lock (sessionLock)
                    {
                        current = s;
                        try { s.Run(); }
                        finally
                        {
                            current = null;
                            status(L.T("ready", Environment.MachineName));
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
        public readonly SessionStats Stats = new SessionStats();
        readonly object sendLock = new object();
        Stream ns;                    // the TV connection (a SecureStream once encrypted)
        volatile bool closed;
        volatile Streamer streamer;       // encoder currently streaming
        volatile Streamer streamerNext;   // encoder starting to replace it (seamless switch)
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Dictionary<string, string> hello; // the TV's first line
        string tvName = "TV";
        bool lingerOnRefuse;          // keep the connection a while so an old TV app shows our message
        volatile bool tvAudio;        // the TV announced audio=1
        volatile bool streaming;      // the TV is showing the stream
        AudioLoopback audio;          // PC sound -> TV
        readonly object audioLock = new object();
        volatile bool modeSwitch;     // fluid <-> game mode asked while streaming
        bool tvStamp;                 // the TV wants each frame's capture time (ts=1): measured capture -> screen latency
        bool tvCursor;                // the TV draws the cursor itself (cursor=1)
        CursorSender cursor;          // cursor position/shape -> TV
        IPEndPoint audioUdp;          // TV's UDP port for the sound (audioudp=), null = sound in the TCP stream
        UdpClient udpOut;
        volatile PadHub pads;         // TV gamepads -> virtual Xbox 360 controllers
        byte[] secBase;               // from the pairing key and this connection's challenge (SecureChannel)
        volatile bool secureUp;       // what the TV sends is encrypted and authenticated: remote control allowed
        bool secureDown;              // the picture and the sound are encrypted too
        SecureDatagrams udpSeal;      // UDP sound / cursor packets encrypted (with secureDown)
        volatile RemoteControl remote; // mouse, keyboard, windows, applications from the TV
        volatile string tvDevice;     // GDI name of the TV's virtual display (remote control)

        public Session(TcpClient c, Action<string> status, Action<string, string> notify)
        {
            client = c; this.status = status; this.notify = notify;
        }

        public void Close()
        {
            closed = true;
            try { client.Close(); } catch { }
            try { streamer?.Dispose(); } catch { }
            try { streamerNext?.Dispose(); } catch { }
            try { wake.Set(); } catch { }
            SetAudio(false);
            try { pads?.Dispose(); } catch { }
            try { cursor?.Dispose(); } catch { }
            try { udpOut?.Close(); } catch { }
            try { remote?.Dispose(); } catch { }
            try { udpSeal?.Dispose(); } catch { }
            try { (ns as SecureStream)?.Dispose(); } catch { }
        }

        /// <summary>Sound and cursor packets to the TV, encrypted when the picture is.</summary>
        void SendUdp(UdpClient u, byte[] p, IPEndPoint ep)
        {
            var s = udpSeal;
            var d = s != null ? s.Seal(p) : p;
            u.Send(d, d.Length, ep);
        }

        UdpClient Udp()
        {
            lock (audioLock)
            {
                if (udpOut == null)
                {
                    // Sent from the address the TV connected to: the TV only accepts sound and cursor packets from it
                    // (a PC with Ethernet + Wi-Fi on the same network could otherwise send from the other one).
                    var local = (client.Client.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Any;
                    udpOut = new UdpClient(new IPEndPoint(local, 0));
                    Server.NoInherit(udpOut.Client);
                }
                return udpOut;
            }
        }

        /// <summary>Wall clock shared with ffmpeg's frame stamps and the sound (ms since 1970 mod 2^31).</summary>
        // Same precise clock as the capture timestamps (DateTime.UtcNow ticks in up to 16 ms steps on .NET Framework).
        static uint NowMs() => WallClock.Ms(Stopwatch.GetTimestamp());
        /// <summary>Difference of two 31-bit clock values, as a small signed number.</summary>
        static long Wrap31(long x) { x &= 0x7FFFFFFF; return x >= 0x40000000 ? x - 0x80000000L : x; }

        /// <summary>Ping payload: PC time, so that the TV can relate both clocks (older TVs ignore it).</summary>
        static byte[] PingPayload() { uint t = NowMs(); return new[] { (byte)(t >> 24), (byte)(t >> 16), (byte)(t >> 8), (byte)t }; }

        /// <summary>Restart the encoder with the current mode (fluid / game) and tell the TV.</summary>
        public void SwitchMode()
        {
            if (!streaming || closed) return;
            modeSwitch = true;
            wake.Set(); // the streaming loop starts the new encoder; the current one keeps going until it takes over
        }

        /// <summary>Starts / stops sending the PC sound (only to a TV that supports it, once streaming).</summary>
        public void SetAudio(bool on)
        {
            lock (audioLock)
            {
                if (on && tvAudio && streaming && !closed && audio == null)
                {
                    if (audioUdp != null)
                    {
                        // Own UDP channel: sound never waits behind a large video frame in the TCP stream
                        // (measured: up to 177 ms of arrival jitter when YouTube pushed the video to 40-58 Mb/s).
                        // 5 ms packets (~1 kB) fit in one Ethernet frame.
                        var ep = audioUdp; var u = Udp();
                        audio = new AudioLoopback(p => { if (!closed) try { SendUdp(u, p, ep); } catch { } }, Stats, 5);
                        Log.W($"Audio to TV: on (UDP {ep})");
                    }
                    else
                    {
                        audio = new AudioLoopback(p => { if (!closed) try { Send(0x08, p); } catch { } }, Stats);
                        Log.W("Audio to TV: on (TCP)");
                    }
                }
                else if (!on && audio != null)
                {
                    audio.Dispose(); audio = null;
                    if (!closed) Stats.Audio = L.T("au_off_pc");
                    Stats.AudioKbps = 0;
                    Log.W("Audio to TV: off");
                }
            }
        }

        /// <summary>A game set the motors of a virtual controller: forward to the TV gamepad.</summary>
        void SendRumble(int slot, int large, int small)
        {
            if (closed) return;
            try { Send(0x08, new[] { PadHub.RumbleKind, (byte)slot, (byte)large, (byte)small }); } catch { }
        }

        void Send(int flags, byte[] payload)
        {
            var b = new byte[payload.Length + 5];
            b[0] = (byte)(payload.Length >> 24); b[1] = (byte)(payload.Length >> 16); b[2] = (byte)(payload.Length >> 8); b[3] = (byte)payload.Length;
            b[4] = (byte)flags;
            Buffer.BlockCopy(payload, 0, b, 5, payload.Length);
            lock (sendLock) ns.Write(b, 0, b.Length);
        }

        string lang = "fr";
        void TvStatus(string key, params object[] a)
        {
            status(string.Format(Tr(L.Lang, key), a));
            TvText(key, a);
        }

        /// <summary>Text shown on the TV only, in its language.</summary>
        void TvText(string key, params object[] a) => Send(0x10, Encoding.UTF8.GetBytes(string.Format(Tr(lang, key), a)));

        static string Tr(string lang, string key)
        {
            int i = Array.IndexOf(new[] { "fr", "en", "es", "de" }, lang); if (i < 0) i = 1;
            switch (key)
            {
                case "net": return new[] { "Analyse du réseau…", "Analysing the network…", "Analizando la red…", "Netzwerk wird analysiert…" }[i];
                case "screen": return new[] { "Préparation de l'écran…", "Preparing the screen…", "Preparando la pantalla…", "Bildschirm wird vorbereitet…" }[i];
                case "update_tv": return new[] {
                    "Mettez à jour l'application TV (Paramètres → Rechercher une mise à jour) pour vous connecter à ce PC.",
                    "Update the TV app (Settings → Check for updates) to connect to this PC.",
                    "Actualice la aplicación de la TV (Ajustes → Buscar actualizaciones) para conectarse a este PC.",
                    "Aktualisieren Sie die TV-App (Einstellungen → Nach Updates suchen), um sich mit diesem PC zu verbinden." }[i];
                case "refused": return new[] { "Connexion refusée sur le PC.", "Connection refused on the PC.", "Conexión rechazada en el PC.", "Verbindung am PC abgelehnt." }[i];
                case "auth_failed": return new[] {
                    "Cette TV n'est plus reconnue par le PC : oubliez les ordinateurs dans les paramètres, puis reconnectez-vous.",
                    "This PC no longer recognises this TV: forget the computers in the settings, then connect again.",
                    "Este PC ya no reconoce esta TV: olvide los ordenadores en los ajustes y vuelva a conectarse.",
                    "Dieser PC erkennt diesen Fernseher nicht mehr: Computer in den Einstellungen vergessen und neu verbinden." }[i];
                default: return new[] { "Calibrage de l'encodeur ({0}p)…", "Calibrating the encoder ({0}p)…", "Calibrando el codificador ({0}p)…", "Encoder wird kalibriert ({0}p)…" }[i];
            }
        }

        /// <summary>Refused pairings per TV address: no new request for a minute (no dialog flood).</summary>
        static readonly Dictionary<string, DateTime> refusedAt = new Dictionary<string, DateTime>();

        /// <summary>
        /// Reads the TV's hello and checks that it is a paired TV, or pairs it if the person at the PC accepts.
        /// Runs before this TV can affect anything (the current stream keeps going meanwhile). False = refused.
        /// TV apps with auth2=1 (1.2.5+) answer a challenge that covers their hello line and this PC's address, and
        /// committed to their pairing key in the hello; 1.2.4 apps (no auth2) keep working, but always fully encrypted
        /// (their answer could be relayed by a fake PC); apps that cannot encrypt (before 1.2.4) must be updated.
        /// </summary>
        public bool Handshake(Func<string, string, string, bool> approve)
        {
            // A connection that never finishes its handshake (slow drip of bytes) is closed: it holds a slot.
            var deadline = new Timer(_ => { if (!approving) try { client.Close(); } catch { } }, null, 15000, Timeout.Infinite);
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;
                ns = client.GetStream();
                var raw = ReadHello(ns);
                hello = Parse(Encoding.UTF8.GetString(raw));
                if (!hello.ContainsKey("TVSP1")) return false;
                lang = hello.TryGetValue("lang", out var lg) ? lg : "fr";
                tvName = Clean(hello.TryGetValue("name", out var n) ? n : "");
                string id = hello.TryGetValue("id", out var i) ? i : null;
                string ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                if (Int(hello, "v", 1) < 2 || Int(hello, "auth", 0) != 1 || Int(hello, "sec", 0) != 1 || !Pairing.ValidId(id))
                {
                    Log.W($"TV '{tvName}' {ip}: app too old (no authentication or no encryption), asked to update");
                    if (Int(hello, "v", 1) >= 2) { TvText("update_tv"); lingerOnRefuse = true; }
                    return false;
                }
                bool v2 = Int(hello, "auth2", 0) == 1;
                var helloHash = Pairing.Sha256(raw);
                var local = (client.Client.LocalEndPoint as IPEndPoint)?.Address;
                if (local != null && local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
                var pcAddress = local?.GetAddressBytes() ?? new byte[0];

                var key = Pairing.KeyOf(id);
                if (key != null)
                {
                    var nonce = Pairing.Random(32);
                    Send(0x08, new byte[] { (byte)(v2 ? 13 : 5) }.Concat(Pairing.PcId).Concat(nonce).ToArray());
                    string answer = ReadReply("AUTH;");
                    if (answer != "none")
                    {
                        var expected = v2 ? Pairing.Proof2(key, nonce, helloHash, pcAddress) : Pairing.Proof(key, nonce);
                        if (Pairing.Same(Pairing.Unhex(answer), expected))
                        {
                            secBase = SecureChannel.Base(key, nonce);
                            return Secure(forceDown: !v2);
                        }
                        Log.W($"TV '{tvName}' {ip}: wrong pairing key{(v2 ? " or connection relayed by another device" : "")}, refused");
                        Deny("auth_failed");
                        return false;
                    }
                    Log.W($"TV '{tvName}' {ip} lost its pairing key: pairing again");
                }
                // Pairing needs the TV's commitment to its key (sent before it saw ours): without it, a device in the
                // middle could try keys until both screens show the same code.
                var commit = v2 && hello.TryGetValue("commit", out var cm) ? Pairing.Unhex(cm) : null;
                if (commit == null || commit.Length != 32)
                {
                    Log.W($"TV '{tvName}' {ip}: app too old to pair safely, asked to update");
                    Deny("update_tv");
                    return false;
                }
                lock (refusedAt)
                    if (refusedAt.TryGetValue(ip, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(1)) { Deny("refused"); return false; }
                using (var ex = new Pairing.Exchange())
                {
                    Send(0x08, new byte[] { 6 }.Concat(Pairing.PcId).Concat(ex.PublicKey).ToArray());
                    var tvPublic = Pairing.Unhex(ReadReply("PAIR;"));
                    if (tvPublic == null || !Pairing.Same(Pairing.Sha256(tvPublic), commit))
                    {
                        Log.W($"Pairing with TV '{tvName}' {ip}: key does not match its commitment (device in the middle?), refused");
                        Deny("auth_failed");
                        return false;
                    }
                    var shared = ex.Key(tvPublic);
                    string code = Pairing.Code(shared, ex.PublicKey, tvPublic);
                    Log.W($"Pairing request from TV '{tvName}' {ip}");
                    approving = true;     // the person may take a while to answer
                    client.ReceiveTimeout = 0;
                    bool ok = approve(tvName, ip, code);
                    if (!ok)
                    {
                        Log.W($"Pairing with TV '{tvName}' {ip} refused");
                        lock (refusedAt) refusedAt[ip] = DateTime.UtcNow;
                        Deny("refused");
                        return false;
                    }
                    Send(0x08, new byte[] { 7, 1 });
                    // The TV confirms on its side too, then proves the new key like on every connection.
                    client.ReceiveTimeout = 180000;
                    var nonce = Pairing.Random(32);
                    Send(0x08, new byte[] { 13 }.Concat(Pairing.PcId).Concat(nonce).ToArray());
                    var proof = Pairing.Unhex(ReadReply("AUTH;"));
                    client.ReceiveTimeout = 5000;
                    if (!Pairing.Same(proof, Pairing.Proof2(shared, nonce, helloHash, pcAddress)))
                    {
                        Log.W($"Pairing with TV '{tvName}' {ip}: not confirmed on the TV");
                        return false;
                    }
                    Pairing.Trust(id, tvName, shared);
                    Log.W($"TV '{tvName}' {ip} paired");
                    secBase = SecureChannel.Base(shared, nonce);
                    return Secure(forceDown: false);
                }
            }
            catch (Exception e) { Log.W("Handshake failed: " + e.Message); return false; }
            finally { deadline.Dispose(); }
        }
        volatile bool approving;      // waiting for the person at the PC: the handshake deadline does not apply

        /// <summary>
        /// Authenticated TV: switch the connection to encryption. [11][bits] tells it what is encrypted; it answers
        /// "SEC" in clear and only sends encrypted records after it. What the TV sends is always encrypted (a few
        /// bytes); the picture and the sound follow Settings.Crypt (auto: if the TV is fast enough), or always
        /// (forceDown) for a TV app whose answer could have been relayed.
        /// </summary>
        bool Secure(bool forceDown)
        {
            int aes = Int(hello, "aes", 0);
            bool wifi = hello.TryGetValue("net", out var net) && net == "wifi";
            string mode = Settings.Crypt;
            bool down = forceDown || mode == "on" || (mode == "auto" && SecureChannel.AutoDown(aes, wifi));
            Send(0x08, new byte[] { 11, (byte)(SecureChannel.UpBit | (down ? SecureChannel.DownBit : 0)) });
            ReadReply("SEC");
            ns = new SecureStream(ns, down ? SecureChannel.Key(secBase, "down") : null, SecureChannel.Key(secBase, "up"));
            secureUp = true; secureDown = down;
            if (down) udpSeal = new SecureDatagrams(SecureChannel.Key(secBase, "udp"));
            Stats.Crypt = forceDown ? L.T("crypt_all_old")
                        : down ? L.T("crypt_all")
                        : L.T("crypt_cmd") + (mode == "off" ? L.T("crypt_cmd_never") : L.T("crypt_cmd_slow", aes));
            Log.W($"Encryption: commands{(down ? " + picture and sound" : "")} (setting {mode}{(forceDown ? ", forced: TV app 1.2.4" : "")}, TV AES {aes} MB/s, {(wifi ? "Wi-Fi" : "wired")})");
            // Diagnostic lines the TV sent before it was authenticated are only written now.
            foreach (var l in preAuthLog) TvLog(l);
            preAuthLog.Clear();
            return true;
        }

        /// <summary>Tells the TV it is refused (it then stops reconnecting by itself) and why.</summary>
        void Deny(string key)
        {
            Send(0x08, new byte[] { 7, 0 });
            TvText(key);
        }

        /// <summary>The TV's LOG lines received during the handshake (logged once it is authenticated).</summary>
        readonly List<string> preAuthLog = new List<string>();

        /// <summary>Next line starting with prefix (the TV's diagnostic LOG lines may come first).</summary>
        string ReadReply(string prefix)
        {
            for (int i = 0; i < 100; i++)
            {
                string l = ReadLine(ns, true) ?? throw new IOException("TV disconnected");
                if (l.StartsWith(prefix)) return l.Substring(prefix.Length).Trim();
                if (l.StartsWith("LOG;") && preAuthLog.Count < 40) preAuthLog.Add(l.Substring(4, Math.Min(l.Length - 4, 400)));
            }
            throw new IOException("no answer from the TV");
        }

        /// <summary>A diagnostic line from the TV: logged; its model and settings are kept for "Copy for a report".</summary>
        void TvLog(string text)
        {
            Log.W("TV: " + text);
            if (text.StartsWith("device ")) SessionStats.LastTvDevice = text.Substring(7);
            else if (text.StartsWith("settings ")) SessionStats.LastTvSettings = text.Substring(9);
            else if (text.StartsWith("crypto: ")) SessionStats.LastTvCrypto = text.Substring(8);
        }

        /// <summary>Ends a refused connection; an old TV app keeps it a minute so that our message stays on its screen.</summary>
        public void Refuse()
        {
            if (lingerOnRefuse && Interlocked.Increment(ref lingering) <= 2)
                try { Thread.Sleep(60000); } finally { Interlocked.Decrement(ref lingering); }
            else if (lingerOnRefuse) Interlocked.Decrement(ref lingering);
            Close();
        }
        static int lingering;

        /// <summary>TV name as shown on the PC (pairing dialog, notifications, settings): printable, short.</summary>
        static string Clean(string s)
        {
            s = new string((s ?? "").Where(ch => !char.IsControl(ch) && ch != '|').ToArray()).Trim();
            return s.Length == 0 ? "TV" : s.Length > 40 ? s.Substring(0, 40) : s;
        }

        public void Run()
        {
            DisplayTarget target = null;
            System.Threading.Tasks.Task<DisplayTarget> early = null; // virtual display prepared during the network test
            try
            {
                client.ReceiveTimeout = 0;
                string tv = tvName;
                int tvW = Int(hello, "w", 1920), tvH = Int(hello, "h", 1080), hz = Int(hello, "hz", 60);
                // Max height decodable at 60 fps per codec.
                int hevcMax = Int(hello, "hevc", 0), avcMax = Int(hello, "avc", tvH);
                string pref = hello.TryGetValue("pref", out var pf) ? pf : "auto";   // auto | smooth | quality (TV setting)
                int tvMaxH = Int(hello, "maxh", 0);                                     // TV-side resolution cap, 0 = none
                string net = hello.TryGetValue("net", out var nt) ? nt : "?";
                string band = hello.TryGetValue("band", out var bd) ? bd : "0";
                Stats.Tv = tv; Stats.TvLatencyMs = Int(hello, "lat", -1);
                Stats.Network = net == "eth" ? "Ethernet" : net == "wifi" ? L.T("wifi", band, Int(hello, "link", 0)) : "?";
                // Optional features: only what this TV announced (older TV apps announce nothing).
                int helloAudio = Int(hello, "audio", -1), helloPads = Int(hello, "pads", -1);
                tvAudio = helloAudio == 1;
                int udpPort = Int(hello, "audioudp", 0); // UDP port of the TV: sound and cursor positions
                if (udpPort > 0 && udpPort < 65536 && client.Client.RemoteEndPoint is IPEndPoint tvEp)
                    audioUdp = new IPEndPoint(tvEp.Address, udpPort);
                tvStamp = Int(hello, "ts", 0) == 1;
                tvCursor = Int(hello, "cursor", 0) == 1;
                bool padsOn = helloPads == 1 && Settings.Pads;
                Stats.Audio = L.T(helloAudio < 0 ? "au_unsupported" : !tvAudio ? "au_off_tv" : Settings.Audio ? "au_waiting" : "au_off_pc");
                Stats.Pads = L.T(helloPads < 0 ? "pads_unsupported" : helloPads == 0 ? "pads_off_tv" : padsOn ? "pads_none" : "pads_off_pc");
                if (padsOn) pads = new PadHub(SendRumble, notify, Stats);
                Log.W($"TV '{tv}' {client.Client.RemoteEndPoint} {tvW}x{tvH}@{hz} hevc≤{hevcMax} avc≤{avcMax} " +
                      $"tested={Int(hello, "tested", 0)} lat={Stats.TvLatencyMs}ms net={Stats.Network} pref={pref} maxh={tvMaxH} audio={helloAudio} pads={helloPads}");
                StartReader();

                // The virtual display takes ~0.7 s to appear (attach + mode set): prepare it at the highest resolution
                // this TV can get while the network is measured (1 s); a slow network then resizes it.
                int fps = hz >= 49 && hz <= 51 ? 50 : 60;  // match the TV refresh: no judder
                bool hevcTv = hevcMax >= 720;
                int decMax = Math.Max(hevcTv ? hevcMax : 0, avcMax);
                var heights = Heights();
                early = System.Threading.Tasks.Task.Run(() => VirtualDisplay.Attach(W(heights[0]), heights[0], fps));

                // ---- 1. Network: latency + throughput ----
                double mbps = 60, rtt = 0;
                TvStatus("net");
                mbps = MeasureMbps(out rtt);
                Log.W($"Network: {mbps:0} Mb/s, RTT {rtt:0.0} ms");
                bool auto = Settings.Res < 0, autoQ = Settings.Quality <= 0;

                // ---- 2. Resolution + bitrate budget ----
                // Headroom for fluctuations: wired is steady, 2.4 GHz Wi-Fi is not. TV priority adjusts it.
                double share = net == "eth" ? 0.7 : band == "2.4" ? 0.45 : 0.55;
                if (pref == "smooth") share *= 0.7; else if (pref == "quality") share = Math.Min(0.8, share * 1.3);
                double budget = mbps * 1000 * share;
                // The PC sound travels with the video: keep room for it.
                int audioKbps = tvAudio && Settings.Audio ? AudioLoopback.Kbps : 0;
                budget = Math.Max(budget * 0.5, budget - audioKbps);
                Stats.MeasuredMbps = mbps; Stats.RttMs = rtt;
                List<int> Heights()
                {
                    var hs = new List<int>();
                    foreach (int hh in new[] { 2160, 1440, 1080, 900, 720 })
                        if (hh <= Math.Min(tvH, Math.Max(decMax, 720)) || hh == 720) hs.Add(hh);
                    if (tvMaxH > 0) hs.RemoveAll(hh => hh > Math.Max(tvMaxH, 720));
                    if (pref == "smooth") hs.RemoveAll(hh => hh > 1080 && hs.Count > 1);
                    if (Settings.Res >= 0)
                    {
                        int want = Settings.Res == 0 ? tvH : Settings.Res;
                        hs.RemoveAll(hh => hh > want);
                        if (hs.Count == 0) hs.Add(720);
                    }
                    return hs;
                }
                if (auto)
                {
                    // Highest resolution whose "high quality" bitrate fits in the measured budget.
                    while (heights.Count > 1 && Ideal(W(heights[0]), heights[0], fps, hevcTv) > budget) heights.RemoveAt(0);
                }

                // ---- 3. Virtual display + encoder + live calibration ----
                TvStatus("screen");
                target = early.GetAwaiter().GetResult(); early = null;
                if (target.Height != heights[0]) target = VirtualDisplay.Resize(target, W(heights[0]), heights[0], fps);
                var enc = Streamer.Pick(target, hevcTv, tvCursor && Settings.NativeEncoder);
                if (!enc.Hardware) heights.RemoveAll(hh => hh > 1080 && heights.Count > 1);
                EncodePlan plan = null;
                for (int i = 0; i < heights.Count && plan == null; i++)
                {
                    if (target.Height != heights[i]) target = VirtualDisplay.Resize(target, W(heights[i]), heights[i], fps);
                    TvStatus("calib", heights[i]);
                    var fpsChoices = i == heights.Count - 1 ? new[] { fps, 30 } : new[] { fps };
                    plan = Streamer.Calibrate(enc, target, fpsChoices);
                    if (plan == null && enc.Native)
                    {
                        // The in-house encoder works but too slowly here: ffmpeg at this same resolution before
                        // going lower (a lower resolution is a worse fallback than the 1.0 encoder). The slow result
                        // is cached, so later connections switch at once.
                        var ff = Streamer.Pick(target, hevcTv, false);
                        Log.W($"In-house encoder too slow at {target.Width}x{target.Height}: trying {ff.Name}");
                        enc = ff;
                        plan = Streamer.Calibrate(enc, target, fpsChoices);
                    }
                }
                if (plan == null) plan = new EncodePlan { Enc = enc, Quality = false, Fps = 30 };
                tvDevice = target.GdiName;
                // Remote control only over an encrypted, authenticated upstream (keyboard input = full control of the PC).
                if (secureUp && Int(hello, "remote", 0) == 1)
                    remote = new RemoteControl((kind, text) =>
                    {
                        if (closed) return;
                        try { Send(0x08, new[] { kind }.Concat(Encoding.UTF8.GetBytes(text)).ToArray()); } catch { }
                    }, () => tvDevice);
                bool hevc = enc.Codec == "hevc";
                double ideal = Ideal(target.Width, target.Height, plan.Fps, hevc);
                int kbps = (int)(autoQ ? Math.Max(ideal * 0.6, Math.Min(budget, ideal * 2.5)) : ideal * 1.4 * Settings.Quality);
                // Never above what the measured link can carry, even with a forced quality: the encoder's peak
                // rate (maxrate) beyond the link only queues frames (lag) then triggers congestion drops.
                string capped = "";
                int desired = kbps;              // what the quality setting asks for
                // A slow link (100 Mb/s TV port, Wi-Fi) saturates on the encoder's peaks (keyframes, scene changes):
                // measured on a 93 Mb/s TV link at 77 Mb/s: 43 % blocked, bitrate drops + encoder restarts (freezes)
                // and the sound queued behind the video (stutter). Keep more headroom there.
                // Wi-Fi delivers in bursts (50-70 ms of sound at once measured on 5 GHz Wi-Fi 6): big encoder
                // peaks make it worse, so half the measured rate and never above 80 Mb/s (plenty for 4K HEVC).
                // These are only starting points: the ceiling then rises while the network stays perfectly
                // healthy and drops on congestion, and is remembered per TV and network type, so a better
                // setup (gigabit port, clean Wi-Fi) earns a higher one.
                int hardCap = (int)(mbps * 1000 * 0.85) - audioKbps; // never above 85 % of the measured link
                // Wi-Fi: never above 70 Mb/s (the Ethernet level, plenty for 4K HEVC). Once the ceiling could rise
                // it went to 139 Mb/s on a 300 Mb/s link: +10 ms of latency and saturation hiccups, no visible gain.
                if (net == "wifi") hardCap = Math.Min(hardCap, 70000);
                // Keyed by network TYPE, not by the measured speed: one wrong measurement (481 Mb/s once on a
                // 94 Mb/s port) must not bypass what this TV has learned (it then aimed at 139 Mb/s and froze).
                string tvKey = "netcap_" + new string(tv.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()) + "_";
                string capKey = tvKey + (net == "wifi" ? "wifi" : "eth"); // learned ceiling for this TV on this kind of network
                double defShare = net == "wifi" ? 0.5 : mbps < 150 ? 0.6 : 0.85;
                // First connection on this network type: at most 50 Mb/s, the ceiling then rises if all is well.
                int defCap = Math.Min((int)(mbps * 1000 * defShare) - audioKbps, 50000);
                int learned = int.TryParse(Settings.Get(capKey, ""), out int lc) ? lc : 0;
                int netCap = Math.Max(1000, Math.Min(hardCap, learned > 0 ? learned : defCap)); // > 0 even on a link under 2 Mb/s
                if (kbps > netCap) { capped = $", capped to network {netCap}{(learned > 0 ? " (learned)" : "")}"; kbps = netCap; }
                int maxKbps = kbps;
                // ~20 ms of video: little hidden queue. Wi-Fi acknowledges in bursts (aggregation, TV power save):
                // with 20 ms the socket blocked 4-10 % of the time at 40 Mb/s on a 245-438 Mb/s link, read as
                // saturation. 60 ms there: the queue only fills when the link really stalls.
                client.SendBufferSize = Math.Max(64 << 10, kbps * 1000 / 8 / (net == "wifi" ? 16 : 50));
                Log.W($"Plan: {target.Width}x{target.Height} {plan} {kbps} kb/s (budget {budget:0}, ideal {ideal:0}{capped})");
                Stats.Width = target.Width; Stats.Height = target.Height; Stats.Codec = enc.Codec.ToUpper();
                Stats.Encoder = plan.ToString(); Stats.TargetFps = plan.Fps; Stats.TargetKbps = kbps;
                Stats.Since = DateTime.Now; Stats.Streaming = true;
                StartPing();

                // A/V sync: how much later than the sound the image leaves the PC (ms). NVENC in fluid mode holds
                // 2 frames (-delay 2); encoding ~8 ms; +15 ms because loopback captures the sound when Windows mixes it,
                // ahead of the PC's own speakers that the player (browser…) syncs its video to.
                // avsync=0 in game mode: the TV never delays the image for the sound (lowest image latency first).
                // mode= tells the TV which mode is active (its up/down keys switch it).
                string Features()
                {
                    int vpc = (plan.Enc.Native ? 0 : plan.Enc.Name.Contains("nvenc") ? (Settings.GameMode ? 0 : 2) : 1) * 1000 / plan.Fps + 8 + 15;
                    Stats.PcVideoMs = vpc;
                    return $";audio={(tvAudio ? 1 : 0)};pads={(pads != null ? 1 : 0)};vpc={vpc};apc=5;avsync={(Settings.GameMode ? 0 : 1)};mode={(Settings.GameMode ? "game" : "fluid")}" +
                           (tvStamp ? ";ts=1;lpc=15" : "") + (tvCursor ? ";cursor=1" : "") +
                           (remote != null ? ";remote=1" : "") + (secureUp ? ";sec=" + (secureDown ? 3 : 1) : "");
                }
                Send(0x20, ConfigPacket());
                string info = $"{target.Width}x{target.Height} {plan.Fps} {L.T("fps")} {enc.Codec.ToUpper()} {kbps / 1000} Mb/s";
                status(L.T("streaming", tv, info));
                notify("TVScreenPlus", L.T("tv_connected", tv, info));
                streaming = true;
                SetAudio(Settings.Audio);
                if (tvCursor)
                {
                    // The TV draws the cursor: positions over UDP (or TCP), shapes over TCP; not drawn in the video.
                    var ep = audioUdp;
                    cursor = new CursorSender(target,
                        p => { if (closed) return; try { if (ep != null) SendUdp(Udp(), p, ep); else Send(0x08, p); } catch { } },
                        p => { if (closed) return; try { Send(0x08, p); } catch { } });
                }

                // ---- 4. Stream with congestion control ----
                // Encoder changes (bitrate, mode) are seamless: the new ffmpeg starts while the current one keeps
                // streaming and takes over at its first frame (a keyframe). Restarting in place froze the image for
                // ~1 s each time (measured 926-963 ms on the TV), which the learned ceiling would make frequent.
                int quickFails = 0, congested = 0, healthy = 0, lastUnderruns = 0;
                var window = Stopwatch.StartNew();
                var cleanSince = Stopwatch.StartNew();
                Stopwatch lastCongestion = null;
                long blockedTicks = 0, bytes = 0;
                int frames = 0;
                var genLock = new object();
                int genCounter = 0, activeGen = 0, nextGen = 0;
                Streamer activeSt = null, nextSt = null;
                bool configOnSwitch = false;
                bool configOnKey = false;        // send the config right before the next keyframe (live mode change)
                DateTime activeStarted = DateTime.UtcNow;

                byte[] ConfigPacket() => Encoding.ASCII.GetBytes(
                    $"codec={plan.Enc.Codec};w={target.Width};h={target.Height};fps={plan.Fps};kbps={kbps};enc={plan.Enc.Name}" + Features());

                void SendFrame(byte[] buf, int o, int l, uint cap)
                {
                    if (tvStamp)
                    {
                        // [len+4][flags|0x04][u32 capture ms][payload]: the TV measures capture -> screen.
                        int plen = l - 5;
                        var h = new byte[9];
                        h[0] = (byte)((plen + 4) >> 24); h[1] = (byte)((plen + 4) >> 16); h[2] = (byte)((plen + 4) >> 8); h[3] = (byte)(plen + 4);
                        h[4] = (byte)(buf[o + 4] | 0x04);
                        h[5] = (byte)(cap >> 24); h[6] = (byte)(cap >> 16); h[7] = (byte)(cap >> 8); h[8] = (byte)cap;
                        lock (sendLock) { ns.Write(h, 0, 9); ns.Write(buf, o + 5, plen); }
                    }
                    else lock (sendLock) ns.Write(buf, o, l);
                }

                // Starts an encoder that will replace the current one at its first keyframe.
                void StartNext(int k, bool sendConfig)
                {
                    Streamer st, superseded; int gen;
                    lock (genLock)
                    {
                        superseded = nextSt;
                        gen = ++genCounter; nextGen = gen;
                        st = nextSt = streamerNext = new Streamer();
                        if (sendConfig) configOnSwitch = true;
                    }
                    if (superseded != null) try { superseded.Dispose(); } catch { } // a newer change wins
                    new Thread(() =>
                    {
                        try { st.Run(plan, target, k, (b, o, l, c) => Deliver(gen, b, o, l, c), cursor == null); }
                        catch (IOException) { Close(); }               // TV gone
                        catch (ObjectDisposedException) { }
                        catch (Exception e) { Log.W("encoder: " + e.Message); }
                        finally { st.Done = true; wake.Set(); }
                    }) { IsBackground = true, Name = "encoder " + gen }.Start();
                }

                void Deliver(int gen, byte[] buf, int o, int l, uint cap)
                {
                    Streamer replaced = null;
                    lock (genLock)
                    {
                        if (gen == nextGen && gen != activeGen)
                        {
                            if ((buf[o + 4] & 1) == 0) return;          // wait for its first keyframe
                            replaced = activeSt;
                            activeSt = nextSt; nextSt = null; activeGen = gen; activeStarted = DateTime.UtcNow;
                            streamer = activeSt; streamerNext = null;
                            if (configOnSwitch) { configOnSwitch = false; Send(0x20, ConfigPacket()); }
                        }
                        else if (gen != activeGen) return;               // last frames of the replaced encoder
                        // The TV rebuilds its decoder on a config: it must come right before a keyframe.
                        if (configOnKey && (buf[o + 4] & 1) != 0) { configOnKey = false; Send(0x20, ConfigPacket()); }
                        // Sent under the lock: a frame of the old encoder can never follow the new one's keyframe.
                        long t0 = Stopwatch.GetTimestamp();
                        SendFrame(buf, o, l, cap);
                        long took = Stopwatch.GetTimestamp() - t0;
                        blockedTicks += took;
                        // One send stuck for 300 ms = the link is saturated now: don't wait for two bad seconds.
                        if (took > Stopwatch.Frequency * 3 / 10) congested = Math.Max(congested, 1);
                    }
                    if (replaced != null) ThreadPool.QueueUserWorkItem(_ => { try { replaced.Dispose(); } catch { } });
                    Stats.AddPcCapture(Wrap31((long)NowMs() - cap));
                    frames++; bytes += l;
                    if (window.ElapsedMilliseconds < 1000) return;
                    // >25 % of the time blocked on the socket = network can't keep up.
                    double secs = window.Elapsed.TotalSeconds;
                    double blocked = blockedTicks / (double)Stopwatch.Frequency / secs;
                    Stats.SentFps = frames / secs; Stats.SentKbps = bytes * 8 / secs / 1000; Stats.Blocked = blocked;
                    frames = 0; bytes = 0;
                    blockedTicks = 0; window.Restart();
                    congested = blocked > 0.25 ? congested + 1 : 0;
                    // Healthy second: socket rarely waits, steady pings, no frame lost and no sound cut on the TV.
                    // Not the sound packets' jitter: Wi-Fi delivers in bursts (64-75 ms measured, 5 GHz Wi-Fi 6, no cut),
                    // so a Wi-Fi ceiling never rose again and each hiccup lowered it for good (50 -> 30 Mb/s, blurrier).
                    bool soundOk = Stats.TvAudioUnderruns <= lastUnderruns;
                    lastUnderruns = Stats.TvAudioUnderruns;
                    // Ethernet keeps the strict test of 1.1.2 (untested otherwise: a 100 Mb/s TV port saturates at
                    // 77 Mb/s, 43 % blocked, so a looser test there could cycle up/saturate/down).
                    bool good = net == "wifi"
                        ? blocked < 0.10 && Stats.Jitter < 20 && soundOk && Stats.TvDropsDelta == 0
                        : blocked < 0.03 && Stats.Jitter < 8 && (Stats.TvAudioJitter < 0 || Stats.TvAudioJitter <= 20) && Stats.TvDropsDelta == 0;
                    // A single busy second (frequent on Wi-Fi) costs 5 s of credit instead of the whole run.
                    healthy = good ? healthy + 1 : net == "wifi" ? Math.Max(0, healthy - 5) : 0;
                    if (blocked > 0.1) cleanSince.Restart();
                    bool change = false;
                    bool repeated = lastCongestion != null && lastCongestion.Elapsed.TotalMinutes < 5;
                    if (congested >= 2 && kbps > ideal * 0.35 && net == "wifi" && !repeated)
                    {
                        // Wi-Fi: every isolated "congestion" measured (6 in 3 h, at 30-139 Mb/s) was the TV's own
                        // Wi-Fi scan (Google Play services, every 30 min, ~2 s off channel): not a capacity limit,
                        // and a lower bitrate doesn't help. Keep bitrate and ceiling; a real saturation repeats
                        // within seconds and is handled below.
                        Log.W($"Isolated Wi-Fi hiccup ({blocked:P0} blocked): bitrate and ceiling kept at {kbps} kb/s");
                        lastCongestion = Stopwatch.StartNew();
                        congested = 0; healthy = 0;
                    }
                    else if (congested >= 2 && kbps > ideal * 0.35)
                    {
                        kbps = (int)Math.Max(ideal * 0.35, kbps * 0.7);
                        Log.W($"Congestion ({blocked:P0} blocked) -> {kbps} kb/s");
                        lastCongestion = Stopwatch.StartNew();
                        // This link can't take the ceiling: remember a lower one for next time too.
                        maxKbps = (int)Math.Max(ideal * 0.35, maxKbps * 0.85);
                        Settings.Set(capKey, maxKbps.ToString());
                        Log.W($"Learned ceiling lowered to {maxKbps} kb/s ({capKey})");
                        healthy = 0; congested = 0;
                        Stats.BitrateDown++;
                        change = true;
                    }
                    else if (kbps < maxKbps && cleanSince.Elapsed.TotalSeconds > 45)
                    {
                        kbps = Math.Min(maxKbps, (int)(kbps * 1.25));
                        Log.W($"Network OK -> {kbps} kb/s");
                        cleanSince.Restart();
                        change = true;
                    }
                    else if (kbps >= maxKbps && maxKbps < Math.Min(desired, hardCap) && healthy >= 30)
                    {
                        // ~30 s without sign of saturation while the ceiling limits the quality:
                        // try 15 % more, and remember it.
                        maxKbps = Math.Min(Math.Min(desired, hardCap), (int)(maxKbps * 1.15));
                        kbps = maxKbps;
                        Settings.Set(capKey, maxKbps.ToString());
                        Log.W($"Network healthy for 30 s -> ceiling raised to {kbps} kb/s ({capKey})");
                        healthy = 0; cleanSince.Restart();
                        change = true;
                    }
                    if (change)
                    {
                        Stats.TargetKbps = kbps;
                        status(L.T("streaming", tv, $"{target.Width}x{target.Height} {kbps / 1000} Mb/s"));
                        // In-house encoder: new bitrate applied live, no new encoder (no keyframe, no switch).
                        Streamer live;
                        lock (genLock) live = nextSt == null ? activeSt : null;
                        if (live == null || !live.SetBitrate(kbps)) StartNext(kbps, false);
                    }
                }

                StartNext(kbps, false);
                while (!closed)
                {
                    wake.WaitOne(500);
                    if (closed) break;
                    if (modeSwitch)
                    {
                        modeSwitch = false;
                        Stats.Encoder = plan.ToString();
                        Log.W("Mode switched to " + (Settings.GameMode ? "game (minimum latency)" : "fluid (steady 60 fps)"));
                        // In-house encoder: applied live. A second encoder could not start anyway: Desktop
                        // Duplication refuses a second duplication of the same screen (E_INVALIDARG).
                        Streamer live;
                        lock (genLock) live = nextSt == null ? activeSt : null;
                        if (live != null && live.SetMode(Settings.GameMode))
                        {
                            lock (genLock) configOnKey = true;
                            live.ForceKeyFrame();
                        }
                        else StartNext(kbps, true); // the TV gets the new config right before the new encoder's keyframe
                        continue;
                    }
                    Streamer cur, nxt;
                    lock (genLock) { cur = activeSt; nxt = nextSt; }
                    if (nxt != null && nxt.Done)
                    {
                        // The replacement died before its first frame: keep the current encoder.
                        lock (genLock) { if (nextSt == nxt) { nextSt = null; nextGen = activeGen; streamerNext = null; } }
                        Log.W("New encoder ended before its first frame");
                        quickFails++;
                        if (cur != null && !cur.Done) continue;
                        nxt = null;
                    }
                    if (nxt != null || (cur != null && !cur.Done)) continue;
                    // No encoder running (it ended: display change, secure desktop, driver reset…): start another.
                    if (cur != null) quickFails = (DateTime.UtcNow - activeStarted).TotalSeconds < 3 ? quickFails + 1 : 0;
                    if (quickFails >= 3)
                    {
                        Log.W("Encoder keeps failing, re-probing");
                        Streamer.Forget(target);
                        target = Dxgi.Find(target.GdiName) ?? VirtualDisplay.Attach(target.Width, target.Height, plan.Fps);
                        tvDevice = target.GdiName;
                        var e2 = Streamer.Pick(target, hevcTv, tvCursor && Settings.NativeEncoder);
                        plan = new EncodePlan { Enc = e2, Quality = false, Fps = plan.Fps };
                        quickFails = 0;
                        lock (genLock) { activeSt = null; activeGen = 0; }
                        StartNext(kbps, true);
                    }
                    else
                    {
                        lock (genLock) { activeSt = null; activeGen = 0; }
                        Thread.Sleep(300);
                        StartNext(kbps, false);
                    }
                }
            }
            catch (Exception e)
            {
                Log.W("Session ended: " + e.Message);
                if (!closed) notify("TVScreenPlus", L.T("error", e.Message));
            }
            finally
            {
                Close();
                try { early?.Wait(5000); } catch { } // a display still being prepared must not appear after Detach
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
            Send(0x40, PingPayload());
            if (!ack.WaitOne(3000)) throw new Exception(L.T("tv_no_answer"));
            rttMs = sw.Elapsed.TotalMilliseconds;
            var filler = new byte[256 * 1024];
            long sent = 0;
            sw.Restart();
            while (sw.ElapsedMilliseconds < 1000 && sent < (96L << 20)) { Send(0x80, filler); sent += filler.Length + 5; }
            Send(0x40, PingPayload());
            if (!ack.WaitOne(10000)) throw new Exception(L.T("net_too_slow"));
            double secs = Math.Max(0.05, sw.Elapsed.TotalSeconds - rttMs / 2000);
            return Math.Min(800, sent * 8 / secs / 1e6);
        }

        /// <summary>
        /// Round-trip time every second (the ping waits behind queued video: real frame latency),
        /// and a summary line in the log every 10 s so a session can be analysed afterwards.
        /// </summary>
        void StartPing()
        {
            new Thread(() =>
            {
                var sw = new Stopwatch();
                var logEvery = Stopwatch.StartNew();
                while (!closed)
                {
                    try
                    {
                        sw.Restart();
                        Send(0x40, PingPayload());
                        if (ack.WaitOne(3000)) Stats.AddRtt(sw.Elapsed.TotalMilliseconds);
                        else Stats.AddRtt(3000);
                    }
                    catch { return; }
                    if (logEvery.ElapsedMilliseconds >= 10000) { logEvery.Restart(); Log.W(Stats.LogLine()); }
                    Thread.Sleep(1000);
                }
            }) { IsBackground = true, Name = "ping" }.Start();
        }

        /// <summary>Upstream messages from the TV; also detects disconnection.</summary>
        void StartReader()
        {
            new Thread(() =>
            {
                try
                {
                    // Buffered reads: gamepad lines can come hundreds of times per second (writes still go to ns).
                    var input = new BufferedStream(ns, 4096);
                    while (!closed)
                    {
                        string l = ReadLine(input, true);
                        if (l == null) break;
                        if (l == "ACK") ack.Set();
                        else if (l.StartsWith("PAD")) pads?.OnLine(l);
                        else if (l.StartsWith("STAT"))
                        {
                            var d = Parse(l);
                            if (d.ContainsKey("e2e")) Stats.OnTvLatency(Int(d, "e2e", -1), Int(d, "e2emax", -1));
                            Stats.OnTvStats(Int(d, "fps", 0), Int(d, "kbps", 0), Int(d, "drop", 0),
                                            Int(d, "dec", -1), Int(d, "decmax", -1), Int(d, "wait", -1));
                            // Frames dropped before decoding break the following ones until the next keyframe:
                            // ask for one at once (in-house encoder; ffmpeg only makes one when restarted).
                            if (Stats.TvDropsDelta > 0 && streamer?.ForceKeyFrame() == true) Log.W($"TV dropped {Stats.TvDropsDelta} frames -> keyframe");
                            if (d.ContainsKey("alat")) Stats.OnTvAudio(Int(d, "alat", -1), Int(d, "aund", 0), Int(d, "adrop", 0), Int(d, "ajit", -1));
                            if (d.ContainsKey("vdel")) Stats.OnTvSync(Int(d, "aout", -1), Int(d, "abuf", -1), Int(d, "aext", 0), Int(d, "vdel", 0), Int(d, "avoff", 0));
                        }
                        else if (l.StartsWith("LOG;")) TvLog(l.Substring(4));
                        else if (RemoteLine(l))
                        {
                            // remote is only created over an encrypted upstream; the tray switch applies at once.
                            var r = remote;
                            if (r != null && secureUp && Settings.RemoteControl) r.OnLine(l);
                        }
                        else if (l.StartsWith("MODE;"))
                        {
                            // Up/down keys of the TV remote: fluid <-> game mode (also updates the tray menu).
                            bool game = l.Substring(5).Trim() == "game";
                            Log.W("TV asks for " + (game ? "game" : "fluid") + " mode");
                            if (game != Settings.GameMode) { Settings.GameMode = game; SwitchMode(); }
                        }
                    }
                }
                catch { }
                Close();
            }) { IsBackground = true, Name = "tv-reader" }.Start();
        }

        static readonly string[] RemoteCommands = { "MOUSE;", "BTN;", "WHEEL;", "KEY;", "TEXT;", "WINS?", "WIN;", "APPS?", "APP;", "FAV;" };
        static bool RemoteLine(string l) => RemoteCommands.Any(l.StartsWith);

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

        /// <summary>The TV's first line, as sent (the auth2 answer covers these exact bytes).</summary>
        static byte[] ReadHello(Stream s)
        {
            var bytes = new List<byte>();
            int c;
            while ((c = s.ReadByte()) >= 0 && c != '\n')
                if (bytes.Count < 8192) bytes.Add((byte)c); else throw new IOException("hello too long");
            if (bytes.Count > 0 && bytes[bytes.Count - 1] == '\r') bytes.RemoveAt(bytes.Count - 1);
            return bytes.ToArray();
        }

        static string ReadLine(Stream s, bool nullOnEof = false)
        {
            var bytes = new List<byte>();
            int c;
            int total = 0;
            while ((c = s.ReadByte()) >= 0 && c != '\n')
            {
                if (bytes.Count < 1024) bytes.Add((byte)c);
                if (++total > 1 << 20) throw new IOException("line too long");
            }
            if (c < 0 && nullOnEof) return null;
            return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
        }
    }

    /// <summary>Live numbers shown by the debug window.</summary>
    sealed class SessionStats
    {
        public string Tv = "", Network = "?", Codec = "", Encoder = "";
        public int Width, Height, TargetFps, TargetKbps, TvLatencyMs = -1, BitrateDown;
        public double MeasuredMbps, RttMs, SentFps, SentKbps, Blocked;
        public int TvFps, TvKbps, TvDrops, TvDropsDelta;
        /// <summary>Live decoder latency (avg / max over 2 s, ms) and % of time the decoder input was full; -1 = old TV app.</summary>
        public int TvDecMs = -1, TvDecMaxMs = -1, TvWaitPct = -1;
        public bool Streaming;
        /// <summary>PC sound and gamepads, as shown in the debug window.</summary>
        public volatile string Audio = "—", Pads = "—";
        /// <summary>What travels encrypted on this connection (debug window).</summary>
        public volatile string Crypt = "—";
        /// <summary>Last TV seen (model, its settings, its encryption speed), kept after it disconnects: "Copy for a report".</summary>
        public static volatile string LastTvDevice, LastTvSettings, LastTvCrypto;
        public double AudioKbps;
        public int AudioSkipped, TvAudioMs = -1, TvAudioUnderruns, TvAudioDrops, TvAudioJitter = -1;
        public DateTime Since = DateTime.Now;
        readonly Queue<double> rtts = new Queue<double>();
        int loggedDrops;

        public void AddRtt(double ms)
        {
            lock (rtts) { rtts.Enqueue(ms); while (rtts.Count > 20) rtts.Dequeue(); RttMs = ms; }
        }

        public void OnTvStats(int fps, int kbps, int drops, int decMs, int decMaxMs, int waitPct)
        {
            TvFps = fps; TvKbps = kbps; TvDropsDelta = Math.Max(0, drops - TvDrops); TvDrops = drops;
            TvDecMs = decMs; TvDecMaxMs = decMaxMs; TvWaitPct = waitPct;
        }

        /// <summary>TV audio buffer (ms), underruns, packets dropped to keep latency low, arrival jitter (ms).</summary>
        public void OnTvAudio(int queuedMs, int underruns, int drops, int jitterMs)
        {
            TvAudioMs = queuedMs; TvAudioUnderruns = underruns; TvAudioDrops = drops; TvAudioJitter = jitterMs;
        }

        /// <summary>A/V sync, from the TV: sound write -> speaker delay, jitter buffer, delay added to the sound,
        /// delay added to the image, user correction (ms). PcVideoMs = image delay estimated on the PC.</summary>
        public int TvAudioOutMs = -1, TvAudioBufMs = -1, TvAudioExtraMs, TvVideoDelayMs, TvAvOffsetMs, PcVideoMs = -1;

        /// <summary>Measured capture -> screen latency from the TV (avg / max over 2 s, ms; TV picture processing not included),
        /// and capture -> send on the PC (ms, smoothed).</summary>
        public int TvE2eMs = -1, TvE2eMaxMs = -1;
        public double PcCaptureMs = -1;
        public void OnTvLatency(int avg, int max) { TvE2eMs = avg; TvE2eMaxMs = max; }
        public void AddPcCapture(long ms)
        {
            if (ms < 0 || ms > 2000) return;
            PcCaptureMs = PcCaptureMs < 0 ? ms : PcCaptureMs * 0.95 + ms * 0.05;
        }

        public void OnTvSync(int outMs, int bufMs, int extraMs, int videoDelayMs, int offsetMs)
        {
            TvAudioOutMs = outMs; TvAudioBufMs = bufMs; TvAudioExtraMs = extraMs; TvVideoDelayMs = videoDelayMs; TvAvOffsetMs = offsetMs;
        }

        string AudioLine => Audio + (AudioKbps > 0 ? FormattableString.Invariant($" · {AudioKbps / 1000:0.0} Mb/s") : "") +
            (TvAudioMs >= 0 ? "\r\n" + Sub("st_tv_side") + L.T("st_tv_side_v", TvAudioMs, TvAudioUnderruns, TvAudioDrops) +
                              (TvAudioJitter >= 0 ? L.T("st_jitter_v", TvAudioJitter) : "") : "") +
            (TvAudioOutMs >= 0 ? "\r\n" + Sub("st_avsync") + L.T("st_avsync_v", TvAudioOutMs, TvVideoDelayMs, TvAudioExtraMs, TvAvOffsetMs) : "");

        /// <summary>Debug window columns: labels padded to 22 characters, indented ones to 2 + 20.</summary>
        static string Top(string key) => L.Col(key, 22);
        static string Sub(string key) => "  " + L.Col(key, 20);

        /// <summary>Decoder latency used for estimates: live measurement first, then the TV hardware test.</summary>
        public int DecodeMs => TvDecMs >= 0 ? TvDecMs : TvLatencyMs;

        /// <summary>One-line summary for the log (every 10 s while streaming).</summary>
        public string LogLine()
        {
            int drops = TvDrops - loggedDrops; loggedDrops = TvDrops;
            string dec = TvDecMs >= 0 ? $"dec {TvDecMs} ms (max {TvDecMaxMs}) wait {TvWaitPct}%" : "dec ?";
            return string.Join(" | ",
                FormattableString.Invariant($"Stats: sent {SentFps:0} fps {SentKbps / 1000:0.0} Mb/s (target {TargetKbps / 1000.0:0.0})"),
                FormattableString.Invariant($"TV {TvFps} fps {TvKbps / 1000.0:0.0} Mb/s drops +{Math.Max(0, drops)} {dec}"),
                FormattableString.Invariant($"blocked {Blocked * 100:0}% RTT {RttMs:0.0} ms jitter {Jitter:0.0}"),
                FormattableString.Invariant($"stability {Stability} | ~{EndToEndMs:0} ms PC->screen"),
                FormattableString.Invariant($"audio {AudioKbps / 1000:0.0} Mb/s TV buffer {TvAudioMs} ms (target {TvAudioBufMs}) underruns {TvAudioUnderruns} drops {TvAudioDrops} jitter {TvAudioJitter} ms PC skips {AudioSkipped}"),
                FormattableString.Invariant($"latency: capture->screen {TvE2eMs} ms (max {TvE2eMaxMs}), PC capture->send {PcCaptureMs:0} ms"),
                FormattableString.Invariant($"sync: sound out {TvAudioOutMs} ms, image delayed {TvVideoDelayMs} ms, sound delayed {TvAudioExtraMs} ms, user {TvAvOffsetMs} ms, PC image {PcVideoMs} ms"));
        }

        public double Jitter
        {
            get
            {
                lock (rtts)
                {
                    if (rtts.Count < 2) return 0;
                    double avg = rtts.Average();
                    return Math.Sqrt(rtts.Sum(r => (r - avg) * (r - avg)) / rtts.Count);
                }
            }
        }

        /// <summary>0-100: socket saturation, latency jitter, frames lost on the TV, TV frame rate.</summary>
        public int Stability
        {
            get
            {
                double sc = 100 - Blocked * 150 - Math.Min(40, Jitter * 1.5) - Math.Min(30, TvDropsDelta * 5);
                if (TvFps > 0 && TvFps < TargetFps * 0.9) sc -= 20;
                return (int)Math.Max(0, Math.Min(100, sc));
            }
        }

        /// <summary>Measured by the TV when it can; else capture + encode (~1 frame) + network (RTT/2) + TV decode + display (~1 frame).</summary>
        public double EndToEndMs
        {
            get
            {
                if (TvE2eMs >= 0) return TvE2eMs;
                double frame = 1000.0 / Math.Max(1, TargetFps);
                return frame + 4 + RttMs / 2 + (DecodeMs >= 0 ? DecodeMs : frame) + frame + TvVideoDelayMs;
            }
        }

        public string Describe()
        {
            int st = Stability;
            string q = L.T(st >= 85 ? "q_excellent" : st >= 65 ? "q_good" : st >= 40 ? "q_average" : "q_weak");
            var up = DateTime.Now - Since;
            return
                Top("st_tv") + Tv + "\r\n" +
                Top("st_network") + L.T("st_network_v", Network, MeasuredMbps) + "\r\n" +
                "\r\n" +
                Top("st_stability") + $"{st} % ({q})\r\n" +
                Sub("st_blocked") + $"{Blocked:P0}\r\n" +
                Sub("st_jitter") + $"{Jitter:0.0} ms\r\n" +
                Sub("st_drops") + $"{TvDrops} (+{TvDropsDelta})\r\n" +
                (TvWaitPct >= 0 ? Sub("st_decwait") + L.T("st_decwait_v", TvWaitPct) + "\r\n" : "") +
                Sub("st_bitdown") + BitrateDown + "\r\n" +
                "\r\n" +
                Top("st_fps") + L.T("st_fps_v", SentFps, TvFps, TargetFps) + "\r\n" +
                Top("resolution") + $"{Width} x {Height} · {Codec}\r\n" +
                Top("st_encoder") + Encoder + "\r\n" +
                Top("st_bitrate") + L.T("st_bitrate_v", SentKbps / 1000, TargetKbps / 1000.0) + "\r\n" +
                "\r\n" +
                Top("st_rtt") + $"{RttMs:0.0} ms\r\n" +
                Top("st_dec") + (TvDecMs >= 0 ? L.T("st_dec_live", TvDecMs, TvDecMaxMs) : TvLatencyMs >= 0 ? L.T("st_dec_test", TvLatencyMs) : L.T("st_dec_unknown")) + "\r\n" +
                (TvE2eMs >= 0 ? Top("st_e2e") + L.T("st_e2e_v", TvE2eMs, TvE2eMaxMs, PcCaptureMs) + "\r\n"
                              : Top("st_est") + L.T("st_est_v", EndToEndMs) + "\r\n") +
                "\r\n" +
                Top("sound_tv") + AudioLine + "\r\n" +
                Top("st_pads") + Pads + "\r\n" +
                Top("st_crypt") + Crypt + "\r\n" +
                "\r\n" +
                Top("st_since") + $"{(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}";
        }
    }
}
