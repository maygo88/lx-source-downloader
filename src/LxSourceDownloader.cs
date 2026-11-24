using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace LxSourceDownloader
{
    internal sealed class SourceItem
    {
        public string Name;
        public string Path;
        public string RepoKey;
        public bool LxOnly;
        public bool IsNew;

        public SourceItem(string name, string path)
        {
            Name = name;
            Path = path;
            RepoKey = "pdone";
        }
    }

    internal sealed class WorkResult
    {
        public int Added;
        public int Updated;
        public int Unchanged;
        public int Failed;
        public int Discovered;
        public string Error;
    }

    internal sealed class ItemUpdate
    {
        public string Name;
        public string Status;
        public string Size;
        public string Time;
        public Color Color;
    }

    /// <summary>
    /// 带 HTTP 状态码的下载失败。用来区分两种完全不同的情况：
    /// 404 是「上游没有这个文件」，其余是「镜像/网络这次不行」。
    /// </summary>
    internal sealed class HttpStatusException : Exception
    {
        public readonly int Status;

        public HttpStatusException(int status) : base("HTTP " + status)
        {
            Status = status;
        }
    }

    internal sealed class SourceFetchResult
    {
        public byte[] Data;
        public bool NotFound;
        public string Error;
    }

    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, IntPtr size);

        [STAThread]
        private static void Main(string[] args)
        {
            // 阻断第三方 overlay / 安全软件往本进程注入 DLL（例如游戏加加的 GPP64.dll，
            // 它会注入所有 GUI 进程并随机触发 0xC0000005）。仅允许微软签名的模块加载。
            try
            {
                if (Environment.GetEnvironmentVariable("LX_TOOL_NO_MITIGATION") != "1")
                {
                    SetErrorMode(0x0001 | 0x0002); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX
                    uint sigPolicy = 1; // MicrosoftSignedOnly
                    SetProcessMitigationPolicy(8, ref sigPolicy, (IntPtr)4);
                }
            }
            catch
            {
            }

            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol
                    | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.DefaultConnectionLimit = 8;
            }
            catch
            {
            }

            bool auto = false;
            foreach (string a in args)
            {
                if (a.Equals("/auto", StringComparison.OrdinalIgnoreCase) || a.Equals("--auto", StringComparison.OrdinalIgnoreCase))
                    auto = true;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { CrashLog(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { CrashLog(e.ExceptionObject as Exception); };

            // 构造函数会读取洛雪数据 / config.ini，任何异常都不该让进程静默消失
            // （没有窗口、没有提示、只有 crash.log）。这里兜住并给出可读的提示。
            MainForm f;
            try
            {
                f = new MainForm();
            }
            catch (Exception ex)
            {
                CrashLog(ex);
                if (!auto)
                {
                    try
                    {
                        MessageBox.Show("启动失败：" + ex.Message + "\r\n\r\n详情见 crash.log。",
                            "洛雪音源下载器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    catch
                    {
                    }
                }
                return;
            }

            if (auto)
            {
                f.AutoMode = true;
                f.WindowState = FormWindowState.Minimized;
                f.ShowInTaskbar = false;
            }
            try
            {
                Application.Run(f);
            }
            catch (Exception ex)
            {
                CrashLog(ex);
            }
        }

        private static void CrashLog(Exception ex)
        {
            string body = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  "
                + (ex == null ? "(no exception object)" : ex.ToString()) + Environment.NewLine;

            // exe 目录只读时（例如放进了 Program Files）退回到 %TEMP%，否则崩溃日志直接丢失。
            string[] targets = new string[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine(Path.GetTempPath(), "LxSourceDownloader")
            };
            foreach (string dir in targets)
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "crash.log"), body, new UTF8Encoding(true));
                    return;
                }
                catch
                {
                }
            }
        }
    }

    internal sealed class MainForm : Form
    {
        // 默认上游仓库（老版本只有一个仓库，配置里的 repo= 仍然指向它）。
        private const string DefaultRepo = "pdone/lx-music-source/main/";
        private const string DefaultRepoKey = "pdone";
        private const string AppTitle = "洛雪音源下载器 / LX Source Downloader";
        private static readonly string Version = typeof(Program).Assembly.GetName().Version.ToString(3);

        // 镜像失效很快，这里只保留当前实测可用的几个；config.ini 的 proxies= 可整体替换。
        private static readonly string[] DefaultProxies = new string[]
        {
            "https://gh-proxy.org/raw.githubusercontent.com/",
            "https://ghfast.top/raw.githubusercontent.com/",
            "https://gh-proxy.com/raw.githubusercontent.com/",
            "https://raw.githubusercontent.com/"
        };

        /// <summary>
        /// 内置的上游仓库注册表。每个仓库配一个短键，音源用 "键/仓库内路径" 指向它，
        /// 于是同一个列表里可以混放多个仓库的音源。
        /// </summary>
        private static readonly string[,] DefaultRepos = new string[,]
        {
            { DefaultRepoKey, DefaultRepo },
            { "gy",   "guoyue2010/lxmusic-/main/" },
            { "xzh",  "xzh767/lxmusic-source-all/main/" },
            { "wzh",  "wzh15802/lxmusic/main/" },
            { "sec",  "shellsec/lx-music-source/master/" },
            { "ikun", "Macrohard0001/lx-ikun-music-sources/main/" }
        };

        /// <summary>
        /// 首次运行的音源清单：{ 仓库键, 列表名(也是本地文件名), 仓库内路径, 是否默认勾选 }。
        /// pdone 的 8 个默认勾选；其余仓库的音源默认不勾选，避免第一次运行就拉一堆用不上的，
        /// 但它们都在列表里，想用直接勾上即可。
        /// </summary>
        private static readonly string[,] DefaultSources = new string[,]
        {
            { "pdone", "xinghai",     "xinghai/latest.js",     "1" },
            { "pdone", "qingliang",   "qingliang/latest.js",   "1" },
            { "pdone", "suyin",       "suyin/latest.js",       "1" },
            { "pdone", "huanyin",     "huanyin/latest.js",     "1" },
            { "pdone", "changqing",   "changqing/latest.js",   "1" },
            { "pdone", "fish",        "fish/latest.js",        "1" },
            { "pdone", "xinglan",     "xinglan/latest.js",     "1" },
            { "pdone", "feichangdao", "feichangdao/latest.js", "1" },

            // 以下来自其他仓库，2026-11-24 实测可下载且带合法 userscript 头
            { "gy",  "nianxin",     "V2603/优质-支持四平台FLAC/念心音源 v1.0.0.js",            "0" },
            { "gy",  "ciallo",      "V2603/一般-支持单平台FLAC或多平台320k/Ciallo~.js",        "0" },
            { "gy",  "yiyin",       "V2603/一般-支持单平台FLAC或多平台320k/忆音音源 v1.js",     "0" },
            { "gy",  "kuwodes",     "V2603/一般-支持单平台FLAC或多平台320k/KE-DES.js",          "0" },
            { "gy",  "yibai",       "V2603/一般-支持单平台FLAC或多平台320k/yibai酷我流式V4.js", "0" },
            { "gy",  "juheapi",     "V2603/一般-支持单平台FLAC或多平台320k/收集の聚合接口.js",   "0" },
            { "gy",  "xiyuan",      "V2603/一般-支持单平台FLAC或多平台320k/惜缘 v1.js",           "0" },
            { "xzh", "mdownloader", "MusicDownloader.js",                                     "0" },
            { "xzh", "aya",         "aya音源v1.0.2（QQ无法下载）.js",                          "0" },
            { "xzh", "gdstudio",    "gdstudio音乐源 v1.0.1（仅支持网易）.js",                  "0" },
            { "wzh", "kxh",         "K×H测试+v1.7.17.js",                                     "0" },
            { "sec", "animemusic",  "sources/animemusic/lx-animemusic.js",                    "0" },
            { "sec", "anni",        "sources/annie/安妮独家音源.js",                           "0" },
            { "sec", "appx64",      "sources/appx64/Appx64 Clear Music.js",                    "0" },
            { "sec", "doubao",      "sources/doubao/豆包定制[聚合音源].js",                     "0" }
        };

        private string _repo = DefaultRepo;
        private string _api = RepoApi(DefaultRepo);
        private List<string> _proxies = new List<string>(DefaultProxies);
        private readonly Dictionary<string, string> _repos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 上次并入内置清单时的版本号：版本一变就把新增的内置音源补进配置，老用户也能看到新音源。
        private string _defaultsStamp;
        // 探测 README.md 时拿到 404：说明仓库/分支路径本身不对，不是镜像挂了。
        private bool _repoMissing;

        private readonly List<SourceItem> _items = new List<SourceItem>();
        private readonly Dictionary<string, string> _hash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _time = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _outDir;
        private string _configPath;
        private string _workingProxy;
        // 本轮已判定失效的镜像：只由「真实下载失败」标记，避免对死站点重复等超时。
        private readonly HashSet<string> _deadProxies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _running;

        private string _lxPath;
        private Dictionary<string, object> _lxRoot;
        private readonly Dictionary<string, Dictionary<string, object>> _lxByName = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lxHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lxVersion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lxText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _checkState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _checkedCfg = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public bool AutoMode;

        private Panel _top;
        private Panel _bottom;
        private Panel _mid;
        private Label _lblPath;
        private TextBox _txtOut;
        private Button _btnBrowse;
        private Button _btnOpen;
        private SplitContainer _split;
        private ListView _list;
        private TextBox _log;
        private Button _btnUpdate;
        private Button _btnRefresh;
        private Button _btnAll;
        private Button _btnNone;
        private Button _btnSync;
        private ProgressBar _bar;
        private Label _lblStatus;
        private Button[] _buttons;

        public MainForm()
        {
            Text = AppTitle + " v" + Version;
            ClientSize = new Size(920, 660);
            MinimumSize = new Size(820, 560);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point); }
            catch { }

            // 顺序很重要：BuildUi 先建好日志框，后面读洛雪/配置出错时才写得出日志。
            LoadConfig();
            BuildUi();
            MergeNewDefaults();
            LoadLx();
            LoadList();
            SetStatus("就绪。勾选需要的音源，点击「检查并更新」。");
            Log("保存目录：" + _outDir);
            Log("配置文件：" + _configPath);
        }

        // ---------------- UI ----------------

        private void BuildUi()
        {
            _top = new Panel { Dock = DockStyle.None };
            Controls.Add(_top);

            _lblPath = new Label { Text = "保存位置", AutoSize = true, Location = new Point(12, 22) };
            _txtOut = new TextBox { Text = _outDir, Location = new Point(78, 18), Width = 600 };
            _btnBrowse = new Button { Text = "浏览…", AccessibleName = "浏览", Location = new Point(690, 16), Size = new Size(90, 30) };
            _btnOpen = new Button { Text = "打开", AccessibleName = "打开目录", Location = new Point(788, 16), Size = new Size(72, 30) };
            _top.Controls.Add(_lblPath);
            _top.Controls.Add(_txtOut);
            _top.Controls.Add(_btnBrowse);
            _top.Controls.Add(_btnOpen);

            _btnBrowse.Click += delegate { BrowseFolder(); };
            _btnOpen.Click += delegate { OpenFolder(); };
            _txtOut.Leave += delegate
            {
                string v = _txtOut.Text.Trim();
                ChangeOutDir(v);
            };

            _mid = new Panel { Dock = DockStyle.None };
            Controls.Add(_mid);

            _split = new SplitContainer { Orientation = Orientation.Horizontal, SplitterWidth = 6, BackColor = Color.FromArgb(232, 232, 232) };
            _split.Panel1.Padding = new Padding(12, 6, 12, 4);
            _split.Panel2.Padding = new Padding(12, 4, 12, 6);
            _mid.Controls.Add(_split);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                ShowItemToolTips = true
            };
            _list.Columns.Add("音源", 150);
            _list.Columns.Add("状态", 110);
            _list.Columns.Add("洛雪", 120);
            _list.Columns.Add("大小", 90);
            _list.Columns.Add("本地更新时间", 140);
            _list.Columns.Add("仓库路径", 300);
            _list.DoubleClick += delegate { OpenFolder(); };
            _split.Panel1.Controls.Add(_list);

            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                BorderStyle = BorderStyle.FixedSingle
            };
            try { _log.Font = new Font("Consolas", 9F); }
            catch { }
            _split.Panel2.Controls.Add(_log);

            _bottom = new Panel { Dock = DockStyle.None };
            Controls.Add(_bottom);

            _btnUpdate = new Button { Text = "检查并更新", AccessibleName = "检查并更新", Location = new Point(12, 10), Size = new Size(120, 34) };
            _btnRefresh = new Button { Text = "刷新清单", AccessibleName = "刷新清单", Location = new Point(140, 10), Size = new Size(100, 34) };
            _btnAll = new Button { Text = "全选", AccessibleName = "全选", Location = new Point(256, 10), Size = new Size(64, 34) };
            _btnNone = new Button { Text = "全不选", AccessibleName = "全不选", Location = new Point(326, 10), Size = new Size(64, 34) };
            _btnSync = new Button { Text = "同步到洛雪", AccessibleName = "同步到洛雪", Location = new Point(400, 10), Size = new Size(110, 34) };
            _bar = new ProgressBar { Location = new Point(12, 52), Size = new Size(600, 16), Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
            _lblStatus = new Label { Text = "", AccessibleName = "状态", AutoSize = false, Location = new Point(12, 72), Size = new Size(700, 18), ForeColor = Color.FromArgb(70, 70, 70) };

            _bottom.Controls.Add(_btnUpdate);
            _bottom.Controls.Add(_btnRefresh);
            _bottom.Controls.Add(_btnAll);
            _bottom.Controls.Add(_btnNone);
            _bottom.Controls.Add(_btnSync);
            _bottom.Controls.Add(_bar);
            _bottom.Controls.Add(_lblStatus);

            _btnUpdate.Click += delegate { StartUpdate(); };
            _btnRefresh.Click += delegate { StartDiscover(); };
            _btnAll.Click += delegate { SetAllChecked(true); };
            _btnNone.Click += delegate { SetAllChecked(false); };
            _btnSync.Click += delegate { SyncToLx(); };

            _buttons = new Button[] { _btnUpdate, _btnRefresh, _btnAll, _btnNone, _btnSync };

            Shown += delegate { if (AutoMode) BeginInvoke(new Action(StartUpdate)); };
            Resize += delegate { LayoutPanels(); };
            LayoutPanels();
        }

        private void LayoutPanels()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0 || h <= 0 || _buttons == null) return;

            const int gap = 8;

            // 布局全部由控件的实际尺寸推出来，不再写死像素坐标。这样当系统把字体和控件
            // 放大（DPI 覆盖 / 125%、150% 缩放）时，底栏按钮不会互相压住。
            int topHeight = _btnBrowse.Height + 28;
            int btnTop = (topHeight - _btnBrowse.Height) / 2;
            _top.Bounds = new Rectangle(0, 0, w, topHeight);

            int right = w - 12;
            _btnOpen.Left = right - _btnOpen.Width;
            _btnOpen.Top = btnTop;
            _btnBrowse.Left = _btnOpen.Left - gap - _btnBrowse.Width;
            _btnBrowse.Top = btnTop;
            _lblPath.Left = 12;
            _lblPath.Top = (topHeight - _lblPath.Height) / 2;
            _txtOut.Left = _lblPath.Right + gap;
            _txtOut.Top = (topHeight - _txtOut.Height) / 2;
            _txtOut.Width = Math.Max(120, _btnBrowse.Left - gap - _txtOut.Left);

            int rowTop = 10;
            int x = 12;
            int rowHeight = 0;
            foreach (Button b in _buttons)
            {
                b.Location = new Point(x, rowTop);
                x = b.Right + gap;
                if (b.Height > rowHeight) rowHeight = b.Height;
            }
            _bar.Location = new Point(12, rowTop + rowHeight + gap);
            _bar.Width = Math.Max(200, w - 24);
            _lblStatus.Location = new Point(12, _bar.Bottom + 4);
            _lblStatus.Width = Math.Max(200, w - 24);

            int bottomHeight = _lblStatus.Bottom + 6;
            _bottom.Bounds = new Rectangle(0, h - bottomHeight, w, bottomHeight);
            _mid.Bounds = new Rectangle(0, topHeight, w, Math.Max(0, h - topHeight - bottomHeight));

            if (_mid.Width > 0 && _mid.Height > 0)
            {
                _split.Bounds = _mid.ClientRectangle;
                int d = (int)(_mid.Height * 0.58);
                if (d > 180 && d < _mid.Height - 120) _split.SplitterDistance = d;
            }

            if (_list.Columns.Count == 6)
            {
                int fixedWidth = 150 + 110 + 120 + 90 + 140;
                _list.Columns[5].Width = Math.Max(160, _list.ClientSize.Width - fixedWidth - 24);
            }
        }

        // ---------------- list ----------------

        private void LoadList()
        {
            foreach (ListViewItem l in _list.Items)
                _checkState[((SourceItem)l.Tag).Name] = l.Checked;

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (SourceItem it in _items)
            {
                ListViewItem lvi = new ListViewItem(it.Name);
                lvi.Checked = _checkState.ContainsKey(it.Name) ? _checkState[it.Name] : (_checkedCfg.ContainsKey(it.Name) ? _checkedCfg[it.Name] : !it.IsNew);
                lvi.Tag = it;
                lvi.SubItems.Add("未检查");
                lvi.SubItems.Add("-");
                lvi.SubItems.Add("-");
                lvi.SubItems.Add("-");
                lvi.SubItems.Add(it.Path);
                _list.Items.Add(lvi);
            }
            _list.EndUpdate();
            foreach (SourceItem it in _items) it.IsNew = false;
            RefreshLocalState();
        }

        private void RefreshLocalState()
        {
            foreach (ListViewItem lvi in _list.Items)
            {
                SourceItem it = (SourceItem)lvi.Tag;
                string file = Path.Combine(_outDir, it.Name + ".js");
                if (File.Exists(file))
                {
                    FileInfo fi = new FileInfo(file);
                    lvi.SubItems[3].Text = Kb(fi.Length);
                    lvi.SubItems[4].Text = fi.LastWriteTime.ToString("MM-dd HH:mm");
                    if (!_hash.ContainsKey(it.Name))
                    {
                        try { _hash[it.Name] = Sha256(File.ReadAllBytes(file)); }
                        catch { }
                    }
                    if (lvi.SubItems[1].Text == "未检查" || lvi.SubItems[1].Text == "未下载")
                    {
                        lvi.SubItems[1].Text = "已下载";
                        lvi.ForeColor = Color.FromArgb(70, 70, 70);
                    }
                }
                else if (it.LxOnly)
                {
                    lvi.SubItems[3].Text = "-";
                    lvi.SubItems[4].Text = "-";
                    lvi.SubItems[1].Text = "仅洛雪";
                    lvi.ForeColor = Color.FromArgb(120, 90, 20);
                }
                else
                {
                    lvi.SubItems[3].Text = "-";
                    lvi.SubItems[4].Text = "-";
                    // 上一次运行给出的结论（失败 / 上游已移除 / 无效内容）不要被抹成「未下载」，
                    // 否则一次下载失败看起来就像从没下载过。
                    if (!IsResultStatus(lvi.SubItems[1].Text))
                    {
                        lvi.SubItems[1].Text = "未下载";
                        lvi.ForeColor = Color.Black;
                    }
                }
                UpdateLxCell(lvi, it);
            }
        }

        /// <summary>
        /// 「这次运行得到的结论」型状态：文件可能不存在，但这一行仍然有意义，
        /// 不能被文件状态覆盖掉。
        /// </summary>
        private static bool IsResultStatus(string status)
        {
            return status == "失败" || status == "无效内容" || status == "上游已移除"
                || status == "导出失败" || status == "无法导出";
        }

        private void SetAllChecked(bool value)
        {
            foreach (ListViewItem lvi in _list.Items) lvi.Checked = value;
        }

        // ---------------- actions ----------------

        private void BrowseFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择音源保存位置";
                dlg.ShowNewFolderButton = true;
                if (Directory.Exists(_outDir)) dlg.SelectedPath = _outDir;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtOut.Text = dlg.SelectedPath;
                    ChangeOutDir(dlg.SelectedPath);
                }
            }
        }

        private void ChangeOutDir(string v)
        {
            if (_running)
            {
                _txtOut.Text = _outDir;
                return;
            }
            if (string.IsNullOrEmpty(v) || string.Equals(v, _outDir, StringComparison.OrdinalIgnoreCase)) return;
            _outDir = v;
            _txtOut.Text = v;
            _hash.Clear();
            _time.Clear();
            RefreshLocalState();
            Log("保存目录已切换为：" + _outDir);
        }

        private void OpenFolder()
        {
            try
            {
                if (!Directory.Exists(_outDir)) Directory.CreateDirectory(_outDir);
                Process.Start("explorer.exe", "\"" + _outDir + "\"");
            }
            catch (Exception ex)
            {
                Log("打开目录失败：" + ex.Message);
            }
        }

        private void SetBusy(bool busy)
        {
            _running = busy;
            _btnUpdate.Enabled = !busy;
            _btnRefresh.Enabled = !busy;
            _btnBrowse.Enabled = !busy;
            _btnAll.Enabled = !busy;
            _btnNone.Enabled = !busy;
            _btnSync.Enabled = !busy;
            _btnOpen.Enabled = !busy;
            // 下载途中不许改保存位置：worker 线程用的是 _outDir 的快照，边跑边改会让
            // 同一批音源落到两个目录里。
            _txtOut.Enabled = !busy;
            _list.Enabled = !busy;
            _bar.Value = 0;
        }

        private void StartUpdate()
        {
            if (_running) return;
            List<string> names = new List<string>();
            foreach (ListViewItem lvi in _list.Items)
            {
                if (lvi.Checked) names.Add(((SourceItem)lvi.Tag).Name);
            }
            if (names.Count == 0)
            {
                Log("没有勾选任何音源。");
                // /auto 是无人值守的：没有可做的事也必须退出，否则计划任务会永远挂着一个进程。
                AutoFinish("没有勾选任何音源，未做任何更新。");
                return;
            }
            RunWorker(false, names, null);
        }

        /// <summary>
        /// 静默模式的收尾：写 auto.log 并关掉窗口。任何一条返回路径都必须经过这里，
        /// 否则 /auto 会留下一个不可见的进程。
        /// </summary>
        private void AutoFinish(string summary)
        {
            if (!AutoMode) return;
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "auto.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    summary + Environment.NewLine + "outdir=" + _outDir + Environment.NewLine,
                    new UTF8Encoding(true));
            }
            catch
            {
            }
            if (IsHandleCreated) BeginInvoke(new Action(Close));
            else Close();
        }

        private void StartDiscover()
        {
            if (_running) return;
            LoadLx();
            RunWorker(true, null, null);
        }

        private void RunWorker(bool discover, List<string> names, string dummy)
        {
            SetBusy(true);
            SetStatus(discover ? "正在从仓库读取音源清单…" : "开始检查更新…");
            Log("");
            Log("----- " + (discover ? "刷新清单" : "检查并更新") + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " -----");

            BackgroundWorker bw = new BackgroundWorker { WorkerReportsProgress = true };
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                WorkResult r = new WorkResult();
                try
                {
                    if (discover) DoDiscover(bw, r);
                    else DoUpdate(bw, names, r);
                }
                catch (Exception ex)
                {
                    r.Error = ex.Message;
                }
                e.Result = r;
            };
            bw.ProgressChanged += delegate(object s, ProgressChangedEventArgs e)
            {
                ItemUpdate iu = e.UserState as ItemUpdate;
                if (iu != null)
                {
                    ApplyItemUpdate(iu);
                    return;
                }
                string msg = e.UserState as string;
                if (msg != null) Log(msg);
                if (e.ProgressPercentage >= 0)
                {
                    int v = e.ProgressPercentage;
                    if (v > 100) v = 100;
                    _bar.Value = v;
                }
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                SetBusy(false);
                WorkResult r = e.Result as WorkResult;
                if (r == null)
                {
                    SetStatus("已中断。");
                    AutoFinish("已中断。");
                    return;
                }
                if (!string.IsNullOrEmpty(r.Error)) Log("出错：" + r.Error);
                if (discover)
                {
                    LoadList();
                    SetStatus("清单已刷新，当前共 " + _items.Count + " 个音源。");
                }
                else if (!string.IsNullOrEmpty(r.Error))
                {
                    SetStatus("出错：" + r.Error);
                }
                else
                {
                    SetStatus(string.Format("完成：新增 {0} / 更新 {1} / 已是最新 {2} / 失败 {3}", r.Added, r.Updated, r.Unchanged, r.Failed));
                }
                SaveConfig();
                RefreshLocalState();

                if (AutoMode) AutoFinish(_lblStatus.Text);
            };
            bw.RunWorkerAsync();
        }

        private void ApplyItemUpdate(ItemUpdate iu)
        {
            foreach (ListViewItem lvi in _list.Items)
            {
                SourceItem it = (SourceItem)lvi.Tag;
                if (!string.Equals(it.Name, iu.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (iu.Status != null) lvi.SubItems[1].Text = iu.Status;
                if (iu.Size != null) lvi.SubItems[3].Text = iu.Size;
                if (iu.Time != null) lvi.SubItems[4].Text = iu.Time;
                if (iu.Color != Color.Empty) lvi.ForeColor = iu.Color;
                UpdateLxCell(lvi, it);
                break;
            }
        }

        // ---------------- worker ----------------

        private void DoUpdate(BackgroundWorker bw, List<string> names, WorkResult r)
        {
            if (!Directory.Exists(_outDir)) Directory.CreateDirectory(_outDir);

            bool needNet = false;
            foreach (string n0 in names)
            {
                SourceItem i0 = Find(n0);
                if (i0 != null && !i0.LxOnly) { needNet = true; break; }
            }

            string proxy = null;
            if (needNet)
            {
                _deadProxies.Clear();
                _repoMissing = false;
                bw.ReportProgress(-1, "正在探测可用加速站…");
                proxy = EnsureProxy();
                if (_repoMissing)
                {
                    r.Error = "上游仓库不存在：" + _repo + "（HTTP 404）。可能是仓库改名、删库或默认分支变了——"
                        + "在 config.ini 里用 repo=owner/name/branch/ 指向新位置即可，不用等新版本。";
                    bw.ReportProgress(-1, r.Error);
                    return;
                }
                if (proxy == null)
                {
                    // 探测只是加速手段：真正的下载会带着 20 秒超时把所有镜像再试一遍，
                    // 所以探测没成功也继续，而不是整批直接判死。
                    bw.ReportProgress(-1, "加速站探测未成功，下载时逐个重试…");
                }
                else
                {
                    bw.ReportProgress(-1, "使用加速站：" + HostOf(proxy));
                }
            }

            int total = names.Count;
            int done = 0;
            foreach (string name in names)
            {
                SourceItem it = Find(name);
                if (it == null) continue;

                if (it.LxOnly)
                {
                    ItemUpdate xu = new ItemUpdate { Name = it.Name };
                    string text = _lxText.ContainsKey(it.Name) ? _lxText[it.Name] : null;
                    if (text == null)
                    {
                        r.Failed++;
                        xu.Status = "无法导出";
                        xu.Color = Color.FromArgb(192, 28, 40);
                        bw.ReportProgress(-1, "[" + it.Name + "] 洛雪里已找不到该音源");
                    }
                    else
                    {
                        try
                        {
                            // 不带 BOM：下载回来的脚本也是无 BOM 的，保持两条路径写出的文件一致。
                            File.WriteAllText(LocalScriptPath(it), text, new UTF8Encoding(false));
                            _hash[it.Name] = HashText(text);
                            r.Added++;
                            xu.Status = "已从洛雪导出";
                            xu.Size = Kb(Encoding.UTF8.GetByteCount(text));
                            xu.Time = DateTime.Now.ToString("MM-dd HH:mm");
                            xu.Color = Color.FromArgb(10, 125, 50);
                            bw.ReportProgress(-1, "[" + it.Name + "] 已从洛雪导出（" + Kb(Encoding.UTF8.GetByteCount(text)) + "）");
                        }
                        catch (Exception ex)
                        {
                            r.Failed++;
                            xu.Status = "导出失败";
                            xu.Color = Color.FromArgb(192, 28, 40);
                            bw.ReportProgress(-1, "[" + it.Name + "] 导出失败：" + ex.Message);
                        }
                    }
                    bw.ReportProgress(-1, xu);
                    done++;
                    bw.ReportProgress(done * 100 / total, (object)null);
                    continue;
                }

                ItemUpdate iu = new ItemUpdate { Name = it.Name };
                try
                {
                    bw.ReportProgress(-1, "[" + it.Name + "] 下载中…");
                    SourceFetchResult fr = FetchSource(proxy, it);
                    proxy = _workingProxy;

                    if (fr.NotFound)
                    {
                        // 404 是「上游删了这个音源」，不是网络问题：明确告诉用户该删哪一行。
                        r.Failed++;
                        iu.Status = "上游已移除";
                        iu.Size = "-";
                        iu.Time = "-";
                        iu.Color = Color.FromArgb(140, 100, 20);
                        bw.ReportProgress(-1, "[" + it.Name + "] 上游仓库里没有 " + it.Path
                            + "（HTTP 404），大概率已被作者删除；可以在 config.ini 里删掉 source." + it.Name + " 这一行");
                        bw.ReportProgress(-1, iu);
                        done++;
                        bw.ReportProgress(done * 100 / total, (object)null);
                        continue;
                    }

                    byte[] data = fr.Data;
                    if (data == null)
                    {
                        r.Failed++;
                        iu.Status = "失败";
                        iu.Size = "-";
                        iu.Time = "-";
                        iu.Color = Color.FromArgb(192, 28, 40);
                        bw.ReportProgress(-1, "[" + it.Name + "] 失败："
                            + (string.IsNullOrEmpty(fr.Error) ? "所有加速站都不可用" : fr.Error));
                        bw.ReportProgress(-1, iu);
                        done++;
                        bw.ReportProgress(done * 100 / total, (object)null);
                        continue;
                    }

                    if (!LooksLikeUserscript(data))
                    {
                        r.Failed++;
                        iu.Status = "无效内容";
                        iu.Size = Kb(data.Length);
                        iu.Time = "-";
                        iu.Color = Color.FromArgb(192, 28, 40);
                        bw.ReportProgress(-1, "[" + it.Name + "] 内容无效（" + data.Length + " 字节），已跳过");
                        bw.ReportProgress(-1, iu);
                        done++;
                        bw.ReportProgress(done * 100 / total, (object)null);
                        continue;
                    }

                    string sha = Sha256(data);
                    string file = Path.Combine(_outDir, it.Name + ".js");
                    bool exists = File.Exists(file);

                    if (exists && string.Equals(sha, _hash.ContainsKey(it.Name) ? _hash[it.Name] : "", StringComparison.OrdinalIgnoreCase))
                    {
                        r.Unchanged++;
                        iu.Status = "已是最新";
                        iu.Size = Kb(data.Length);
                        iu.Time = new FileInfo(file).LastWriteTime.ToString("MM-dd HH:mm");
                        iu.Color = Color.FromArgb(100, 100, 100);
                        bw.ReportProgress(-1, "[" + it.Name + "] 内容未变化，跳过（" + Kb(data.Length) + "）");
                    }
                    else
                    {
                        string tmp = file + ".tmp";
                        File.WriteAllBytes(tmp, data);
                        File.Copy(tmp, file, true);
                        File.Delete(tmp);

                        _hash[it.Name] = sha;
                        _time[it.Name] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                        if (exists) { r.Updated++; iu.Status = "已更新"; iu.Color = Color.FromArgb(26, 95, 180); }
                        else { r.Added++; iu.Status = "已下载"; iu.Color = Color.FromArgb(10, 125, 50); }
                        iu.Size = Kb(data.Length);
                        iu.Time = DateTime.Now.ToString("MM-dd HH:mm");
                        bw.ReportProgress(-1, "[" + it.Name + "] " + (exists ? "已更新" : "已下载") + "（" + Kb(data.Length) + "）");
                    }
                    bw.ReportProgress(-1, iu);
                }
                catch (Exception ex)
                {
                    r.Failed++;
                    iu.Status = "失败";
                    iu.Color = Color.FromArgb(192, 28, 40);
                    bw.ReportProgress(-1, iu);
                    bw.ReportProgress(-1, "[" + it.Name + "] 失败：" + ex.Message);
                }

                done++;
                bw.ReportProgress(done * 100 / total, (object)null);
            }
        }

        private void DoDiscover(BackgroundWorker bw, WorkResult r)
        {
            bw.ReportProgress(-1, "请求 GitHub 仓库目录列表…");
            string json = null;
            string[] attempts = new string[]
            {
                _api,
                "https://gh-proxy.org/" + _api,
                "https://ghfast.top/" + _api
            };
            foreach (string u in attempts)
            {
                try
                {
                    byte[] raw = Fetch(u, 15000);
                    if (raw != null && raw.Length > 0)
                    {
                        json = Encoding.UTF8.GetString(raw);
                        bw.ReportProgress(-1, "已获取仓库清单（" + u + "）");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    bw.ReportProgress(-1, "请求失败：" + ex.Message);
                }
            }

            if (json == null)
            {
                r.Error = "无法获取仓库清单，已保留当前 " + _items.Count + " 个音源。";
                return;
            }

            MatchCollection ms = Regex.Matches(json, "\"name\"\\s*:\\s*\"([^\"]+)\"[^}]*?\"type\"\\s*:\\s*\"dir\"");
            int added = 0;
            foreach (Match m in ms)
            {
                string dir = m.Groups[1].Value;
                if (dir.StartsWith(".")) continue;
                if (Find(dir) != null) continue;
                SourceItem it = new SourceItem(dir, dir + "/latest.js");
                it.IsNew = true;
                _checkedCfg[dir] = false;
                _items.Add(it);
                added++;
                bw.ReportProgress(-1, "发现新音源：" + dir);
            }
            r.Discovered = added;
            bw.ReportProgress(-1, "仓库共 " + ms.Count + " 个目录，新增 " + added + " 个。");

            if (_lxRoot != null)
            {
                int lxAdded = 0;
                List<string> names = new List<string>(_lxByName.Keys);
                foreach (string name in names)
                {
                    bool exists = false;
                    foreach (SourceItem it in _items)
                    {
                        if (string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                        if (!it.LxOnly && string.Equals(LocalScriptName(it), name, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                    }
                    if (exists) continue;
                    SourceItem li = new SourceItem(name, "");
                    li.LxOnly = true;
                    li.IsNew = true;
                    _checkedCfg[name] = false;
                    _items.Add(li);
                    lxAdded++;
                    bw.ReportProgress(-1, "仅洛雪有的音源：" + name);
                }
                if (lxAdded > 0)
                    bw.ReportProgress(-1, "从洛雪补充了 " + lxAdded + " 个音源，勾选并「检查并更新」即可导出到本地目录。");
            }
        }

        // ---------------- network ----------------

        /// <summary>
        /// 把用户填的仓库地址规范成 owner/name/branch/。接受 "owner/name"、
        /// "owner/name/dev"、完整 HTTPS 地址，甚至带 .git 后缀。
        /// </summary>
        private static string NormalizeRepo(string repo)
        {
            if (string.IsNullOrEmpty(repo)) return DefaultRepo;
            string r = repo.Trim().Replace('\\', '/');
            int at = r.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) r = r.Substring(at + "github.com/".Length);
            if (r.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) r = r.Substring(0, r.Length - 4);
            string[] parts = r.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return DefaultRepo;
            string branch = parts.Length >= 3 ? parts[2] : "main";
            return parts[0] + "/" + parts[1] + "/" + branch + "/";
        }

        private static string RepoApi(string repo)
        {
            string[] parts = NormalizeRepo(repo).Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return "https://api.github.com/repos/" + parts[0] + "/" + parts[1] + "/contents/";
        }

        /// <summary>
        /// config.ini 的 source.&lt;名字&gt;= 值。值有三种写法：
        ///   "lx/latest.js"                 → 默认仓库里的路径（老版本配置）
        ///   "gy:V2603/…/念心音源 v1.0.0.js" → 指定仓库键 + 该仓库内的路径
        /// 用冒号而不是斜杠做分隔，因为仓库里存在叫 ikun 之类的目录，会和仓库键撞名。
        /// </summary>
        private void AddConfiguredSource(string name, string value)
        {
            if (Find(name) != null) return;
            string key = DefaultRepoKey;
            string path = value;
            int colon = value.IndexOf(':');
            if (colon > 0)
            {
                string head = value.Substring(0, colon);
                if (_repos.ContainsKey(head))
                {
                    key = head;
                    path = value.Substring(colon + 1);
                }
            }
            SourceItem it = new SourceItem(name, path);
            it.RepoKey = key;
            _items.Add(it);
        }

        /// <summary>音源所属仓库的前缀，形如 owner/name/branch/。</summary>
        private string RepoPrefix(SourceItem it)
        {
            string repo;
            if (!string.IsNullOrEmpty(it.RepoKey) && _repos.TryGetValue(it.RepoKey, out repo)) return repo;
            return _repo;
        }

        /// <summary>
        /// 逐段 URL 编码：这些仓库里大量中文、空格、括号文件名，不编码的话请求直接失败。
        /// 斜杠作为路径分隔符保留。
        /// </summary>
        private static string EncodePath(string path)
        {
            string[] segments = (path ?? "").Split('/');
            for (int i = 0; i < segments.Length; i++) segments[i] = Uri.EscapeDataString(segments[i]);
            return string.Join("/", segments);
        }

        private string EnsureProxy()
        {
            if (_workingProxy != null && !_deadProxies.Contains(_workingProxy)) return _workingProxy;
            bool sawNotFound = false;
            foreach (string p in CandidateProxies(null))
            {
                try
                {
                    // 用仓库根目录的 README.md 做健康检查：它一定在，不依赖某个音源是否被作者删掉。
                    // 旧版拿 lx/latest.js 探测，作者删掉 lx 之后所有镜像都被误判成「不可用」。
                    byte[] probe = Fetch(p + _repo + "README.md", 10000);
                    if (probe != null && probe.Length > 500)
                    {
                        _workingProxy = p;
                        return p;
                    }
                }
                catch (HttpStatusException hx)
                {
                    if (hx.Status == 404 || hx.Status == 410) sawNotFound = true;
                }
                catch
                {
                }
            }
            _repoMissing = sawNotFound;
            return null;
        }

        /// <summary>
        /// 依次尝试的镜像列表：上次成功的 → 调用方指定的 → 内置列表，跳过本轮已确认失效的。
        /// </summary>
        private List<string> CandidateProxies(string preferred)
        {
            List<string> all = new List<string>();
            AddProxyCandidate(all, preferred);
            AddProxyCandidate(all, _workingProxy);
            foreach (string p in _proxies) AddProxyCandidate(all, p);

            List<string> alive = new List<string>();
            foreach (string p in all)
            {
                if (!_deadProxies.Contains(p)) alive.Add(p);
            }
            return alive;
        }

        private static void AddProxyCandidate(List<string> list, string proxy)
        {
            if (string.IsNullOrEmpty(proxy)) return;
            foreach (string existing in list)
            {
                if (string.Equals(existing, proxy, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(proxy);
        }

        private SourceFetchResult FetchSource(string proxy, SourceItem it)
        {
            SourceFetchResult result = new SourceFetchResult();
            string urlPath = EncodePath(it.Path);
            foreach (string p in CandidateProxies(proxy))
            {
                try
                {
                    byte[] d = Fetch(p + RepoPrefix(it) + urlPath, 20000);
                    if (d != null && d.Length > 0)
                    {
                        _workingProxy = p;
                        result.Data = d;
                        return result;
                    }
                }
                catch (HttpStatusException hx)
                {
                    result.Error = "HTTP " + hx.Status;
                    if (hx.Status == 404 || hx.Status == 410)
                    {
                        // 所有镜像读的是同一个仓库，这个文件不存在，换镜像也没用。
                        result.NotFound = true;
                        return result;
                    }
                    // 403 / 429 / 5xx 属于镜像自身的问题，拉黑后换下一个。
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }
                // 只有真正下载失败才拉黑，探测超时不算——探测只是优化，不是判据。
                _deadProxies.Add(p);
            }

            _workingProxy = null;
            return result;
        }

        private static byte[] Fetch(string url, int timeoutMs)
        {
            const int MaxResponseBytes = 8 * 1024 * 1024;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LxSourceTool/" + Version;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs * 2;
            req.AllowAutoRedirect = true;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            HttpWebResponse resp;
            try
            {
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException wex)
            {
                // HttpWebRequest 遇到 4xx/5xx 直接抛异常。把状态码取出来交给调用方，
                // 否则 404（上游删了音源）会被当成镜像故障，误导用户去查网络。
                HttpWebResponse err = wex.Response as HttpWebResponse;
                if (err == null) throw;
                int code = (int)err.StatusCode;
                err.Close();
                throw new HttpStatusException(code);
            }

            using (resp)
            {
                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new HttpStatusException((int)resp.StatusCode);
                using (Stream s = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        // 音源脚本只有几十 KB；这个上限只防止异常响应把内存吃光。
                        if (ms.Length > MaxResponseBytes)
                            throw new InvalidDataException("响应超过 " + (MaxResponseBytes / 1024 / 1024) + " MB，已中止");
                    }
                    return ms.ToArray();
                }
            }
        }

        private static bool LooksLikeHtml(byte[] data)
        {
            int i = 0;
            while (i < data.Length && (data[i] == 32 || data[i] == 9 || data[i] == 13 || data[i] == 10)) i++;
            if (i >= data.Length) return true;
            return data[i] == (byte)'<';
        }

        /// <summary>
        /// 判断下载到的内容是不是一个洛雪音源脚本。以前用「小于 5 KB 一律丢弃」，
        /// 但真实存在的音源里有 1.5 KB、2.7 KB 的，会被误杀；改成看 userscript 头。
        /// </summary>
        private static bool LooksLikeUserscript(byte[] data)
        {
            if (data == null || data.Length < 256) return false;
            if (LooksLikeHtml(data)) return false;
            string head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 8192));
            return head.IndexOf("@name", StringComparison.OrdinalIgnoreCase) >= 0
                || head.IndexOf("==UserScript==", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string Kb(long len)
        {
            if (len >= 1024 * 1024) return (len / 1024.0 / 1024.0).ToString("0.0") + " MB";
            return Math.Max(1, (len + 1023) / 1024) + " KB";
        }

        /// <summary>
        /// 脚本内容的规范哈希：一律按解码后的 UTF-8 文本计算。
        /// 洛雪里存的、磁盘上的 .js 都必须走这一个口径，否则同一个脚本会因为磁盘文件带
        /// BOM 而算出两个哈希，界面就会永远显示「待同步」。
        /// </summary>
        private static string HashText(string text)
        {
            return Sha256(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static string HostOf(string url)
        {
            try
            {
                Uri u = new Uri(url);
                return u.Host;
            }
            catch
            {
                return url;
            }
        }

        private SourceItem Find(string name)
        {
            foreach (SourceItem it in _items)
                if (string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase)) return it;
            return null;
        }

        // ---------------- lx music ----------------

        private static string LxDataPath()
        {
            // 允许用环境变量指向别的 user_api.json（便携版洛雪 / 自测用）
            string custom = Environment.GetEnvironmentVariable("LX_TOOL_DATA");
            if (!string.IsNullOrEmpty(custom)) return custom;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "lx-music-desktop\\LxDatas\\user_api.json");
        }

        private static bool LxPathOverridden()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LX_TOOL_DATA"));
        }

        private static bool IsLxRunning()
        {
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.ProcessName.IndexOf("lx-music-desktop", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return false;
        }

        private static string ScriptName(string text)
        {
            Match m = Regex.Match(text ?? "", "@name\\s+([^\\r\\n]+)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static string ScriptVersion(string text)
        {
            Match m = Regex.Match(text ?? "", "@version\\s+([^\\r\\n]+)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        private static string ScriptMeta(string text, string key)
        {
            Match m = Regex.Match(text ?? "", "@" + key + "\\s+([^\\r\\n]+)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static readonly Random _rnd = new Random();

        private static string NewLxId()
        {
            int n;
            lock (_rnd) { n = _rnd.Next(100, 1000); }
            long ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            return "user_api_" + n + "_" + ms;
        }

        private static string VerLabel(string v)
        {
            if (string.IsNullOrEmpty(v)) return "?";
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) return v;
            return "v" + v;
        }
        private static int[] ParseVer(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            MatchCollection ms = Regex.Matches(v, "\\d+");
            if (ms.Count == 0) return null;
            int[] r = new int[ms.Count];
            for (int i = 0; i < ms.Count; i++) int.TryParse(ms[i].Value, out r[i]);
            return r;
        }

        private static int CompareVersions(string a, string b)
        {
            int[] pa = ParseVer(a);
            int[] pb = ParseVer(b);
            if (pa == null || pb == null) return 0;
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length ? pa[i] : 0;
                int y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte x in data)
            {
                a = (a + x) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static string DecodeScript(string stored)
        {
            if (stored == null) return null;
            if (!stored.StartsWith("gz_", StringComparison.Ordinal)) return stored;
            byte[] z = Convert.FromBase64String(stored.Substring(3));
            using (MemoryStream ms = new MemoryStream(z, 2, z.Length - 2))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (MemoryStream outMs = new MemoryStream())
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = ds.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
                return Encoding.UTF8.GetString(outMs.ToArray());
            }
        }

        private static string EncodeScript(string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text);
            byte[] def;
            using (MemoryStream ms = new MemoryStream())
            {
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(raw, 0, raw.Length);
                def = ms.ToArray();
            }
            byte[] z = new byte[2 + def.Length + 4];
            z[0] = 0x78;
            z[1] = 0x9C;
            Array.Copy(def, 0, z, 2, def.Length);
            uint ad = Adler32(raw);
            z[z.Length - 4] = (byte)(ad >> 24);
            z[z.Length - 3] = (byte)(ad >> 16);
            z[z.Length - 2] = (byte)(ad >> 8);
            z[z.Length - 1] = (byte)ad;
            return "gz_" + Convert.ToBase64String(z);
        }

        private void LoadLx()
        {
            _lxPath = LxDataPath();
            _lxByName.Clear();
            _lxHash.Clear();
            _lxVersion.Clear();
            _lxText.Clear();
            _lxRoot = null;
            if (!File.Exists(_lxPath)) return;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                _lxRoot = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(_lxPath, Encoding.UTF8));
                object[] arr = _lxRoot.ContainsKey("userApis") ? _lxRoot["userApis"] as object[] : null;
                if (arr == null) return;
                foreach (object o in arr)
                {
                    Dictionary<string, object> e = o as Dictionary<string, object>;
                    if (e == null) continue;
                    string name = e.ContainsKey("name") ? Convert.ToString(e["name"]) : "";
                    if (name.Length == 0) continue;
                    _lxByName[name] = e;
                    _lxVersion[name] = e.ContainsKey("version") ? Convert.ToString(e["version"]) : "";
                    string text = null;
                    try { text = DecodeScript(e.ContainsKey("script") ? Convert.ToString(e["script"]) : null); }
                    catch { }
                    if (text != null)
                    {
                        _lxText[name] = text;
                        _lxHash[name] = Sha256(Encoding.UTF8.GetBytes(text));
                    }
                }
            }
            catch (Exception ex)
            {
                _lxRoot = null;
                Log("读取洛雪数据失败：" + ex.Message);
            }
        }

        private string LocalScriptPath(SourceItem it)
        {
            return Path.Combine(_outDir, it.Name + ".js");
        }

        private string LocalScriptName(SourceItem it)
        {
            string file = LocalScriptPath(it);
            if (File.Exists(file))
            {
                try
                {
                    string n = ScriptName(File.ReadAllText(file, Encoding.UTF8));
                    if (n.Length > 0) return n;
                }
                catch { }
            }
            return it.Name;
        }

        private void UpdateLxCell(ListViewItem lvi, SourceItem it)
        {
            if (lvi.SubItems.Count < 3) return;
            ListViewItem.ListViewSubItem cell = lvi.SubItems[2];
            if (_lxRoot == null)
            {
                cell.Text = "-";
                cell.ForeColor = Color.Gray;
                return;
            }
            string name = LocalScriptName(it);
            if (!_lxByName.ContainsKey(name))
            {
                cell.Text = it.LxOnly ? "洛雪已无此源" : "未安装";
                cell.ForeColor = Color.Gray;
                return;
            }
            string ver = _lxVersion.ContainsKey(name) ? _lxVersion[name] : "?";
            if (it.LxOnly)
            {
                cell.Text = "仅洛雪 " + VerLabel(ver);
                cell.ForeColor = Color.FromArgb(120, 90, 20);
                return;
            }
            string file = LocalScriptPath(it);
            string text = null;
            try
            {
                if (File.Exists(file)) text = File.ReadAllText(file, Encoding.UTF8);
            }
            catch
            {
            }
            bool same = text != null && _lxHash.ContainsKey(name)
                && string.Equals(HashText(text), _lxHash[name], StringComparison.OrdinalIgnoreCase);
            if (same)
            {
                cell.Text = "已装 " + VerLabel(ver);
                cell.ForeColor = Color.FromArgb(40, 110, 60);
                return;
            }
            string localVer = text == null ? "" : ScriptVersion(text);
            if (CompareVersions(localVer, ver) < 0)
            {
                cell.Text = "洛雪更新 " + VerLabel(ver);
                cell.ForeColor = Color.FromArgb(120, 120, 120);
            }
            else
            {
                cell.Text = "待同步 " + VerLabel(ver);
                cell.ForeColor = Color.FromArgb(26, 95, 180);
            }
        }

        private void SyncToLx()
        {
            if (_running) return;
            if (!File.Exists(_lxPath))
            {
                Log("未找到洛雪数据文件：" + _lxPath);
                SetStatus("未找到洛雪，先安装并在洛雪里导入一次音源。");
                return;
            }
            if (!LxPathOverridden() && IsLxRunning())
            {
                DialogResult ask = MessageBox.Show(this,
                    "洛雪正在运行。同步前请完全退出洛雪（主窗口 + 托盘图标），否则它退出时会把旧数据写回、覆盖本次同步。\n\n确定继续？",
                    AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ask != DialogResult.Yes) return;
            }

            LoadLx();
            if (_lxRoot == null)
            {
                Log("洛雪数据解析失败，已放弃同步。");
                return;
            }

            StringBuilder changed = new StringBuilder();
            int updated = 0, added = 0, same = 0, skipped = 0, older = 0;
            List<object> newEntries = new List<object>();
            object[] existing = _lxRoot.ContainsKey("userApis") ? _lxRoot["userApis"] as object[] : null;
            if (existing == null) existing = new object[0];
            foreach (SourceItem it in _items)
            {
                if (it.LxOnly) continue;
                string file = LocalScriptPath(it);
                if (!File.Exists(file)) { skipped++; continue; }
                string text;
                try { text = File.ReadAllText(file, Encoding.UTF8); }
                catch { skipped++; continue; }
                string name = ScriptName(text);
                if (name.Length == 0) name = it.Name;
                string localHash = HashText(text);
                string ver = ScriptVersion(text);

                if (!_lxByName.ContainsKey(name))
                {
                    Dictionary<string, object> created = new Dictionary<string, object>();
                    created["id"] = NewLxId();
                    created["name"] = name;
                    created["description"] = ScriptMeta(text, "description");
                    created["version"] = ver.Length > 0 ? ver : "1.0";
                    created["author"] = ScriptMeta(text, "author");
                    created["homepage"] = ScriptMeta(text, "homepage");
                    created["allowShowUpdateAlert"] = false;
                    created["script"] = EncodeScript(text);
                    newEntries.Add(created);
                    _lxByName[name] = created;
                    _lxHash[name] = localHash;
                    _lxVersion[name] = Convert.ToString(created["version"]);
                    added++;
                    changed.AppendLine("  · " + name + "（新增 " + VerLabel(Convert.ToString(created["version"])) + "）");
                    continue;
                }

                if (_lxHash.ContainsKey(name) && string.Equals(localHash, _lxHash[name], StringComparison.OrdinalIgnoreCase))
                {
                    same++;
                    continue;
                }

                string lxVer = _lxVersion.ContainsKey(name) ? _lxVersion[name] : "";
                if (CompareVersions(ver, lxVer) < 0)
                {
                    older++;
                    changed.AppendLine("  · " + name + "（本地 " + VerLabel(ver) + " < 洛雪 " + VerLabel(lxVer) + "，已跳过，避免降级）");
                    continue;
                }

                Dictionary<string, object> entry = _lxByName[name];
                entry["script"] = EncodeScript(text);
                if (ver.Length > 0) entry["version"] = ver;
                _lxHash[name] = localHash;
                if (ver.Length > 0) _lxVersion[name] = ver;
                updated++;
                changed.AppendLine("  · " + name + (ver.Length > 0 ? " → " + VerLabel(ver) : ""));
            }

            int changedCount = added + updated;
            if (changedCount == 0)
            {
                Log("没有需要同步的音源（已一致 " + same + " 个，本地缺文件 " + skipped + " 个，降级忽略 " + older + " 个）。");
                SetStatus("洛雪里的音源已经是最新，无需同步。");
                RefreshLocalState();
                return;
            }

            if (added > 0)
            {
                List<object> all = new List<object>(existing);
                all.AddRange(newEntries);
                _lxRoot["userApis"] = all.ToArray();
            }

            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string backup = _lxPath + ".bak-" + stamp;
                string tmp = _lxPath + ".tmp";
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                string json = ser.Serialize(_lxRoot);

                // 先写临时文件再替换：中途崩溃 / 断电也不会留下写了一半的 user_api.json。
                // File.Replace 会顺带生成备份；某些文件系统不支持时退回「备份 + 覆盖」。
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                bool replaced = false;
                try
                {
                    File.Replace(tmp, _lxPath, backup);
                    replaced = true;
                }
                catch
                {
                }
                if (!replaced)
                {
                    File.Copy(_lxPath, backup, true);
                    File.Copy(tmp, _lxPath, true);
                    File.Delete(tmp);
                }
                Log("已写入洛雪：新增 " + added + " 个，更新 " + updated + " 个（原文已备份为 " + Path.GetFileName(backup) + "，降级忽略 " + older + " 个）：");
                Log(changed.ToString().TrimEnd());
                Log("请重启洛雪，在「设置 → 自定义源管理」里确认音源与版本号。");
                SetStatus("已同步到洛雪（新增 " + added + " / 更新 " + updated + "），重启洛雪生效。");
            }
            catch (Exception ex)
            {
                Log("写入洛雪失败：" + ex.Message);
                SetStatus("同步失败：" + ex.Message);
            }
            LoadLx();
            RefreshLocalState();
        }

        // ---------------- config ----------------

        private static string ConfigDirCandidate()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private void LoadConfig()
        {
            _configPath = Path.Combine(ConfigDirCandidate(), "config.ini");

            _repos.Clear();
            for (int i = 0; i < DefaultRepos.GetLength(0); i++) _repos[DefaultRepos[i, 0]] = DefaultRepos[i, 1];

            if (File.Exists(_configPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(_configPath, Encoding.UTF8);
                    foreach (string line in lines)
                    {
                        if (line.TrimStart().StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k.Equals("outdir", StringComparison.OrdinalIgnoreCase)) _outDir = v;
                        else if (k.Equals("defaults", StringComparison.OrdinalIgnoreCase)) _defaultsStamp = v;
                        else if (k.Equals("repo", StringComparison.OrdinalIgnoreCase)) _repo = NormalizeRepo(v);
                        else if (k.StartsWith("repo.", StringComparison.OrdinalIgnoreCase))
                        {
                            // repo.<键>=owner/name/branch/ —— 多仓库支持
                            string key = k.Substring(5).Trim();
                            if (key.Length > 0) _repos[key] = NormalizeRepo(v);
                        }
                        else if (k.Equals("proxies", StringComparison.OrdinalIgnoreCase))
                        {
                            List<string> list = new List<string>();
                            foreach (string piece in v.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                string x = piece.Trim();
                                if (x.Length == 0) continue;
                                if (!x.EndsWith("/")) x += "/";
                                AddProxyCandidate(list, x);
                            }
                            if (list.Count > 0) _proxies = list;
                        }
                        else if (k.StartsWith("check.", StringComparison.OrdinalIgnoreCase)) _checkedCfg[k.Substring(6)] = (v == "1");
                        else if (k.StartsWith("hash.", StringComparison.OrdinalIgnoreCase)) _hash[k.Substring(5)] = v;
                        else if (k.StartsWith("time.", StringComparison.OrdinalIgnoreCase)) _time[k.Substring(5)] = v;
                        else if (k.StartsWith("source.", StringComparison.OrdinalIgnoreCase))
                        {
                            string name = k.Substring(7);
                            if (v.Length == 0) continue;
                            AddConfiguredSource(name, v);
                        }
                    }
                }
                catch
                {
                }
            }

            _api = RepoApi(_repo);
            // 默认仓库键始终指向当前生效的默认仓库（repo= 可能把它换掉了）。
            _repos[DefaultRepoKey] = _repo;

            if (_items.Count == 0)
            {
                for (int i = 0; i < DefaultSources.GetLength(0); i++)
                {
                    SourceItem it = new SourceItem(DefaultSources[i, 1], DefaultSources[i, 2]);
                    it.RepoKey = DefaultSources[i, 0];
                    _items.Add(it);
                    // 非 pdone 仓库的内置音源默认不勾选：列表里看得到，想要再勾。
                    if (!_checkedCfg.ContainsKey(it.Name)) _checkedCfg[it.Name] = (DefaultSources[i, 3] == "1");
                }
                _defaultsStamp = Version;
            }

            if (string.IsNullOrEmpty(_outDir))
            {
                _outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "lx-sources");
            }
        }

        /// <summary>
        /// 老配置升级时把这一版新增的内置音源补进列表：已存在的不动，也不会覆盖用户的勾选状态。
        /// 放在 BuildUi 之后调用，这样日志框已经存在，能告诉用户补了几个。
        /// </summary>
        private void MergeNewDefaults()
        {
            if (_items.Count == 0) return;
            if (string.Equals(_defaultsStamp, Version, StringComparison.OrdinalIgnoreCase)) return;

            int merged = 0;
            for (int i = 0; i < DefaultSources.GetLength(0); i++)
            {
                string name = DefaultSources[i, 1];
                if (Find(name) != null) continue;
                SourceItem it = new SourceItem(name, DefaultSources[i, 2]);
                it.RepoKey = DefaultSources[i, 0];
                _items.Add(it);
                _checkedCfg[name] = (DefaultSources[i, 3] == "1");
                merged++;
            }
            _defaultsStamp = Version;
            if (merged > 0) Log("已并入 " + merged + " 个新增内置音源（默认未勾选），需要时在列表里勾上。");
        }

        private void SaveConfig()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (ListViewItem l in _list.Items)
                {
                    if (l.Tag is SourceItem)
                        _checkedCfg[((SourceItem)l.Tag).Name] = l.Checked;
                }
                sb.AppendLine("# 洛雪音源下载器配置，可手动修改");
                sb.AppendLine("# repo.<键> = 上游仓库 owner/name/branch/；音源用「键:仓库内路径」指定，省略键则用默认仓库");                List<string> repoKeys = new List<string>(_repos.Keys);
                repoKeys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string key in repoKeys) sb.AppendLine("repo." + key + "=" + _repos[key]);
                sb.AppendLine("# proxies = 镜像前缀，逗号分隔（留空则用内置列表）；镜像失效时改这里");
                sb.AppendLine("proxies=" + string.Join(",", _proxies.ToArray()));
                sb.AppendLine("outdir=" + _outDir);
                sb.AppendLine("# 内置音源清单的版本标记：升级后据此并入新增音源，请勿手改");
                sb.AppendLine("defaults=" + (_defaultsStamp != null ? _defaultsStamp : Version));
                foreach (SourceItem it in _items)
                {
                    if (it.LxOnly) continue;
                    string prefix = string.Equals(it.RepoKey, DefaultRepoKey, StringComparison.OrdinalIgnoreCase)
                        ? "" : it.RepoKey + ":";
                    sb.AppendLine("source." + it.Name + "=" + prefix + it.Path);
                    if (_checkedCfg.ContainsKey(it.Name)) sb.AppendLine("check." + it.Name + "=" + (_checkedCfg[it.Name] ? "1" : "0"));
                    if (_hash.ContainsKey(it.Name)) sb.AppendLine("hash." + it.Name + "=" + _hash[it.Name]);
                    if (_time.ContainsKey(it.Name)) sb.AppendLine("time." + it.Name + "=" + _time[it.Name]);
                }
                File.WriteAllText(_configPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log("保存配置失败：" + ex.Message);
            }
        }

        // ---------------- misc ----------------

        private void Log(string msg)
        {
            // _log 在 BuildUi 之前是 null；窗口已销毁时也可能被后台线程回调到这里。
            if (_log == null || _log.IsDisposed) return;
            _log.AppendText((_log.TextLength > 0 ? "\r\n" : "") + msg);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private void SetStatus(string msg)
        {
            if (_lblStatus == null || _lblStatus.IsDisposed) return;
            _lblStatus.Text = msg;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                DialogResult r = MessageBox.Show(this, "正在下载中，确定要退出吗？", AppTitle,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            SaveConfig();
            base.OnFormClosing(e);
        }
    }
}
