using System;
using System.Drawing;
using System.Windows.Forms;

namespace TVScreenPlus
{
    /// <summary>Debug mode: live session statistics (signal stability, fps, resolution, latency).</summary>
    sealed class DebugWindow : Form
    {
        static DebugWindow open;
        readonly Func<Server> server;
        readonly Label body;
        readonly Timer timer;

        public static void Toggle(Func<Server> server)
        {
            if (open != null && !open.IsDisposed) { open.Activate(); return; }
            open = new DebugWindow(server);
            open.Show();
        }

        DebugWindow(Func<Server> server)
        {
            this.server = server;
            Text = "TVScreenPlus – Mode debug";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(680, 560);
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            TopMost = true;
            BackColor = Color.FromArgb(18, 24, 32);

            var top = new CheckBox { Text = "Toujours au premier plan", Checked = true, ForeColor = Color.Silver, Dock = DockStyle.Bottom, Padding = new Padding(8, 0, 0, 4) };
            top.CheckedChanged += (s, e) => TopMost = top.Checked;
            var copy = new Button
            {
                Text = "Copier pour un signalement", Dock = DockStyle.Bottom, Height = 34, FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White, BackColor = Color.FromArgb(40, 52, 68), Cursor = Cursors.Hand
            };
            copy.FlatAppearance.BorderColor = Color.FromArgb(70, 90, 115);
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(Report()); copy.Text = "Copié ✓ : collez-le dans une issue GitHub"; }
                catch (Exception ex) { copy.Text = "Copie impossible : " + ex.Message; }
            };
            body = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, Padding = new Padding(12),
                Font = new Font("Consolas", 10.5f), AutoSize = false
            };
            Controls.Add(body);
            Controls.Add(top);
            Controls.Add(copy);

            timer = new Timer { Interval = 500 };
            timer.Tick += (s, e) => Refresh2();
            timer.Start();
            Refresh2();
            FormClosed += (s, e) => timer.Dispose();
        }

        /// <summary>Version, Windows, live statistics and the end of the log, ready to paste in a GitHub issue.</summary>
        string Report()
        {
            string tail;
            try
            {
                // The log is appended to while we read it: share it, and keep the end (an issue holds ~65,000 characters).
                using (var f = new System.IO.FileStream(Log.FilePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var r = new System.IO.StreamReader(f))
                {
                    var lines = r.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    tail = string.Join("\r\n", lines, Math.Max(0, lines.Length - 300), Math.Min(300, lines.Length));
                }
                if (tail.Length > 50000) tail = tail.Substring(tail.Length - 50000);
            }
            catch (Exception e) { tail = "(journal illisible : " + e.Message + ")"; }
            var st = server()?.Stats;
            string os = "Windows";
            try
            {
                // Environment.OSVersion says 6.2 to apps without a compatibility manifest: read the real build.
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build = int.TryParse(k?.GetValue("CurrentBuild") as string, out int b) ? b : 0;
                    os = (build >= 22000 ? "Windows 11" : "Windows 10") + " " + k?.GetValue("DisplayVersion") + " (build " + build + ")";
                }
            }
            catch { }
            return "**TVScreenPlus " + Program.DisplayVersion + "** · " + os + "\r\n\r\n" +
                   "```\r\n" + (st == null ? "Aucune TV connectée" : st.Describe()) + "\r\n```\r\n\r\n" +
                   "```\r\n" + Configuration() + "\r\n```\r\n\r\n" +
                   "<details><summary>Journal (300 dernières lignes)</summary>\r\n\r\n```\r\n" + tail.Trim() + "\r\n```\r\n</details>\r\n";
        }

        /// <summary>
        /// PC settings, hardware and the last TV's model and settings. Listed one by one: settings.txt also holds the
        /// pairing keys, which must never end up in a public issue.
        /// </summary>
        static string Configuration()
        {
            string On(bool b) => b ? "oui" : "non";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Réglages du PC");
            sb.AppendLine("  résolution          " + (Settings.Res < 0 ? "automatique" : Settings.Res == 0 ? "native de la TV" : Settings.Res + "p"));
            sb.AppendLine("  qualité / débit     " + (Settings.Quality <= 0 ? "automatique" : "x" + Settings.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sb.AppendLine("  mode                " + (Settings.GameMode ? "jeu" : "fluide"));
            sb.AppendLine("  encodeur            " + (Settings.NativeEncoder ? "TVScreenPlus" : "ffmpeg"));
            sb.AppendLine("  son sur la TV       " + On(Settings.Audio) + " · couper le son du PC " + On(Settings.MutePc));
            sb.AppendLine("  manettes            " + On(Settings.Pads));
            sb.AppendLine("  contrôle depuis TV  " + On(Settings.RemoteControl) + " · chiffrement " + Settings.Crypt);
            sb.AppendLine("  démarrage / màj     " + On(Settings.AutoStart) + " / " + On(Settings.AutoUpdate));
            sb.AppendLine("  TV jumelées         " + Pairing.Count);
            foreach (var k in Settings.Keys("netcap_")) sb.AppendLine("  plafond appris      " + k.Substring(7) + " = " + Settings.Get(k, "") + " kb/s");
            sb.AppendLine("Matériel");
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    sb.AppendLine("  processeur          " + (k?.GetValue("ProcessorNameString") as string ?? "?").Trim() + " · " + Environment.ProcessorCount + " threads");
            }
            catch { }
            if (GetPhysicallyInstalledSystemMemory(out long kb)) sb.AppendLine("  mémoire             " + (kb / 1024 / 1024) + " Go");
            try
            {
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    foreach (var n in cls?.GetSubKeyNames() ?? new string[0])
                    {
                        if (n.Length != 4 || !char.IsDigit(n[0])) continue;
                        using (var g = cls.OpenSubKey(n))
                            if (g?.GetValue("DriverDesc") is string desc)
                                sb.AppendLine("  carte graphique     " + desc + " · pilote " + g.GetValue("DriverVersion"));
                    }
            }
            catch { }
            foreach (var s in Screen.AllScreens)
                sb.AppendLine("  écran               " + s.DeviceName.TrimStart('\\', '.') + " " + s.Bounds.Width + "x" + s.Bounds.Height + (s.Primary ? " (principal)" : ""));
            sb.AppendLine("Dernière TV");
            sb.AppendLine("  modèle              " + (SessionStats.LastTvDevice ?? "aucune depuis le démarrage du PC"));
            if (SessionStats.LastTvSettings != null) sb.AppendLine("  réglages            " + SessionStats.LastTvSettings);
            if (SessionStats.LastTvCrypto != null) sb.AppendLine("  chiffrement         " + SessionStats.LastTvCrypto);
            return sb.ToString().TrimEnd();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool GetPhysicallyInstalledSystemMemory(out long kilobytes);

        void Refresh2()
        {
            var st = server()?.Stats;
            body.Text = st == null
                ? "Aucune TV connectée.\r\n\r\nLes statistiques apparaissent dès qu'une TV se connecte."
                : st.Describe();
            int stab = st?.Stability ?? 100;
            body.ForeColor = st == null ? Color.Silver : stab >= 65 ? Color.Gainsboro : stab >= 40 ? Color.Khaki : Color.LightCoral;
        }
    }
}
