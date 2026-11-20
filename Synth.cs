namespace WaveGenFS;

public enum WaveType { Off, Sine, Square, Triangle, Saw, Pulse, WhiteNoise, PinkNoise, PulsePM, Mls }
public enum OutCh { LR, L, R, LmR }
public enum ModType { AM, FM }

/// <summary>1系統分の波形パラメータ</summary>
public class WaveParams
{
    public WaveType Type { get; set; } = WaveType.Off;
    public double Freq { get; set; } = 1000;
    public double AmpDb { get; set; } = -10;
    /// <summary>極性反転 (振幅を 値/% 単位で負にした場合)</summary>
    public bool Invert { get; set; }
    public string FreqUnit { get; set; } = "Hz";     // Hz / Sa
    public string AmpUnit { get; set; } = "dB";      // dB / value / percent
    public double OffsetPct { get; set; }
    public double PhaseDeg { get; set; }
    public int DelaySamples { get; set; }
    public OutCh Ch { get; set; } = OutCh.LR;
    public double Duty { get; set; } = 50;
    public bool BandLimit { get; set; }

    public bool Gate { get; set; }
    public double GateOnMs { get; set; } = 100;
    public double GateOffMs { get; set; } = 100;

    public bool Sweep { get; set; }
    public double SweepEndFreq { get; set; } = 10000;
    public double SweepTimeSec { get; set; } = 10;
    public bool SweepLog { get; set; } = true;
    public bool SweepPingPong { get; set; }

    public bool Mod { get; set; }
    public ModType ModType { get; set; } = ModType.AM;
    public double ModFreq { get; set; } = 10;
    public double ModDepth { get; set; } = 50;   // AM: %, FM: 偏移 Hz

    /// <summary>この波形単体の最大瞬間値 (フルスケール=1)</summary>
    public double PeakLin => Type == WaveType.Off ? 0 : AmpLin * AmFactor + Math.Abs(OffsetPct) / 100;
    public double AmpLin => Math.Pow(10, AmpDb / 20);
    public double AmFactor => Mod && ModType == ModType.AM ? 1 + ModDepth / 100 : 1;
    /// <summary>出力先 [L, R] (モノラル時は L のみ)</summary>
    public bool[] Feeds(bool mono) => mono ? new[] { true, false } :
        new[] { Ch != OutCh.R, Ch != OutCh.L };

    public WaveParams Clone() => (WaveParams)MemberwiseClone();

    public static readonly string[] TypeNames =
        { "OFF", "サイン波", "矩形波", "三角波", "ノコギリ波", "パルス", "ホワイトノイズ", "ピンクノイズ", "パルス(+-)", "M系列ノイズ" };

    public bool IsNoise => Type is WaveType.WhiteNoise or WaveType.PinkNoise or WaveType.Mls;
    public static readonly string[] ChNames = { "L+R", "L", "R", "L,-R" };

    public string Summary()
    {
        if (Type == WaveType.Off) return null;
        string s = $"{TypeNames[(int)Type]} ";
        if (!IsNoise)
            s += Sweep ? $"{FmtHz(Freq)}→{FmtHz(SweepEndFreq)}/{SweepTimeSec:g}s " : $"{FmtHz(Freq)} ";
        s += $"{AmpDb:0.0}dB{(Invert ? "(反転)" : "")} {ChNames[(int)Ch]}";
        if (Mod) s += ModType == ModType.AM ? $" AM {ModFreq:g}Hz {ModDepth:g}%" : $" FM {ModFreq:g}Hz ±{ModDepth:g}Hz";
        if (Gate) s += $" Gate {GateOnMs:g}/{GateOffMs:g}ms";
        return s;
    }

    public static string FmtHz(double f) => f >= 1000 ? $"{f / 1000:0.###}kHz" : $"{f:0.###}Hz";
}

/// <summary>8系統の合成器。Params を差し替えると次のブロックから反映される(位相は連続)。</summary>
public class Synth
{
    public const int NWaves = 8;
    public volatile WaveParams[] Params;
    public readonly int Fs;
    public bool Mono;
    public float PeakL, PeakR;
    /// <summary>出力全体に掛ける係数 (出力減衰 1/N)</summary>
    public volatile float Gain = 1;

    long n;
    readonly State[] st = new State[NWaves];

    class State
    {
        public double Phase, ModPhase;
    }

    // ノイズは全 Wave で同一の時系列 (WaveGene と同じ)
    ulong rng = 0x9E3779B97F4A7C15UL;
    uint lfsr = 1;
    double b0, b1, b2, b3, b4, b5, b6;
    double gaussSpare; bool hasSpare;
    double[] nWhite = Array.Empty<double>(), nPink = Array.Empty<double>(), nMls = Array.Empty<double>();

