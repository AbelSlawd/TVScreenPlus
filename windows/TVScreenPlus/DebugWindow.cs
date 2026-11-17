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
            Text = L.T("dbg_title");
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(680, 560);
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            TopMost = true;
            BackColor = Color.FromArgb(18, 24, 32);

            var top = new CheckBox { Text = L.T("dbg_top"), Checked = true, ForeColor = Color.Silver, Dock = DockStyle.Bottom, Padding = new Padding(8, 0, 0, 4) };
            top.CheckedChanged += (s, e) => TopMost = top.Checked;
            var copy = new Button
            {
                Text = L.T("dbg_copy"), Dock = DockStyle.Bottom, Height = 34, FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White, BackColor = Color.FromArgb(40, 52, 68), Cursor = Cursors.Hand
            };
            copy.FlatAppearance.BorderColor = Color.FromArgb(70, 90, 115);
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(Report()); copy.Text = L.T("dbg_copied"); }
                catch (Exception ex) { copy.Text = L.T("dbg_copy_failed", ex.Message); }
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
            catch (Exception e) { tail = L.T("dbg_log_error", e.Message); }
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
                   "```\r\n" + (st == null ? L.T("dbg_no_tv") : st.Describe()) + "\r\n```\r\n\r\n" +
                   "```\r\n" + Configuration() + "\r\n```\r\n\r\n" +
                   "<details><summary>" + L.T("dbg_log") + "</summary>\r\n\r\n```\r\n" + tail.Trim() + "\r\n```\r\n</details>\r\n";
        }

        /// <summary>
        /// PC settings, hardware and the last TV's model and settings. Listed one by one: settings.txt also holds the
        /// pairing keys, which must never end up in a public issue.
        /// </summary>
        static string Configuration()
        {
            string On(bool b) => L.T(b ? "yes" : "no");
            string Row(string key) => "  " + L.Col(key, 20);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(L.T("cfg_pc"));
            sb.AppendLine(Row("cfg_res") + (Settings.Res < 0 ? L.T("automatic") : Settings.Res == 0 ? L.T("res_native") : Settings.Res + "p"));
            sb.AppendLine(Row("cfg_quality") + (Settings.Quality <= 0 ? L.T("automatic") : "x" + Settings.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("cfg_mode") + L.T(Settings.GameMode ? "m_game" : "m_fluid"));
            sb.AppendLine(Row("cfg_encoder") + (Settings.NativeEncoder ? "TVScreenPlus" : "ffmpeg"));
            sb.AppendLine(Row("cfg_sound") + On(Settings.Audio) + " · " + L.T("cfg_mute") + " " + On(Settings.MutePc));
            sb.AppendLine(Row("cfg_pads") + On(Settings.Pads));
            sb.AppendLine(Row("cfg_remote") + On(Settings.RemoteControl) + " · " + L.T("cfg_crypt") + " " + Settings.Crypt);
            sb.AppendLine(Row("cfg_start") + On(Settings.AutoStart) + " / " + On(Settings.AutoUpdate));
            sb.AppendLine(Row("cfg_paired") + Pairing.Count);
            foreach (var k in Settings.Keys("netcap_")) sb.AppendLine(Row("cfg_netcap") + k.Substring(7) + " = " + Settings.Get(k, "") + " kb/s");
            sb.AppendLine(L.T("cfg_hw"));
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    sb.AppendLine(Row("cfg_cpu") + (k?.GetValue("ProcessorNameString") as string ?? "?").Trim() + " · " + Environment.ProcessorCount + " threads");
            }
            catch { }
            if (GetPhysicallyInstalledSystemMemory(out long kb)) sb.AppendLine(Row("cfg_ram") + L.T("cfg_gb", kb / 1024 / 1024));
            try
            {
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    foreach (var n in cls?.GetSubKeyNames() ?? new string[0])
                    {
                        if (n.Length != 4 || !char.IsDigit(n[0])) continue;
                        using (var g = cls.OpenSubKey(n))
                            if (g?.GetValue("DriverDesc") is string desc)
                                sb.AppendLine(Row("cfg_gpu") + desc + " · " + L.T("cfg_driver") + " " + g.GetValue("DriverVersion"));
                    }
            }
            catch { }
            foreach (var s in Screen.AllScreens)
                sb.AppendLine(Row("cfg_screen") + s.DeviceName.TrimStart('\\', '.') + " " + s.Bounds.Width + "x" + s.Bounds.Height + (s.Primary ? " " + L.T("cfg_primary") : ""));
            sb.AppendLine(L.T("cfg_last_tv"));
            sb.AppendLine(Row("cfg_model") + (SessionStats.LastTvDevice ?? L.T("cfg_no_tv")));
            if (SessionStats.LastTvSettings != null) sb.AppendLine(Row("cfg_tv_settings") + SessionStats.LastTvSettings);
            if (SessionStats.LastTvCrypto != null) sb.AppendLine(Row("cfg_crypt") + SessionStats.LastTvCrypto);
            return sb.ToString().TrimEnd();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool GetPhysicallyInstalledSystemMemory(out long kilobytes);

        void Refresh2()
        {
            var st = server()?.Stats;
            body.Text = st == null
                ? L.T("dbg_no_tv") + ".\r\n\r\n" + L.T("dbg_wait")
                : st.Describe();
            int stab = st?.Stability ?? 100;
            body.ForeColor = st == null ? Color.Silver : stab >= 65 ? Color.Gainsboro : stab >= 40 ? Color.Khaki : Color.LightCoral;
        }
    }
}
