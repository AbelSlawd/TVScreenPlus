using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TVScreenPlus
{
    /// <summary>Colours and helpers shared by the tray menu and the windows: the dark blue look of the TV app.</summary>
    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(16, 28, 44);
        public static readonly Color Surface = Color.FromArgb(28, 46, 70);
        public static readonly Color SurfaceHover = Color.FromArgb(38, 62, 94);
        public static readonly Color Accent = Color.FromArgb(30, 136, 229);
        public static readonly Color AccentHover = Color.FromArgb(66, 157, 236);
        public static readonly Color AccentLight = Color.FromArgb(144, 202, 249);
        public static readonly Color Text = Color.FromArgb(236, 242, 248);
        public static readonly Color Muted = Color.FromArgb(150, 168, 190);
        public static readonly Color Line = Color.FromArgb(44, 64, 90);

        /// <summary>Marks a menu item drawn as an on/off switch.</summary>
        public static readonly object Toggle = new object();

        static readonly string IconFont = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }.FirstOrDefault(Installed) ?? "Segoe UI Symbol";

        static bool Installed(string family)
        {
            try { using (var f = new FontFamily(family)) return true; } catch (ArgumentException) { return false; }
        }

        /// <summary>A Windows icon glyph (Segoe Fluent Icons / MDL2 Assets code point) as a small bitmap.</summary>
        public static Bitmap Glyph(char code, Color color, int size = 16)
        {
            var b = new Bitmap(size, size);
            using (var g = Graphics.FromImage(b))
            using (var f = new Font(IconFont, size * 0.72f, GraphicsUnit.Pixel))
            using (var br = new SolidBrush(color))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(code.ToString(), f, br, new RectangleF(0, 0, size, size), sf);
            }
            return b;
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Windows 11: rounded corners and a dark title bar (ignored on older systems).</summary>
        public static void Window(IntPtr hwnd, bool darkTitle)
        {
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, 4);
                if (darkTitle) { int on = 1; DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, 4); }
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }

    /// <summary>Dark tray menu: accent highlight, section titles, on/off switches, accent marks for the choices.</summary>
    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Back;
            public override Color ImageMarginGradientBegin => Theme.Back;
            public override Color ImageMarginGradientMiddle => Theme.Back;
            public override Color ImageMarginGradientEnd => Theme.Back;
            public override Color MenuBorder => Theme.Line;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color SeparatorDark => Theme.Line;
            public override Color SeparatorLight => Theme.Back;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using (var p = Theme.Rounded(r, 5)) using (var b = new SolidBrush(Theme.SurfaceHover)) g.FillPath(b, p);
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 5, r.Y + 5, 3, r.Height - 10); // accent bar
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Disabled items are the header, the status and the section titles: drawn in their own colours
            // (the base renderer would grey them).
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, e.Item.Enabled ? Theme.Text : e.Item.ForeColor, e.TextFormat);
            if (ReferenceEquals(e.Item.Tag, Theme.Toggle) && e.Item is ToolStripMenuItem mi) DrawSwitch(e.Graphics, e.Item, mi.Checked);
        }

        static void DrawSwitch(Graphics g, ToolStripItem item, bool on)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(item.Width - 50, (item.Height - 18) / 2f, 34, 18);
            using (var p = Theme.Rounded(r, 9))
            {
                if (on) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
                else using (var pen = new Pen(Theme.Muted, 1.4f)) g.DrawPath(pen, p);
            }
            float k = on ? r.Right - 15 : r.X + 3;
            using (var b = new SolidBrush(on ? Color.White : Theme.Muted)) g.FillEllipse(b, k, r.Y + 3, 12, 12);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            if (ReferenceEquals(e.Item.Tag, Theme.Toggle)) return; // shown by the switch
            // Choices in the sub-menus: an accent dot.
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, r.X + r.Width / 2f - 5, r.Y + r.Height / 2f - 5, 10, 10);
        }

        /// <summary>Icons as they are: no box behind checked items, no grey for the header.</summary>
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image != null) e.Graphics.DrawImage(e.Image, e.ImageRectangle);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Muted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 12, y, e.Item.Width - 12, y);
        }
    }
}
