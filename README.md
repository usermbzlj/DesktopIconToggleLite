# Desktop Icon Toggle Lite

> 轻量、可靠的桌面图标显隐切换器。
> C# / .NET 8 WinForms 托盘应用，支持**全局热键**、**桌面空白处双击**（可选）、**全屏自动避让**、**单实例**、**自启动**与 **JSON 配置**。

---

## ✨ 特性

* **单文件可执行**（可发布为单文件）
* **单实例**：避免重复热键注册/多个托盘
* **全局热键**：默认 `Ctrl+Alt+F1`，可自定义
* **桌面空白处双击**（可选）：仅在非全屏时启用低级鼠标钩子
* **全屏自动避让**：无边框/缩放场景容差更友好
* **自启动**：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
* **可选自动检查更新**：GitHub Releases，新版本托盘提示
* **配置持久化 + 日志记录**：`%APPDATA%\a6.DesktopIconToggleLite\`
* **跨实例命令**：`toggle`（切换） / `exit`（退出）
* **高 DPI**：Per-Monitor v2

---

## 目录

* [环境要求](#环境要求)
* [快速上手](#快速上手)
* [构建](#构建)
* [用法](#用法)
* [配置](#配置)
* [热键语法](#热键语法)
* [工作原理](#工作原理)
* [常见问题](#常见问题)
* [卸载与清理](#卸载与清理)
* [安全与签名](#安全与签名)
* [版本与协议](#版本与协议)

---

## 环境要求

* Windows 10/11
* [.NET 8 SDK](https://dotnet.microsoft.com/)（仅开发/构建需要；运行已发布的自包含程序时可不装）
* x64 或 ARM64

---

## 快速上手

1. 下载/编译 `DesktopIconToggleLite.exe`
2. 双击运行（首次运行会生成配置文件）
3. 托盘右键菜单可切换模式/设置自启动/打开配置

> **左键点击托盘图标** = 立即显隐桌面图标

---

## 构建

将以下两个文件放在同一目录：

* `Program.cs`（主程序源码）
* `DesktopIconToggleLite.csproj`（项目文件）

### PowerShell（推荐）

```powershell
# 发布为 win-x64，自包含，Release
dotnet publish -c Release -r win-x64

