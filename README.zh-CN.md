# Codex Usage Monitor — ECAM

[English](README.md)

一款 Windows 原生、紧凑且默认置顶的 Codex 使用情况监视器，用于显示剩余额度、
重置次数和 Codex Desktop 当前聚焦任务的上下文占用。主仪表借鉴 Airbus A320
ECAM 的视觉语言，右下角上下文弧则借鉴 Boeing 737 EICAS 的数量显示思路。

这是独立的社区项目，并非 OpenAI 或任何飞机制造商的官方产品。

![Codex ECAM Monitor 主窗口](docs/images/main-window.png)

隐藏主窗口后，后台托盘图标仍会显示剩余百分比：

<img src="docs/images/tray-icon.png" alt="显示 79 的 Codex ECAM Monitor 托盘图标" width="64">

## 功能

- 通过 Codex CLI `app-server` 读取实时剩余百分比。
- 采用 ECAM 风格的绿色、琥珀色和红色阈值。
- 显示当前额度周期和下一次重置时间。
- 当 Codex 返回相关数据时，通过 `RST` 显示可用完整重置次数。
- 通过 `CTX K` 显示 Codex Desktop 当前聚焦任务的上下文 token 数（单位：千）。
- 无边框、可拖动、默认置顶并保存窗口位置。
- Windows 托盘数字图标支持显示、隐藏、刷新、置顶和退出。
- 监视器自身是 Windows 原生可执行文件，运行时不依赖 WSL、Python 或 PowerShell。

## 系统要求

- Windows 10 或 Windows 11 x64。
- .NET Framework 4.x；受支持的 Windows 通常已包含。
- 已安装并完成登录的 Codex CLI。

