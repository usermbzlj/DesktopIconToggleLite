"""热键解析工具。"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, Iterable, Tuple

from . import winapi


@dataclass
class Hotkey:
    """表示一个可注册的全局热键。"""

    modifiers: int
    vk: int
    raw: str


_MOD_MAP: Dict[str, int] = {
    "CTRL": winapi.MOD_CONTROL,
    "ALT": winapi.MOD_ALT,
    "SHIFT": winapi.MOD_SHIFT,
    "WIN": winapi.MOD_WIN,
}

_KEY_MAP: Dict[str, int] = {f"F{i}": 0x6F + i for i in range(1, 25)}
_KEY_MAP.update({chr(c): ord(chr(c)) for c in range(ord("A"), ord("Z") + 1)})
_KEY_MAP.update({str(i): 0x30 + i for i in range(0, 10)})
_EXTRA_KEYS: Dict[str, int] = {
    "SPACE": 0x20,
    "TAB": 0x09,
    "ESCAPE": 0x1B,
    "ENTER": 0x0D,
    "HOME": 0x24,
    "END": 0x23,
    "INSERT": 0x2D,
    "DELETE": 0x2E,
    "UP": 0x26,
    "DOWN": 0x28,
    "LEFT": 0x25,
    "RIGHT": 0x27,
}
_KEY_MAP.update(_EXTRA_KEYS)


class HotkeyParseError(ValueError):
    """热键字符串非法。"""


def parse_hotkey(text: str) -> Hotkey:
    """解析热键描述为结构体。"""

    if not text:
        raise HotkeyParseError("空热键")
    parts = [p.strip() for p in text.replace("-", "+").split("+") if p.strip()]
    if not parts:
        raise HotkeyParseError("缺少键位")

    modifiers = 0
    main_key = None
    for part in parts:
        upper = part.upper()
        if upper in _MOD_MAP:
            modifiers |= _MOD_MAP[upper]
        else:
            if main_key is not None:
                raise HotkeyParseError("只能有一个主键")
            if upper not in _KEY_MAP:
                raise HotkeyParseError(f"未知按键: {part}")
            main_key = _KEY_MAP[upper]
    if main_key is None:
        raise HotkeyParseError("未找到主键")
    return Hotkey(modifiers, main_key, text)


__all__ = ["Hotkey", "HotkeyParseError", "parse_hotkey"]