# 或 ARM64
dotnet publish -c Release -r win-arm64
```

发布目录：

```
.\bin\Release\net8.0-windows10.0.19041.0\<RID>\publish\
```

其中 `<RID>` 为 `win-x64` 或 `win-arm64`。

> 想要**单文件**：项目已启用 `PublishSingleFile=true`。
> 如需进一步瘦身，可考虑 `PublishTrimmed=true`（谨慎，WinForms 反射较多，默认关闭）。

---

## 用法

### 启动

双击 `DesktopIconToggleLite.exe`。程序常驻托盘。

### 托盘菜单

* **立即切换图标**：显隐桌面图标
* **模式：热键（推荐）**：仅使用全局热键
* **模式：桌面空白处双击**：开启低级鼠标钩子，非全屏时生效
* **检查更新 / 启用自动检查更新**：查询 GitHub Releases，找到新版本会弹出托盘气泡提示
* **开机自启**：写入/删除 `HKCU\...\Run`
* **打开配置文件**：在记事本中打开 JSON
* **退出**

### 命令行（对已运行实例发信号）

```powershell
.\DesktopIconToggleLite.exe toggle  # 让已运行实例立即切换图标
.\DesktopIconToggleLite.exe exit    # 让已运行实例退出
```

---

## 配置

路径：`%APPDATA%\a6.DesktopIconToggleLite\config.json`
日志：`%APPDATA%\a6.DesktopIconToggleLite\log.txt`
示例：

```json
{
  "Mode": "Hotkey",
  "Hotkey": "Ctrl+Alt+F1",
  "SuppressInFullscreen": true,
  "ShowTrayIcon": true,
  "AutoStart": false,
  "CheckUpdates": true,
  "FullscreenTolerance": 3
}
```

字段说明：

* `Mode`：`Hotkey` 或 `DesktopDoubleClick`
* `Hotkey`：全局热键（语法见下节）
* `SuppressInFullscreen`：全屏时自动关闭双击钩子
* `ShowTrayIcon`：是否显示托盘图标（`true` 建议保留，方便退出/设置）
* `AutoStart`：是否开机自启（菜单操作优先，建议通过菜单切换）
* `CheckUpdates`：是否开机后自动检查 GitHub Releases 新版本（手动点击菜单也会顺便开启）
* `FullscreenTolerance`：全屏判定容差（像素），用于兼容无边框/缩放场景

> 修改后：部分设置即时生效（如模式）；热键变更建议重启程序以确保稳定注册。

---

## 热键语法

* 由组合键 + 主键构成，使用 `+` 连接：

  * 组合键：`Ctrl` / `Alt` / `Shift` / `Win`
  * 主键：

    * 功能键：`F1`..`F24`
    * 字母/数字：`A..Z`、`0..9`
    * 其它 Keys 枚举名：如 `Space`、`Tab`、`Home`、`End`、`Insert`、`Delete` 等（与 `System.Windows.Forms.Keys` 对齐）

示例：

* `Ctrl+Alt+F1`（默认）
* `Ctrl+Shift+D`
* `Win+Space`
* `Alt+F10`

> 注意：含 **Win** 的组合可能与系统/厂商快捷键冲突，注册可能失败。

---

## 工作原理

* **切换桌面图标**：向 `Progman` 窗口发送 `WM_COMMAND (0x7402)`，由 Explorer 处理，**无需重启 Explorer/改注册表**。
* **全局热键**：`RegisterHotKey` 绑定到隐藏消息窗体。
* **桌面空白处双击**：

  * 仅在 `Mode=DesktopDoubleClick` 且非全屏时启用 `WH_MOUSE_LL`。
  * 双击判定：系统双击时间/距离 + 同一窗口句柄。
  * 命中后对 `SysListView32` 做 `LVM_HITTEST`，只在**空白处**触发。
  * 失败回退：枚举 `Progman/WorkerW → SHELLDLL_DefView → SysListView32`。
* **全屏避让**：前台窗口矩形与显示器工作区对比，容差 `±3px`，兼容无边框全屏与缩放。

---

## 常见问题

**Q1: 热键注册失败？**
A: 常见原因是与系统/其他程序冲突。

* 换一个组合（避免裸 `F1`/`F2`，建议加 `Ctrl/Alt`）。
* 某些 **Win** 组合被系统保留，尽量避免。

**Q2: 双击无效？**
A:

* 确认模式为 `桌面空白处双击`；
* 是否处于全屏（全屏会自动停钩子）；
* 鼠标是否确实点在桌面“空白处”（非图标/文件/小组件区域）；
* 某些第三方桌面替换程序可能不兼容，改用**热键/托盘**切换。

**Q3: 全屏游戏里误触？**
A: 开启 `SuppressInFullscreen=true`（默认），程序会在全屏前台时停止钩子。

**Q4: 安全软件拦截？**
A: 程序包含全局热键/低级钩子，部分安全软件可能提示。添加信任或自签名即可（见下节）。

**Q5: Explorer 重启后还能用吗？**
A: 可以。切换逻辑每次查找目标窗口并发送消息，Explorer 恢复后即继续生效。

---

## 卸载与清理

1. 托盘菜单 → 退出；
2. 若设置了自启动，托盘菜单取消或删除注册表项：

```reg
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ a6.DesktopIconToggleLite
```

3. 删除可执行文件；
4. 可选清理配置目录：

```
%APPDATA%\a6.DesktopIconToggleLite\
```

---

## 安全与签名

* 默认不需要管理员权限。
* 若要**企业内分发**，建议：

  * 对 `DesktopIconToggleLite.exe` 进行代码签名（EV/OV 均可）；
  * 通过组策略允许受信任发布者；
  * 如需更严苛自保护，可在 CI 中附加 SHA256 校验信息并在启动时自检。

---

## 版本与协议

* **最低系统**：Windows 10 19041（.NET 目标框架设置）
* **CPU 架构**：`win-x64`（推荐）、`win-arm64`
