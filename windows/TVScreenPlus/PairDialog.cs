using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TVScreenPlus
{
    /// <summary>
    /// "A new TV asks to show this PC": the code must match the one on the TV. Same look as the TV app; refused by
    /// itself after 60 s (the bar shows the time left).
    /// </summary>
    sealed class PairDialog : Form
    {
        const int Seconds = 60;
        readonly string code;
        readonly DateTime until = DateTime.UtcNow.AddSeconds(Seconds);
        readonly Label left;
        readonly Panel bar;
        bool accepted;

        public static void Ask(string tv, string ip, string code, TaskCompletionSource<bool> answer)
        {
            var d = new PairDialog(tv, ip, code);
            d.FormClosed += (s, e) => answer.TrySetResult(d.accepted);
            d.Show();
            d.Activate();
        }

        PairDialog(string tv, string ip, string code)
        {
            this.code = code;
            Text = "TVScreenPlus – nouvelle TV";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowIcon = false;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(500, 372);
            DoubleBuffered = true;

            Controls.Add(new PictureBox { Image = Theme.Glyph('', Theme.Accent, 40), Location = new Point(28, 26), Size = new Size(40, 40) });
            Controls.Add(new Label { Text = "Nouvelle TV", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(80, 22) });
            Controls.Add(new Label
            {
                Text = $"« {tv} » ({ip}) demande à afficher l'écran de ce PC.", ForeColor = Theme.Muted,
                AutoSize = false, Location = new Point(82, 56), Size = new Size(400, 40),
            });
            Controls.Add(new Label
            {
                Text = "Autorisez-la seulement si la TV affiche le même code :", AutoSize = false,
                Location = new Point(28, 112), Size = new Size(444, 24),
            });
            // The code itself is drawn in OnPaint: six digit boxes.
            left = new Label { ForeColor = Theme.Muted, Font = new Font("Segoe UI", 9f), AutoSize = false, Location = new Point(28, 262), Size = new Size(444, 20) };
            Controls.Add(left);
            var track = new Panel { BackColor = Theme.Surface, Location = new Point(28, 286), Size = new Size(444, 4) };
            bar = new Panel { BackColor = Theme.Accent, Location = new Point(0, 0), Size = track.Size };
            track.Controls.Add(bar);
            Controls.Add(track);

            var no = FlatButton("Refuser", Theme.Surface, Theme.SurfaceHover, new Point(232, 312));
            var yes = FlatButton("Autoriser", Theme.Accent, Theme.AccentHover, new Point(356, 312));
            no.Click += (s, e) => Close();
            yes.Click += (s, e) => { accepted = true; Close(); };
            Controls.Add(no);
            Controls.Add(yes);
            CancelButton = no; // Esc refuses; Enter alone does nothing (no default button)

            var tick = new Timer { Interval = 200 };
            tick.Tick += (s, e) =>
            {
                double rest = (until - DateTime.UtcNow).TotalSeconds;
                if (rest <= 0) { tick.Stop(); Close(); return; }
                bar.Width = (int)(track.Width * rest / Seconds);
                left.Text = $"Refus automatique dans {Math.Ceiling(rest):0} s";
            };
            tick.Start();
            FormClosed += (s, e) => tick.Dispose();
            HandleCreated += (s, e) => Theme.Window(Handle, true);
        }

        static Button FlatButton(string text, Color back, Color hover, Point at)
        {
            var b = new Button
            {
                Text = text, Location = at, Size = new Size(116, 40), FlatStyle = FlatStyle.Flat, BackColor = back,
                ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 10f), Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.FlatAppearance.MouseDownBackColor = hover;
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            const int w = 58, h = 76, gap = 10, groupGap = 22, top = 150;
            int x = (ClientSize.Width - (6 * w + 4 * gap + groupGap)) / 2;
            using (var font = new Font("Segoe UI Semibold", 30f))
            using (var box = new SolidBrush(Theme.Surface))
            using (var edge = new Pen(Theme.Line))
            using (var text = new SolidBrush(Color.White))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                for (int i = 0; i < 6; i++)
                {
                    var r = new RectangleF(x, top, w, h);
                    using (var p = Theme.Rounded(r, 10)) { g.FillPath(box, p); g.DrawPath(edge, p); }
                    g.DrawString(code[i].ToString(), font, text, r, sf);
                    x += w + (i == 2 ? groupGap : gap);
                }
            }
        }
    }
}
