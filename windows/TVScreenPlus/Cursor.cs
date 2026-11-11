using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TVScreenPlus
{
    sealed class CursorSender : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
            public int dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        static extern bool EnumDisplaySettingsEx(string dev, int mode, ref DEVMODE dm, int flags);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int cb, out BITMAP bm);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        readonly DisplayTarget target;
        readonly Action<byte[]> sendPosition, sendShape;
        readonly Thread thread;
        volatile bool stop;
        readonly HashSet<string> logged = new HashSet<string>();

        public CursorSender(DisplayTarget target, Action<byte[]> sendPosition, Action<byte[]> sendShape)
        {
            this.target = target; this.sendPosition = sendPosition; this.sendShape = sendShape;
            thread = new Thread(Loop) { IsBackground = true, Name = "cursor", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void Dispose()
        {
            stop = true;
            try { thread.Join(500); } catch { }
        }

        void LogOnce(Exception e)
        {
            string m = e.Message;
            if (logged.Add(m)) Log.W("cursor: " + m);
        }

        void Loop()
        {
            Log.W($"Cursor overlay: sending position/shape for {target.GdiName}");
            timeBeginPeriod(1);
            try
            {
                int rx = 0, ry = 0, rw = target.Width, rh = target.Height;
                long lastRefresh = long.MinValue, lastSend = long.MinValue;
                bool lastVis = false; int lastX = -1, lastY = -1;
                IntPtr lastCursor = IntPtr.Zero;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!stop)
                {
                    try
                    {
                        long now = sw.ElapsedMilliseconds;
                        if (lastRefresh == long.MinValue || now - lastRefresh >= 500)
                        {
                            lastRefresh = now;
                            var dm = new DEVMODE();
                            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                            if (EnumDisplaySettingsEx(target.GdiName, -1, ref dm, 0) && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                            { rx = dm.dmPositionX; ry = dm.dmPositionY; rw = dm.dmPelsWidth; rh = dm.dmPelsHeight; }
                        }
                        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
                        if (GetCursorInfo(ref ci))
                        {
                            int px = ci.ptScreenPos.x - rx, py = ci.ptScreenPos.y - ry;
                            bool vis = (ci.flags & 1) != 0 && px >= 0 && py >= 0 && px < rw && py < rh;
                            int x = 0, y = 0;
                            if (vis)
                            {
                                x = rw == target.Width ? px : (int)((long)px * target.Width / rw);
                                y = rh == target.Height ? py : (int)((long)py * target.Height / rh);
                                x = Math.Max(0, Math.Min(65535, x)); y = Math.Max(0, Math.Min(65535, y));
                            }
                            if (ci.hCursor != IntPtr.Zero && ci.hCursor != lastCursor)
                            {
                                lastCursor = ci.hCursor;
                                byte[] shape = null;
                                try { shape = BuildShape(ci.hCursor); } catch (Exception e) { LogOnce(e); }
                                if (shape != null) sendShape(shape);
                            }
                            if (vis != lastVis || x != lastX || y != lastY || lastSend == long.MinValue || now - lastSend >= 100)
                            {
                                lastVis = vis; lastX = x; lastY = y; lastSend = now;
                                long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0x7FFFFFFF;
                                var p = new byte[10];
                                p[0] = 3; p[1] = (byte)(vis ? 1 : 0);
                                p[2] = (byte)(x >> 8); p[3] = (byte)x; p[4] = (byte)(y >> 8); p[5] = (byte)y;
                                p[6] = (byte)(ms >> 24); p[7] = (byte)(ms >> 16); p[8] = (byte)(ms >> 8); p[9] = (byte)ms;
                                sendPosition(p);
                            }
                        }
                    }
                    catch (Exception e) { LogOnce(e); }
                    Thread.Sleep(4);
                }
            }
            finally { timeEndPeriod(1); }
        }

        static void Put16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }

        static byte[] BuildShape(IntPtr hCursor)
        {
            ICONINFO ii;
            if (!GetIconInfo(hCursor, out ii)) return null;
            int w, h;
            try
            {
                BITMAP bm;
                if (ii.hbmColor != IntPtr.Zero)
                {
                    if (GetObject(ii.hbmColor, Marshal.SizeOf(typeof(BITMAP)), out bm) == 0) return null;
                    w = bm.bmWidth; h = bm.bmHeight;
                }
                else
                {
                    if (ii.hbmMask == IntPtr.Zero || GetObject(ii.hbmMask, Marshal.SizeOf(typeof(BITMAP)), out bm) == 0) return null;
                    w = bm.bmWidth; h = bm.bmHeight / 2;
                }
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
            if (w <= 0 || h <= 0) return null;
            w = Math.Min(w, 256); h = Math.Min(h, 256);

            byte[] black = Render(hCursor, w, h, 0x00);
            byte[] white = Render(hCursor, w, h, 0xFF);
            var res = new byte[9 + w * h * 4];
            res[0] = 4;
            Put16(res, 1, w); Put16(res, 3, h);
            Put16(res, 5, Math.Max(0, Math.Min(w - 1, ii.xHotspot)));
            Put16(res, 7, Math.Max(0, Math.Min(h - 1, ii.yHotspot)));
            int n = w * h;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                int a = 0;
                for (int c = 0; c < 3; c++)
                {
                    int d = 255 - (white[o + c] - black[o + c]);
                    if (d > a) a = d;
                }
                if (a < 0) a = 0; if (a > 255) a = 255;
                int d0 = 9 + o;
                res[d0 + 3] = (byte)a;
                if (a > 0)
                    for (int c = 0; c < 3; c++)
                    {
                        int v = black[o + c] * 255 / a;
                        res[d0 + c] = (byte)(v > 255 ? 255 : v);
                    }
            }
            return res;
        }

        static byte[] Render(IntPtr hCursor, int w, int h, byte fill)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                dc = CreateCompatibleDC(screen);
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = w; bmi.bmiHeader.biHeight = -h;
                bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 32;
                IntPtr bits;
                dib = CreateDIBSection(dc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                old = SelectObject(dc, dib);
                int len = w * h * 4;
                var buf = new byte[len];
                for (int i = 0; i < len; i++) buf[i] = fill;
                Marshal.Copy(buf, 0, bits, len);
                DrawIconEx(dc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, 3);
                Marshal.Copy(bits, buf, 0, len);
                return buf;
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(dc, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (dc != IntPtr.Zero) DeleteDC(dc);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