发布包刻意**不包含 Codex CLI**。请根据
[OpenAI Codex 官方仓库](https://github.com/openai/codex)中的最新说明安装。
官方当前给出的 Windows 安装命令为：

```powershell
powershell -ExecutionPolicy Bypass -c "irm https://chatgpt.com/codex/install.ps1 | iex"
```

也可以使用 npm：

```powershell
npm install -g @openai/codex
```

启动监视器前，请先运行一次 `codex`，并选择 **Sign in with ChatGPT** 完成登录。

## 快速开始

1. 从 GitHub Releases 下载 `CodexEcamMonitor-v1.0.1-win-x64.zip`。
2. 可选验证 `.sha256` 校验值，然后把 ZIP 解压到普通可写目录。
3. 按照上面的说明安装并登录 Codex CLI。
4. 双击 `Start Codex ECAM Monitor.bat` 或 `CodexEcamMonitor.exe`。
5. 把窗口拖到目标屏幕；下次启动时会恢复位置。

账户周额度每 60 秒刷新一次；当前聚焦任务及 `CTX K` 由独立定时器每 5 秒刷新，
因此切换 Codex Desktop 任务后通常会在约 5 秒内更新。按 `F5` 或在右键菜单中
选择 **Refresh now** 可立即刷新两类数据。

## Codex CLI 查找顺序

监视器按以下顺序寻找 CLI：

1. `CODEX_CLI_PATH` 指定的 `.exe`、`.cmd` 或 `.bat`。
2. 与 `CodexEcamMonitor.exe` 同目录的旧版兼容 `codex.exe`。
3. Windows `PATH` 中的 `codex.exe`、`codex.cmd` 或 `codex.bat`。

非标准安装可以显式指定路径，然后重新启动监视器：

```powershell
setx CODEX_CLI_PATH "C:\Tools\Codex\codex.exe"
```

通过 `setx` 新增的环境变量，需要在下次登录或重启启动程序所用的终端后才能被
新进程读取。

如果找不到 CLI，监视器不会崩溃，而是保持 `NO DATA` 状态并显示安装提示。

## 仪表含义

- 大号百分比表示 Codex 返回的最长额度周期中的剩余比例。
- 红色和琥珀色刻度表示低剩余额度区域。
- `WEEK`、`DAY`、`HR` 或 `MIN` 表示额度周期。
- `RESET` 表示按本地时间显示的下一次重置时间。
- `RST` 表示账户返回的完整额度重置次数。
- `CTX K` 表示 Codex Desktop 当前聚焦任务的上下文 token 数；白色 240° 弧表示
  它占该模型上下文窗口的比例。程序读取最后一个有效 `token_count` 事件，并使用
  `info.last_token_usage.total_tokens / model_context_window` 计算占比。

`CTX K` 是本地任务状态，不是账户 token 账单，也不参与主百分比计算。

## 托盘操作

- 双击托盘数字图标可以显示或隐藏主窗口。
- 右键菜单包含 **Show monitor**、**Hide to tray**、**Refresh now**、
  **Always on top** 和 **Exit**。
- 正常状态为绿色，剩余不超过 20% 为琥珀色，不超过 10% 为红色，无数据时显示
  `--`。
- Windows 可能会把新托盘图标放入隐藏图标区域。

## 隐私与本地数据

程序自身不包含遥测。它启动本机安装的 Codex CLI，并通过本地标准输入输出形式的
`app-server` 调用 `account/rateLimits/read`。

为避免与 Codex App 共用数据库状态，监视器使用
`%LOCALAPPDATA%\CodexEcamMonitor\codex-home`，并从用户的 Codex 主目录复制可用
登录凭据。若设置了 `CODEX_HOME`，优先使用该目录；否则自动使用当前 Windows
用户的 `%USERPROFILE%\.codex`，不依赖固定用户名。

为读取 `CTX K`，程序跟踪 Codex Desktop 日志中最新的
`thread_stream_view_activity_changed active=true` 事件，再依据其中的
`conversationId`，递归查找 `sessions` 和 `archived_sessions` 下对应的 JSONL。
程序同时支持传统安装的 `%LOCALAPPDATA%\Codex\Logs`、
`%LOCALAPPDATA%\OpenAI\Codex\Logs`，并动态枚举名称匹配 `OpenAI.Codex_*` 的
Microsoft Store 包。若无法识别聚焦任务，则回退到最近写入的非 `subagent` 主任务。

日志或 session 临时读取失败时会保留上一次有效 CTX 数值。个人凭据、Codex CLI
二进制文件和用户 session 数据绝不会进入 Git 跟踪、构建产物或 Release 包。

## 从源码构建

在 Windows 命令提示符中运行：

```bat
scripts\build.cmd
```

脚本使用 Windows 自带的 .NET Framework x64 C# 编译器，将程序生成到
`dist\CodexEcamMonitor.exe`。

构建完整发布包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package-release.ps1 -Version 1.0.1
```

ZIP 和校验文件会写入 `release\`。打包脚本会拒绝凭据、会话日志、`codex.exe`
以及任何超过 100 MB 的文件。

## 故障排查

- **NO DATA / CLI NOT FOUND：**安装 Codex CLI，运行一次 `codex` 完成登录，或设置
  `CODEX_CLI_PATH`。
- **升级后仍为 NO DATA：**按 `F5`；如果问题持续，检查已安装 CLI 是否能够启动
  `codex app-server --stdio`。本项目依赖的上游 app-server 接口未来可能变化。
- **CTX 未随当前任务切换：**确认 Codex Desktop 已在上述传统安装或 Microsoft
  Store 路径中生成日志，并确认对应 session 位于当前 Codex 主目录；若数据位于
  其他位置，请设置 `CODEX_HOME`。
- **找不到托盘图标：**检查 Windows 隐藏图标区域。
- **字体外观不正确：**确保 `assets\ECAMFontRegular.ttf` 与 EXE 一起保留。

## 许可证与设计来源

Codex ECAM Monitor 采用 GPL-3.0。随包提供的 Display-EIS 字体来自采用 GPL-3.0
的 FlyByWire aircraft 项目。A320 ECAM 与 FlightGear 737 EICAS 的设计参考、外部
Codex CLI 依赖和商标声明详见 [NOTICE.md](NOTICE.md)。
