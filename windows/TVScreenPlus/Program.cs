using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TVScreenPlus
{
    static class Program
    {
        public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TVScreenPlus");

        public static readonly Version Version = typeof(Program).Assembly.GetName().Version;
        public const string QuitEvent = "Global\\TVScreenPlus_quit";

        [STAThread]
        static void Main(string[] args)
        {
            Directory.CreateDirectory(DataDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--uninstall")) { Installer.Uninstall(); return; }
            try { if (!Installer.EnsureInstalled()) return; }
            catch (Exception e) { Log.W("Install failed: " + e); MessageBox.Show("Installation impossible : " + e.Message, "TVScreenPlus"); return; }

            using (var mtx = new Mutex(false, "Global\\TVScreenPlus_single"))
            {
                // A previous instance may still be shutting down (update/reinstall): wait for it.
                try { if (!mtx.WaitOne(15000)) return; } catch (AbandonedMutexException) { }
                Log.Init();
                Log.W("TVScreenPlus " + Version + " in " + Dir);
                Application.Run(new TrayApp());
            }
        }
    }

    static class Log
    {
        static readonly object L = new object();
        public static string FilePath = Path.Combine(Program.DataDir, "log.txt");
        public static void Init()
        {
            try { if (File.Exists(FilePath) && new FileInfo(FilePath).Length > (2 << 20)) File.WriteAllText(FilePath, ""); } catch { }
        }
        public static void W(string s)
        {
            lock (L) try { File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n"); } catch { }
        }
    }

    /// <summary>Simple persistent key=value settings.</summary>
    static class Settings
    {
        static readonly string F = Path.Combine(Program.DataDir, "settings.txt");
        static readonly Dictionary<string, string> D = new Dictionary<string, string>();
        static Settings()
        {
            try { foreach (var l in File.ReadAllLines(F)) { int i = l.IndexOf('='); if (i > 0) D[l.Substring(0, i)] = l.Substring(i + 1); } } catch { }
        }
        public static string Get(string k, string def) { lock (D) return D.TryGetValue(k, out var v) ? v : def; }
        public static void Set(string k, string v)
        {
            lock (D)
            {
                if (v == null) D.Remove(k); else D[k] = v;
                try { File.WriteAllLines(F, D.Select(p => p.Key + "=" + p.Value)); } catch { }
            }
        }
        /// <summary>Target vertical resolution: -1 = automatic, 0 = TV native, else 1080 / 1440 / 2160.</summary>
        public static int Res { get => int.Parse(Get("resolution", "-1")); set => Set("resolution", value.ToString()); }
        /// <summary>Bitrate: 0 = automatic (measured network), else multiplier 0.6 / 1 / 2.</summary>
        public static double Quality { get => double.Parse(Get("bitrate", "0"), System.Globalization.CultureInfo.InvariantCulture); set => Set("bitrate", value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        public static bool AutoUpdate { get => Get("autoupdate", "1") == "1"; set => Set("autoupdate", value ? "1" : "0"); }
        /// <summary>Forget encoder probes and benchmarks (new GPU/driver).</summary>
        public static void ResetCalibration()
        {
            lock (D) foreach (var k in D.Keys.Where(k => k.StartsWith("enc_") || k.StartsWith("bench_")).ToList()) D.Remove(k);
            Set("_", null);
        }
        public static bool AutoStart { get => Get("autostart", "1") == "1"; set => Set("autostart", value ? "1" : "0"); }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon icon;
        readonly ToolStripMenuItem status;
        readonly SynchronizationContext ui;
        Server server;

        public TrayApp()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            var menu = new ContextMenuStrip();
            status = new ToolStripMenuItem("Démarrage…") { Enabled = false };
            menu.Items.Add(status);
            menu.Items.Add("Redémarrer la connexion", null, (s, e) => Task.Run(RestartConnection));
            menu.Items.Add(new ToolStripSeparator());

            var res = new ToolStripMenuItem("Résolution");
            foreach (var (label, v) in new[] { ("Automatique (recommandé)", -1), ("1080p", 1080), ("1440p", 1440), ("4K", 2160), ("Native de la TV", 0) })
                res.DropDownItems.Add(Radio(label, () => Settings.Res == v, () => Settings.Res = v));
            menu.Items.Add(res);

            var q = new ToolStripMenuItem("Qualité / débit");
            foreach (var (label, v) in new[] { ("Automatique selon le réseau (recommandé)", 0.0), ("Wi-Fi faible", 0.6), ("Haute", 1.0), ("Maximale (Ethernet / Wi-Fi 6)", 2.0) })
                q.DropDownItems.Add(Radio(label, () => Math.Abs(Settings.Quality - v) < 0.01, () => Settings.Quality = v));
            menu.Items.Add(q);

            var auto = new ToolStripMenuItem("Lancer au démarrage de Windows") { Checked = Settings.AutoStart, CheckOnClick = true };
            auto.CheckedChanged += (s, e) => { Settings.AutoStart = auto.Checked; Setup.SetAutoStart(auto.Checked); };
            menu.Items.Add(auto);
            var upd = new ToolStripMenuItem("Mises à jour automatiques") { Checked = Settings.AutoUpdate, CheckOnClick = true };
            upd.CheckedChanged += (s, e) => Settings.AutoUpdate = upd.Checked;
            menu.Items.Add(upd);
            menu.Items.Add("Rechercher une mise à jour", null, (s, e) => Task.Run(() => CheckUpdate(true)));
            menu.Items.Add("Recalibrer (nouveau matériel)", null, (s, e) => { Settings.ResetCalibration(); Notify("TVScreenPlus", "Le calibrage sera refait à la prochaine connexion."); });
            menu.Items.Add("Mode debug (statistiques en direct)", null, (s, e) => DebugWindow.Toggle(() => server));
            menu.Items.Add("Ouvrir le journal", null, (s, e) => Process.Start("notepad.exe", Log.FilePath));
            menu.Items.Add(new ToolStripMenuItem("Version " + Program.Version.ToString(3)) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quitter", null, (s, e) => Quit());

            icon = new NotifyIcon { Icon = MakeIcon(), Text = "TVScreenPlus", ContextMenuStrip = menu, Visible = true };
            // A newer copy being installed asks us to quit.
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEvent);
            ThreadPool.RegisterWaitForSingleObject(quit, (s, t) => ui.Post(_ => Quit(), null), null, -1, true);
            Task.Run(Start);
            updateTimer = new System.Threading.Timer(_ => CheckUpdate(false), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
        }

        readonly System.Threading.Timer updateTimer;
        DateTime lastCheck = DateTime.MinValue;
        int updating;

        /// <summary>Checks GitHub every 6 h (or on demand) and installs when no TV is connected.</summary>
        void CheckUpdate(bool manual)
        {
            if (!manual && (!Settings.AutoUpdate || DateTime.UtcNow - lastCheck < TimeSpan.FromHours(6))) return;
            if (Interlocked.Exchange(ref updating, 1) == 1) return;
            try
            {
                lastCheck = DateTime.UtcNow;
                var latest = Updater.Latest();
                Log.W("Latest release: " + (latest?.Item1?.ToString() ?? "?"));
                if (latest == null || !Updater.Newer(latest.Item1))
                {
                    if (manual) Notify("TVScreenPlus", "Vous avez la dernière version (" + Program.Version.ToString(3) + ").");
                    return;
                }
                if (server != null && server.Busy && !manual)
                {
                    lastCheck = DateTime.MinValue; // retry at next tick, once the TV is disconnected
                    return;
                }
                Notify("TVScreenPlus", "Mise à jour vers la version " + latest.Item1.ToString(3) + "…");
                Updater.Install(latest.Item2);
            }
            catch (Exception e)
            {
                Log.W("Update check failed: " + e.Message);
                if (manual) Notify("TVScreenPlus", "Mise à jour impossible : " + e.Message);
            }
            finally { Interlocked.Exchange(ref updating, 0); }
        }

        static ToolStripMenuItem Radio(string label, Func<bool> isOn, Action set)
        {
            var it = new ToolStripMenuItem(label) { Tag = isOn, Checked = isOn() };
            it.Click += (s, e) =>
            {
                set();
                foreach (var x in it.Owner.Items.OfType<ToolStripMenuItem>()) x.Checked = ((Func<bool>)x.Tag)();
            };
            return it;
        }

        void Start()
        {
            try
            {
                SetStatus("Installation des composants…");
                Setup.EnsureAll();
                VirtualDisplay.Detach(); // in case a previous instance was killed while streaming
                Installer.Register();
                if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "TVScreenPlus.lnk"))) Installer.Shortcut();
                Task.Delay(15000).ContinueWith(_ => Installer.CleanupTemp());
                server = new Server(SetStatus, Notify);
                server.Start();
                SetStatus("Prêt – en attente d'une TV (" + Environment.MachineName + ")");
            }
            catch (Exception e)
            {
                Log.W("Startup failed: " + e);
                SetStatus("Erreur : " + e.Message);
                Notify("TVScreenPlus – erreur", e.Message);
            }
        }

        void RestartConnection()
        {
            try
            {
                bool hadTv = server != null && server.Busy;
                if (server == null) { Start(); return; }
                SetStatus("Redémarrage de la connexion…");
                server.Restart();
                SetStatus("Prêt – en attente d'une TV (" + Environment.MachineName + ")");
                Notify("TVScreenPlus", hadTv ? "Connexion redémarrée : la TV va se reconnecter automatiquement." : "Connexion redémarrée.");
            }
            catch (Exception e)
            {
                Log.W("Restart failed: " + e);
                SetStatus("Erreur : " + e.Message);
            }
        }

        public void SetStatus(string s)
        {
            Log.W("STATUS " + s);
            ui.Post(_ =>
            {
                status.Text = s;
                icon.Text = ("TVScreenPlus – " + s).Length > 63 ? ("TVScreenPlus – " + s).Substring(0, 63) : "TVScreenPlus – " + s;
            }, null);
        }

        public void Notify(string title, string text) => ui.Post(_ => icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info), null);

        void Quit()
        {
            try { server?.Stop(); } catch { }
            icon.Visible = false;
            ExitThread();
        }

        static Icon MakeIcon()
        {
            using (var b = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillRectangle(new SolidBrush(Color.FromArgb(30, 136, 229)), 2, 5, 28, 18);
                    g.FillRectangle(Brushes.White, 5, 8, 22, 12);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(30, 136, 229)), 12, 24, 8, 3);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(30, 136, 229)), 8, 27, 16, 2);
                }
                return Icon.FromHandle(b.GetHicon());
            }
        }
    }

    /// <summary>First-run setup: ffmpeg, virtual display driver, firewall, autostart.</summary>
    static class Setup
    {
        public static string FFmpeg => Path.Combine(Program.Dir, "ffmpeg.exe") is var p && File.Exists(p) ? p : Path.Combine(Program.DataDir, "ffmpeg.exe");
        const string FFmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        const string DriverUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip";

        public static void EnsureAll()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (!File.Exists(FFmpeg))
            {
                Log.W("Downloading ffmpeg");
                DownloadExtract(FFmpegUrl, e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase), Program.DataDir);
            }
            if (!VirtualDisplay.IsInstalled())
            {
                string inf = FindInf(Program.Dir) ?? FindInf(Program.DataDir);
                if (inf == null)
                {
                    Log.W("Downloading virtual display driver");
                    DownloadExtract(DriverUrl, e => e.Name.Length > 0,
                        Path.Combine(Program.DataDir, "driver"));
                    inf = FindInf(Program.DataDir);
                }
                if (inf == null) throw new Exception("Pilote d'écran virtuel introuvable");
                VirtualDisplay.Install(inf);
            }
            Firewall();
            SetAutoStart(Settings.AutoStart);
        }

        static string FindInf(string root)
        {
            try { return Directory.GetFiles(root, "MttVDD.inf", SearchOption.AllDirectories).FirstOrDefault(); } catch { return null; }
        }

        static void DownloadExtract(string url, Func<ZipArchiveEntry, bool> filter, string dest)
        {
            Directory.CreateDirectory(dest);
            string tmp = Path.Combine(Path.GetTempPath(), "tvsp_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var wc = new WebClient()) wc.DownloadFile(url, tmp);
                using (var z = ZipFile.OpenRead(tmp))
                    foreach (var e in z.Entries.Where(filter))
                        e.ExtractToFile(Path.Combine(dest, e.Name), true);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        static void Firewall()
        {
            if (Settings.Get("fw", "") == Program.Dir) return;
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            Run("netsh", "advfirewall firewall delete rule name=TVScreenPlus");
            Run("netsh", $"advfirewall firewall add rule name=TVScreenPlus dir=in action=allow program=\"{exe}\" enable=yes profile=any");
            Run("netsh", "advfirewall firewall add rule name=TVScreenPlus dir=in action=allow protocol=UDP localport=" + Server.UdpPort + " profile=any");
            Run("netsh", "advfirewall firewall add rule name=TVScreenPlus dir=in action=allow protocol=TCP localport=" + Server.TcpPort + " profile=any");
            Settings.Set("fw", Program.Dir);
        }

        public static void SetAutoStart(bool on)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            if (on) Run("schtasks", $"/Create /F /TN TVScreenPlus /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"\"");
            else Run("schtasks", "/Delete /F /TN TVScreenPlus");
        }

        public static int Run(string file, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false }))
                {
                    p.WaitForExit(30000);
                    return p.ExitCode;
                }
            }
            catch (Exception e) { Log.W(file + " failed: " + e.Message); return -1; }
        }
    }
}
