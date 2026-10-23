using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DiskWriteMonitor
{
    // ================= 工具函数 =================
    internal static class Fmt
    {
        public static string Bytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.##} {units[u]}";
        }

        public static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 0) bytesPerSecond = 0;
            if (bytesPerSecond < 1024) return $"{bytesPerSecond:0} B/s";
            double v = bytesPerSecond / 1024;
            if (v < 1024) return $"{v:0.0} KB/s";
            v /= 1024;
            if (v < 1024) return $"{v:0.00} MB/s";
            v /= 1024;
            return $"{v:0.00} GB/s";
        }
    }

    // ================= 单日记录 =================
    public sealed class DayRecord
    {
        public string Date { get; set; } = "";      // yyyy-MM-dd
        public long WriteBytes { get; set; }
        public long ReadBytes { get; set; }
    }

    // ================= 本地 JSON 存储 =================
    public sealed class Store
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskWriteMonitor");
        private static readonly string FilePath = Path.Combine(Dir, "records.json");
        private static readonly string SettingPath = Path.Combine(Dir, "settings.json");

        public Dictionary<string, List<DayRecord>> Data { get; private set; } = new();

        public static Store Load()
        {
            var s = new Store();
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    s.Data = JsonSerializer.Deserialize<Dictionary<string, List<DayRecord>>>(json) ?? new();
                }
            }
            catch { s.Data = new(); }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private List<DayRecord> GetList(string partition)
        {
            if (!Data.TryGetValue(partition, out var list))
            {
                list = new List<DayRecord>();
                Data[partition] = list;
            }
            return list;
        }

        public void AddBytes(string partition, string date, long wDelta, long rDelta)
        {
            if (wDelta <= 0 && rDelta <= 0) return;
            var list = GetList(partition);
            var rec = list.FirstOrDefault(x => x.Date == date);
            if (rec == null)
            {
                rec = new DayRecord { Date = date };
                list.Add(rec);
                list.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
            }
            rec.WriteBytes = Math.Max(0, rec.WriteBytes + wDelta);
            rec.ReadBytes = Math.Max(0, rec.ReadBytes + rDelta);
        }

        public DayRecord GetDay(string partition, DateTime date)
        {
            string s = date.ToString("yyyy-MM-dd");
            return GetList(partition).FirstOrDefault(x => x.Date == s);
        }

        public long TotalWrite(string partition) => GetList(partition).Sum(x => x.WriteBytes);
        public long TotalRead(string partition) => GetList(partition).Sum(x => x.ReadBytes);
        public int DayCount(string partition) => GetList(partition).Count;

        public string FirstDate(string partition)
        {
            var l = GetList(partition);
            return l.Count == 0 ? null : l.Min(x => x.Date);
        }

        public void Clear(string partition) => Data.Remove(partition);

        public static string LastDrive
        {
            get
            {
                try
                {
                    if (File.Exists(SettingPath))
                    {
                        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingPath));
                        return d != null && d.TryGetValue("LastDrive", out var v) ? v : null;
                    }
                }
                catch { }
                return null;
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllText(SettingPath,
                        JsonSerializer.Serialize(new Dictionary<string, string> { ["LastDrive"] = value ?? "" }));
                }
                catch { }
            }
        }
    }

    // ================= PDH：读取磁盘性能计数器（免管理员、无第三方依赖） =================
    internal static class Pdh
    {
        private const int PDH_FMT_DOUBLE = 0x00000200;
        private const int PERF_DETAIL_WIZARD = 400;

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhAddCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        private static extern int PdhGetFormattedCounterValue(IntPtr counter, int format, out int type, out FmtValue value);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhEnumObjectItems(string dataSource, string machine, string obj,
            IntPtr counterList, ref int counterListLen, IntPtr instanceList, ref int instanceListLen,
            int detail, int flags);

        [DllImport("pdh.dll")]
        private static extern int PdhCloseQuery(IntPtr query);

        [StructLayout(LayoutKind.Sequential)]
        private struct FmtValue
        {
            public int Status;
            public double Value;
        }

        public static List<string> GetDiskInstances()
        {
            var result = new List<string>();
            int clen = 0, ilen = 0;
            if (PdhEnumObjectItems(null, null, "PhysicalDisk", IntPtr.Zero, ref clen, IntPtr.Zero, ref ilen,
                    PERF_DETAIL_WIZARD, 0) != 0)
                return result;
            if (clen <= 0 || ilen <= 0) return result;

            var cbuf = Marshal.AllocHGlobal(clen * 2);
            var ibuf = Marshal.AllocHGlobal(ilen * 2);
            try
            {
                if (PdhEnumObjectItems(null, null, "PhysicalDisk", cbuf, ref clen, ibuf, ref ilen,
                        PERF_DETAIL_WIZARD, 0) == 0)
                    ParseStrings(ibuf, ilen * 2, result);
            }
            finally
            {
                Marshal.FreeHGlobal(cbuf);
                Marshal.FreeHGlobal(ibuf);
            }
            return result;
        }

        private static void ParseStrings(IntPtr ptr, int byteLen, List<string> into)
        {
            int off = 0;
            while (off + 8 < byteLen)
            {
                var s = Marshal.PtrToStringUni(ptr + off);
                if (string.IsNullOrEmpty(s)) break;
                into.Add(s);
                off += (s.Length + 1) * 2;
            }
        }

        public static string FindInstance(IEnumerable<string> instances, char letter)
        {
            string want = letter + ":";
            foreach (var inst in instances)
                if (inst == want || inst.EndsWith(" " + want)) return inst;
            return null;
        }

        public static int OpenQuery(out IntPtr query) => PdhOpenQuery(null, IntPtr.Zero, out query);
        public static int AddCounter(IntPtr query, string path, out IntPtr counter) =>
            PdhAddCounter(query, path, IntPtr.Zero, out counter);
        public static int Collect(IntPtr query) => PdhCollectQueryData(query);
        public static int CloseQuery(IntPtr query) => PdhCloseQuery(query);

        public static double GetValue(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return 0;
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var v) != 0) return 0;
            return v.Value < 0 ? 0 : v.Value;
        }
    }

    // ================= 磁盘采样器 =================
    public sealed class DiskSampler : IDisposable
    {
        private IntPtr _query, _readCounter, _writeCounter;

        public bool Attach(string instance)
        {
            Detach();
            if (string.IsNullOrEmpty(instance)) return false;
            if (Pdh.OpenQuery(out _query) != 0) return false;
            if (Pdh.AddCounter(_query, $@"\PhysicalDisk({instance})\Disk Read Bytes/sec", out _readCounter) != 0)
            { Detach(); return false; }
            if (Pdh.AddCounter(_query, $@"\PhysicalDisk({instance})\Disk Write Bytes/sec", out _writeCounter) != 0)
            { Detach(); return false; }
            Pdh.Collect(_query); // 基线采样
            return true;
        }

        public (double ReadRate, double WriteRate) Sample()
        {
            if (_query == IntPtr.Zero) return (0, 0);
            try
            {
                Pdh.Collect(_query);
                return (Pdh.GetValue(_readCounter), Pdh.GetValue(_writeCounter));
            }
            catch { return (0, 0); }
        }

        public void Detach()
        {
            if (_query != IntPtr.Zero) { Pdh.CloseQuery(_query); _query = IntPtr.Zero; }
            _readCounter = IntPtr.Zero;
            _writeCounter = IntPtr.Zero;
        }

        public void Dispose() => Detach();
    }

    // ================= 图表数据项 =================
    public sealed class ChartItem
    {
        public string Label;
        public long Write;
        public long Read;
    }

    // ================= 柱状图面板 =================
    public sealed class ChartPanel : Panel
    {
        private List<ChartItem> _items = new();

        public void SetData(IEnumerable<ChartItem> items)
        {
            _items = items?.ToList() ?? new List<ChartItem>();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 图例
            DrawLegend(g);

            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, "暂无数据", Font,
                    new Rectangle(0, 0, Width, Height), Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            long max = _items.Max(i => Math.Max(i.Write, i.Read));
            if (max <= 0) max = 1;

            const int padLeft = 10, padRight = 10, padTop = 46, padBottom = 26;
            int chartW = Width - padLeft - padRight;
            int chartH = Height - padTop - padBottom;
            int n = _items.Count;
            double slot = n > 0 ? chartW / (double)n : 0;
            double barW = Math.Max(3, slot * 0.30);

            for (int i = 0; i < n; i++)
            {
                var it = _items[i];
                double x = padLeft + slot * i + slot / 2.0;

                if (it.Write > 0)
                {
                    double h = Math.Max(2, chartH * (it.Write / (double)max));
                    using var b = new SolidBrush(Color.FromArgb(66, 133, 244));
                    g.FillRectangle(b, (float)(x - barW - 1.5), (float)(padTop + chartH - h), (float)barW, (float)h);
                }
                if (it.Read > 0)
                {
                    double h = Math.Max(2, chartH * (it.Read / (double)max));
                    using var b = new SolidBrush(Color.FromArgb(52, 168, 83));
                    g.FillRectangle(b, (float)(x + 1.5), (float)(padTop + chartH - h), (float)barW, (float)h);
                }

                // X 轴标签（数据多时跳绘）
                int skip = n > 16 ? 3 : n > 10 ? 2 : 1;
                if (i % skip == 0 || i == n - 1)
                {
                    using var f = new Font(Font.FontFamily, 8f);
                    var txt = it.Label;
                    var sz = g.MeasureString(txt, f);
                    g.DrawString(txt, f, Brushes.DimGray,
                        (float)(x - sz.Width / 2), padTop + chartH + 4);
                }
            }

            if (max > 1)
                TextRenderer.DrawText(g, "峰值 " + Fmt.Bytes(max), Font,
                    new Rectangle(padLeft, 6, Width, 18), Color.DimGray);
        }

        private void DrawLegend(Graphics g)
        {
            using var f = new Font(Font.FontFamily, 8.5f);
            string ws = " 写入", rs = " 读取";
            float y = 6, x = Width - 150;
            g.FillRectangle(Brushes.RoyalBlue, x, y + 2, 11, 11);
            g.DrawString(ws, f, Brushes.DimGray, x + 14, y);
            x += 55;
            g.FillRectangle(Brushes.MediumSeaGreen, x, y + 2, 11, 11);
            g.DrawString(rs, f, Brushes.DimGray, x + 14, y);
        }
    }

    // ================= 主窗体 =================
    public sealed class MainForm : Form
    {
        private readonly Store _store = Store.Load();
        private readonly DiskSampler _sampler = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

        private ComboBox _cmbDrive;
        private Label _lblTodayDate, _lblTodayWrite, _lblTodayRead;
        private Label _lblTotalWrite, _lblTotalRead, _lblSince, _lblStat;
        private Label _lblWriteSpeed, _lblReadSpeed, _lblDataPath;
        private CheckBox _chkAutoStart;
        private NotifyIcon _notify;
        private bool _closing;

        // 历史页面
        private RadioButton _rb7d, _rbMonth, _rbDate;
        private DateTimePicker _dtpMonth, _dtpDate;
        private ChartPanel _chart;
        private DataGridView _grid;
        private Label _lblSum;

        // 采样状态
        private string _partKey;          // 如 "C:\"
        private char _partLetter;         // 如 'C'
        private string _instance;         // PDH 实例名，如 "0 C:"
        private Stopwatch _tickWatch = new();
        private int _saveCounter;

        public MainForm()
        {
            Text = "磁盘写入量监控";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(860, 640);
            MinimumSize = new Size(820, 600);
            BackColor = Color.White;

            BuildTodayTab();
            BuildHistoryTab();
            BuildTray();

            _timer.Tick += OnTick;
            Load += OnFormLoad;
            FormClosing += OnFormClosing;
        }

        // ------------- UI 构建 -------------
        private Label MakeLabel(int x, int y, string text, Font font, Color color, int w = 300)
        {
            var l = new Label { Text = text, Left = x, Top = y, Width = w, Font = font, ForeColor = color, AutoEllipsis = true };
            Controls.Add(l);
            return l;
        }

        private void BuildTodayTab()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(245, 247, 250) };
            Controls.Add(top);

            int cx = 14, cy = 12;
            top.Controls.Add(new Label { Text = "监控分区:", AutoSize = true, Left = cx, Top = cy + 3 });

            _cmbDrive = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = cx + 86, Top = cy, Width = 300
            };
            _cmbDrive.SelectedIndexChanged += (s, e) => OnDriveChanged();
            top.Controls.Add(_cmbDrive);

            var btnRefresh = new Button { Text = "刷新", Left = cx + 396, Top = cy - 1, Width = 60 };
            btnRefresh.Click += (s, e) => ReloadDrives(true);
            top.Controls.Add(btnRefresh);

            _chkAutoStart = new CheckBox { Text = "开机自启动", Left = cx + 470, Top = cy + 3, AutoSize = true };
            _chkAutoStart.CheckedChanged += (s, e) => SetAutoStart(_chkAutoStart.Checked);
            top.Controls.Add(_chkAutoStart);

            var btnClear = new Button { Text = "清空所选分区记录", Left = ClientSize.Width - 190, Top = cy - 1, Width = 176 };
            btnClear.Click += (s, e) => OnClear();
            top.Controls.Add(btnClear);
            btnClear.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            // 今日统计
            _lblTodayDate = MakeLabel(26, 68, "今日 (----)", new Font(Font, FontStyle.Bold), Color.FromArgb(60, 60, 60));
            _lblTodayWrite = MakeLabel(26, 96, "0 B", new Font("Microsoft YaHei UI", 24F, FontStyle.Bold), Color.FromArgb(66, 133, 244));
            _lblTodayRead = MakeLabel(300, 96, "0 B", new Font("Microsoft YaHei UI", 24F, FontStyle.Bold), Color.FromArgb(52, 168, 83));
            MakeLabel(26, 74, "今日写入量", new Font(Font.FontFamily, 9F), Color.Gray);
            MakeLabel(300, 74, "今日读取量", new Font(Font.FontFamily, 9F), Color.Gray);

            // 累计统计
            MakeLabel(26, 190, "自安装以来累计写入量", new Font(Font.FontFamily, 9F), Color.Gray);
            _lblTotalWrite = MakeLabel(26, 212, "0 B", new Font("Microsoft YaHei UI", 20F, FontStyle.Bold), Color.FromArgb(30, 60, 120));
            MakeLabel(300, 190, "累计读取量", new Font(Font.FontFamily, 9F), Color.Gray);
            _lblTotalRead = MakeLabel(300, 212, "0 B", new Font("Microsoft YaHei UI", 20F, FontStyle.Bold), Color.FromArgb(30, 90, 50));
            _lblSince = MakeLabel(26, 258, "", new Font(Font.FontFamily, 9F), Color.Gray, 600);

            // 实时速度
            var speedBox = new GroupBox { Text = "实时速度", Left = 20, Top = 300, Width = 560, Height = 110 };
            _lblWriteSpeed = MakeLabel(40, 332, "写入速度:  --", new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), Color.FromArgb(66, 133, 244), 250);
            _lblReadSpeed = MakeLabel(320, 332, "读取速度:  --", new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), Color.FromArgb(52, 168, 83), 250);
            speedBox.Controls.Add(_lblWriteSpeed);
            speedBox.Controls.Add(_lblReadSpeed);
            Controls.Add(speedBox);

            _lblStat = MakeLabel(26, 560, "", new Font(Font.FontFamily, 9F), Color.FromArgb(180, 80, 80), 800);
            _lblDataPath = MakeLabel(26, 580, "记录文件: " + Path.Combine(Store.Dir, "records.json"),
                new Font(Font.FontFamily, 8.5F), Color.Gray, 800);
            _lblDataPath.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _lblStat.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            speedBox.Anchor = AnchorStyles.Left | AnchorStyles.Top;

            MakeLabel(26, 430,
                "说明：程序运行时每秒采样一次磁盘计数器，并将累计数据保存到本地文件。\r\n" +
                "请保持程序运行（或开启“开机自启动”）以持续记录，关闭程序期间的数据无法补录。",
                new Font(Font.FontFamily, 9F), Color.DarkGray, 700);
        }

        private void BuildHistoryTab()
        {
            // 模式选择
            _rb7d = new RadioButton { Text = "最近7天", Left = 14, Top = 8, AutoSize = true, Checked = true };
            _rbMonth = new RadioButton { Text = "按月查看", Left = 110, Top = 8, AutoSize = true };
            _rbDate = new RadioButton { Text = "按指定日期", Left = 220, Top = 8, AutoSize = true };
            foreach (var rb in new[] { _rb7d, _rbMonth, _rbDate })
            {
                rb.CheckedChanged += (s, e) => { UpdatePickerVisibility(); RefreshHistory(); };
                Controls.Add(rb);
            }

            _dtpMonth = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy年MM月",
                ShowUpDown = true,
                Left = 330, Top = 4, Width = 140,
                Value = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)
            };
            _dtpMonth.ValueChanged += (s, e) => RefreshHistory();
            Controls.Add(_dtpMonth);

            _dtpDate = new DateTimePicker { Format = DateTimePickerFormat.Short, Left = 330, Top = 4, Width = 140 };
            _dtpDate.ValueChanged += (s, e) => RefreshHistory();
            Controls.Add(_dtpDate);
            _dtpDate.Visible = false;

            // 图表
            _chart = new ChartPanel
            {
                Left = 14, Top = 40, Width = ClientSize.Width - 28, Height = 250,
                BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle
            };
            _chart.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(_chart);

            // 表格
            _grid = new DataGridView
            {
                Left = 14, Top = 300, Width = ClientSize.Width - 28, Height = 250,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, BackgroundColor = Color.White,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "日期", Width = 160 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "写入量", Width = 240,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "读取量", Width = 240,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            Controls.Add(_grid);

            _lblSum = new Label
            {
                Left = 14, Top = ClientSize.Height - 40, Width = ClientSize.Width - 28,
                AutoSize = true, ForeColor = Color.FromArgb(60, 60, 60),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            Controls.Add(_lblSum);
        }

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开主界面", null, (s, e) => ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("推出(关闭主界面)", null, (s, e) =>
            {
                _closing = true;
                _store.Save();
                Application.Exit();
            });

            _notify = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "磁盘写入量监控（双击显示主界面）",
                Visible = true,
                ContextMenuStrip = menu
            };
            _notify.DoubleClick += (s, e) => ShowWindow();
        }

        // ------------- 生命周期 -------------
        private void OnFormLoad(object sender, EventArgs e)
        {
            _chkAutoStart.Checked = IsAutoStartEnabled();
            ReloadDrives(false);
            UpdatePickerVisibility();
            _tickWatch.Restart();
            _timer.Start();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_closing && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _notify.ShowBalloonTip(3000, "磁盘写入量监控", "程序已最小化到托盘，将继续记录磁盘数据。", ToolTipIcon.Info);
                return;
            }
            _timer.Stop();
            _store.Save();
        }

        private void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // ------------- 驱动器 -------------
        private void ReloadDrives(bool keepSelection)
        {
            string prev = keepSelection ? (_cmbDrive.SelectedItem as string) : null;
            var instances = Pdh.GetDiskInstances();
            string saved = !keepSelection ? Store.LastDrive : null;

            _cmbDrive.Items.Clear();
            string selected = null;

            foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
            {
                var letter = d.Name.TrimEnd('\\');
                var inst = Pdh.FindInstance(instances, letter[0]);
                bool monitored = inst != null;
                string label = $"{d.Name}  ({d.DriveFormat}  {Fmt.Bytes(d.TotalSize)})" + (monitored ? "" : "  [无法读取计数器]");
                _cmbDrive.Items.Add(label);

                if (selected == null)
                {
                    if (prev != null && label == prev) selected = label;
                    else if (!keepSelection && saved != null && d.Name.Equals(saved, StringComparison.OrdinalIgnoreCase)) selected = label;
                }
            }

            if (_cmbDrive.Items.Count == 0)
            {
                _cmbDrive.Items.Add("（未发现可用磁盘）");
                _cmbDrive.SelectedIndex = 0;
            }
            else if (selected != null)
            {
                _cmbDrive.SelectedItem = selected;
            }
            else
            {
                _cmbDrive.SelectedIndex = 0;
            }
            OnDriveChanged();
        }

        private void OnDriveChanged()
        {
            _sampler.Detach();
            _partKey = null;
            _instance = null;

            var text = _cmbDrive.SelectedItem as string;
            if (string.IsNullOrEmpty(text) || text.StartsWith("（")) return;

            var letter = text[0];
            var drive = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && x.Name[0] == letter);
            if (drive == null) return;

            var instances = Pdh.GetDiskInstances();
            _instance = Pdh.FindInstance(instances, letter);
            _partLetter = letter;
            _partKey = drive.Name;

            Store.LastDrive = _partKey;

            if (_instance == null)
            {
                _lblStat.Text = "未能找到该分区的磁盘性能计数器，数据无法记录。";
            }
            else if (_sampler.Attach(_instance))
            {
                _lblStat.Text = $"采样正常：性能计数器实例 [{_instance}]   每秒采样一次。";
            }
            else
            {
                _lblStat.Text = "计数器初始化失败，请稍后点击“刷新”重试。";
            }

            _tickWatch.Restart();
            UpdateTodayView();
            RefreshHistory();
        }

        // ------------- 今日 / 累计展示 -------------
        private void UpdateTodayView()
        {
            if (_partKey == null)
            {
                _lblTodayDate.Text = "今日 (--)";
                _lblTodayWrite.Text = "0 B";
                _lblTodayRead.Text = "0 B";
                _lblTotalWrite.Text = "0 B";
                _lblTotalRead.Text = "0 B";
                _lblSince.Text = "";
                return;
            }
            var today = _store.GetDay(_partKey, DateTime.Today);
            _lblTodayDate.Text = "今日 (" + DateTime.Today.ToString("yyyy-MM-dd") + ")";
            _lblTodayWrite.Text = Fmt.Bytes(today?.WriteBytes ?? 0);
            _lblTodayRead.Text = Fmt.Bytes(today?.ReadBytes ?? 0);
            _lblTotalWrite.Text = Fmt.Bytes(_store.TotalWrite(_partKey));
            _lblTotalRead.Text = Fmt.Bytes(_store.TotalRead(_partKey));
            var first = _store.FirstDate(_partKey);
            _lblSince.Text = first == null
                ? "首次运行，还没有记录。"
                : $"共记录 {_store.DayCount(_partKey)} 天，首次记录于 {first}。";
        }

        // ------------- 采样循环 -------------
        private void OnTick(object sender, EventArgs e)
        {
            double dt = _tickWatch.Elapsed.TotalSeconds;
            _tickWatch.Restart();
            if (dt <= 0.001) dt = 1.0;

            var (rr, wr) = _sampler.Sample();

            if (_partKey != null && _instance != null && (wr > 0 || rr > 0))
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                _store.AddBytes(_partKey, today, (long)Math.Round(wr * dt), (long)Math.Round(rr * dt));
            }

            // 无论是否有活动，每 10 秒落盘一次，保证数据即时持久化
            if (++_saveCounter % 10 == 0) _store.Save();

            _lblWriteSpeed.Text = "写入速度:  " + Fmt.Rate(wr);
            _lblReadSpeed.Text = "读取速度:  " + Fmt.Rate(rr);

            // 跨天时刷新今日显示
            if (_partKey != null && (DateTime.Now.ToString("yyyy-MM-dd") != _lblTodayDate.Tag as string))
            {
                UpdateTodayView();
                _lblTodayDate.Tag = DateTime.Now.ToString("yyyy-MM-dd");
            }
        }

        // ------------- 历史 -------------
        private void UpdatePickerVisibility()
        {
            _dtpMonth.Visible = _rbMonth.Checked;
            _dtpDate.Visible = _rbDate.Checked;
        }

        private void RefreshHistory()
        {
            if (_partKey == null)
            {
                _chart.SetData(null);
                _grid.Rows.Clear();
                _lblSum.Text = "请先在首页选择监控分区。";
                return;
            }

            var rows = new List<DayRecord>();
            var items = new List<ChartItem>();
            DateTime today = DateTime.Today;

            if (_rb7d.Checked)
            {
                for (int i = 6; i >= 0; i--)
                {
                    var d = today.AddDays(-i);
                    var rec = _store.GetDay(_partKey, d);
                    rows.Add(rec ?? new DayRecord { Date = d.ToString("yyyy-MM-dd") });
                    items.Add(new ChartItem
                    {
                        Label = d.ToString("MM-dd"),
                        Write = rec?.WriteBytes ?? 0,
                        Read = rec?.ReadBytes ?? 0
                    });
                }
            }
            else if (_rbMonth.Checked)
            {
                var m = _dtpMonth.Value;
                int days = DateTime.DaysInMonth(m.Year, m.Month);
                for (int day = 1; day <= days; day++)
                {
                    var d = new DateTime(m.Year, m.Month, day);
                    var rec = _store.GetDay(_partKey, d);
                    rows.Add(rec ?? new DayRecord { Date = d.ToString("yyyy-MM-dd") });
                    items.Add(new ChartItem { Label = day.ToString(), Write = rec?.WriteBytes ?? 0, Read = rec?.ReadBytes ?? 0 });
                }
            }
            else
            {
                var d = _dtpDate.Value.Date;
                var rec = _store.GetDay(_partKey, d);
                rows.Add(rec ?? new DayRecord { Date = d.ToString("yyyy-MM-dd") });
                items.Add(new ChartItem
                {
                    Label = d.ToString("MM-dd"),
                    Write = rec?.WriteBytes ?? 0,
                    Read = rec?.ReadBytes ?? 0
                });
            }

            _grid.Rows.Clear();
            foreach (var r in rows)
            {
                bool has = r.WriteBytes > 0 || r.ReadBytes > 0;
                int idx = _grid.Rows.Add(r.Date, Fmt.Bytes(r.WriteBytes), Fmt.Bytes(r.ReadBytes));
                if (!has)
                    _grid.Rows[idx].DefaultCellStyle.ForeColor = Color.FromArgb(180, 180, 180);
            }

            _chart.SetData(items);
            _lblSum.Text = $"合计({rows.Count}天): 写入 {Fmt.Bytes(rows.Sum(r => r.WriteBytes))}，读取 {Fmt.Bytes(rows.Sum(r => r.ReadBytes))}";
        }

        // ------------- 清空 -------------
        private void OnClear()
        {
            if (_partKey == null)
            {
                MessageBox.Show("请先选择要清空的监控分区。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(
                $"确定要清空分区 {_partKey} 的全部记录数据吗？\n此操作不可恢复。",
                "清空确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _store.Clear(_partKey);
            _store.Save();
            UpdateTodayView();
            RefreshHistory();
            MessageBox.Show("已清空 " + _partKey + " 的记录数据。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ------------- 开机自启动（注册表 HKCU Run） -------------
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "DiskWriteMonitor";

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        private bool _busyAutoStart;

        private void SetAutoStart(bool enable)
        {
            if (_busyAutoStart) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (key == null) return;
                if (enable)
                    key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else
                    key.DeleteValue(RunValue, false);
            }
            catch
            {
                MessageBox.Show("设置开机自启动失败（无法写入注册表）。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _busyAutoStart = true;
                _chkAutoStart.Checked = !enable;
                _busyAutoStart = false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sampler.Dispose();
                _timer.Dispose();
                _notify?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ================= 入口 =================
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "DiskWriteMonitor.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("磁盘写入量监控程序已经在运行中。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}