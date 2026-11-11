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
            body = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, Padding = new Padding(12),
                Font = new Font("Consolas", 10.5f), AutoSize = false
            };
            Controls.Add(body);
            Controls.Add(top);

            timer = new Timer { Interval = 500 };
            timer.Tick += (s, e) => Refresh2();
            timer.Start();
            Refresh2();
            FormClosed += (s, e) => timer.Dispose();
        }

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