    double Uniform()
    {
        ulong x = rng; x ^= x << 13; x ^= x >> 7; x ^= x << 17; rng = x;
        return ((x >> 11) + 0.5) * (1.0 / (1UL << 53));
    }

    /// <summary>正規乱数 (Box-Muller)</summary>
    double Gauss()
    {
        if (hasSpare) { hasSpare = false; return gaussSpare; }
        double r = Math.Sqrt(-2 * Math.Log(Uniform())), th = 2 * Math.PI * Uniform();
        gaussSpare = r * Math.Sin(th); hasSpare = true;
        return r * Math.Cos(th);
    }

    void MakeNoise(int frames, bool white, bool pink, bool mls)
    {
        if (nWhite.Length < frames) { nWhite = new double[frames]; nPink = new double[frames]; nMls = new double[frames]; }
        for (int i = 0; i < frames; i++)
        {
            if (white || pink)
            {
                double g = Gauss();
                // ホワイト: 正規乱数を ±3.5σ でリミットし、3.5σ をフルスケールに正規化
                nWhite[i] = Math.Clamp(g, -3.5, 3.5) / 3.5;
                if (pink)
                {
                    b0 = 0.99886 * b0 + g * 0.0555179;
                    b1 = 0.99332 * b1 + g * 0.0750759;
                    b2 = 0.96900 * b2 + g * 0.1538520;
                    b3 = 0.86650 * b3 + g * 0.3104856;
                    b4 = 0.55000 * b4 + g * 0.5329522;
                    b5 = -0.7616 * b5 - g * 0.0168980;
                    double pk = b0 + b1 + b2 + b3 + b4 + b5 + b6 + g * 0.5362;
                    b6 = g * 0.115926;
                    // フィルター後 σ≈3.058 → ホワイトと同じく ±3.5σ をフルスケールに
                    nPink[i] = Math.Clamp(pk / (3.5 * 3.058), -1.0, 1.0);
                }
            }
            if (mls)
            {
                // 32bit 最大長系列 (x^32 + x^22 + x^2 + x + 1), 周期 2^32-1
                uint lsb = lfsr & 1u;
                lfsr >>= 1;
                if (lsb != 0) lfsr ^= 0x80200003u;
                nMls[i] = lsb != 0 ? 1 : -1;
            }
        }
    }

    public Synth(int fs, WaveParams[] ps, bool mono)
    {
        Fs = fs; Params = ps; Mono = mono;
        for (int i = 0; i < NWaves; i++) st[i] = new State();
    }

    static double PolyBlep(double t, double dt)
    {
        if (dt <= 0) return 0;
        if (t < dt) { t /= dt; return t + t - t * t - 1; }
        if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
        return 0;
    }

    static double Frac(double x) => x - Math.Floor(x);


