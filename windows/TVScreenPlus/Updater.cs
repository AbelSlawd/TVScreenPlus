using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TVScreenPlus
{
    /// <summary>
    /// Single installed copy in Program Files. Running any other copy (new zip, update download)
    /// stops the running instance, replaces the installed files, removes the previous copy and restarts.
    /// </summary>
    static class Installer
    {
        public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TVScreenPlus");
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TVScreenPlus";
        static readonly string[] Files = { "TVScreenPlus.exe", "TVScreenPlus.exe.config", "ffmpeg.exe", GamepadDriver.SetupName };

        static bool Same(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns true when this process is the installed copy and should keep running.</summary>
        public static bool EnsureInstalled()
        {
            string here = Program.Dir;
            if (Same(here, InstallDir)) return true;
            Log.W($"Installing {Program.Version} from {here}");
            string previous = PreviousLocation();
            StopRunning(previous);

            Directory.CreateDirectory(InstallDir);
            foreach (var f in Files)
                if (File.Exists(Path.Combine(here, f))) Retry(() => File.Copy(Path.Combine(here, f), Path.Combine(InstallDir, f), true));
            string drv = Path.Combine(here, "driver");
            if (Directory.Exists(drv))
            {
                Directory.CreateDirectory(Path.Combine(InstallDir, "driver"));
                foreach (var f in Directory.GetFiles(drv)) Retry(() => File.Copy(f, Path.Combine(InstallDir, "driver", Path.GetFileName(f)), true));
            }
            // The previous manual copy (e.g. an old extracted folder) is removed.
            if (previous != null && !Same(previous, InstallDir) && !Same(previous, here)) RemoveCopy(previous);
            Register();
            Shortcut();
            // ShellExecute: the new instance must not inherit any of our handles.
            Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, "TVScreenPlus.exe")) { UseShellExecute = true, WorkingDirectory = InstallDir });
            return false;
        }

        public static void Register()
        {
            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                string exe = Path.Combine(InstallDir, "TVScreenPlus.exe");
                k.SetValue("DisplayName", "TVScreenPlus");
                k.SetValue("DisplayVersion", Program.Version.ToString(3));
                k.SetValue("Publisher", "TVScreenPlus");
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", 180000, RegistryValueKind.DWord);
            }
        }

        /// <summary>Where the currently installed/auto-started copy lives (registry, else scheduled task).</summary>
        static string PreviousLocation()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(UninstallKey))
                    if (k?.GetValue("InstallLocation") is string s && s.Length > 0) return s;
            }
            catch { }
            try
            {
                var psi = new ProcessStartInfo("schtasks", "/Query /TN TVScreenPlus /XML") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var m = Regex.Match(p.StandardOutput.ReadToEnd(), "<Command>\"?([^<\"]+)\"?</Command>");
                    p.WaitForExit(5000);
                    if (m.Success) return Path.GetDirectoryName(m.Groups[1].Value);
                }
            }
            catch { }
            return null;
        }

        /// <summary>Ask running instances to quit, then make sure they and their ffmpeg are gone.</summary>
        public static void StopRunning(string previous)
        {
            try { using (var ev = EventWaitHandle.OpenExisting(Program.QuitEvent)) ev.Set(); } catch { }
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("TVScreenPlus").Where(p => p.Id != me))
                try { if (!p.WaitForExit(10000)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            foreach (var p in Process.GetProcessesByName("ffmpeg"))
                try
                {
                    string dir = Path.GetDirectoryName(p.MainModule.FileName);
                    if (Same(dir, InstallDir) || (previous != null && Same(dir, previous)) || Same(dir, Program.DataDir)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
        }

        public static void KillStrayFfmpeg()
        {
            foreach (var p in Process.GetProcessesByName("ffmpeg"))
                try
                {
                    string dir = Path.GetDirectoryName(p.MainModule.FileName);
                    if (Same(dir, InstallDir) || Same(dir, Program.Dir) || Same(dir, Program.DataDir)) { p.Kill(); p.WaitForExit(3000); Log.W("Killed stray ffmpeg " + p.Id); }
                }
                catch { }
        }

        static readonly string ShortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "TVScreenPlus.lnk");

        /// <summary>Start menu shortcut, so the app can be relaunched after "Quitter".</summary>
        public static void Shortcut()
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { ShortcutPath });
                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Path.Combine(InstallDir, "TVScreenPlus.exe") });
                lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { InstallDir });
                lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "Utiliser une TV Google TV comme écran secondaire" });
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch (Exception e) { Log.W("shortcut: " + e.Message); }
        }

        static void RemoveCopy(string dir)
        {
            Log.W("Removing previous copy in " + dir);
            foreach (var f in Files) try { File.Delete(Path.Combine(dir, f)); } catch { }
            try { Directory.Delete(Path.Combine(dir, "driver"), true); } catch { }
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }

        static void Retry(Action a)
        {
            for (int i = 0; ; i++)
                try { a(); return; }
                catch (IOException) when (i < 20) { Thread.Sleep(500); }
                catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(500); }
        }

        /// <summary>Delete leftovers of previous update downloads.</summary>
        public static void CleanupTemp()
        {
            foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "TVScreenPlus_update_*"))
                try { Directory.Delete(d, true); } catch { }
        }

        public static void Uninstall()
        {
            if (MessageBox.Show("Désinstaller TVScreenPlus ?", "TVScreenPlus", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            StopRunning(PreviousLocation());
            try { VirtualDisplay.Remove(); } catch (Exception e) { Log.W("driver remove: " + e.Message); }
            try { GamepadDriver.UninstallIfOurs(); } catch (Exception e) { Log.W("gamepad driver remove: " + e.Message); }
            AudioLoopback.RestoreMuteAfterCrash();
            Setup.Run("schtasks", "/Delete /F /TN TVScreenPlus");
            Setup.Run("netsh", "advfirewall firewall delete rule name=TVScreenPlus");
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { File.Delete(ShortcutPath); } catch { }
            try { Directory.Delete(Program.DataDir, true); } catch { }
            // Delete our own folder once this process has exited.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{Program.Dir.TrimEnd('\\')}\"")
            { CreateNoWindow = true, UseShellExecute = false });
            MessageBox.Show("TVScreenPlus a été désinstallé.", "TVScreenPlus");
        }
    }

    /// <summary>Checks GitHub Releases and installs newer versions.</summary>
    static class Updater
    {
        const string Api = "https://api.github.com/repos/AbelSlawd/TVScreenPlus/releases/latest";

        public static bool Newer(Version v) =>
            v.Major != Program.Version.Major ? v.Major > Program.Version.Major :
            v.Minor != Program.Version.Minor ? v.Minor > Program.Version.Minor :
            Math.Max(v.Build, 0) > Math.Max(Program.Version.Build, 0);

        /// <summary>Returns (version, zip url) of the latest release, or null.</summary>
        public static Tuple<Version, string> Latest()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "TVScreenPlus-Updater";
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                string json = wc.DownloadString(Api);
                var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(\\.[0-9]+){1,3})\"");
                var url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/TVScreenPlus-PC\\.zip)\"");
                return tag.Success && url.Success ? Tuple.Create(new Version(tag.Groups[1].Value), url.Groups[1].Value) : null;
            }
        }

        /// <summary>Download, extract and start the new version (it replaces this one and restarts).</summary>
        public static void Install(string zipUrl)
        {
            string dir = Path.Combine(Path.GetTempPath(), "TVScreenPlus_update_" + DateTime.Now.Ticks);
            string zip = dir + ".zip";
            Directory.CreateDirectory(dir);
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "TVScreenPlus-Updater";
                wc.DownloadFile(zipUrl, zip);
            }
            ZipFile.ExtractToDirectory(zip, dir);
            try { File.Delete(zip); } catch { }
            string exe = Directory.GetFiles(dir, "TVScreenPlus.exe", SearchOption.AllDirectories).First();
            Log.W("Starting update " + exe);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        }
    }
}
