using System.Drawing.Drawing2D;

namespace WaveGenFS;

/// <summary>波形表示。ホイール=時間軸, Ctrl+ホイール=振幅軸。サイズに応じて文字・線幅を拡大。</summary>
public class ScopeView : Control
{
    public static readonly double[] Spans = { 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000 };
    public static readonly double[] YZooms = { 1, 2, 5, 10, 20, 50, 100 };

    public float[] L = Array.Empty<float>(), R = Array.Empty<float>();
    public int Fs = 48000;
    public int SpanIdx = 6;
    public int YZoomIdx;
    public int DispCh;           // 0: L+R, 1: L, 2: R
    public bool Split;           // L/R 分離表示 (上段 L・下段 R)
    public bool Mono;
    public string[] Overlay = Array.Empty<string>();
    public string Status = "";
    public bool Clip;
    public bool WaveClipped;   // 表示データが ±100% でクリップされている

    public Color BackCol = Color.Black, FrameCol = Color.FromArgb(0xD0, 0x20, 0x20),
                 GridCol = Color.FromArgb(0x90, 0x20, 0x20), LCol = Color.Lime,
                 RCol = Color.FromArgb(0xFF, 0x00, 0xFF), TextCol = Color.FromArgb(0x40, 0xFF, 0x40);

    public event EventHandler SpanChanged;

    public double SpanMs => Spans[SpanIdx];

