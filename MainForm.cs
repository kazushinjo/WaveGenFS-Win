using System.Text.Json;

namespace WaveGenFS;

public class AppSettings
{
    public WaveParams[] Waves { get; set; }
    public int Fs { get; set; } = 48000;
    public int Bits { get; set; } = 16;
    public bool Stereo { get; set; } = true;
    public string Device { get; set; } = "";
    public double LengthSec { get; set; } = 10;
    public int LatencyMs { get; set; } = 120;
    public int SpanIdx { get; set; } = 6;
    public int YZoomIdx { get; set; }
    public int DispCh { get; set; }
    public int TabIdx { get; set; }
    public string LastWavDir { get; set; } = "";
    public bool StartFullScreen { get; set; }
    public int FftN { get; set; } = 4096;
    public bool AttOn { get; set; }
    public double AttN { get; set; } = 10;
    public bool FullVolume { get; set; }
    public string Comment { get; set; } = "";
    public string LastParamDir { get; set; } = "";
}

/// <summary>設定ファイル (*.wgfs) の内容</summary>
public class ParamFile
{
    public string Comment { get; set; } = "";
    public int Fs { get; set; } = 48000;
    public int Bits { get; set; } = 16;
    public bool Stereo { get; set; } = true;
    public double LengthSec { get; set; } = 10;
    public int FftN { get; set; } = 4096;
    public bool AttOn { get; set; }
    public double AttN { get; set; } = 10;
    public WaveParams[] Waves { get; set; }
}

public class MainForm : Form
{
    static readonly int[] FsList = { 8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000, 384000 };
    static readonly string[] BitsNames = { "16", "24", "32 float" };
    static readonly int[] BitsList = { 16, 24, 32 };

    readonly string iniPath = Path.Combine(AppContext.BaseDirectory, "WaveGenFS.json");
    AppSettings cfg;
    WaveParams[] model;

