using System.Runtime.InteropServices;

namespace WaveGenFS;

/// <summary>winmm waveOut による連続再生 (CALLBACK_EVENT + 複数バッファ)</summary>
public unsafe sealed class WaveOutPlayer : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatExt
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WaveOutCaps
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels, wReserved1;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WaveFormatExt fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, WaveHdr* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, WaveHdr* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, WaveHdr* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll")] static extern int waveOutGetVolume(IntPtr h, out uint vol);
    [DllImport("winmm.dll")] static extern int waveOutSetVolume(IntPtr h, uint vol);
    uint savedVol; bool volChanged;
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")]
    static extern int waveOutGetDevCaps(UIntPtr id, ref WaveOutCaps caps, int size);

    const uint CALLBACK_EVENT = 0x00050000;
    const uint WHDR_DONE = 1;
    const uint WAVE_MAPPER = 0xFFFFFFFF;
    const int NBuf = 4;

    public static List<string> Devices()
    {
        var l = new List<string>();
        int n = waveOutGetNumDevs();
        for (int i = 0; i < n; i++)
        {
            var c = new WaveOutCaps();
            l.Add(waveOutGetDevCaps((UIntPtr)i, ref c, Marshal.SizeOf<WaveOutCaps>()) == 0 ? c.szPname : $"Device {i}");
        }
        return l;
    }

    IntPtr h;
    readonly AutoResetEvent ev = new(false);
    Thread thread;
    volatile bool running;
    WaveHdr*[] hdrs = new WaveHdr*[NBuf];
    public Synth Synth { get; private set; }

    /// <param name="device">-1 = 既定のデバイス</param>
    public void Start(Synth synth, int bits, int ch, int device, int latencyMs, bool fullVolume = false)
    {
        Stop();
        Synth = synth;
        int bps = Synth.BytesPerSample(bits), block = bps * ch;
        bool ext = bits > 16;
        var fmt = new WaveFormatExt
        {
            wFormatTag = (ushort)(ext ? 0xFFFE : 1),
            nChannels = (ushort)ch,
            nSamplesPerSec = (uint)synth.Fs,
            nAvgBytesPerSec = (uint)(synth.Fs * block),
            nBlockAlign = (ushort)block,
            wBitsPerSample = (ushort)(bps * 8),
            cbSize = (ushort)(ext ? 22 : 0),
            wValidBitsPerSample = (ushort)bits,
            dwChannelMask = ch == 1 ? 4u : 3u,
            SubFormat = bits == 32 ? new Guid("00000003-0000-0010-8000-00aa00389b71")
                                   : new Guid("00000001-0000-0010-8000-00aa00389b71"),
        };
        int r = waveOutOpen(out h, device < 0 ? WAVE_MAPPER : (uint)device, ref fmt,
                            ev.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
        if (r != 0) { h = IntPtr.Zero; throw new InvalidOperationException($"waveOutOpen 失敗 (MMRESULT={r})。このデバイスは {synth.Fs}Hz/{bits}bit/{ch}ch に対応していない可能性があります。"); }

        // Volume最大: 再生中だけデバイス(アプリ)の音量を最大にし、停止時に戻す
        volChanged = false;
        if (fullVolume && waveOutGetVolume(h, out savedVol) == 0)
            volChanged = waveOutSetVolume(h, 0xFFFFFFFF) == 0;

        int frames = Math.Max(256, synth.Fs * latencyMs / 1000 / NBuf);
        int bytes = frames * block;
        for (int i = 0; i < NBuf; i++)
        {
            var p = (WaveHdr*)Marshal.AllocHGlobal(sizeof(WaveHdr));
            *p = default;
            p->lpData = Marshal.AllocHGlobal(bytes);
            p->dwBufferLength = (uint)bytes;
            waveOutPrepareHeader(h, p, sizeof(WaveHdr));
            hdrs[i] = p;
        }
        running = true;
        thread = new Thread(() => Loop(frames, ch, bits, bytes)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "waveOut" };
        thread.Start();
    }

    void Loop(int frames, int ch, int bits, int bytes)
    {
        var L = new float[frames]; var R = new float[frames]; var buf = new byte[bytes];
        var queued = new bool[NBuf];
        while (running)
        {
            for (int i = 0; i < NBuf && running; i++)
            {
                var p = hdrs[i];
                if (queued[i] && (p->dwFlags & WHDR_DONE) == 0) continue;
                Synth.Render(L, R, frames);
                Synth.Encode(L, R, frames, ch, bits, buf);
                Marshal.Copy(buf, 0, p->lpData, bytes);
                waveOutWrite(h, p, sizeof(WaveHdr));
                queued[i] = true;
            }
            ev.WaitOne(50);
        }
    }

    public bool IsPlaying => running;

    public void Stop()
    {
        if (h == IntPtr.Zero) return;
        running = false;
        thread?.Join();
        thread = null;
        waveOutReset(h);
        if (volChanged) { waveOutSetVolume(h, savedVol); volChanged = false; }
        for (int i = 0; i < NBuf; i++)
        {
            if (hdrs[i] == null) continue;
            waveOutUnprepareHeader(h, hdrs[i], sizeof(WaveHdr));
            Marshal.FreeHGlobal(hdrs[i]->lpData);
            Marshal.FreeHGlobal((IntPtr)hdrs[i]);
            hdrs[i] = null;
        }
        waveOutClose(h);
        h = IntPtr.Zero;
    }

    public void Dispose() { Stop(); ev.Dispose(); }
}
