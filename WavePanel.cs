namespace WaveGenFS;

static class Ui
{
    /// <summary>現在のサンプリング周波数 (MainForm が設定)</summary>
    public static Func<int> Fs = () => 48000;
    /// <summary>FFT用に最適化 で使うサンプル数</summary>
    public static int FftN = 4096;
    public static readonly int[] FftNs = { 2048, 4096, 8192, 16384, 32768, 65536, 131072 };

    public static NumericUpDown Nud(decimal min, decimal max, int dec, decimal inc, int width = 92) =>
        new() { Minimum = min, Maximum = max, DecimalPlaces = dec, Increment = inc, Width = width, TextAlign = HorizontalAlignment.Right, Margin = new Padding(2) };

    public static ComboBox Combo(string[] items, int width = 110)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(2) };
        c.Items.AddRange(items);
        return c;
    }

    public static Label Lbl(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 2, 2) };

    public static decimal D(double v, NumericUpDown n) => Math.Clamp((decimal)Math.Clamp(v, -7.9e27, 7.9e27), n.Minimum, n.Maximum);

    /// <summary>増減幅を値の桁に合わせる (1000 → 10 刻み)</summary>
    public static void AutoInc(NumericUpDown n)
    {
        double v = Math.Max(0.01, Math.Abs((double)n.Value));
        n.Increment = (decimal)Math.Max(Math.Pow(10, -n.DecimalPlaces), Math.Pow(10, Math.Floor(Math.Log10(v)) - 2));
    }

    public static void SetMenu(Control c, ContextMenuStrip m)
    {
        c.ContextMenuStrip = m;
        foreach (Control ch in c.Controls) ch.ContextMenuStrip = m;
    }
}

/// <summary>1系統の基本設定パネル。詳細はボタンから DetailForm。</summary>
public class WavePanel : GroupBox
{
    public readonly int Index;
    public WaveParams P;
    public event Action Changed;
    public event Action<WavePanel> DetailRequested;
    /// <summary>(このパネル, 倍数) : Wave1/Wave5 の倍数に設定</summary>
    public event Action<WavePanel, int> MultipleRequested;

    readonly ComboBox cbType, cbCh;
    readonly NumericUpDown nFreq, nAmp, nOff, nPhase;
    readonly Label lFreqUnit, lAmpUnit;
    readonly Button bDetail;
    readonly ToolTip tip = new();
    double ampMaxLin = 10, offMaxPct = 100;
    bool loading;

    const double ValueMax = 32767;

    public WavePanel(int index, WaveParams p)
    {
        Index = index; P = p;
        Text = $"Wave{index + 1}";
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(4, 2, 4, 4);
        Margin = new Padding(3);

        var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        cbType = Ui.Combo(WaveParams.TypeNames, 120);
        nFreq = Ui.Nud(0, 10_000_000, 4, 10);
        nAmp = Ui.Nud(-200, 20, 1, 1);
        nOff = Ui.Nud(-100, 100, 1, 1);
        nPhase = Ui.Nud(-360, 360, 1, 5);
        cbCh = Ui.Combo(WaveParams.ChNames, 70);
        bDetail = new Button { Text = "詳細...", AutoSize = true, Margin = new Padding(2) };
        lFreqUnit = UnitLabel(); lAmpUnit = UnitLabel();

        void Row(string l, Control c, Control unit)
        {
            t.Controls.Add(Ui.Lbl(l));
            t.Controls.Add(c);
            t.Controls.Add(unit);
        }
        t.Controls.Add(Ui.Lbl("波形"));
        t.Controls.Add(cbType); t.SetColumnSpan(cbType, 2);
        Row("周波数", nFreq, lFreqUnit);
        Row("振幅", nAmp, lAmpUnit);
        Row("ｵﾌｾｯﾄ", nOff, Ui.Lbl("%"));
        Row("位相", nPhase, Ui.Lbl("°"));
        t.Controls.Add(Ui.Lbl("出力"));
        var fl = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        fl.Controls.Add(cbCh); fl.Controls.Add(bDetail);
        t.Controls.Add(fl); t.SetColumnSpan(fl, 2);
        Controls.Add(t);

        // 単位: クリックで順に切替、右クリックでメニュー
        tip.SetToolTip(lFreqUnit, "クリック: Hz / Sa(周期サンプル数) 切替");
        tip.SetToolTip(lAmpUnit, "クリック: dB / 値(32767max) / % 切替\n値・% で負の値にすると極性反転");
        lFreqUnit.Click += (_, _) => { P.FreqUnit = P.FreqUnit == "Sa" ? "Hz" : "Sa"; Load(); };
        lAmpUnit.Click += (_, _) => { P.AmpUnit = P.AmpUnit switch { "dB" => "value", "value" => "percent", _ => "dB" }; Load(); };

        BuildFreqMenu();

        cbType.SelectedIndexChanged += (_, _) => { if (!loading) { P.Type = (WaveType)cbType.SelectedIndex; Fire(); } };
        cbCh.SelectedIndexChanged += (_, _) => { if (!loading) { P.Ch = (OutCh)cbCh.SelectedIndex; Fire(); } };
        nFreq.ValueChanged += (_, _) =>
        {
            Ui.AutoInc(nFreq);
            if (loading) return;
            double v = (double)nFreq.Value;
            P.Freq = P.FreqUnit == "Sa" ? (v == 0 ? 0 : Ui.Fs() / v) : v;
            Fire();
        };
        nAmp.ValueChanged += (_, _) =>
        {
            if (loading) return;
            double v = (double)nAmp.Value;
            if (P.AmpUnit == "dB") P.AmpDb = v;
            else
            {
                double lin = Math.Abs(v) / (P.AmpUnit == "value" ? ValueMax : 100);
                P.Invert = v < 0;
                P.AmpDb = lin <= 0 ? -200 : 20 * Math.Log10(lin);
            }
            Fire();
        };
        nOff.ValueChanged += (_, _) => { if (!loading) { P.OffsetPct = (double)nOff.Value; Fire(); } };
        nPhase.ValueChanged += (_, _) => { if (!loading) { P.PhaseDeg = (double)nPhase.Value; Fire(); } };
        bDetail.Click += (_, _) => DetailRequested?.Invoke(this);
        Load();
    }

