# 环境站 EnvStation

Windows 上装运行时、改环境变量、整理 PATH、处理同名命令冲突。默认只预演，确认后才写入；写之前建快照，失败可回滚。

[![Build](https://img.shields.io/badge/build-passing-brightgreen?logo=github)](https://github.com/bigxingyun/EnvStation/actions)
[![Version](https://img.shields.io/badge/release-v0.0.1-blue)](https://github.com/bigxingyun/EnvStation/releases/tag/v0.0.1)
[![License](https://img.shields.io/badge/license-AGPL--3.0-blue)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%201809%2B%20%7C%2011%20(x64%2FARM64)-0078D6?logo=windows)](https://www.microsoft.com/windows)

> 当前版本 **v0.0.1**。没有 MSI/MSIX，也没有代码签名；[Releases](https://github.com/bigxingyun/EnvStation/releases/tag/v0.0.1) 里是可直接跑的 zip。能改本机环境，建议先在虚拟机或闲置机器上试用。

## 目录

- [能做什么](#能做什么)
- [下载](#下载)
- [环境要求](#环境要求)
- [从源码构建](#从源码构建)
- [快速上手](#快速上手)
- [如何编写包](#如何编写包)
- [项目结构](#项目结构)
- [已知限制](#已知限制)
- [许可证](#许可证)

## 能做什么

| 能力 | 说明 |
| --- | --- |
| 环境检测 | 系统、磁盘、PATH、依赖、同名命令冲突 |
| 环境变量 / PATH | 查看、清理失效项、去重、校验；用户级可写 |
| 快照与回滚 | 写操作前自动快照，界面与命令行都可恢复 |
| 运行时 | 扫描本机工具链；常见缺失项可走 winget 等安装 |
| 包 | 用 TOML 声明步骤，校验后预演或执行；不能跑任意脚本 |
| 动作库 | 约 74 个预制动作（探测、下载、安装、PATH、配置等） |

写操作规则：

1. 默认预演，加 `--apply`（或界面里确认）才真正改。
2. 能力用 `--allow` / 勾选框逐项授权，没有「全部同意」。
3. 第三方包只能编排预制动作，没有 `exec` / `shell`。

界面（WinUI 3）和命令行（Native AOT）共用同一套内核。日常改环境两边都能做；写包、批处理更适合命令行。

### 界面预览

| 环境检测 | 动作库 |
| --- | --- |
| ![环境检测 · 浅色](docs/screenshots/doctor-light.png) | ![动作库 · 浅色](docs/screenshots/actions-light.png) |
| ![环境检测 · 深色](docs/screenshots/doctor-dark.png) | ![导入包 · 深色](docs/screenshots/packages-dark.png) |

## 下载

从 [v0.0.1](https://github.com/bigxingyun/EnvStation/releases/tag/v0.0.1) 拿 zip，解压就能跑，不用先装 .NET。

| 文件 | 内容 |
| --- | --- |
| `EnvStation-0.0.1-win-x64.zip` | 图形界面，解压后打开 `EnvStation.exe` |
| `envstation-cli-0.0.1-win-x64.zip` | 命令行，解压后运行 `envstation.exe` |

Windows 可能提示未签名，选「仍要运行」即可。ARM64 本机请从源码构建。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809+ / Windows 11，x64 或 ARM64 |
| SDK | .NET 8.0.425+（见 `global.json`） |
| 磁盘 | 构建大约 1 GB；界面自包含发布约 160 MB |
| 管理员 | 不需要；当前版本不能写系统级环境变量 |

数据目录：`%LOCALAPPDATA%\EnvStation`（`audit/`、`snapshots/`、`logs/`）。

## 从源码构建

```powershell
git clone https://github.com/bigxingyun/EnvStation.git
cd EnvStation
dotnet --version          # 需要 8.0.4xx
dotnet restore EnvStation.sln
dotnet build EnvStation.sln -c Release
```

警告按错误处理（`TreatWarningsAsErrors`）。

发布：

```powershell
dotnet publish src\EnvStation.Cli -c Release -r win-x64
dotnet publish src\EnvStation.App -c Release -r win-x64
```

自检与全量验证：

```powershell
dotnet run --project src\EnvStation.Cli -c Release -- doctor
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m0.ps1
```

<details>
<summary>常见问题</summary>

- **`MSB4062` / 找不到 Pri.Tasks**：先 `dotnet restore`；仓库已带 MSIX 构建包。
- **SDK 版本不对**：按 `global.json` 装 8.0.4xx，或改其中的 `version`。
- **文件被占用**：关掉正在跑的环境站窗口。
- **脚本「未数字签名」**：用上面的 `-ExecutionPolicy Bypass`。
- **界面起不来（App SDK）**：用默认自包含发布，或安装对应 Windows App SDK 运行时。

</details>

## 快速上手

### 检测

```powershell
dotnet run --project src\EnvStation.Cli -c Release -- doctor
```

`!` 表示检测本身成功，但该项有问题（缺依赖、PATH 脏、命令冲突等）。

### 图形界面

```powershell
dotnet run --project src\EnvStation.App -c Release
```

### 命令一览

```
doctor
actions [--json]
check <包> [--pubkey <公钥>] [--v4]
pack <源目录> [--out <文件>] [--key <私钥>]
keygen [--out <目录>]
fingerprint <公钥>
run <包> [--apply] [--allow <能力ID>] [--root <目录>]
env  get|set|unset|diff|restore|validate
path list|ensure|remove|clean|validate
install <包ID> [--manager winget|scoop|choco] [--apply]
```

退出码：`0` 成功，`1` 业务失败，`2` 用法错误，`3` 有阻断项。

### 跑一个示例包

仓库里有两份示例：

- [`samples/python-env/`](samples/python-env/)：只检测 Python 和 PATH  
- [`samples/python-install/`](samples/python-install/)：用 winget 装 Python 3.12  

```powershell
$cli = "src\EnvStation.Cli"

dotnet run --project $cli -c Release -- pack .\samples\python-env --out .\python-env.envstation
dotnet run --project $cli -c Release -- check .\python-env.envstation --v4
dotnet run --project $cli -c Release -- run .\python-env.envstation
```

确认无误后再执行（三项都要给齐）：

```powershell
dotnet run --project $cli -c Release -- run .\python-env.envstation `
  --apply `
  --allow CAP.INSPECT `
  --allow CAP.PATH.MODIFY `
  --root "$env:LOCALAPPDATA\EnvStation"
```

- `--apply`：真正执行  
- `--allow`：授权能力，可重复  
- `--root`：允许写入的根目录  

不传 `--pubkey` 时只检查包结构是否自洽，不建立信任锚。需要验签时再带上公钥。

## 如何编写包

包就是一个目录，最少两个文件：

| 文件 | 作用 |
| --- | --- |
| `envstation.toml` | 包身份、系统要求、权限说明 |
| `workflow.toml` | 按顺序调用哪些动作 |

不支持在包里写脚本或任意命令；步骤只能引用动作库里的 ID（`envstation actions` 可列出）。

### 1. 建目录

```powershell
mkdir my-python-check
cd my-python-check
```

### 2. 写 `envstation.toml`

```toml
spec_version = "1.0"
id           = "x-example.python-check"   # 建议 x-<作者>.<名字>
version      = "1.0.0"
name         = "Python 与 PATH 检查"
license      = "MIT"
tier         = "T0-b"

[requirements]
os   = ">=10.0.17763"
arch = ["x64", "arm64"]

# 权限说明给人看：界面勾选、命令行 --allow 都用这里的键
[permissions]
"CAP.INSPECT"     = "检测是否已安装 Python，并检查用户 PATH"
"CAP.PATH.MODIFY" = "必要时调整用户级 PATH（会先建快照）"

[network]
allow = []    # 需要下载时再填允许的主机；没有网络步骤就留空
```

`permissions` 里声明的能力，必须盖住工作流实际会用到的动作。多声明没事，少声明会在校验或试运行时被拦住。

### 3. 写 `workflow.toml`

```toml
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

[[steps]]
id   = "notify"
uses = "envstation.ui.notify@1.0.0"

[steps.with]
level   = "info"
message = "Python：${pkg.py.found}（${pkg.py.version}）；PATH 问题数：${pkg.pathinfo.problem_count}"
```

要点：

- `uses` 必须带版本，例如 `@1.0.0`  
- `register` 把该步输出挂到 `pkg.<名字>.*`，后面步骤和提示文案可以引用  
- `default_on_error = "rollback"`：任一步失败就回滚到进入包之前  
- 参数名、取值范围以 `envstation actions` 里该动作的说明为准  

需要装软件时，常见写法是 `envstation.pkg.install`（winget / scoop / choco），可参考 [`samples/python-install/`](samples/python-install/)。

### 4. 打包、校验、预演

```powershell
# 可选：生成签名密钥
dotnet run --project src\EnvStation.Cli -c Release -- keygen --out .\keys

# 打包（会自动补全动作契约哈希）
dotnet run --project src\EnvStation.Cli -c Release -- pack . --out ..\my-python-check.envstation
# 若要签名：再加上 --key .\keys\signing.priv

# 结构 + 规则校验；加 --v4 会在沙箱里真跑一遍，核对「声明能力 vs 实际能力」
dotnet run --project src\EnvStation.Cli -c Release -- check ..\my-python-check.envstation --v4

# 预演（不改机器）
dotnet run --project src\EnvStation.Cli -c Release -- run ..\my-python-check.envstation
```

沙箱试运行时：不联网、可写区只有临时目录。声明了却没用到的能力只记浪费；**用了却没声明的能力会直接失败**。

### 5. 真正执行

```powershell
dotnet run --project src\EnvStation.Cli -c Release -- run ..\my-python-check.envstation `
  --apply `
  --allow CAP.INSPECT `
  --allow CAP.PATH.MODIFY `
  --root "$env:LOCALAPPDATA\EnvStation"
```

在图形界面里：打开「导入包」→ 选 `.envstation` → 校验 → 勾选能力 → 预演或执行。

### 6. 编写时注意

- 权限文案写清楚「会改什么」，同事才敢勾选。  
- 尽量只申请用户级能力；系统级写入当前版本界面也不支持。  
- 改 PATH / 环境变量前，内核会建快照；出问题可在「快照与回滚」或 `env restore` 恢复。  
- 装完软件后提示用户新开终端，旧会话里的 PATH 不会自动刷新。  
- 完整动作列表：`dotnet run --project src\EnvStation.Cli -c Release -- actions`

## 项目结构

```
src/EnvStation.Abstractions/   契约与错误码
src/EnvStation.Core/           内核（环境、PATH、快照、动作、工作流、验包）
src/EnvStation.Cli/            命令行
src/EnvStation.App/            图形界面
tests/                         测试
samples/                       示例包
design/tokens.json             设计令牌
tools/                         验证与测量脚本
```

## 已知限制

- 没有 MSI/MSIX，没有代码签名；[Releases](https://github.com/bigxingyun/EnvStation/releases) 提供 zip  
- 不能写系统级环境变量（无提权子进程）  
- 安装依赖本机包管理器和网络  
- 部分静态规则依赖外部数据，尚未全部落地  
- 仅 Windows  

## 许可证

[AGPL-3.0](LICENSE)。改完再分发或做成网络服务时，需要按 AGPL 开源。

用环境站写出来的 `.envstation` 包本身是数据，不受 AGPL 约束，可按你自己的许可证分发。

| 用途 | 是否允许 |
| --- | --- |
| 自用 / 公司内部 | 可以 |
| 修改后自用、不分发 | 可以 |
| 修改后分发或提供网络服务 | 可以，须按 AGPL 提供源码 |
| 修改后闭源分发或并入闭源产品 | 不可以 |
| 编写并分发 `.envstation` 包 | 可以 |

以上是通俗说明，以 [LICENSE](LICENSE) 为准。
