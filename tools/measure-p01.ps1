<#
.SYNOPSIS
    M0-P01 性能实测：启动、体积、内存、页面切换、主题切换、滚动帧率。

.DESCRIPTION
    为什么要有这个脚本，而不是手工测几次：
      P01 的每一条阈值都写明了测量方法（5 次取中位数、停留 60s 后取工作集、滚动 10s 数帧），
      手工测既不可复现也说不清口径。这里把口径固化进脚本，任何人重跑都得到同一套定义的数据。

    数据来源：
      · 应用自计 —— 应用内的启动探针（ENVSTATION_BOOT_LOG）在关键节点打时间戳与内存快照；
      · 外部观测 —— 本脚本从进程启动到主窗口句柄出现的外部计时（比自计多了宿主与运行时初始化）。

    被测对象是发布产物（Release + 指定 RID），不是 bin\Debug —— 性能数字必须来自最终形态。

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/measure-p01.ps1
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/measure-p01.ps1 -SkipPublish -ColdRuns 3
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [int]$ColdRuns = 5,
    [int]$IdleSeconds = 60,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'artifacts\p01'
$resultDir = Join-Path $repoRoot 'artifacts\poc-results'
$null = New-Item -ItemType Directory -Force -Path $artifactRoot, $resultDir

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { throw 'dotnet not found on PATH' }

function Write-Head([string]$text) {
    Write-Host ''
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
}

function Get-DirMetrics([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $files = Get-ChildItem $path -File -Recurse
    $bytes = ($files | Measure-Object Length -Sum).Sum
    return [pscustomobject]@{
        Files = $files.Count
        Bytes = [long]$bytes
        Mb    = [math]::Round($bytes / 1MB, 2)
    }
}

function Get-ZipMb([string]$path, [string]$zipPath) {
    if (-not (Test-Path $path)) { return 0 }
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $path '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    return $mb
}

# ── 1. 发布各变体 ───────────────────────────────────────────────────────────

$appProj = Join-Path $repoRoot 'src\EnvStation.App\EnvStation.App.csproj'
$variants = @(
    @{ Id = 'V1-selfcontained'; Note = 'NET 与 Windows App SDK 都打进目录（下载即用）'; Args = @() }
    @{ Id = 'V2-wasdk-fd';      Note = 'WASDK 依赖系统安装，NET 自包含';             Args = @('-p:WindowsAppSDKSelfContained=false') }
    @{ Id = 'V3-all-fd';        Note = '两者都依赖系统安装（最省体积）';              Args = @('--self-contained', 'false', '-p:WindowsAppSDKSelfContained=false') }
)

if (-not $SkipPublish) {
    Write-Head "Publish variants ($Configuration / $RuntimeIdentifier)"
    foreach ($v in $variants) {
        $out = Join-Path $artifactRoot $v.Id
        Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

        # V3 用 --self-contained false 覆盖前面的 --self-contained，所以参数顺序要保留。
        $argv = @('publish', $appProj, '-c', $Configuration, '-r', $RuntimeIdentifier,
                  '--nologo', '-v', 'quiet', '-o', $out)
        if ($v.Args -notcontains '--self-contained') { $argv += '--self-contained' }
        $argv += $v.Args

        $output = & $dotnet @argv 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [$($v.Id)] 发布失败：" -ForegroundColor Red
            $output | Select-Object -Last 8 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkRed }
            $v.Metrics = $null
            continue
        }

        $v.Metrics = Get-DirMetrics $out
        $v.ZipMb = Get-ZipMb $out (Join-Path $artifactRoot ($v.Id + '.zip'))
    }
} else {
    foreach ($v in $variants) {
        $out = Join-Path $artifactRoot $v.Id
        $v.Metrics = Get-DirMetrics $out
        if ($v.Metrics) { $v.ZipMb = Get-ZipMb $out (Join-Path $artifactRoot ($v.Id + '.zip')) }
    }
}

