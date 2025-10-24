"""简单日志模块。"""
from __future__ import annotations

import os
from datetime import datetime
from pathlib import Path
from typing import Iterable

from .config import CONFIG_DIR

LOG_PATH = CONFIG_DIR / "log.txt"


def _write(level: str, parts: Iterable[str]) -> None:
    """写入日志文件。"""

    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    line = f"{datetime.now():%Y-%m-%d %H:%M:%S.%f} [{level}] " + " ".join(parts)
    with LOG_PATH.open("a", encoding="utf-8") as fp:
        fp.write(line.rstrip() + os.linesep)


def info(*parts: str) -> None:
    """记录信息级日志。"""

    _write("INFO", parts)


def warn(*parts: str) -> None:
    """记录警告级日志。"""

    _write("WARN", parts)


def error(*parts: str) -> None:
    """记录错误级日志。"""

    _write("ERROR", parts)


__all__ = ["info", "warn", "error", "LOG_PATH"]
