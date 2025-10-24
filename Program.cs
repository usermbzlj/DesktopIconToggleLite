// 单文件入口，负责托盘逻辑、全局热键与桌面双击切换。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class App
{
    public const string AppName = "Desktop Icon Toggle Lite";
    public const string AppId = "a6.DesktopIconToggleLite";
    public static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId);
    public static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    public static readonly Version CurrentVersion = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

    public static string? ConfigLoadError;
    public static Config Cfg = Config.Load(ConfigPath);

    private static Mutex? _mtx;
    private const string MutexName = "Global\\a6.DesktopIconToggleLite";
    private static uint _uMsgToggle;
    private static uint _uMsgExit;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _mtx = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (args.Length > 0)
            {
                _uMsgToggle = RegisterWindowMessage("A6_DITL_TOGGLE");
                _uMsgExit = RegisterWindowMessage("A6_DITL_EXIT");

                IntPtr h = FindWindow(null, HiddenForm.WindowTitle);
                if (h != IntPtr.Zero)
                {
                    if (string.Equals(args[0], "toggle", StringComparison.OrdinalIgnoreCase))
                    {
                        SendMessage(h, _uMsgToggle, IntPtr.Zero, IntPtr.Zero);
                    }
                    else if (string.Equals(args[0], "exit", StringComparison.OrdinalIgnoreCase))
                    {
                        SendMessage(h, _uMsgExit, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
            return;
        }

        try
        {
            Directory.CreateDirectory(ConfigDir);
        }
        catch (Exception ex)
        {
            Log.Warn($"创建配置目录失败：{ex.Message}");
        }

        Cfg.Normalize();
        if (!File.Exists(ConfigPath))
        {
            try
            {
                Cfg.Save(ConfigPath);
            }
            catch (Exception ex)
            {
                Log.Warn($"首次保存默认配置失败：{ex.Message}");
            }
        }

        _uMsgToggle = RegisterWindowMessage("A6_DITL_TOGGLE");
        _uMsgExit = RegisterWindowMessage("A6_DITL_EXIT");

        using var ctx = new TrayContext(_uMsgToggle, _uMsgExit);
        Application.Run(ctx);

        _mtx.ReleaseMutex();
        _mtx.Dispose();
    }

    internal sealed class Config
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RunMode Mode { get; set; } = RunMode.Hotkey;
        public string Hotkey { get; set; } = "Ctrl+Alt+F1";
        public bool SuppressInFullscreen { get; set; } = true;
        public bool ShowTrayIcon { get; set; } = true;
        public bool AutoStart { get; set; } = false;
        public bool CheckUpdates { get; set; } = false;
        public int FullscreenTolerance { get; set; } = 3;

        public static Config Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var cfg = JsonSerializer.Deserialize<Config>(json, JsonOptions());
                    if (cfg != null)
                    {
                        cfg.Normalize();
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                ConfigLoadError = $"配置读取失败：{ex.Message}";
                Log.Error("读取配置时发生异常", ex);
            }

            var fallback = new Config();
            fallback.Normalize();
            return fallback;
        }

        public void Save(string path)
        {
            var json = JsonSerializer.Serialize(this, JsonOptions());
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Hotkey))
            {
                Hotkey = "Ctrl+Alt+F1";
            }
            if (FullscreenTolerance < 0 || FullscreenTolerance > 50)
            {
                FullscreenTolerance = 3;
            }
        }

        private static JsonSerializerOptions JsonOptions() => new()
        {
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true
        };
    }

    internal enum RunMode
    {
        Hotkey,
        DesktopDoubleClick
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miToggle;
        private readonly ToolStripMenuItem _miModeHotkey;
        private readonly ToolStripMenuItem _miModeDbl;
        private readonly ToolStripMenuItem _miAutoStart;
        private readonly ToolStripMenuItem _miOpenConfig;
        private readonly ToolStripMenuItem _miExit;
        private readonly ToolStripMenuItem _miUpdate;

        private readonly HiddenForm _wnd;
        private readonly Timer _hookTimer;
        private IntPtr _mouseHook = IntPtr.Zero;
        private Native.LowLevelMouseProc? _mouseProc;

        private DateTime _lastClick = DateTime.MinValue;
        private IntPtr _lastHwnd = IntPtr.Zero;
        private Native.POINT _lastPt;

        private readonly uint _msgToggle;
        private readonly uint _msgExit;
        private readonly EventHandler _hotkeyHandler;
        private readonly SynchronizationContext? _syncContext;
        private string? _latestReleaseUrl;
        private string? _latestReleaseTag;

        public TrayContext(uint msgToggle, uint msgExit)
        {
            _msgToggle = msgToggle;
            _msgExit = msgExit;
            _syncContext = SynchronizationContext.Current;

            _wnd = new HiddenForm(_msgToggle, _msgExit);
            _hotkeyHandler = (_, _) => ToggleDesktopIcons();
            _wnd.HotkeyPressed += _hotkeyHandler;
            _wnd.ToggleRequested += (_, _) => ToggleDesktopIcons();
            _wnd.ExitRequested += (_, _) => ExitApp();

            _menu = new ContextMenuStrip();
            _miToggle = new ToolStripMenuItem("立即切换图标", null, (_, __) => ToggleDesktopIcons());
            _miUpdate = new ToolStripMenuItem("检查更新", null, OnUpdateMenuClick) { Visible = false };
            _miModeHotkey = new ToolStripMenuItem("模式：热键（推荐）", null, (_, __) => { App.Cfg.Mode = RunMode.Hotkey; PersistAndRefresh(); });
            _miModeDbl = new ToolStripMenuItem("模式：桌面空白处双击", null, (_, __) => { App.Cfg.Mode = RunMode.DesktopDoubleClick; PersistAndRefresh(); });
            _miAutoStart = new ToolStripMenuItem("开机自启", null, (_, __) => { ToggleAutoStart(); });
            _miOpenConfig = new ToolStripMenuItem("打开配置文件", null, (_, __) => OpenConfig());
            _miExit = new ToolStripMenuItem("退出", null, (_, __) => ExitApp());

            _menu.Items.AddRange(new ToolStripItem[]
            {
                _miToggle,
                _miUpdate,
                new ToolStripSeparator(),
                _miModeHotkey,
                _miModeDbl,
                new ToolStripSeparator(),
                _miAutoStart,
                _miOpenConfig,
                new ToolStripSeparator(),
                _miExit
            });

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = App.Cfg.ShowTrayIcon,
                Text = AppName,
                ContextMenuStrip = _menu
            };
            _tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ToggleDesktopIcons();
                }
            };

            RegisterHotkeyOrWarn();

            _hookTimer = new Timer { Interval = 1200 };
            _hookTimer.Tick += (_, __) => UpdateMouseHookState();
            _hookTimer.Start();

            EnsureAutoStartState();
            RefreshMenuChecks();
            NotifyConfigLoadError();
            StartUpdateCheck();
        }

        private void NotifyConfigLoadError()
        {
            if (!string.IsNullOrEmpty(ConfigLoadError))
            {
                MessageBox.Show(ConfigLoadError, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ConfigLoadError = null;
            }
        }

        private void PersistAndRefresh()
        {
            PersistConfig(false);
            RefreshMenuChecks();
            UpdateMouseHookState();
        }

        private void PersistConfig(bool silent)
        {
            try
            {
                App.Cfg.Normalize();
                App.Cfg.Save(App.ConfigPath);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
                if (!silent)
                {
                    MessageBox.Show($"保存配置失败：{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void RefreshMenuChecks()
        {
            _miModeHotkey.Checked = App.Cfg.Mode == RunMode.Hotkey;
            _miModeDbl.Checked = App.Cfg.Mode == RunMode.DesktopDoubleClick;
            _miAutoStart.Checked = IsAutoStartEnabled();
            _miUpdate.Visible = true;
            if (!string.IsNullOrEmpty(_latestReleaseUrl) && !string.IsNullOrEmpty(_latestReleaseTag))
            {
                _miUpdate.Text = $"下载新版本：{_latestReleaseTag}";
            }
            else
            {
                _miUpdate.Text = App.Cfg.CheckUpdates ? "检查更新" : "启用自动检查更新";
            }
        }

        private void ToggleAutoStart()
        {
            try
            {
                if (IsAutoStartEnabled())
                {
                    DisableAutoStart();
                    App.Cfg.AutoStart = false;
                }
                else
                {
                    EnableAutoStart();
                    App.Cfg.AutoStart = true;
                }
                PersistConfig(false);
            }
            catch (Exception ex)
            {
                Log.Error("自启动配置失败", ex);
                MessageBox.Show($"自启动配置失败：{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshMenuChecks();
        }

        private void EnsureAutoStartState()
        {
            bool actual = IsAutoStartEnabled();
            if (App.Cfg.AutoStart != actual)
            {
                Log.Warn($"检测到自启动配置不一致，已将配置更新为 {actual}");
                App.Cfg.AutoStart = actual;
                PersistConfig(true);
            }
        }

        private static string RunRegPath => @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string RunRegName => AppId;
        private static string ExePath => Application.ExecutablePath;

        private static bool IsAutoStartEnabled()
        {
            using var rk = Registry.CurrentUser.OpenSubKey(RunRegPath, false);
            var val = rk?.GetValue(RunRegName) as string;
            return !string.IsNullOrEmpty(val);
        }

        private static void EnableAutoStart()
        {
            using var rk = Registry.CurrentUser.OpenSubKey(RunRegPath, true) ?? Registry.CurrentUser.CreateSubKey(RunRegPath, true);
            rk.SetValue(RunRegName, $"\"{ExePath}\"");
        }

        private static void DisableAutoStart()
        {
            using var rk = Registry.CurrentUser.OpenSubKey(RunRegPath, true);
            rk?.DeleteValue(RunRegName, false);
        }

        private void OpenConfig()
        {
            try
            {
                Directory.CreateDirectory(App.ConfigDir);
                if (!File.Exists(App.ConfigPath))
                {
                    App.Cfg.Save(App.ConfigPath);
                }
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{App.ConfigPath}\"") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Log.Error("打开配置文件失败", ex);
                MessageBox.Show($"打开配置失败：{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RegisterHotkeyOrWarn()
        {
            if (_wnd.TryRegisterHotkey(App.Cfg.Hotkey, out string? error))
            {
                Log.Info($"注册全局热键成功：{App.Cfg.Hotkey}");
                return;
            }

            Log.Warn($"注册全局热键失败：{error}");
            var dialog = MessageBox.Show($"注册全局热键失败：{App.Cfg.Hotkey}\n{error}\n是否现在选择新的组合键？", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dialog == DialogResult.Yes)
            {
                using var capture = new HotkeyCaptureForm(App.Cfg.Hotkey);
                if (capture.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(capture.ResultHotkey))
                {
                    App.Cfg.Hotkey = capture.ResultHotkey;
                    PersistConfig(false);
                    if (_wnd.TryRegisterHotkey(App.Cfg.Hotkey, out string? retryError))
                    {
                        Log.Info($"新的全局热键注册成功：{App.Cfg.Hotkey}");
                        return;
                    }
                    Log.Warn($"新的全局热键注册失败：{retryError}");
                    MessageBox.Show($"新的全局热键注册失败：{retryError}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else
            {
                MessageBox.Show("热键注册失败，暂时只能通过托盘菜单切换桌面图标。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void UpdateMouseHookState()
        {
            bool wantHook = App.Cfg.Mode == RunMode.DesktopDoubleClick;
            if (App.Cfg.SuppressInFullscreen && IsFullscreenForeground(App.Cfg.FullscreenTolerance))
            {
                wantHook = false;
            }

            if (wantHook && _mouseHook == IntPtr.Zero)
            {
                _mouseProc = MouseProc;
                _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc!, IntPtr.Zero, 0);
                if (_mouseHook == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Log.Warn($"注册鼠标钩子失败，错误码：{error}");
                }
                else
                {
                    Log.Info("低级鼠标钩子已启用");
                }
            }
            else if (!wantHook && _mouseHook != IntPtr.Zero)
            {
                ReleaseMouseHook();
            }
        }

        private void ReleaseMouseHook()
        {
            if (_mouseHook == IntPtr.Zero)
            {
                return;
            }

            for (int i = 0; i < 3; i++)
            {
                if (Native.UnhookWindowsHookEx(_mouseHook))
                {
                    _mouseHook = IntPtr.Zero;
                    _mouseProc = null;
                    Log.Info("低级鼠标钩子已释放");
                    return;
                }
                Thread.Sleep(50);
            }

            Log.Warn("低级鼠标钩子释放失败，将放弃句柄并继续退出");
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)Native.WM_LBUTTONDOWN)
            {
                var now = DateTime.UtcNow;
                int dblTime = SystemInformation.DoubleClickTime;
                var dblSize = SystemInformation.DoubleClickSize;

                Native.POINT ptNow;
                Native.GetCursorPos(out ptNow);
                IntPtr hwndNow = Native.WindowFromPoint(ptNow);

                bool withinTime = (now - _lastClick).TotalMilliseconds <= dblTime;
                int dx = Math.Abs(ptNow.X - _lastPt.X);
                int dy = Math.Abs(ptNow.Y - _lastPt.Y);
                bool withinDist = dx <= dblSize.Width && dy <= dblSize.Height;
                bool sameHwnd = hwndNow == _lastHwnd;

                if (withinTime && withinDist && sameHwnd)
                {
                    if (TestDesktopBlankHit())
                    {
                        try
                        {
                            ToggleDesktopIcons();
                        }
                        catch (Exception ex)
                        {
                            Log.Error("桌面空白双击触发切换失败", ex);
                        }
                    }
                    _lastClick = DateTime.MinValue;
                    _lastHwnd = IntPtr.Zero;
                }
                else
                {
                    _lastClick = now;
                    _lastHwnd = hwndNow;
                    _lastPt = ptNow;
                }
            }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private static bool TestDesktopBlankHit()
        {
            IntPtr lv = GetDesktopListViewFromCursor();
            if (lv == IntPtr.Zero)
            {
                lv = GetDesktopListViewEnumerate();
                if (lv == IntPtr.Zero)
                {
                    return false;
                }
            }

            Native.POINT ptScreen;
            Native.GetCursorPos(out ptScreen);
            var ptClient = ptScreen;
            Native.ScreenToClient(lv, ref ptClient);

            var hti = new Native.LVHITTESTINFO
            {
                pt = ptClient,
                flags = 0,
                iItem = -1,
                iSubItem = -1,
                iGroup = 0
            };
            Native.SendMessage(lv, Native.LVM_HITTEST, IntPtr.Zero, ref hti);
            return hti.iItem < 0;
        }

        private static IntPtr GetDesktopListViewFromCursor()
        {
            Native.POINT pt;
            Native.GetCursorPos(out pt);
            IntPtr h = Native.WindowFromPoint(pt);
            IntPtr cur = h;
            for (int i = 0; i < 10 && cur != IntPtr.Zero; i++)
            {
                var cls = GetClassName(cur);
                if (cls == "SysListView32")
                {
                    return cur;
                }
                cur = Native.GetParent(cur);
            }
            return IntPtr.Zero;
        }

        private static IntPtr GetDesktopListViewEnumerate()
        {
            IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                IntPtr workerW = IntPtr.Zero;
                while ((workerW = Native.FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null)) != IntPtr.Zero)
                {
                    defView = Native.FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (defView != IntPtr.Zero)
                    {
                        break;
                    }
                }
            }
            if (defView == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            IntPtr lv = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            return lv;
        }

        private static string GetClassName(IntPtr h)
        {
            var sb = new StringBuilder(256);
            Native.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static void ToggleDesktopIcons()
        {
            IntPtr prog = Native.FindWindow("Progman", null);
            if (prog != IntPtr.Zero)
            {
                Native.SendMessage(prog, Native.WM_COMMAND, (IntPtr)0x7402, IntPtr.Zero);
                return;
            }
            IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                IntPtr parent = Native.GetParent(defView);
                if (parent != IntPtr.Zero)
                {
                    Native.SendMessage(parent, Native.WM_COMMAND, (IntPtr)0x7402, IntPtr.Zero);
                }
            }
        }

        private void ExitApp()
        {
            try
            {
                _hookTimer.Stop();
            }
            catch
            {
            }

            try
            {
                ReleaseMouseHook();
            }
            catch (Exception ex)
            {
                Log.Warn($"释放鼠标钩子时出现异常：{ex.Message}");
            }

            try
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"托盘图标释放失败：{ex.Message}");
            }

            try
            {
                _wnd.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"隐藏窗口释放失败：{ex.Message}");
            }

            Application.ExitThread();
        }

        private void StartUpdateCheck()
        {
            if (!App.Cfg.CheckUpdates)
            {
                return;
            }
            Task.Run(async () => await CheckForUpdatesAsync());
        }

        private void StartManualUpdateCheck()
        {
            if (!App.Cfg.CheckUpdates)
            {
                App.Cfg.CheckUpdates = true;
                PersistConfig(false);
                RefreshMenuChecks();
            }
            Task.Run(async () => await CheckForUpdatesAsync(true));
        }

        private async Task CheckForUpdatesAsync(bool fromUser = false)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopIconToggleLite/1.0");
                using var resp = await client.GetAsync("https://api.github.com/repos/a6computing/DesktopIconToggleLite/releases/latest");
                resp.EnsureSuccessStatusCode();

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (!doc.RootElement.TryGetProperty("tag_name", out var tag))
                {
                    return;
                }
                string? tagName = tag.GetString();
                string? url = doc.RootElement.TryGetProperty("html_url", out var html) ? html.GetString() : null;
                if (string.IsNullOrWhiteSpace(tagName))
                {
                    return;
                }
                string cleaned = tagName.TrimStart('v', 'V');
                if (!Version.TryParse(cleaned, out var remote))
                {
                    return;
                }
                if (remote > CurrentVersion)
                {
                    Log.Info($"检测到新版本：{tagName}");
                    _latestReleaseUrl = url;
                    _syncContext?.Post(_ => ShowUpdateNotice(tagName), null);
                }
                else
                {
                    _latestReleaseUrl = null;
                    _latestReleaseTag = null;
                    _syncContext?.Post(_ =>
                    {
                        RefreshMenuChecks();
                        if (fromUser)
                        {
                            MessageBox.Show("当前已是最新版本。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }, null);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"检查更新失败：{ex.Message}");
                if (fromUser)
                {
                    _syncContext?.Post(_ => MessageBox.Show($"检查更新失败：{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning), null);
                }
            }
        }

        private void ShowUpdateNotice(string tagName)
        {
            _latestReleaseTag = tagName;
            _miUpdate.Visible = true;
            RefreshMenuChecks();
            _tray.BalloonTipTitle = AppName;
            _tray.BalloonTipText = $"发现新版本 {tagName}，请通过托盘菜单下载。";
            _tray.ShowBalloonTip(5000);
        }

        private void OnUpdateMenuClick(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_latestReleaseUrl))
            {
                StartManualUpdateCheck();
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(_latestReleaseUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("打开新版本链接失败", ex);
                MessageBox.Show($"无法打开浏览器：{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        private sealed class HotkeyCaptureForm : Form
        {
            private readonly Label _label;
            private readonly Button _btnOk;
            private readonly Button _btnCancel;
            private string _captured = string.Empty;

            public string ResultHotkey => _captured;

            public HotkeyCaptureForm(string current)
            {
                Text = "选择新的全局热键";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                MaximizeBox = false;
                MinimizeBox = false;
                Width = 420;
                Height = 180;
                KeyPreview = true;
                TopMost = true;

                _label = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 60,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = $"请按下新的热键组合，当前配置：{current}"
                };

                _btnOk = new Button
                {
                    Text = "确定",
                    Dock = DockStyle.Left,
                    DialogResult = DialogResult.OK,
                    Enabled = false,
                    Width = 200
                };
                _btnOk.Click += (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(_captured))
                    {
                        DialogResult = DialogResult.Cancel;
                    }
                };

                _btnCancel = new Button
                {
                    Text = "取消",
                    Dock = DockStyle.Right,
                    DialogResult = DialogResult.Cancel,
                    Width = 200
                };

                var panel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
                panel.Controls.Add(_btnOk);
                panel.Controls.Add(_btnCancel);

                Controls.Add(panel);
                Controls.Add(_label);

                AcceptButton = _btnOk;
                CancelButton = _btnCancel;
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                e.SuppressKeyPress = true;

                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
                {
                    return;
                }

                var parts = new List<string>();
                if (e.Control)
                {
                    parts.Add("Ctrl");
                }
                if (e.Alt)
                {
                    parts.Add("Alt");
                }
                if (e.Shift)
                {
                    parts.Add("Shift");
                }
                if ((e.Modifiers & Keys.LWin) == Keys.LWin || (e.Modifiers & Keys.RWin) == Keys.RWin)
                {
                    parts.Add("Win");
                }

                string keyName = e.KeyCode.ToString();
                parts.Add(keyName);

                _captured = string.Join("+", parts);
                _label.Text = $"已捕获：{_captured}";
                _btnOk.Enabled = true;
            }
        }
    }

    private sealed class HiddenForm : Form
    {
        public const string WindowTitle = "A6.DesktopIconToggleLite";
        public event EventHandler? HotkeyPressed;
        public event EventHandler? ToggleRequested;
        public event EventHandler? ExitRequested;

        private int _hotId = 1;
        private readonly uint _msgToggle;
        private readonly uint _msgExit;

        public HiddenForm(uint msgToggle, uint msgExit)
        {
            Text = WindowTitle;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Opacity = 0;
            Width = 0;
            Height = 0;
            _msgToggle = msgToggle;
            _msgExit = msgExit;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Hide();
        }

        public bool TryRegisterHotkey(string hk, out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                ParseHotkey(hk, out int mod, out int vk);
                Native.UnregisterHotKey(Handle, _hotId);
                if (!Native.RegisterHotKey(Handle, _hotId, mod, vk))
                {
                    int err = Marshal.GetLastWin32Error();
                    errorMessage = $"系统错误码：{err}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == _hotId)
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
            }
            else if (m.Msg == _msgToggle)
            {
                ToggleRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (m.Msg == _msgExit)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                Native.UnregisterHotKey(Handle, _hotId);
            }
            catch
            {
            }
            base.Dispose(disposing);
        }

        private static void ParseHotkey(string s, out int mod, out int vk)
        {
            mod = 0;
            vk = 0;

            var parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl":
                        mod |= Native.MOD_CONTROL;
                        continue;
                    case "alt":
                        mod |= Native.MOD_ALT;
                        continue;
                    case "shift":
                        mod |= Native.MOD_SHIFT;
                        continue;
                    case "win":
                        mod |= Native.MOD_WIN;
                        continue;
                }

                if (Enum.TryParse(p, true, out Keys key)
                    && key != Keys.ControlKey
                    && key != Keys.Menu
                    && key != Keys.ShiftKey
                    && key != Keys.LWin
                    && key != Keys.RWin)
                {
                    vk = (int)key;
                    continue;
                }

                if (p.Length == 1)
                {
                    char ch = char.ToUpperInvariant(p[0]);
                    vk = ch;
                    continue;
                }

                throw new ArgumentException($"未知键：{p}");
            }

            if (vk == 0)
            {
                throw new ArgumentException("未指定主键（如 F1）");
            }
        }
    }

    private static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_COMMAND = 0x0111;

        public const int MOD_ALT = 0x0001;
        public const int MOD_CONTROL = 0x0002;
        public const int MOD_SHIFT = 0x0004;
        public const int MOD_WIN = 0x0008;

        public const int WH_MOUSE_LL = 14;
        public const int WM_LBUTTONDOWN = 0x0201;

        public const int LVM_FIRST = 0x1000;
        public const int LVM_HITTEST = LVM_FIRST + 18;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LVHITTESTINFO
        {
            public POINT pt;
            public uint flags;
            public int iItem;
            public int iSubItem;
            public int iGroup;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT Point);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMax);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, ref LVHITTESTINFO lParam);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string lpString);
    }

    private static uint RegisterWindowMessage(string name) => Native.RegisterWindowMessage(name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    private static class NativeExt
    {
        public const uint MONITOR_DEFAULTTONEAREST = 2;
    }

    internal static bool IsFullscreenForeground(int tolerance)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return false;
        }
        if (!Native.GetWindowRect(fg, out var r))
        {
            return false;
        }

        IntPtr mon = Native.MonitorFromWindow(fg, NativeExt.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi))
        {
            return false;
        }

        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        int mw = mi.rcMonitor.Right - mi.rcMonitor.Left;
        int mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

        return Math.Abs(w - mw) <= tolerance && Math.Abs(h - mh) <= tolerance;
    }

    internal static class Log
    {
        private static readonly object SyncRoot = new();
        private static string LogPath => Path.Combine(ConfigDir, "log.txt");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception ex) => Write("ERROR", $"{message} | {ex}");

        private static void Write(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                lock (SyncRoot)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }
    }
}
