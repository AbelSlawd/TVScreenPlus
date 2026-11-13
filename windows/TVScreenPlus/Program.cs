using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
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

        /// <summary>Machine version 1.1.&lt;CI run&gt;: always increases, compared by the auto-updater.</summary>
        public static readonly Version Version = typeof(Program).Assembly.GetName().Version;
        /// <summary>Version shown to people (VERSION file, e.g. "1.1.0").</summary>
        public static readonly string DisplayVersion = DisplayOf(typeof(Program).Assembly);
        static string DisplayOf(System.Reflection.Assembly a)
        {
            var v = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(a, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            string s = v?.InformationalVersion?.Split('+')[0];
            return string.IsNullOrEmpty(s) ? Version.ToString(3) : s;
        }
        public const string QuitEvent = "Global\\TVScreenPlus_quit";

        [STAThread]
        static void Main(string[] args)
        {
            Directory.CreateDirectory(DataDir);
            LockDown(DataDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--uninstall")) { Installer.Uninstall(); return; }
            if (args.Length > 0 && args[0] == "--bench-encoder") { EncoderBench.Run(args); return; }
            try { if (!Installer.EnsureInstalled()) return; }
            catch (Exception e) { Log.W("Install failed: " + e); MessageBox.Show("Installation impossible : " + e.Message, "TVScreenPlus"); return; }

            using (var mtx = new Mutex(false, "Global\\TVScreenPlus_single"))
            {
                // A previous instance may still be shutting down (update/reinstall): wait for it.
                try { if (!mtx.WaitOne(15000)) return; } catch (AbandonedMutexException) { }
                Log.Init();
                Log.W("TVScreenPlus " + DisplayVersion + " (" + Version + ") in " + Dir);
                Application.Run(new TrayApp());
            }
        }

        /// <summary>
        /// ProgramData lets every user create files in our folder, which an elevated process reads: only
        /// administrators and SYSTEM may write there; users may still read (the log). settings.txt, which holds the
        /// TV pairing keys, is closed to users too (see Settings).
        /// </summary>
        static void LockDown(string dir)
        {
            try
            {
                var sec = new DirectorySecurity();
                sec.SetOwner(Admins);
                sec.SetAccessRuleProtection(true, false);
                var all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                foreach (var sid in new[] { Admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                    sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(dir, sec);
            }
            catch (Exception e) { Log.W("Data folder permissions: " + e.Message); }
        }

        static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        /// <summary>Administrators and SYSTEM only, nothing inherited.</summary>
        public static void AdminsOnly(string file)
        {
            try
            {
                var sec = new FileSecurity();
                sec.SetOwner(Admins);
                sec.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { Admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                    sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(file, sec);
            }
            catch (Exception e) { Log.W("Settings permissions: " + e.Message); }
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
        static int writes;
        public static void W(string s)
        {
            lock (L) try
            {
                // Also checked while running: the TV's LOG lines must not fill the disk during a long session.
                if (++writes % 1000 == 0 && new FileInfo(FilePath).Length > (8 << 20)) File.WriteAllText(FilePath, "");
                File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n");
            }
            catch { }
        }
    }

    /// <summary>Simple persistent key=value settings (administrators only: they hold the TV pairing keys).</summary>
    static class Settings
    {
        static readonly string F = Path.Combine(Program.DataDir, "settings.txt");
        static readonly Dictionary<string, string> D = new Dictionary<string, string>();
        static bool restricted;
        static Settings()
        {
            try { foreach (var l in File.ReadAllLines(F)) { int i = l.IndexOf('='); if (i > 0) D[l.Substring(0, i)] = l.Substring(i + 1); } } catch { }
            if (File.Exists(F)) { Program.AdminsOnly(F); restricted = true; }
        }
        public static string Get(string k, string def) { lock (D) return D.TryGetValue(k, out var v) ? v : def; }
        public static List<string> Keys(string prefix) { lock (D) return D.Keys.Where(k => k.StartsWith(prefix)).ToList(); }
        public static void Set(string k, string v)
        {
            // One "key=value" per line: a line break in a value would forge another setting.
            if (k.IndexOfAny(new[] { '=', '\r', '\n' }) >= 0 || (v != null && v.IndexOfAny(new[] { '\r', '\n' }) >= 0))
                throw new ArgumentException("invalid setting " + k);
            lock (D)
            {
                if (v == null) D.Remove(k); else D[k] = v;
                try { File.WriteAllLines(F, D.Select(p => p.Key + "=" + p.Value)); } catch { }
                if (!restricted) { Program.AdminsOnly(F); restricted = true; } // created just now: it inherited the folder's read access
            }
        }
        /// <summary>Target vertical resolution: -1 = automatic, 0 = TV native, else 1080 / 1440 / 2160.</summary>
        public static int Res { get => int.TryParse(Get("resolution", ""), out int v) ? v : -1; set => Set("resolution", value.ToString()); }
        /// <summary>Bitrate: 0 = automatic (measured network), else multiplier 0.6 / 1 / 2.</summary>
        public static double Quality
        {
            get => double.TryParse(Get("bitrate", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
            set => Set("bitrate", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        public static bool AutoUpdate { get => Get("autoupdate", "1") == "1"; set => Set("autoupdate", value ? "1" : "0"); }
        /// <summary>Forget encoder probes, benchmarks and learned network ceilings (new GPU/driver/network).</summary>
        public static void ResetCalibration()
        {
            lock (D) foreach (var k in D.Keys.Where(k => k.StartsWith("enc_") || k.StartsWith("bench_") || k.StartsWith("netcap_")).ToList()) D.Remove(k);
            Set("_", null);
        }
        public static bool AutoStart { get => Get("autostart", "1") == "1"; set => Set("autostart", value ? "1" : "0"); }
        /// <summary>Game mode: minimum latency (synchronous encoding) at the cost of a few lost frames; off = fluid 60 fps.</summary>
        public static bool GameMode
        {
            get => Get("gamemode", "0") == "1";
            set { if (GameMode == value) return; Set("gamemode", value ? "1" : "0"); GameModeChanged?.Invoke(); }
        }
        /// <summary>Raised when the mode changes (tray menu or TV remote).</summary>
        public static event Action GameModeChanged;
        /// <summary>In-house encoder (Desktop Duplication + Media Foundation) instead of ffmpeg.</summary>
        public static bool NativeEncoder { get => Get("encoder", "native") == "native"; set => Set("encoder", value ? "native" : "ffmpeg"); }
        /// <summary>PC sound played by the TV while streaming.</summary>
        public static bool Audio { get => Get("audio", "1") == "1"; set => Set("audio", value ? "1" : "0"); }
        /// <summary>Mute the PC's own speakers while the TV plays the sound.</summary>
        public static bool MutePc { get => Get("mutepc", "0") == "1"; set => Set("mutepc", value ? "1" : "0"); }
        /// <summary>Gamepads connected to the TV appear on the PC as Xbox 360 controllers.</summary>
        public static bool Pads { get => Get("pads", "1") == "1"; set => Set("pads", value ? "1" : "0"); }
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
            ToolStripManager.Renderer = new MenuRenderer();
            var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9.75f), ShowCheckMargin = false, ShowImageMargin = true };
            menu.Opened += (s, e) => Theme.Window(menu.Handle, false);
            menu.Items.Add(new ToolStripMenuItem("TVScreenPlus   " + Program.DisplayVersion, Theme.Glyph('', Theme.Accent, 20))
            {
                Enabled = false, ForeColor = Theme.Text, Font = new Font("Segoe UI Semibold", 11f), Padding = new Padding(0, 6, 0, 0)
            });
            status = new ToolStripMenuItem("Démarrage…") { Enabled = false, ForeColor = Theme.AccentLight, Padding = new Padding(0, 0, 0, 4) };
            menu.Items.Add(status);
            menu.Items.Add(Item("Redémarrer la connexion", '', () => Task.Run(RestartConnection)));

            menu.Items.Add(Section("IMAGE"));
            var res = Submenu("Résolution", '');
            foreach (var (label, v) in new[] { ("Automatique (recommandé)", -1), ("1080p", 1080), ("1440p", 1440), ("4K", 2160), ("Native de la TV", 0) })
                res.DropDownItems.Add(Radio(label, () => Settings.Res == v, () => Settings.Res = v));
            menu.Items.Add(res);

            var q = Submenu("Qualité / débit", '');
            foreach (var (label, v) in new[] { ("Automatique selon le réseau (recommandé)", 0.0), ("Wi-Fi faible", 0.6), ("Haute", 1.0), ("Maximale (Ethernet / Wi-Fi 6)", 2.0) })
                q.DropDownItems.Add(Radio(label, () => Math.Abs(Settings.Quality - v) < 0.01, () => Settings.Quality = v));
            menu.Items.Add(q);

            var game = Switch("Mode jeu (latence minimale)", '', Settings.GameMode);
            game.CheckedChanged += (s, e) =>
            {
                if (Settings.GameMode == game.Checked) return; // changed from the TV remote: already applied
                Settings.GameMode = game.Checked;
                Log.W("Game mode " + (game.Checked ? "on" : "off"));
                // Applied to the running stream at once (encoder restart, the TV stays connected).
                if (server != null && server.Busy) server.SwitchMode();
                else Notify("TVScreenPlus", game.Checked ? "Mode jeu activé : latence minimale, quelques images peuvent être sautées."
                                                         : "Mode fluide : 60 images/s régulières, latence légèrement plus élevée.");
            };
            // The TV remote (up / down) can switch the mode too: keep the tick in sync.
            Settings.GameModeChanged += () => ui.Post(_ => game.Checked = Settings.GameMode, null);
            menu.Items.Add(game);
            var encoder = Submenu("Encodeur vidéo", '');
            foreach (var (label, v) in new[] { ("TVScreenPlus (latence minimale, 60 i/s)", true), ("ffmpeg (version 1.0, secours)", false) })
                encoder.DropDownItems.Add(Radio(label, () => Settings.NativeEncoder == v, () =>
                {
                    if (Settings.NativeEncoder == v) return;
                    Settings.NativeEncoder = v;
                    Log.W("Encoder: " + (v ? "in-house" : "ffmpeg"));
                    if (server != null && server.Busy) Task.Run(RestartConnection); // applied by reconnecting
                }));
            menu.Items.Add(encoder);

            menu.Items.Add(Section("SON ET MANETTES"));
            var sound = Switch("Son sur la TV", '', Settings.Audio);
            sound.CheckedChanged += (s, e) => { Settings.Audio = sound.Checked; server?.SetAudio(sound.Checked); };
            menu.Items.Add(sound);
            var mute = Switch("Couper le son du PC pendant la diffusion", '', Settings.MutePc);
            mute.CheckedChanged += (s, e) => Settings.MutePc = mute.Checked;
            menu.Items.Add(mute);
            var pads = Switch("Manettes de la TV (à la prochaine connexion)", '', Settings.Pads);
            pads.CheckedChanged += (s, e) => Settings.Pads = pads.Checked;
            menu.Items.Add(pads);

            menu.Items.Add(Section("APPLICATION"));
            var auto = Switch("Lancer au démarrage de Windows", '', Settings.AutoStart);
            auto.CheckedChanged += (s, e) => { Settings.AutoStart = auto.Checked; Setup.SetAutoStart(auto.Checked); };
            menu.Items.Add(auto);
            var upd = Switch("Mises à jour automatiques", '', Settings.AutoUpdate);
            upd.CheckedChanged += (s, e) => Settings.AutoUpdate = upd.Checked;
            menu.Items.Add(upd);
            menu.Items.Add(Item("Rechercher une mise à jour", '', () => Task.Run(() => CheckUpdate(true))));
            menu.Items.Add(Item("Recalibrer (nouveau matériel)", '', () => { Settings.ResetCalibration(); Notify("TVScreenPlus", "Le calibrage sera refait à la prochaine connexion."); }));
            menu.Items.Add(Item("Oublier les TV autorisées", '', () =>
            {
                if (MessageBox.Show($"Oublier les {Pairing.Count} TV autorisées ? Chacune devra être acceptée à nouveau sur ce PC.",
                                    "TVScreenPlus", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Pairing.ForgetAll();
                Log.W("Paired TVs forgotten");
                Task.Run(RestartConnection); // the connected TV, if any, must pair again too
            }));
            menu.Items.Add(Item("Mode debug (statistiques en direct)", '', () => DebugWindow.Toggle(() => server)));
            menu.Items.Add(Item("Ouvrir le journal", '', () => Process.Start("notepad.exe", Log.FilePath)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Quitter", '', Quit));

            icon = new NotifyIcon { Icon = MakeIcon(), Text = "TVScreenPlus", ContextMenuStrip = menu, Visible = true };
            // A newer copy being installed asks us to quit.
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEvent);
            ThreadPool.RegisterWaitForSingleObject(quit, (s, t) => ui.Post(_ => Quit(), null), null, -1, true);
            Task.Run(Start);
            updateTimer = new System.Threading.Timer(_ => CheckUpdate(false), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(Updater.TestChannel ? 1 : 30));
        }

        readonly System.Threading.Timer updateTimer;
        DateTime lastCheck = DateTime.MinValue;
        int updating;

        /// <summary>Checks GitHub every 6 h (or on demand) and installs when no TV is connected.</summary>
        void CheckUpdate(bool manual)
        {
            // Test channel: every 5 min (GitHub allows 60 anonymous API calls per hour per address), and installed
            // even while streaming (the TV reconnects by itself).
            var every = Updater.TestChannel ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(6);
            if (!manual && (!Settings.AutoUpdate || DateTime.UtcNow - lastCheck < every)) return;
            if (Interlocked.Exchange(ref updating, 1) == 1) return;
            try
            {
                lastCheck = DateTime.UtcNow;
                var latest = Updater.Latest();
                Log.W("Latest release: " + (latest?.Item1?.ToString() ?? "?"));
                if (latest == null || !Updater.Newer(latest.Item1))
                {
                    if (manual) Notify("TVScreenPlus", "Vous avez la dernière version (" + Program.DisplayVersion + ").");
                    return;
                }
                if (server != null && server.Busy && !manual && !Updater.TestChannel)
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

        static readonly Padding ItemPadding = new Padding(0, 4, 0, 4);

        static ToolStripMenuItem Item(string text, char glyph, Action onClick)
        {
            var it = new ToolStripMenuItem(text, Theme.Glyph(glyph, Theme.AccentLight)) { Padding = ItemPadding };
            it.Click += (s, e) => onClick();
            return it;
        }

        /// <summary>On/off setting, drawn with a switch on the right (the spaces reserve its room).</summary>
        static ToolStripMenuItem Switch(string text, char glyph, bool on) =>
            new ToolStripMenuItem(text, Theme.Glyph(glyph, Theme.AccentLight))
            {
                Checked = on, CheckOnClick = true, Tag = Theme.Toggle, Padding = ItemPadding,
                ShortcutKeyDisplayString = new string(' ', 10), ShowShortcutKeys = true,
            };

        static ToolStripMenuItem Submenu(string text, char glyph)
        {
            var it = new ToolStripMenuItem(text, Theme.Glyph(glyph, Theme.AccentLight)) { Padding = ItemPadding };
            var dd = (ToolStripDropDownMenu)it.DropDown;
            dd.ShowImageMargin = false; dd.ShowCheckMargin = true;
            dd.Font = new Font("Segoe UI", 9.75f);
            dd.Opened += (s, e) => Theme.Window(dd.Handle, false);
            return it;
        }

        static ToolStripMenuItem Section(string title) => new ToolStripMenuItem(title)
        {
            Enabled = false, ForeColor = Theme.Accent, Font = new Font("Segoe UI Semibold", 8.25f), Padding = new Padding(0, 8, 0, 0),
        };

        static ToolStripMenuItem Radio(string label, Func<bool> isOn, Action set)
        {
            var it = new ToolStripMenuItem(label) { Tag = isOn, Checked = isOn(), Padding = ItemPadding };
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
                AudioLoopback.RestoreMuteAfterCrash();
                Installer.Register();
                if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "TVScreenPlus.lnk"))) Installer.Shortcut();
                Task.Delay(15000).ContinueWith(_ => Installer.CleanupTemp());
                server = new Server(SetStatus, Notify, ApproveTv);
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

        readonly SemaphoreSlim pairing = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Session thread: asks the person at the PC whether a new TV may show this PC's screen. One request at a
        /// time, "no" after 60 s without an answer.
        /// </summary>
        bool ApproveTv(string tv, string ip, string code)
        {
            if (!pairing.Wait(0)) return false;
            try
            {
                var answer = new TaskCompletionSource<bool>();
                ui.Post(_ => PairDialog.Ask(tv, ip, code, answer), null);
                Notify("TVScreenPlus", $"Nouvelle TV : « {tv} » demande l'accès à ce PC.");
                return answer.Task.Wait(TimeSpan.FromSeconds(65)) && answer.Task.Result;
            }
            finally { pairing.Release(); }
        }

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

    /// <summary>
    /// First-run setup: virtual display driver, firewall, autostart. ffmpeg, the driver and the gamepad driver setup
    /// come in TVScreenPlus-PC.zip (installed in Program Files, writable by administrators only): nothing is
    /// downloaded and run unchecked.
    /// </summary>
    static class Setup
    {
        public static string FFmpeg => Path.Combine(Program.Dir, "ffmpeg.exe");

        public static void EnsureAll()
        {
            RemoveOldDownloads();
            if (!File.Exists(FFmpeg)) Log.W("ffmpeg.exe missing: only the in-house encoder is available");
            if (!VirtualDisplay.IsInstalled())
            {
                string inf = FindInf(Program.Dir) ?? throw new Exception("Pilote d'écran virtuel introuvable : réinstallez TVScreenPlus depuis TVScreenPlus-PC.zip");
                VirtualDisplay.Install(inf);
            }
            Firewall();
            SetAutoStart(Settings.AutoStart);
        }

        static string FindInf(string root)
        {
            try { return Directory.GetFiles(Path.Combine(root, "driver"), "MttVDD.inf").FirstOrDefault(); } catch { return null; }
        }

        /// <summary>Versions before 1.2 could download ffmpeg / drivers into the data folder: never run those copies.</summary>
        static void RemoveOldDownloads()
        {
            foreach (var f in new[] { "ffmpeg.exe", GamepadDriver.SetupName }) try { File.Delete(Path.Combine(Program.DataDir, f)); } catch { }
            try { Directory.Delete(Path.Combine(Program.DataDir, "driver"), true); } catch { }
        }

        /// <summary>Only our two ports, only for this program, only from the local subnet (whatever the network profile).</summary>
        static void Firewall()
        {
            string version = Program.Dir + "|2";
            if (Settings.Get("fw", "") == version) return;
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            Run("netsh", "advfirewall firewall delete rule name=TVScreenPlus");
            foreach (var (proto, port) in new[] { ("UDP", Server.UdpPort), ("TCP", Server.TcpPort) })
                Run("netsh", $"advfirewall firewall add rule name=TVScreenPlus dir=in action=allow program=\"{exe}\" protocol={proto} localport={port} remoteip=localsubnet profile=any");
            Settings.Set("fw", version);
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
