"""配置加载与保存逻辑。"""
from __future__ import annotations

import json
import os
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Dict

CONFIG_DIR = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming")) / "a6.DesktopIconToggleLite"
CONFIG_PATH = CONFIG_DIR / "config.json"


@dataclass
class Config:
    """表示应用配置。"""

    mode: str = "Hotkey"
    hotkey: str = "Ctrl+Alt+F1"
    suppress_in_fullscreen: bool = True
    show_tray_icon: bool = True
    auto_start: bool = False
    check_updates: bool = True
    fullscreen_tolerance: int = 3
    show_toggle_toast: bool = True
    show_first_run_guide: bool = True
    extras: Dict[str, Any] = field(default_factory=dict)

    def normalize(self) -> None:
        """归一化配置字段，确保取值安全。"""

        if self.mode not in {"Hotkey", "DesktopDoubleClick"}:
            self.mode = "Hotkey"
        self.hotkey = self.hotkey.strip() or "Ctrl+Alt+F1"
        self.fullscreen_tolerance = max(0, min(int(self.fullscreen_tolerance), 64))
        for key in ("suppress_in_fullscreen", "show_tray_icon", "auto_start", "check_updates", "show_toggle_toast", "show_first_run_guide"):
            setattr(self, key, bool(getattr(self, key)))

    def to_dict(self) -> Dict[str, Any]:
        """转换为字典，保留额外字段。"""

        data = asdict(self)
        extras = data.pop("extras", {})
        data.update(extras)
        return data

    def update_from_dict(self, data: Dict[str, Any]) -> None:
        """根据字典更新字段。"""

        for key, value in data.items():
            if hasattr(self, key):
                setattr(self, key, value)
            else:
                self.extras[key] = value
        self.normalize()

    def save(self, path: Path = CONFIG_PATH) -> None:
        """保存配置为 JSON。"""

        CONFIG_DIR.mkdir(parents=True, exist_ok=True)
        with path.open("w", encoding="utf-8") as fp:
            json.dump(self.to_dict(), fp, ensure_ascii=False, indent=2)

    @classmethod
    def load(cls, path: Path = CONFIG_PATH) -> "Config":
        """从 JSON 加载配置，不存在时返回默认配置。"""

        cfg = cls()
        if path.exists():
            with path.open("r", encoding="utf-8") as fp:
                data = json.load(fp)
            if isinstance(data, dict):
                cfg.update_from_dict(data)
        cfg.normalize()
        return cfg


__all__ = ["Config", "CONFIG_DIR", "CONFIG_PATH"]
