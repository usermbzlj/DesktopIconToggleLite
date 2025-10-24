"""Win32 API 封装。"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wintypes
from contextlib import contextmanager
from typing import Callable

if not hasattr(ctypes, "WinDLL"):
    raise RuntimeError("仅支持在 Windows 平台运行")

# 兼容不同 Python 版本中缺失的 Win32 类型定义
def _ptr_sized_unsigned() -> type:
    """根据指针位数返回无符号整数类型。"""

    return ctypes.c_ulonglong if ctypes.sizeof(ctypes.c_void_p) == 8 else ctypes.c_ulong


def _ptr_sized_signed() -> type:
    """根据指针位数返回有符号整数类型。"""

    return ctypes.c_longlong if ctypes.sizeof(ctypes.c_void_p) == 8 else ctypes.c_long


_FALLBACK_WIN_TYPES: dict[str, type] = {
    "HCURSOR": wintypes.HANDLE,
    "HBRUSH": wintypes.HANDLE,
    "HICON": wintypes.HANDLE,
    "HMENU": wintypes.HANDLE,
    "HHOOK": wintypes.HANDLE,
    "HMONITOR": wintypes.HANDLE,
    "UINT_PTR": _ptr_sized_unsigned(),
    "ULONG_PTR": _ptr_sized_unsigned(),
    "DWORD_PTR": _ptr_sized_unsigned(),
    "WPARAM": _ptr_sized_unsigned(),
    "LONG_PTR": _ptr_sized_signed(),
    "INT_PTR": _ptr_sized_signed(),
    "LRESULT": _ptr_sized_signed(),
    "REGSAM": wintypes.DWORD,
    "LPSECURITY_ATTRIBUTES": ctypes.c_void_p,
}

for _name, _ctype in _FALLBACK_WIN_TYPES.items():
    if not hasattr(wintypes, _name):
        setattr(wintypes, _name, _ctype)

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
advapi32 = ctypes.WinDLL("Advapi32", use_last_error=True)


class WinAPIError(RuntimeError):
    """封装 Win32 错误。"""

    def __init__(self, func: str) -> None:
        code = ctypes.get_last_error()
        super().__init__(f"{func} failed with code {code}")
        self.code = code


# 常量定义
WM_CREATE = 0x0001
WM_DESTROY = 0x0002
WM_COMMAND = 0x0111
WM_HOTKEY = 0x0312
WM_USER = 0x0400
WM_APP = 0x8000
WM_LBUTTONUP = 0x0202
WM_LBUTTONDBLCLK = 0x0203
WM_RBUTTONUP = 0x0205
WM_MOUSEMOVE = 0x0200
WM_QUIT = 0x0012

WM_TRAYICON = WM_APP + 1
WM_USER_TOGGLE = WM_APP + 2

NIF_MESSAGE = 0x00000001
NIF_ICON = 0x00000002
NIF_TIP = 0x00000004
NIF_INFO = 0x00000010
NIM_ADD = 0x00000000
NIM_MODIFY = 0x00000001
NIM_DELETE = 0x00000002

IDM_TRAY_FIRST = 1000

CS_HREDRAW = 0x0002
CS_VREDRAW = 0x0001
CW_USEDEFAULT = 0x80000000
WS_OVERLAPPED = 0x00000000
WS_SYSMENU = 0x00080000
WS_CAPTION = 0x00C00000

MOD_ALT = 0x0001
MOD_CONTROL = 0x0002
MOD_SHIFT = 0x0004
MOD_WIN = 0x0008

WH_MOUSE_LL = 14
HC_ACTION = 0

SM_CXDOUBLECLK = 36
MF_STRING = 0x00000000
MF_SEPARATOR = 0x00000800
MF_CHECKED = 0x00000008
MF_UNCHECKED = 0x00000000
MF_DISABLED = 0x00000002
MF_GRAYED = 0x00000001
MF_BYCOMMAND = 0x00000000
MF_BYPOSITION = 0x00000400
MF_RADIOCHECK = 0x00000200
TPM_RIGHTBUTTON = 0x0002
TPM_BOTTOMALIGN = 0x0020
TPM_RETURNCMD = 0x0100
SM_CYDOUBLECLK = 37

IDI_APPLICATION = 32512
IDC_ARROW = 32512

MB_ICONWARNING = 0x00000030
MB_ICONINFORMATION = 0x00000040
MB_OK = 0x00000000
MB_OKCANCEL = 0x00000001
MB_YESNO = 0x00000004

MONITOR_DEFAULTTONEAREST = 2

WM_LBUTTONDOWN = 0x0201
LVM_HITTEST = 0x1000 + 18

# 结构体定义


WndProcType = ctypes.WINFUNCTYPE(ctypes.c_long, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
HookProcType = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)


class WNDCLASS(ctypes.Structure):
    """窗口类结构。"""

    _fields_ = [
        ("style", wintypes.UINT),
        ("lpfnWndProc", WndProcType),
        ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int),
        ("hInstance", wintypes.HINSTANCE),
        ("hIcon", wintypes.HICON),
        ("hCursor", wintypes.HCURSOR),
        ("hbrBackground", wintypes.HBRUSH),
        ("lpszMenuName", wintypes.LPCWSTR),
        ("lpszClassName", wintypes.LPCWSTR),
    ]


class NOTIFYICONDATA(ctypes.Structure):
    """托盘图标结构。"""

    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("hWnd", wintypes.HWND),
        ("uID", wintypes.UINT),
        ("uFlags", wintypes.UINT),
        ("uCallbackMessage", wintypes.UINT),
        ("hIcon", wintypes.HICON),
        ("szTip", wintypes.WCHAR * 128),
        ("dwState", wintypes.DWORD),
        ("dwStateMask", wintypes.DWORD),
        ("szInfo", wintypes.WCHAR * 256),
        ("uTimeoutOrVersion", wintypes.UINT),
        ("szInfoTitle", wintypes.WCHAR * 64),
        ("dwInfoFlags", wintypes.DWORD),
        ("guidItem", ctypes.c_byte * 16),
        ("hBalloonIcon", wintypes.HICON),
    ]


class POINT(ctypes.Structure):
    """点坐标结构。"""

    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class MSLLHOOKSTRUCT(ctypes.Structure):
    """低级鼠标钩子结构。"""

    _fields_ = [
        ("pt", POINT),
        ("mouseData", wintypes.DWORD),
        ("flags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", wintypes.ULONG_PTR),
    ]


class MSG(ctypes.Structure):
    """消息结构。"""

    _fields_ = [
        ("hwnd", wintypes.HWND),
        ("message", wintypes.UINT),
        ("wParam", wintypes.WPARAM),
        ("lParam", wintypes.LPARAM),
        ("time", wintypes.DWORD),
        ("pt", POINT),
    ]


class RECT(ctypes.Structure):
    """矩形结构。"""

    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long), ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


# 兼容缺失的 RECT 指针别名
if not hasattr(wintypes, "LPRECT"):
    wintypes.LPRECT = ctypes.POINTER(RECT)

if not hasattr(wintypes, "LPCRECT"):
    wintypes.LPCRECT = ctypes.POINTER(RECT)


class MONITORINFO(ctypes.Structure):
    """显示器信息结构。"""

    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("rcMonitor", RECT),
        ("rcWork", RECT),
        ("dwFlags", wintypes.DWORD),
    ]


class LVHITTESTINFO(ctypes.Structure):
    """ListView 命中测试结构。"""

    _fields_ = [
        ("pt", POINT),
        ("flags", wintypes.UINT),
        ("iItem", ctypes.c_int),
        ("iSubItem", ctypes.c_int),
        ("iGroup", ctypes.c_int),
    ]


# 注册表相关常量
HKEY_CURRENT_USER = wintypes.HKEY(0x80000001)
KEY_QUERY_VALUE = 0x0001
KEY_SET_VALUE = 0x0002
KEY_WOW64_64KEY = 0x0100
REG_SZ = 1


def errcheck_bool(result: int, func: Callable, args: tuple) -> int:
    """统一处理 BOOL 返回值。"""

    if not result:
        raise WinAPIError(func.__name__)
    return result


# 函数签名
user32.RegisterClassExW.argtypes = [ctypes.POINTER(WNDCLASS)]
user32.RegisterClassExW.restype = wintypes.ATOM
user32.UnregisterClassW.argtypes = [wintypes.LPCWSTR, wintypes.HINSTANCE]
user32.UnregisterClassW.restype = wintypes.BOOL
user32.CreateWindowExW.argtypes = [
    wintypes.DWORD,
    wintypes.LPCWSTR,
    wintypes.LPCWSTR,
    wintypes.DWORD,
    ctypes.c_int,
    ctypes.c_int,
    ctypes.c_int,
    ctypes.c_int,
    wintypes.HWND,
    wintypes.HMENU,
    wintypes.HINSTANCE,
    wintypes.LPVOID,
]
user32.CreateWindowExW.restype = wintypes.HWND
user32.DestroyWindow.argtypes = [wintypes.HWND]
user32.DestroyWindow.restype = wintypes.BOOL
user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.DefWindowProcW.restype = wintypes.LRESULT
user32.GetMessageW.argtypes = [ctypes.POINTER(MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT]
user32.GetMessageW.restype = wintypes.BOOL
user32.TranslateMessage.argtypes = [ctypes.POINTER(MSG)]
user32.DispatchMessageW.argtypes = [ctypes.POINTER(MSG)]
user32.PostQuitMessage.argtypes = [ctypes.c_int]
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.RegisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int, wintypes.UINT, wintypes.UINT]
user32.RegisterHotKey.restype = wintypes.BOOL
user32.UnregisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int]
user32.UnregisterHotKey.restype = wintypes.BOOL
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.restype = wintypes.BOOL
user32.CreatePopupMenu.argtypes = []
user32.CreatePopupMenu.restype = wintypes.HMENU
user32.DestroyMenu.argtypes = [wintypes.HMENU]
user32.DestroyMenu.restype = wintypes.BOOL
user32.CheckMenuItem.argtypes = [wintypes.HMENU, wintypes.UINT, wintypes.UINT]
user32.CheckMenuItem.restype = wintypes.DWORD
user32.CheckMenuRadioItem.argtypes = [wintypes.HMENU, wintypes.UINT, wintypes.UINT, wintypes.UINT, wintypes.UINT]
user32.CheckMenuRadioItem.restype = wintypes.BOOL
user32.EnableMenuItem.argtypes = [wintypes.HMENU, wintypes.UINT, wintypes.UINT]
user32.EnableMenuItem.restype = wintypes.BOOL
user32.AppendMenuW.argtypes = [wintypes.HMENU, wintypes.UINT, wintypes.UINT_PTR, wintypes.LPCWSTR]
user32.AppendMenuW.restype = wintypes.BOOL
user32.TrackPopupMenu.argtypes = [wintypes.HMENU, wintypes.UINT, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.HWND, wintypes.LPCRECT]
user32.TrackPopupMenu.restype = wintypes.BOOL
user32.GetCursorPos.argtypes = [ctypes.POINTER(POINT)]
user32.GetCursorPos.restype = wintypes.BOOL
user32.SetFocus.argtypes = [wintypes.HWND]
user32.SetFocus.restype = wintypes.HWND
user32.LoadIconW.argtypes = [wintypes.HINSTANCE, wintypes.LPCWSTR]
user32.LoadIconW.restype = wintypes.HICON
user32.LoadCursorW.argtypes = [wintypes.HINSTANCE, wintypes.LPCWSTR]
user32.LoadCursorW.restype = wintypes.HCURSOR
user32.MessageBoxW.argtypes = [wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.UINT]
user32.MessageBoxW.restype = ctypes.c_int
user32.WindowFromPoint.argtypes = [POINT]
user32.WindowFromPoint.restype = wintypes.HWND
user32.GetParent.argtypes = [wintypes.HWND]
user32.GetParent.restype = wintypes.HWND
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetClassNameW.restype = ctypes.c_int
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.GetForegroundWindow.argtypes = []
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(RECT)]
user32.GetWindowRect.restype = wintypes.BOOL
user32.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
user32.MonitorFromWindow.restype = wintypes.HMONITOR
user32.GetMonitorInfoW.argtypes = [wintypes.HMONITOR, ctypes.POINTER(MONITORINFO)]
user32.GetMonitorInfoW.restype = wintypes.BOOL
user32.GetDoubleClickTime.argtypes = []
user32.GetDoubleClickTime.restype = wintypes.UINT
user32.GetSystemMetrics.argtypes = [ctypes.c_int]
user32.GetSystemMetrics.restype = ctypes.c_int
user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
user32.FindWindowW.restype = wintypes.HWND
user32.FindWindowExW.argtypes = [wintypes.HWND, wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR]
user32.FindWindowExW.restype = wintypes.HWND
user32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.SendMessageW.restype = wintypes.LRESULT
user32.ScreenToClient.argtypes = [wintypes.HWND, ctypes.POINTER(POINT)]
user32.ScreenToClient.restype = wintypes.BOOL
user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HookProcType, wintypes.HINSTANCE, wintypes.DWORD]
user32.SetWindowsHookExW.restype = wintypes.HHOOK
user32.CallNextHookEx.argtypes = [wintypes.HHOOK, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM]
user32.CallNextHookEx.restype = wintypes.LRESULT
user32.UnhookWindowsHookEx.argtypes = [wintypes.HHOOK]
user32.UnhookWindowsHookEx.restype = wintypes.BOOL
user32.RegisterWindowMessageW.argtypes = [wintypes.LPCWSTR]
user32.RegisterWindowMessageW.restype = wintypes.UINT

shell32.Shell_NotifyIconW.argtypes = [wintypes.DWORD, ctypes.POINTER(NOTIFYICONDATA)]
shell32.Shell_NotifyIconW.restype = wintypes.BOOL

kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
kernel32.GetModuleHandleW.restype = wintypes.HMODULE
kernel32.CreateMutexW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR]
kernel32.CreateMutexW.restype = wintypes.HANDLE
kernel32.GetLastError.argtypes = []
kernel32.GetLastError.restype = wintypes.DWORD
kernel32.ReleaseMutex.argtypes = [wintypes.HANDLE]
kernel32.ReleaseMutex.restype = wintypes.BOOL
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL

advapi32.RegCreateKeyExW.argtypes = [
    wintypes.HKEY,
    wintypes.LPCWSTR,
    wintypes.DWORD,
    wintypes.LPWSTR,
    wintypes.DWORD,
    wintypes.REGSAM,
    wintypes.LPSECURITY_ATTRIBUTES,
    ctypes.POINTER(wintypes.HKEY),
    ctypes.POINTER(wintypes.DWORD),
]
advapi32.RegCreateKeyExW.restype = wintypes.LONG
advapi32.RegSetValueExW.argtypes = [
    wintypes.HKEY,
    wintypes.LPCWSTR,
    wintypes.DWORD,
    wintypes.DWORD,
    wintypes.LPCVOID,
    wintypes.DWORD,
]
advapi32.RegSetValueExW.restype = wintypes.LONG
advapi32.RegQueryValueExW.argtypes = [
    wintypes.HKEY,
    wintypes.LPCWSTR,
    ctypes.POINTER(wintypes.DWORD),
    ctypes.POINTER(wintypes.DWORD),
    wintypes.LPVOID,
    ctypes.POINTER(wintypes.DWORD),
]
advapi32.RegQueryValueExW.restype = wintypes.LONG
advapi32.RegOpenKeyExW.argtypes = [
    wintypes.HKEY,
    wintypes.LPCWSTR,
    wintypes.DWORD,
    wintypes.REGSAM,
    ctypes.POINTER(wintypes.HKEY),
]
advapi32.RegOpenKeyExW.restype = wintypes.LONG
advapi32.RegDeleteValueW.argtypes = [wintypes.HKEY, wintypes.LPCWSTR]
advapi32.RegDeleteValueW.restype = wintypes.LONG
advapi32.RegCloseKey.argtypes = [wintypes.HKEY]
advapi32.RegCloseKey.restype = wintypes.LONG


# 错误检查配置
def _setup_errcheck() -> None:
    user32.RegisterClassExW.errcheck = errcheck_bool
    user32.DestroyWindow.errcheck = errcheck_bool
    user32.UnregisterClassW.errcheck = errcheck_bool
    user32.RegisterHotKey.errcheck = errcheck_bool
    user32.UnregisterHotKey.errcheck = errcheck_bool
    user32.SetForegroundWindow.errcheck = errcheck_bool
    shell32.Shell_NotifyIconW.errcheck = errcheck_bool
    user32.GetCursorPos.errcheck = errcheck_bool
    user32.GetClassNameW.errcheck = errcheck_bool
    user32.IsWindowVisible.errcheck = errcheck_bool
    user32.GetWindowRect.errcheck = errcheck_bool
    user32.GetMonitorInfoW.errcheck = errcheck_bool
    user32.UnhookWindowsHookEx.errcheck = errcheck_bool
    advapi32.RegCloseKey.errcheck = errcheck_bool


_setup_errcheck()


@contextmanager
def system_cursor() -> None:
    """预留获取/恢复光标的上下文。"""

    yield


def register_window_message(name: str) -> int:
    """注册自定义窗口消息。"""

    msg = user32.RegisterWindowMessageW(name)
    if not msg:
        raise WinAPIError("RegisterWindowMessageW")
    return msg


def create_mutex(name: str) -> tuple[bool, wintypes.HANDLE]:
    """创建命名互斥体，返回 (是否新建, 句柄)。"""

    handle = kernel32.CreateMutexW(None, False, name)
    if not handle:
        raise WinAPIError("CreateMutexW")
    existed = kernel32.GetLastError() == 183
    return (not existed), handle


def release_mutex(handle: wintypes.HANDLE) -> None:
    """释放互斥体。"""

    kernel32.ReleaseMutex(handle)
    kernel32.CloseHandle(handle)


def toggle_desktop_icons() -> None:
    """向 Explorer 发送消息切换桌面图标。"""

    progman = user32.FindWindowW("Progman", None)
    if not progman:
        progman = user32.FindWindowW("WorkerW", None)
        if not progman:
            raise WinAPIError("FindWindowW")
    user32.SendMessageW(progman, WM_COMMAND, 0x7402, 0)


def make_notify_icon(hwnd: wintypes.HWND, icon: wintypes.HICON, tip: str, ident: int = 1) -> NOTIFYICONDATA:
    """创建托盘图标结构体。"""

    data = NOTIFYICONDATA()
    data.cbSize = ctypes.sizeof(NOTIFYICONDATA)
    data.hWnd = hwnd
    data.uID = ident
    data.uFlags = NIF_MESSAGE | NIF_TIP | NIF_ICON
    data.uCallbackMessage = WM_TRAYICON
    data.hIcon = icon
    ctypes.windll.msvcrt.wcscpy(data.szTip, tip[:127])
    return data


def is_window_class(hwnd: wintypes.HWND, class_name: str) -> bool:
    """检查窗口类名。"""

    buf = ctypes.create_unicode_buffer(256)
    try:
        user32.GetClassNameW(hwnd, buf, len(buf))
    except WinAPIError:
        return False
    return buf.value == class_name


def make_int_resource(value: int):
    """将整数资源编号转换为指针。"""

    return ctypes.cast(ctypes.c_void_p(value), wintypes.LPWSTR)


def get_module_handle(name: str | None = None) -> wintypes.HMODULE:
    """获取模块句柄。"""

    handle = kernel32.GetModuleHandleW(name)
    if not handle:
        raise WinAPIError("GetModuleHandleW")
    return handle


__all__ = [
    "WinAPIError",
    "user32",
    "kernel32",
    "shell32",
    "advapi32",
    "WndProcType",
    "HookProcType",
    "WNDCLASS",
    "NOTIFYICONDATA",
    "POINT",
    "MSG",
    "RECT",
    "MONITORINFO",
    "LVHITTESTINFO",
    "MSLLHOOKSTRUCT",
    "WM_CREATE",
    "WM_DESTROY",
    "WM_COMMAND",
    "WM_HOTKEY",
    "WM_TRAYICON",
    "WM_USER_TOGGLE",
    "WM_LBUTTONUP",
    "WM_LBUTTONDBLCLK",
    "WM_RBUTTONUP",
    "WM_LBUTTONDOWN",
    "LVM_HITTEST",
    "NIM_ADD",
    "NIM_MODIFY",
    "NIM_DELETE",
    "NIF_MESSAGE",
    "NIF_ICON",
    "NIF_TIP",
    "NIF_INFO",
    "MOD_ALT",
    "MOD_CONTROL",
    "MOD_SHIFT",
    "MOD_WIN",
    "MF_STRING",
    "MF_SEPARATOR",
    "MF_CHECKED",
    "MF_UNCHECKED",
    "MF_DISABLED",
    "MF_GRAYED",
    "MF_BYCOMMAND",
    "MF_BYPOSITION",
    "MF_RADIOCHECK",
    "TPM_RIGHTBUTTON",
    "TPM_BOTTOMALIGN",
    "TPM_RETURNCMD",
    "WH_MOUSE_LL",
    "HC_ACTION",
    "SM_CXDOUBLECLK",
    "SM_CYDOUBLECLK",
    "IDI_APPLICATION",
    "IDC_ARROW",
    "MB_OK",
    "MB_YESNO",
    "MB_ICONWARNING",
    "MB_ICONINFORMATION",
    "IDM_TRAY_FIRST",
    "HKEY_CURRENT_USER",
    "KEY_QUERY_VALUE",
    "KEY_SET_VALUE",
    "KEY_WOW64_64KEY",
    "REG_SZ",
    "register_window_message",
    "create_mutex",
    "release_mutex",
    "toggle_desktop_icons",
    "make_notify_icon",
    "is_window_class",
    "get_module_handle",
    "make_int_resource",
]
