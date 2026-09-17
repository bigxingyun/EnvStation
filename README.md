# 环境站 EnvStation

Windows 开发环境配置与回滚工具。装语言、配环境变量、理 PATH、解命令冲突，每一步都可预演、可拒绝、可回滚。

[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](LICENSE)
[![Tests](https://img.shields.io/badge/tests-370%20passing-brightgreen.svg)](#验证)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%201809%2B%20%7C%2011-lightgrey.svg)](#环境要求)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

> **开发中，无发布版本，无安装包，无代码签名。** 内核、命令行与界面均可用，370 项用例通过。
> 安装包与代码签名尚未提供。环境站会修改环境变量，请在非关键机器或虚拟机上先行验证。

## 功能

| 能力 | 说明 |
| --- | --- |
| 环境检测 | 系统版本、磁盘、用户级与系统级 PATH、前置依赖、同名命令冲突 |
| 环境变量与 PATH | 读写、去重、清理失效条目、diff、快照回滚 |
| 包管理 | 导入第三方 `.envstation` 包，经校验与能力授权后执行 |
| 快照与回滚 | 写操作前自动快照，失败自动回滚 |
| 预制动作 | 74 个动作，覆盖探测、下载、解压、安装、环境变量、PATH、配置与镜像源、验证、清理 |

界面含概览、环境检测、导入包、快照与回滚、动作库、设置六个页面。
命令行提供 `doctor`、`check`、`pack`、`run`、`env`、`path` 等命令，功能覆盖完整。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809（build 17763）或更高；Windows 11 |
| CPU 架构 | x64 或 ARM64 |
| .NET SDK | 8.0.425 或更高的 8.0.x（`global.json` 已固定版本，低版本将直接报错） |
| 磁盘 | 构建约需 1 GB；界面自包含发布目录 160 MB |
| 网络 | 首次构建需从 NuGet 还原 Windows App SDK、SDK 构建工具、MSIX 工具链 |
| 管理员权限 | 不需要。系统级环境变量的写入在当前版本不可用 |

## 构建与运行

```powershell
git clone https://github.com/bigxingyun/EnvStation.git
cd EnvStation
dotnet build EnvStation.sln -c Release

# 只读检测，不做任何写入
dotnet run --project src\EnvStation.Cli -c Release -- doctor

# 图形界面
dotnet run --project src\EnvStation.App -c Release
```

图形界面不接受命令行参数，启动后进入概览页。命令行是主要入口。

全量验证（17 个工程构建 + 12 个套件 + AOT 探针 + 一致性检查，约 10 分钟）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m0.ps1
```

构建启用 `TreatWarningsAsErrors`，存在警告即视为失败。

## 使用

### 环境检测

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

上例为开发机实际输出。`!` 表示动作执行成功但该项存在问题：缺少前置依赖、PATH 含失效条目、
同名命令冲突均属此类。这类问题不会导致命令报错，只会造成命令间歇性失效，因此单独标注。

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

`run` 默认仅预演，打印全部将要执行的步骤而不做任何修改。实际执行需同时提供三项授权：

```powershell
envstation run pkg.envstation --apply `
  --allow CAP.ENV.USER `
  --root "$env:LOCALAPPDATA\EnvStation"
```

不提供「全部同意」类开关。

退出码：`0` 成功 · `1` 业务失败 · `2` 用法错误 · `3` 存在阻断项。

### 编写自动化包

包为一个目录，含 `envstation.toml`（声明）与 `workflow.toml`（流程）。
权限清单面向用户阅读，每项须以自然语言书写：

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

流程为声明式，动作取自预制清单，不支持脚本：

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

打包与校验：

```powershell
envstation pack .\my-package --out .\python-env.envstation --key .\signing.priv
envstation check .\python-env.envstation --v4
```

`pack` 自动补全动作契约哈希，无需手工填写。完整示例见 [`samples/python-env/`](samples/python-env/)。

## 安全设计

**第三方包不能执行任意命令。** 不提供 `exec`、`shell`、`eval`，包只能从预制动作清单中编排。
若允许任意命令，导入第三方包这一功能在实质上即构成远程代码执行通道；
包来源不可控，其作者亦不可控。

| 机制 | 说明 |
| --- | --- |
| 能力模型 | 动作声明、包清单声明、用户授权三者不一致即阻断 |
| 预演不可写入 | 预演模式注入只读环境实现，结构上无法修改系统 |
| 可执行文件允许列表 | 仅可信目录下的白名单程序可被调用，参数经 `ArgumentList` 传递 |
| 快照 | 写操作前自动快照，失败自动回滚 |
| 包验证 | V1 结构 → V2 静态规则 → V3 合规与指纹 → V4 沙箱试运行 |
| 签名 | ECDSA P-256 + SHA-256；不预置官方根密钥，信任锚点由用户逐包确认 |
| 审计 | 每次动作调用落盘 JSONL，按大小轮转 |
| 权限 | 主程序以 `asInvoker` 运行，不主动请求提权 |

V4 沙箱试运行在隔离环境中实际执行一遍：只读环境实现、不注入网络、可写根仅限临时目录。
执行完毕后比对包声明的能力与实际调用的能力。声明而未使用仅记为浪费；
**使用而未声明则直接阻断**。

## 项目结构

```
src/EnvStation.Abstractions/   契约：接口、模型、错误码，无第三方依赖
src/EnvStation.Core/           内核：环境变量、PATH、快照事务、动作层、工作流、包验证
src/EnvStation.Cli/            命令行入口，Native AOT
src/EnvStation.App/            图形界面，WinUI 3
tests/                         12 个测试套件 + TestKit + AOT 探针
design/tokens.json             设计令牌唯一来源；generate-tokens.ps1 生成 C# 表
samples/python-env/            示例自动化包
tools/                         verify-m0.ps1 / measure-p01.ps1 / capture-ui.ps1 等
```

## 配置

无需配置文件即可运行。以下文件仅在需要时修改：

| 文件 | 作用 |
| --- | --- |
| `global.json` | 固定 .NET SDK 版本（8.0.425，`rollForward: latestFeature`） |
| `Directory.Build.props` | 全仓库构建基线：TFM、语言版本、警告即错误、版本号、AOT 基线 |
| `src/EnvStation.App/EnvStation.App.csproj` | 界面打包方式（自包含、非打包运行、PRI 工具链） |

运行期数据目录为 `%LOCALAPPDATA%\EnvStation`：

| 子目录 | 内容 |
| --- | --- |
| `audit/` | 审计日志 JSONL，按大小轮转 |
| `snapshots/` | 环境变量与配置快照 |
| `logs/` | 界面未处理异常日志 |

包与工作流在各自目录内声明，不读取全局配置。

## 验证

| 检查 | 结果 |
| --- | --- |
| 17 个工程构建 | 零警告（`TreatWarningsAsErrors=true`） |
| 12 个测试套件 | 370 项，通过 365，跳过 5，失败 0 |
| AOT 探针 | 命令行产物 7.19 MB（总计 40.3 MB，5 个文件） |
| 令牌一致性 | 生成的 C# 表与 `tokens.json` 同步；44 个颜色角色三主题齐全 |

性能实测（`tools/measure-p01.ps1`，测量机为开发机，数据偏乐观）：

| 指标 | 阈值 | 实测 |
| --- | --- | --- |
| 冷启动到窗口可见 | ≤ 1.5 s | 222 ms |
| 空闲内存 | ≤ 200 MB | 133 MB |
| 1000 项列表滚动 | 60 FPS | 60.0 FPS |
| 页面切换 | ≤ 100 ms | 首次 9.4~38.9 ms，再进入 0.4~1.9 ms |
| 主题切换 | < 200 ms | 196 ms / 105 ms |
| 命令行 AOT 产物 | — | 7.19 MB（总计 40.3 MB，5 个文件） |
| 界面发布目录 | ≤ 60 MB | 160 MB，**未达标** |

最后一项未达标的原因：该目录中 99.7% 为 .NET 自包含运行时与 WinUI 本身，非应用代码。
可选路径为调整口径或更换交付形态。

页面切换一项的排查过程值得记录：帧率、空闲占用、窗口缩放、DPI 逐项测量后均被排除，
实际问题在切页路径——整页同步构建占用 UI 线程 12~34 ms，而单帧预算为 16.7 ms。
且原测量口径将该耗时并入笼统的「页面切换耗时」，因而显示为达标。详见性能报告第 2.1 节。

## 常见问题

**构建报 `MSB4062`，找不到 `Microsoft.Build.Packaging.Pri.Tasks.dll`**
本机缺少 Visual Studio 的通用 Windows 平台开发工作负载。仓库已通过
`Microsoft.Windows.SDK.BuildTools.MSIX` 包规避；若仍报错，执行 `dotnet restore` 后重试。

**构建报文件被占用**
图形界面仍在运行，占用 `bin` 下的 DLL。先结束进程：

```powershell
Get-Process -Name 'EnvStation' -ErrorAction SilentlyContinue | Stop-Process -Force
```

**构建报 SDK 版本不满足**
`global.json` 固定为 8.0.425。安装任一 8.0.4xx 版本，或修改该文件的 `version`。

**`verify-m0.ps1` 报未对文件进行数字签名**
受执行策略限制，以如下方式运行：
`powershell -ExecutionPolicy Bypass -File tools\verify-m0.ps1`

**图形界面启动报 `This application could not be started`，退出码 `-2140733418`**
缺少 Windows App SDK 运行时。默认配置为自包含发布（`WindowsAppSDKSelfContained=true`），
仅在修改该设置或本机仅安装旧版运行时（如 1.4 的 DDLM）时出现。

**`check` 报告签名未验证**
未提供 `--pubkey` 时仅验证包内自洽性，不判断信任。环境站不预置任何官方根密钥，此为设计如此。

**界面某一页长时间无内容**
页面构建排在渲染之后的低优先级队列，正常情况下首帧立即呈现、内容随后填充。
若持续空白，查看 `%LOCALAPPDATA%\EnvStation\logs\ui-crash.log`。

## 已知限制

- 无安装包（MSI/MSIX），仅能从源码构建
- 无代码签名，Windows SmartScreen 将给出警告；未签名的包在其他机器上会被拒绝导入
- 需要管理员权限的系统级操作尚未独立进程化，界面上不可用
- 静态规则实现 22 / 46 条，其余依赖外部数据（漏洞库、镜像可用性）或需真实执行
- 界面尚未覆盖包管理与冲突解决，该部分仅在命令行提供
- 动作库页未做虚拟化，首次进入耗时 38.9 ms
- 界面发布目录体积未达标
- 仅支持 Windows

## 许可证

AGPL-3.0，全文见 [LICENSE](LICENSE)。

选择该许可证的原因：环境站会修改系统变量，并可执行第三方提供的包。
AGPL 要求修改后的版本公开源码，避免其被闭源后用于锁定用户。
此类工具依赖用户信任，而信任的前提是内部实现可被审计。

| 用途 | 是否允许 |
| --- | --- |
| 自用、公司内部使用 | 允许 |
| 修改后自用，不再分发 | 允许 |
| 修改后分发或作为网络服务提供 | 允许，须提供完整源码并以 AGPL 授权 |
| 修改后闭源分发 | 不允许 |
| 用于闭源商业产品 | 不允许 |
| 使用环境站编写并分发 `.envstation` 包 | 允许；包属数据，不受 AGPL 约束 |

以上为通俗说明，不构成法律意见。
