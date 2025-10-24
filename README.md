# Desktop Icon Toggle Lite (Python 版)

> 轻量、可靠的桌面图标显隐切换器。使用 Python + Win32 API 实现，提供全局热键、桌面空白处双击、自启动、自动检查更新等功能。

---

## ✨ 特性

* **单实例**：互斥锁保证只运行一个副本，避免热键重复注册。
* **全局热键**：默认 `Ctrl+Alt+F1`，可在配置文件中自定义。
* **桌面空白处双击**：可选模式，通过低级鼠标钩子识别桌面空白区域。
* **全屏自动避让**：可配置容差，避免在全屏游戏或无边框窗口下触发。
* **系统托盘菜单**：立即切换、模式切换、开机自启、配置/指南等常用操作。
* **自启动管理**：写入/删除 `HKCU\\...\\Run` 注册表项。
* **自动检查更新**：调用 GitHub Releases API，可检测新版本并提示下载链接。
* **配置持久化 + 日志记录**：保存在 `%APPDATA%\\a6.DesktopIconToggleLite\\`。
* **跨实例命令**：`toggle`（切换） / `exit`（退出）。

---

## 环境要求

* Windows 10/11 x64 或 ARM64
* Python 3.11 及以上（推荐使用 64 位解释器）
* 运行时依赖仅标准库与 Win32 API（无需额外第三方包）

---

## 快速开始

1. 安装 Python 3.11，并确保 `python` 在系统 `PATH` 中。
2. 克隆或下载本仓库。
3. 在 PowerShell 中执行：

   ```powershell
   python -m desktop_icon_toggle_lite.app
   ```

4. 首次运行会在 `%APPDATA%\\a6.DesktopIconToggleLite\\` 下生成 `config.json`、`log.txt`，并弹出新手指南。

> **提示**：可通过 `python -m desktop_icon_toggle_lite.app toggle` / `exit` 向已运行实例发送指令。

---

## 配置

路径：`%APPDATA%\\a6.DesktopIconToggleLite\\config.json`

示例：

```json
{
  "mode": "Hotkey",
  "hotkey": "Ctrl+Alt+F1",
  "suppress_in_fullscreen": true,
  "show_tray_icon": true,
  "auto_start": false,
  "check_updates": true,
  "fullscreen_tolerance": 3,
  "show_toggle_toast": true,
  "show_first_run_guide": false
}
```

字段说明：

* `mode`：`Hotkey` 或 `DesktopDoubleClick`。
* `hotkey`：全局热键描述，语法与原版一致（`Ctrl`/`Alt`/`Shift`/`Win` + 主键）。
* `suppress_in_fullscreen`：在全屏前台窗口时忽略双击触发。
* `show_tray_icon`：是否显示托盘图标（建议保持开启）。
* `auto_start`：是否开机自启。
* `check_updates`：是否自动检查 GitHub Releases 新版本。
* `fullscreen_tolerance`：全屏判定容差（像素）。
* `show_toggle_toast`：切换图标后是否显示托盘气泡提示（亦用于更新通知）。
* `show_first_run_guide`：首次运行时是否展示新手指南。

修改配置后可直接保存，部分选项会即时生效；如修改热键，建议重启程序以确保注册成功。

---

## 托盘菜单

* **立即切换图标**：显隐桌面图标。
* **使用小白指南**：重新查看新手提示。
* **模式切换**：热键模式 / 桌面空白处双击模式。
* **切换时显示提示**：控制托盘气泡提示。
* **开机自启**：注册/移除启动项。
* **检查更新 / 启用自动检查更新**：手动检查或开启自动检查。
* **恢复默认设置**：重置配置并重新展示指南。
* **打开配置文件**：使用系统默认编辑器打开 JSON。
* **退出**：关闭程序并移除托盘图标。

---

## 日志

日志路径：`%APPDATA%\\a6.DesktopIconToggleLite\\log.txt`

程序运行过程中会记录关键事件，如热键注册、钩子状态、更新检查结果等，方便排查问题。

---

## 卸载

1. 托盘菜单 → 退出。
2. 若启用了自启动，可在托盘菜单关闭或手动删除注册表项：

   ```reg
   HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ a6.DesktopIconToggleLite
   ```

3. 删除项目文件夹；可选删除 `%APPDATA%\\a6.DesktopIconToggleLite\\` 目录。

---

## 许可

沿用原项目的开源协议（如需自定义，请在此处补充）。
