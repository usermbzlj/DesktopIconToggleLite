"""应用主逻辑。"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wintypes
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from enum import IntEnum, auto
from typing import Callable, Optional

from . import logging, winapi
from .config import CONFIG_DIR, CONFIG_PATH, Config
from .hotkey import Hotkey, HotkeyParseError, parse_hotkey

APP_NAME = "Desktop Icon Toggle Lite"
RUN_REG_PATH = r"Software\\Microsoft\\Windows\\CurrentVersion\\Run"
RUN_REG_VALUE = "a6.DesktopIconToggleLite"
MUTEX_NAME = "Local\\DesktopIconToggleLite.Python"
WINDOW_CLASS = "DesktopIconToggleLite.Python.MainWindow"


class MenuId(IntEnum):
    """托盘菜单项枚举。"""

    TOGGLE = winapi.IDM_TRAY_FIRST
    GUIDE = auto()
    MODE_HOTKEY = auto()
    MODE_DBL = auto()
    TOGGLE_TOAST = auto()
    AUTO_START = auto()
    CHECK_UPDATE = auto()
    RESET = auto()
    OPEN_CONFIG = auto()
    EXIT = auto()


@dataclass
class UpdateInfo:
    """保存更新结果。"""

    tag: Optional[str] = None
    url: Optional[str] = None


class DesktopDoubleClickHook:
    """桌面空白处双击钩子。"""

    def __init__(self, trigger: Callable[[], None]) -> None:
        self._trigger = trigger
        self._hook_handle: Optional[int] = None
        self._proc = winapi.HookProcType(self._hook_proc)
        self._last_time = 0.0
        self._last_point = winapi.POINT(0, 0)
        self._last_hwnd: Optional[int] = None
        self._double_time = winapi.user32.GetDoubleClickTime()
        self._double_cx = winapi.user32.GetSystemMetrics(winapi.SM_CXDOUBLECLK)
        self._double_cy = winapi.user32.GetSystemMetrics(winapi.SM_CYDOUBLECLK)

    def start(self) -> None:
        """安装钩子。"""

        if self._hook_handle:
            return
        module = winapi.get_module_handle(None)
        handle = winapi.user32.SetWindowsHookExW(winapi.WH_MOUSE_LL, self._proc, module, 0)
        if not handle:
            raise winapi.WinAPIError("SetWindowsHookExW")
        self._hook_handle = handle

    def stop(self) -> None:
        """卸载钩子。"""

        if not self._hook_handle:
            return
        winapi.user32.UnhookWindowsHookEx(self._hook_handle)
        self._hook_handle = None

    def _hook_proc(self, n_code: int, w_param: int, l_param: int) -> int:
        """钩子回调。"""

        if n_code == winapi.HC_ACTION and w_param == winapi.WM_LBUTTONDOWN:
            info = ctypes.cast(l_param, ctypes.POINTER(winapi.MSLLHOOKSTRUCT)).contents
            now = time.monotonic() * 1000.0
            hwnd = winapi.user32.WindowFromPoint(info.pt)
            if hwnd:
                if self._is_double_click(now, info.pt, hwnd) and self._is_desktop_blank(hwnd, info.pt):
                    try:
                        self._trigger()
                    except Exception as exc:  # noqa: BLE001
                        logging.error("双击钩子执行失败", repr(exc))
            self._last_time = now
            self._last_point = winapi.POINT(info.pt.x, info.pt.y)
            self._last_hwnd = hwnd
        return winapi.user32.CallNextHookEx(self._hook_handle, n_code, w_param, l_param)

    def _is_double_click(self, now: float, pt: winapi.POINT, hwnd: int) -> bool:
        """根据时间与距离判定双击。"""

        if self._last_time <= 0:
            return False
        if now - self._last_time > self._double_time:
            return False
        if not self._last_hwnd or self._last_hwnd != hwnd:
            return False
        dx = abs(pt.x - self._last_point.x)
        dy = abs(pt.y - self._last_point.y)
        return dx <= self._double_cx and dy <= self._double_cy

    def _is_desktop_blank(self, hwnd: int, pt: winapi.POINT) -> bool:
        """判断是否点击在桌面空白区域。"""

        if not hwnd:
            return False
        if winapi.is_window_class(hwnd, "SysListView32"):
            local = winapi.POINT(pt.x, pt.y)
            winapi.user32.ScreenToClient(hwnd, ctypes.byref(local))
            hit = winapi.LVHITTESTINFO()
            hit.pt = local
            res = winapi.user32.SendMessageW(hwnd, winapi.LVM_HITTEST, 0, ctypes.byref(hit))
            return res == -1
        if winapi.is_window_class(hwnd, "WorkerW") or winapi.is_window_class(hwnd, "Progman"):
            return True
        parent = winapi.user32.GetParent(hwnd)
        if parent:
            return self._is_desktop_blank(parent, pt)
        return False


class DesktopIconToggleApp:
    """应用主体。"""

    def __init__(self) -> None:
        self.cfg = Config.load()
        self.mutex_handle: Optional[int] = None
        self.hwnd: Optional[int] = None
        self.class_atom: Optional[int] = None
        self._wnd_proc = winapi.WndProcType(self._wndproc)
        self._tray_data: Optional[winapi.NOTIFYICONDATA] = None
        self._tray_visible = False
        self._hotkey_id = 1
        self._hotkey: Optional[Hotkey] = None
        self._icons_visible = self._detect_icon_visibility()
        self._hook = DesktopDoubleClickHook(self._toggle_request)
        self._update_info = UpdateInfo()
        self._update_thread: Optional[threading.Thread] = None
        self._running = threading.Event()
        self._running.set()
        self._toggle_msg = winapi.register_window_message("DesktopIconToggleLite.Python.Toggle")
        self._exit_msg = winapi.register_window_message("DesktopIconToggleLite.Python.Exit")
        logging.info("配置加载完成", json.dumps(self.cfg.to_dict(), ensure_ascii=False))

    # 入口 -----------------------------------------------------------------
    def run(self) -> int:
        """启动应用。"""

        created, handle = winapi.create_mutex(MUTEX_NAME)
        if not created:
            logging.warn("检测到已有实例，发送激活消息")
            self._notify_existing()
            return 0
        self.mutex_handle = handle

        try:
            self._register_window_class()
            self._create_window()
            self._apply_config(initial=True)
            self._message_loop()
        finally:
            if self.hwnd:
                try:
                    winapi.user32.DestroyWindow(self.hwnd)
                except winapi.WinAPIError:
                    pass
            if self.class_atom:
                try:
                    winapi.user32.UnregisterClassW(WINDOW_CLASS, winapi.get_module_handle(None))
                except winapi.WinAPIError:
                    pass
            if self.mutex_handle:
                winapi.release_mutex(self.mutex_handle)
        return 0

    # 窗口与消息 -----------------------------------------------------------
    def _register_window_class(self) -> None:
        """注册窗口类。"""

        wc = winapi.WNDCLASS()
        wc.style = winapi.CS_HREDRAW | winapi.CS_VREDRAW
        wc.lpfnWndProc = self._wnd_proc
        wc.cbClsExtra = 0
        wc.cbWndExtra = 0
        wc.hInstance = winapi.get_module_handle(None)
        wc.hIcon = winapi.user32.LoadIconW(None, winapi.make_int_resource(winapi.IDI_APPLICATION))
        wc.hCursor = winapi.user32.LoadCursorW(None, winapi.make_int_resource(winapi.IDC_ARROW))
        wc.hbrBackground = None
        wc.lpszMenuName = None
        wc.lpszClassName = WINDOW_CLASS
        atom = winapi.user32.RegisterClassExW(ctypes.byref(wc))
        if not atom:
            raise winapi.WinAPIError("RegisterClassExW")
        self.class_atom = atom

    def _create_window(self) -> None:
        """创建隐藏窗口。"""

        hwnd = winapi.user32.CreateWindowExW(
            0,
            WINDOW_CLASS,
            WINDOW_CLASS,
            winapi.WS_OVERLAPPED,
            0,
            0,
            0,
            0,
            None,
            None,
            winapi.get_module_handle(None),
            None,
        )
        if not hwnd:
            raise winapi.WinAPIError("CreateWindowExW")
        self.hwnd = hwnd

    def _message_loop(self) -> None:
        """主消息循环。"""

        msg = winapi.MSG()
        while self._running.is_set() and winapi.user32.GetMessageW(ctypes.byref(msg), None, 0, 0) != 0:
            winapi.user32.TranslateMessage(ctypes.byref(msg))
            winapi.user32.DispatchMessageW(ctypes.byref(msg))

    # 窗口过程 -------------------------------------------------------------
    def _wndproc(self, hwnd: int, msg: int, wparam: int, lparam: int) -> int:
        """窗口消息处理。"""

        if msg == winapi.WM_DESTROY:
            self._remove_tray_icon()
            self._stop_hook()
            winapi.user32.PostQuitMessage(0)
            return 0
        if msg == winapi.WM_COMMAND:
            self._handle_command(wparam & 0xFFFF)
            return 0
        if msg == winapi.WM_HOTKEY:
            self._toggle_request()
            return 0
        if msg == winapi.WM_TRAYICON:
            self._handle_tray(lparam)
            return 0
        if msg == self._toggle_msg:
            self._toggle_request()
            return 0
        if msg == self._exit_msg:
            self.quit()
            return 0
        return winapi.user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    # 托盘 ---------------------------------------------------------------
    def _ensure_tray_icon(self) -> None:
        """根据配置确保托盘存在。"""

        if not self.hwnd:
            return
        if self.cfg.show_tray_icon:
            if not self._tray_visible:
                icon = winapi.user32.LoadIconW(None, winapi.make_int_resource(winapi.IDI_APPLICATION))
                data = winapi.make_notify_icon(self.hwnd, icon, self._tray_tip())
                winapi.shell32.Shell_NotifyIconW(winapi.NIM_ADD, ctypes.byref(data))
                self._tray_data = data
                self._tray_visible = True
            else:
                self._update_tray_tip()
        elif self._tray_visible:
            self._remove_tray_icon()

    def _remove_tray_icon(self) -> None:
        """删除托盘图标。"""

        if self._tray_visible and self._tray_data is not None:
            winapi.shell32.Shell_NotifyIconW(winapi.NIM_DELETE, ctypes.byref(self._tray_data))
            self._tray_visible = False
            self._tray_data = None

    def _tray_tip(self) -> str:
        """生成托盘提示文本。"""

        state = "显示" if self._icons_visible else "隐藏"
        return f"{APP_NAME} - 图标已{state}"

    def _update_tray_tip(self) -> None:
        """更新托盘提示。"""

        if not self._tray_visible or not self._tray_data:
            return
        tip = self._tray_tip()
        ctypes.windll.msvcrt.wcscpy(self._tray_data.szTip, tip[:127])
        winapi.shell32.Shell_NotifyIconW(winapi.NIM_MODIFY, ctypes.byref(self._tray_data))

    def _handle_tray(self, lparam: int) -> None:
        """处理托盘消息。"""

        if lparam == winapi.WM_LBUTTONUP or lparam == winapi.WM_LBUTTONDBLCLK:
            self._toggle_request()
        elif lparam == winapi.WM_RBUTTONUP:
            self._show_context_menu()

    def _show_context_menu(self) -> None:
        """展示托盘菜单。"""

        if not self.hwnd:
            return
        menu = winapi.user32.CreatePopupMenu()
        try:
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.TOGGLE, "立即切换图标")
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.GUIDE, "使用小白指南")
            winapi.user32.AppendMenuW(menu, winapi.MF_SEPARATOR, 0, None)
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.MODE_HOTKEY, "模式：热键（推荐）")
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.MODE_DBL, "模式：桌面空白处双击")
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.TOGGLE_TOAST, "切换时显示提示")
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.AUTO_START, "开机自启")
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.CHECK_UPDATE, self._update_menu_text())
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.RESET, "恢复默认设置")
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.OPEN_CONFIG, "打开配置文件")
            winapi.user32.AppendMenuW(menu, winapi.MF_SEPARATOR, 0, None)
            winapi.user32.AppendMenuW(menu, winapi.MF_STRING, MenuId.EXIT, "退出")

            checked_mode = MenuId.MODE_HOTKEY if self.cfg.mode == "Hotkey" else MenuId.MODE_DBL
            winapi.user32.CheckMenuRadioItem(menu, MenuId.MODE_HOTKEY, MenuId.MODE_DBL, checked_mode, winapi.MF_BYCOMMAND)
            if self.cfg.show_toggle_toast:
                winapi.user32.CheckMenuItem(menu, MenuId.TOGGLE_TOAST, winapi.MF_CHECKED)
            if self.cfg.auto_start:
                winapi.user32.CheckMenuItem(menu, MenuId.AUTO_START, winapi.MF_CHECKED)
            if self.cfg.check_updates and not self._update_info.tag:
                winapi.user32.CheckMenuItem(menu, MenuId.CHECK_UPDATE, winapi.MF_CHECKED)

            pt = winapi.POINT()
            winapi.user32.GetCursorPos(ctypes.byref(pt))
            winapi.user32.SetForegroundWindow(self.hwnd)
            winapi.user32.TrackPopupMenu(menu, winapi.TPM_RIGHTBUTTON | winapi.TPM_RETURNCMD | winapi.TPM_BOTTOMALIGN, pt.x, pt.y, 0, self.hwnd, None)
        finally:
            winapi.user32.DestroyMenu(menu)

    def _update_menu_text(self) -> str:
        """返回更新菜单文本。"""

        if self._update_info.tag and self._update_info.url:
            return f"下载新版本：{self._update_info.tag}"
        return "启用自动检查更新" if not self.cfg.check_updates else "检查更新"

    # 命令处理 -----------------------------------------------------------
    def _handle_command(self, ident: int) -> None:
        """处理菜单命令。"""

        if ident == MenuId.TOGGLE:
            self._toggle_request()
        elif ident == MenuId.GUIDE:
            self._show_guide(True)
        elif ident == MenuId.MODE_HOTKEY:
            self.cfg.mode = "Hotkey"
            self._persist_config()
            self._apply_config()
        elif ident == MenuId.MODE_DBL:
            self.cfg.mode = "DesktopDoubleClick"
            self._persist_config()
            self._apply_config()
        elif ident == MenuId.TOGGLE_TOAST:
            self.cfg.show_toggle_toast = not self.cfg.show_toggle_toast
            self._persist_config()
        elif ident == MenuId.AUTO_START:
            self._toggle_autostart()
        elif ident == MenuId.CHECK_UPDATE:
            if self._update_info.tag and self._update_info.url:
                os.startfile(self._update_info.url)
            else:
                self.cfg.check_updates = not self.cfg.check_updates
                self._persist_config()
                if self.cfg.check_updates:
                    self._start_update_check()
        elif ident == MenuId.RESET:
            self._reset_config()
        elif ident == MenuId.OPEN_CONFIG:
            self._open_config()
        elif ident == MenuId.EXIT:
            self.quit()

    # 热键与钩子 ---------------------------------------------------------
    def _apply_config(self, *, initial: bool = False) -> None:
        """根据配置更新运行状态。"""

        self.cfg.normalize()
        self._ensure_tray_icon()
        self._register_hotkey()
        self._update_hook_state()
        if initial and self.cfg.show_first_run_guide:
            self._show_guide(False)
            self.cfg.show_first_run_guide = False
            self._persist_config()
        if self.cfg.auto_start:
            self._ensure_autostart_state()
        if self.cfg.check_updates:
            self._start_update_check()

    def _register_hotkey(self) -> None:
        """注册全局热键。"""

        if not self.hwnd:
            return
        try:
            hotkey = parse_hotkey(self.cfg.hotkey)
        except HotkeyParseError as exc:
            logging.error("热键解析失败", str(exc))
            self._show_message(f"热键解析失败：{exc}")
            return
        if self._hotkey:
            try:
                winapi.user32.UnregisterHotKey(self.hwnd, self._hotkey_id)
            except winapi.WinAPIError:
                pass
        try:
            winapi.user32.RegisterHotKey(self.hwnd, self._hotkey_id, hotkey.modifiers, hotkey.vk)
            self._hotkey = hotkey
        except winapi.WinAPIError as exc:
            logging.error("热键注册失败", repr(exc))
            self._show_message("热键注册失败，可能与系统或其他程序冲突", warning=True)

    def _update_hook_state(self) -> None:
        """根据模式启用或关闭鼠标钩子。"""

        if self.cfg.mode == "DesktopDoubleClick":
            try:
                self._hook.start()
            except winapi.WinAPIError as exc:
                logging.error("安装鼠标钩子失败", repr(exc))
                self._show_message("安装鼠标钩子失败，请尝试重新启动程序", warning=True)
        else:
            self._stop_hook()

    def _stop_hook(self) -> None:
        """关闭鼠标钩子。"""

        try:
            self._hook.stop()
        except winapi.WinAPIError as exc:
            logging.warn("卸载鼠标钩子失败", repr(exc))

    # 功能逻辑 -----------------------------------------------------------
    def _toggle_request(self) -> None:
        """执行桌面图标切换。"""

        if self.cfg.mode == "DesktopDoubleClick" and self.cfg.suppress_in_fullscreen and self._is_fullscreen_foreground():
            logging.info("当前为全屏窗口，忽略切换")
            return
        try:
            winapi.toggle_desktop_icons()
            time.sleep(0.05)
            self._icons_visible = self._detect_icon_visibility()
            self._update_tray_tip()
            if self.cfg.show_toggle_toast:
                self._show_balloon("已切换桌面图标")
        except winapi.WinAPIError as exc:
            logging.error("切换桌面图标失败", repr(exc))
            self._show_message("切换桌面图标失败，请检查 Explorer 是否正常运行", warning=True)

    def _show_balloon(self, text: str) -> None:
        """显示气泡提示。"""

        if not self._tray_visible or not self._tray_data:
            return
        data = self._tray_data
        data.uFlags |= winapi.NIF_INFO
        ctypes.windll.msvcrt.wcscpy(data.szInfo, text[:255])
        ctypes.windll.msvcrt.wcscpy(data.szInfoTitle, APP_NAME[:63])
        data.dwInfoFlags = winapi.MB_ICONINFORMATION
        winapi.shell32.Shell_NotifyIconW(winapi.NIM_MODIFY, ctypes.byref(data))
        data.uFlags &= ~winapi.NIF_INFO

    def _detect_icon_visibility(self) -> bool:
        """检测桌面图标是否可见。"""

        listview = self._find_desktop_listview()
        if not listview:
            return True
        try:
            return bool(winapi.user32.IsWindowVisible(listview))
        except winapi.WinAPIError:
            return True

    def _find_desktop_listview(self) -> Optional[int]:
        """查找桌面 ListView。"""

        progman = winapi.user32.FindWindowW("Progman", None)
        shellview = winapi.user32.FindWindowExW(progman, None, "SHELLDLL_DefView", None) if progman else None
        if shellview:
            listview = winapi.user32.FindWindowExW(shellview, None, "SysListView32", None)
            if listview:
                return listview
        worker = winapi.user32.FindWindowW("WorkerW", None)
        while worker:
            shellview = winapi.user32.FindWindowExW(worker, None, "SHELLDLL_DefView", None)
            if shellview:
                listview = winapi.user32.FindWindowExW(shellview, None, "SysListView32", None)
                if listview:
                    return listview
            worker = winapi.user32.FindWindowExW(None, worker, "WorkerW", None)
        return None

    def _is_fullscreen_foreground(self) -> bool:
        """判断当前是否存在全屏前台窗口。"""

        tolerance = int(self.cfg.fullscreen_tolerance)
        hwnd = winapi.user32.GetForegroundWindow()
        if not hwnd:
            return False
        rect = winapi.RECT()
        try:
            winapi.user32.GetWindowRect(hwnd, ctypes.byref(rect))
        except winapi.WinAPIError:
            return False
        monitor = winapi.user32.MonitorFromWindow(hwnd, winapi.MONITOR_DEFAULTTONEAREST)
        if not monitor:
            return False
        info = winapi.MONITORINFO()
        info.cbSize = ctypes.sizeof(info)
        try:
            winapi.user32.GetMonitorInfoW(monitor, ctypes.byref(info))
        except winapi.WinAPIError:
            return False
        width = rect.right - rect.left
        height = rect.bottom - rect.top
        mwidth = info.rcMonitor.right - info.rcMonitor.left
        mheight = info.rcMonitor.bottom - info.rcMonitor.top
        return abs(width - mwidth) <= tolerance and abs(height - mheight) <= tolerance

    def _persist_config(self) -> None:
        """保存配置并更新托盘提示。"""

        try:
            self.cfg.save(CONFIG_PATH)
            logging.info("配置已保存")
        except Exception as exc:  # noqa: BLE001
            logging.error("保存配置失败", repr(exc))
            self._show_message(f"保存配置失败：{exc}", warning=True)
        self._ensure_tray_icon()

    def _reset_config(self) -> None:
        """恢复默认设置。"""

        result = winapi.user32.MessageBoxW(self.hwnd, "将恢复默认设置，是否继续？", APP_NAME, winapi.MB_YESNO | winapi.MB_ICONWARNING)
        if result != 6:  # IDYES
            return
        self.cfg = Config()
        self.cfg.normalize()
        self._persist_config()
        self._apply_config()
        self._show_message("已恢复默认设置。")

    def _open_config(self) -> None:
        """用记事本打开配置。"""

        CONFIG_DIR.mkdir(parents=True, exist_ok=True)
        if not CONFIG_PATH.exists():
            self.cfg.save(CONFIG_PATH)
        os.startfile(str(CONFIG_PATH))

    def _toggle_autostart(self) -> None:
        """开关自启动。"""

        desired = not self.cfg.auto_start
        try:
            if desired:
                self._enable_autostart()
            else:
                self._disable_autostart()
            self.cfg.auto_start = desired
            self._persist_config()
        except Exception as exc:  # noqa: BLE001
            logging.error("自启动配置失败", repr(exc))
            self._show_message(f"自启动配置失败：{exc}", warning=True)


    def _start_update_check(self) -> None:
        """启动更新检查线程。"""

        if self._update_thread and self._update_thread.is_alive():
            return
        def worker() -> None:
            try:
                self._check_updates()
            except Exception as exc:  # noqa: BLE001
                logging.warn("检查更新失败", repr(exc))
        self._update_thread = threading.Thread(target=worker, name="UpdateCheck", daemon=True)
        self._update_thread.start()

    def _check_updates(self) -> None:
        """访问 GitHub Releases 获取最新版本。"""

        api = "https://api.github.com/repos/a632079/DesktopIconToggleLite/releases/latest"
        request = urllib.request.Request(api, headers={"User-Agent": "DesktopIconToggleLitePython"})
        try:
            with urllib.request.urlopen(request, timeout=5) as resp:
                data = json.loads(resp.read().decode("utf-8"))
        except urllib.error.URLError as exc:
            logging.warn("检查更新请求失败", repr(exc))
            return
        except Exception as exc:  # noqa: BLE001
            logging.warn("解析更新信息失败", repr(exc))
            return
        tag = data.get("tag_name") if isinstance(data, dict) else None
        url = data.get("html_url") if isinstance(data, dict) else None
        if tag and url:
            if self._update_info.tag != tag:
                self._update_info = UpdateInfo(tag=tag, url=url)
                logging.info("发现新版本", tag)
                if self.cfg.show_toggle_toast:
                    self._show_balloon(f"发现新版本 {tag}")
        else:
            logging.info("未获取到有效的版本信息")
    def _ensure_autostart_state(self) -> None:
        """校验注册表中的自启动状态。"""

        actual = self._is_autostart_enabled()
        if actual != self.cfg.auto_start:
            logging.warn(f"检测到自启动状态不一致，已同步为 {actual}")
            self.cfg.auto_start = actual
            self._persist_config()

    def _enable_autostart(self) -> None:
        """写入注册表开机启动。"""

        exe = sys.executable
        hkey = wintypes.HKEY()
        res = winapi.advapi32.RegCreateKeyExW(
            winapi.HKEY_CURRENT_USER,
            RUN_REG_PATH,
            0,
            None,
            0,
            winapi.KEY_SET_VALUE | winapi.KEY_WOW64_64KEY,
            None,
            ctypes.byref(hkey),
            None,
        )
        if res != 0:
            raise winapi.WinAPIError("RegCreateKeyExW")
        try:
            command = f'"{exe}"'
            buffer = ctypes.create_unicode_buffer(command)
            size = ctypes.sizeof(buffer)
            res = winapi.advapi32.RegSetValueExW(
                hkey,
                RUN_REG_VALUE,
                0,
                winapi.REG_SZ,
                buffer,
                size,
            )
            if res != 0:
                raise winapi.WinAPIError("RegSetValueExW")
        finally:
            winapi.advapi32.RegCloseKey(hkey)

    def _disable_autostart(self) -> None:
        """删除自启动注册表。"""

        hkey = wintypes.HKEY()
        res = winapi.advapi32.RegOpenKeyExW(
            winapi.HKEY_CURRENT_USER,
            RUN_REG_PATH,
            0,
            winapi.KEY_SET_VALUE | winapi.KEY_WOW64_64KEY,
            ctypes.byref(hkey),
        )
        if res != 0:
            return
        try:
            winapi.advapi32.RegDeleteValueW(hkey, RUN_REG_VALUE)
        finally:
            winapi.advapi32.RegCloseKey(hkey)

    def _is_autostart_enabled(self) -> bool:
        """读取注册表判断自启动状态。"""

        hkey = wintypes.HKEY()
        res = winapi.advapi32.RegOpenKeyExW(
            winapi.HKEY_CURRENT_USER,
            RUN_REG_PATH,
            0,
            winapi.KEY_QUERY_VALUE | winapi.KEY_WOW64_64KEY,
            ctypes.byref(hkey),
        )
        if res != 0:
            return False
        try:
            buffer = ctypes.create_unicode_buffer(1024)
            size = wintypes.DWORD(ctypes.sizeof(buffer))
            res = winapi.advapi32.RegQueryValueExW(
                hkey,
                RUN_REG_VALUE,
                None,
                None,
                buffer,
                ctypes.byref(size),
            )
            if res != 0:
                return False
            return bool(buffer.value)
        finally:
            winapi.advapi32.RegCloseKey(hkey)

    # 辅助 ---------------------------------------------------------------
    def _show_message(self, text: str, *, warning: bool = False) -> None:
        """弹出消息框。"""

        flags = winapi.MB_ICONWARNING if warning else winapi.MB_ICONINFORMATION
        winapi.user32.MessageBoxW(self.hwnd, text, APP_NAME, winapi.MB_OK | flags)

    def _show_guide(self, force: bool) -> None:
        """展示新手指南。"""

        message = (
            "欢迎使用 Desktop Icon Toggle Lite\n\n"
            "• 默认热键：Ctrl+Alt+F1\n"
            "• 可在托盘菜单修改模式/设置\n"
            "• 支持桌面空白处双击（需启用）"
        )
        winapi.user32.MessageBoxW(self.hwnd, message, APP_NAME, winapi.MB_OK | winapi.MB_ICONINFORMATION)
        if force:
            self.cfg.show_first_run_guide = False
            self._persist_config()

    def _notify_existing(self) -> None:
        """向已运行实例发送显示请求。"""

        hwnd = winapi.user32.FindWindowW(WINDOW_CLASS, None)
        if hwnd:
            winapi.user32.PostMessageW(hwnd, self._toggle_msg, 0, 0)

    def quit(self) -> None:
        """退出应用。"""

        self._running.clear()
        if self.hwnd:
            winapi.user32.DestroyWindow(self.hwnd)


def send_command(cmd: str) -> bool:
    """向运行中的实例发送命令。"""

    hwnd = winapi.user32.FindWindowW(WINDOW_CLASS, None)
    if not hwnd:
        return False
    if cmd == "toggle":
        winapi.user32.PostMessageW(hwnd, winapi.WM_USER_TOGGLE, 0, 0)
        return True
    if cmd == "exit":
        msg = winapi.register_window_message("DesktopIconToggleLite.Python.Exit")
        winapi.user32.PostMessageW(hwnd, msg, 0, 0)
        return True
    return False


def main(argv: list[str] | None = None) -> int:
    """命令行入口。"""

    argv = list(sys.argv[1:] if argv is None else argv)
    if argv:
        if argv[0] in {"toggle", "exit"}:
            if send_command(argv[0]):
                return 0
            print("未检测到正在运行的实例。")
            return 1
    app = DesktopIconToggleApp()
    return app.run()


if __name__ == "__main__":
    sys.exit(main())
