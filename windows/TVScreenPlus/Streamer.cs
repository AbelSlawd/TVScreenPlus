using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TVScreenPlus
{
    /// <summary>An encoder pipeline: GPU capture (Desktop Duplication) -> encoder, zero-copy when possible.</summary>
    sealed class Encoder
    {
        public string Name, Codec, Filter, Args;
        public Encoder(string name, string codec, string filter, string args) { Name = name; Codec = codec; Filter = filter; Args = args; }

        /// <summary>Candidates from fastest (hardware, zero-copy GPU) to slowest (CPU).</summary>
        public static IEnumerable<Encoder> Candidates(bool hevc)
        {
            foreach (var c in hevc ? new[] { "hevc", "h264" } : new[] { "h264" })
            {
                string nv = $"-c:v {c}_nvenc -preset p2 -tune ull -rc cbr -zerolatency 1 -delay 0 -bf 0 -forced-idr 1";
                string amf = $"-c:v {c}_amf -usage ultralowlatency -quality speed -rc cbr -bf 0";
                string qsv = $"-c:v {c}_qsv -preset veryfast -async_depth 1 -low_delay_brc 1 -bf 0";
                yield return new Encoder(c + "_nvenc", c, "", nv);
                yield return new Encoder(c + "_amf", c, "", amf);
                yield return new Encoder(c + "_qsv", c, ",hwmap=derive_device=qsv,format=qsv,vpp_qsv=format=nv12", qsv);
                yield return new Encoder(c + "_nvenc_sys", c, ",hwdownload,format=bgra", nv);
                yield return new Encoder(c + "_amf_sys", c, ",hwdownload,format=bgra,format=nv12", amf);
                yield return new Encoder(c + "_qsv_sys", c, ",hwdownload,format=bgra,format=nv12", qsv);
            }
            yield return new Encoder("x264", "h264", ",hwdownload,format=bgra,format=yuv420p",
                "-c:v libx264 -preset ultrafast -tune zerolatency -bf 0");
        }

        public string CommandLine(DisplayTarget t, int fps, int kbps, string output) =>
            "-hide_banner -loglevel error -nostdin " +
            $"-init_hw_device d3d11va=d3d:{t.Adapter} -filter_hw_device d3d " +
            $"-f lavfi -i \"ddagrab=output_idx={t.Output}:framerate={fps}:draw_mouse=1{Filter}\" -an " +
            Args + $" -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps / 8}k -g {fps * 60} " +
            output;
    }

    /// <summary>Runs ffmpeg and turns its FLV output into Annex-B access units (one per frame).</summary>
    sealed class Streamer : IDisposable
    {
        Process proc;
        public DateTime Started;

        /// <summary>Find a working encoder for this display (result cached per GPU/codec).</summary>
        public static Encoder Pick(DisplayTarget t, bool hevc, int fps)
        {
            var all = Encoder.Candidates(hevc).ToList();
            string key = "enc_" + t.Adapter + "_" + (hevc ? "hevc" : "h264");
            var cached = all.FirstOrDefault(e => e.Name == Settings.Get(key, ""));
            if (cached != null) return cached;
            foreach (var e in all)
            {
                var sw = Stopwatch.StartNew();
                bool ok = Probe(e, t, fps);
                Log.W($"probe {e.Name}: {(ok ? "OK" : "fail")} ({sw.ElapsedMilliseconds} ms)");
                if (ok) { Settings.Set(key, e.Name); return e; }
            }
            throw new Exception("Aucun encodeur vidéo utilisable");
        }

        public static void Forget(DisplayTarget t)
        {
            Settings.Set("enc_" + t.Adapter + "_hevc", null);
            Settings.Set("enc_" + t.Adapter + "_h264", null);
        }

        static bool Probe(Encoder e, DisplayTarget t, int fps)
        {
            try
            {
                var psi = new ProcessStartInfo(Setup.FFmpeg, e.CommandLine(t, fps, 8000, "-frames:v 5 -f null -"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return false; }
                    if (p.ExitCode != 0) Log.W("  " + err.Result.Trim().Replace("\n", " | "));
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { Log.W("probe error " + ex.Message); return false; }
        }

        /// <summary>
        /// Streams until ffmpeg exits or onFrame throws. onFrame(buffer, offset, length) receives a ready-to-send
        /// packet: [u32 BE payload length][u8 flags][Annex-B access unit]. flags: 1 = keyframe, 2 = new stream.
        /// </summary>
        public void Run(Encoder e, DisplayTarget t, int fps, int kbps, Action<byte[], int, int> onFrame)
        {
            string args = e.CommandLine(t, fps, kbps,
                "-bsf:v dump_extra=freq=keyframe -flush_packets 1 -f flv -flvflags no_duration_filesize pipe:1");
            Log.W("ffmpeg " + args);
            var psi = new ProcessStartInfo(Setup.FFmpeg, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            proc = Process.Start(psi);
            try { proc.PriorityClass = ProcessPriorityClass.High; } catch { }
            proc.ErrorDataReceived += (s, a) => { if (!string.IsNullOrWhiteSpace(a.Data)) Log.W("ffmpeg: " + a.Data); };
            proc.BeginErrorReadLine();
            Started = DateTime.UtcNow;
            Pump(proc.StandardOutput.BaseStream, onFrame);
        }

        static void Pump(Stream s, Action<byte[], int, int> onFrame)
        {
            var hdr = new byte[15];
            if (!Read(s, hdr, 9)) return;                       // "FLV" header
            int skip = (hdr[5] << 24 | hdr[6] << 16 | hdr[7] << 8 | hdr[8]) - 9;
            if (skip > 0 && !Read(s, new byte[skip], skip)) return;
            var buf = new byte[4 << 20];
            bool first = true;
            while (Read(s, hdr, 15))                            // PreviousTagSize(4) + tag header(11)
            {
                int type = hdr[4], size = hdr[5] << 16 | hdr[6] << 8 | hdr[7];
                if (size > buf.Length) buf = new byte[size * 2];
                if (!Read(s, buf, size)) return;
                if (type != 9 || size < 6) continue;            // video only

                int b0 = buf[0], off;
                bool key = ((b0 >> 4) & 7) == 1;
                if ((b0 & 0x80) != 0)
                {                                               // enhanced FLV (HEVC): [hdr][fourcc]([cts])
                    int pt = b0 & 0x0F;
                    if (pt != 1 && pt != 3) continue;           // 1 = CodedFrames, 3 = CodedFramesX
                    off = pt == 1 ? 8 : 5;
                }
                else
                {                                               // legacy FLV (AVC): [hdr][pkttype][cts x3]
                    if (buf[1] != 1) continue;
                    off = 5;
                }
                int end = size;
                if (end - off < 4) continue;

                // Length-prefixed NAL units -> Annex-B start codes, in place.
                bool annexB = buf[off] == 0 && buf[off + 1] == 0 && (buf[off + 2] == 1 || (buf[off + 2] == 0 && buf[off + 3] == 1));
                if (!annexB)
                {
                    for (int i = off; i + 4 <= end;)
                    {
                        int len = buf[i] << 24 | buf[i + 1] << 16 | buf[i + 2] << 8 | buf[i + 3];
                        buf[i] = 0; buf[i + 1] = 0; buf[i + 2] = 0; buf[i + 3] = 1;
                        if (len <= 0) break;
                        i += 4 + len;
                    }
                }
                // Our packet header goes in the 5 bytes just before the payload (off >= 5).
                int plen = end - off, h = off - 5;
                buf[h] = (byte)(plen >> 24); buf[h + 1] = (byte)(plen >> 16); buf[h + 2] = (byte)(plen >> 8); buf[h + 3] = (byte)plen;
                buf[h + 4] = (byte)((key ? 1 : 0) | (first ? 2 : 0));
                first = false;
                onFrame(buf, h, plen + 5);
            }
        }

        static bool Read(Stream s, byte[] b, int n)
        {
            for (int o = 0; o < n;)
            {
                int r = s.Read(b, o, n - o);
                if (r <= 0) return false;
                o += r;
            }
            return true;
        }

        public void Dispose()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            try { proc?.Dispose(); } catch { }
        }
    }
}