    public void Render(float[] L, float[] R, int frames)
    {
        var ps = Params;
        Array.Clear(L, 0, frames);
        Array.Clear(R, 0, frames);
        double fs = Fs;
        MakeNoise(frames,
            ps.Any(p => p?.Type == WaveType.WhiteNoise), ps.Any(p => p?.Type == WaveType.PinkNoise), ps.Any(p => p?.Type == WaveType.Mls));

        for (int w = 0; w < NWaves && w < ps.Length; w++)
        {
            var p = ps[w];
            if (p == null || p.Type == WaveType.Off) continue;
            var s = st[w];
            double amp = Math.Pow(10, p.AmpDb / 20) * (p.Invert ? -1 : 1);
            double off = p.OffsetPct / 100;
            double ph0 = p.PhaseDeg / 360;
            double duty = Math.Clamp(p.Duty / 100, 0.001, 0.999);
            long gOn = (long)(p.GateOnMs * fs / 1000), gPer = gOn + (long)(p.GateOffMs * fs / 1000);
            double sweepN = p.SweepTimeSec * fs;
            long sweepPer = (long)(p.SweepPingPong ? 2 * sweepN : sweepN);
            bool doSweep = p.Sweep && sweepPer >= 1;
            bool logSweep = p.SweepLog && p.Freq > 0 && p.SweepEndFreq > 0;

            for (int i = 0; i < frames; i++)
            {
                long t = n + i - p.DelaySamples;
                if (t < 0) continue;

                double f = p.Freq;
                if (doSweep)
                {
                    double pos = (t % sweepPer) / sweepN;
                    if (pos > 1) pos = 2 - pos;
                    f = logSweep ? p.Freq * Math.Pow(p.SweepEndFreq / p.Freq, pos)
                                 : p.Freq + (p.SweepEndFreq - p.Freq) * pos;
                }
                double a = amp;
                if (p.Mod)
                {
                    double m = Math.Sin(2 * Math.PI * s.ModPhase);
                    s.ModPhase = Frac(s.ModPhase + p.ModFreq / fs);
                    if (p.ModType == ModType.FM) f += p.ModDepth * m;
                    else a *= 1 + p.ModDepth / 100 * m;
                }
                double dt = f / fs;
                double ph = Frac(s.Phase + ph0);
                double adt = Math.Abs(dt);
                double v;
                switch (p.Type)
                {
                    case WaveType.Sine: v = f == 0 ? 1 : Math.Sin(2 * Math.PI * ph); break;
                    case WaveType.Square:
                        v = ph < duty ? 1 : -1;
                        if (p.BandLimit) v += PolyBlep(ph, adt) - PolyBlep(Frac(ph - duty), adt);
                        break;
                    case WaveType.Triangle:
                        v = ph < 0.25 ? 4 * ph : ph < 0.75 ? 2 - 4 * ph : 4 * ph - 4; break;
                    case WaveType.Saw:
                        {
                            double q = Frac(ph + 0.5);
                            v = 2 * q - 1;
                            if (p.BandLimit) v -= PolyBlep(q, adt);
                        }
                        break;
                    case WaveType.Pulse: v = ph < adt ? 1 : 0; break;
                    case WaveType.WhiteNoise: v = nWhite[i]; break;
                    case WaveType.PinkNoise: v = nPink[i]; break;
                    case WaveType.Mls: v = nMls[i]; break;
                    case WaveType.PulsePM: v = ph < adt ? 1 : Frac(ph - 0.5) < adt ? -1 : 0; break;
                    default: v = 0; break;
                }
                s.Phase = Frac(s.Phase + dt);

                if (p.Gate && gPer > 0 && (t % gPer) >= gOn) continue;

                float o = (float)(a * v + off);
                if (Mono) { L[i] += o; continue; }
                switch (p.Ch)
                {
                    case OutCh.LR: L[i] += o; R[i] += o; break;
                    case OutCh.L: L[i] += o; break;
                    case OutCh.R: R[i] += o; break;
                    case OutCh.LmR: L[i] += o; R[i] -= o; break;
                }
            }
        }
        n += frames;

        float gain = Gain;
        if (gain != 1)
            for (int i = 0; i < frames; i++) { L[i] *= gain; R[i] *= gain; }

        float pl = 0, pr = 0;
        for (int i = 0; i < frames; i++)
        {
            float al = Math.Abs(L[i]), ar = Math.Abs(R[i]);
            if (al > pl) pl = al;
            if (ar > pr) pr = ar;
        }
        PeakL = pl; PeakR = Mono ? pl : pr;
    }

    public static int BytesPerSample(int bits) => bits == 16 ? 2 : bits == 24 ? 3 : 4;

    /// <summary>float → PCM16 / PCM24 / Float32 (リトルエンディアン, インターリーブ)</summary>
    public static void Encode(float[] L, float[] R, int frames, int ch, int bits, byte[] dst)
    {
        int k = 0;
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < ch; c++)
            {
                double v = c == 0 ? L[i] : R[i];
                if (v > 1) v = 1; else if (v < -1) v = -1;
                if (bits == 16)
                {
                    int x = (int)Math.Round(v * 32767);
                    dst[k++] = (byte)x; dst[k++] = (byte)(x >> 8);
                }
                else if (bits == 24)
                {
                    int x = (int)Math.Round(v * 8388607);
                    dst[k++] = (byte)x; dst[k++] = (byte)(x >> 8); dst[k++] = (byte)(x >> 16);
                }
                else
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(dst.AsSpan(k), (float)v);
                    k += 4;
                }
            }
        }
    }

    public static void WriteWav(string path, WaveParams[] ps, int fs, int bits, int ch, double seconds, float gain = 1)
    {
        var syn = new Synth(fs, ps, ch == 1) { Gain = gain };
        long total = (long)(seconds * fs);
        int bps = BytesPerSample(bits), block = bps * ch;
        long dataLen = total * block;
        bool isFloat = bits == 32;
        using var fsr = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fsr);
        int fmtLen = isFloat ? 18 : 16;
        bw.Write("RIFF"u8); bw.Write((uint)(4 + 8 + fmtLen + 8 + dataLen + (dataLen & 1)));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(fmtLen);
        bw.Write((ushort)(isFloat ? 3 : 1)); bw.Write((ushort)ch); bw.Write(fs); bw.Write(fs * block);
        bw.Write((ushort)block); bw.Write((ushort)bits);
        if (isFloat) bw.Write((ushort)0);
        bw.Write("data"u8); bw.Write((uint)dataLen);
        const int chunk = 8192;
        var L = new float[chunk]; var R = new float[chunk]; var buf = new byte[chunk * block];
        for (long done = 0; done < total;)
        {
            int nf = (int)Math.Min(chunk, total - done);
            syn.Render(L, R, nf);
            Encode(L, R, nf, ch, bits, buf);
            bw.Write(buf, 0, nf * block);
            done += nf;
        }
        if ((dataLen & 1) != 0) bw.Write((byte)0);
    }
}
