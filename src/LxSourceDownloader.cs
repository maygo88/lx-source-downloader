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
        public bool LxOnly;
        public bool IsNew;

        public SourceItem(string name, string path)
        {
            Name = name;
            Path = path;
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

            MainForm f = new MainForm();
            if (auto)
            {
                f.AutoMode = true;
                f.WindowState = FormWindowState.Minimized;
                f.ShowInTaskbar = false;
            }
            Application.Run(f);
        }

        private static void CrashLog(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (ex == null ? "(no exception object)" : ex.ToString()) + Environment.NewLine,
                    new UTF8Encoding(true));
            }
            catch
            {
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private const string Repo = "pdone/lx-music-source/main/";
        private const string Api = "https://api.github.com/repos/pdone/lx-music-source/contents/";
        private const string AppTitle = "洛雪音源下载器 / LX Source Downloader";
        private static readonly string Version = typeof(Program).Assembly.GetName().Version.ToString(3);

        private static readonly string[] Proxies = new string[]
        {
            "https://gh-proxy.org/raw.githubusercontent.com/",
            "https://ghproxy.net/raw.githubusercontent.com/",
            "https://gh.llkk.cc/raw.githubusercontent.com/",
            "https://github.moeyy.xyz/raw.githubusercontent.com/",
            "https://raw.githubusercontent.com/"
        };

        private static readonly string[,] DefaultSources = new string[,]
        {
            { "lx", "lx/latest.js" },
            { "xinghai", "xinghai/latest.js" },
            { "qingliang", "qingliang/latest.js" },
            { "suyin", "suyin/latest.js" },
            { "huanyin", "huanyin/latest.js" },
            { "changqing", "changqing/latest.js" },
            { "fish", "fish/latest.js" },
            { "xinglan", "xinglan/latest.js" }
        };

        private readonly List<SourceItem> _items = new List<SourceItem>();
        private readonly Dictionary<string, string> _hash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _time = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _outDir;
        private string _configPath;
        private string _workingProxy;
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

        public MainForm()
        {
            Text = AppTitle + " v" + Version;
            ClientSize = new Size(920, 660);
            MinimumSize = new Size(820, 560);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point); }
            catch { }

            LoadConfig();
            LoadLx();
            BuildUi();
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

            Shown += delegate { if (AutoMode) BeginInvoke(new Action(StartUpdate)); };
            Resize += delegate { LayoutPanels(); };
            LayoutPanels();
        }

        private void LayoutPanels()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            _top.Bounds = new Rectangle(0, 0, w, 58);
            _bottom.Bounds = new Rectangle(0, h - 96, w, 96);
            _mid.Bounds = new Rectangle(0, 58, w, h - 58 - 96);

            int right = w - 12;
            _btnOpen.Left = right - _btnOpen.Width;
            _btnBrowse.Left = _btnOpen.Left - 8 - _btnBrowse.Width;
            _txtOut.Left = 78;
            _txtOut.Width = Math.Max(120, _btnBrowse.Left - 12 - _txtOut.Left);

            if (_mid.Width > 0 && _mid.Height > 0)
            {
                _split.Bounds = _mid.ClientRectangle;
                int d = (int)(_mid.Height * 0.58);
                if (d > 180 && d < _mid.Height - 120) _split.SplitterDistance = d;
            }
            _bar.Width = Math.Max(200, w - 24);
            _lblStatus.Width = Math.Max(200, w - 24);

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
                    lvi.SubItems[1].Text = "未下载";
                    lvi.ForeColor = Color.Black;
                }
                UpdateLxCell(lvi, it);
            }
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
                return;
            }
            RunWorker(false, names, null);
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

                if (AutoMode)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "auto.log"),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                            _lblStatus.Text + Environment.NewLine + "outdir=" + _outDir + Environment.NewLine,
                            new UTF8Encoding(true));
                    }
                    catch
                    {
                    }
                    BeginInvoke(new Action(Close));
                }
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
                bw.ReportProgress(-1, "正在探测可用加速站…");
                proxy = EnsureProxy(bw);
                if (proxy == null)
                {
                    r.Error = "所有加速站都不可用，请检查网络。";
                    bw.ReportProgress(-1, "所有加速站都不可用。");
                    return;
                }
                bw.ReportProgress(-1, "使用加速站：" + HostOf(proxy));
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
                            File.WriteAllText(LocalScriptPath(it), text, new UTF8Encoding(true));
                            _hash[it.Name] = Sha256(Encoding.UTF8.GetBytes(text));
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
                    byte[] data = FetchWithFallback(proxy, it.Path, bw);
                    proxy = _workingProxy;

                    if (data == null)
                    {
                        r.Failed++;
                        iu.Status = "失败";
                        iu.Size = "-";
                        iu.Time = "-";
                        iu.Color = Color.FromArgb(192, 28, 40);
                        bw.ReportProgress(-1, "[" + it.Name + "] 失败：没有任何加速站可用");
                        bw.ReportProgress(-1, iu);
                        done++;
                        bw.ReportProgress(done * 100 / total, (object)null);
                        continue;
                    }

                    if (data.Length < 5000 || LooksLikeHtml(data))
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
                Api,
                "https://gh-proxy.org/https://api.github.com/repos/pdone/lx-music-source/contents/"
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

        private string EnsureProxy(BackgroundWorker bw)
        {
            if (_workingProxy != null) return _workingProxy;
            foreach (string p in Proxies)
            {
                try
                {
                    byte[] probe = Fetch(p + Repo + "lx/latest.js", 6000);
                    if (probe != null && probe.Length > 5000)
                    {
                        _workingProxy = p;
                        return p;
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private byte[] FetchWithFallback(string proxy, string path, BackgroundWorker bw)
        {
            string[] order = new string[] { proxy, _workingProxy, null };
            foreach (string p in order)
            {
                if (p == null) continue;
                try
                {
                    byte[] d = Fetch(p + Repo + path, 20000);
                    if (d != null && d.Length > 0)
                    {
                        _workingProxy = p;
                        return d;
                    }
                }
                catch
                {
                }
            }

            _workingProxy = null;
            string again = EnsureProxy(bw);
            if (again == null) return null;
            try
            {
                return Fetch(again + Repo + path, 20000);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Fetch(string url, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LxSourceTool/" + Version;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs * 2;
            req.AllowAutoRedirect = true;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new WebException("HTTP " + (int)resp.StatusCode);
                using (Stream s = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
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
            bool same = false;
            if (File.Exists(file) && _lxHash.ContainsKey(name))
            {
                try { same = string.Equals(Sha256(File.ReadAllBytes(file)), _lxHash[name], StringComparison.OrdinalIgnoreCase); }
                catch { }
            }
            if (same)
            {
                cell.Text = "已装 " + VerLabel(ver);
                cell.ForeColor = Color.FromArgb(40, 110, 60);
                return;
            }
            string localVer = "";
            try { localVer = ScriptVersion(File.ReadAllText(file, Encoding.UTF8)); }
            catch { }
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
            int n = 0, same = 0, skipped = 0, older = 0;
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
                if (!_lxByName.ContainsKey(name)) { skipped++; continue; }

                string localHash = Sha256(Encoding.UTF8.GetBytes(text));
                if (_lxHash.ContainsKey(name) && string.Equals(localHash, _lxHash[name], StringComparison.OrdinalIgnoreCase))
                {
                    same++;
                    continue;
                }

                string ver = ScriptVersion(text);
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
                n++;
                changed.AppendLine("  · " + name + (ver.Length > 0 ? " → " + VerLabel(ver) : ""));
            }

            if (n == 0)
            {
                Log("没有需要同步的音源（已一致 " + same + " 个，跳过 " + skipped + " 个，需降级而忽略 " + older + " 个）。");
                SetStatus("洛雪里的音源已经是最新，无需同步。");
                RefreshLocalState();
                return;
            }

            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(_lxPath, _lxPath + ".bak-" + stamp, true);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                File.WriteAllText(_lxPath, ser.Serialize(_lxRoot), new UTF8Encoding(false));
                Log("已写入 " + n + " 个音源到洛雪（原文已备份为 user_api.json.bak-" + stamp + "，downgrade-skipped=" + older + "）：");
                Log(changed.ToString().TrimEnd());
                Log("请重启洛雪，在「设置 → 自定义源管理」里确认版本号已更新。");
                SetStatus("已同步 " + n + " 个到洛雪，重启洛雪生效。");
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

            if (File.Exists(_configPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(_configPath, Encoding.UTF8);
                    foreach (string line in lines)
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k.Equals("outdir", StringComparison.OrdinalIgnoreCase)) _outDir = v;
                        else if (k.StartsWith("check.", StringComparison.OrdinalIgnoreCase)) _checkedCfg[k.Substring(6)] = (v == "1");
                        else if (k.StartsWith("hash.", StringComparison.OrdinalIgnoreCase)) _hash[k.Substring(5)] = v;
                        else if (k.StartsWith("time.", StringComparison.OrdinalIgnoreCase)) _time[k.Substring(5)] = v;
                        else if (k.StartsWith("source.", StringComparison.OrdinalIgnoreCase))
                        {
                            string name = k.Substring(7);
                            if (v.Length == 0) continue;
                            if (Find(name) == null) _items.Add(new SourceItem(name, v));
                        }
                    }
                }
                catch
                {
                }
            }

            if (_items.Count == 0)
            {
                for (int i = 0; i < DefaultSources.GetLength(0); i++)
                    _items.Add(new SourceItem(DefaultSources[i, 0], DefaultSources[i, 1]));
            }

            if (string.IsNullOrEmpty(_outDir))
            {
                _outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "lx-sources");
            }
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
                sb.AppendLine("outdir=" + _outDir);
                foreach (SourceItem it in _items)
                {
                    if (it.LxOnly) continue;
                    sb.AppendLine("source." + it.Name + "=" + it.Path);
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
            if (_log.IsDisposed) return;
            _log.AppendText((_log.TextLength > 0 ? "\r\n" : "") + msg);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private void SetStatus(string msg)
        {
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
