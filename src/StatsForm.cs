// 统计窗口：概览仪表盘（功率曲线 / 每日用电 / 构成环图）+ 会话记录表
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace PowerMonitor
{
    internal sealed class DayKwh
    {
        public DateTime Day;
        public double Wh;
    }

    internal sealed class MonthStat
    {
        public DateTime Month;
        public double Wh, PeakW, Cost, Minutes;
        public int Sessions;

        public double AvgW { get { return Minutes > 0 ? Wh * 60.0 / Minutes : 0; } }
        public double Kwh { get { return Wh / 1000.0; } }
    }

    internal sealed class StatsForm : Form
    {
        const int DesignW = 880;
        const int DesignH = 664;

        readonly HistoryStore _store;
        readonly AppSettings _cfg;
        float _scale = 1f;
        readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        int _view;          // 0 概览 1 会话
        int _range = 1;     // 0 本次开机 1 今天 2 近7天 3 近30天
        Point _mouse = new Point(-100, -100);
        int _hover = -1;
        int _scroll;
        DateTime _lastNotify = DateTime.MinValue;

        // 布局缓存
        RectangleF _rClose, _rView0, _rView1, _rExport, _rRefresh, _rPath;
        readonly RectangleF[] _rRange = new RectangleF[4];
        readonly RectangleF[] _rCard = new RectangleF[5];
        RectangleF _rChart, _rDaily, _rDonut, _rTable, _rMonths, _rView2;

        // 绘制用数据
        List<HistoryBucket> _plot = new List<HistoryBucket>();
        DateTime _plotFrom, _plotTo;
        double _plotMax = 100;
        List<DayKwh> _days = new List<DayKwh>();
        double _totalWh, _cpuWh, _gpuWh, _platWh, _peakW, _avgW;
        DateTime _peakAt;
        int _activeMinutes;
        List<HistorySession> _sessions = new List<HistorySession>();
        List<MonthStat> _months = new List<MonthStat>();
        int _monthScroll;

        static readonly Color Bg1 = Color.FromArgb(255, 33, 37, 45);
        static readonly Color Bg2 = Color.FromArgb(255, 15, 17, 21);
        static readonly Color CardBg = Color.FromArgb(255, 24, 27, 34);
        static readonly Color CardBorder = Color.FromArgb(20, 255, 255, 255);
        static readonly Color TextMain = Color.FromArgb(255, 240, 244, 250);
        static readonly Color TextSec = Color.FromArgb(255, 138, 148, 166);
        static readonly Color TextTer = Color.FromArgb(255, 100, 110, 126);
        static readonly Color Accent = Color.FromArgb(255, 78, 158, 255);
        static readonly Color AccentSoft = Color.FromArgb(255, 154, 133, 255);
        static readonly Color ColCpu = Color.FromArgb(255, 78, 158, 255);
        static readonly Color ColGpu = Color.FromArgb(255, 158, 133, 255);
        static readonly Color ColPlat = Color.FromArgb(255, 96, 108, 126);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetDpiForSystem();

        public StatsForm(HistoryStore store, AppSettings cfg)
        {
            _store = store;
            _cfg = cfg;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg2;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "功耗统计";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _scale = DpiScale();
            ClientSize = new Size((int)Math.Round(DesignW * _scale), (int)Math.Round(DesignH * _scale));
        }

        static float DpiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
            }
            catch { return 1f; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                float dpi = 0;
                try { dpi = GetDpiForWindow(Handle) / 96f; } catch { }
                if (dpi <= 0) dpi = DeviceDpi / 96f;
                if (Math.Abs(dpi - _scale) > 0.01f || ClientSize.Width != (int)Math.Round(DesignW * dpi))
                {
                    _scale = dpi;
                    ClientSize = new Size((int)Math.Round(DesignW * _scale), (int)Math.Round(DesignH * _scale));
                }
                Paths.Log(string.Format("stats dpi win={0} sys={1} scale={2:F2} client={3}x{4}", GetDpiForWindow(Handle), GetDpiForSystem(), _scale, ClientSize.Width, ClientSize.Height));
            }
            catch { }
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch { }
            ClearFonts();
            Paths.Log(string.Format("stats dpi={0} client={1}x{2} scale={3}", DeviceDpi, ClientSize.Width, ClientSize.Height, _scale));
        }

        void ClearFonts()
        {
            foreach (KeyValuePair<string, Font> kv in _fonts) { try { kv.Value.Dispose(); } catch { } }
            _fonts.Clear();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ClearFonts();
            base.OnFormClosed(e);
        }

        Font F(float px, FontStyle style)
        {
            string key = px.ToString("0.0", CultureInfo.InvariantCulture) + "|" + ((int)style).ToString();
            Font f;
            if (_fonts.TryGetValue(key, out f)) return f;
            f = Fonts.Make(px * _scale, style);
            _fonts[key] = f;
            return f;
        }

        float S(float v) { return v * _scale; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) // WM_NCHITTEST
            {
                base.WndProc(ref m);
                int lp = m.LParam.ToInt32();
                Point p = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                Point c = PointToClient(p);
                if (c.Y < S(52) && !new RectangleF(S(DesignW - 50), S(12), S(34), S(34)).Contains(c))
                {
                    m.Result = (IntPtr)2; // HTCAPTION
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void NotifyData(Reading r)
        {
            if (!Visible) return;
            DateTime now = DateTime.Now;
            if ((now - _lastNotify).TotalMilliseconds < 700) return;
            _lastNotify = now;
            Invalidate();
        }

        public void RefreshView()
        {
            Invalidate();
        }

        public int View
        {
            set { _view = value; Invalidate(); }
        }

        public void DebugHover(int x, int y)
        {
            _mouse = new Point(x, y);
        }

        // ---------------- 数据处理 ----------------

        void BuildData()
        {
            DateTime now = DateTime.Now;
            DateTime from;
            switch (_range)
            {
                case 0: from = _store.Boot; break;
                case 1: from = now.Date; break;
                case 2: from = now.Date.AddDays(-6); break;
                default: from = now.Date.AddDays(-29); break;
            }
            if (_range == 0 && from > now.AddHours(-1)) from = now.AddHours(-1);

            List<HistoryBucket> raw = _store.BucketsSince(from);

            // 数据刚开始积累时，把横轴缩放到实际有数据的区间，避免曲线被挤成一条竖线
            _plotFrom = from;
            if (raw.Count > 1 && raw[0].Ts > from.AddMinutes(10))
            {
                DateTime zoom = raw[0].Ts.AddMinutes(-10);
                if (zoom > from) _plotFrom = zoom;
            }
            _plotTo = now;
            _plot = Aggregate(raw, 340);

            _totalWh = 0; _cpuWh = 0; _gpuWh = 0; _platWh = 0; _peakW = 0; _activeMinutes = 0;
            _peakAt = DateTime.MinValue;
            for (int i = 0; i < raw.Count; i++)
            {
                HistoryBucket b = raw[i];
                _totalWh += b.Wh;
                _cpuWh += b.CpuWh;
                _gpuWh += b.GpuWh;
                _platWh += b.PlatWh;
                _activeMinutes += (int)Math.Round(b.Weight);
                if (b.N > 0 && b.MaxW > _peakW) { _peakW = b.MaxW; _peakAt = b.Ts; }
            }
            _avgW = _activeMinutes > 0 ? _totalWh * 60.0 / _activeMinutes : 0;

            double max = 10;
            for (int i = 0; i < _plot.Count; i++) if (_plot[i].Avg > max) max = _plot[i].Avg;
            _plotMax = NiceMax(max * 1.18);

            // 近 14 天
            _days = new List<DayKwh>();
            DateTime today = DateTime.Now.Date;
            List<HistoryBucket> all = _store.AllBuckets();
            for (int d = 13; d >= 0; d--)
            {
                DateTime day = today.AddDays(-d);
                DayKwh dk = new DayKwh();
                dk.Day = day;
                for (int i = 0; i < all.Count; i++)
                {
                    HistoryBucket b = all[i];
                    if (b.Ts >= day && b.Ts < day.AddDays(1)) dk.Wh += b.Wh;
                }
                _days.Add(dk);
            }

            _sessions = _store.Sessions();
            _sessions.Sort(delegate(HistorySession a, HistorySession b) { return b.Boot.CompareTo(a.Boot); });

            BuildMonths(all);
        }

        static readonly double[] NiceSteps = new double[] { 1, 2, 2.5, 5, 10, 12.5, 15, 20, 25, 30, 40, 50, 75, 100, 125, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 3000 };

        // 取 4 等分后仍是整数的刻度上限，避免出现 38/113 这种读数
        static double NiceMax(double v)
        {
            double raw = Math.Max(v, 4) / 4.0;
            for (int i = 0; i < NiceSteps.Length; i++) if (raw <= NiceSteps[i]) return NiceSteps[i] * 4.0;
            return Math.Ceiling(raw / 1000.0) * 1000.0 * 4.0;
        }

        static List<HistoryBucket> Aggregate(List<HistoryBucket> src, int maxPoints)
        {
            if (src.Count <= maxPoints) return new List<HistoryBucket>(src);
            int step = (int)Math.Ceiling(src.Count / (double)maxPoints);
            List<HistoryBucket> outl = new List<HistoryBucket>();
            HistoryBucket acc = null;
            for (int i = 0; i < src.Count; i++)
            {
                if (acc == null)
                {
                    acc = new HistoryBucket();
                    acc.Ts = src[i].Ts;
                    acc.MaxW = double.MinValue;
                    acc.MinW = double.MaxValue;
                    acc.Weight = 0;
                }
                acc.MergeFrom(src[i]);
                if ((i + 1) % step == 0) { outl.Add(acc); acc = null; }
            }
            if (acc != null) outl.Add(acc);
            return outl;
        }

        // ---------------- 绘制 ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintContent(e.Graphics);
        }

        public void SaveSnapshot(string path)
        {
            using (Bitmap bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp)) PaintContent(g);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        void PaintContent(Graphics g)
        {
            BuildData();
            ComputeLayout();

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            int w = ClientSize.Width, h = ClientSize.Height;
            using (LinearGradientBrush bg = new LinearGradientBrush(
                new Rectangle(0, 0, w, h), Bg1, Bg2, 100f))
                g.FillRectangle(bg, 0, 0, w, h);
            using (Pen edge = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
                g.DrawRectangle(edge, 0, 0, w - 1, h - 1);

            DrawHeader(g);
            if (_view == 0) DrawOverview(g);
            else if (_view == 1) DrawSessions(g);
            else DrawMonths(g);

            using (Pen sep = new Pen(Color.FromArgb(18, 255, 255, 255), 1f))
                g.DrawLine(sep, S(22), S(DesignH - 52), S(DesignW - 22), S(DesignH - 52));
            DrawFooter(g);
        }

        void ComputeLayout()
        {
            float pad = S(22);
            float w = S(DesignW);
            _rClose = new RectangleF(w - S(46), S(13), S(26), S(26));
            _rView0 = new RectangleF(pad, S(58), S(76), S(30));
            _rView1 = new RectangleF(pad + S(84), S(58), S(92), S(30));
            _rView2 = new RectangleF(pad + S(184), S(58), S(92), S(30));
            _rMonths = new RectangleF(pad, S(182), w - pad * 2, S(DesignH - 182 - 62));

            float[] rw = new float[] { S(84), S(58), S(58), S(66) };
            float rx = w - pad;
            for (int i = 3; i >= 0; i--)
            {
                rx -= rw[i];
                _rRange[i] = new RectangleF(rx, S(58), rw[i], S(30));
                rx -= S(6);
            }

            float gap = S(10);
            float cw = (w - pad * 2 - gap * 4) / 5f;
            for (int i = 0; i < 5; i++)
                _rCard[i] = new RectangleF(pad + i * (cw + gap), S(96), cw, S(74));

            _rChart = new RectangleF(pad, S(182), w - pad * 2, S(208));
            float bh = S(196);
            float bw = (w - pad * 2) * 0.615f;
            _rDaily = new RectangleF(pad, S(402), bw - S(6), bh);
            _rDonut = new RectangleF(pad + bw + S(6), S(402), w - pad * 2 - bw - S(6), bh);

            _rExport = new RectangleF(w - pad - S(92), S(DesignH - 42), S(92), S(28));
            _rRefresh = new RectangleF(w - pad - S(92) - S(66), S(DesignH - 42), S(58), S(28));
            _rPath = new RectangleF(pad, S(DesignH - 40), w - pad * 2 - S(170), S(24));

            _rTable = new RectangleF(pad, S(96), w - pad * 2, S(DesignH - 96 - 62));
        }

        void DrawHeader(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(TextMain))
                g.DrawString("功耗统计", F(17, FontStyle.Bold), b, S(22), S(14));
            using (SolidBrush b = new SolidBrush(TextTer))
                g.DrawString("历史用电 · 平均功率 · 分项构成", F(11, FontStyle.Regular), b, S(22) + g.MeasureString("功耗统计", F(17, FontStyle.Bold), PointF.Empty, StringFormat.GenericTypographic).Width + S(10), S(20));

            // 关闭
            bool hot = _rClose.Contains(_mouse);
            using (SolidBrush b = new SolidBrush(hot ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(0, 255, 255, 255)))
                g.FillEllipse(b, _rClose);
            using (Pen pen = new Pen(hot ? TextMain : TextSec, S(1.6f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                float m = S(9);
                g.DrawLine(pen, _rClose.Left + m, _rClose.Top + m, _rClose.Right - m, _rClose.Bottom - m);
                g.DrawLine(pen, _rClose.Right - m, _rClose.Top + m, _rClose.Left + m, _rClose.Bottom - m);
            }

            Pill(g, _rView0, "概览", _view == 0);
            Pill(g, _rView1, "会话记录", _view == 1);
            Pill(g, _rView2, "月度账单", _view == 2);

            string[] names = new string[] { "本次开机", "今天", "近 7 天", "近 30 天" };
            if (_view == 0)
                for (int i = 0; i < 4; i++) Pill(g, _rRange[i], names[i], _range == i);
        }

        void Pill(Graphics g, RectangleF r, string text, bool active)
        {
            bool hot = _mouse.X >= r.Left && _mouse.X <= r.Right && _mouse.Y >= r.Top && _mouse.Y <= r.Bottom;
            Color fill = active ? Color.FromArgb(46, Accent) : (hot ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(12, 255, 255, 255));
            Color border = active ? Color.FromArgb(120, Accent) : Color.FromArgb(10, 255, 255, 255);
            using (GraphicsPath p = IconFactory.RoundRect(r, r.Height / 2f))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
            }
            Color tc = active ? Color.FromArgb(255, 190, 220, 255) : TextSec;
            using (SolidBrush b = new SolidBrush(tc))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, F(11.5f, FontStyle.Regular), b, r, sf);
            }
        }

        void DrawOverview(Graphics g)
        {
            string v1 = _totalWh >= 1000 ? (_totalWh / 1000).ToString("0.000", CultureInfo.InvariantCulture) + " kWh"
                                        : _totalWh.ToString("0.0", CultureInfo.InvariantCulture) + " Wh";
            string v2 = _avgW.ToString("0.0", CultureInfo.InvariantCulture) + " W";
            string v3 = _peakW.ToString("0.0", CultureInfo.InvariantCulture) + " W";
            string v4;
            if (_activeMinutes >= 60) v4 = (_activeMinutes / 60) + " 小时 " + (_activeMinutes % 60) + " 分";
            else v4 = _activeMinutes + " 分钟";
            string v5 = "¥" + (_totalWh / 1000.0 * _cfg.PricePerKwh).ToString("0.00", CultureInfo.InvariantCulture);

            string s1 = RangeName() + "累计";
            string s2 = _peakAt == DateTime.MinValue ? "—" : "峰值 " + _peakAt.ToString("HH:mm");
            string s3 = _peakAt == DateTime.MinValue ? "—" : _peakAt.ToString("MM-dd HH:mm");
            string s4 = "采样 " + _activeMinutes + " 分钟";
            string s5 = "¥" + _cfg.PricePerKwh.ToString("0.00", CultureInfo.InvariantCulture) + " / kWh";

            Card(g, _rCard[0], "总耗电", v1, s1, Accent);
            Card(g, _rCard[1], "平均功率", v2, "按采样时长折算", Accent);
            Card(g, _rCard[2], "峰值功率", v3, s3, Color.FromArgb(255, 255, 128, 100));
            Card(g, _rCard[3], "运行时长", v4, s4, AccentSoft);
            Card(g, _rCard[4], "估算电费", v5, s5, Color.FromArgb(255, 80, 205, 140));

            DrawChartPanel(g);
            DrawDailyPanel(g);
            DrawDonutPanel(g);
        }

        string RangeName()
        {
            switch (_range)
            {
                case 0: return "本次开机";
                case 1: return "今日";
                case 2: return "近 7 天";
                default: return "近 30 天";
            }
        }

        void Card(Graphics g, RectangleF r, string label, string value, string sub, Color accent)
        {
            using (GraphicsPath p = IconFactory.RoundRect(r, S(12)))
            {
                using (SolidBrush b = new SolidBrush(CardBg)) g.FillPath(b, p);
                using (Pen pen = new Pen(CardBorder, 1f)) g.DrawPath(pen, p);
            }
            using (SolidBrush b = new SolidBrush(accent))
                g.FillRectangle(b, r.Left + S(14), r.Top + S(14), S(3), S(12));
            using (SolidBrush b = new SolidBrush(TextSec))
                g.DrawString(label, F(11, FontStyle.Regular), b, r.Left + S(23), r.Top + S(11));

            float size = 20f;
            while (size > 12f && g.MeasureString(value, F(size, FontStyle.Bold), PointF.Empty, StringFormat.GenericTypographic).Width > r.Width - S(24))
                size -= 0.5f;
            using (SolidBrush b = new SolidBrush(TextMain))
                g.DrawString(value, F(size, FontStyle.Bold), b, r.Left + S(14), r.Top + S(28));
            using (SolidBrush b = new SolidBrush(TextTer))
                g.DrawString(sub, F(10, FontStyle.Regular), b, r.Left + S(14), r.Bottom - S(19));
        }

        void Panel(Graphics g, RectangleF r, string title, string hint)
        {
            using (GraphicsPath p = IconFactory.RoundRect(r, S(12)))
            {
                using (SolidBrush b = new SolidBrush(CardBg)) g.FillPath(b, p);
                using (Pen pen = new Pen(CardBorder, 1f)) g.DrawPath(pen, p);
            }
            using (SolidBrush b = new SolidBrush(TextSec))
                g.DrawString(title, F(11.5f, FontStyle.Regular), b, r.Left + S(14), r.Top + S(11));
            if (!string.IsNullOrEmpty(hint))
            {
                using (SolidBrush b = new SolidBrush(TextTer))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Far;
                    g.DrawString(hint, F(10, FontStyle.Regular), b, new RectangleF(r.Left, r.Top + S(12), r.Width - S(14), S(16)), sf);
                }
            }
        }

        void DrawChartPanel(Graphics g)
        {
            string hint = _range == 0 ? "本次开机 · 每分钟一点" : RangeName() + " · 每分钟一点";
            Panel(g, _rChart, "功率曲线 (W)", hint);

            RectangleF plot = new RectangleF(
                _rChart.Left + S(46), _rChart.Top + S(36),
                _rChart.Width - S(46) - S(16), _rChart.Height - S(36) - S(24));
            if (plot.Width < 20 || plot.Height < 20) return;

            using (Pen grid = new Pen(Color.FromArgb(16, 255, 255, 255), 1f))
            using (SolidBrush tb = new SolidBrush(TextTer))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                for (int i = 0; i <= 4; i++)
                {
                    float y = plot.Bottom - plot.Height * i / 4f;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    double val = _plotMax * i / 4.0;
                    g.DrawString(val.ToString("0", CultureInfo.InvariantCulture), F(9.5f, FontStyle.Regular), tb,
                        new RectangleF(_rChart.Left + S(6), y - S(8), S(34), S(16)), sf);
                }
            }

            double span = (_plotTo - _plotFrom).TotalSeconds;
            if (span < 60) span = 60;

            if (_plot.Count >= 2)
            {
                // 按时间定位，遇到长时间空档断开
                List<List<PointF>> segs = new List<List<PointF>>();
                List<PointF> cur = null;
                DateTime prev = DateTime.MinValue;
                for (int i = 0; i < _plot.Count; i++)
                {
                    HistoryBucket b = _plot[i];
                    float x = plot.Left + (float)((b.Ts - _plotFrom).TotalSeconds / span * plot.Width);
                    if (x < plot.Left) x = plot.Left;
                    if (x > plot.Right) x = plot.Right;
                    float y = plot.Bottom - (float)(b.Avg / _plotMax * plot.Height);
                    if (y < plot.Top) y = plot.Top;
                    if (cur == null || (prev != DateTime.MinValue && (b.Ts - prev).TotalSeconds > 900))
                    {
                        cur = new List<PointF>();
                        segs.Add(cur);
                    }
                    cur.Add(new PointF(x, y));
                    prev = b.Ts;
                }

                foreach (List<PointF> seg in segs)
                {
                    if (seg.Count < 2) continue;
                    using (GraphicsPath area = new GraphicsPath())
                    {
                        area.AddLine(seg[0].X, plot.Bottom, seg[0].X, seg[0].Y);
                        for (int i = 1; i < seg.Count; i++) area.AddLine(seg[i - 1], seg[i]);
                        area.AddLine(seg[seg.Count - 1].X, plot.Bottom, seg[0].X, plot.Bottom);
                        area.CloseFigure();
                        using (LinearGradientBrush fb = new LinearGradientBrush(
                            new RectangleF(plot.Left, plot.Top, Math.Max(1, plot.Width), Math.Max(1, plot.Height)),
                            Color.FromArgb(78, 78, 158, 255), Color.FromArgb(4, 120, 150, 255), 90f))
                            g.FillPath(fb, area);
                    }
                    using (GraphicsPath line = new GraphicsPath())
                    {
                        line.AddLines(seg.ToArray());
                        using (Pen pen = new Pen(Accent, S(1.7f)))
                        {
                            pen.LineJoin = LineJoin.Round;
                            g.DrawPath(pen, line);
                        }
                    }
                }
            }
            else
            {
                using (SolidBrush b = new SolidBrush(TextTer))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("暂无足够数据，运行一两分钟后自动出现", F(11, FontStyle.Regular), b, plot, sf);
                }
            }

            // X 轴刻度
            using (SolidBrush tb = new SolidBrush(TextTer))
            using (StringFormat sf = new StringFormat())
            {
                for (int i = 0; i <= 4; i++)
                {
                    float x = plot.Left + plot.Width * i / 4f;
                    DateTime t = _plotFrom.AddSeconds(span * i / 4.0);
                    string label = span > 86400 * 2 ? t.ToString("MM-dd") : (span > 86400 ? t.ToString("MM-dd HH时") : t.ToString("HH:mm"));
                    sf.Alignment = i == 0 ? StringAlignment.Near : (i == 4 ? StringAlignment.Far : StringAlignment.Center);
                    g.DrawString(label, F(9.5f, FontStyle.Regular), tb,
                        new RectangleF(x - S(40), plot.Bottom + S(5), S(80), S(14)), sf);
                }
            }

            // 悬停十字与提示
            _hover = -1;
            if (_plot.Count > 1 && plot.Contains(_mouse))
            {
                int best = 0; float bestD = float.MaxValue;
                for (int i = 0; i < _plot.Count; i++)
                {
                    float x = plot.Left + (float)((_plot[i].Ts - _plotFrom).TotalSeconds / span * plot.Width);
                    float d = Math.Abs(x - _mouse.X);
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (bestD < S(26))
                {
                    _hover = best;
                    HistoryBucket b = _plot[best];
                    float x = plot.Left + (float)((b.Ts - _plotFrom).TotalSeconds / span * plot.Width);
                    float y = plot.Bottom - (float)(b.Avg / _plotMax * plot.Height);
                    using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                    }
                    using (SolidBrush b2 = new SolidBrush(Accent))
                        g.FillEllipse(b2, x - S(3.2f), y - S(3.2f), S(6.4f), S(6.4f));
                    using (SolidBrush b2 = new SolidBrush(Color.FromArgb(255, 245, 248, 253)))
                        g.FillEllipse(b2, x - S(1.5f), y - S(1.5f), S(3f), S(3f));

                    string t1 = b.Ts.ToString(span > 86400 ? "MM-dd HH:mm" : "HH:mm");
                    string t2 = "平均 " + b.Avg.ToString("0.0", CultureInfo.InvariantCulture) + " W";
                    string t3 = "峰值 " + (b.N > 0 ? b.MaxW.ToString("0.0", CultureInfo.InvariantCulture) : "—") + " W";
                    DrawTip(g, new PointF(x, y), new string[] { t1, t2, t3 }, plot);
                }
            }
        }

        void DrawTip(Graphics g, PointF anchor, string[] lines, RectangleF bounds)
        {
            float w = S(112), lh = S(15), h = S(12) + lines.Length * lh;
            float x = anchor.X + S(12), y = anchor.Y - h - S(8);
            if (x + w > bounds.Right) x = anchor.X - w - S(12);
            if (y < bounds.Top) y = anchor.Y + S(12);
            RectangleF r = new RectangleF(x, y, w, h);
            using (GraphicsPath p = IconFactory.RoundRect(r, S(8)))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 12, 15, 20))) g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(40, 255, 255, 255), 1f)) g.DrawPath(pen, p);
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 160, 170, 186)))
                g.DrawString(lines[0], F(9.5f, FontStyle.Regular), b, r.Left + S(9), r.Top + S(5));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 230, 236, 245)))
                g.DrawString(lines[1], F(10.5f, FontStyle.Bold), b, r.Left + S(9), r.Top + S(5) + lh);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 130, 140, 158)))
                g.DrawString(lines[2], F(9.5f, FontStyle.Regular), b, r.Left + S(9), r.Top + S(5) + lh * 2);
        }

        void DrawDailyPanel(Graphics g)
        {
            double max = 0.001;
            for (int i = 0; i < _days.Count; i++) if (_days[i].Wh > max) max = _days[i].Wh;
            string hint = "峰值 " + (max / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " kWh";
            Panel(g, _rDaily, "近 14 天用电 (kWh)", hint);

            RectangleF plot = new RectangleF(
                _rDaily.Left + S(14), _rDaily.Top + S(36),
                _rDaily.Width - S(28), _rDaily.Height - S(36) - S(26));
            if (plot.Width < 20) return;

            using (Pen grid = new Pen(Color.FromArgb(14, 255, 255, 255), 1f))
                g.DrawLine(grid, plot.Left, plot.Bottom, plot.Right, plot.Bottom);

            int n = _days.Count;
            float slot = plot.Width / n;
            float bw = slot * 0.56f;
            for (int i = 0; i < n; i++)
            {
                DayKwh d = _days[i];
                float h = (float)(d.Wh / max * plot.Height);
                if (d.Wh > 0 && h < S(2)) h = S(2);
                float x = plot.Left + slot * i + (slot - bw) / 2f;
                float y = plot.Bottom - h;
                bool today = d.Day == DateTime.Today;
                using (GraphicsPath p = IconFactory.RoundRect(new RectangleF(x, y, bw, Math.Max(h, 1)), Math.Min(S(4), bw / 2)))
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(
                        new RectangleF(x, y, bw, Math.Max(h, 1)),
                        today ? Color.FromArgb(255, 120, 186, 255) : Color.FromArgb(210, 78, 158, 255),
                        today ? Color.FromArgb(200, 130, 124, 255) : Color.FromArgb(110, 120, 148, 190), 90f))
                        g.FillPath(b, p);
                }
                if (i % 2 == 0 || today)
                {
                    using (SolidBrush b = new SolidBrush(today ? TextSec : TextTer))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        g.DrawString(d.Day.ToString("M/d"), F(9, FontStyle.Regular), b,
                            new RectangleF(x - S(12) + bw / 2, plot.Bottom + S(4), S(40), S(14)), sf);
                    }
                }
                if (d.Wh >= 5)
                {
                    using (SolidBrush b = new SolidBrush(TextSec))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        string s = (d.Wh / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
                        g.DrawString(s, F(8.5f, FontStyle.Regular), b, new RectangleF(x - S(10), y - S(14), bw + S(20), S(12)), sf);
                    }
                }
            }
        }

        void DrawDonutPanel(Graphics g)
        {
            Panel(g, _rDonut, "用电构成", "");

            double total = _cpuWh + _gpuWh + _platWh;
            float cx = _rDonut.Left + S(74), cy = _rDonut.Top + S(104);
            float rad = S(46);
            float thick = S(13);

            using (Pen bgPen = new Pen(Color.FromArgb(24, 255, 255, 255), thick))
                g.DrawEllipse(bgPen, cx - rad, cy - rad, rad * 2, rad * 2);

            if (total > 0.0001)
            {
                float start = -90f;
                double[] vals = new double[] { _cpuWh, _gpuWh, _platWh };
                Color[] cols = new Color[] { ColCpu, ColGpu, ColPlat };
                for (int i = 0; i < 3; i++)
                {
                    float sweep = (float)(360.0 * vals[i] / total);
                    if (sweep <= 0.2f) continue;
                    using (Pen pen = new Pen(cols[i], thick))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawArc(pen, cx - rad, cy - rad, rad * 2, rad * 2, start + 1.2f, Math.Max(0.5f, sweep - 2.4f));
                    }
                    start += sweep;
                }
            }

            using (SolidBrush b = new SolidBrush(TextMain))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                string v = total >= 1000 ? (total / 1000).ToString("0.00", CultureInfo.InvariantCulture) : total.ToString("0", CultureInfo.InvariantCulture);
                g.DrawString(v, F(18, FontStyle.Bold), b, new RectangleF(cx - S(46), cy - S(14), S(92), S(20)), sf);
                using (SolidBrush b2 = new SolidBrush(TextTer))
                    g.DrawString(total >= 1000 ? "kWh" : "Wh", F(9, FontStyle.Regular), b2, new RectangleF(cx - S(46), cy + S(6), S(92), S(14)), sf);
            }

            // 图例
            float lx = _rDonut.Left + S(140);
            float ly = _rDonut.Top + S(56);
            string[] names = new string[] { "CPU", "GPU", "平台" };
            double[] vs = new double[] { _cpuWh, _gpuWh, _platWh };
            Color[] cs = new Color[] { ColCpu, ColGpu, ColPlat };
            for (int i = 0; i < 3; i++)
            {
                float y = ly + i * S(40);
                using (SolidBrush b = new SolidBrush(cs[i]))
                using (GraphicsPath p = IconFactory.RoundRect(new RectangleF(lx, y + S(4), S(8), S(8)), S(2)))
                    g.FillPath(b, p);
                using (SolidBrush b = new SolidBrush(TextSec))
                    g.DrawString(names[i], F(10.5f, FontStyle.Regular), b, lx + S(15), y);
                string val = vs[i] >= 1000 ? (vs[i] / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " kWh"
                                            : vs[i].ToString("0", CultureInfo.InvariantCulture) + " Wh";
                using (SolidBrush b = new SolidBrush(TextMain))
                    g.DrawString(val, F(11, FontStyle.Bold), b, lx + S(15), y + S(15));
                if (total > 0.0001)
                {
                    string pct = (vs[i] / total * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
                    using (SolidBrush b = new SolidBrush(TextTer))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Far;
                        g.DrawString(pct, F(9.5f, FontStyle.Regular), b, new RectangleF(lx, y + S(15), _rDonut.Right - S(14) - lx, S(14)), sf);
                    }
                }
            }
        }

        void BuildMonths(List<HistoryBucket> all)
        {
            DateTime cur = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            _months = new List<MonthStat>();
            for (int i = 11; i >= 0; i--)
            {
                MonthStat st = new MonthStat();
                st.Month = cur.AddMonths(-i);
                _months.Add(st);
            }
            for (int i = 0; i < all.Count; i++)
            {
                HistoryBucket b = all[i];
                DateTime key = new DateTime(b.Ts.Year, b.Ts.Month, 1);
                MonthStat st = FindMonth(key);
                if (st == null) continue;
                st.Wh += b.Wh;
                if (b.MaxW > st.PeakW) st.PeakW = b.MaxW;
                st.Minutes += b.Weight;
                st.Cost += b.Wh / 1000.0 * _cfg.PricePerKwh;
            }
            for (int i = 0; i < _sessions.Count; i++)
            {
                HistorySession s = _sessions[i];
                MonthStat st = FindMonth(new DateTime(s.Boot.Year, s.Boot.Month, 1));
                if (st != null) st.Sessions++;
            }
        }

        MonthStat FindMonth(DateTime key)
        {
            for (int i = 0; i < _months.Count; i++) if (_months[i].Month == key) return _months[i];
            return null;
        }

        void DrawMonths(Graphics g)
        {
            MonthStat cur = FindMonth(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
            if (cur == null) cur = new MonthStat();

            DateTime today = DateTime.Today;
            int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
            int elapsed = today.Day;
            double projected = elapsed > 0 ? cur.Cost * daysInMonth / (double)elapsed : 0;
            double budget = _cfg.MonthlyBudgetYuan;
            double pct = budget > 0 ? cur.Cost / budget * 100.0 : 0;

            string v1 = cur.Kwh.ToString("0.00", CultureInfo.InvariantCulture) + " kWh";
            string s1 = "开机 " + cur.Sessions + " 次 · 运行 " + FormatMinutes(cur.Minutes);
            string v2 = "¥" + cur.Cost.ToString("0.00", CultureInfo.InvariantCulture);
            string s2 = "¥" + _cfg.PricePerKwh.ToString("0.00", CultureInfo.InvariantCulture) + " / kWh";
            string v3 = budget > 0 ? pct.ToString("0", CultureInfo.InvariantCulture) + " %" : "—";
            string s3 = budget > 0
                ? "¥" + cur.Cost.ToString("0.00", CultureInfo.InvariantCulture) + " / ¥" + budget.ToString("0.00", CultureInfo.InvariantCulture)
                : "在 settings.ini 设置预算";
            string v4 = "¥" + projected.ToString("0.00", CultureInfo.InvariantCulture);
            string s4 = "按已过 " + elapsed + "/" + daysInMonth + " 天外推";
            string v5 = cur.PeakW.ToString("0.0", CultureInfo.InvariantCulture) + " W";
            string s5 = cur.Kwh > 0 ? "本月峰值" : "本月暂无数据";

            Color budgetColor = budget <= 0 ? Accent
                              : pct >= 100 ? Color.FromArgb(255, 255, 96, 92)
                              : pct >= 80 ? Color.FromArgb(255, 255, 178, 46)
                              : Color.FromArgb(255, 80, 205, 140);

            Card(g, _rCard[0], "本月用电", v1, s1, Accent);
            Card(g, _rCard[1], "本月电费", v2, s2, Color.FromArgb(255, 255, 128, 100));
            CardBar(g, _rCard[2], "预算进度", v3, s3, budgetColor,
                budget > 0 ? Math.Min(1.0, cur.Cost / budget) : 0);
            Card(g, _rCard[3], "预计月末电费", v4, s4, AccentSoft);
            Card(g, _rCard[4], "本月峰值", v5, s5, Color.FromArgb(255, 96, 200, 240));

            DrawMonthTable(g);
        }

        string FormatMinutes(double minutes)
        {
            if (minutes < 1) return "0 分";
            if (minutes < 60) return ((int)Math.Round(minutes)) + " 分";
            int h = (int)(minutes / 60);
            int m = (int)Math.Round(minutes - h * 60);
            return h + " 小时 " + m + " 分";
        }

        // 带进度条的卡片
        void CardBar(Graphics g, RectangleF r, string label, string value, string sub, Color accent, double frac)
        {
            Card(g, r, label, value, sub, accent);
            float x = r.Left + S(14);
            float w = r.Width - S(28);
            RectangleF track = new RectangleF(x, r.Bottom - S(8), w, S(3));
            using (GraphicsPath p = IconFactory.RoundRect(track, S(1.5f)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                g.FillPath(b, p);
            frac = Math.Max(0, Math.Min(1, frac));
            if (frac > 0.005)
            {
                RectangleF fill = new RectangleF(track.X, track.Y, Math.Max(S(3), (float)(track.Width * frac)), track.Height);
                using (GraphicsPath p = IconFactory.RoundRect(fill, S(1.5f)))
                using (SolidBrush b = new SolidBrush(accent))
                    g.FillPath(b, p);
            }
        }

        void DrawMonthTable(Graphics g)
        {
            RectangleF r = _rMonths;
            Panel(g, r, "月度汇总", "最近 12 个月");

            float top = r.Top + S(38);
            float left = r.Left + S(14);
            float right = r.Right - S(14);
            string[] head = new string[] { "月份", "用电", "电费", "平均", "峰值", "运行", "开机", "" };
            float[] colW = new float[] { S(96), S(104), S(90), S(86), S(86), S(104), S(64), S(180) };
            float x = left;
            using (SolidBrush b = new SolidBrush(TextTer))
            {
                for (int i = 0; i < head.Length; i++)
                {
                    using (StringFormat sf = new StringFormat())
                    {
                        if (i >= 1 && i <= 6) sf.Alignment = StringAlignment.Far;
                        g.DrawString(head[i], F(10, FontStyle.Regular), b, new RectangleF(x, top, colW[i], S(16)), sf);
                    }
                    x += colW[i];
                }
            }
            using (Pen pen = new Pen(Color.FromArgb(16, 255, 255, 255), 1f))
                g.DrawLine(pen, left, top + S(20), right, top + S(20));

            float rowH = S(28);
            float listTop = top + S(26);
            int visible = (int)((r.Bottom - S(14) - listTop) / rowH);
            if (visible < 1) visible = 1;
            if (_monthScroll > _months.Count - visible) _monthScroll = Math.Max(0, _months.Count - visible);
            if (_monthScroll < 0) _monthScroll = 0;

            double maxKwh = 0.001;
            for (int i = 0; i < _months.Count; i++) if (_months[i].Kwh > maxKwh) maxKwh = _months[i].Kwh;

            DateTime curMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            for (int i = 0; i < visible; i++)
            {
                int idx = _monthScroll + i;
                if (idx >= _months.Count) break;
                MonthStat m = _months[idx];
                float y = listTop + i * rowH;
                bool isCur = m.Month == curMonth;
                if (isCur)
                {
                    using (GraphicsPath p = IconFactory.RoundRect(new RectangleF(left - S(6), y, right - left + S(12), rowH - S(2)), S(6)))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(24, Accent)))
                        g.FillPath(b, p);
                }
                else if (i % 2 == 1)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(8, 255, 255, 255)))
                        g.FillRectangle(b, left - S(6), y, right - left + S(12), rowH - S(2));
                }

                string[] cells = new string[] {
                    m.Month.ToString("yyyy-MM"),
                    m.Kwh.ToString("0.00", CultureInfo.InvariantCulture) + " kWh",
                    "¥" + m.Cost.ToString("0.00", CultureInfo.InvariantCulture),
                    m.AvgW.ToString("0.0", CultureInfo.InvariantCulture) + " W",
                    m.PeakW.ToString("0.0", CultureInfo.InvariantCulture) + " W",
                    FormatMinutes(m.Minutes),
                    m.Sessions.ToString(CultureInfo.InvariantCulture)
                };
                float cx = left;
                for (int c = 0; c < cells.Length; c++)
                {
                    using (SolidBrush b = new SolidBrush(c == 0 ? (isCur ? Color.FromArgb(255, 180, 210, 255) : TextMain) : TextSec))
                    using (StringFormat sf = new StringFormat())
                    {
                        if (c >= 1) sf.Alignment = StringAlignment.Far;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(cells[c], F(10.5f, FontStyle.Regular), b, new RectangleF(cx, y, colW[c], rowH), sf);
                    }
                    cx += colW[c];
                }

                // 迷你条
                float barX = cx + S(2);
                float barW = Math.Min(colW[7] - S(10), right - barX);
                if (barW > S(20) && m.Kwh > 0)
                {
                    RectangleF track = new RectangleF(barX, y + rowH / 2 - S(3), barW, S(6));
                    using (GraphicsPath p = IconFactory.RoundRect(track, S(3)))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 255, 255, 255)))
                        g.FillPath(b, p);
                    float frac = (float)(m.Kwh / maxKwh);
                    RectangleF fill = new RectangleF(track.X, track.Y, Math.Max(S(4), (float)(track.Width * frac)), track.Height);
                    using (GraphicsPath p = IconFactory.RoundRect(fill, S(3)))
                    using (LinearGradientBrush b = new LinearGradientBrush(fill,
                        Color.FromArgb(230, 120, 186, 255), Color.FromArgb(160, 150, 130, 255), 0f))
                        g.FillPath(b, p);
                }
            }

            if (_months.Count > visible)
            {
                using (SolidBrush b = new SolidBrush(TextTer))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Far;
                    g.DrawString("滚轮翻页 " + (_monthScroll + 1) + "–" + Math.Min(_months.Count, _monthScroll + visible) + " / " + _months.Count,
                        F(9.5f, FontStyle.Regular), b, new RectangleF(r.Left, r.Bottom - S(24), r.Width - S(14), S(16)), sf);
                }
            }
        }

        void DrawSessions(Graphics g)
        {
            RectangleF r = _rTable;
            Panel(g, r, "开机记录", "共 " + _sessions.Count + " 次");

            float top = r.Top + S(38);
            float left = r.Left + S(14);
            float right = r.Right - S(14);
            string[] head = new string[] { "开机时间", "时长", "耗电", "平均", "峰值", "CPU", "GPU" };
            float[] colW = new float[] { S(150), S(90), S(96), S(80), S(80), S(86), S(86) };
            float x = left;
            using (SolidBrush b = new SolidBrush(TextTer))
            {
                for (int i = 0; i < head.Length; i++)
                {
                    using (StringFormat sf = new StringFormat())
                    {
                        if (i >= 1) sf.Alignment = StringAlignment.Far;
                        g.DrawString(head[i], F(10, FontStyle.Regular), b,
                            new RectangleF(x, top, colW[i], S(16)), sf);
                    }
                    x += colW[i];
                }
            }
            using (Pen pen = new Pen(Color.FromArgb(16, 255, 255, 255), 1f))
                g.DrawLine(pen, left, top + S(20), right, top + S(20));

            float rowH = S(30);
            float listTop = top + S(26);
            int visible = (int)((r.Bottom - S(16) - listTop) / rowH);
            if (visible < 1) visible = 1;
            if (_scroll > _sessions.Count - visible) _scroll = Math.Max(0, _sessions.Count - visible);
            if (_scroll < 0) _scroll = 0;

            for (int i = 0; i < visible; i++)
            {
                int idx = _scroll + i;
                if (idx >= _sessions.Count) break;
                HistorySession s = _sessions[idx];
                float y = listTop + i * rowH;
                bool cur = s.Id == (_store.Current != null ? _store.Current.Id : "");
                if (cur)
                {
                    using (GraphicsPath p = IconFactory.RoundRect(new RectangleF(left - S(6), y, right - left + S(12), rowH - S(2)), S(6)))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(24, Accent)))
                        g.FillPath(b, p);
                }
                else if (i % 2 == 1)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(8, 255, 255, 255)))
                        g.FillRectangle(b, left - S(6), y, right - left + S(12), rowH - S(2));
                }

                string d1 = s.Boot.ToString("MM-dd HH:mm");
                TimeSpan up = s.Uptime;
                string d2 = up.TotalHours >= 1 ? ((int)up.TotalHours) + "h " + up.Minutes + "m" : up.Minutes + "m";
                if (cur) d2 = "进行中";
                string d3 = (s.Wh / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " kWh";
                string d4 = s.AvgW > 0 ? s.AvgW.ToString("0.0", CultureInfo.InvariantCulture) + " W" : "—";
                string d5 = s.PeakW.ToString("0.0", CultureInfo.InvariantCulture) + " W";
                string d6 = s.CpuWh.ToString("0", CultureInfo.InvariantCulture) + " Wh";
                string d7 = s.GpuWh.ToString("0", CultureInfo.InvariantCulture) + " Wh";
                string[] cells = new string[] { d1, d2, d3, d4, d5, d6, d7 };
                float cx = left;
                for (int c = 0; c < cells.Length; c++)
                {
                    using (SolidBrush b = new SolidBrush(c == 0 ? (cur ? Color.FromArgb(255, 180, 210, 255) : TextMain) : TextSec))
                    using (StringFormat sf = new StringFormat())
                    {
                        if (c >= 1) sf.Alignment = StringAlignment.Far;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(cells[c], F(c == 0 ? 10.5f : 10, c == 0 ? FontStyle.Regular : FontStyle.Regular), b,
                            new RectangleF(cx, y, colW[c], rowH), sf);
                    }
                    cx += colW[c];
                }
            }

            if (_sessions.Count == 0)
            {
                using (SolidBrush b = new SolidBrush(TextTer))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("还没有开机记录", F(11, FontStyle.Regular), b, r, sf);
                }
            }
            else if (visible < _sessions.Count)
            {
                using (SolidBrush b = new SolidBrush(TextTer))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Far;
                    g.DrawString("滚轮翻页 " + (_scroll + 1) + "–" + Math.Min(_sessions.Count, _scroll + visible) + " / " + _sessions.Count,
                        F(9.5f, FontStyle.Regular), b, new RectangleF(r.Left, r.Bottom - S(24), r.Width - S(14), S(16)), sf);
                }
            }
        }

        void DrawFooter(Graphics g)
        {
            string info = _store.Dir;
            using (SolidBrush b = new SolidBrush(_rPath.Contains(_mouse) ? Color.FromArgb(255, 150, 190, 240) : TextTer))
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisPath;
                g.DrawString("数据目录: " + info, F(10, FontStyle.Regular), b, _rPath, sf);
            }

            Button(g, _rRefresh, "刷新");
            Button(g, _rExport, "导出 CSV");
        }

        void Button(Graphics g, RectangleF r, string text)
        {
            bool hot = r.Contains(_mouse);
            using (GraphicsPath p = IconFactory.RoundRect(r, S(8)))
            {
                using (SolidBrush b = new SolidBrush(hot ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(16, 255, 255, 255)))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(18, 255, 255, 255), 1f)) g.DrawPath(pen, p);
            }
            using (SolidBrush b = new SolidBrush(hot ? TextMain : TextSec))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, F(10.5f, FontStyle.Regular), b, r, sf);
            }
        }

        // ---------------- 交互 ----------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouse = new Point(-100, -100);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Point p = e.Location;
            if (_rClose.Contains(p)) { Hide(); return; }
            if (_rView0.Contains(p)) { _view = 0; Invalidate(); return; }
            if (_rView1.Contains(p)) { _view = 1; Invalidate(); return; }
            if (_rView2.Contains(p)) { _view = 2; Invalidate(); return; }
            for (int i = 0; i < 4; i++)
                if (_rRange[i].Contains(p)) { _range = i; _view = 0; Invalidate(); return; }
            if (_rRefresh.Contains(p)) { Invalidate(); return; }
            if (_rExport.Contains(p)) { Export(); return; }
            if (_rPath.Contains(p))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + _store.Dir + "\""); }
                catch { }
                return;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_view == 1)
            {
                _scroll -= Math.Sign(e.Delta) * 2;
                if (_scroll < 0) _scroll = 0;
                Invalidate();
            }
            else if (_view == 2)
            {
                _monthScroll -= Math.Sign(e.Delta) * 2;
                if (_monthScroll < 0) _monthScroll = 0;
                Invalidate();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Hide(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Export()
        {
            try
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Title = "导出功耗明细";
                    dlg.Filter = "CSV 文件|*.csv";
                    dlg.FileName = "PowerMonitor_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    System.IO.File.WriteAllText(dlg.FileName, _store.ExportCsv(), System.Text.Encoding.UTF8);
                    MessageBox.Show(this, "已导出:\n" + dlg.FileName, "功耗统计", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex) { Paths.Log("export: " + ex.Message); }
        }
    }
}