Write-Host ''
foreach ($v in $variants) {
    if ($v.Metrics) {
        Write-Host ("  {0,-18} {1,8:F2} MB   {2,4} 个文件   zip {3,7:F2} MB   {4}" -f `
            $v.Id, $v.Metrics.Mb, $v.Metrics.Files, $v.ZipMb, $v.Note) -ForegroundColor Green
    } else {
        Write-Host ("  {0,-18} （未产出）{1}" -f $v.Id, $v.Note) -ForegroundColor DarkYellow
    }
}

# ── 2. 运行态测量（用 V1，因为只有它不依赖系统预装运行时） ──────────────────

$runDir = Join-Path $artifactRoot 'V1-selfcontained'
$exePath = Join-Path $runDir 'EnvStation.exe'
if (-not (Test-Path $exePath)) { throw "被测可执行文件不存在：$exePath（先去掉 -SkipPublish 跑一次发布）" }

function Invoke-ProbeRun {
    param(
        [int]$ExitAfterMs,
        [switch]$SelfCheck
    )

    $logPath = Join-Path $env:TEMP ('envstation-p01-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.log')
    $env:ENVSTATION_BOOT_LOG = $logPath
    $env:ENVSTATION_BOOT_EXIT_MS = "$ExitAfterMs"
    $env:ENVSTATION_BOOT_SELFCHECK = if ($SelfCheck) { '1' } else { '0' }

    # 每次启动前清掉可能残留的实例，否则测到的是"第二个实例"的行为。
    Get-Process -Name 'EnvStation' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $exePath -PassThru

    # 外部观测：轮询主窗口句柄，量"进程启动 → 窗口出现"。
    $externalMs = $null
    while ($clock.Elapsed.TotalSeconds -lt 30) {
        $proc.Refresh()
        if ($proc.HasExited) { break }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $externalMs = $clock.Elapsed.TotalMilliseconds; break }
        Start-Sleep -Milliseconds 5
    }

    $waitMs = [Math]::Max($ExitAfterMs + 15000, 30000)
    $null = $proc.WaitForExit($waitMs)

    $lines = @()
    if (Test-Path $logPath) { $lines = Get-Content $logPath }
    Remove-Item $logPath -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\ENVSTATION_BOOT_LOG, Env:\ENVSTATION_BOOT_EXIT_MS, Env:\ENVSTATION_BOOT_SELFCHECK -ErrorAction SilentlyContinue

    return [pscustomobject]@{ ExternalMs = $externalMs; Lines = $lines }
}

function Get-Line([object[]]$lines, [string]$pattern) {
    $hit = $lines | Where-Object { $_ -match $pattern } | Select-Object -First 1
    if (-not $hit) { return $null }
    return ($hit -split "`t")[-1]
}

Write-Head "Cold start ($ColdRuns runs)"

$coldInternal = @()
$coldExternal = @()
for ($i = 1; $i -le $ColdRuns; $i++) {
    $r = Invoke-ProbeRun -ExitAfterMs 1200
    $internalText = Get-Line $r.Lines '窗口可见，冷启动'
    $internal = $null
    if ($internalText -match '(\d+)\s*ms') { $internal = [int]$Matches[1] }

    if ($internal) { $coldInternal += $internal }
    if ($r.ExternalMs) { $coldExternal += [math]::Round($r.ExternalMs, 1) }

    Write-Host ("  第 {0} 次：应用自计 {1} ms · 外部观测 {2} ms" -f $i, $internal, [math]::Round($r.ExternalMs, 1))
}

function Get-Median([double[]]$values) {
    if ($values.Count -eq 0) { return 0 }
    $sorted = $values | Sort-Object
    $mid = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return [math]::Round(($sorted[$mid - 1] + $sorted[$mid]) / 2, 1)
}

$medianInternal = Get-Median $coldInternal
$medianExternal = Get-Median $coldExternal
Write-Host ''
Write-Host "  中位数：应用自计 $medianInternal ms · 外部观测 $medianExternal ms" -ForegroundColor Green

Write-Head "Idle memory ($IdleSeconds s on the home page)"

$idleRun = Invoke-ProbeRun -ExitAfterMs ($IdleSeconds * 1000)
$visibleMem = Get-Line $idleRun.Lines '窗口可见：工作集'
$idleMem = Get-Line $idleRun.Lines '稳定后：工作集'
Write-Host "  窗口可见时：$visibleMem"
Write-Host "  停留 $IdleSeconds s 后：$idleMem" -ForegroundColor Green

Write-Head 'Page switch / theme switch / scroll frame rate'

$selfRun = Invoke-ProbeRun -ExitAfterMs 9000 -SelfCheck
$pageSwitches = @{}
foreach ($line in $selfRun.Lines) {
    if ($line -match '页面切换 -> (\w+)：([\d.]+) ms') { $pageSwitches[$Matches[1]] = [double]$Matches[2] }
}

$themeLines = $selfRun.Lines | Where-Object { $_ -match '主题切换 ->' }
$fpsLine = Get-Line $selfRun.Lines '滚动帧率：'

foreach ($k in $pageSwitches.Keys) { Write-Host ("  页面切换 {0,-10} {1,7:F1} ms" -f $k, $pageSwitches[$k]) }
foreach ($line in $themeLines) { Write-Host ("  " + ($line -split "`t")[-1]) }
Write-Host "  $fpsLine" -ForegroundColor Green

# ── 3. 汇总落盘 ────────────────────────────────────────────────────────────

$result = [pscustomobject]@{
    generatedAt   = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    configuration = $Configuration
    rid           = $RuntimeIdentifier
    variants      = @($variants | ForEach-Object {
        [pscustomobject]@{
            id      = $_.Id
            note    = $_.Note
            mb      = if ($_.Metrics) { $_.Metrics.Mb } else { $null }
            files   = if ($_.Metrics) { $_.Metrics.Files } else { $null }
            zipMb   = $_.ZipMb
        }
    })
    coldStart     = [pscustomobject]@{
        runs           = $ColdRuns
        internalMs     = $coldInternal
        externalMs     = $coldExternal
        medianInternal = $medianInternal
        medianExternal = $medianExternal
    }
    memory        = [pscustomobject]@{
        idleSeconds = $IdleSeconds
        atVisible   = $visibleMem
        afterIdle   = $idleMem
    }
    pageSwitchMs  = $pageSwitches
    themeSwitch   = @($themeLines | ForEach-Object { ($_ -split "`t")[-1] })
    frameRate     = $fpsLine
}

$jsonPath = Join-Path $resultDir 'P01.json'
$result | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding UTF8
Write-Host ''
Write-Host "结果已写入：$jsonPath" -ForegroundColor Gray
exit 0
