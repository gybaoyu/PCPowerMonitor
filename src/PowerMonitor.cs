// PowerMonitor — 轻量托盘功耗监视器 (Windows 10/11, .NET Framework 4.x, 零外部依赖)
// 数据源: CPU = Windows 性能计数器 \Energy Meter(RAPL_Package0_PKG)\Power (mW, RAPL 硬件传感器)
//         GPU = NVML 利用率/频率/显存建模估算 (本机 4060 未暴露功率传感器)
//         整机 = CPU + GPU + 平台固定开销(可调)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PowerMonitor")]
[assembly: AssemblyDescription("轻量托盘功耗监视器：整机 / CPU / GPU 功率")]
[assembly: AssemblyProduct("PowerMonitor")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PowerMonitor
{
    internal static class Program
    {
        static Mutex _mutex;

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            _mutex = new Mutex(true, @"Local\PowerMonitor.SingleInstance", out created);
            if (!created)
            {
                try
                {
                    EventWaitHandle ev;
                    if (EventWaitHandle.TryOpenExisting("PowerMonitor.ShowPanel", out ev))
                    {
                        ev.Set();
                        ev.Dispose();
                    }
                }
                catch { }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Paths.Log("unhandled: " + e.ExceptionObject);
            };
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Paths.Log("thread: " + e.Exception.Message);
            };

            try { Application.Run(new TrayContext()); }
            catch (Exception ex) { Paths.Log("fatal: " + ex); }
        }
    }

    internal static class Paths
    {
        public static string ConfigDir
        {
            get
            {
                string d = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerMonitor");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string ConfigFile { get { return System.IO.Path.Combine(ConfigDir, "settings.ini"); } }
        public static string LogFile { get { return System.IO.Path.Combine(ConfigDir, "log.txt"); } }

        public static void Log(string msg)
        {
            try
            {
                FileInfo fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 200 * 1024) fi.Delete();
                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class Fonts
    {
        public static readonly string Ui;

        static Fonts()
        {
            Ui = "Segoe UI";
            try
            {
                using (InstalledFontCollection col = new InstalledFontCollection())
                {
                    foreach (FontFamily f in col.Families)
                    {
                        if (f.Name == "Segoe UI Variable Text") { Ui = "Segoe UI Variable Text"; break; }
                    }
                }
            }
            catch { }
        }

        public static Font Make(float px, FontStyle style)
        {
            return new Font(Ui, px, style, GraphicsUnit.Pixel);
        }
    }

    internal static class Dpi
    {
        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        public static float ForWindow(IntPtr hwnd)
        {
            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    uint d = GetDpiForWindow(hwnd);
                    if (d >= 48 && d <= 600) return d / 96f;
                }
            }
            catch { }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
            }
            catch { return 1f; }
        }
    }

    internal sealed class AppSettings
    {
        public int IntervalMs = 1000;
        public int TrayMetric;          // 0 总功耗 1 CPU 2 GPU
        public double PlatformW = 25.0; // 主板/内存/硬盘/风扇/VRM 固定开销
        public double GpuTgpW = 115.0;
        public double GpuIdleW = 8.0;
        public double PricePerKwh = 0.6;  // 电费单价（元/kWh），用于估算电费
        public double CpuMaxW = 200.0;    // CPU 功率可信上限，超过视为传感器毛刺
        public bool FirstRun;

        static int ClampInt(string s, int lo, int hi, int def)
        {
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return def;
            return Math.Max(lo, Math.Min(hi, v));
        }

        static double ClampDouble(string s, double lo, double hi, double def)
        {
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return def;
            return Math.Max(lo, Math.Min(hi, v));
        }

        public void Load()
        {
            FirstRun = !File.Exists(Paths.ConfigFile);
            try
            {
                if (!File.Exists(Paths.ConfigFile)) return;
                string[] lines = File.ReadAllLines(Paths.ConfigFile);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    if (k == "IntervalMs") IntervalMs = ClampInt(v, 250, 10000, 1000);
                    else if (k == "TrayMetric") TrayMetric = ClampInt(v, 0, 2, 0);
                    else if (k == "PlatformW") PlatformW = ClampDouble(v, 5, 150, 25);
                    else if (k == "GpuTgpW") GpuTgpW = ClampDouble(v, 20, 700, 115);
                    else if (k == "GpuIdleW") GpuIdleW = ClampDouble(v, 1, 80, 8);
                    else if (k == "PricePerKwh") PricePerKwh = ClampDouble(v, 0, 10, 0.6);
                    else if (k == "CpuMaxW") CpuMaxW = ClampDouble(v, 30, 800, 200);
                }
            }
            catch (Exception ex) { Paths.Log("settings load: " + ex.Message); }
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# PowerMonitor 配置 (修改后需重启程序)");
                sb.AppendLine("# IntervalMs  采样间隔毫秒 (250-10000)");
                sb.AppendLine("# TrayMetric   托盘显示内容 0=总功耗 1=CPU 2=GPU");
                sb.AppendLine("# PlatformW    平台固定开销: 主板+内存+硬盘+风扇+VRM");
                sb.AppendLine("# GpuTgpW, GpuIdleW  GPU 功率模型的满载/空载瓦数");
                sb.AppendLine("# PricePerKwh  电费单价（元/kWh），用于估算电费");
                sb.AppendLine("# CpuMaxW      CPU 功率可信上限（超出视为 RAPL 毛刺并丢弃）");
                sb.AppendLine("IntervalMs=" + IntervalMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("TrayMetric=" + TrayMetric.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("PlatformW=" + PlatformW.ToString("0.0", CultureInfo.InvariantCulture));
                sb.AppendLine("GpuTgpW=" + GpuTgpW.ToString("0.0", CultureInfo.InvariantCulture));
                sb.AppendLine("GpuIdleW=" + GpuIdleW.ToString("0.0", CultureInfo.InvariantCulture));
                sb.AppendLine("PricePerKwh=" + PricePerKwh.ToString("0.00", CultureInfo.InvariantCulture));
                sb.AppendLine("CpuMaxW=" + CpuMaxW.ToString("0", CultureInfo.InvariantCulture));
                File.WriteAllText(Paths.ConfigFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Paths.Log("settings save: " + ex.Message); }
        }
    }

    // ---- CPU: RAPL 通过 Windows 性能计数器读取, 无需驱动/管理员 ----
    internal sealed class RaplCpu
    {
        PerformanceCounter _pc;
        DateTime _next = DateTime.MinValue;
        public string LastError = "未初始化";

        public bool Ensure()
        {
            if (_pc != null) return true;
            if (DateTime.Now < _next) return false;
            try
            {
                PerformanceCounterCategory cat = new PerformanceCounterCategory("Energy Meter");
                string[] names = cat.GetInstanceNames();
                string pick = null;
                foreach (string n in names)
                {
                    if (n.IndexOf("pkg", StringComparison.OrdinalIgnoreCase) >= 0) { pick = n; break; }
                }
                if (pick == null) { LastError = "无 RAPL 实例"; _next = DateTime.Now.AddSeconds(15); return false; }
                _pc = new PerformanceCounter("Energy Meter", "Power", pick, true);
                _pc.NextValue();
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                if (_pc != null) { try { _pc.Dispose(); } catch { } _pc = null; }
                _next = DateTime.Now.AddSeconds(15);
                return false;
            }
        }

        public double ReadW()
        {
            if (_pc == null && !Ensure()) return -1;
            try
            {
                double mw = _pc.NextValue();
                if (mw <= 0 || mw > 800000) return 0;
                return mw / 1000.0;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { _pc.Dispose(); } catch { }
                _pc = null;
                _next = DateTime.Now.AddSeconds(10);
                return -1;
            }
        }

        public void Reset()
        {
            try { if (_pc != null) _pc.Dispose(); } catch { }
            _pc = null;
            _next = DateTime.MinValue;
        }
    }

    internal struct GpuInfo
    {
        public bool Ok;
        public double Util;
        public double MemUtil;
        public double ClockMHz;
        public double TempC;
        public double PowerW;
    }

    // ---- GPU: NVML 读取利用率/频率/温度/显存, 功率按模型估算 ----
    internal sealed class GpuReader
    {
        [StructLayout(LayoutKind.Sequential)]
        struct NvmlUtil { public uint Gpu; public uint Memory; }

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlInit_v2();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlShutdown();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr dev);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out NvmlUtil util);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint clock);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensorType, out uint temp);

        IntPtr _dev = IntPtr.Zero;
        bool _ready;
        DateTime _next = DateTime.MinValue;
        public string LastError = "未初始化";

        bool Ensure()
        {
            if (_ready) return true;
            if (DateTime.Now < _next) return false;
            try
            {
                if (nvmlInit_v2() != 0) { LastError = "nvmlInit 失败"; _next = DateTime.Now.AddSeconds(20); return false; }
                IntPtr dev;
                if (nvmlDeviceGetHandleByIndex_v2(0, out dev) != 0)
                { LastError = "无可用 GPU"; _next = DateTime.Now.AddSeconds(20); return false; }
                _dev = dev;
                _ready = true;
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _next = DateTime.Now.AddSeconds(20);
                return false;
            }
        }

        public GpuInfo Read(AppSettings cfg)
        {
            GpuInfo gi = new GpuInfo();
            if (!Ensure()) return gi;
            try
            {
                NvmlUtil u;
                if (nvmlDeviceGetUtilizationRates(_dev, out u) == 0) { gi.Util = u.Gpu; gi.MemUtil = u.Memory; }
                uint clk;
                if (nvmlDeviceGetClockInfo(_dev, 0, out clk) == 0) gi.ClockMHz = clk;
                uint t;
                if (nvmlDeviceGetTemperature(_dev, 0, out t) == 0) gi.TempC = t;

                double idle = cfg.GpuIdleW;
                double tgp = cfg.GpuTgpW;
                if (tgp <= idle) tgp = idle + 20;
                double f = Math.Min(100.0, Math.Max(0.0, gi.Util)) / 100.0;
                double est = idle + (tgp - idle) * Math.Pow(f, 0.9);
                est += (Math.Min(100.0, Math.Max(0.0, gi.MemUtil)) / 100.0) * (tgp * 0.04);
                if (gi.ClockMHz > 0 && gi.ClockMHz < 500) est = Math.Min(est, idle * 1.2);
                gi.PowerW = Math.Max(1.0, est);
                gi.Ok = true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _ready = false;
                _next = DateTime.Now.AddSeconds(20);
            }
            return gi;
        }

        public void Reset()
        {
            try { if (_ready) nvmlShutdown(); } catch { }
            _ready = false;
            _dev = IntPtr.Zero;
            _next = DateTime.MinValue;
        }
    }

    internal sealed class Reading
    {
        public DateTime Time;
        public bool CpuOk;
        public bool GpuOk;
        public double CpuW;
        public double GpuW;
        public double TotalW;
        public double CpuSm;
        public double GpuSm;
        public double TotalSm;
        public double PlatformW;
        public double GpuUtil;
        public double GpuTemp;
    }

    internal sealed class Sampler
    {
        readonly AppSettings _cfg;
        readonly RaplCpu _cpu = new RaplCpu();
        readonly GpuReader _gpu = new GpuReader();
        volatile bool _stop;
        Thread _thread;

        double _cpuSm = -1;
        double _gpuSm = -1;
        double _cpuPrev, _gpuPrev;

        public event Action<Reading> Update;

        public Sampler(AppSettings cfg) { _cfg = cfg; }

        public void Start()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.BelowNormal;
            _thread.Name = "PowerSampler";
            _thread.Start();
        }

        public void Stop() { _stop = true; }

        // 丢弃明显不可能的读数（RAPL 计数器偶发毛刺 / 模型异常），避免污染峰值与累计电量
        static double Sanitize(double v, double prev, double cap)
        {
            if (v < 0) return -1;
            if (v > cap) return prev > 0 ? prev : -1;
            if (prev > 3 && v > prev * 2.5 + 45) return prev;
            return v;
        }

        public void ResetSensors()
        {
            _cpu.Reset();
            _gpu.Reset();
            _cpuSm = -1;
            _gpuSm = -1;
        }

        void Loop()
        {
            while (!_stop)
            {
                int interval = _cfg.IntervalMs;
                DateTime t0 = DateTime.Now;
                Reading r = new Reading();
                r.Time = t0;

                double cpu = _cpu.ReadW();
                cpu = Sanitize(cpu, _cpuPrev, _cfg.CpuMaxW);
                if (cpu > 0) _cpuPrev = cpu;
                r.CpuOk = cpu >= 0;
                r.CpuW = r.CpuOk ? cpu : 0;

                GpuInfo gi = _gpu.Read(_cfg);
                r.GpuOk = gi.Ok;
                r.GpuW = gi.Ok ? gi.PowerW : 0;
                r.GpuUtil = gi.Util;
                r.GpuTemp = gi.TempC;
                if (r.GpuOk) r.GpuW = Sanitize(r.GpuW, _gpuPrev, _cfg.GpuTgpW * 1.25);
                if (r.GpuW > 0) _gpuPrev = r.GpuW;

                double a = 0.35;
                _cpuSm = _cpuSm < 0 ? r.CpuW : _cpuSm + (r.CpuW - _cpuSm) * a;
                _gpuSm = _gpuSm < 0 ? r.GpuW : _gpuSm + (r.GpuW - _gpuSm) * a;

                r.CpuSm = r.CpuOk ? _cpuSm : -1;
                r.GpuSm = r.GpuOk ? _gpuSm : -1;
                r.PlatformW = _cfg.PlatformW;
                r.TotalW = r.CpuW + r.GpuW + r.PlatformW;
                r.TotalSm = (r.CpuOk ? _cpuSm : 0) + (r.GpuOk ? _gpuSm : 0) + r.PlatformW;

                Action<Reading> h = Update;
                if (h != null) { try { h(r); } catch { } }

                int elapsed = (int)(DateTime.Now - t0).TotalMilliseconds;
                int wait = Math.Max(60, interval - elapsed);
                for (int slept = 0; slept < wait && !_stop; slept += 50)
                    Thread.Sleep(Math.Min(50, wait - slept));
            }
        }
    }

    internal static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "PowerMonitor";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(Name);
                    return v != null && v.ToString().IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\" --tray", RegistryValueKind.String);
                    else k.DeleteValue(Name, false);
                }
            }
            catch (Exception ex) { Paths.Log("startup: " + ex.Message); }
        }
    }

    internal static class TrayPromote
    {
        // Win11 新托盘图标默认收进溢出区, 把 IsPromoted 置 1 使其常驻任务栏
        public static bool Apply(string exePath)
        {
            try
            {
                string target = System.IO.Path.GetFullPath(exePath);
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) return false;
                    string[] subs = root.GetSubKeyNames();
                    foreach (string name in subs)
                    {
                        using (RegistryKey sk = root.OpenSubKey(name, true))
                        {
                            if (sk == null) continue;
                            object p = sk.GetValue("ExecutablePath");
                            if (p == null) continue;
                            string full;
                            try { full = System.IO.Path.GetFullPath(p.ToString()); }
                            catch { continue; }
                            if (!string.Equals(full, target, StringComparison.OrdinalIgnoreCase)) continue;
                            sk.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex) { Paths.Log("promote: " + ex.Message); }
            return false;
        }
    }

    internal static class IconFactory
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool DestroyIcon(IntPtr handle);

        // 缓存 "尺寸+文本 -> 字号"，避免每秒重复做拟合测量
        static readonly Dictionary<string, float> FitCache = new Dictionary<string, float>();

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Icon Create(string text, Color accent, int size)
        {
            if (size < 8) size = 16;
            int big = size * 4;
            using (Bitmap hi = new Bitmap(big, big, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(hi))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    using (GraphicsPath p = RoundRect(new RectangleF(1f, 1f, big - 2f, big - 2f), big * 0.27f))
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 25, 28, 34))) g.FillPath(b, p);
                        using (Pen pen = new Pen(Color.FromArgb(80, accent), Math.Max(1f, big * 0.02f))) g.DrawPath(pen, p);
                    }

                    float maxW = big * 0.86f;
                    float maxH = big * 0.68f;
                    string cacheKey = big.ToString() + "|" + text;
                    float chosen;
                    if (!FitCache.TryGetValue(cacheKey, out chosen))
                    {
                        chosen = big * 0.45f;
                        for (float cand = big * 1.05f; cand >= 5f; cand -= big * 0.02f)
                        {
                            Font trial = Fonts.Make(cand, FontStyle.Bold);
                            SizeF m = g.MeasureString(text, trial, new PointF(0, 0), StringFormat.GenericTypographic);
                            trial.Dispose();
                            if (m.Width <= maxW && m.Height <= maxH * 1.3f) { chosen = cand; break; }
                        }
                        FitCache[cacheKey] = chosen;
                    }
                    Font fit = Fonts.Make(chosen, FontStyle.Bold);

                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        sf.FormatFlags = StringFormatFlags.NoWrap;
                        using (SolidBrush b = new SolidBrush(accent))
                            g.DrawString(text, fit, b, new RectangleF(0, 0, big, big), sf);
                    }
                    fit.Dispose();
                }

                using (Bitmap small = new Bitmap(size, size, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g2 = Graphics.FromImage(small))
                    {
                        g2.CompositingMode = CompositingMode.SourceCopy;
                        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g2.DrawImage(hi, new Rectangle(0, 0, size, size), 0, 0, big, big, GraphicsUnit.Pixel);
                    }
                    IntPtr h = small.GetHicon();
                    using (Icon tmp = Icon.FromHandle(h))
                    {
                        Icon result = (Icon)tmp.Clone();
                        DestroyIcon(h);
                        return result;
                    }
                }
            }
        }
    }

    // ---- 悬浮卡片: UpdateLayeredWindow 逐像素 alpha, 圆角/阴影干净 ----
    internal sealed class CardForm : Form
    {
        const int ShadowPad = 22;
        const int CardW = 318;
        const int CardH = 180;

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX; public int CY; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight;
            public short biPlanes; public short biBitCount; public int biCompression;
            public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
            public int biClrUsed; public int biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);

        Reading _r;
        List<double> _hist = new List<double>();
        double _wh;
        public HistoryStore History;
        AppSettings _cfg = new AppSettings();
        float _scale = 1f;
        DateTime _suppressHide = DateTime.MinValue;

        public CardForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "PowerMonitor";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        float ScreenScale()
        {
            return Dpi.ForWindow(IsHandleCreated ? Handle : IntPtr.Zero);
        }

        void PlaceAtTray()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int sp = (int)Math.Round(ShadowPad * _scale);
            int w = (int)Math.Round(CardW * _scale) + sp * 2;
            int h = (int)Math.Round(CardH * _scale) + sp * 2;
            int x = wa.Right - w - (int)Math.Round(6 * _scale);
            int y = wa.Bottom - h - (int)Math.Round(6 * _scale);
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            Location = new Point(x, y);
        }

        public void ShowPanel(Reading r, List<double> hist, double wh, AppSettings cfg)
        {
            _r = r; _hist = hist; _wh = wh; _cfg = cfg;
            _scale = ScreenScale();
            _suppressHide = DateTime.Now.AddMilliseconds(500);
            PlaceAtTray();
            if (!Visible)
            {
                IntPtr force = Handle; // 先建句柄
                Show();
                TopMost = true;
                Render();
                Activate();
            }
            else
            {
                Render();
                BringToFront();
            }
        }

        public void UpdateData(Reading r, List<double> hist, double wh, AppSettings cfg)
        {
            _r = r; _hist = hist; _wh = wh; _cfg = cfg;
            Render();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (DateTime.Now < _suppressHide) return;
            Hide();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Hide();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Hide(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Render()
        {
            using (Bitmap bmp = BuildBitmap())
                Push(bmp);
        }

        // 供调试: 直接导出渲染结果 PNG (与屏幕所见一致)
        public void SaveSnapshot(string path, Reading r, List<double> hist, double wh, AppSettings cfg)
        {
            _r = r; _hist = hist; _wh = wh; _cfg = cfg;
            _scale = ScreenScale();
            using (Bitmap bmp = BuildBitmap())
                bmp.Save(path, ImageFormat.Png);
        }

        public void Reposition()
        {
            if (!Visible) return;
            PlaceAtTray();
            Render();
        }

        Bitmap BuildBitmap()
        {
            float S = _scale;
            int cw = (int)Math.Round(CardW * S);
            int ch = (int)Math.Round(CardH * S);
            int sp = (int)Math.Round(ShadowPad * S);
            int bw = cw + sp * 2;
            int bh = ch + sp * 2;

            Bitmap bmp = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                float rad = 16f * S;

                for (int i = 7; i >= 0; i--)
                {
                    float grow = i * 1.8f * S;
                    using (GraphicsPath pp = IconFactory.RoundRect(
                        new RectangleF(sp - grow, sp + (2.5f * S) - grow * 0.8f, cw + grow * 2, ch + grow * 2), rad + grow))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(7, 0, 0, 0)))
                        g.FillPath(sb, pp);
                }

                RectangleF card = new RectangleF(sp, sp, cw, ch);
                using (GraphicsPath p = IconFactory.RoundRect(card, rad))
                {
                    using (LinearGradientBrush bg = new LinearGradientBrush(
                        new RectangleF(sp, sp, cw, ch), Color.FromArgb(255, 35, 39, 47), Color.FromArgb(255, 17, 19, 23), 90f))
                        g.FillPath(bg, p);
                    using (Pen border = new Pen(Color.FromArgb(22, 255, 255, 255), 1f))
                        g.DrawPath(border, p);
                }

                DrawContent(g, sp, cw, ch, S);
            }
            return bmp;
        }

        Color LevelColor(double w, double green, double amber)
        {
            if (w < green) return Color.FromArgb(255, 60, 205, 130);
            if (w < amber) return Color.FromArgb(255, 255, 178, 46);
            return Color.FromArgb(255, 255, 96, 92);
        }

        static string W1(double v)
        {
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        void DrawContent(Graphics g, int sp, int cw, int ch, float S)
        {
            float x0 = sp + 20f * S;
            float right = sp + cw - 20f * S;

            double total = _r == null ? 0 : _r.TotalSm;
            double cpu = _r == null ? 0 : _r.CpuSm;
            double gpu = _r == null ? 0 : _r.GpuSm;

            Color secondary = Color.FromArgb(255, 139, 149, 166);
            Color tertiary = Color.FromArgb(255, 104, 114, 130);
            Color primary = Color.FromArgb(255, 242, 245, 250);

            // 标题（含等级小圆点）
            Color lvl = LevelColor(total, 90, 190);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(220, lvl)))
                g.FillEllipse(b, x0, sp + 21f * S, 5.5f * S, 5.5f * S);
            using (Font f = Fonts.Make(12.5f * S, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(secondary))
                g.DrawString("整机功耗", f, b, x0 + 12f * S, sp + 15f * S);

            // 累计电量
            string wh = _wh < 1000
                ? _wh.ToString("0.0", CultureInfo.InvariantCulture) + " Wh"
                : (_wh / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " kWh";
            using (Font f = Fonts.Make(11f * S, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(tertiary))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                g.DrawString(wh, f, b, new RectangleF(right - 150f * S, sp + 16f * S, 150f * S, 16f * S), sf);
            }

            // 大数字 + 单位
            string num = _r == null ? "--" : W1(total);
            using (Font fBig = Fonts.Make(42f * S, FontStyle.Bold))
            using (Font fUnit = Fonts.Make(13f * S, FontStyle.Regular))
            using (SolidBrush bNum = new SolidBrush(primary))
            using (SolidBrush bUnit = new SolidBrush(secondary))
            using (StringFormat sfTypo = StringFormat.GenericTypographic)
            {
                float numX = x0 - 2f * S;
                float numY = sp + 26f * S;
                SizeF sz = g.MeasureString(num, fBig, new PointF(0f, 0f), sfTypo);
                g.DrawString(num, fBig, bNum, new PointF(numX, numY), sfTypo);
                g.DrawString("W", fUnit, bUnit, new PointF(numX + sz.Width + 3f * S, numY + 21f * S), sfTypo);
            }

            // 折线图
            RectangleF spark = new RectangleF(x0 + 158f * S, sp + 28f * S, right - (x0 + 158f * S), 48f * S);
            DrawSpark(g, spark, S);

            // 两个芯片卡
            float chipY = sp + 92f * S;
            float chipW = (right - x0 - 8f * S) / 2f;
            float chipH = 44f * S;

            string cpuText = (_r == null || !_r.CpuOk) ? "--" : W1(cpu);
            string gpuText = (_r == null || !_r.GpuOk) ? "--" : W1(gpu);

            DrawChip(g, new RectangleF(x0, chipY, chipW, chipH), "CPU", cpuText,
                Color.FromArgb(255, 78, 158, 255), cpu / 100.0, S);
            DrawChip(g, new RectangleF(x0 + chipW + 8f * S, chipY, chipW, chipH), "GPU", gpuText,
                Color.FromArgb(255, 158, 133, 255), gpu / Math.Max(20.0, _cfg.GpuTgpW), S);

            // 底部说明
            using (Font f = Fonts.Make(10.5f * S, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(tertiary))
            {
                string left = "平台 " + _cfg.PlatformW.ToString("0", CultureInfo.InvariantCulture) + " W · ";
                left += (_r != null && _r.GpuOk) ? "GPU 建模估算" : "GPU 未就绪";
                g.DrawString(left, f, b, x0, sp + ch - 30f * S);

                string rightTxt = History != null
                    ? "今日 " + (History.TodayWh() / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " kWh"
                    : (_cfg.IntervalMs / 1000) + " 秒刷新";
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Far;
                    g.DrawString(rightTxt, f, b, new RectangleF(right - 120f * S, sp + ch - 30f * S, 120f * S, 14f * S), sf);
                }
            }
        }

        void DrawChip(Graphics g, RectangleF r, string label, string value, Color accent, double frac, float S)
        {
            using (GraphicsPath p = IconFactory.RoundRect(r, 11f * S))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 30, 34, 42))) g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(16, 255, 255, 255), 1f)) g.DrawPath(pen, p);
            }

            using (Font fl = Fonts.Make(11f * S, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 128, 138, 154)))
                g.DrawString(label, fl, b, r.X + 12f * S, r.Y + 6f * S);

            using (Font fv = Fonts.Make(14f * S, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 238, 242, 248)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                g.DrawString(value, fv, b, new RectangleF(r.X + 12f * S, r.Y + 5f * S, r.Width - 24f * S, 20f * S), sf);
            }

            float trackH = 3f * S;
            RectangleF track = new RectangleF(r.X + 12f * S, r.Bottom - 10f * S, r.Width - 24f * S, trackH);
            using (GraphicsPath p = IconFactory.RoundRect(track, trackH / 2f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                g.FillPath(b, p);

            frac = Math.Max(0, Math.Min(1, frac));
            if (frac > 0.01)
            {
                RectangleF fill = new RectangleF(track.X, track.Y, Math.Max(trackH, track.Width * (float)frac), trackH);
                using (GraphicsPath p = IconFactory.RoundRect(fill, trackH / 2f))
                using (SolidBrush b = new SolidBrush(accent))
                    g.FillPath(b, p);
            }
        }

        void DrawSpark(Graphics g, RectangleF r, float S)
        {
            if (_hist == null || _hist.Count < 2 || r.Width < 30f) return;

            int n = _hist.Count;
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < n; i++) { double v = _hist[i]; if (v < min) min = v; if (v > max) max = v; }
            double span = max - min;
            if (span < 6.0) span = 6.0;
            min -= span * 0.3;
            max += span * 0.2;
            span = max - min;
            if (span <= 0) return;

            PointF[] pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                float x = r.X + r.Width * i / (float)(n - 1);
                float y = (float)(r.Bottom - (_hist[i] - min) / span * r.Height);
                if (y < r.Top + 1f) y = r.Top + 1f;
                if (y > r.Bottom - 1f) y = r.Bottom - 1f;
                pts[i] = new PointF(x, y);
            }

            using (GraphicsPath line = new GraphicsPath())
            using (GraphicsPath area = new GraphicsPath())
            {
                line.AddCurve(pts, 0.35f);
                area.AddLine(pts[0].X, r.Bottom + 2f, pts[0].X, pts[0].Y);
                area.AddCurve(pts, 0.35f);
                area.AddLine(pts[n - 1].X, r.Bottom + 2f, pts[0].X, r.Bottom + 2f);
                area.CloseFigure();

                using (LinearGradientBrush fb = new LinearGradientBrush(r,
                    Color.FromArgb(64, 96, 176, 255), Color.FromArgb(0, 96, 176, 255), 90f))
                    g.FillPath(fb, area);

                using (LinearGradientBrush lb = new LinearGradientBrush(r,
                    Color.FromArgb(255, 110, 186, 255), Color.FromArgb(255, 172, 140, 255), 15f))
                using (Pen pen = new Pen(lb, Math.Max(1.2f, 1.6f * S)))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, line);
                }
            }
        }

        void Push(Bitmap bmp)
        {
            if (!IsHandleCreated) return;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hDib = IntPtr.Zero;
            IntPtr oldBmp = IntPtr.Zero;
            try
            {
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = bmp.Width;
                bmi.bmiHeader.biHeight = -bmp.Height; // 自顶向下
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;
                bmi.bmiHeader.biSizeImage = bmp.Width * bmp.Height * 4;

                IntPtr bits;
                hDib = CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hDib == IntPtr.Zero) return;

                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int rowBytes = bmp.Width * 4;
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        CopyMemory(new IntPtr(bits.ToInt64() + (long)y * rowBytes),
                                   new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride),
                                   (UIntPtr)(uint)rowBytes);
                    }
                }
                finally { bmp.UnlockBits(bd); }

                oldBmp = SelectObject(memDc, hDib);

                SIZE size = new SIZE();
                size.CX = bmp.Width;
                size.CY = bmp.Height;
                POINT src = new POINT();
                POINT dst = new POINT();
                dst.X = Left;
                dst.Y = Top;
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1; // AC_SRC_ALPHA
                if (!UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2))
                    Paths.Log("ULW failed: " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Paths.Log("push: " + ex.Message); }
            finally
            {
                if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                if (hDib != IntPtr.Zero) DeleteObject(hDib);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        readonly AppSettings _cfg = new AppSettings();
        readonly Sampler _sampler;
        readonly NotifyIcon _ni = new NotifyIcon();
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        readonly CardForm _card = new CardForm();
        readonly List<double> _hist = new List<double>();
        readonly List<ToolStripMenuItem> _metricItems = new List<ToolStripMenuItem>();
        readonly List<ToolStripMenuItem> _intervalItems = new List<ToolStripMenuItem>();
        readonly int[] _intervals = new int[] { 500, 1000, 2000, 5000 };

        ToolStripMenuItem _miStartup;
        Icon _icon;
        string _iconText = "";
        Color _iconColor = Color.Empty;
        Reading _last;
        DateTime _lastTime = DateTime.MinValue;
        double _wh;
        bool _exiting;
        HistoryStore _history;
        StatsForm _stats;
        string _statsSnapPath;
        System.Windows.Forms.Timer _autoTimer;
        EventWaitHandle _showEvent;
        Thread _signalThread;
        System.Windows.Forms.Timer _promoteTimer;
        System.Windows.Forms.Timer _trimTimer;
        System.Windows.Forms.Timer _snapTimer;
        string _snapPath;
        int _promoteTries;
        int _dbg;

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        static int IconSize()
        {
            int px = SystemInformation.SmallIconSize.Width;
            if (px < 12) px = 16;
            return px;
        }

        static void TrimMemory()
        {
            try { SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1)); }
            catch { }
        }

        public TrayContext()
        {
            _cfg.Load();
            _cfg.Save();   // 补全新增配置项，保证 ini 始终包含全部键
            if (_cfg.FirstRun)
            {
                // 需求: 默认开机自启
                Startup.Set(true);
                _cfg.Save();
            }
            _history = new HistoryStore();
            _history.Load();
            _sampler = new Sampler(_cfg);
            _sampler.Update += OnSample;

            IntPtr force = _card.Handle; // 提前建句柄, 供后台线程 BeginInvoke
            _card.History = _history;

            BuildMenu();
            _ni.ContextMenuStrip = _menu;
            _ni.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && e.Clicks == 1) ShowPanel();
            };
            _ni.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowStats();
            };
            _ni.Visible = true;
            RefreshTray(null);
            _ni.Text = "功耗监控 · 启动中…";

            _sampler.Start();

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "PowerMonitor.ShowPanel");
            _signalThread = new Thread(SignalLoop);
            _signalThread.IsBackground = true;
            _signalThread.Name = "ShowSignal";
            _signalThread.Start();

            _promoteTimer = new System.Windows.Forms.Timer();
            _promoteTimer.Interval = 3000;
            _promoteTimer.Tick += delegate
            {
                _promoteTries++;
                if (TrayPromote.Apply(Application.ExecutablePath) || _promoteTries >= 3) _promoteTimer.Stop();
            };
            _promoteTimer.Start();

            _trimTimer = new System.Windows.Forms.Timer();
            _trimTimer.Interval = 90000;
            _trimTimer.Tick += delegate { TrimMemory(); };
            _trimTimer.Start();
            System.Threading.Timer warm = new System.Threading.Timer(delegate { TrimMemory(); }, null, 6000, Timeout.Infinite);
            GC.KeepAlive(warm);

            try
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate
                {
                    try
                    {
                        if (_card.IsHandleCreated)
                            _card.BeginInvoke((MethodInvoker)delegate { _card.Reposition(); });
                    }
                    catch { }
                };
            }
            catch { }

            string[] argv = Environment.GetCommandLineArgs();
            bool autoStats = false;
            for (int i = 0; i < argv.Length - 1; i++)
            {
                if (argv[i] == "--snapshot") _snapPath = argv[i + 1];
                if (argv[i] == "--snapshot-stats") _statsSnapPath = argv[i + 1];
            }
            for (int i = 0; i < argv.Length; i++) if (argv[i] == "--stats") autoStats = true;
            if (autoStats)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 700;
                t.Tick += delegate { t.Stop(); ShowStats(); };
                t.Start();
                GC.KeepAlive(t);
                _autoTimer = t;
            }

            if (!string.IsNullOrEmpty(_snapPath) || !string.IsNullOrEmpty(_statsSnapPath))
            {
                _snapTimer = new System.Windows.Forms.Timer();
                _snapTimer.Interval = 2500;
                _snapTimer.Tick += delegate
                {
                    _snapTimer.Stop();
                    try
                    {
                        if (!string.IsNullOrEmpty(_snapPath))
                        {
                            _card.SaveSnapshot(_snapPath, _last, _hist, _wh, _cfg);
                            SaveIconSnapshot(_snapPath);
                        }
                        if (!string.IsNullOrEmpty(_statsSnapPath))
                        {
                            using (StatsForm f = new StatsForm(_history, _cfg))
                            {
                                f.SaveSnapshot(_statsSnapPath);
                                f.DebugHover((int)(f.ClientSize.Width * 0.63), (int)(f.ClientSize.Height * 0.42));
                                string dir2 = System.IO.Path.GetDirectoryName(_statsSnapPath);
                                string stem = System.IO.Path.GetFileNameWithoutExtension(_statsSnapPath);
                                f.SaveSnapshot(System.IO.Path.Combine(dir2, stem + "-hover.png"));
                                f.View = 1;
                                f.SaveSnapshot(System.IO.Path.Combine(dir2, stem + "-sessions.png"));
                            }
                        }
                    }
                    catch (Exception ex) { Paths.Log("snapshot: " + ex.Message); }
                    ExitApp();
                };
                _snapTimer.Start();
            }
        }

        void SignalLoop()
        {
            while (!_exiting)
            {
                try
                {
                    if (_showEvent.WaitOne(500))
                    {
                        if (_card.IsHandleCreated)
                            _card.BeginInvoke((MethodInvoker)delegate { ShowPanel(); });
                    }
                }
                catch { return; }
            }
        }

        void BuildMenu()
        {
            _menu.ShowImageMargin = false;
            _menu.Font = new Font("Microsoft YaHei UI", 9f);

            ToolStripMenuItem open = new ToolStripMenuItem("打开面板");
            open.Font = new Font(_menu.Font, FontStyle.Bold);
            open.Click += delegate { ShowPanel(); };

            ToolStripMenuItem stats = new ToolStripMenuItem("功耗统计…");
            stats.Click += delegate { ShowStats(); };

            ToolStripMenuItem display = new ToolStripMenuItem("托盘显示");
            string[] names = new string[] { "总功耗", "CPU 功耗", "GPU 功耗" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                ToolStripMenuItem mi = new ToolStripMenuItem(names[i]);
                mi.Click += delegate { SetMetric(idx); };
                display.DropDownItems.Add(mi);
                _metricItems.Add(mi);
            }

            ToolStripMenuItem interval = new ToolStripMenuItem("采样间隔");
            for (int i = 0; i < _intervals.Length; i++)
            {
                int value = _intervals[i];
                ToolStripMenuItem mi = new ToolStripMenuItem(value >= 1000 ? (value / 1000) + " 秒" : value + " 毫秒");
                mi.Click += delegate { SetInterval(value); };
                interval.DropDownItems.Add(mi);
                _intervalItems.Add(mi);
            }

            _miStartup = new ToolStripMenuItem("开机自启");
            _miStartup.Click += delegate
            {
                Startup.Set(!Startup.IsEnabled());
                _miStartup.Checked = Startup.IsEnabled();
            };

            ToolStripMenuItem pin = new ToolStripMenuItem("固定托盘图标");
            pin.Click += delegate
            {
                bool ok = TrayPromote.Apply(Application.ExecutablePath);
                _ni.ShowBalloonTip(2500, "功耗监控",
                    ok ? "已设为常驻托盘；若仍在折叠区，重启资源管理器后生效。" : "尚未注册到托盘，请稍后再试。",
                    ToolTipIcon.Info);
            };

            ToolStripMenuItem rescan = new ToolStripMenuItem("重新检测传感器");
            rescan.Click += delegate
            {
                _sampler.ResetSensors();
                _lastTime = DateTime.MinValue;
            };

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApp(); };

            _menu.Items.Add(open);
            _menu.Items.Add(stats);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(display);
            _menu.Items.Add(interval);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miStartup);
            _menu.Items.Add(pin);
            _menu.Items.Add(rescan);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exit);

            _menu.Opening += delegate
            {
                for (int i = 0; i < _metricItems.Count; i++) _metricItems[i].Checked = _cfg.TrayMetric == i;
                for (int i = 0; i < _intervalItems.Count; i++) _intervalItems[i].Checked = _cfg.IntervalMs == _intervals[i];
                _miStartup.Checked = Startup.IsEnabled();
            };
        }

        void SetMetric(int m)
        {
            _cfg.TrayMetric = m;
            _cfg.Save();
            _iconText = "";
            RefreshTray(_last);
            if (_card.Visible && _last != null) _card.UpdateData(_last, _hist, _wh, _cfg);
        }

        void SetInterval(int ms)
        {
            _cfg.IntervalMs = ms;
            _cfg.Save();
            if (_card.Visible && _last != null) _card.UpdateData(_last, _hist, _wh, _cfg);
        }

        void OnSample(Reading r)
        {
            if (_exiting) return;
            try
            {
                if (!_card.IsHandleCreated) return;
                _card.BeginInvoke((MethodInvoker)delegate { ApplySample(r); });
            }
            catch { }
        }

        void ApplySample(Reading r)
        {
            if (_exiting) return;
            double dt = 0;
            if (_lastTime != DateTime.MinValue)
            {
                dt = (r.Time - _lastTime).TotalSeconds;
                if (dt > 0 && dt < 30) _wh += r.TotalSm * dt / 3600.0;
                else dt = 0;
            }
            _lastTime = r.Time;
            _last = r;

            _hist.Add(r.TotalSm);
            while (_hist.Count > 120) _hist.RemoveAt(0);

            _history.AddSample(r, dt);
            _history.Flush(false);

            RefreshTray(r);
            if (_card.Visible) _card.UpdateData(r, _hist, _wh, _cfg);
            if (_stats != null && !_stats.IsDisposed && _stats.Visible) _stats.NotifyData(r);
        }

        static string TrayText(double v)
        {
            if (v < 0) return "--";
            if (v >= 1000) return (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            return Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        }

        void RefreshTray(Reading r)
        {
            if (r == null)
            {
                if (_icon != null) { _icon.Dispose(); _icon = null; }
                _icon = IconFactory.Create("--", Color.FromArgb(255, 140, 150, 168), IconSize());
                _ni.Icon = _icon;
                _iconText = "--";
                return;
            }

            double v;
            double green, amber;
            if (_cfg.TrayMetric == 1) { v = r.CpuSm; green = 25; amber = 65; }
            else if (_cfg.TrayMetric == 2) { v = r.GpuSm; green = 20; amber = 80; }
            else { v = r.TotalSm; green = 90; amber = 190; }

            string text = TrayText(v);
            _dbg++;
            Color accent = v < green ? Color.FromArgb(255, 62, 205, 132)
                         : v < amber ? Color.FromArgb(255, 255, 178, 46)
                         : Color.FromArgb(255, 255, 96, 92);

            if (text != _iconText || !accent.Equals(_iconColor) || _ni.Icon == null)
            {
                Icon old = _icon;
                _icon = IconFactory.Create(text, accent, IconSize());
                _ni.Icon = _icon;
                if (old != null) old.Dispose();
                _iconText = text;
                _iconColor = accent;
            }

            string cpu = r.CpuOk ? W1(r.CpuSm) + " W" : "—";
            string gpu = r.GpuOk ? W1(r.GpuSm) + " W" : "—";
            double todayWh = _history.TodayWh();
            string today = "今日 " + (todayWh / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " kWh";
            string tip = "整机 " + W1(r.TotalSm) + " W\n"
                       + "CPU " + cpu + " · GPU " + gpu + "\n"
                       + today + "\n"
                       + "平台 " + r.PlatformW.ToString("0", CultureInfo.InvariantCulture) + " W 估算";
            if (tip.Length > 127) tip = tip.Substring(0, 127);
            if (_ni.Text != tip) _ni.Text = tip;
        }

        static string W1(double v)
        {
            return v < 0 ? "--" : v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        void ShowPanel()
        {
            _card.ShowPanel(_last, _hist, _wh, _cfg);
        }

        void ShowStats()
        {
            try
            {
                if (_stats == null || _stats.IsDisposed)
                {
                    StatsForm f = new StatsForm(_history, _cfg);
                    f.FormClosed += delegate { _stats = null; };
                    _stats = f;
                }
                if (!_stats.Visible) _stats.Show();
                _stats.TopMost = true;
                _stats.TopMost = false;
                _stats.BringToFront();
                _stats.Activate();
                _stats.RefreshView();
            }
            catch (Exception ex) { Paths.Log("stats: " + ex); }
        }

        void SaveIconSnapshot(string cardPath)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(cardPath);
                if (string.IsNullOrEmpty(dir)) return;
                double v = _last == null ? 0 : _last.TotalSm;
                string text = TrayText(v);
                Color accent = v < 90 ? Color.FromArgb(255, 62, 205, 132)
                             : v < 190 ? Color.FromArgb(255, 255, 178, 46)
                             : Color.FromArgb(255, 255, 96, 92);
                using (Icon ic = IconFactory.Create(text, accent, 16))
                using (Bitmap b = ic.ToBitmap())
                {
                    b.Save(System.IO.Path.Combine(dir, "tray16.png"), ImageFormat.Png);
                    using (Bitmap big = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(big))
                        {
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(b, new Rectangle(0, 0, 128, 128));
                        }
                        big.Save(System.IO.Path.Combine(dir, "tray16-8x.png"), ImageFormat.Png);
                    }
                }
            }
            catch (Exception ex) { Paths.Log("iconsnap: " + ex.Message); }
        }

        void ExitApp()
        {
            _exiting = true;
            try { _sampler.Stop(); } catch { }
            try { if (_history != null) _history.Flush(true); } catch { }
            try { _promoteTimer.Stop(); } catch { }
            try { _trimTimer.Stop(); } catch { }
            try { if (_stats != null && !_stats.IsDisposed) _stats.Close(); } catch { }
            try { _ni.Visible = false; _ni.Dispose(); } catch { }
            try { if (_icon != null) _icon.Dispose(); } catch { }
            try { if (_showEvent != null) _showEvent.Set(); } catch { }
            Application.Exit();
        }
    }
}
