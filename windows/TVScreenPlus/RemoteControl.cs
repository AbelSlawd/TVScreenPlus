using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TVScreenPlus
{
    /// <summary>
    /// The TV drives the PC (only over the encrypted, authenticated upstream, and only if Settings.RemoteControl):
    ///   "MOUSE;dx;dy" (pixels) | "MOUSE;center" (to the middle of the TV screen) | "BTN;l|r|m;1|0" | "WHEEL;notches"
    ///   "KEY;vk;1|0" | "TEXT;characters" (typed as Unicode)
    ///   "WINS?" -> extension [8]: open windows, Alt-Tab order, "id\tscreen\tstate\ttitle\tpng(base64)" per line
    ///             (screen: tv | main | 2 | 3…; state: n normal, m minimized, x maximized)
    ///   "WIN;id;tv|pc" -> that window moves to the TV screen (or the PC's main screen) and comes to the front
    ///   "APPS?" or "APPS?;1" -> extension [9]: applications of the Start menu, "id\tfavorite 0|1\tname\t" per line;
    ///             with ";1" (the TV has no icons yet), extension [12] "id\tpng" lines follow in small, spaced batches
    ///   "APP;id" launches one (without administrator rights), "FAV;id;1|0" sets a favorite (kept on the PC)
    /// Answers and messages for the user: extension [10] text. Only ids from the last list sent are accepted:
    /// the TV never names a file, a command or a window handle of its own.
    /// </summary>
    sealed class RemoteControl : IDisposable
    {
        readonly Action<byte, string> send;
        readonly Func<string> tvDevice;                  // GDI name of the TV's virtual display
        readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        readonly Thread worker;
        Dictionary<string, IntPtr> windows = new Dictionary<string, IntPtr>();
        Dictionary<string, App> apps = new Dictionary<string, App>();
        static readonly Dictionary<string, string> iconCache = new Dictionary<string, string>(); // app path -> png base64

        sealed class App { public string Id, Name, Launch, IconPath; }

        /// <summary>Icons extracted after the list is sent (seconds the first time): own thread, commands never wait for them.</summary>
        readonly BlockingCollection<Action> iconWork = new BlockingCollection<Action>();
        volatile int iconRun;   // a newer request (or the end of the session) stops the previous extraction

        public RemoteControl(Action<byte, string> send, Func<string> tvDevice)
        {
            this.send = send; this.tvDevice = tvDevice;
            // Shell objects (applications, icons) want a single-threaded apartment; the network thread stays free.
            worker = Sta(work, "remote");
            Sta(iconWork, "remote-icons");
        }

        static Thread Sta(BlockingCollection<Action> q, string name)
        {
            var t = new Thread(() =>
            {
                foreach (var a in q.GetConsumingEnumerable())
                    try { a(); } catch (Exception e) { Log.W("remote: " + e.Message); }
            }) { IsBackground = true, Name = name };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return t;
        }

        public void Dispose()
        {
            iconRun++;
            try { work.CompleteAdding(); } catch { }
            try { iconWork.CompleteAdding(); } catch { }
        }

        /// <summary>A command still arriving while the session closes is dropped.</summary>
        void Queue(Action a) { try { work.Add(a); } catch (InvalidOperationException) { } }

        public void OnLine(string l)
        {
            int c = l.IndexOf(';');
            string cmd = c < 0 ? l : l.Substring(0, c), arg = c < 0 ? "" : l.Substring(c + 1);
            var a = arg.Split(';');
            switch (cmd)
            {
                case "MOUSE":
                    if (arg == "center") CenterOnTv();
                    else if (a.Length == 2 && int.TryParse(a[0], out int dx) && int.TryParse(a[1], out int dy)) MoveBy(dx, dy);
                    break;
                case "BTN":
                    if (a.Length == 2) Button(a[0], a[1] == "1");
                    break;
                case "WHEEL":
                    if (int.TryParse(arg, out int n)) Mouse(0x0800 /*WHEEL*/, Math.Max(-20, Math.Min(20, n)) * 120);
                    break;
                case "KEY":
                    if (a.Length == 2 && int.TryParse(a[0], out int vk) && vk > 0 && vk < 256) Key((ushort)vk, a[1] == "1");
                    break;
                case "TEXT":
                    Text(arg);
                    break;
                case "WINS?": Queue(SendWindows); break;
                case "WIN": if (a.Length == 2) Queue(() => MoveWindow(a[0], a[1] == "tv")); break;
                case "APPS?": Queue(() => SendApps(arg == "1")); break;
                case "APP": Queue(() => Launch(arg)); break;
                case "FAV": if (a.Length == 2) Queue(() => Favorite(a[0], a[1] == "1")); break;
            }
        }

        // ---------------- mouse and keyboard ----------------

        void MoveBy(int dx, int dy)
        {
            GetCursorPos(out var p);
            MoveTo(p.X + Clamp(dx), p.Y + Clamp(dy));
        }

        static int Clamp(int d) => Math.Max(-2000, Math.Min(2000, d));

        /// <summary>Absolute move over the whole desktop (no Windows pointer acceleration, raw input sees it).</summary>
        static void MoveTo(int x, int y)
        {
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = Math.Max(2, GetSystemMetrics(78)), vh = Math.Max(2, GetSystemMetrics(79));
            x = Math.Max(vx, Math.Min(vx + vw - 1, x)); y = Math.Max(vy, Math.Min(vy + vh - 1, y));
            int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)), ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
            SendMouse(0x0001 | 0x8000 | 0x4000 /*MOVE | ABSOLUTE | VIRTUALDESK*/, nx, ny, 0);
        }

        void CenterOnTv()
        {
            var tv = Monitors().FirstOrDefault(m => m.Device == tvDevice());
            if (tv == null) return;
            GetCursorPos(out var p);
            if (tv.Bounds.Contains(p.X, p.Y)) return; // already on the TV: leave it where it is
            MoveTo(tv.Bounds.X + tv.Bounds.Width / 2, tv.Bounds.Y + tv.Bounds.Height / 2);
        }

        static void Button(string which, bool down)
        {
            int f = which == "r" ? (down ? 0x0008 : 0x0010) : which == "m" ? (down ? 0x0020 : 0x0040) : (down ? 0x0002 : 0x0004);
            Mouse(f, 0);
        }

        static void Mouse(int flags, int data) => SendMouse(flags, 0, 0, data);

        static void SendMouse(int flags, int x, int y, int data)
        {
            var i = new INPUT { type = 0 };
            i.u.mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags };
            SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>Keys that are "extended" on a PC keyboard (arrows, Insert/Delete block, Windows, right Alt/Ctrl…).</summary>
        static readonly HashSet<int> Extended = new HashSet<int> { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0xA3, 0xA5, 0x6F, 0x90 };

        static void Key(ushort vk, bool down)
        {
            var i = new INPUT { type = 1 };
            i.u.ki = new KEYBDINPUT
            {
                wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = (Extended.Contains(vk) ? 0x0001 : 0) | (down ? 0 : 0x0002)
            };
            SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
        }

        static void Text(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 2000) return;
            var list = new List<INPUT>();
            foreach (char ch in s)
            {
                if (char.IsControl(ch)) continue;
                foreach (int up in new[] { 0, 0x0002 })
                {
                    var i = new INPUT { type = 1 };
                    i.u.ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 /*UNICODE*/ | up };
                    list.Add(i);
                }
            }
            if (list.Count > 0) SendInput(list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        // ---------------- screens ----------------

        sealed class Monitor { public string Device; public Rectangle Bounds, Work; public bool Primary; }

        static List<Monitor> Monitors()
        {
            var list = new List<Monitor>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, dc, r, d) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (GetMonitorInfo(h, ref mi))
                    list.Add(new Monitor { Device = mi.szDevice, Bounds = mi.rcMonitor.R, Work = mi.rcWork.R, Primary = (mi.dwFlags & 1) != 0 });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>"tv", "main" (the PC's primary screen) or 2, 3… for the other screens, left to right.</summary>
        string ScreenOf(Rectangle r, List<Monitor> mons)
        {
            var best = mons.OrderByDescending(m => Area(Rectangle.Intersect(m.Bounds, r))).FirstOrDefault();
            if (best == null) return "main";
            if (best.Device == tvDevice()) return "tv";
            if (best.Primary) return "main";
            var others = mons.Where(m => !m.Primary && m.Device != tvDevice()).OrderBy(m => m.Bounds.X).ToList();
            return (others.IndexOf(best) + 2).ToString();
        }

        static long Area(Rectangle r) => r.IsEmpty ? 0 : (long)r.Width * r.Height;

        // ---------------- windows ("Alt-Tab") ----------------

        void SendWindows()
        {
            var mons = Monitors();
            var list = new List<IntPtr>();
            EnumWindows((h, p) => { if (AltTab(h)) list.Add(h); return true; }, IntPtr.Zero); // top to bottom = Alt-Tab order
            var ids = new Dictionary<string, IntPtr>();
            var sb = new StringBuilder();
            foreach (var h in list.Take(40))
            {
                string id = ((long)h).ToString("x");
                ids[id] = h;
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                GetWindowPlacement(h, ref wp);
                GetWindowRect(h, out var rc);
                string state = wp.showCmd == 2 ? "m" : wp.showCmd == 3 ? "x" : "n";
                // A minimized window's rectangle is off-screen: its screen is where it will come back.
                var where = state == "m" ? Normal(wp) : rc.R;
                sb.Append(id).Append('\t').Append(ScreenOf(where, mons)).Append('\t').Append(state).Append('\t')
                  .Append(Clean(Title(h))).Append('\t').Append(WindowIcon(h)).Append('\n');
            }
            windows = ids;
            send(8, sb.ToString());
        }

        static Rectangle Normal(WINDOWPLACEMENT wp) => wp.rcNormalPosition.R; // workspace coordinates: close enough to pick the screen

        static bool AltTab(IntPtr h)
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return false;
            int ex = GetWindowLong(h, -20);
            if ((ex & 0x80) != 0) return false;                                         // tool window
            if (GetWindow(h, 4) != IntPtr.Zero && (ex & 0x40000) == 0) return false;   // owned, not an app window
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return false; // other desktop, suspended app
            var cls = new StringBuilder(64); GetClassName(h, cls, 64);
            if (cls.ToString() == "Progman" || cls.ToString() == "Shell_TrayWnd") return false;
            GetWindowThreadProcessId(h, out int pid);
            return pid != Process.GetCurrentProcess().Id;
        }

        static string Title(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            return sb.ToString();
        }

        static string Clean(string s) => new string((s ?? "").Where(c => c != '\t' && c != '\n' && c != '\r').ToArray()).Trim();

        void MoveWindow(string id, bool toTv)
        {
            if (!windows.TryGetValue(id, out var h) || !IsWindow(h)) { send(10, "gone"); return; }
            var mons = Monitors();
            var dest = toTv ? mons.FirstOrDefault(m => m.Device == tvDevice()) : mons.FirstOrDefault(m => m.Primary);
            if (dest == null) return;
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            GetWindowPlacement(h, ref wp);
            bool max = wp.showCmd == 3 || (wp.showCmd == 2 && (wp.flags & 2) != 0); // maximized, or minimized from maximized
            if (wp.showCmd == 2 || wp.showCmd == 3) ShowWindow(h, 9 /*RESTORE*/);
            if (IsZoomed(h)) ShowWindow(h, 9); // minimized from maximized comes back maximized: normal size to move it
            GetWindowRect(h, out var rr);
            var r = rr.R;
            var src = mons.OrderByDescending(m => Area(Rectangle.Intersect(m.Bounds, r))).First();
            if (src.Device != dest.Device)
            {
                // Same place relative to the screen, size kept (shrunk if the screen is smaller).
                int w = Math.Min(r.Width, dest.Work.Width), hh = Math.Min(r.Height, dest.Work.Height);
                double fx = src.Work.Width > r.Width ? (r.X - src.Work.X) / (double)(src.Work.Width - r.Width) : 0;
                double fy = src.Work.Height > r.Height ? (r.Y - src.Work.Y) / (double)(src.Work.Height - r.Height) : 0;
                int x = dest.Work.X + (int)(Math.Max(0, Math.Min(1, fx)) * (dest.Work.Width - w));
                int y = dest.Work.Y + (int)(Math.Max(0, Math.Min(1, fy)) * (dest.Work.Height - hh));
                SetWindowPos(h, IntPtr.Zero, x, y, w, hh, 0x0004 | 0x0010 /*NOZORDER | NOACTIVATE*/);
            }
            if (max) ShowWindow(h, 3 /*MAXIMIZE*/);
            Front(h);
            send(10, toTv ? "moved_tv" : "moved_pc");
        }

        /// <summary>SetForegroundWindow is refused to a process the user is not typing in: borrow the input of the current one.</summary>
        static void Front(IntPtr h)
        {
            var fg = GetForegroundWindow();
            int me = GetCurrentThreadId(), other = GetWindowThreadProcessId(fg, out _);
            bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
            try { BringWindowToTop(h); SetForegroundWindow(h); }
            finally { if (attached) AttachThreadInput(me, other, false); }
        }

        static string WindowIcon(IntPtr h)
        {
            IntPtr icon = IntPtr.Zero;
            foreach (int kind in new[] { 1, 2, 0 }) // ICON_BIG, ICON_SMALL2, ICON_SMALL
            {
                if (SendMessageTimeout(h, 0x7F, (IntPtr)kind, IntPtr.Zero, 0x0002, 100, out icon) != IntPtr.Zero && icon != IntPtr.Zero) break;
                icon = IntPtr.Zero;
            }
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, -14);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, -34);
            if (icon != IntPtr.Zero)
                try { using (var ic = Icon.FromHandle(icon)) using (var b = ic.ToBitmap()) return Png(b, 64); } catch { }
            // Fallback: the program's own icon.
            try
            {
                GetWindowThreadProcessId(h, out int pid);
                using (var p = Process.GetProcessById(pid)) return ShellIcon(p.MainModule.FileName, 64);
            }
            catch { return ""; }
        }

        // ---------------- applications ----------------

        /// <param name="icons">The TV has no icons yet ("APPS?;1"); later requests only refresh the list.</param>
        void SendApps(bool icons)
        {
            var favs = new HashSet<string>(Settings.Get("fav_apps", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            var found = new Dictionary<string, App>();
            foreach (var a in ListApps()) found[a.Id] = a;
            apps = found;
            // The list goes alone (a few KB); the icons follow in small, spaced batches (extension [12] "id\tpng"
            // lines). They travel in the video stream: 2 MB at once froze the picture for a second on a 100 Mb/s TV port.
            var sb = new StringBuilder();
            foreach (var a in found.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                sb.Append(a.Id).Append('\t').Append(favs.Contains(a.Id) ? '1' : '0').Append('\t').Append(Clean(a.Name)).Append('\t').Append('\n');
            send(9, sb.ToString());
            if (!icons) return;
            int run = ++iconRun;
            var order = found.Values.OrderByDescending(x => favs.Contains(x.Id)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            try
            {
                iconWork.Add(() =>
                {
                    var b = new StringBuilder();
                    int n = 0;
                    foreach (var a in order)
                    {
                        if (run != iconRun) return; // newer request or session over
                        string png;
                        lock (iconCache) iconCache.TryGetValue(a.IconPath, out png);
                        if (png == null) { png = ShellIcon(a.IconPath, 72); lock (iconCache) iconCache[a.IconPath] = png; }
                        if (png.Length > 0) { b.Append(a.Id).Append('\t').Append(png).Append('\n'); n++; }
                        // ~50 KB every 40 ms at most: about 10 Mb/s for a second or two, next to the video.
                        if (n == 8) { send(12, b.ToString()); b.Clear(); n = 0; Thread.Sleep(40); }
                    }
                    if (n > 0) send(12, b.ToString());
                });
            }
            catch (InvalidOperationException) { }
        }

        static readonly string[] NotApps = {
            "uninstall", "désinstall", "desinstall", "readme", "read me", "lisez", "help", "aide ", "documentation", "manual", "manuel",
            "release notes", "website", "site web", "license", "licence", "support", "faq", "changelog", "what's new", "nouveautés" };

        /// <summary>
        /// Everything the Start menu's "All apps" shows (shell:AppsFolder: desktop and Store apps, Steam games), without
        /// uninstallers, help files and web links; plus Steam Big Picture when Steam is installed.
        /// </summary>
        static List<App> ListApps()
        {
            var list = new List<App>();
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object folder = Call(shell, "NameSpace", "shell:AppsFolder");
                object items = Call(folder, "Items");
                int count = (int)Get(items, "Count");
                for (int i = 0; i < count && list.Count < 500; i++)
                {
                    object it = Call(items, "Item", i);
                    string name = (Get(it, "Name") as string ?? "").Trim(), path = Get(it, "Path") as string ?? "";
                    Marshal.ReleaseComObject(it);
                    if (name.Length == 0 || path.Length == 0) continue;
                    string low = name.ToLowerInvariant(), lp = path.ToLowerInvariant();
                    if (NotApps.Any(low.Contains)) continue;
                    if (lp.StartsWith("http:") || lp.StartsWith("https:") || lp.StartsWith("mailto:")) continue;
                    // Windows' own tools (System32, SysWOW64, Windows folder, system components): not for a TV couch.
                    if (SystemPrefixes.Any(lp.StartsWith)) continue;
                    string ext = Path.GetExtension(lp);
                    if (new[] { ".txt", ".pdf", ".chm", ".htm", ".html", ".rtf", ".doc", ".docx", ".url", ".ini", ".log", ".msc" }.Contains(ext)) continue;
                    string full = "shell:AppsFolder\\" + path;
                    list.Add(new App { Id = Id(path), Name = name, Launch = full, IconPath = full });
                }
                Marshal.ReleaseComObject(items); Marshal.ReleaseComObject(folder);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string exe = (k?.GetValue("SteamExe") as string)?.Replace('/', '\\');
                    if (exe != null && File.Exists(exe))
                        list.Add(new App { Id = Id("steam-bigpicture"), Name = "Steam Big Picture", Launch = "steam://open/bigpicture", IconPath = exe });
                }
            }
            catch { }
            return list.GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First()).ToList();
        }

        static readonly string[] SystemPrefixes = {
            "{1ac14e77-02e7-4e5d-b744-2eb1ae5198b7}", "{d65231b0-b2f1-4857-a4ce-a8e7c6ea7d27}", "{f38bf404-1d43-42f2-9305-67de0b28fc23}",
            "microsoftwindows.", "windows." };

        static object Call(object o, string m, params object[] a) => o.GetType().InvokeMember(m, BindingFlags.InvokeMethod, null, o, a);
        static object Get(object o, string p) => o.GetType().InvokeMember(p, BindingFlags.GetProperty, null, o, null);

        /// <summary>Stable short id (favorites stay after a reinstall of TVScreenPlus).</summary>
        static string Id(string path)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant())).Take(6).Select(b => b.ToString("x2")));
        }

        void Favorite(string id, bool on)
        {
            if (!apps.ContainsKey(id)) return;
            var favs = new HashSet<string>(Settings.Get("fav_apps", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            if (on) favs.Add(id); else favs.Remove(id);
            Settings.Set("fav_apps", string.Join(",", favs.Take(100)));
        }

        void Launch(string id)
        {
            if (!apps.TryGetValue(id, out var a)) return;
            Log.W("TV launches " + a.Name);
            send(10, LaunchUnelevated(a.Launch) ? "launched;" + Clean(a.Name) : "launch_failed;" + Clean(a.Name));
        }

        /// <summary>
        /// TVScreenPlus runs as administrator: started from here, an application would be too. It is started by the
        /// Windows shell's own (non-elevated) account token instead, exactly as from the Start menu.
        /// </summary>
        static bool LaunchUnelevated(string target)
        {
            IntPtr proc = IntPtr.Zero, tok = IntPtr.Zero, primary = IntPtr.Zero;
            try
            {
                var shellWnd = GetShellWindow();
                if (shellWnd == IntPtr.Zero) { Log.W("launch: no Windows shell"); return false; }
                GetWindowThreadProcessId(shellWnd, out int pid);
                proc = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
                if (proc == IntPtr.Zero || !OpenProcessToken(proc, 0x0002 /*DUPLICATE*/, out tok)) { Log.W("launch: shell token " + Marshal.GetLastWin32Error()); return false; }
                if (!DuplicateTokenEx(tok, 0x018B, IntPtr.Zero, 2, 1, out primary)) { Log.W("launch: duplicate " + Marshal.GetLastWin32Error()); return false; }
                EnablePrivilege("SeIncreaseQuotaPrivilege"); // as in Microsoft's "launch unelevated" sample
                string rundll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe");
                var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
                string cmd = $"\"{rundll}\" shell32.dll,ShellExec_RunDLL \"{target.Replace("\"", "")}\"";
                if (!CreateProcessWithTokenW(primary, 0, null, cmd, 0, IntPtr.Zero, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ref si, out var pi))
                { Log.W("launch: " + Marshal.GetLastWin32Error()); return false; }
                CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
                return true;
            }
            finally
            {
                if (primary != IntPtr.Zero) CloseHandle(primary);
                if (tok != IntPtr.Zero) CloseHandle(tok);
                if (proc != IntPtr.Zero) CloseHandle(proc);
            }
        }

        static void EnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 /*ADJUST_PRIVILEGES*/, out var t)) return;
            try
            {
                var tp = new TOKEN_PRIVILEGES { Count = 1, Attributes = 2 /*ENABLED*/ };
                if (LookupPrivilegeValue(null, name, out tp.Luid)) AdjustTokenPrivileges(t, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(t); }
        }

        // ---------------- icons ----------------

        /// <summary>Icon of a file or shell item (shell:AppsFolder\…) at this size, as PNG base64 ("" if none).</summary>
        static string ShellIcon(string path, int size)
        {
            IntPtr hbm = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var f);
                try
                {
                    if (f.GetImage(new SIZE { cx = size, cy = size }, 0x4 /*ICONONLY*/, out hbm) != 0 || hbm == IntPtr.Zero) return "";
                    using (var b = FromHbitmap(hbm)) return Png(b, size);
                }
                finally { Marshal.ReleaseComObject(f); }
            }
            catch { return ""; }
            finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
        }

        /// <summary>32-bit bitmap with its transparency (Image.FromHbitmap drops it).</summary>
        static Bitmap FromHbitmap(IntPtr hbm)
        {
            var bm = new BITMAP();
            GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), ref bm);
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            var dc = GetDC(IntPtr.Zero);
            try { GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0); } finally { ReleaseDC(IntPtr.Zero, dc); }
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255; // no transparency at all: opaque image
            // The shell hands out premultiplied pixels.
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
            b.UnlockBits(d);
            return b;
        }

        static string Png(Image img, int size)
        {
            using (var b = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, size, size);
                }
                using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return Convert.ToBase64String(ms.ToArray()); }
            }
        }

        // ---------------- Win32 ----------------

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, Rr, B; public Rectangle R => Rectangle.FromLTRB(L, T, Rr, B); }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public int dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public InputUnion u; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT rcNormalPosition; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO { public int cb; public string lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

        delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
        delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, IntPtr r, IntPtr d);

        [DllImport("user32.dll")] static extern int SendInput(int n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc cb, IntPtr d);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(int a, int b, bool attach);
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr h, int i);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, int access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr tok, int access, IntPtr attrs, int level, int type, out IntPtr dup);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessWithTokenW(IntPtr tok, int logon, string app, string cmd, int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory f);
    }
}