    static Label UnitLabel() => new()
    {
        AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 2, 2),
        Cursor = Cursors.Hand, ForeColor = Color.FromArgb(0, 70, 170), Font = new Font("Yu Gothic UI", 9F, FontStyle.Underline),
    };

    void BuildFreqMenu()
    {
        var m = new ContextMenuStrip();
        var fit = new ToolStripMenuItem("FFT用に最適化");
        fit.Click += (_, _) =>
        {
            double bin = (double)Ui.Fs() / Ui.FftN;
            double k = Math.Max(1, Math.Round(P.Freq / bin));
            P.Freq = k * bin;
            Load();
            Fire();
        };
        var nMenu = new ToolStripMenuItem("  FFTサンプル数");
        foreach (var n in Ui.FftNs)
        {
            var it = new ToolStripMenuItem(n.ToString());
            it.Click += (_, _) => Ui.FftN = n;
            nMenu.DropDownItems.Add(it);
        }
        nMenu.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem it in nMenu.DropDownItems) it.Checked = it.Text == Ui.FftN.ToString();
        };
        m.Items.Add(fit);
        m.Items.Add(nMenu);
        if (Index != 0 && Index != 4)
        {
            int baseW = Index < 4 ? 1 : 5;
            var mul = new ToolStripMenuItem($"Wave{baseW} の倍数");
            foreach (var x in new[] { 2, 3, 4, 5, 6, 8, 10 })
            {
                var it = new ToolStripMenuItem($"x{x}");
                it.Click += (_, _) => MultipleRequested?.Invoke(this, x);
                mul.DropDownItems.Add(it);
            }
            m.Items.Add(mul);
        }
        m.Opening += (_, _) =>
        {
            fit.Text = $"FFT用に最適化  ({Ui.FftN}点: {(double)Ui.Fs() / Ui.FftN:0.#####}Hz の整数倍)";
        };
        Ui.SetMenu(nFreq, m);
        var um = new ContextMenuStrip();
        foreach (var (u, txt) in new[] { ("Hz", "Hz"), ("Sa", "Sample数 (周期)") })
            um.Items.Add(txt, null, (_, _) => { P.FreqUnit = u; Load(); });
        lFreqUnit.ContextMenuStrip = um;
        var am = new ContextMenuStrip();
        foreach (var (u, txt) in new[] { ("dB", "dB  (0max)"), ("value", "数値 (32767max)"), ("percent", "%  (100max)") })
            am.Items.Add(txt, null, (_, _) => { P.AmpUnit = u; Load(); });
        lAmpUnit.ContextMenuStrip = am;
    }

    void Fire() { UpdateState(); Changed?.Invoke(); }

    /// <summary>モデルの値を表示に反映 (単位・上限も含む)</summary>
    public void Load()
    {
        loading = true;
        cbType.SelectedIndex = (int)P.Type;
        cbCh.SelectedIndex = (int)P.Ch;

        bool sa = P.FreqUnit == "Sa";
        lFreqUnit.Text = sa ? "Sa" : "Hz";
        nFreq.DecimalPlaces = sa ? 3 : 4;
        nFreq.Value = Ui.D(sa ? (P.Freq == 0 ? 0 : Ui.Fs() / P.Freq) : P.Freq, nFreq);
        Ui.AutoInc(nFreq);
        bool over = P.Freq > Ui.Fs() / 2.0;   // ナイキスト周波数超え (折り返し) の警告
        nFreq.BackColor = over && !P.IsNoise ? Color.MistyRose : SystemColors.Window;

        ApplyAmpLimits();
        nOff.Value = Ui.D(P.OffsetPct, nOff);
        nPhase.Value = Ui.D(P.PhaseDeg, nPhase);
        loading = false;
        UpdateState();
    }

    void ApplyAmpLimits()
    {
        double sign = P.Invert ? -1 : 1, lin = Math.Pow(10, P.AmpDb / 20);
        switch (P.AmpUnit)
        {
            case "value":
                lAmpUnit.Text = "値";
                nAmp.DecimalPlaces = 1; nAmp.Increment = 1;
                nAmp.Minimum = -(decimal)Floor(ampMaxLin * ValueMax, 1); nAmp.Maximum = (decimal)Floor(ampMaxLin * ValueMax, 1);
                nAmp.Value = Ui.D(sign * lin * ValueMax, nAmp);
                break;
            case "percent":
                lAmpUnit.Text = "%";
                nAmp.DecimalPlaces = 2; nAmp.Increment = 1;
                nAmp.Minimum = -(decimal)Floor(ampMaxLin * 100, 2); nAmp.Maximum = (decimal)Floor(ampMaxLin * 100, 2);
                nAmp.Value = Ui.D(sign * lin * 100, nAmp);
                break;
            default:
                lAmpUnit.Text = P.Invert ? "dB(反転)" : "dB";
                nAmp.DecimalPlaces = 1; nAmp.Increment = 1;
                nAmp.Minimum = -200;
                nAmp.Maximum = Math.Max(-200, (decimal)(ampMaxLin <= 1e-10 ? -200 : Floor(20 * Math.Log10(ampMaxLin), 1)));
                nAmp.Value = Ui.D(P.AmpDb, nAmp);
                break;
        }
        nOff.Maximum = (decimal)offMaxPct; nOff.Minimum = -(decimal)offMaxPct;
    }

    static double Floor(double v, int dec) { double s = Math.Pow(10, dec); return Math.Floor(v * s + 1e-9) / s; }

    /// <summary>クリップしない上限を設定 (振幅の線形上限, オフセット絶対値上限 %)</summary>
    public void SetLimits(double ampMaxLinear, double offMaxPercent)
    {
        ampMaxLin = Math.Max(0, ampMaxLinear);
        offMaxPct = Math.Max(0, offMaxPercent);
        loading = true;
        ApplyAmpLimits();
        loading = false;
        // 表示の丸め/制限で値が変わった場合のみモデルへ戻す
        double shown = (double)nAmp.Value;
        double curShown = P.AmpUnit switch
        {
            "value" => (P.Invert ? -1 : 1) * Math.Pow(10, P.AmpDb / 20) * ValueMax,
            "percent" => (P.Invert ? -1 : 1) * Math.Pow(10, P.AmpDb / 20) * 100,
            _ => P.AmpDb,
        };
        if (Math.Abs(shown - curShown) > Math.Pow(10, -nAmp.DecimalPlaces))
        {
            if (P.AmpUnit == "dB") P.AmpDb = shown;
            else
            {
                double lin = Math.Abs(shown) / (P.AmpUnit == "value" ? ValueMax : 100);
                P.AmpDb = lin <= 0 ? -200 : 20 * Math.Log10(lin);
            }
        }
        if (Math.Abs(P.OffsetPct) > offMaxPct + 1e-9) P.OffsetPct = (double)nOff.Value;
    }

    public void UpdateState()
    {
        bool on = P.Type != WaveType.Off;
        nFreq.Enabled = on && !P.IsNoise; nPhase.Enabled = on && !P.IsNoise;
        nAmp.Enabled = nOff.Enabled = cbCh.Enabled = on;
        var tags = new List<string>();
        if (P.Sweep) tags.Add("Sw");
        if (P.Mod) tags.Add(P.ModType.ToString());
        if (P.Gate) tags.Add("G");
        if (P.DelaySamples != 0) tags.Add("D");
        bDetail.Text = tags.Count == 0 ? "詳細..." : $"詳細[{string.Join(",", tags)}]";
        ForeColor = on ? SystemColors.ControlText : SystemColors.GrayText;
    }
}
