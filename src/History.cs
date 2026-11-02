// 历史记录层：分钟级功率桶 + 每次开机会话，落盘为 CSV，崩溃至多丢 1 分钟
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PowerMonitor
{
    internal sealed class HistoryBucket
    {
        public DateTime Ts;                 // 分钟起点（本地时间）
        public double SumW, MaxW = double.MinValue, MinW = double.MaxValue;
        public int N;
        public double SumCpu; public int NCpu;
        public double SumGpu; public int NGpu;
        public double SumPlat;

        public double Avg { get { return N > 0 ? SumW / N : 0; } }
        public double AvgCpu { get { return NCpu > 0 ? SumCpu / NCpu : 0; } }
        public double AvgGpu { get { return NGpu > 0 ? SumGpu / NGpu : 0; } }
        public double AvgPlat { get { return N > 0 ? SumPlat / N : 0; } }
        public double Weight = 0;            // 该桶覆盖的分钟数（按采样间隔累加，聚合时相加）
        public double EnergyWh, CpuEnergyWh, GpuEnergyWh, PlatEnergyWh;

        public double Wh
        {
            get
            {
                if (EnergyWh > 0) return EnergyWh;
                return N > 0 && Weight > 0 ? Avg * Weight / 60.0 : 0;
            }
        }
        public double CpuWh { get { return CpuEnergyWh > 0 ? CpuEnergyWh : (NCpu > 0 && Weight > 0 ? AvgCpu * Weight / 60.0 : 0); } }
        public double GpuWh { get { return GpuEnergyWh > 0 ? GpuEnergyWh : (NGpu > 0 && Weight > 0 ? AvgGpu * Weight / 60.0 : 0); } }
        public double PlatWh { get { return PlatEnergyWh > 0 ? PlatEnergyWh : (N > 0 && Weight > 0 ? AvgPlat * Weight / 60.0 : 0); } }

        public void Add(double total, double cpu, double gpu, double plat, double dtSec)
        {
            SumW += total;
            N++;
            if (total > MaxW) MaxW = total;
            if (total < MinW) MinW = total;
            if (cpu >= 0) { SumCpu += cpu; NCpu++; }
            if (gpu >= 0) { SumGpu += gpu; NGpu++; }
            SumPlat += plat;

            if (dtSec > 0 && dtSec <= 30)
            {
                Weight += dtSec / 60.0;
                EnergyWh += total * dtSec / 3600.0;
                if (cpu >= 0) CpuEnergyWh += cpu * dtSec / 3600.0;
                if (gpu >= 0) GpuEnergyWh += gpu * dtSec / 3600.0;
                PlatEnergyWh += plat * dtSec / 3600.0;
            }
        }

        public void MergeFrom(HistoryBucket o)
        {
            SumW += o.SumW; N += o.N;
            if (o.MaxW > MaxW) MaxW = o.MaxW;
            if (o.MinW < MinW && o.N > 0) MinW = o.MinW;
            SumCpu += o.SumCpu; NCpu += o.NCpu;
            SumGpu += o.SumGpu; NGpu += o.NGpu;
            SumPlat += o.SumPlat;
            Weight += o.Weight;
            EnergyWh += o.Wh;
            CpuEnergyWh += o.CpuWh;
            GpuEnergyWh += o.GpuWh;
            PlatEnergyWh += o.PlatWh;
        }

        public string ToLine()
        {
            return string.Join(",", new string[] {
                Ts.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                SumW.ToString("0.###", CultureInfo.InvariantCulture),
                N.ToString(CultureInfo.InvariantCulture),
                (N > 0 ? MaxW : 0).ToString("0.###", CultureInfo.InvariantCulture),
                (N > 0 ? MinW : 0).ToString("0.###", CultureInfo.InvariantCulture),
                SumCpu.ToString("0.###", CultureInfo.InvariantCulture),
                NCpu.ToString(CultureInfo.InvariantCulture),
                SumGpu.ToString("0.###", CultureInfo.InvariantCulture),
                NGpu.ToString(CultureInfo.InvariantCulture),
                SumPlat.ToString("0.###", CultureInfo.InvariantCulture),
                EnergyWh.ToString("0.#####", CultureInfo.InvariantCulture),
                CpuEnergyWh.ToString("0.#####", CultureInfo.InvariantCulture),
                GpuEnergyWh.ToString("0.#####", CultureInfo.InvariantCulture),
                PlatEnergyWh.ToString("0.#####", CultureInfo.InvariantCulture),
                Weight.ToString("0.####", CultureInfo.InvariantCulture)
            });
        }

        public static HistoryBucket Parse(string line)
        {
            string[] p = line.Split(',');
            if (p.Length < 10) return null;
            HistoryBucket b = new HistoryBucket();
            DateTime ts;
            if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out ts)) return null;
            b.Ts = ts;
            double d; int n;
            if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null;
            b.SumW = d;
            if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return null;
            b.N = n;
            double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out b.MaxW);
            double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out b.MinW);
            double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out b.SumCpu);
            int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out b.NCpu);
            double.TryParse(p[7], NumberStyles.Float, CultureInfo.InvariantCulture, out b.SumGpu);
            int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out b.NGpu);
            double.TryParse(p[9], NumberStyles.Float, CultureInfo.InvariantCulture, out b.SumPlat);
            if (p.Length >= 14)
            {
                double.TryParse(p[10], NumberStyles.Float, CultureInfo.InvariantCulture, out b.EnergyWh);
                double.TryParse(p[11], NumberStyles.Float, CultureInfo.InvariantCulture, out b.CpuEnergyWh);
                double.TryParse(p[12], NumberStyles.Float, CultureInfo.InvariantCulture, out b.GpuEnergyWh);
                double.TryParse(p[13], NumberStyles.Float, CultureInfo.InvariantCulture, out b.PlatEnergyWh);
            }
            if (p.Length >= 15)
                double.TryParse(p[14], NumberStyles.Float, CultureInfo.InvariantCulture, out b.Weight);
            else
                b.Weight = 1.0;   // 旧格式没有权重列，按 1 分钟计
            return b;
        }
    }

    internal sealed class HistorySession
    {
        public string Id = "";
        public DateTime Boot;
        public DateTime First;
        public DateTime Last;
        public double Wh, CpuWh, GpuWh, PlatWh;
        public double PeakW;
        public DateTime PeakAt;
        public long Samples;

        public double AvgW
        {
            get
            {
                double h = (Last - First).TotalHours;
                return h > 0.033 ? Wh / h : 0;   // 少于 2 分钟不折算，避免噪声
            }
        }

        public TimeSpan Uptime { get { return Last > Boot ? Last - Boot : TimeSpan.Zero; } }

        public string ToLine()
        {
            return string.Join(",", new string[] {
                Id,
                Boot.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                First.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Last.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Wh.ToString("0.###", CultureInfo.InvariantCulture),
                CpuWh.ToString("0.###", CultureInfo.InvariantCulture),
                GpuWh.ToString("0.###", CultureInfo.InvariantCulture),
                PlatWh.ToString("0.###", CultureInfo.InvariantCulture),
                PeakW.ToString("0.###", CultureInfo.InvariantCulture),
                PeakAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Samples.ToString(CultureInfo.InvariantCulture)
            });
        }

        public static HistorySession Parse(string line)
        {
            string[] p = line.Split(',');
            if (p.Length < 11) return null;
            HistorySession s = new HistorySession();
            s.Id = p[0];
            try
            {
                s.Boot = DateTime.ParseExact(p[1], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                s.First = DateTime.ParseExact(p[2], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                s.Last = DateTime.ParseExact(p[3], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                s.Wh = double.Parse(p[4], CultureInfo.InvariantCulture);
                s.CpuWh = double.Parse(p[5], CultureInfo.InvariantCulture);
                s.GpuWh = double.Parse(p[6], CultureInfo.InvariantCulture);
                s.PlatWh = double.Parse(p[7], CultureInfo.InvariantCulture);
                s.PeakW = double.Parse(p[8], CultureInfo.InvariantCulture);
                s.PeakAt = DateTime.ParseExact(p[9], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                s.Samples = long.Parse(p[10], CultureInfo.InvariantCulture);
            }
            catch { return null; }
            return s;
        }
    }

    internal sealed class HistoryStore
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        readonly object _lock = new object();
        readonly List<HistoryBucket> _buckets = new List<HistoryBucket>();
        readonly List<HistorySession> _sessions = new List<HistorySession>();

        HistoryBucket _cur;
        HistorySession _session;
        int _written;
        bool _rewriteAll;
        DateTime _lastFlush = DateTime.MinValue;
        DateTime _lastFlushSession = DateTime.MinValue;

        public string Dir { get; private set; }
        public DateTime BootTime { get; private set; }
        public int RetentionDays = 60;
        public int SessionKeep = 400;

        public HistoryStore()
        {
            Dir = System.IO.Path.Combine(Paths.ConfigDir, "history");
            try { if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir); }
            catch { }
            DateTime now = DateTime.Now;
            ulong tick = 0;
            try { tick = GetTickCount64(); } catch { }
            BootTime = tick > 0 ? now.AddMilliseconds(-(double)tick) : now;
            if ((now - BootTime).TotalDays > 90) BootTime = now; // 时钟异常保护
        }

        string BucketFile { get { return System.IO.Path.Combine(Dir, "power-minutes.csv"); } }
        string SessionFile { get { return System.IO.Path.Combine(Dir, "sessions.csv"); } }

        public void Load()
        {
            lock (_lock)
            {
                LoadBuckets();
                LoadSessions();
                DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
                int before = _buckets.Count;
                _buckets.RemoveAll(delegate(HistoryBucket b) { return b.Ts < cutoff; });
                if (_buckets.Count != before) _rewriteAll = true;
                _written = _buckets.Count;

                string id = BootTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
                foreach (HistorySession s in _sessions)
                    if (s.Id == id) { _session = s; break; }
                if (_session == null)
                {
                    _session = new HistorySession();
                    _session.Id = id;
                    _session.Boot = BootTime;
                    _session.First = DateTime.Now;
                    _session.Last = DateTime.Now;
                    _sessions.Add(_session);
                }
            }
        }

        void LoadBuckets()
        {
            try
            {
                if (!File.Exists(BucketFile)) return;
                foreach (string line in File.ReadAllLines(BucketFile))
                {
                    if (line.Length < 8) continue;
                    HistoryBucket b = HistoryBucket.Parse(line);
                    if (b != null && b.N > 0) _buckets.Add(b);
                }
                _buckets.Sort(delegate(HistoryBucket a, HistoryBucket c) { return a.Ts.CompareTo(c.Ts); });
            }
            catch (Exception ex) { Paths.Log("history load buckets: " + ex.Message); }
        }

        void LoadSessions()
        {
            try
            {
                if (!File.Exists(SessionFile)) return;
                foreach (string line in File.ReadAllLines(SessionFile))
                {
                    if (line.Length < 10) continue;
                    HistorySession s = HistorySession.Parse(line);
                    if (s != null) _sessions.Add(s);
                }
                _sessions.Sort(delegate(HistorySession a, HistorySession b) { return a.Boot.CompareTo(b.Boot); });
                while (_sessions.Count > SessionKeep) _sessions.RemoveAt(0);
            }
            catch (Exception ex) { Paths.Log("history load sessions: " + ex.Message); }
        }

        public void AddSample(Reading r, double dtSec)
        {
            lock (_lock)
            {
                DateTime t = r.Time;
                double total = r.TotalSm;
                double cpu = r.CpuOk ? r.CpuSm : -1;
                double gpu = r.GpuOk ? r.GpuSm : -1;
                double plat = r.PlatformW;

                DateTime minute = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
                if (_cur == null || _cur.Ts != minute)
                {
                    _cur = FindBucket(minute);
                    if (_cur == null)
                    {
                        _cur = new HistoryBucket();
                        _cur.Ts = minute;
                        _buckets.Add(_cur);
                    }
                    else
                    {
                        // 同一分钟内有另一次运行写过数据 -> 合并，需要整体重写
                        _rewriteAll = true;
                    }
                }
                _cur.Add(total, cpu, gpu, plat, dtSec);

                if (dtSec > 0 && dtSec <= 30)
                {
                    double wh = total * dtSec / 3600.0;
                    _session.Wh += wh;
                    if (r.CpuOk) _session.CpuWh += cpu * dtSec / 3600.0;
                    if (r.GpuOk) _session.GpuWh += gpu * dtSec / 3600.0;
                    _session.PlatWh += plat * dtSec / 3600.0;
                    _session.Samples++;
                }
                _session.Last = t;
                if (total > _session.PeakW) { _session.PeakW = total; _session.PeakAt = t; }
            }
        }

        HistoryBucket FindBucket(DateTime minute)
        {
            int lo = 0, hi = _buckets.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int c = _buckets[mid].Ts.CompareTo(minute);
                if (c == 0) return _buckets[mid];
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return null;
        }

        public void Flush(bool force)
        {
            lock (_lock)
            {
                DateTime now = DateTime.Now;
                bool doBuckets = force || _rewriteAll || (now - _lastFlush).TotalSeconds >= 60;
                if (doBuckets)
                {
                    _lastFlush = now;
                    WriteBuckets();
                }
                if (force || (now - _lastFlushSession).TotalSeconds >= 30)
                {
                    _lastFlushSession = now;
                    WriteSessions();
                }
            }
        }

        void WriteBuckets()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                if (_rewriteAll)
                {
                    for (int i = 0; i < _buckets.Count; i++) sb.AppendLine(_buckets[i].ToLine());
                    File.WriteAllText(BucketFile, sb.ToString(), Encoding.UTF8);
                    _written = _buckets.Count;
                    _rewriteAll = false;
                }
                else
                {
                    if (_buckets.Count <= _written) return;
                    for (int i = _written; i < _buckets.Count; i++) sb.AppendLine(_buckets[i].ToLine());
                    File.AppendAllText(BucketFile, sb.ToString(), Encoding.UTF8);
                    _written = _buckets.Count;
                }
            }
            catch (Exception ex) { Paths.Log("history write buckets: " + ex.Message); }
        }

        void WriteSessions()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < _sessions.Count; i++) sb.AppendLine(_sessions[i].ToLine());
                File.WriteAllText(SessionFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Paths.Log("history write sessions: " + ex.Message); }
        }

        public HistorySession Current { get { lock (_lock) { return _session; } } }
        public DateTime Boot { get { return BootTime; } }

        public List<HistoryBucket> BucketsSince(DateTime from)
        {
            lock (_lock)
            {
                List<HistoryBucket> list = new List<HistoryBucket>();
                for (int i = 0; i < _buckets.Count; i++)
                    if (_buckets[i].Ts >= from) list.Add(Clone(_buckets[i]));
                if (_cur != null && _cur.Ts >= from && (list.Count == 0 || list[list.Count - 1].Ts != _cur.Ts))
                    list.Add(Clone(_cur));
                return list;
            }
        }

        public List<HistoryBucket> AllBuckets()
        {
            lock (_lock)
            {
                List<HistoryBucket> list = new List<HistoryBucket>(_buckets.Count);
                for (int i = 0; i < _buckets.Count; i++) list.Add(Clone(_buckets[i]));
                return list;
            }
        }

        static HistoryBucket Clone(HistoryBucket b)
        {
            HistoryBucket c = new HistoryBucket();
            c.Ts = b.Ts; c.SumW = b.SumW; c.MaxW = b.MaxW; c.MinW = b.MinW; c.N = b.N;
            c.SumCpu = b.SumCpu; c.NCpu = b.NCpu; c.SumGpu = b.SumGpu; c.NGpu = b.NGpu; c.SumPlat = b.SumPlat;
            c.Weight = b.Weight; c.EnergyWh = b.EnergyWh; c.CpuEnergyWh = b.CpuEnergyWh;
            c.GpuEnergyWh = b.GpuEnergyWh; c.PlatEnergyWh = b.PlatEnergyWh;
            return c;
        }

        public List<HistorySession> Sessions()
        {
            lock (_lock)
            {
                List<HistorySession> list = new List<HistorySession>(_sessions.Count);
                for (int i = 0; i < _sessions.Count; i++) list.Add(_sessions[i]);
                return list;
            }
        }

        public double TodayWh()
        {
            lock (_lock)
            {
                DateTime today = DateTime.Today;
                double wh = 0;
                for (int i = _buckets.Count - 1; i >= 0; i--)
                {
                    HistoryBucket b = _buckets[i];
                    if (b.Ts < today) break;
                    wh += b.Wh;
                }
                return wh;
            }
        }

        public string ExportCsv()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("时间,平均功率W,最大W,最小W,样本数,平均CPUW,平均GPUW,平台W");
            List<HistoryBucket> all;
            lock (_lock) { all = new List<HistoryBucket>(_buckets); }
            for (int i = 0; i < all.Count; i++)
            {
                HistoryBucket b = all[i];
                sb.AppendLine(string.Join(",", new string[] {
                    b.Ts.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    b.Avg.ToString("0.0", CultureInfo.InvariantCulture),
                    (b.N > 0 ? b.MaxW : 0).ToString("0.0", CultureInfo.InvariantCulture),
                    (b.N > 0 ? b.MinW : 0).ToString("0.0", CultureInfo.InvariantCulture),
                    b.N.ToString(CultureInfo.InvariantCulture),
                    b.NCpu > 0 ? b.AvgCpu.ToString("0.0", CultureInfo.InvariantCulture) : "",
                    b.NGpu > 0 ? b.AvgGpu.ToString("0.0", CultureInfo.InvariantCulture) : "",
                    b.AvgPlat.ToString("0.0", CultureInfo.InvariantCulture)
                }));
            }
            return sb.ToString();
        }
    }
}