    public ScopeView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
    }


    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int d = e.Delta > 0 ? -1 : 1;
        if ((ModifierKeys & Keys.Control) != 0)
            YZoomIdx = Math.Clamp(YZoomIdx - d, 0, YZooms.Length - 1);
        else
            SpanIdx = Math.Clamp(SpanIdx + d, 0, Spans.Length - 1);
        SpanChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    static double NiceStep(double range, int maxDiv)
    {
        double raw = range / maxDiv;
        double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var m in new[] { 1.0, 2, 5, 10 })
            if (m * p >= raw) return m * p;
        return 10 * p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackCol);
        float fsz = Math.Clamp(Math.Min(Height / 42f, Width / 75f), 8f, 26f);
        using var font = new Font("Consolas", fsz, GraphicsUnit.Point);
        float scale = fsz / 9f;
        var tb = new SolidBrush(TextCol);
        var lineH = font.GetHeight(g);

        double span = SpanMs;
        int nSamp = Math.Min(L.Length, (int)Math.Ceiling(span * Fs / 1000.0) + 1);
        bool showL = DispCh != 2, showR = DispCh != 1 && !Mono;

        // 振幅スケール: 設定倍率で収まらない場合は波形全体が入るレンジへ自動変更
        double ymax = 1.0 / YZooms[YZoomIdx];
        double peak = 0;
        for (int i = 0; i < nSamp; i++)
        {
            if (showL) peak = Math.Max(peak, Math.Abs(L[i]));
            if (showR) peak = Math.Max(peak, Math.Abs(R[i]));
        }
        bool auto = false;
        if (peak > ymax * 1.0001)
        {
            auto = true;
            double need = peak * 1.05, p10 = Math.Pow(10, Math.Floor(Math.Log10(need)));
            ymax = new[] { 1.0, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }.Select(m => m * p10).First(v => v >= need - 1e-12);
            if (peak <= 1.0001) ymax = Math.Min(ymax, 1.0);   // クリップ後の波形は ±100% に収まる
        }
        string lblFmt = ymax < 0.1 || (ymax * 100) % 1 != 0 ? "+0.0'%';-0.0'%';0" : "+0'%';-0'%';0";
        float leftM = g.MeasureString((-ymax * 100).ToString(lblFmt), font).Width + 8 * scale;
        var full = new RectangleF(leftM, 6 * scale, Width - leftM - 14 * scale, Height - 6 * scale - lineH - 8 * scale);
        // L/R 分離: 上段 L・下段 R (同じ振幅スケール)
        bool isSplit = Split && showL && showR;
        var areas = new List<(RectangleF rect, int ch)>();   // ch: 0=L, 1=R, -1=表示ch どおり
        if (isSplit)
        {
            float gap = lineH * 1.2f, h = (full.Height - gap) / 2;
            areas.Add((new RectangleF(full.X, full.Y, full.Width, h), 0));
            areas.Add((new RectangleF(full.X, full.Y + h + gap, full.Width, h), 1));
        }
        else areas.Add((full, -1));
        if (full.Width < 20 || areas.Any(ar => ar.rect.Height < 20)) { tb.Dispose(); return; }

        using var gridPen = new Pen(GridCol, Math.Max(1, scale * 0.8f)) { DashStyle = DashStyle.Dash };
        using var framePen = new Pen(FrameCol, Math.Max(1, scale));
        var clip = g.Clip;

        for (int ai = 0; ai < areas.Count; ai++)
        {
            var plot = areas[ai].rect;
            int ach = areas[ai].ch;
            bool isLast = ai == areas.Count - 1;

            // 横線 (振幅)
            double ystep = NiceStep(2 * ymax, plot.Height > 400 ? 8 : plot.Height > 160 ? 4 : 2);
            var ys = new List<double>();
            for (double y = -Math.Floor(ymax / ystep + 1e-9) * ystep; y <= ymax + 1e-9; y += ystep) ys.Add(y);
            // 上下端の値も必ず表示 (近すぎる目盛りは省く)
            ys.RemoveAll(y => Math.Abs(Math.Abs(y) - ymax) > 1e-9 && ymax - Math.Abs(y) < ystep * 0.4);
            if (!ys.Any(y => Math.Abs(y - ymax) < 1e-9)) { ys.Add(ymax); ys.Add(-ymax); }
            foreach (var y in ys)
            {
                float py = (float)(plot.Top + (ymax - y) / (2 * ymax) * plot.Height);
                if (Math.Abs(y) > 1e-12 && Math.Abs(Math.Abs(y) - ymax) > 1e-9) g.DrawLine(gridPen, plot.Left, py, plot.Right, py);
                if (Math.Abs(y) < 1e-12) g.DrawLine(framePen, plot.Left, py, plot.Right, py);
                string s = (y * 100).ToString(lblFmt);
                var sz = g.MeasureString(s, font);
                g.DrawString(s, font, tb, plot.Left - sz.Width - 2 * scale, py - sz.Height / 2);
            }
            // 縦線 (時間)。時間の数字は一番下の枠だけ
            double xstep = NiceStep(span, Math.Max(4, (int)(plot.Width / (70 * scale))));
            for (double x = xstep; x < span - 1e-9; x += xstep)
            {
                float px = (float)(plot.Left + x / span * plot.Width);
                g.DrawLine(gridPen, px, plot.Top, px, plot.Bottom);
                if (!isLast) continue;
                string s = x.ToString("0.###");
                var sz = g.MeasureString(s, font);
                g.DrawString(s, font, tb, px - sz.Width / 2, plot.Bottom + 2 * scale);
            }
            if (isLast) g.DrawString("ms", font, tb, plot.Right - g.MeasureString("ms", font).Width, plot.Bottom + 2 * scale);
            g.DrawRectangle(framePen, plot.X, plot.Y, plot.Width, plot.Height);

            // 波形
            g.SetClip(plot);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (showR && ach != 0) DrawTrace(g, R, nSamp, plot, ymax, RCol, scale);
            if (showL && ach != 1) DrawTrace(g, L, nSamp, plot, ymax, LCol, scale);
            g.SmoothingMode = SmoothingMode.None;
            g.Clip = clip;

            // 凡例
            string legend = ach == 0 ? "L" : ach == 1 ? "R" : Mono ? "Mono" : DispCh == 0 ? "L  R" : DispCh == 1 ? "L" : "R";
            var lsz = g.MeasureString(legend, font);
            float lx = plot.Right - lsz.Width - 6 * scale, ly = plot.Bottom - lsz.Height - 4 * scale;
            if (legend == "L  R")
            {
                using var lb = new SolidBrush(LCol); using var rb = new SolidBrush(RCol);
                g.DrawString("L", font, lb, lx, ly);
                g.DrawString("R", font, rb, lx + g.MeasureString("L  ", font).Width - g.MeasureString("R", font).Width * 0.3f, ly);
            }
            else
            {
                using var lb = new SolidBrush(legend == "R" ? RCol : LCol);
                g.DrawString(legend, font, lb, lx, ly);
            }
        }

        var top = areas[0].rect;
        var bottom = areas[^1].rect;
        g.SetClip(full);
        // オーバーレイ文字
        float ty = top.Top + 4 * scale;
        float statusW = string.IsNullOrEmpty(Status) ? 0 : g.MeasureString(Status, font).Width;
        float ovW = Overlay.Length == 0 ? 0 : g.MeasureString(Overlay[0], font).Width;
        if (statusW > 0 && ovW + statusW + 20 * scale > top.Width) ty += lineH;
        foreach (var line in Overlay)
        {
            g.DrawString(line, font, tb, top.Left + 6 * scale, ty);
            ty += lineH;
        }
        if (!string.IsNullOrEmpty(Status))
        {
            var sz = g.MeasureString(Status, font);
            using var sb = new SolidBrush(Clip ? Color.Red : TextCol);
            g.DrawString(Status, font, sb, top.Right - sz.Width - 6 * scale, top.Top + 4 * scale);
        }
        var notes = new List<string>();
        if (auto) notes.Add($"自動スケール ±{ymax * 100:0.###}%");
        if (WaveClipped) notes.Add("クリップ発生 (±100%で制限)");
        if (notes.Count > 0)
        {
            string a = string.Join("   ", notes);
            var sz = g.MeasureString(a, font);
            using var ab = new SolidBrush(WaveClipped ? Color.Red : TextCol);
            g.DrawString(a, font, ab, bottom.Left + 6 * scale, bottom.Bottom - sz.Height - 4 * scale);
        }
        g.Clip = clip;
        tb.Dispose();
    }

    void DrawTrace(Graphics g, float[] d, int n, RectangleF plot, double ymax, Color col, float scale)
    {
        if (n < 2) return;
        using var pen = new Pen(col, Math.Max(1f, scale * 1.1f));
        double span = SpanMs * Fs / 1000.0;
        float Y(double v) => (float)(plot.Top + (ymax - v) / (2 * ymax) * plot.Height);
        int w = (int)plot.Width;
        if (n <= w * 2)
        {
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
                pts[i] = new PointF((float)(plot.Left + i / span * plot.Width), Math.Clamp(Y(d[i]), -1e5f, 1e5f));
            g.DrawLines(pen, pts);
        }
        else
        {
            g.SmoothingMode = SmoothingMode.None;
            float prevY = Y(d[0]);
            for (int x = 0; x < w; x++)
            {
                int a = (int)(x * span / w), b = Math.Min(n, (int)((x + 1) * span / w) + 1);
                if (a >= n) break;
                float mn = d[a], mx = d[a];
                for (int i = a; i < b; i++) { if (d[i] < mn) mn = d[i]; if (d[i] > mx) mx = d[i]; }
                float y1 = Math.Clamp(Y(mx), -1e5f, 1e5f), y2 = Math.Clamp(Y(mn), -1e5f, 1e5f);
                g.DrawLine(pen, plot.Left + x, Math.Min(y1, prevY), plot.Left + x, Math.Max(y2, prevY) + 0.5f);
                prevY = (y1 + y2) / 2;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }
    }
}
