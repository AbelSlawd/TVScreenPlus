using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TVScreenPlus
{
    /// <summary>
    /// TV gamepads seen by Windows as Xbox 360 controllers, through the ViGEmBus driver (BSD-3, signed).
    /// TV -> PC lines: "PADON;slot;name", "PADOFF;slot", "PAD;slot;buttons(hex, XInput bits);lt;rt;lx;ly;rx;ry".
    /// PC -> TV rumble (flag 0x08): [u8 kind=2][u8 slot][u8 large motor][u8 small motor].
    /// One hub per TV session; its virtual pads disappear with the session (closing the bus handle unplugs them).
    /// </summary>
    sealed class PadHub : IDisposable
    {
        public const byte RumbleKind = 2;
        const int Slots = 4;
        readonly Action<int, int, int> rumble;
        readonly Action<string, string> notify;
        readonly SessionStats stats;
        readonly object l = new object();
        readonly string[] names = new string[Slots];
        readonly byte[][] reports = new byte[Slots][];
        readonly ViGEmBus.X360[] pads = new ViGEmBus.X360[Slots];
        ViGEmBus bus;
        int opening; // 0 = idle, 1 = opening/installing, 2 = failed (no retry this session)
        volatile bool disposed;

        public PadHub(Action<int, int, int> rumble, Action<string, string> notify, SessionStats stats)
        {
            this.rumble = rumble; this.notify = notify; this.stats = stats;
            stats.Pads = "aucune manette détectée sur la TV";
        }

        public void OnLine(string line)
        {
            var p = line.Split(';');
            if (p.Length < 2 || !int.TryParse(p[1], out int slot) || slot < 0 || slot >= Slots) return;
            switch (p[0])
            {
                case "PADON":
                    lock (l) names[slot] = p.Length > 2 && p[2].Length > 0 ? p[2] : "Manette";
                    Log.W($"Gamepad {slot + 1} connected on the TV: {names[slot]}");
                    if (bus == null) OpenBus(); else lock (l) Plug(slot);
                    break;
                case "PADOFF":
                    lock (l)
                    {
                        Log.W($"Gamepad {slot + 1} disconnected from the TV");
                        names[slot] = null; reports[slot] = null;
                        pads[slot]?.Dispose(); pads[slot] = null;
                    }
                    break;
                case "PAD":
                    if (p.Length < 9) return;
                    var r = new byte[12];
                    if (!ushort.TryParse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort b)) return;
                    r[0] = (byte)b; r[1] = (byte)(b >> 8);
                    r[2] = (byte)Clamp(p[3], 0, 255); r[3] = (byte)Clamp(p[4], 0, 255);
                    for (int i = 0; i < 4; i++) { short s = (short)Clamp(p[5 + i], -32768, 32767); r[4 + i * 2] = (byte)s; r[5 + i * 2] = (byte)(s >> 8); }
                    lock (l)
                    {
                        reports[slot] = r;
                        try { pads[slot]?.Submit(r); } catch (Exception e) { Log.W("pad submit: " + e.Message); }
                    }
                    break;
            }
            UpdateStats();
        }

        static int Clamp(string s, int min, int max) => int.TryParse(s, out int v) ? Math.Max(min, Math.Min(max, v)) : 0;

        /// <summary>Opens the bus, installing the driver the first time a TV gamepad is used.</summary>
        void OpenBus()
        {
            if (Interlocked.CompareExchange(ref opening, 1, 0) != 0) return;
            new Thread(() =>
            {
                try
                {
                    if (!ViGEmBus.IsInstalled())
                    {
                        notify("TVScreenPlus", "Installation du pilote de manettes (ViGEmBus)…");
                        GamepadDriver.Install();
                    }
                    var b = ViGEmBus.Open();
                    lock (l)
                    {
                        if (disposed) { b.Dispose(); return; }
                        bus = b;
                        for (int i = 0; i < Slots; i++) if (names[i] != null) Plug(i);
                    }
                    opening = 0;
                }
                catch (Exception e)
                {
                    Log.W("Gamepads unavailable: " + e.Message);
                    stats.Pads = "indisponibles : " + e.Message;
                    notify("TVScreenPlus", "Manettes indisponibles : " + e.Message);
                    opening = 2;
                }
                UpdateStats();
            }) { IsBackground = true, Name = "vigem-open" }.Start();
        }

        /// <summary>Must hold l.</summary>
        void Plug(int slot)
        {
            if (bus == null || pads[slot] != null || disposed) return;
            try
            {
                int s = slot;
                pads[slot] = bus.PlugX360((large, small) => rumble(s, large, small));
                Log.W($"Gamepad {slot + 1} -> virtual Xbox 360 controller #{pads[slot].Serial}");
                if (reports[slot] != null) pads[slot].Submit(reports[slot]);
            }
            catch (Exception e) { Log.W("pad plug: " + e.Message); stats.Pads = "erreur : " + e.Message; }
        }

        void UpdateStats()
        {
            lock (l)
            {
                var on = Enumerable.Range(0, Slots).Where(i => names[i] != null).Select(i => $"{i + 1}: {names[i]}{(pads[i] != null ? "" : " (en attente du pilote)")}").ToList();
                if (opening == 2) return;
                stats.Pads = on.Count == 0 ? "aucune manette détectée sur la TV" : string.Join(" · ", on);
            }
        }

        public void Dispose()
        {
            lock (l)
            {
                disposed = true;
                for (int i = 0; i < Slots; i++) { pads[i]?.Dispose(); pads[i] = null; }
                bus?.Dispose(); bus = null;
            }
        }
    }

    /// <summary>Install / uninstall of the ViGEmBus driver (silent setup bundled with the app, else downloaded).</summary>
    static class GamepadDriver
    {
        /// <summary>ViGEmBus 1.22.0 setup, shipped in TVScreenPlus-PC.zip (its signature is checked when the package is built).</summary>
        public const string SetupName = "ViGEmBus_setup.exe";

        public static void Install()
        {
            string exe = Path.Combine(Program.Dir, SetupName);
            if (!File.Exists(exe)) throw new Exception("installation du pilote de manettes introuvable : réinstallez TVScreenPlus depuis TVScreenPlus-PC.zip");
            Log.W("Installing ViGEmBus: " + exe);
            // Advanced Installer bootstrapper: no UI, silent MSI, never reboot by itself.
            int code = -1;
            using (var p = Process.Start(new ProcessStartInfo(exe, "/exenoui /qn /norestart") { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (p.WaitForExit(180000)) code = p.ExitCode; else try { p.Kill(); } catch { }
            }
            Log.W("ViGEmBus setup exit code " + code);
            for (int i = 0; i < 20 && !ViGEmBus.IsInstalled(); i++) Thread.Sleep(500);
            if (!ViGEmBus.IsInstalled())
                throw new Exception(code == 3010 ? "redémarrez le PC pour terminer l'installation du pilote" : "le pilote ViGEmBus n'a pas pu être installé (code " + code + ")");
            Settings.Set("vigem_ours", "1"); // uninstalled with TVScreenPlus (left alone if another app installed it)
            Log.W("ViGEmBus installed");
        }

        /// <summary>Removes ViGEmBus only if TVScreenPlus installed it.</summary>
        public static void UninstallIfOurs()
        {
            if (Settings.Get("vigem_ours", "0") != "1") return;
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var un = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (un == null) continue;
                        foreach (var name in un.GetSubKeyNames())
                            using (var k = un.OpenSubKey(name))
                            {
                                if (!((k?.GetValue("DisplayName") as string) ?? "").StartsWith("ViGEm Bus Driver", StringComparison.OrdinalIgnoreCase)) continue;
                                var m = Regex.Match(name + " " + (k.GetValue("UninstallString") as string ?? ""), @"\{[0-9A-Fa-f\-]{36}\}");
                                if (!m.Success) continue;
                                Log.W("Uninstalling ViGEmBus " + m.Value);
                                using (var p = Process.Start(new ProcessStartInfo("msiexec.exe", "/x " + m.Value + " /qn /norestart") { UseShellExecute = false, CreateNoWindow = true }))
                                    p.WaitForExit(120000);
                                return;
                            }
                    }
                }
                catch (Exception e) { Log.W("ViGEmBus uninstall: " + e.Message); }
        }
    }

    /// <summary>
    /// Minimal ViGEmBus client talking to the driver's documented IOCTL interface (the same requests as the
    /// official ViGEmClient library, MIT), so no native DLL has to be shipped.
    /// </summary>
    sealed class ViGEmBus : IDisposable
    {
        static Guid BusInterface = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");
        // CTL_CODE(FILE_DEVICE_BUS_EXTENDER, 0x801 + n, METHOD_BUFFERED, FILE_WRITE_DATA [| FILE_READ_DATA])
        const uint IoctlPlugin = 0x2AA004, IoctlUnplug = 0x2AA008, IoctlCheckVersion = 0x2AA00C, IoctlWaitReady = 0x2AA010,
                   IoctlXusbNotification = 0x2AE804, IoctlXusbSubmit = 0x2AA808;
        readonly SafeFileHandle h;
        readonly Io io = new Io();      // plug / unplug / reports (serialised by 'io')

        ViGEmBus(SafeFileHandle h) { this.h = h; }

        public static bool IsInstalled() => DevicePath() != null;

        public static ViGEmBus Open()
        {
            string path = DevicePath() ?? throw new Exception("pilote ViGEmBus introuvable");
            var fh = CreateFile(path, 0xC0000000 /*GENERIC_READ|WRITE*/, 3, IntPtr.Zero, 3 /*OPEN_EXISTING*/,
                                0x80 | 0x20000000 | 0x80000000 | 0x40000000 /*NORMAL|NO_BUFFERING|WRITE_THROUGH|OVERLAPPED*/, IntPtr.Zero);
            if (fh.IsInvalid) throw new Exception("accès au pilote ViGEmBus refusé (0x" + Marshal.GetLastWin32Error().ToString("X") + ")");
            var bus = new ViGEmBus(fh);
            lock (bus.io)
            {
                bus.io.Set(8, 1); // VIGEM_CHECK_VERSION { Size, VIGEM_COMMON_VERSION }
                if (!bus.io.Call(fh, IoctlCheckVersion, 8, 0)) { bus.Dispose(); throw new Exception("version du pilote ViGEmBus incompatible"); }
            }
            return bus;
        }

        /// <summary>Plugs a virtual Xbox 360 controller; onRumble(large, small) is called when a game sets the motors.</summary>
        public X360 PlugX360(Action<int, int> onRumble)
        {
            lock (io)
            {
                for (uint serial = 1; serial <= 16; serial++)
                {
                    // VIGEM_PLUGIN_TARGET { Size, SerialNo, TargetType (0 = Xbox360Wired), VendorId, ProductId }
                    io.Set(16, serial, 0, 0x045E | (0x028E << 16));
                    if (!io.Call(h, IoctlPlugin, 16, 0)) continue; // serial taken by another app
                    io.Set(8, serial);
                    if (!io.Call(h, IoctlWaitReady, 8, 0) && Marshal.GetLastWin32Error() != 87 /*pre-1.17 driver*/)
                    {
                        io.Set(8, serial); io.Call(h, IoctlUnplug, 8, 0);
                        throw new Exception("la manette virtuelle n'a pas démarré");
                    }
                    return new X360(this, serial, onRumble);
                }
            }
            throw new Exception("aucun emplacement libre sur le pilote ViGEmBus");
        }

        public void Dispose()
        {
            try { CancelIoEx(h, IntPtr.Zero); } catch { }
            h.Dispose(); // closing the handle unplugs every controller we created
        }

        public sealed class X360 : IDisposable
        {
            readonly ViGEmBus bus;
            public readonly uint Serial;
            readonly Thread notifications;
            volatile bool closed;

            internal X360(ViGEmBus bus, uint serial, Action<int, int> onRumble)
            {
                this.bus = bus; Serial = serial;
                notifications = new Thread(() =>
                {
                    using (var nio = new Io())
                    {
                        int fails = 0;
                        while (!closed && fails < 20)
                        {
                            // XUSB_REQUEST_NOTIFICATION { Size = 12, SerialNo, LargeMotor, SmallMotor, LedNumber }: pending until a game changes the motors.
                            nio.Set(12, Serial, 0);
                            if (nio.Call(bus.h, IoctlXusbNotification, 12, 12))
                            {
                                fails = 0;
                                if (!closed) try { onRumble(Marshal.ReadByte(nio.Buf, 8), Marshal.ReadByte(nio.Buf, 9)); } catch { }
                            }
                            else
                            {
                                int err = Marshal.GetLastWin32Error();
                                if (err == 995 /*ABORTED*/ || err == 5 /*ACCESS_DENIED*/ || err == 6 /*INVALID_HANDLE*/) break;
                                fails++; Thread.Sleep(50);
                            }
                        }
                    }
                }) { IsBackground = true, Name = "pad-rumble-" + serial };
                notifications.Start();
            }

            /// <summary>XUSB_REPORT: wButtons, bLeftTrigger, bRightTrigger, sThumbLX, sThumbLY, sThumbRX, sThumbRY (12 bytes).</summary>
            public void Submit(byte[] report)
            {
                if (closed) return;
                lock (bus.io)
                {
                    bus.io.Set(20, Serial);
                    Marshal.Copy(report, 0, bus.io.Buf + 8, 12);
                    if (!bus.io.Call(bus.h, IoctlXusbSubmit, 20, 0)) throw new Exception("report 0x" + Marshal.GetLastWin32Error().ToString("X"));
                }
            }

            public void Dispose()
            {
                if (closed) return;
                closed = true;
                if (!bus.h.IsClosed)
                    lock (bus.io) { bus.io.Set(8, Serial); bus.io.Call(bus.h, IoctlUnplug, 8, 0); }
                // The pending notification request completes on unplug; make sure the thread is gone.
                for (int i = 0; i < 5 && notifications.IsAlive; i++)
                {
                    if (!bus.h.IsClosed) try { CancelIoEx(bus.h, IntPtr.Zero); } catch { }
                    notifications.Join(100);
                }
            }
        }

        /// <summary>One overlapped request at a time: OVERLAPPED + 64-byte buffer in unmanaged memory (the driver writes asynchronously).</summary>
        sealed class Io : IDisposable
        {
            public readonly IntPtr Buf = Marshal.AllocHGlobal(64);
            readonly IntPtr ov = Marshal.AllocHGlobal(64);
            readonly ManualResetEvent ev = new ManualResetEvent(false);

            /// <summary>Zeroes the buffer and writes [size, values…] as 32-bit integers.</summary>
            public void Set(int size, params uint[] values)
            {
                for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(Buf, i, 0);
                Marshal.WriteInt32(Buf, 0, size);
                for (int i = 0; i < values.Length; i++) Marshal.WriteInt32(Buf, 4 + i * 4, (int)values[i]);
            }

            public bool Call(SafeFileHandle h, uint code, int inLen, int outLen)
            {
                for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(ov, i, 0);
                ev.Reset();
                Marshal.WriteIntPtr(ov, IntPtr.Size * 2 + 8, ev.SafeWaitHandle.DangerousGetHandle()); // OVERLAPPED.hEvent
                if (!DeviceIoControl(h, code, Buf, inLen, outLen > 0 ? Buf : IntPtr.Zero, outLen, out _, ov) && Marshal.GetLastWin32Error() != 997 /*IO_PENDING*/)
                    return false;
                return GetOverlappedResult(h, ov, out _, true);
            }

            public void Dispose() { Marshal.FreeHGlobal(Buf); Marshal.FreeHGlobal(ov); ev.Dispose(); }
        }

        static string DevicePath()
        {
            var set = SetupDiGetClassDevs(ref BusInterface, IntPtr.Zero, IntPtr.Zero, 0x2 | 0x10 /*PRESENT|DEVICEINTERFACE*/);
            if (set == new IntPtr(-1)) return null;
            try
            {
                var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref BusInterface, 0, ref ifd)) return null;
                SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) return null;
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref ifd, detail, size, out _, IntPtr.Zero)) return null;
                    return Marshal.PtrToStringUni(detail + 4);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, int share, IntPtr sa, int disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inLen, IntPtr outBuf, int outLen, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
    }
}
