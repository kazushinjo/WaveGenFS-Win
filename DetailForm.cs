namespace WaveGenFS;

/// <summary>遅延・デューティ・帯域制限・ゲート・スイープ・変調の設定 (モードレス, 即時反映)</summary>
public class DetailForm : Form
{
    readonly WavePanel panel;
    WaveParams P => panel.P;
    bool loading;

    readonly NumericUpDown nDelay, nDuty, nGOn, nGOff, nSwEnd, nSwTime, nModF, nModD;
    readonly CheckBox cBand, cGate, cSweep, cSwLog, cSwPP, cMod;
    readonly ComboBox cbModType;
    readonly Label lDepthUnit;

    public DetailForm(WavePanel panel)
    {
        this.panel = panel;
        Text = $"Wave{panel.Index + 1} 詳細設定";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = false;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        var t = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Padding = new Padding(8) };
        void Row(params Control[] cs)
        {
            foreach (var c in cs) t.Controls.Add(c);
            for (int i = cs.Length; i < 4; i++) t.Controls.Add(new Label { AutoSize = true });
        }
        Label Hdr(string s) => new() { Text = s, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(2, 10, 2, 2) };

        nDelay = Ui.Nud(0, 10_000_000, 0, 1, 100);
        nDuty = Ui.Nud(0.1m, 99.9m, 1, 1);
        cBand = new CheckBox { Text = "する (矩形/ﾉｺｷﾞﾘ)", AutoSize = true };
        cGate = new CheckBox { Text = "ゲート", AutoSize = true };
        nGOn = Ui.Nud(0, 1_000_000, 1, 10);
        nGOff = Ui.Nud(0, 1_000_000, 1, 10);
        cSweep = new CheckBox { Text = "スイープ", AutoSize = true };
        nSwEnd = Ui.Nud(0, 384000, 3, 100);
        nSwTime = Ui.Nud(0.001m, 100000, 3, 1);
        cSwLog = new CheckBox { Text = "対数", AutoSize = true };
        cSwPP = new CheckBox { Text = "往復", AutoSize = true };
        cMod = new CheckBox { Text = "変調", AutoSize = true };
        cbModType = Ui.Combo(new[] { "AM", "FM" }, 70);
        nModF = Ui.Nud(0, 100000, 3, 1);
        nModD = Ui.Nud(0, 100000, 1, 1);
        lDepthUnit = Ui.Lbl("%");

        Row(Hdr("基本"));
        Row(Ui.Lbl("遅延"), nDelay, Ui.Lbl("sample"));
        Row(Ui.Lbl("デューティ"), nDuty, Ui.Lbl("% (矩形波)"));
        Row(Ui.Lbl("帯域制限"), cBand);
        Row(Hdr("ゲート (断続)"), cGate);
        Row(Ui.Lbl("ON 時間"), nGOn, Ui.Lbl("ms"));
        Row(Ui.Lbl("OFF 時間"), nGOff, Ui.Lbl("ms"));
        Row(Hdr("周波数スイープ"), cSweep);
        Row(Ui.Lbl("開始"), Ui.Lbl("= 周波数 欄"));
        Row(Ui.Lbl("終了"), nSwEnd, Ui.Lbl("Hz"));
        Row(Ui.Lbl("時間"), nSwTime, Ui.Lbl("秒"));
        Row(new Label { AutoSize = true }, cSwLog, cSwPP);
        Row(Hdr("変調"), cMod);
        Row(Ui.Lbl("方式"), cbModType);
        Row(Ui.Lbl("変調周波数"), nModF, Ui.Lbl("Hz"));
        Row(Ui.Lbl("深さ/偏移"), nModD, lDepthUnit);
        var bClose = new Button { Text = "閉じる", AutoSize = true, Anchor = AnchorStyles.Right };
        bClose.Click += (_, _) => Close();
        Row(new Label { AutoSize = true }, new Label { AutoSize = true }, new Label { AutoSize = true }, bClose);
        Controls.Add(t);
        CancelButton = bClose;

        void H(Control c) {
            switch (c)
            {
                case NumericUpDown n: n.ValueChanged += (_, _) => Save(); break;
                case CheckBox cb: cb.CheckedChanged += (_, _) => Save(); break;
                case ComboBox co: co.SelectedIndexChanged += (_, _) => Save(); break;
            }
        }
        foreach (Control c in new Control[] { nDelay, nDuty, cBand, cGate, nGOn, nGOff, cSweep, nSwEnd, nSwTime, cSwLog, cSwPP, cMod, cbModType, nModF, nModD })
            H(c);
        nSwEnd.ValueChanged += (_, _) => Ui.AutoInc(nSwEnd);
        LoadValues();
    }

    public void LoadValues()
    {
        loading = true;
        nDelay.Value = Ui.D(P.DelaySamples, nDelay);
        nDuty.Value = Ui.D(P.Duty, nDuty);
        cBand.Checked = P.BandLimit;
        cGate.Checked = P.Gate;
        nGOn.Value = Ui.D(P.GateOnMs, nGOn);
        nGOff.Value = Ui.D(P.GateOffMs, nGOff);
        cSweep.Checked = P.Sweep;
        nSwEnd.Value = Ui.D(P.SweepEndFreq, nSwEnd); Ui.AutoInc(nSwEnd);
        nSwTime.Value = Ui.D(P.SweepTimeSec, nSwTime);
        cSwLog.Checked = P.SweepLog;
        cSwPP.Checked = P.SweepPingPong;
        cMod.Checked = P.Mod;
        cbModType.SelectedIndex = (int)P.ModType;
        nModF.Value = Ui.D(P.ModFreq, nModF);
        nModD.Value = Ui.D(P.ModDepth, nModD);
        loading = false;
        UpdateEnable();
    }

    void UpdateEnable()
    {
        nGOn.Enabled = nGOff.Enabled = cGate.Checked;
        nSwEnd.Enabled = nSwTime.Enabled = cSwLog.Enabled = cSwPP.Enabled = cSweep.Checked;
        cbModType.Enabled = nModF.Enabled = nModD.Enabled = cMod.Checked;
        lDepthUnit.Text = cbModType.SelectedIndex == 1 ? "Hz (偏移)" : "% (変調度)";
    }

    void Save()
    {
        if (loading) return;
        P.DelaySamples = (int)nDelay.Value;
        P.Duty = (double)nDuty.Value;
        P.BandLimit = cBand.Checked;
        P.Gate = cGate.Checked;
        P.GateOnMs = (double)nGOn.Value;
        P.GateOffMs = (double)nGOff.Value;
        P.Sweep = cSweep.Checked;
        P.SweepEndFreq = (double)nSwEnd.Value;
        P.SweepTimeSec = (double)nSwTime.Value;
        P.SweepLog = cSwLog.Checked;
        P.SweepPingPong = cSwPP.Checked;
        P.Mod = cMod.Checked;
        P.ModType = (ModType)Math.Max(0, cbModType.SelectedIndex);
        P.ModFreq = (double)nModF.Value;
        P.ModDepth = (double)nModD.Value;
        UpdateEnable();
        panel.UpdateState();
        Changed?.Invoke();
    }

    public event Action Changed;
}