    readonly WaveOutPlayer player = new();
    readonly ScopeView scope = new() { Dock = DockStyle.Fill };
    readonly TabControl tabs = new() { Dock = DockStyle.Top };
    readonly FlowLayoutPanel toolbar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(2) };
    readonly FlowLayoutPanel levelRow = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(2) };
    readonly WavePanel[] panels = new WavePanel[Synth.NWaves];
    readonly DetailForm[] details = new DetailForm[Synth.NWaves];
    readonly Label[] levelLbl = new Label[Synth.NWaves];
    readonly Label outLbl = new() { AutoSize = true, Margin = new Padding(12, 6, 2, 2) };
    Button bPlay, bFull, bHide;
    CheckBox chkAtt, chkFullVol;
    NumericUpDown nAttN;
    Label lAttDb;
    TextBox tbComment;
    ComboBox cbFs, cbBits, cbCh, cbDev, cbDisp;
    NumericUpDown nLen, nLat;
    readonly System.Windows.Forms.Timer meterTimer = new() { Interval = 100 };
    float holdL, holdR; bool clipHold; DateTime clipUntil;

    bool fullScreen, controlsHidden;
    FormWindowState prevState; Rectangle prevBounds;

    public MainForm()
    {
        Text = "WaveGenFS - 音声テスト信号発生器";
        Font = new Font("Yu Gothic UI", 9F);
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        LoadSettings();
        Ui.Fs = () => cfg.Fs;
        Ui.FftN = Ui.FftNs.Contains(cfg.FftN) ? cfg.FftN : 4096;
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) LoadParamFile(f[0]); };
        BuildUi();
        ApplyAll();
        meterTimer.Tick += (_, _) => UpdateMeter();
        meterTimer.Start();
    }

    // ---------------------------------------------------------------- 設定
    void LoadSettings()
    {
        try
        {
            if (File.Exists(iniPath)) cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(iniPath));
        }
        catch { cfg = null; }
        cfg ??= new AppSettings();
        if (cfg.Waves == null || cfg.Waves.Length != Synth.NWaves)
        {
            cfg.Waves = Enumerable.Range(0, Synth.NWaves).Select(_ => new WaveParams()).ToArray();
            cfg.Waves[0].Type = WaveType.Sine; cfg.Waves[0].AmpDb = -10;
        }
        model = cfg.Waves;
    }

    void SaveSettings()
    {
        try
        {
            File.WriteAllText(iniPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---------------------------------------------------------------- UI 構築
    void BuildUi()
    {
        bPlay = new Button { Text = "▶ 再生 (F5)", AutoSize = true, Margin = new Padding(2) };
        bPlay.Click += (_, _) => TogglePlay();
        var bSave = new Button { Text = "WAV保存...", AutoSize = true, Margin = new Padding(2) };
        bSave.Click += (_, _) => SaveWav();
        nLen = Ui.Nud(0.1m, 36000, 1, 1, 70); nLen.Value = Ui.D(cfg.LengthSec, nLen);
        nLen.ValueChanged += (_, _) => cfg.LengthSec = (double)nLen.Value;

        cbFs = Ui.Combo(FsList.Select(f => f.ToString()).ToArray(), 80);
        cbFs.SelectedIndex = Math.Max(0, Array.IndexOf(FsList, cfg.Fs));
        cbBits = Ui.Combo(BitsNames, 80);
        cbBits.SelectedIndex = Math.Max(0, Array.IndexOf(BitsList, cfg.Bits));
        cbCh = Ui.Combo(new[] { "Stereo", "Mono" }, 75);
        cbCh.SelectedIndex = cfg.Stereo ? 0 : 1;
        var devs = WaveOutPlayer.Devices();
        cbDev = Ui.Combo(new[] { "既定のデバイス" }.Concat(devs).ToArray(), 220);
        int di = devs.IndexOf(cfg.Device);
        cbDev.SelectedIndex = di >= 0 ? di + 1 : 0;
        nLat = Ui.Nud(20, 2000, 0, 10, 60); nLat.Value = Ui.D(cfg.LatencyMs, nLat);

        EventHandler fmtChanged = (_, _) =>
        {
            cfg.Fs = FsList[cbFs.SelectedIndex];
            cfg.Bits = BitsList[cbBits.SelectedIndex];
            cfg.Stereo = cbCh.SelectedIndex == 0;
            cfg.Device = cbDev.SelectedIndex > 0 ? (string)cbDev.SelectedItem : "";
            cfg.LatencyMs = (int)nLat.Value;
            foreach (var pn in panels) pn?.Load();
            ApplyAll();
            if (player.IsPlaying) StartPlay();
        };
        cbFs.SelectedIndexChanged += fmtChanged; cbBits.SelectedIndexChanged += fmtChanged;
        cbCh.SelectedIndexChanged += fmtChanged; cbDev.SelectedIndexChanged += fmtChanged;
        nLat.ValueChanged += fmtChanged;

        // 出力減衰 (設定振幅の 1/N)
        chkAtt = new CheckBox { Text = "出力 1/N", Appearance = Appearance.Button, AutoSize = true, Margin = new Padding(12, 2, 2, 2), Checked = cfg.AttOn };
        nAttN = Ui.Nud(1, 1_000_000, 2, 1, 80); nAttN.Value = Ui.D(cfg.AttN, nAttN);
        lAttDb = Ui.Lbl("");
        chkAtt.CheckedChanged += (_, _) => { cfg.AttOn = chkAtt.Checked; ApplyAtt(); };
        nAttN.ValueChanged += (_, _) => { cfg.AttN = (double)nAttN.Value; ApplyAtt(); };
        chkFullVol = new CheckBox { Text = "Volume最大", AutoSize = true, Margin = new Padding(8, 5, 2, 2), Checked = cfg.FullVolume };
        chkFullVol.CheckedChanged += (_, _) => { cfg.FullVolume = chkFullVol.Checked; if (player.IsPlaying) StartPlay(); };

        var bLoad = new Button { Text = "設定読込...", AutoSize = true, Margin = new Padding(2) };
        bLoad.Click += (_, _) => OpenParamFile();
        var bSaveP = new Button { Text = "設定保存...", AutoSize = true, Margin = new Padding(2) };
        bSaveP.Click += (_, _) => SaveParamFile();
        tbComment = new TextBox { Width = 260, MaxLength = 80, Text = cfg.Comment, Margin = new Padding(2, 3, 2, 2), PlaceholderText = "設定ファイル用コメント" };
        tbComment.TextChanged += (_, _) => cfg.Comment = tbComment.Text;

        bFull = new Button { Text = "全画面 (F11)", AutoSize = true, Margin = new Padding(12, 2, 2, 2) };
        bFull.Click += (_, _) => SetFullScreen(!fullScreen);
        bHide = new Button { Text = "波形のみ (F12)", AutoSize = true, Margin = new Padding(2) };
        bHide.Click += (_, _) => SetControlsHidden(!controlsHidden);

        toolbar.Controls.AddRange(new Control[] {
            bPlay, bSave, nLen, Ui.Lbl("秒"),
            Ui.Lbl("  Fs"), cbFs, Ui.Lbl("bit"), cbBits, cbCh,
            Ui.Lbl("  出力"), cbDev, Ui.Lbl("遅延"), nLat, Ui.Lbl("ms"), chkFullVol,
            chkAtt, nAttN, lAttDb,
            bLoad, bSaveP, tbComment,
            bFull, bHide });
        var tipT = new ToolTip();
        tipT.SetToolTip(chkAtt, "押すと出力を設定振幅の 1/N に減衰 (再生・波形表示・WAV保存に適用)");
        tipT.SetToolTip(chkFullVol, "再生中だけ Windows のアプリ音量を最大にする (停止で元に戻す)");
        tipT.SetToolTip(bLoad, "設定ファイルを開く (Ctrl+O)  ※ウィンドウへのドロップでも可");
        tipT.SetToolTip(bSaveP, "現在の全設定をコメント付きで保存 (Ctrl+S)");
        tipT.SetToolTip(bSave, "WAV ファイルへ出力 (Ctrl+W)");

        for (int i = 0; i < Synth.NWaves; i++)
        {
            var p = new WavePanel(i, model[i]);
            int idx = i;
            p.Changed += () => ApplyAll(idx);
            p.DetailRequested += ShowDetail;
            p.MultipleRequested += (pn, x) =>
            {
                int b = pn.Index < 4 ? 0 : 4;
                model[pn.Index].Freq = model[b].Freq * x;
                pn.Load();
                ApplyAll(pn.Index);
            };
            panels[i] = p;
        }
        for (int page = 0; page < 2; page++)
        {
            var tp = new TabPage(page == 0 ? "Wave 1,2,3,4" : "Wave 5,6,7,8") { UseVisualStyleBackColor = true };
            var fl = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, AutoScroll = true };
            for (int k = 0; k < 4; k++) fl.Controls.Add(panels[page * 4 + k]);
            tp.Controls.Add(fl);
            tabs.TabPages.Add(tp);
        }
        tabs.SelectedIndex = Math.Clamp(cfg.TabIdx, 0, 1);
        tabs.SelectedIndexChanged += (_, _) => cfg.TabIdx = tabs.SelectedIndex;

        cbDisp = Ui.Combo(new[] { "Lch + Rch", "Lch", "Rch" }, 90);
        cbDisp.SelectedIndex = Math.Clamp(cfg.DispCh, 0, 2);
        cbDisp.SelectedIndexChanged += (_, _) => { cfg.DispCh = scope.DispCh = cbDisp.SelectedIndex; scope.Invalidate(); };
        levelRow.Controls.Add(Ui.Lbl("表示ch"));
        levelRow.Controls.Add(cbDisp);
        levelRow.Controls.Add(Ui.Lbl("  Level (dBFS rms)"));
        for (int i = 0; i < Synth.NWaves; i++)
        {
            levelRow.Controls.Add(Ui.Lbl($"{i + 1}:"));
            levelLbl[i] = new Label { AutoSize = false, Width = 52, Height = 22, BorderStyle = BorderStyle.Fixed3D, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 3, 4, 2) };
            levelRow.Controls.Add(levelLbl[i]);
        }
        levelRow.Controls.Add(outLbl);

        scope.SpanIdx = Math.Clamp(cfg.SpanIdx, 0, ScopeView.Spans.Length - 1);
        scope.YZoomIdx = Math.Clamp(cfg.YZoomIdx, 0, ScopeView.YZooms.Length - 1);
        scope.DispCh = cbDisp.SelectedIndex;
        scope.SpanChanged += (_, _) => { cfg.SpanIdx = scope.SpanIdx; cfg.YZoomIdx = scope.YZoomIdx; UpdateScope(); };
        scope.DoubleClick += (_, _) => { if (!fullScreen) { SetFullScreen(true); SetControlsHidden(true); } else { SetControlsHidden(false); SetFullScreen(false); } };
        scope.MouseDown += (_, _) => scope.Focus();

        // Dock の順序: Fill を先に追加し、Top は下から順に追加
        Controls.Add(scope);
        Controls.Add(levelRow);
        Controls.Add(tabs);
        Controls.Add(toolbar);

        var tip = new ToolTip();
        tip.SetToolTip(scope, "ホイール: 時間軸  Ctrl+ホイール: 振幅軸  ダブルクリック: 全画面波形表示\nF11: 全画面  F12: 波形のみ  Esc: 戻る  F5: 再生/停止");
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // タブの高さをパネルに合わせる
        var fl = tabs.TabPages[0].Controls[0];
        int inner = fl.GetPreferredSize(Size.Empty).Height + 8;
        tabs.Height = inner + (tabs.Height - tabs.DisplayRectangle.Height);
        toolbar.WrapContents = false;
        int tw = toolbar.GetPreferredSize(Size.Empty).Width;
        toolbar.WrapContents = true;
        int w = Math.Min(Screen.FromControl(this).WorkingArea.Width - 40, Math.Max(fl.GetPreferredSize(Size.Empty).Width + 40, tw + 20));
        ClientSize = new Size(Math.Max(w, 900), toolbar.Height + tabs.Height + levelRow.Height + 340);
        MinimumSize = new Size(400, 300);
        if (cfg.StartFullScreen) SetFullScreen(true);
        ApplyAtt();
    }

    // ---------------------------------------------------------------- 反映
    WaveParams[] Snapshot() => model.Select(p => p.Clone()).ToArray();

    // ---------------------------------------------------------------- クリップ防止
    // 各チャンネルで「全波形の最大瞬間値の合計」が 1 (100%) を超えないよう制限する。
    const double Tol = 1e-9;

    double OthersPeak(int i, int c)
    {
        bool mono = !cfg.Stereo;
        double sum = 0;
        for (int j = 0; j < Synth.NWaves; j++)
            if (j != i && model[j].Feeds(mono)[c]) sum += model[j].PeakLin;
        return sum;
    }

    double Headroom(int i)
    {
        var f = model[i].Feeds(!cfg.Stereo);
        double h = 1;
        for (int c = 0; c < 2; c++) if (f[c]) h = Math.Min(h, 1 - OthersPeak(i, c));
        return Math.Max(0, h);
    }

    static double FloorDb(double lin) => lin <= 1e-10 ? -200 : Math.Max(-200, Math.Floor(20 * Math.Log10(lin) * 10 + 1e-9) / 10);
    static double Floor1(double v) => Math.Floor(v * 10 + 1e-9) / 10;

    bool Violates(int c)
    {
        double sum = 0;
        for (int j = 0; j < Synth.NWaves; j++) if (model[j].Feeds(!cfg.Stereo)[c]) sum += model[j].PeakLin;
        return sum > 1 + Tol;
    }

    /// <summary>超過していれば変更した波形 (なければ最大の波形) の振幅を下げる</summary>
    void Enforce(int changed)
    {
        for (int guard = 0; guard < 32 && (Violates(0) || Violates(1)); guard++)
        {
            int c = Violates(0) ? 0 : 1;
            int k = changed >= 0 && model[changed].Type != WaveType.Off && model[changed].Feeds(!cfg.Stereo)[c] ? changed
                  : Enumerable.Range(0, Synth.NWaves).Where(j => model[j].Type != WaveType.Off && model[j].Feeds(!cfg.Stereo)[c])
                              .OrderByDescending(j => model[j].PeakLin).First();
            var p = model[k];
            double room = Headroom(k);
            if (room - Math.Abs(p.OffsetPct) / 100 <= 1e-6)
            {
                p.OffsetPct = Math.Sign(p.OffsetPct) * Floor1(room * 100 * 0.5);   // オフセットも入り切らない場合は半分を割り当て
            }
            p.AmpDb = FloorDb((room - Math.Abs(p.OffsetPct) / 100) / p.AmFactor);
            panels[k].Load();
            changed = -1;
        }
        // 入力欄の上限を更新
        for (int i = 0; i < Synth.NWaves; i++)
        {
            var p = model[i];
            double room = Headroom(i);
            double ampMax = (room - Math.Abs(p.OffsetPct) / 100) / p.AmFactor;
            double offMax = Floor1((room - (p.Type == WaveType.Off ? 0 : p.AmpLin * p.AmFactor)) * 100);
            if (p.Type == WaveType.Off) { ampMax = 10; offMax = 100; }
            panels[i].SetLimits(Math.Min(10, ampMax), Math.Clamp(offMax, 0, 100));
        }
    }

    void ApplyAll() => ApplyAll(-1);

    void ApplyAll(int changed)
    {
        Enforce(changed);
        if (player.IsPlaying && player.Synth != null) player.Synth.Params = Snapshot();
        UpdateScope();
        UpdateLevels();
    }

    void UpdateScope()
    {
        int fs = cfg.Fs;
        int n = (int)Math.Ceiling(scope.SpanMs * fs / 1000.0) + 2;
        var syn = new Synth(fs, Snapshot(), !cfg.Stereo) { Gain = AttGain };
        var L = new float[n]; var R = new float[n];
        syn.Render(L, R, n);
        // 実際の出力と同じく ±100% でクリップした波形を表示
        bool clipped = false;
        for (int i = 0; i < n; i++)
        {
            if (L[i] > 1) { L[i] = 1; clipped = true; } else if (L[i] < -1) { L[i] = -1; clipped = true; }
            if (R[i] > 1) { R[i] = 1; clipped = true; } else if (R[i] < -1) { R[i] = -1; clipped = true; }
        }
        scope.WaveClipped = clipped;
        scope.L = L; scope.R = R; scope.Fs = fs; scope.Mono = !cfg.Stereo;
        scope.Overlay = model.Select((p, i) => p.Summary() is string s ? $"W{i + 1}: {s}" : null).Where(s => s != null).ToArray();
        UpdateStatus();
        scope.Invalidate();
    }

    void UpdateStatus()
    {
        string bits = cfg.Bits == 32 ? "32f" : cfg.Bits.ToString();
        scope.Status = $"{(player.IsPlaying ? "▶ 再生中" : "■ 停止")}  {cfg.Fs}Hz {bits}bit {(cfg.Stereo ? "Stereo" : "Mono")}  " +
                       $"{FmtSpan(scope.SpanMs)}/画面" + (cfg.AttOn ? $"  ATT 1/{cfg.AttN:0.##}" : "") + (scope.YZoomIdx > 0 ? $" ×{ScopeView.YZooms[scope.YZoomIdx]}" : "") + (scope.Clip ? "  CLIP!" : "");
    }

    static string FmtSpan(double ms) => ms >= 1000 ? $"{ms / 1000:g}s" : $"{ms:g}ms";

    void UpdateLevels()
    {
        int fs = cfg.Fs, n = Math.Max(4096, fs / 4);
        var L = new float[n]; var R = new float[n];
        for (int i = 0; i < Synth.NWaves; i++)
        {
            var p = model[i].Clone();
            if (p.Type == WaveType.Off) { levelLbl[i].Text = ""; continue; }
            p.DelaySamples = 0;
            var ps = Enumerable.Range(0, Synth.NWaves).Select(k => k == i ? p : new WaveParams()).ToArray();
            new Synth(fs, ps, true).Render(L, R, n);
            double sum = 0;
            for (int k = 0; k < n; k++) sum += (double)L[k] * L[k];
            double rms = Math.Sqrt(sum / n);
            levelLbl[i].Text = rms > 0 ? (20 * Math.Log10(rms)).ToString("0.00") : "-∞";
        }
    }

    void UpdateMeter()
    {
        if (!player.IsPlaying || player.Synth == null) { outLbl.Text = ""; return; }
        var s = player.Synth;
        holdL = Math.Max(s.PeakL, holdL * 0.8f);
        holdR = Math.Max(s.PeakR, holdR * 0.8f);
        if (s.PeakL > 1f || s.PeakR > 1f) { clipHold = true; clipUntil = DateTime.Now.AddSeconds(1.5); }
        if (clipHold && DateTime.Now > clipUntil) clipHold = false;
        static string Db(float v) => v > 0 ? (20 * Math.Log10(v)).ToString("0.0") : "-∞";
        outLbl.Text = $"出力ピーク L {Db(holdL)}  R {Db(holdR)} dBFS" + (clipHold ? "  CLIP!" : "");
        outLbl.ForeColor = clipHold ? Color.Red : SystemColors.ControlText;
        if (scope.Clip != clipHold) { scope.Clip = clipHold; UpdateStatus(); scope.Invalidate(); }
    }

    // ---------------------------------------------------------------- 再生
    void TogglePlay()
    {
        if (player.IsPlaying) StopPlay(); else StartPlay();
    }

    void StartPlay()
    {
        try
        {
            player.Stop();
            var syn = new Synth(cfg.Fs, Snapshot(), !cfg.Stereo) { Gain = AttGain };
            player.Start(syn, cfg.Bits, cfg.Stereo ? 2 : 1, cbDev.SelectedIndex - 1, cfg.LatencyMs, cfg.FullVolume);
            bPlay.Text = "■ 停止 (F5)";
        }
        catch (Exception ex)
        {
            player.Stop();
            bPlay.Text = "▶ 再生 (F5)";
            MessageBox.Show(this, ex.Message, "再生エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateStatus(); scope.Invalidate();
    }

    void StopPlay()
    {
        player.Stop();
        bPlay.Text = "▶ 再生 (F5)";
        UpdateStatus(); scope.Invalidate();
    }

    void SaveWav()
    {
        using var dlg = new SaveFileDialog { Filter = "WAV ファイル (*.wav)|*.wav", FileName = "WAVE.wav" };
        if (Directory.Exists(cfg.LastWavDir)) dlg.InitialDirectory = cfg.LastWavDir;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            Synth.WriteWav(dlg.FileName, Snapshot(), cfg.Fs, cfg.Bits, cfg.Stereo ? 2 : 1, cfg.LengthSec, AttGain);
            cfg.LastWavDir = Path.GetDirectoryName(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    // ---------------------------------------------------------------- 出力減衰
    float AttGain => cfg.AttOn ? (float)(1.0 / Math.Max(1, cfg.AttN)) : 1f;

    void ApplyAtt()
    {
        lAttDb.Text = $"({-20 * Math.Log10(Math.Max(1, cfg.AttN)):0.0#}dB)";
        lAttDb.ForeColor = cfg.AttOn ? Color.FromArgb(0, 90, 200) : SystemColors.GrayText;
        chkAtt.BackColor = cfg.AttOn ? Color.FromArgb(255, 220, 140) : SystemColors.Control;
        chkAtt.Text = cfg.AttOn ? "出力 1/N : ON" : "出力 1/N : OFF";
        if (player.IsPlaying && player.Synth != null) player.Synth.Gain = AttGain;
        UpdateScope();
    }

    // ---------------------------------------------------------------- 設定ファイル
    void OpenParamFile()
    {
        using var dlg = new OpenFileDialog { Filter = "WaveGenFS 設定 (*.wgfs)|*.wgfs|すべてのファイル (*.*)|*.*", Title = "設定ファイルを開く" };
        if (Directory.Exists(cfg.LastParamDir)) dlg.InitialDirectory = cfg.LastParamDir;
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadParamFile(dlg.FileName);
    }

    void LoadParamFile(string path)
    {
        try
        {
            var pf = JsonSerializer.Deserialize<ParamFile>(File.ReadAllText(path));
            if (pf?.Waves == null || pf.Waves.Length != Synth.NWaves) throw new InvalidDataException("WaveGenFS の設定ファイルではありません");
            bool wasPlaying = player.IsPlaying;
            if (wasPlaying) StopPlay();
            for (int i = 0; i < Synth.NWaves; i++)
            {
                model[i] = pf.Waves[i] ?? new WaveParams();
                panels[i].P = model[i];
                if (details[i] != null && !details[i].IsDisposed) details[i].Close();
            }
            cfg.Waves = model;
            cfg.FftN = Ui.FftN = Ui.FftNs.Contains(pf.FftN) ? pf.FftN : 4096;
            cfg.AttN = pf.AttN; cfg.AttOn = pf.AttOn;
            nAttN.Value = Ui.D(pf.AttN, nAttN); chkAtt.Checked = pf.AttOn;
            nLen.Value = Ui.D(pf.LengthSec, nLen);
            tbComment.Text = pf.Comment ?? "";
            int fi = Array.IndexOf(FsList, pf.Fs), bi = Array.IndexOf(BitsList, pf.Bits);
            cfg.Fs = fi >= 0 ? pf.Fs : cfg.Fs; cfg.Bits = bi >= 0 ? pf.Bits : cfg.Bits; cfg.Stereo = pf.Stereo;
            if (fi >= 0) cbFs.SelectedIndex = fi;
            if (bi >= 0) cbBits.SelectedIndex = bi;
            cbCh.SelectedIndex = pf.Stereo ? 0 : 1;
            foreach (var pn in panels) pn.Load();
            ApplyAll();
            cfg.LastParamDir = Path.GetDirectoryName(path);
            Text = $"WaveGenFS - {Path.GetFileName(path)}";
            if (wasPlaying) StartPlay();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "設定読込エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void SaveParamFile()
    {
        using var dlg = new SaveFileDialog { Filter = "WaveGenFS 設定 (*.wgfs)|*.wgfs", Title = "設定ファイルの保存", FileName = "WAVE.wgfs" };
        if (Directory.Exists(cfg.LastParamDir)) dlg.InitialDirectory = cfg.LastParamDir;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var pf = new ParamFile
            {
                Comment = cfg.Comment, Fs = cfg.Fs, Bits = cfg.Bits, Stereo = cfg.Stereo, LengthSec = cfg.LengthSec,
                FftN = Ui.FftN, AttOn = cfg.AttOn, AttN = cfg.AttN, Waves = Snapshot(),
            };
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(pf, new JsonSerializerOptions { WriteIndented = true }));
            cfg.LastParamDir = Path.GetDirectoryName(dlg.FileName);
            Text = $"WaveGenFS - {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void ShowDetail(WavePanel p)
    {
        var d = details[p.Index];
        if (d == null || d.IsDisposed)
        {
            d = details[p.Index] = new DetailForm(p);
            int idx = p.Index;
            d.Changed += () => ApplyAll(idx);
            d.Show(this);
        }
        else d.Activate();
    }

    // ---------------------------------------------------------------- 全画面
    void SetFullScreen(bool on)
    {
        if (on == fullScreen) return;
        fullScreen = on;
        SuspendLayout();
        if (on)
        {
            prevState = WindowState;
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            prevBounds = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = prevBounds;
            WindowState = prevState;
        }
        bFull.Text = on ? "全画面解除 (F11)" : "全画面 (F11)";
        ResumeLayout();
    }

    void SetControlsHidden(bool hide)
    {
        controlsHidden = hide;
        toolbar.Visible = tabs.Visible = levelRow.Visible = !hide;
        bHide.Text = hide ? "操作部表示 (F12)" : "波形のみ (F12)";
        if (hide) scope.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F11: SetFullScreen(!fullScreen); return true;
            case Keys.F12: SetControlsHidden(!controlsHidden); return true;
            case Keys.F5: TogglePlay(); return true;
            case Keys.Control | Keys.O: OpenParamFile(); return true;
            case Keys.Control | Keys.S: SaveParamFile(); return true;
            case Keys.Control | Keys.W: SaveWav(); return true;
            case Keys.Escape when fullScreen || controlsHidden:
                SetControlsHidden(false); SetFullScreen(false); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        player.Dispose();
        meterTimer.Stop();
        cfg.FftN = Ui.FftN;
        SaveSettings();
        base.OnFormClosing(e);
    }
}
