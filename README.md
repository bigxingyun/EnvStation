# 环境站 EnvStation

Windows 开发环境配置与回滚工具。装语言、配环境变量、理 PATH、解命令冲突，每一步都能预演、能拒绝、能回滚。

[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](LICENSE)
[![Tests](https://img.shields.io/badge/tests-345%20passing-brightgreen.svg)](#验证)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%201809%2B%20%7C%2011-lightgrey.svg)](#环境要求)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

> **开发中，无发布版本，无安装包，无代码签名。** 核心功能可用，有 345 项自动化用例。
> 它会修改环境变量，请先在非关键机器或虚拟机上试。

## 功能

- **环境检测**：系统版本、磁盘、用户级与系统级 PATH、前置依赖、同名命令冲突
- **环境变量与 PATH**：读写、去重、清理失效条目、diff、快照回滚
- **包管理**：导入他人分享的 `.envstation` 包，先校验再执行
- **快照与回滚**：写入前自动快照，失败自动回滚
- **74 个预制动作**：探测、下载、解压、安装、环境变量、PATH、配置与镜像源、验证、清理

图形界面含概览、环境检测、导入包、快照与回滚、动作库、设置六个页面；
命令行提供 `doctor` / `check` / `pack` / `run` / `env` / `path` 等命令。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809（build 17763）或更高，Windows 11 |
| CPU 架构 | x64 或 ARM64 |
| .NET SDK | 8.0.425 或更高的 8.0.x（`global.json` 已钉住版本，低版本会直接报错） |
| 磁盘 | 构建需约 1 GB；图形界面自包含发布目录 160 MB |
| 网络 | 首次构建需从 NuGet 还原 Windows App SDK、SDK 构建工具、MSIX 工具链 |
| 管理员权限 | 不需要。系统级环境变量的写入在当前版本还不可用 |

## 快速开始

```powershell
# 1. 克隆
git clone <本仓库地址> EnvStation
cd EnvStation

# 2. 构建
dotnet build EnvStation.sln -c Release

# 3. 跑一次只读检测（不需要授权，不会写入任何东西）
dotnet run --project src\EnvStation.Cli -c Release -- doctor

# 4. 启动图形界面
dotnet run --project src\EnvStation.App -c Release
```

图形界面没有命令行参数，启动后停在概览页。命令行是主要入口，所有能力都可用。

全量验证（17 个工程构建 + 12 套件 + AOT 探针 + 一致性检查，约 10 分钟）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m0.ps1
```

## 项目结构

```
src/EnvStation.Abstractions/   契约：接口、模型、错误码，无第三方依赖
src/EnvStation.Core/           内核：环境变量、PATH、快照事务、动作层、工作流、包验证
src/EnvStation.Cli/            命令行入口，Native AOT
src/EnvStation.App/            图形界面，WinUI 3
tests/                         12 个测试套件 + TestKit + AOT 探针
design/tokens.json             设计令牌唯一来源，generate-tokens.ps1 生成 C# 表
samples/python-env/            示例自动化包
tools/                         verify-m0.ps1 / measure-p01.ps1 / capture-ui.ps1 等
```

## 配置

不需要配置文件即可运行。以下文件只在需要时改：

| 文件 | 作用 | 何时需要改 |
| --- | --- | --- |
| `global.json` | 钉住 .NET SDK 版本（8.0.425，`rollForward: latestFeature`） | 本机装了别的 8.0.x 且想换用时 |
| `Directory.Build.props` | 全仓库构建基线：TFM、语言版本、警告即错误、版本号、AOT 基线 | 改版本号或调整分析严格度时 |
| `src/EnvStation.App/EnvStation.App.csproj` | 图形界面打包方式（自包含、非打包运行、PRI 工具链） | 改交付形态时 |

运行期数据目录为 `%LOCALAPPDATA%\EnvStation`：

| 子目录 | 内容 |
| --- | --- |
| `audit/` | 审计日志 JSONL，按大小轮转 |
| `snapshots/` | 环境变量与配置快照 |
| `logs/` | 界面未处理异常日志 |

包与工作流在各自目录内声明，不读全局配置。

## 使用文档

### 检测本机环境

```powershell
envstation doctor
```

```
环境站 · 环境检测
────────────────────────────────────────────────────────────────────
  ✓ detect.os                    Windows 11 Home China · 构建 22631.4317 · 中文（中国）
  ✓ detect.arch                  CPU 架构：x64（进程 x64）
  ✓ detect.disk                  磁盘 C:\（NTFS）可用 30467.2 MB，可写：是
  ✓ detect.env                   读取到 2 个环境变量（用户级与系统级）
  ! detect.deps                  缺少 1 项前置依赖：powershell-7
  ! path.validate                用户级 PATH 共 14 项，其中 4 项异常（不存在 2、重复 0、空条目 0、变量未解析 0）
  ! path.validate                系统级 PATH 共 35 项，其中 20 项异常（不存在 2、重复 1、空条目 0、变量未解析 1）
  ! detect.conflict              发现 2 处同名命令冲突：…

共 8 项，其中 4 项异常。
```

`!` 表示动作执行成功但这一项有问题。缺前置依赖、PATH 有失效条目、命令冲突都属于这一类。

### 命令行

```
doctor                          检测本机环境
actions [--json]                列出全部动作及其参数
check <包> [--pubkey <公钥>] [--v4]   校验包（V1 结构 → V2 静态 → V3 合规 → V4 沙箱试运行）
pack <源目录> [--out <文件>] [--key <私钥>]   打成 .envstation
keygen [--out <目录>]           生成 ECDSA P-256 密钥对
fingerprint <公钥>              计算密钥指纹
run <包> [--apply] [--allow <能力ID>] [--root <目录>]   执行包内工作流
env get|set|unset|diff|restore|validate
path list|ensure|remove|clean|validate
```

`run` 默认只预演。真正执行需要同时给出 `--apply`、逐项 `--allow` 和 `--root`，没有全部同意的开关。

```powershell
# 预演：打印将要发生的每一步，不碰机器
envstation run pkg.envstation

# 执行
envstation run pkg.envstation --apply `
  --allow CAP.ENV.USER `
  --root "$env:LOCALAPPDATA\EnvStation"
```

退出码：`0` 成功 · `1` 业务失败 · `2` 用法错误 · `3` 存在阻断项。

### 写包

包是一个目录，含 `envstation.toml` 与 `workflow.toml`。

```toml
# envstation.toml
spec_version = "1.0"
id           = "x-example.python-env"
version      = "1.0.0"
name         = "Python 环境检查与 PATH 配置"
license      = "MIT"
tier         = "T0-b"

[requirements]
os   = ">=10.0.17763"
arch = ["x64", "arm64"]

[permissions]
"CAP.INSPECT"     = "检测本机是否已安装 Python，并检查 PATH 是否健康"
"CAP.PATH.MODIFY" = "经确认后把指定目录加入用户级 PATH（写入前创建快照，可回滚）"

[network]
allow = []
```

```toml
# workflow.toml
default_on_error = "rollback"

[[steps]]
id       = "detect-python"
uses     = "envstation.detect.runtime@1.0.0"
register = "py"

[steps.with]
kind        = "python"
requirement = ">=3.10"

[[steps]]
id       = "inspect-path"
uses     = "envstation.path.validate@1.0.0"
register = "pathinfo"

[steps.with]
scope = "user"
```

```powershell
envstation pack .\my-package --out .\python-env.envstation --key .\signing.priv
envstation check .\python-env.envstation --v4
```

`pack` 会自动补全动作契约哈希。完整样例见 [`samples/python-env/`](samples/python-env/)。

包只能从预制动作清单里挑动作，不能执行任意命令。没有 `exec`、`shell`、`eval`。每个动作声明所需能力，用户在界面上逐项授权。

## 安全

| 机制 | 说明 |
| --- | --- |
| 能力模型 | 动作、包清单、用户授权三者不一致即阻断 |
| 预演不可写入 | 预演模式注入只读环境实现，结构上无法改动机器 |
| 可执行文件允许列表 | 仅可信目录下的白名单程序可被调用，参数走 `ArgumentList` |
| 快照 | 写操作前自动快照，失败自动回滚 |
| 包验证 | V1 结构 → V2 静态规则 → V3 合规与指纹 → V4 沙箱试运行（核对声明与实际） |
| 签名 | ECDSA P-256 + SHA-256，不预置官方根密钥，信任锚点由用户逐包确认 |
| 审计 | 每次动作调用落盘 JSONL，按大小轮转 |
| 权限 | 主程序 `asInvoker`，不提权 |

V4 沙箱试运行会真跑一遍，但只读环境、不联网、可写根只有临时目录，然后比对包声明的能力与实际用到的能力。

## 验证

| 检查 | 结果 |
| --- | --- |
| 17 个工程构建 | 零警告（`TreatWarningsAsErrors=true`） |
| 12 个测试套件 | 345 项，通过 344，跳过 1，失败 0 |
| AOT 探针 | 命令行产物 7.19 MB（总计 40.3 MB，5 个文件） |
| 令牌一致性 | 生成的 C# 表与 `tokens.json` 同步，44 个颜色角色三主题齐全 |

性能实测：

| 指标 | 阈值 | 实测 |
| --- | --- | --- |
| 冷启动到窗口可见 | ≤ 1.5 s | 222 ms |
| 空闲内存 | ≤ 200 MB | 133.0 MB |
| 1000 项列表滚动 | 60 FPS | 60.0 FPS |
| 页面切换 | ≤ 100 ms | 首次 9.4~38.9 ms，再进入 0.4~1.9 ms |
| 主题切换 | < 200 ms | 195.9 ms / 104.7 ms |
| 图形界面发布目录 | ≤ 60 MB | 160.12 MB |

## 常见问题

**构建报 `MSB4062`，找不到 `Microsoft.Build.Packaging.Pri.Tasks.dll`**
本机没有 Visual Studio 的「通用 Windows 平台开发」工作负载。仓库已通过
`Microsoft.Windows.SDK.BuildTools.MSIX` 包绕开，若仍报此错，执行 `dotnet restore` 后重试。

**构建报文件被占用**
图形界面还在运行，锁住了 `bin` 下的 DLL。先关掉：

```powershell
Get-Process -Name 'EnvStation' -ErrorAction SilentlyContinue | Stop-Process -Force
```

**构建报 SDK 版本不满足**
`global.json` 钉的是 8.0.425。装一个 8.0.4xx 的 SDK，或改 `global.json` 的 `version`。

**`verify-m0.ps1` 报「无法加载文件，未对文件进行数字签名」**
执行策略限制。用 `powershell -ExecutionPolicy Bypass -File tools\verify-m0.ps1` 运行。

**图形界面启动报 `This application could not be started`，退出码 `-2140733418`**
缺 Windows App SDK 运行时。本仓库默认自包含发布（`WindowsAppSDKSelfContained=true`），
只有在改过该设置、或本机只装了旧版运行时（如 1.4 的 DDLM）时才会出现。

**`check` 说签名未验证**
`check` 不带 `--pubkey` 时只做包内自洽性验证，不判断信任。这是设计如此：环境站不预置任何官方根密钥。

**界面停在某一页看起来没反应**
页面构建排在渲染之后的低优先级队列上。正常情况下首帧立即提交、内容随后出现；
若长时间空白，看 `%LOCALAPPDATA%\EnvStation\logs\ui-crash.log`。

**为什么交付包里没有 exe**
当前只发布源码。安装包与代码签名在后续版本，见下方已知限制。

## 已知限制

- 无安装包（MSI/MSIX），只能从源码构建
- 无代码签名，SmartScreen 会报警告；未签名的包在他机上会被拒绝导入
- 系统级操作需要管理员权限的部分尚未独立进程化，界面上走不通
- 静态规则 22 / 46 条，其余依赖外部数据（漏洞库、镜像可用性）或需要真实执行
- 界面尚未覆盖包管理与冲突解决，这部分只在命令行
- 动作库页未做虚拟化，首次进入 38.9 ms
- 发布目录体积未达标，160 MB 中 99.7% 是 .NET 运行时与 WinUI
- 仅 Windows

## 许可证

AGPL-3.0，全文见 [LICENSE](LICENSE)。

| 用途 | 是否允许 |
| --- | --- |
| 自用、公司内部使用 | 可以 |
| 修改后自用，不再分发 | 可以 |
| 修改后分发或作为网络服务提供 | 可以，须提供完整源码并以 AGPL 授权 |
| 修改后闭源分发 | 不可以 |
| 用于闭源商业产品 | 不可以 |
| 用本项目写并分发自己的 `.envstation` 包 | 可以，包是数据，不受 AGPL 约束 |

以上是通俗说明，不构成法律意见。
