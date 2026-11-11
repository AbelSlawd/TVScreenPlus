using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace TVScreenPlus
{
    /// <summary>Where ffmpeg must capture: DXGI adapter index + output index on that adapter.</summary>
    sealed class DisplayTarget
    {
        public string GdiName; public int Adapter; public int Output; public int Width; public int Height;
        public override string ToString() => $"{GdiName} adapter={Adapter} output={Output} {Width}x{Height}";
    }

    /// <summary>
    /// Controls the open-source IddCx "Virtual Display Driver" (MIT, github.com/VirtualDrivers/Virtual-Display-Driver):
    /// a real extended monitor for Windows that only exists while a TV is connected.
    /// </summary>
    static class VirtualDisplay
    {
        const string HwId = "Root\\MttVDD";
        static Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        const string ConfDir = @"C:\VirtualDisplayDriver";

        // ---------- driver install / enable ----------
        public static bool IsInstalled() => WithDevice((h, d) => true);

        public static void Install(string inf)
        {
            Log.W("Installing driver " + inf);
            // Trust the driver publisher (avoids the "install this device software?" prompt).
            try
            {
                string cat = Directory.GetFiles(Path.GetDirectoryName(inf), "*.cat").FirstOrDefault();
                if (cat != null)
                {
                    var certs = new X509Certificate2Collection();
                    certs.Import(File.ReadAllBytes(cat));
                    using (var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine))
                    {
                        store.Open(OpenFlags.ReadWrite);
                        foreach (var c in certs) store.Add(c);
                    }
                }
            }
            catch (Exception e) { Log.W("cert import: " + e.Message); }

            WriteSettings(1920, 1080, 60);
            var h = SetupDiCreateDeviceInfoList(ref DisplayClass, IntPtr.Zero);
            try
            {
                var d = NewDevInfo();
                if (!SetupDiCreateDeviceInfo(h, "Display", ref DisplayClass, null, IntPtr.Zero, 1 /*DICD_GENERATE_ID*/, ref d))
                    throw Err("SetupDiCreateDeviceInfo");
                byte[] id = Encoding.Unicode.GetBytes(HwId + "\0\0");
                if (!SetupDiSetDeviceRegistryProperty(h, ref d, 1 /*SPDRP_HARDWAREID*/, id, id.Length)) throw Err("SetDeviceRegistryProperty");
                if (!SetupDiCallClassInstaller(0x19 /*DIF_REGISTERDEVICE*/, h, ref d)) throw Err("DIF_REGISTERDEVICE");
            }
            finally { SetupDiDestroyDeviceInfoList(h); }
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HwId, inf, 1 /*INSTALLFLAG_FORCE*/, out _)) throw Err("UpdateDriverForPlugAndPlayDevices");
            Thread.Sleep(2000);
            SetEnabled(false); // only present while a TV is connected
            Log.W("Driver installed");
        }

        static Exception Err(string what) => new Exception(what + " a échoué (0x" + Marshal.GetLastWin32Error().ToString("X") + ")");

        public static void SetEnabled(bool on)
        {
            bool ok = WithDevice((h, d) =>
            {
                var p = new SP_PROPCHANGE_PARAMS { cbSize = 8, InstallFunction = 0x12, StateChange = on ? 1 : 2, Scope = 1, HwProfile = 0 };
                return SetupDiSetClassInstallParams(h, ref d, ref p, Marshal.SizeOf(p)) && SetupDiCallClassInstaller(0x12, h, ref d);
            });
            Log.W("Virtual display " + (on ? "enabled" : "disabled") + " ok=" + ok);
        }

        public static void Remove()
        {
            bool ok = WithDevice((h, d) => SetupDiCallClassInstaller(0x05 /*DIF_REMOVE*/, h, ref d));
            Log.W("Virtual display removed ok=" + ok);
        }

        static bool WithDevice(Func<IntPtr, SP_DEVINFO_DATA, bool> act)
        {
            var h = SetupDiGetClassDevs(ref DisplayClass, IntPtr.Zero, IntPtr.Zero, 0x2 /*DIGCF_PRESENT*/);
            if (h == new IntPtr(-1)) return false;
            try
            {
                var d = NewDevInfo();
                var buf = new byte[2048];
                for (uint i = 0; SetupDiEnumDeviceInfo(h, i, ref d); i++)
                {
                    if (!SetupDiGetDeviceRegistryProperty(h, ref d, 1, out _, buf, buf.Length, out int req)) continue;
                    string ids = Encoding.Unicode.GetString(buf, 0, Math.Max(0, req));
                    if (ids.Split('\0').Any(x => x.Equals(HwId, StringComparison.OrdinalIgnoreCase))) return act(h, d);
                }
                return false;
            }
            finally { SetupDiDestroyDeviceInfoList(h); }
        }

        static SP_DEVINFO_DATA NewDevInfo() => new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };

        static void WriteSettings(int w, int h, int fps)
        {
            Directory.CreateDirectory(ConfDir);
            var modes = new List<(int, int)> { (w, h), (1920, 1080), (2560, 1440), (3840, 2160), (1600, 900), (1280, 720) }.Distinct();
            var sb = new StringBuilder("<?xml version='1.0' encoding='utf-8'?>\n<vdd_settings>\n<monitors><count>1</count></monitors>\n<gpu><friendlyname>default</friendlyname></gpu>\n");
            sb.Append($"<global><g_refresh_rate>{fps}</g_refresh_rate></global>\n<resolutions>\n");
            foreach (var (mw, mh) in modes) sb.Append($"<resolution><width>{mw}</width><height>{mh}</height><refresh_rate>{fps}</refresh_rate></resolution>\n");
            sb.Append("</resolutions>\n<logging><SendLogsThroughPipe>false</SendLogsThroughPipe><logging>false</logging><debuglogging>false</debuglogging></logging>\n</vdd_settings>\n");
            File.WriteAllText(Path.Combine(ConfDir, "vdd_settings.xml"), sb.ToString());
        }

        // ---------- attach / detach ----------
        public static DisplayTarget Attach(int w, int h, int fps)
        {
            WriteSettings(w, h, fps);
            SetEnabled(false);
            Thread.Sleep(300);
            SetEnabled(true);

            string name = null;
            for (int i = 0; i < 50 && name == null; i++)
            {
                if (i == 10 || i == 30) SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x4 /*TOPOLOGY_EXTEND*/ | 0x80 /*APPLY*/);
                Thread.Sleep(200);
                name = FindGdiName();
            }
            if (name == null) throw new Exception("L'écran virtuel n'est pas apparu");

            return SetMode(name, w, h, fps);
        }

        /// <summary>Change the virtual monitor resolution (stays attached).</summary>
        public static DisplayTarget Resize(DisplayTarget t, int w, int h, int fps) => SetMode(t.GdiName, w, h, fps);

        static DisplayTarget SetMode(string name, int w, int h, int fps)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)), dmPelsWidth = w, dmPelsHeight = h, dmDisplayFrequency = fps, dmFields = 0x80000 | 0x100000 | 0x400000 };
            int r = ChangeDisplaySettingsEx(name, ref dm, IntPtr.Zero, 0x1 /*CDS_UPDATEREGISTRY*/, IntPtr.Zero);
            Log.W($"ChangeDisplaySettingsEx {name} {w}x{h}@{fps} -> {r}");

            for (int i = 0; i < 25; i++)
            {
                var t = Dxgi.Find(name);
                if (t != null && (t.Width == w || i > 10)) { Log.W("Target " + t); return t; }
                Thread.Sleep(200);
            }
            throw new Exception("Sortie DXGI introuvable pour " + name);
        }

        public static void Detach() { try { SetEnabled(false); } catch (Exception e) { Log.W("detach: " + e.Message); } }

        /// <summary>GDI name (\\.\DISPLAYn) of the virtual monitor attached to the desktop.</summary>
        static string FindGdiName()
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)))
            {
                if ((dd.StateFlags & 1 /*ATTACHED_TO_DESKTOP*/) == 0) continue;
                bool virt = dd.DeviceString.IndexOf("Virtual Display", StringComparison.OrdinalIgnoreCase) >= 0
                         || dd.DeviceID.IndexOf("MttVDD", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!virt)
                {
                    var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    if (EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) && mon.DeviceID.IndexOf("MTT1337", StringComparison.OrdinalIgnoreCase) >= 0) virt = true;
                }
                if (virt) return dd.DeviceName;
            }
            return null;
        }

        // ---------- P/Invoke ----------
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        struct SP_PROPCHANGE_PARAMS { public int cbSize; public int InstallFunction; public int StateChange; public int Scope; public int HwProfile; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr h, uint i, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiCreateDeviceInfo(IntPtr h, string name, ref Guid g, string desc, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceRegistryProperty(IntPtr h, ref SP_DEVINFO_DATA d, int prop, out int type, byte[] buf, int size, out int req);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiSetDeviceRegistryProperty(IntPtr h, ref SP_DEVINFO_DATA d, int prop, byte[] buf, int size);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiCallClassInstaller(int fn, IntPtr h, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiSetClassInstallParams(IntPtr h, ref SP_DEVINFO_DATA d, ref SP_PROPCHANGE_PARAMS p, int size);
        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hwid, string inf, int flags, out bool reboot);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr p);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint np, IntPtr p, uint nm, IntPtr m, uint flags);
    }

    /// <summary>Minimal DXGI enumeration (same order as ffmpeg's d3d11va device index).</summary>
    static class Dxgi
    {
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr f);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumFn(IntPtr self, uint i, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DescFn(IntPtr self, IntPtr desc);

        static T Vt<T>(IntPtr obj, int slot) where T : class =>
            Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T)) as T;

        public static DisplayTarget Find(string gdiName)
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out IntPtr f) < 0) return null;
            IntPtr desc = Marshal.AllocHGlobal(256);
            try
            {
                var enumAdapters = Vt<EnumFn>(f, 12); // IDXGIFactory1::EnumAdapters1
                for (uint a = 0; enumAdapters(f, a, out IntPtr ad) >= 0; a++)
                {
                    try
                    {
                        var enumOutputs = Vt<EnumFn>(ad, 7); // IDXGIAdapter::EnumOutputs
                        for (uint o = 0; enumOutputs(ad, o, out IntPtr op) >= 0; o++)
                        {
                            try
                            {
                                Vt<DescFn>(op, 7)(op, desc); // IDXGIOutput::GetDesc
                                string n = Marshal.PtrToStringUni(desc);
                                if (string.Equals(n, gdiName, StringComparison.OrdinalIgnoreCase))
                                {
                                    int l = Marshal.ReadInt32(desc, 64), t = Marshal.ReadInt32(desc, 68), r = Marshal.ReadInt32(desc, 72), b = Marshal.ReadInt32(desc, 76);
                                    return new DisplayTarget { GdiName = n, Adapter = (int)a, Output = (int)o, Width = r - l, Height = b - t };
                                }
                            }
                            finally { Marshal.Release(op); }
                        }
                    }
                    finally { Marshal.Release(ad); }
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(desc); Marshal.Release(f); }
        }
    }
}
