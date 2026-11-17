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
            // A settings.txt we did not write (created by a standard user before our first run, when the folder was
            // still open) could hold the key of a TV of theirs: it is not used.
            DropUnlessAdminOwned(Path.Combine(DataDir, "settings.txt"));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--uninstall")) { Installer.Uninstall(); return; }
            if (args.Length > 0 && args[0] == "--bench-encoder") { EncoderBench.Run(args); return; }
            try { if (!Installer.EnsureInstalled()) return; }
            catch (Exception e) { Log.W("Install failed: " + e); MessageBox.Show(L.T("install_failed", e.Message), "TVScreenPlus"); return; }

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
        public static void LockDown(string dir)
        {
            try
            {
                var sec = new DirectorySecurity();
                sec.SetOwner(Admins);
                sec.SetAccessRuleProtection(true, false);
                var all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                foreach (var sid in new[] { Admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                    sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                // Readers: users (the log) and the services that may read a folder of ours (display driver host).
                foreach (var t in new[] { WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.LocalServiceSid })
                    sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(t, null),
                        FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(dir, sec);
            }
            catch (Exception e) { Log.W("Data folder permissions: " + e.Message); }
        }

        static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        /// <summary>Administrators and SYSTEM only, nothing inherited.</summary>
        public static void AdminsOnly(string file)
        {
            try { File.SetAccessControl(file, AdminsOnlySecurity()); }
            catch (Exception e) { Log.W("Settings permissions: " + e.Message); }
        }

        /// <summary>Administrators and SYSTEM only (given at creation: no moment where a user could open the file).</summary>
        public static FileSecurity AdminsOnlySecurity()
        {
            var sec = new FileSecurity();
            sec.SetOwner(Admins);
            sec.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { Admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            return sec;
        }

        /// <summary>Deletes a file (or link) whose owner is neither the administrators nor SYSTEM: a user planted it.</summary>
        public static void DropUnlessAdminOwned(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                bool link = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
                var owner = File.GetAccessControl(path).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (!link && owner != null && (owner == Admins || owner.IsWellKnown(WellKnownSidType.LocalSystemSid))) return;
                Log.W($"Removed {path}: not created by TVScreenPlus (owner {owner}{(link ? ", link" : "")})");
                if (Directory.Exists(path)) Directory.Delete(path); else File.Delete(path);
            }
            catch (Exception e) { Log.W($"Check of {path}: {e.Message}"); }
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
                if (!restricted)
                {
                    // Created restricted: it would inherit the folder's read access with the keys in it.
                    try { if (!File.Exists(F)) using (new FileStream(F, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, Program.AdminsOnlySecurity())) { } } catch { }
                    Program.AdminsOnly(F); restricted = true;
                }
                try { File.WriteAllLines(F, D.Select(p => p.Key + "=" + p.Value)); } catch { }
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
        /// <summary>The TV may move the mouse, type, bring windows to its screen and launch applications.</summary>
        public static bool RemoteControl { get => Get("remote", "1") == "1"; set => Set("remote", value ? "1" : "0"); }
        /// <summary>Picture and sound encryption: "auto" (when the TV encrypts fast enough), "on" or "off".
        /// What the TV sends (gamepads, mouse, keyboard) is always encrypted.</summary>
        public static string Crypt
        {
            get { var v = Get("crypt", "auto"); return v == "on" || v == "off" ? v : "auto"; }
            set => Set("crypt", value);
        }
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
            status = new ToolStripMenuItem(L.T("starting")) { Enabled = false, ForeColor = Theme.AccentLight, Padding = new Padding(0, 0, 0, 4) };
            menu.Items.Add(status);
            menu.Items.Add(Item(L.T("restart_conn"), '', () => Task.Run(RestartConnection)));

            menu.Items.Add(Section(L.T("sec_image")));
            var res = Submenu(L.T("resolution"), '');
            foreach (var (label, v) in new[] { (L.T("auto_rec"), -1), ("1080p", 1080), ("1440p", 1440), ("4K", 2160), (L.T("res_native"), 0) })
                res.DropDownItems.Add(Radio(label, () => Settings.Res == v, () => Settings.Res = v));
            menu.Items.Add(res);

            var q = Submenu(L.T("quality"), '');
            foreach (var (label, v) in new[] { (L.T("q_auto"), 0.0), (L.T("q_low"), 0.6), (L.T("q_high"), 1.0), (L.T("q_max"), 2.0) })
                q.DropDownItems.Add(Radio(label, () => Math.Abs(Settings.Quality - v) < 0.01, () => Settings.Quality = v));
            menu.Items.Add(q);

            var game = Switch(L.T("game_mode"), '', Settings.GameMode);
            game.CheckedChanged += (s, e) =>
            {
                if (Settings.GameMode == game.Checked) return; // changed from the TV remote: already applied
                Settings.GameMode = game.Checked;
                Log.W("Game mode " + (game.Checked ? "on" : "off"));
                // Applied to the running stream at once (encoder restart, the TV stays connected).
                if (server != null && server.Busy) server.SwitchMode();
                else Notify("TVScreenPlus", L.T(game.Checked ? "game_on" : "game_off"));
            };
            // The TV remote (up / down) can switch the mode too: keep the tick in sync.
            Settings.GameModeChanged += () => ui.Post(_ => game.Checked = Settings.GameMode, null);
            menu.Items.Add(game);
            var encoder = Submenu(L.T("encoder"), '');
            foreach (var (label, v) in new[] { (L.T("enc_native"), true), (L.T("enc_ffmpeg"), false) })
                encoder.DropDownItems.Add(Radio(label, () => Settings.NativeEncoder == v, () =>
                {
                    if (Settings.NativeEncoder == v) return;
                    Settings.NativeEncoder = v;
                    Log.W("Encoder: " + (v ? "in-house" : "ffmpeg"));
                    if (server != null && server.Busy) Task.Run(RestartConnection); // applied by reconnecting
                }));
            menu.Items.Add(encoder);

            menu.Items.Add(Section(L.T("sec_sound")));
            var sound = Switch(L.T("sound_tv"), '', Settings.Audio);
            sound.CheckedChanged += (s, e) => { Settings.Audio = sound.Checked; server?.SetAudio(sound.Checked); };
            menu.Items.Add(sound);
            var mute = Switch(L.T("mute_pc"), '', Settings.MutePc);
            mute.CheckedChanged += (s, e) => Settings.MutePc = mute.Checked;
            menu.Items.Add(mute);
            var pads = Switch(L.T("pads_tv"), '', Settings.Pads);
            pads.CheckedChanged += (s, e) => Settings.Pads = pads.Checked;
            menu.Items.Add(pads);

            menu.Items.Add(Section(L.T("sec_control")));
            var remote = Switch(L.T("remote"), '', Settings.RemoteControl);
            remote.CheckedChanged += (s, e) => Settings.RemoteControl = remote.Checked;
            menu.Items.Add(remote);
            var crypt = Submenu(L.T("crypt"), '');
            foreach (var (label, v) in new[] { (L.T("crypt_auto"), "auto"), (L.T("crypt_on"), "on"), (L.T("crypt_off"), "off") })
                crypt.DropDownItems.Add(Radio(label, () => Settings.Crypt == v, () => Settings.Crypt = v));
            menu.Items.Add(crypt);

            menu.Items.Add(Section(L.T("sec_app")));
            var auto = Switch(L.T("autostart"), '', Settings.AutoStart);
            auto.CheckedChanged += (s, e) => { Settings.AutoStart = auto.Checked; Setup.SetAutoStart(auto.Checked); };
            menu.Items.Add(auto);
            var upd = Switch(L.T("autoupdate"), '', Settings.AutoUpdate);
            upd.CheckedChanged += (s, e) => Settings.AutoUpdate = upd.Checked;
            menu.Items.Add(upd);
            menu.Items.Add(Item(L.T("check_update"), '', UpdateClicked));
            menu.Items.Add(Item(L.T("recalibrate"), '', () => { Settings.ResetCalibration(); Notify("TVScreenPlus", L.T("recalibrated")); }));
            menu.Items.Add(Item(L.T("forget"), '', () =>
            {
                if (MessageBox.Show(L.T("forget_ask", Pairing.Count),
                                    "TVScreenPlus", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Pairing.ForgetAll();
                Log.W("Paired TVs forgotten");
                Task.Run(RestartConnection); // the connected TV, if any, must pair again too
            }));
            menu.Items.Add(Item(L.T("debug_mode"), '', () => DebugWindow.Toggle(() => server)));
            menu.Items.Add(Item(L.T("open_log"), '', () => Process.Start("notepad.exe", Log.FilePath)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item(L.T("quit"), '', Quit));

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

        readonly List<DateTime> updateClicks = new List<DateTime>();

        /// <summary>"Check for updates"; clicked 5 times within 10 s: offers the latest test version (hidden on purpose).</summary>
        void UpdateClicked()
        {
            var now = DateTime.UtcNow;
            updateClicks.Add(now);
            updateClicks.RemoveAll(t => now - t > TimeSpan.FromSeconds(10));
            if (updateClicks.Count >= 5) { updateClicks.Clear(); Task.Run(InstallTestVersion); }
            else Task.Run(() => CheckUpdate(true));
        }

        /// <summary>
        /// Installs the newest pre-release (test branch). A test version then follows the pre-releases by itself, and
        /// goes back to the official versions with the next release (the newest of all).
        /// </summary>
        void InstallTestVersion()
        {
            if (Interlocked.Exchange(ref updating, 1) == 1) return;
            try
            {
                var t = Updater.Latest(true);
                if (t == null || !Updater.Newer(t.Item1))
                {
                    Notify("TVScreenPlus", L.T("no_test", Program.DisplayVersion));
                    return;
                }
                if (MessageBox.Show(L.T("test_ask", t.Item1.ToString(3)), L.T("test_title"),
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                Log.W("Test version requested: " + t.Item1);
                Notify("TVScreenPlus", L.T("test_installing", t.Item1.ToString(3)));
                Updater.Install(t.Item2);
            }
            catch (Exception e)
            {
                Log.W("Test version: " + e.Message);
                Notify("TVScreenPlus", L.T("test_failed", e.Message));
            }
            finally { Interlocked.Exchange(ref updating, 0); }
        }

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
                    if (manual) Notify("TVScreenPlus", L.T("uptodate", Program.DisplayVersion));
                    return;
                }
                if (server != null && server.Busy && !manual && !Updater.TestChannel)
                {
                    lastCheck = DateTime.MinValue; // retry at next tick, once the TV is disconnected
                    return;
                }
                Notify("TVScreenPlus", L.T("updating", latest.Item1.ToString(3)));
                Updater.Install(latest.Item2);
            }
            catch (Exception e)
            {
                Log.W("Update check failed: " + e.Message);
                if (manual) Notify("TVScreenPlus", L.T("update_failed", e.Message));
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
                SetStatus(L.T("installing_parts"));
                Setup.EnsureAll();
                VirtualDisplay.Detach(); // in case a previous instance was killed while streaming
                AudioLoopback.RestoreMuteAfterCrash();
                Installer.Register();
                if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "TVScreenPlus.lnk"))) Installer.Shortcut();
                Task.Delay(15000).ContinueWith(_ => Installer.CleanupTemp());
                server = new Server(SetStatus, Notify, ApproveTv);
                server.Start();
                SetStatus(L.T("ready", Environment.MachineName));
            }
            catch (Exception e)
            {
                Log.W("Startup failed: " + e);
                SetStatus(L.T("error", e.Message));
                Notify(L.T("error_title"), e.Message);
            }
        }

        void RestartConnection()
        {
            try
            {
                bool hadTv = server != null && server.Busy;
                if (server == null) { Start(); return; }
                SetStatus(L.T("restarting"));
                server.Restart();
                SetStatus(L.T("ready", Environment.MachineName));
                Notify("TVScreenPlus", L.T(hadTv ? "restarted_tv" : "restarted"));
            }
            catch (Exception e)
            {
                Log.W("Restart failed: " + e);
                SetStatus(L.T("error", e.Message));
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
                Notify("TVScreenPlus", L.T("new_tv_notify", tv));
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
                string inf = FindInf(Program.Dir) ?? throw new Exception(L.T("vdd_missing"));
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
            bool ok = true;
            foreach (var (proto, port) in new[] { ("UDP", Server.UdpPort), ("TCP", Server.TcpPort) })
                ok &= Run("netsh", $"advfirewall firewall add rule name=TVScreenPlus dir=in action=allow program=\"{exe}\" protocol={proto} localport={port} remoteip=localsubnet profile=any") == 0;
            if (ok) Settings.Set("fw", version); // else tried again at the next start
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
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } Log.W(file + " timed out"); return -1; }
                    return p.ExitCode;
                }
            }
            catch (Exception e) { Log.W(file + " failed: " + e.Message); return -1; }
        }
    }
}
