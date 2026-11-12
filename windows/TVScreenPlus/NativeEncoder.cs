using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TVScreenPlus
{
    /// <summary>
    /// Minimal raw COM access: calls through vtable slots, no RCWs (no apartment marshaling, deterministic release).
    /// Every argument is passed as a pointer-sized integer, which is ABI-identical on x64 for integer and pointer
    /// parameters (none of the methods used take floats or structs by value). Slot numbers come from the
    /// Windows SDK headers (d3d11.h, dxgi1_2.h, mfobjects.h, mftransform.h, strmif.h).
    /// </summary>
    static unsafe class Com
    {
        public static void* M(void* p, int i) => (*(void***)p)[i];
        public static int C(void* p, int i) => ((delegate* unmanaged[Stdcall]<void*, int>)M(p, i))(p);
        public static int C(void* p, int i, nint a) => ((delegate* unmanaged[Stdcall]<void*, nint, int>)M(p, i))(p, a);
        public static int C(void* p, int i, nint a, nint b) => ((delegate* unmanaged[Stdcall]<void*, nint, nint, int>)M(p, i))(p, a, b);
        public static int C(void* p, int i, nint a, nint b, nint c) => ((delegate* unmanaged[Stdcall]<void*, nint, nint, nint, int>)M(p, i))(p, a, b, c);
        public static int C(void* p, int i, nint a, nint b, nint c, nint d) => ((delegate* unmanaged[Stdcall]<void*, nint, nint, nint, nint, int>)M(p, i))(p, a, b, c, d);
        public static int C(void* p, int i, nint a, nint b, nint c, nint d, nint e) => ((delegate* unmanaged[Stdcall]<void*, nint, nint, nint, nint, nint, int>)M(p, i))(p, a, b, c, d, e);

        public static void Release(ref void* p) { if (p != null) { C(p, 2); p = null; } }
        public static void* QI(void* p, Guid iid)
        {
            void* r = null;
            Check(C(p, 0, (nint)(&iid), (nint)(&r)), "QueryInterface " + iid);
            return r;
        }
        public static void* TryQI(void* p, Guid iid) { void* r = null; return C(p, 0, (nint)(&iid), (nint)(&r)) >= 0 ? r : null; }
        public static void Check(int hr, string what) { if (hr < 0) throw new COMException($"{what} a échoué (0x{hr:X8})", hr); }

        // IMFAttributes (also the base of IMFMediaType, IMFSample, IMFActivate)
        public static int SetU32(void* a, Guid k, uint v) => C(a, 21, (nint)(&k), (nint)v);
        public static int SetU64(void* a, Guid k, ulong v) => C(a, 22, (nint)(&k), (nint)(long)v);
        public static int SetGuid(void* a, Guid k, Guid v) => C(a, 24, (nint)(&k), (nint)(&v));
        public static uint GetU32(void* a, Guid k, uint def) { uint v; return C(a, 7, (nint)(&k), (nint)(&v)) >= 0 ? v : def; }
        public static string GetString(void* a, Guid k)
        {
            char* s = null; uint n;
            if (C(a, 13, (nint)(&k), (nint)(&s), (nint)(&n)) < 0 || s == null) return null;
            try { return new string(s, 0, (int)n); } finally { Native.CoTaskMemFree((nint)s); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Variant { public ushort vt; ushort r1, r2, r3; public ulong v; ulong pad; }

        /// <summary>ICodecAPI::SetValue with a VT_UI4 (or VT_BOOL) value.</summary>
        public static int SetCodec(void* codecApi, Guid k, uint v, bool asBool = false)
        {
            var x = new Variant { vt = (ushort)(asBool ? 11 : 19), v = asBool ? (v != 0 ? 0xFFFFUL : 0) : v };
            int hr = C(codecApi, 9, (nint)(&k), (nint)(&x));
            string e = $" {k.ToString().Substring(0, 8)}={hr:X8}";
            if (hr < 0 && !CodecErrors.Contains(e)) CodecErrors += e;
            return hr;
        }
        /// <summary>ICodecAPI values the encoder refused (diagnostics).</summary>
        public static string CodecErrors = "";
        public static uint GetCodec(void* codecApi, Guid k)
        {
            var x = new Variant();
            return C(codecApi, 8, (nint)(&k), (nint)(&x)) >= 0 ? (uint)x.v : 0xFFFFFFFF;
        }
    }

    static unsafe class Native
    {
        [DllImport("ole32.dll")] public static extern void CoTaskMemFree(nint p);
        [DllImport("ole32.dll")] public static extern int CoInitializeEx(nint reserved, uint coInit);
        [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(Guid* riid, void** factory);
        [DllImport("d3d11.dll")]
        public static extern int D3D11CreateDevice(void* adapter, int driverType, nint software, uint flags, int* levels, uint nLevels,
                                                   uint sdkVersion, void** device, int* level, void** context);
        [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] public static extern int MFCreateDXGIDeviceManager(uint* token, void** manager);
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(void** type);
        [DllImport("mfplat.dll")] public static extern int MFCreateSample(void** sample);
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint max, void** buffer);
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(void** attrs, uint initialSize);
        [DllImport("mfplat.dll")] public static extern int MFCreateDXGISurfaceBuffer(Guid* riid, void* surface, uint subresource, int bottomUp, void** buffer);
        [DllImport("mfplat.dll")]
        public static extern int MFTEnum2(Guid category, uint flags, RegType* input, RegType* output, void* attrs, void*** activates, uint* count);
        [DllImport("kernel32.dll")] public static extern void GetSystemTimePreciseAsFileTime(out long fileTime);
        [DllImport("winmm.dll")] public static extern int timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern int timeEndPeriod(uint ms);

        [StructLayout(LayoutKind.Sequential)] public struct RegType { public Guid Major, Sub; }

        static int mfStarted;
        public static void StartMF()
        {
            if (Interlocked.Exchange(ref mfStarted, 1) == 0) Com.Check(MFStartup(0x00020070, 0), "MFStartup");
        }
    }

    /// <summary>
    /// Wall clock for capture timestamps: QPC (Stopwatch) anchored once to the precise system time, so every
    /// frame gets a sub-millisecond "ms since 1970" without the 1-16 ms granularity of DateTime.UtcNow.
    /// </summary>
    static class WallClock
    {
        static readonly long baseQpc, baseMs100ns;
        static WallClock()
        {
            Native.GetSystemTimePreciseAsFileTime(out long ft);
            baseQpc = Stopwatch.GetTimestamp();
            baseMs100ns = ft - 116444736000000000L; // 1601 -> 1970, in 100 ns
        }
        /// <summary>QPC tick -> ms since 1970 modulo 2^31 (the stream's capture timestamp).</summary>
        public static uint Ms(long qpc) =>
            (uint)((baseMs100ns / 10000 + (long)((qpc - baseQpc) * 1000.0 / Stopwatch.Frequency)) & 0x7FFFFFFF);
    }

    /// <summary>
    /// DXGI Desktop Duplication of one output + GPU conversion BGRA -> NV12 (BT.709 limited range) with the
    /// D3D11 video processor, into a small ring of NV12 textures that the encoder reads directly.
    /// </summary>
    sealed unsafe class DesktopCapture : IDisposable
    {
        static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static readonly Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        static readonly Guid IID_ID3D11VideoDevice = new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
        static readonly Guid IID_ID3D11VideoContext = new Guid("61F21C45-3C0E-4a74-9CEA-67100D9AD5E4");
        static readonly Guid IID_ID3D11Multithread = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");

        public void* Device, Context;
        public long AdapterLuid;
        public int Width, Height;
        public string AdapterName, OutputName;
        public int Left, Top;
        void* dupl, vdev, vctx, vpEnum, vp, copy, inView;
        public readonly int RingSize = 6;
        public static bool FlushAfterConvert = true;
        public static int GpuPriorityLevel = 7, GpuClass = 5;
        public string GpuPriority;
        static readonly Guid IID_IDXGIDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        [DllImport("gdi32.dll")] static extern int D3DKMTSetProcessSchedulingPriorityClass(nint process, int cls);

        /// <summary>
        /// Like OBS: our small conversion/encode jobs must not queue behind a game's frames on the GPU.
        /// Needs administrator rights (the app has them); otherwise the calls fail harmlessly.
        /// </summary>
        static string RaiseGpuPriority(void* device)
        {
            int hr1 = -1, hr2 = -1;
            void* d = Com.TryQI(device, IID_IDXGIDevice);
            if (d != null) { if (GpuPriorityLevel != 0) hr1 = Com.C(d, 10, GpuPriorityLevel); Com.Release(ref d); }
            if (GpuClass != 2) hr2 = D3DKMTSetProcessSchedulingPriorityClass(Process.GetCurrentProcess().Handle, GpuClass);
            return $"thread {GpuPriorityLevel}:{(hr1 >= 0 ? "ok" : hr1.ToString("X8"))} classe {GpuClass}:{(hr2 >= 0 ? "ok" : hr2.ToString("X8"))}";
        }
        public void*[] Ring; void*[] outViews;

        [StructLayout(LayoutKind.Sequential)]
        struct FrameInfo
        {
            public long LastPresentTime, LastMouseUpdateTime;
            public uint AccumulatedFrames; public int RectsCoalesced, ProtectedContentMaskedOut;
            public int PointerX, PointerY, PointerVisible;
            public uint TotalMetadataBufferSize, PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Tex2DDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }

        [StructLayout(LayoutKind.Sequential)]
        struct VpStream
        {
            public int Enable; public uint OutputIndex, InputFrameOrField, PastFrames, FutureFrames;
            public void* ppPast; public void* pInput; public void* ppFuture; public void* ppPastRight; public void* pInputRight; public void* ppFutureRight;
        }

        /// <summary>Lists "adapter/output: name WxH" for every desktop output.</summary>
        public static string[] ListOutputs()
        {
            var r = new System.Collections.Generic.List<string>();
            void* f = null;
            Guid iid = IID_IDXGIFactory1;
            Com.Check(Native.CreateDXGIFactory1(&iid, &f), "CreateDXGIFactory1");
            try
            {
                for (uint a = 0; ; a++)
                {
                    void* ad = null;
                    if (Com.C(f, 12, (nint)a, (nint)(&ad)) < 0) break;
                    for (uint o = 0; ; o++)
                    {
                        void* op = null;
                        if (Com.C(ad, 7, (nint)o, (nint)(&op)) < 0) break;
                        byte* d = stackalloc byte[96];
                        Com.C(op, 7, (nint)d);
                        int* rc = (int*)(d + 64);
                        r.Add($"{a}/{o}: {new string((char*)d).TrimEnd('\0')} {rc[2] - rc[0]}x{rc[3] - rc[1]} à ({rc[0]},{rc[1]})");
                        Com.Release(ref op);
                    }
                    Com.Release(ref ad);
                }
            }
            finally { Com.Release(ref f); }
            return r.ToArray();
        }

        public DesktopCapture(int adapterIdx, int outputIdx)
        {
            void* f = null, ad = null, op = null, op1 = null;
            Guid iid = IID_IDXGIFactory1;
            Com.Check(Native.CreateDXGIFactory1(&iid, &f), "CreateDXGIFactory1");
            try
            {
                Com.Check(Com.C(f, 12, (nint)adapterIdx, (nint)(&ad)), "EnumAdapters1");
                byte* ades = stackalloc byte[312];
                Com.Check(Com.C(ad, 10, (nint)ades), "GetDesc1");
                AdapterName = new string((char*)ades).TrimEnd('\0');
                AdapterLuid = *(long*)(ades + 296);
                Com.Check(Com.C(ad, 7, (nint)outputIdx, (nint)(&op)), "EnumOutputs");
                byte* odes = stackalloc byte[96];
                Com.C(op, 7, (nint)odes);
                OutputName = new string((char*)odes).TrimEnd('\0');
                int* rc = (int*)(odes + 64);
                Left = rc[0]; Top = rc[1];

                int* levels = stackalloc int[] { 0xb100, 0xb000 };
                void* dev = null, ctx = null; int got;
                // BGRA_SUPPORT | VIDEO_SUPPORT
                Com.Check(Native.D3D11CreateDevice(ad, 0, 0, 0x20 | 0x800, levels, 2, 7, &dev, &got, &ctx), "D3D11CreateDevice");
                Device = dev; Context = ctx;
                void* mt = Com.TryQI(Context, IID_ID3D11Multithread);
                if (mt != null) { Com.C(mt, 5, 1); Com.Release(ref mt); } // the encoder uses the device from its own threads
                GpuPriority = RaiseGpuPriority(Device);

                op1 = Com.QI(op, IID_IDXGIOutput1);
                void* d = null;
                Com.Check(Com.C(op1, 22, (nint)Device, (nint)(&d)), "DuplicateOutput");
                dupl = d;
                byte* dd = stackalloc byte[36];
                Com.C(dupl, 7, (nint)dd);
                Width = *(int*)dd; Height = *(int*)(dd + 4);
            }
            finally { Com.Release(ref op1); Com.Release(ref op); Com.Release(ref ad); Com.Release(ref f); }

            InitConverter();
        }

        void InitConverter()
        {
            vdev = Com.QI(Device, IID_ID3D11VideoDevice);
            vctx = Com.QI(Context, IID_ID3D11VideoContext);
            uint* cd = stackalloc uint[10];
            // content desc: progressive, 60/1 in, W, H, 60/1 out, W, H, usage optimal speed
            cd[0] = 0; cd[1] = 60; cd[2] = 1; cd[3] = (uint)Width; cd[4] = (uint)Height; cd[5] = 60; cd[6] = 1; cd[7] = (uint)Width; cd[8] = (uint)Height; cd[9] = 1;
            void* e = null, p = null;
            Com.Check(Com.C(vdev, 10, (nint)cd, (nint)(&e)), "CreateVideoProcessorEnumerator");
            vpEnum = e;
            Com.Check(Com.C(vdev, 4, (nint)vpEnum, 0, (nint)(&p)), "CreateVideoProcessor");
            vp = p;

            // Private copy of the desktop image: the duplicated frame can be released immediately.
            var td = new Tex2DDesc { Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = 87 /*BGRA*/, SampleCount = 1, BindFlags = 0x20 | 0x8 };
            void* t = null;
            Com.Check(Com.C(Device, 5, (nint)(&td), 0, (nint)(&t)), "CreateTexture2D BGRA");
            copy = t;
            uint* ivd = stackalloc uint[4]; ivd[0] = 0; ivd[1] = 1; ivd[2] = 0; ivd[3] = 0;
            void* iv = null;
            Com.Check(Com.C(vdev, 8, (nint)copy, (nint)vpEnum, (nint)ivd, (nint)(&iv)), "CreateVideoProcessorInputView");
            inView = iv;

            Ring = new void*[RingSize]; outViews = new void*[RingSize];
            var nd = new Tex2DDesc { Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = 103 /*NV12*/, SampleCount = 1, BindFlags = 0x20 | 0x400 /*VIDEO_ENCODER*/ };
            uint* ovd = stackalloc uint[4]; ovd[0] = 1; ovd[1] = 0; ovd[2] = 0; ovd[3] = 0;
            for (int i = 0; i < RingSize; i++)
            {
                void* n = null, ov = null;
                if (Com.C(Device, 5, (nint)(&nd), 0, (nint)(&n)) < 0)
                {
                    nd.BindFlags = 0x20; // some drivers refuse BIND_VIDEO_ENCODER on render targets
                    Com.Check(Com.C(Device, 5, (nint)(&nd), 0, (nint)(&n)), "CreateTexture2D NV12");
                }
                Ring[i] = n;
                Com.Check(Com.C(vdev, 9, (nint)n, (nint)vpEnum, (nint)ovd, (nint)(&ov)), "CreateVideoProcessorOutputView");
                outViews[i] = ov;
            }
            // Colour: full-range RGB in, BT.709 limited-range YCbCr out (D3D11_VIDEO_PROCESSOR_COLOR_SPACE bit fields).
            uint inCs = 0, outCs = 0x4 | 0x10;
            Com.C(vctx, 28, (nint)vp, 0, (nint)(&inCs));
            Com.C(vctx, 15, (nint)vp, (nint)(&outCs));
            Com.C(vctx, 27, (nint)vp, 0, 0);       // progressive
            Com.C(vctx, 37, (nint)vp, 0, 0);       // no auto processing (denoise/enhancement)
        }

        /// <summary>
        /// Waits up to timeoutMs for a new desktop image. On success, converts it into Ring[slot] and returns true
        /// with the QPC time it was acquired. Returns false on timeout or a mouse-only update.
        /// Throws on access loss (mode change, secure desktop): the caller recreates the capture.
        /// </summary>
        public bool Next(int timeoutMs, int slot, out long acquiredQpc, out long presentQpc)
        {
            acquiredQpc = presentQpc = 0;
            FrameInfo fi; void* res = null;
            int hr = Com.C(dupl, 8, (nint)timeoutMs, (nint)(&fi), (nint)(&res));
            if (hr == unchecked((int)0x887A0027)) return false; // DXGI_ERROR_WAIT_TIMEOUT
            Com.Check(hr, "AcquireNextFrame");
            acquiredQpc = Stopwatch.GetTimestamp();
            try
            {
                if (fi.LastPresentTime == 0) return false; // only the pointer moved
                presentQpc = fi.LastPresentTime;
                void* tex = Com.QI(res, IID_ID3D11Texture2D);
                Com.C(Context, 47, (nint)copy, (nint)tex); // CopyResource
                Com.Release(ref tex);
            }
            finally { Com.Release(ref res); Com.C(dupl, 14); }
            Convert(slot);
            return true;
        }

        void Convert(int slot)
        {
            var s = new VpStream { Enable = 1, pInput = inView };
            Com.Check(Com.C(vctx, 53, (nint)vp, (nint)outViews[slot], 0, 1, (nint)(&s)), "VideoProcessorBlt");
            if (FlushAfterConvert) Com.C(Context, 111); // start the GPU work now, not when the encoder next touches the context
        }

        public void Dispose()
        {
            if (outViews != null) for (int i = 0; i < RingSize; i++) { Com.Release(ref outViews[i]); Com.Release(ref Ring[i]); }
            Com.Release(ref inView); Com.Release(ref copy); Com.Release(ref vp); Com.Release(ref vpEnum);
            Com.Release(ref vctx); Com.Release(ref vdev); Com.Release(ref dupl);
            Com.Release(ref Context); Com.Release(ref Device);
        }
    }

    /// <summary>
    /// Media Foundation hardware encoder MFT (NVENC / AMF / QSV behind one API), asynchronous, low-latency mode,
    /// no B-frames, fed with D3D11 NV12 textures. Outputs Annex-B access units.
    /// </summary>
    sealed unsafe class MfEncoder : IDisposable
    {
        static readonly Guid IID_IMFTransform = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");
        static readonly Guid IID_IMFMediaEventGenerator = new Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D");
        static readonly Guid IID_ICodecAPI = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        static readonly Guid IID_IMFShutdown = new Guid("97ec2ea4-0e42-4937-97ac-9d6d328824e1");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a");
        static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        static readonly Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_HEVC = new Guid("43564548-0000-0010-8000-00AA00389B71");
        static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        static readonly Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        static readonly Guid MF_MT_MPEG2_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        static readonly Guid MF_MT_YUV_MATRIX = new Guid("3e23d450-2c75-4d25-a00e-b91670d12327");
        static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11");
        static readonly Guid MF_MT_VIDEO_PRIMARIES = new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
        static readonly Guid MF_MT_TRANSFER_FUNCTION = new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93");
        static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new Guid("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        static readonly Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        static readonly Guid MFT_FRIENDLY_NAME_Attribute = new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3");
        static readonly Guid MFT_ENUM_ADAPTER_LUID = new Guid("1d39518c-e220-4da8-a07f-ba172552d6b1");
        static readonly Guid MFSampleExtension_CleanPoint = new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
        static readonly Guid CODECAPI_AVLowLatencyMode = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        static readonly Guid CODECAPI_AVEncCommonRateControlMode = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");
        static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new Guid("f7222374-2144-4815-b550-a37f8e12ee52");
        static readonly Guid CODECAPI_AVEncCommonMaxBitRate = new Guid("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
        static readonly Guid CODECAPI_AVEncCommonBufferSize = new Guid("0db96574-b6a4-4c8b-8106-3773de0310cd");
        static readonly Guid CODECAPI_AVEncMPVGOPSize = new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new Guid("8d390aac-dc5c-4200-b57f-814d04babab2");
        static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new Guid("398c1b98-8353-475a-9ef2-8f265d260345");
        static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new Guid("98332df8-03cd-476b-89fa-3f9e442dec9f");

        const int METransformNeedInput = 601, METransformHaveOutput = 602;
        const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

        public string Name;
        /// <summary>0 = fastest .. 100 = best quality (CODECAPI_AVEncCommonQualityVsSpeed).</summary>
        public static uint QualityVsSpeed = 50;
        /// <summary>
        /// eAVEncCommonRateControlMode: 0 CBR, 1 peak-constrained VBR, 4 low-delay VBR. CBR: measured on the NVIDIA
        /// MFT, it is the only mode that applies live bitrate changes and stays under the cap (the VBR modes ignored
        /// a halved target and overshot the peak by 10-15 %). It does not pad: a static desktop costs little.
        /// </summary>
        public static uint RateControl = 0;
        /// <summary>
        /// CODECAPI_AVEncCommonBufferSize is in BYTES on the NVIDIA MFT: as bits, the 1/6 s VBV became 1.3 s and a
        /// still desktop turning into motion burst at ~2x the target for a second (75 Mb/s sent at 35, congestion).
        /// Measured with alternating still/noise content: 33.4 Mb/s peak at 18 (bits) vs 19.7 (bytes).
        /// </summary>
        public static uint BufferDivisor = 8;
        public readonly bool Hevc;
        readonly int width, height, fps;
        void* activate, mft, events, codec, manager;
        bool providesSamples; uint outSize;
        readonly SemaphoreSlim needInput = new SemaphoreSlim(0);
        Thread eventThread;
        volatile bool stopping;
        byte[] buf = new byte[1 << 20];

        /// <summary>(packet buffer, Annex-B length (AU starts at offset 5), keyframe, sample time in 100 ns)</summary>
        public Action<byte[], int, bool, long> OnOutput;
        public Action<Exception> OnError;
        /// <summary>When the current output's METransformHaveOutput arrived (valid inside OnOutput).</summary>
        public long EventQpc;

        [StructLayout(LayoutKind.Sequential)]
        struct OutputBuffer { public uint StreamId; public void* Sample; public uint Status; public void* Events; }

        public MfEncoder(DesktopCapture cap, bool hevc, int fps, int kbps, int gop, uint? qualityVsSpeed = null)
        {
            Native.StartMF();
            Hevc = hevc; width = cap.Width; height = cap.Height; this.fps = fps;
            var inT = new Native.RegType { Major = MFMediaType_Video, Sub = MFVideoFormat_NV12 };
            var outT = new Native.RegType { Major = MFMediaType_Video, Sub = hevc ? MFVideoFormat_HEVC : MFVideoFormat_H264 };
            void* attrs = null; void** list = null; uint count = 0;
            Com.Check(Native.MFCreateAttributes(&attrs, 1), "MFCreateAttributes");
            long luid = cap.AdapterLuid;
            Guid k = MFT_ENUM_ADAPTER_LUID;
            Com.C(attrs, 26, (nint)(&k), (nint)(&luid), 8); // SetBlob: only encoders of the capture GPU
            try
            {
                // HARDWARE | ASYNCMFT | SORTANDFILTER
                Com.Check(Native.MFTEnum2(MFT_CATEGORY_VIDEO_ENCODER, 0x4 | 0x2 | 0x40, &inT, &outT, attrs, &list, &count), "MFTEnum2");
                if (count == 0) throw new NotSupportedException($"aucun encodeur matériel {(hevc ? "HEVC" : "H.264")} sur ce GPU");
                activate = list[0];
                for (uint i = 1; i < count; i++) { void* x = list[i]; Com.Release(ref x); }
            }
            finally { if (list != null) Native.CoTaskMemFree((nint)list); Com.Release(ref attrs); }

            Name = Com.GetString(activate, MFT_FRIENDLY_NAME_Attribute) ?? "MFT";
            drainEachFrame = AllowDrain && Name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
            void* t = null; Guid iid = IID_IMFTransform;
            Com.Check(Com.C(activate, 33, (nint)(&iid), (nint)(&t)), "ActivateObject");
            mft = t;
            void* a = null;
            Com.Check(Com.C(mft, 8, (nint)(&a)), "GetAttributes");
            Com.SetU32(a, MF_TRANSFORM_ASYNC_UNLOCK, 1);
            Com.SetU32(a, MF_LOW_LATENCY, 1);
            Com.Release(ref a);
            events = Com.QI(mft, IID_IMFMediaEventGenerator);
            codec = Com.QI(mft, IID_ICodecAPI);

            uint token; void* m = null;
            Com.Check(Native.MFCreateDXGIDeviceManager(&token, &m), "MFCreateDXGIDeviceManager");
            manager = m;
            Com.Check(Com.C(manager, 7, (nint)cap.Device, (nint)token), "ResetDevice");
            Com.Check(Com.C(mft, 23, 2 /*SET_D3D_MANAGER*/, (nint)manager), "SET_D3D_MANAGER");

            // Rate control and latency, before the media types (some encoders read them at type negotiation).
            Com.SetCodec(codec, CODECAPI_AVLowLatencyMode, 1, true);
            Com.SetCodec(codec, CODECAPI_AVEncMPVDefaultBPictureCount, 0);
            Com.SetCodec(codec, CODECAPI_AVEncMPVGOPSize, (uint)gop);
            Com.SetCodec(codec, CODECAPI_AVEncCommonRateControlMode, RateControl);
            Com.SetCodec(codec, CODECAPI_AVEncCommonQualityVsSpeed, qualityVsSpeed ?? QualityVsSpeed);
            SetBitrate(kbps);

            void* ot = null;
            Com.Check(Native.MFCreateMediaType(&ot), "MFCreateMediaType");
            Com.SetGuid(ot, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            Com.SetGuid(ot, MF_MT_SUBTYPE, hevc ? MFVideoFormat_HEVC : MFVideoFormat_H264);
            Com.SetU64(ot, MF_MT_FRAME_SIZE, (ulong)width << 32 | (uint)height);
            Com.SetU64(ot, MF_MT_FRAME_RATE, (ulong)fps << 32 | 1);
            Com.SetU64(ot, MF_MT_PIXEL_ASPECT_RATIO, 1UL << 32 | 1);
            Com.SetU32(ot, MF_MT_INTERLACE_MODE, 2);
            Com.SetU32(ot, MF_MT_AVG_BITRATE, (uint)kbps * 1000);
            Com.SetU32(ot, MF_MT_MPEG2_PROFILE, hevc ? 1u /*Main*/ : 100u /*High*/);
            SetColour(ot);
            Com.Check(Com.C(mft, 16, 0, (nint)ot, 0), "SetOutputType");
            Com.Release(ref ot);

            void* it = null;
            Com.Check(Native.MFCreateMediaType(&it), "MFCreateMediaType");
            Com.SetGuid(it, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            Com.SetGuid(it, MF_MT_SUBTYPE, MFVideoFormat_NV12);
            Com.SetU64(it, MF_MT_FRAME_SIZE, (ulong)width << 32 | (uint)height);
            Com.SetU64(it, MF_MT_FRAME_RATE, (ulong)fps << 32 | 1);
            Com.SetU64(it, MF_MT_PIXEL_ASPECT_RATIO, 1UL << 32 | 1);
            Com.SetU32(it, MF_MT_INTERLACE_MODE, 2);
            SetColour(it);
            Com.Check(Com.C(mft, 15, 0, (nint)it, 0), "SetInputType");
            Com.Release(ref it);

            uint* si = stackalloc uint[3];
            Com.Check(Com.C(mft, 7, 0, (nint)si), "GetOutputStreamInfo");
            providesSamples = (si[0] & 0x100) != 0;
            outSize = Math.Max(si[1], (uint)(width * height));

            Com.Check(Com.C(mft, 23, 0x10000000 /*BEGIN_STREAMING*/, 0), "BEGIN_STREAMING");
            Com.Check(Com.C(mft, 23, 0x10000003 /*START_OF_STREAM*/, 0), "START_OF_STREAM");
            eventThread = new Thread(EventLoop) { IsBackground = true, Name = "MF events", Priority = ThreadPriority.Highest };
            eventThread.Start();
        }

        static void SetColour(void* t)
        {
            Com.SetU32(t, MF_MT_YUV_MATRIX, 1);          // BT.709
            Com.SetU32(t, MF_MT_VIDEO_PRIMARIES, 2);     // BT.709
            Com.SetU32(t, MF_MT_TRANSFER_FUNCTION, 5);   // BT.709
            Com.SetU32(t, MF_MT_VIDEO_NOMINAL_RANGE, 2); // 16-235
        }

        /// <summary>Live bitrate change (no restart): CBR at 90 % of the cap, VBV of 1/6 s (largest frame).</summary>
        public void SetBitrate(int kbps)
        {
            Com.SetCodec(codec, CODECAPI_AVEncCommonMeanBitRate, (uint)kbps * (RateControl == 0 ? 900u : 750u));
            Com.SetCodec(codec, CODECAPI_AVEncCommonMaxBitRate, (uint)kbps * 1000);
            Com.SetCodec(codec, CODECAPI_AVEncCommonBufferSize, (uint)kbps * 1000 / 6 / BufferDivisor);
        }

        public string Diag() => $"rc={Com.GetCodec(codec, CODECAPI_AVEncCommonRateControlMode)} mean={Com.GetCodec(codec, CODECAPI_AVEncCommonMeanBitRate)} max={Com.GetCodec(codec, CODECAPI_AVEncCommonMaxBitRate)} buf={Com.GetCodec(codec, CODECAPI_AVEncCommonBufferSize)} lowlat={Com.GetCodec(codec, CODECAPI_AVLowLatencyMode)} refus:{(Com.CodecErrors == "" ? " aucun" : Com.CodecErrors)}";

        /// <summary>Live speed/quality trade-off (0 fastest .. 100 best): game / fluid mode without a restart.</summary>
        public void SetQualityVsSpeed(uint q) => Com.SetCodec(codec, CODECAPI_AVEncCommonQualityVsSpeed, q);

        /// <summary>The next submitted frame becomes an IDR.</summary>
        public void ForceKeyFrame() => Com.SetCodec(codec, CODECAPI_AVEncVideoForceKeyFrame, 1);

        /// <summary>
        /// The NVIDIA MFT keeps each encoded frame until the next input arrives (+1 frame period: encode 16 ms p50
        /// instead of 3 ms, bench RTX 5080 at 5120x1440; 4K stream 2 ms). A drain after every frame makes it emit at once
        /// (bitrate, frame sizes and keyframes unchanged), BUT about once a minute to once in 10 min the driver holds
        /// a drained frame exactly 5 s: 5 s freezes in 1.1.3-1.1.4, also on the TV side. The stuck call is inside
        /// the driver, so a watchdog cannot cut it short. Off by default; bench option "drain" to try it again.
        /// </summary>
        public static bool AllowDrain = false;
        bool drainEachFrame;

        /// <summary>Encoder ready for one more frame within waitMs?</summary>
        public bool WaitInput(int waitMs) => needInput.Wait(waitMs);

        /// <summary>Queues one NV12 texture (call only after WaitInput returned true).</summary>
        public void Submit(void* nv12, long time100ns)
        {
            void* b = null, s = null;
            Guid iid = IID_ID3D11Texture2D;
            Com.Check(Native.MFCreateDXGISurfaceBuffer(&iid, nv12, 0, 0, &b), "MFCreateDXGISurfaceBuffer");
            try
            {
                uint max;
                if (Com.C(b, 7, (nint)(&max)) >= 0) Com.C(b, 6, (nint)max);
                Com.Check(Native.MFCreateSample(&s), "MFCreateSample");
                Com.C(s, 42, (nint)b);
                Com.C(s, 36, (nint)time100ns);
                Com.C(s, 38, (nint)(10000000L / fps));
                Com.Check(Com.C(mft, 24, 0, (nint)s, 0), "ProcessInput");
                if (drainEachFrame) Com.C(mft, 23, 1 /*COMMAND_DRAIN*/, 0);
            }
            finally { Com.Release(ref s); Com.Release(ref b); }
        }

        void EventLoop()
        {
            Native.CoInitializeEx(0, 0);
            try
            {
                while (!stopping)
                {
                    void* ev = null;
                    if (Com.C(events, 3, 0, (nint)(&ev)) < 0) break; // MF_E_SHUTDOWN on Dispose
                    int type = 0;
                    Com.C(ev, 33, (nint)(&type));
                    Com.Release(ref ev);
                    if (type == METransformNeedInput) needInput.Release();
                    else if (type == METransformHaveOutput) { EventQpc = Stopwatch.GetTimestamp(); Drain(); }
                    else if (type == 603 /*METransformDrainComplete*/ && drainEachFrame) Com.C(mft, 23, 0x10000003 /*NOTIFY_START_OF_STREAM*/, 0); // drained: accept input again
                }
            }
            catch (Exception e) { if (!stopping) OnError?.Invoke(e); }
        }

        void Drain()
        {
            var ob = new OutputBuffer();
            void* own = null, ownBuf = null;
            if (!providesSamples)
            {
                Com.Check(Native.MFCreateSample(&own), "MFCreateSample");
                Com.Check(Native.MFCreateMemoryBuffer(outSize, &ownBuf), "MFCreateMemoryBuffer");
                Com.C(own, 42, (nint)ownBuf);
                ob.Sample = own;
            }
            uint status;
            int hr = Com.C(mft, 25, 0, 1, (nint)(&ob), (nint)(&status));
            try
            {
                if (ob.Events != null) { void* e = ob.Events; Com.Release(ref e); }
                if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    void* nt = null;
                    if (Com.C(mft, 14, 0, 0, (nint)(&nt)) >= 0) { Com.C(mft, 16, 0, (nint)nt, 0); Com.Release(ref nt); }
                    return;
                }
                if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                Com.Check(hr, "ProcessOutput");
                void* s = ob.Sample;
                long time; Com.C(s, 35, (nint)(&time));
                bool key = Com.GetU32(s, MFSampleExtension_CleanPoint, 0) != 0;
                void* mb = null;
                Com.Check(Com.C(s, 41, (nint)(&mb)), "ConvertToContiguousBuffer");
                byte* p; uint max, len;
                Com.Check(Com.C(mb, 3, (nint)(&p), (nint)(&max), (nint)(&len)), "Lock");
                try
                {
                    if (buf.Length < len + 5) buf = new byte[len * 2 + 5];
                    Marshal.Copy((IntPtr)p, buf, 5, (int)len);
                }
                finally { Com.C(mb, 4); Com.Release(ref mb); }
                OnOutput?.Invoke(buf, (int)len, key, time);
            }
            finally
            {
                if (ob.Sample != null && ob.Sample != own) { void* s = ob.Sample; Com.Release(ref s); }
                Com.Release(ref ownBuf); Com.Release(ref own);
            }
        }

        public void Dispose()
        {
            stopping = true;
            if (mft != null)
            {
                Com.C(mft, 23, 0 /*FLUSH*/, 0);
                Com.C(mft, 23, 0x10000001 /*END_STREAMING*/, 0);
                void* sd = Com.TryQI(mft, IID_IMFShutdown);
                if (sd != null) { Com.C(sd, 3); Com.Release(ref sd); }
            }
            eventThread?.Join(2000);
            Com.Release(ref codec); Com.Release(ref events); Com.Release(ref mft);
            if (activate != null) { Com.C(activate, 34); Com.Release(ref activate); }
            Com.Release(ref manager);
        }
    }

    /// <summary>
    /// Capture + encode loop shared by the streamer and the bench. A new desktop image is encoded as soon as it
    /// arrives (no pipelining: that was ffmpeg's -delay 2), at most once per frame period: faster sources keep only
    /// the latest image. Without a new image the last one is encoded again at the frame rate, like ddagrab:
    /// steady fps for the TV and a static desktop that keeps sharpening.
    /// </summary>
    sealed unsafe class NativeSession : IDisposable
    {
        public readonly DesktopCapture Cap;
        public readonly MfEncoder Enc;
        readonly int fps;
        readonly System.Collections.Concurrent.ConcurrentDictionary<long, long[]> inflight = new System.Collections.Concurrent.ConcurrentDictionary<long, long[]>();
        volatile bool stop, keyRequested;
        volatile int newKbps;
        Exception error;
        public int NewFrames, Repeats, Dropped;
        public static bool FineTimer = true;

        /// <summary>(buffer, Annex-B length at offset 5, keyframe, acquired QPC, submitted QPC, new image?)</summary>
        public Action<byte[], int, bool, long, long, bool> OnPacket;

        public NativeSession(int adapter, int output, bool hevc, int fps, int kbps, uint? qualityVsSpeed = null)
        {
            Native.CoInitializeEx(0, 0); // MTA (already-initialised threads just keep their apartment)
            // AcquireNextFrame's timeout is rounded to the system timer tick (15.6 ms by default): the repeated
            // images of a still desktop then came late (measured 46-55 fps instead of 60). ffmpeg does the same.
            if (FineTimer) Native.timeBeginPeriod(1);
            Cap = new DesktopCapture(adapter, output);
            try { Enc = new MfEncoder(Cap, hevc, fps, kbps, fps * 120, qualityVsSpeed); }
            catch { Cap.Dispose(); if (FineTimer) Native.timeEndPeriod(1); throw; }
            this.fps = fps;
            // Packets leave on their own thread: a socket write that blocks must never stop the encoder's event
            // loop (no METransformNeedInput handled -> captured images dropped: measured 55-59 fps on the TV link).
            Enc.OnOutput = (b, len, key, t) =>
            {
                if (!inflight.TryRemove(t, out var i)) return;
                var copy = Rent(len + 5);
                Buffer.BlockCopy(b, 5, copy, 5, len);
                try { outQueue.Add(new Packet { Buf = copy, Len = len, Key = key, Acquired = i[0], Submitted = i[1], Fresh = i[2] == 1, Event = Enc.EventQpc }); }
                catch (InvalidOperationException) { } // session closing: the queue no longer takes packets
            };
            Enc.OnError = e => { error = e; stop = true; };
            sender = new Thread(SendLoop) { IsBackground = true, Name = "native send", Priority = ThreadPriority.AboveNormal };
            sender.Start();
        }

        struct Packet { public byte[] Buf; public int Len; public bool Key, Fresh; public long Acquired, Submitted, Event; }

        // Where the time goes, per frame (QPC ticks summed since the last Stages() call).
        long stHold, stEncode, stQueue, stSend, stBytes, stMaxTotal; int stFresh, stAll;
        readonly object stLock = new object();

        /// <summary>
        /// Average ms per stage since the last call: hold (captured -> submitted, new images only), encode
        /// (submitted -> encoder output: GPU conversion + encoding), queue (-> sender thread), send (socket write).
        /// </summary>
        public string Stages()
        {
            lock (stLock)
            {
                double f = 1000.0 / Stopwatch.Frequency;
                string r = stAll == 0 ? "no frame" : FormattableString.Invariant(
                    $"hold {stHold * f / Math.Max(1, stFresh):0.0} ms, encode {stEncode * f / stAll:0.0}, queue {stQueue * f / stAll:0.0}, send {stSend * f / stAll:0.0}, total max {stMaxTotal * f:0}, {stBytes / stAll / 1024} KB/frame");
                stHold = stEncode = stQueue = stSend = stBytes = stMaxTotal = 0; stFresh = stAll = 0;
                return r;
            }
        }
        readonly System.Collections.Concurrent.BlockingCollection<Packet> outQueue = new System.Collections.Concurrent.BlockingCollection<Packet>(8);
        // ~130 ms: when the link stalls (Wi-Fi hiccup) new captures are skipped before encoding (safe for the
        // stream) instead of 0.5 s of stale frames being replayed in a burst afterwards (87 fps measured).
        readonly Thread sender;
        readonly System.Collections.Concurrent.ConcurrentBag<byte[]> pool = new System.Collections.Concurrent.ConcurrentBag<byte[]>();

        byte[] Rent(int size)
        {
            // A buffer too small for this frame is dropped: the pool converges on keyframe-sized buffers.
            while (pool.TryTake(out var b)) if (b.Length >= size) return b;
            return new byte[Math.Max(size, 256 << 10)];
        }

        void SendLoop()
        {
            try
            {
                foreach (var p in outQueue.GetConsumingEnumerable())
                {
                    long dq = Stopwatch.GetTimestamp();
                    OnPacket?.Invoke(p.Buf, p.Len, p.Key, p.Acquired, p.Submitted, p.Fresh);
                    long sent = Stopwatch.GetTimestamp();
                    if (pool.Count < 40) pool.Add(p.Buf); // reused: no 90 KB allocation per frame
                    lock (stLock)
                    {
                        if (p.Fresh) { stHold += p.Submitted - p.Acquired; stFresh++; }
                        stEncode += p.Event - p.Submitted; stQueue += dq - p.Event; stSend += sent - dq;
                        stBytes += p.Len; stAll++;
                        if (p.Fresh) stMaxTotal = Math.Max(stMaxTotal, sent - p.Acquired);
                    }
                }
            }
            catch (Exception e) { error = error ?? e; stop = true; }
        }

        /// <summary>Thread-safe: applied by the capture loop before its next frame.</summary>
        public void SetBitrate(int kbps) => newKbps = kbps;
        public void SetQualityVsSpeed(uint q) => newQvs = (int)q + 1;
        volatile int newQvs;
        public void ForceKeyFrame() => keyRequested = true;
        public void Stop() => stop = true;

        /// <summary>
        /// Runs until Stop(), the deadline (QPC) or an error (thrown). Time is cut into slots of one frame period,
        /// each sending exactly one image: a new image goes out the moment it is captured if its slot is still
        /// free (otherwise at the start of the next slot); a slot that ends without one sends the last image again.
        /// </summary>
        public void Run(long untilQpc = long.MaxValue)
        {
            long freq = Stopwatch.Frequency, period = freq / fps;
            int periodMs = (int)Math.Max(1, period * 1000 / freq);
            long start = Stopwatch.GetTimestamp(), slotEnd = start + period;
            int slot = 0;
            bool have = false, held = false, used = false;
            long heldAcq = 0;
            while (!stop && Stopwatch.GetTimestamp() < untilQpc)
            {
                long now = Stopwatch.GetTimestamp();
                int wait = (int)Math.Max(0, (slotEnd - now) * 1000 / freq);
                int writeSlot = held ? slot : (slot + 1) % Cap.RingSize;
                bool got = Cap.Next(wait, writeSlot, out long acq, out _);
                if (got)
                {
                    slot = writeSlot; held = true; heldAcq = acq; have = true;
                    NewFrames++;
                }
                now = Stopwatch.GetTimestamp();
                bool send = false, fresh = false;
                if (now >= slotEnd)
                {
                    bool repeat = !used && !held && have;     // the slot that just ended sent nothing
                    slotEnd = now - slotEnd > period ? now + period : slotEnd + period; // resync after a stall
                    used = false;
                    if (held) { send = fresh = true; used = true; } // image held back: it opens the new slot
                    else if (repeat) send = true;                   // belongs to the past slot: new one stays free
                }
                else if (held && !used) { send = fresh = true; used = true; }
                if (!send) continue;
                if (!Enc.WaitInput(periodMs)) { Dropped++; held = false; continue; }
                int k = newKbps;
                if (k != 0) { newKbps = 0; Enc.SetBitrate(k); }
                int qv = newQvs;
                if (qv != 0) { newQvs = 0; Enc.SetQualityVsSpeed((uint)(qv - 1)); }
                if (keyRequested) { keyRequested = false; Enc.ForceKeyFrame(); }
                now = Stopwatch.GetTimestamp();
                long t100 = (now - start) * 10000000L / freq;
                inflight[t100] = new[] { fresh ? heldAcq : now, now, fresh ? 1L : 0L };
                Enc.Submit(Cap.Ring[slot], t100);
                if (fresh) held = false; else Repeats++;
            }
            if (error is System.IO.IOException || error is ObjectDisposedException)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); // the TV is gone
            if (error != null) throw new Exception("encodeur : " + error.Message, error);
        }

        public void Dispose()
        {
            stop = true;
            // Before the encoder: its event thread may be blocked adding to the full queue (TV gone, sender stopped).
            // Released only afterwards, it resumed on a disposed MFT: access violation in ntdll (1.1.42, queue of 8).
            outQueue.CompleteAdding();
            Enc?.Dispose();
            if (Thread.CurrentThread != sender) sender.Join(1000);
            Cap.Dispose();
            if (FineTimer) Native.timeEndPeriod(1);
        }
    }
}
