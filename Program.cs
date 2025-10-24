// Desktop Icon Toggle Lite (C# .NET 8 WinForms)
// Single-instance tray app with global hotkey + optional "desktop blank double-click".
// Author: a6 (refactor request)
// Target: Windows 10+
// Build: see csproj below.
//
// Features vs original PS script:
// - Single instance (mutex) + optional CLI signals (toggle/exit)
// - Robust hotkey parsing & (un)register
// - Optional low-level mouse hook, suspended in fullscreen
// - Two ways to detect desktop listview: by cursor hit-test + fallback enumeration (Progman/WorkerW → SHELLDLL_DefView → SysListView32)
// - Per-monitor DPI aware (PMv2)
// - Config in %APPDATA%\a6.DesktopIconToggleLite\config.json
// - AutoStart via HKCU\...\Run (no ExecutionPolicy/lnk/COM)
// - Clean disposal on exit

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class App
{
    public const string AppName = "Desktop Icon Toggle Lite";
    public const string AppId   = "a6.DesktopIconToggleLite";
    public static readonly string ConfigDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId);
    public static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public static Config Cfg = Config.Load(ConfigPath);

    // Single instance
    private static System.Threading.Mutex? _mtx;
    private const string MutexName = "Global\\a6.DesktopIconToggleLite";

    // Registered message for cross-instance signaling
    private static uint _uMsgToggle;
    private static uint _uMsgExit;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _mtx = new System.Threading.Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);
        if (!createdNew)
        {
            // second instance: try to signal toggle/exit if requested
            if (args.Length > 0)
            {
                _uMsgToggle = RegisterWindowMessage("A6_DITL_TOGGLE");
                _uMsgExit   = RegisterWindowMessage("A6_DITL_EXIT");

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
            // quit silently
            return;
        }

        // Ensure config dir exists
        try { Directory.CreateDirectory(ConfigDir); } catch { /* ignore */ }
        // Save defaults on first run
        if (!File.Exists(ConfigPath)) Cfg.Save(ConfigPath);

        _uMsgToggle = RegisterWindowMessage("A6_DITL_TOGGLE");
        _uMsgExit   = RegisterWindowMessage("A6_DITL_EXIT");

        using var ctx = new TrayContext(_uMsgToggle, _uMsgExit);
        Application.Run(ctx);

        _mtx.ReleaseMutex();
        _mtx.Dispose();
    }

    // ---------- Config ----------
    internal sealed class Config
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RunMode Mode { get; set; } = RunMode.Hotkey; // Hotkey or DesktopDoubleClick
        public string Hotkey { get; set; } = "Ctrl+Alt+F1";
        public bool   SuppressInFullscreen { get; set; } = true;
        public bool   ShowTrayIcon { get; set; } = true;
        public bool   AutoStart    { get; set; } = false;

        public static Config Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var cfg = JsonSerializer.Deserialize<Config>(json, JsonOptions());
                    if (cfg != null) return cfg;
                }
            }
            catch { /* ignore */ }
            return new Config();
        }

        public void Save(string path)
        {
            var json = JsonSerializer.Serialize(this, JsonOptions());
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    // ---------- ApplicationContext / Tray ----------
    private sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miToggle;
        private readonly ToolStripMenuItem _miModeHotkey;
        private readonly ToolStripMenuItem _miModeDbl;
        private readonly ToolStripMenuItem _miAutoStart;
        private readonly ToolStripMenuItem _miExit;
        private readonly ToolStripMenuItem _miOpenConfig;

        private readonly HiddenForm _wnd; // message sink + hotkey window
        private readonly Timer _hookTimer;
        private IntPtr _mouseHook = IntPtr.Zero;
        private Native.LowLevelMouseProc? _mouseProc;

        private DateTime _lastClick = DateTime.MinValue;
        private IntPtr   _lastHwnd  = IntPtr.Zero;
        private Native.POINT _lastPt;

        private readonly uint _msgToggle;
        private readonly uint _msgExit;

        public TrayContext(uint msgToggle, uint msgExit)
        {
            _msgToggle = msgToggle;
            _msgExit = msgExit;

            // message sink window
            _wnd = new HiddenForm(_msgToggle, _msgExit);
            _wnd.ToggleRequested += (_, _) => ToggleDesktopIcons();
            _wnd.ExitRequested   += (_, _) => ExitApp();

            // tray menu
            _menu = new ContextMenuStrip();
            _miToggle = new ToolStripMenuItem("立即切换图标", null, (_, __) => ToggleDesktopIcons());
            _miModeHotkey = new ToolStripMenuItem("模式：热键（推荐）", null, (_, __) => { App.Cfg.Mode = RunMode.Hotkey; PersistAndRefresh(); });
            _miModeDbl    = new ToolStripMenuItem("模式：桌面空白处双击", null, (_, __) => { App.Cfg.Mode = RunMode.DesktopDoubleClick; PersistAndRefresh(); });
            _miAutoStart  = new ToolStripMenuItem("开机自启", null, (_, __) => { ToggleAutoStart(); });
            _miOpenConfig = new ToolStripMenuItem("打开配置文件", null, (_, __) => OpenConfig());
            _miExit       = new ToolStripMenuItem("退出", null, (_, __) => ExitApp());

            _menu.Items.AddRange(new ToolStripItem[]
            {
                _miToggle,
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
                if (e.Button == MouseButtons.Left) ToggleDesktopIcons();
            };

            // hotkey
            RegisterHotkeyOrWarn();

            // mouse hook timer (start/stop based on mode + fullscreen)
            _hookTimer = new Timer { Interval = 1200 };
            _hookTimer.Tick += (_, __) => UpdateMouseHookState();
            _hookTimer.Start();

            RefreshMenuChecks();
            EnsureAutoStartState();
        }

        private void PersistAndRefresh()
        {
            try { App.Cfg.Save(App.ConfigPath); } catch { /* ignore */ }
            RefreshMenuChecks();
            UpdateMouseHookState();
        }

        private void RefreshMenuChecks()
        {
            _miModeHotkey.Checked = App.Cfg.Mode == RunMode.Hotkey;
            _miModeDbl.Checked    = App.Cfg.Mode == RunMode.DesktopDoubleClick;
            _miAutoStart.Checked  = IsAutoStartEnabled();
        }

        private void ToggleAutoStart()
        {
            try
            {
                if (IsAutoStartEnabled()) DisableAutoStart();
                else EnableAutoStart();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"自启动配置失败：{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshMenuChecks();
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
                if (!File.Exists(App.ConfigPath)) App.Cfg.Save(App.ConfigPath);
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{App.ConfigPath}\"") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开配置失败：{ex.Message}", AppName);
            }
        }

        private void RegisterHotkeyOrWarn()
        {
            if (!_wnd.TryRegisterHotkey(App.Cfg.Hotkey))
            {
                MessageBox.Show($"注册全局热键失败：{App.Cfg.Hotkey}\n请在配置中更换组合键后重启。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            else
            {
                _wnd.HotkeyPressed += (_, __) => ToggleDesktopIcons();
            }
        }

        private void UpdateMouseHookState()
        {
            bool wantHook = App.Cfg.Mode == RunMode.DesktopDoubleClick;
            if (App.Cfg.SuppressInFullscreen && Native.IsFullscreenForeground()) wantHook = false;

            if (wantHook && _mouseHook == IntPtr.Zero)
            {
                _mouseProc = MouseProc;
                _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc!, IntPtr.Zero, 0);
            }
            else if (!wantHook && _mouseHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
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
                        try { ToggleDesktopIcons(); } catch { /* ignore */ }
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
                // fallback to enumeration (Progman/WorkerW → SHELLDLL_DefView → SysListView32)
                lv = GetDesktopListViewEnumerate();
                if (lv == IntPtr.Zero) return false;
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
                if (cls == "SysListView32") return cur;
                cur = Native.GetParent(cur);
            }
            return IntPtr.Zero;
        }

        private static IntPtr GetDesktopListViewEnumerate()
        {
            // Find SHELLDLL_DefView under Progman or WorkerW
            IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                IntPtr workerW = IntPtr.Zero;
                while ((workerW = Native.FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null)) != IntPtr.Zero)
                {
                    defView = Native.FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (defView != IntPtr.Zero) break;
                }
            }
            if (defView == IntPtr.Zero) return IntPtr.Zero;
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
            // Primary approach: send WM_COMMAND 0x7402 to Progman (Explorer processes it)
            IntPtr prog = Native.FindWindow("Progman", null);
            if (prog != IntPtr.Zero)
            {
                Native.SendMessage(prog, Native.WM_COMMAND, (IntPtr)0x7402, IntPtr.Zero);
                return;
            }
            // Fallback: send to SHELLDLL_DefView parent
            IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                IntPtr parent = Native.GetParent(defView);
                if (parent != IntPtr.Zero)
                    Native.SendMessage(parent, Native.WM_COMMAND, (IntPtr)0x7402, IntPtr.Zero);
            }
        }

        private void ExitApp()
        {
            try
            {
                if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
            }
            catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            try { _wnd.Dispose(); } catch { }
            Application.ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }

    // ---------- Hidden window for hotkey & messages ----------
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
            Opacity = 0; // truly hidden
            Width = 0; Height = 0;
            _msgToggle = msgToggle;
            _msgExit = msgExit;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // keep hidden
            Hide();
        }

        public bool TryRegisterHotkey(string hk)
        {
            try
            {
                ParseHotkey(hk, out int mod, out int vk);
                Native.UnregisterHotKey(Handle, _hotId);
                if (!Native.RegisterHotKey(Handle, _hotId, mod, vk))
                    return false;
                return true;
            }
            catch { return false; }
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
            try { Native.UnregisterHotKey(Handle, _hotId); } catch { }
            base.Dispose(disposing);
        }

        // "Ctrl+Alt+F1" / "Ctrl+Shift+D" / "Win+Space" / "Alt+NumPad0"
        private static void ParseHotkey(string s, out int mod, out int vk)
        {
            mod = 0; vk = 0;

            foreach (var raw in s.Split('+'))
            {
                string p = raw.Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) { mod |= Native.MOD_CONTROL; continue; }
                if (p.Equals("Alt",  StringComparison.OrdinalIgnoreCase)) { mod |= Native.MOD_ALT;      continue; }
                if (p.Equals("Shift",StringComparison.OrdinalIgnoreCase)) { mod |= Native.MOD_SHIFT;    continue; }
                if (p.Equals("Win",  StringComparison.OrdinalIgnoreCase)) { mod |= Native.MOD_WIN;      continue; }

                // try Keys enum
                if (Enum.TryParse<Keys>(p, true, out var k) && k != Keys.None)
                {
                    vk = (int)k;
                    continue;
                }
                // F1..F24
                var f = System.Text.RegularExpressions.Regex.Match(p, @"^[Ff](\d{1,2})$");
                if (f.Success && int.TryParse(f.Groups[1].Value, out int n) && n >= 1 && n <= 24)
                {
                    vk = (int)Keys.F1 + (n - 1);
                    continue;
                }
                // Single letter/digit
                if (p.Length == 1)
                {
                    char ch = char.ToUpperInvariant(p[0]);
                    vk = ch; // VK for 'A'..'Z' or '0'..'9'
                    continue;
                }
                throw new ArgumentException($"未知键：{p}");
            }
            if (vk == 0) throw new ArgumentException("未指定主键（如 F1）");
        }
    }

    // ---------- Native P/Invoke ----------
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
        public struct POINT { public int X; public int Y; }

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
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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

    // ---------- Helpers ----------
    private static uint RegisterWindowMessage(string name) => Native.RegisterWindowMessage(name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    private static class NativeExt
    {
        public const uint MONITOR_DEFAULTTONEAREST = 2;
    }

    internal static bool IsFullscreenForeground()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;

        IntPtr mon = Native.MonitorFromWindow(fg, NativeExt.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;

        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        int mw = mi.rcMonitor.Right - mi.rcMonitor.Left;
        int mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

        // Allow 3px tolerance to reduce false negatives in borderless / scaling scenarios.
        return Math.Abs(w - mw) <= 3 && Math.Abs(h - mh) <= 3;
    }
}
