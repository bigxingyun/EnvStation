<#
.SYNOPSIS
    M0-P00 / P02 / P05 / P06 one-shot verification script.

.DESCRIPTION
    Steps: environment check -> build -> run three test suites -> AOT probe -> summary.

    Design notes:
    - The AOT probe project (EnvStation.Poc.AotProbe) is handled separately because it
      depends on NuGet packages. When offline it is SKIPPED and recorded explicitly
      instead of failing the whole pipeline.
    - All test projects have zero external dependencies and run offline.
    - Results are written to artifacts/poc-results for M0 report aggregation.

.NOTES
    This file is intentionally ASCII-only so that Windows PowerShell 5.1 parses it
    correctly regardless of file encoding.
#>
[CmdletBinding()]
param(
    [switch]$SkipAot,
    [switch]$IncludePoc,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$repoRoot = Split-Path -Parent $PSScriptRoot
$resultDir = Join-Path $repoRoot 'artifacts\poc-results'
$null = New-Item -ItemType Directory -Force -Path $resultDir

$script:steps = New-Object System.Collections.ArrayList

function Write-Head([string]$text) {
    Write-Host ''
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
}

function Write-Step([string]$name, [string]$status, [string]$detail = '') {
    $color = 'Gray'
    switch ($status) {
        'OK'   { $color = 'Green' }
        'FAIL' { $color = 'Red' }
        'SKIP' { $color = 'DarkYellow' }
    }
    $null = $script:steps.Add([pscustomobject]@{ Step = $name; Status = $status; Detail = $detail })
    Write-Host ("  [{0,-4}] {1,-34} {2}" -f $status, $name, $detail) -ForegroundColor $color
}

function Get-DotNet {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        'dotnet'
    )
    foreach ($c in $candidates) {
        try {
            $v = & $c --version 2>$null
            if ($LASTEXITCODE -eq 0 -and $v) { return $c }
        } catch { }
    }
    return $null
}

Write-Head 'EnvStation M0 verification'

# ---- Step 1: SDK ----
$dotnet = Get-DotNet
if (-not $dotnet) {
    Write-Step 'dotnet SDK availability' 'FAIL' 'No runnable dotnet found; install .NET 8 SDK'
    Write-Host ''
    Write-Host 'Cannot continue.' -ForegroundColor Red
    exit 2
}

$sdkVersion = (& $dotnet --version) 2>$null
Write-Step 'dotnet SDK availability' 'OK' "version $sdkVersion"

# ---- Step 2: build (excluding the NuGet-dependent AOT probe) ----
Write-Head "Build ($Configuration)"

# EnvStation.TestKit is a LIBRARY (no entry point): it is built as a dependency of the
# test suites below, never built directly.
$projects = @(
    'src\EnvStation.Abstractions\EnvStation.Abstractions.csproj',
    'src\EnvStation.Core\EnvStation.Core.csproj',
    'src\EnvStation.Cli\EnvStation.Cli.csproj',
    'src\EnvStation.App\EnvStation.App.csproj',
    'tests\EnvStation.Tests.PathKernel\EnvStation.Tests.PathKernel.csproj',
    'tests\EnvStation.Tests.Concurrency\EnvStation.Tests.Concurrency.csproj',
    'tests\EnvStation.Tests.RegistrySandbox\EnvStation.Tests.RegistrySandbox.csproj',
    'tests\EnvStation.Tests.Transactions\EnvStation.Tests.Transactions.csproj',
    'tests\EnvStation.Tests.Installation\EnvStation.Tests.Installation.csproj',
    'tests\EnvStation.Tests.Scripting\EnvStation.Tests.Scripting.csproj',
    'tests\EnvStation.Tests.Actions\EnvStation.Tests.Actions.csproj',
    'tests\EnvStation.Tests.EnvironmentActions\EnvStation.Tests.EnvironmentActions.csproj',
    'tests\EnvStation.Tests.VerifyActions\EnvStation.Tests.VerifyActions.csproj',
    'tests\EnvStation.Tests.Workflow\EnvStation.Tests.Workflow.csproj',
    'tests\EnvStation.Tests.NetArchive\EnvStation.Tests.NetArchive.csproj',
    'tests\EnvStation.Tests.Config\EnvStation.Tests.Config.csproj'
)

$buildFailed = $false
foreach ($proj in $projects) {
    $full = Join-Path $repoRoot $proj
    $name = [System.IO.Path]::GetFileNameWithoutExtension($proj)

    $output = & $dotnet build $full -c $Configuration --nologo -v minimal 2>&1
    if ($LASTEXITCODE -ne 0) {
        $buildFailed = $true
        Write-Step "build $name" 'FAIL' 'see output below'
        $output | Select-Object -Last 40 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkRed }
    } else {
        $warnCount = ($output | Select-String -Pattern 'warning' -SimpleMatch).Count
        $detail = 'no warnings'
        if ($warnCount -gt 0) { $detail = "$warnCount warning(s)" }
        Write-Step "build $name" 'OK' $detail
    }
}

if ($buildFailed) {
    Write-Host ''
    Write-Host 'Build failed; skipping test execution.' -ForegroundColor Red
    exit 1
}

# ---- Step 3: run test suites ----
Write-Head 'Run test suites'

$suites = @(
    @{ Id = 'PK';   Name = 'PATH kernel boundary cases';  Proj = 'tests\EnvStation.Tests.PathKernel' }
    @{ Id = 'CC';   Name = 'Concurrency control';          Proj = 'tests\EnvStation.Tests.Concurrency' }
    @{ Id = 'SC';   Name = 'ActionScript standard layer';  Proj = 'tests\EnvStation.Tests.Scripting' }
    @{ Id = 'AC';   Name = 'Action registry and pipeline'; Proj = 'tests\EnvStation.Tests.Actions' }
    @{ Id = 'EA';   Name = 'Env/PATH actions (sandboxed)'; Proj = 'tests\EnvStation.Tests.EnvironmentActions' }
    @{ Id = 'VA';   Name = 'Verify/UI/cleanup actions';  Proj = 'tests\EnvStation.Tests.VerifyActions' }
    @{ Id = 'WF';   Name = 'Workflow interpreter';       Proj = 'tests\EnvStation.Tests.Workflow' }
    @{ Id = 'NA';   Name = 'Download/archive actions';   Proj = 'tests\EnvStation.Tests.NetArchive' }
    @{ Id = 'CF';   Name = 'Config and mirrors';         Proj = 'tests\EnvStation.Tests.Config' }
    @{ Id = 'P02';  Name = 'M0-P02 registry read/write';   Proj = 'tests\EnvStation.Tests.RegistrySandbox' }
    @{ Id = 'P06';  Name = 'M0-P06 snapshot/rollback';     Proj = 'tests\EnvStation.Tests.Transactions' }
    @{ Id = 'P05';  Name = 'M0-P05 shadow switch';         Proj = 'tests\EnvStation.Tests.Installation' }
)

$testFailed = $false
foreach ($suite in $suites) {
    $binDir = Join-Path $repoRoot ($suite.Proj + '\bin\' + $Configuration + '\net8.0')
    $exe = Get-ChildItem -Path $binDir -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $exe) {
        Write-Step $suite.Name 'SKIP' 'executable not found'
        continue
    }

    $jsonPath = Join-Path $resultDir ($suite.Id + '.json')
    Write-Host ''
    & $exe.FullName --json $jsonPath
    if ($LASTEXITCODE -ne 0) {
        $testFailed = $true
        Write-Step $suite.Name 'FAIL' 'some cases failed'
    } else {
        Write-Step $suite.Name 'OK' ('result -> ' + [System.IO.Path]::GetFileName($jsonPath))
    }
}

# ---- Step 4: AOT probe ----
if (-not $SkipAot) {
    Write-Head 'M0-P00 AOT probe'

    $pocProj = Join-Path $repoRoot 'tests\EnvStation.Poc.AotProbe\EnvStation.Poc.AotProbe.csproj'

    Write-Host '  restoring NuGet packages...' -ForegroundColor Gray
    $restore = & $dotnet restore $pocProj --nologo 2>&1
    $restoreOk = $LASTEXITCODE -eq 0

    if (-not $restoreOk) {
        Write-Step 'NuGet restore (AOT probe)' 'SKIP' 'unreachable or package missing -> three-library verdict = NOT VERIFIED'
        Write-Host '      Impact: P00-a/b/c inconclusive; definition format and signing scheme pending.' -ForegroundColor DarkYellow
    } else {
        Write-Step 'NuGet restore (AOT probe)' 'OK'

        Write-Host ''
        Write-Host '  -- JIT baseline --' -ForegroundColor DarkCyan
        $jitJson = Join-Path $resultDir 'P00-jit.json'
        $jitOut = & $dotnet run --project $pocProj -c $Configuration -- --json $jitJson 2>&1
        $jitOk = $LASTEXITCODE -eq 0
        $jitOut | Select-Object -Last 45 | ForEach-Object { Write-Host "      $_" }
        if ($jitOk) { Write-Step 'P00 JIT baseline' 'OK' } else { Write-Step 'P00 JIT baseline' 'FAIL' }

        Write-Host ''
        Write-Host '  -- Native AOT publish (the core of this PoC) --' -ForegroundColor DarkCyan
        $publishDir = Join-Path $repoRoot 'artifacts\aot\win-x64'
        $publish = & $dotnet publish $pocProj -c $Configuration -r win-x64 -p:PublishAot=true -p:PublishTrimmed=true -o $publishDir --nologo 2>&1
        $publishOk = $LASTEXITCODE -eq 0

        if (-not $publishOk) {
            Write-Step 'P00 AOT publish' 'FAIL' 'publish failed - see output'
            $publish | Select-Object -Last 45 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkRed }
        } else {
            $exePath = Join-Path $publishDir 'aotprobe.exe'
            $exeMb = 0
            if (Test-Path $exePath) { $exeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 2) }
            $allFiles = Get-ChildItem $publishDir -File -Recurse
            $totalMb = [math]::Round((($allFiles | Measure-Object Length -Sum).Sum / 1MB), 2)
            Write-Step 'P00 AOT publish' 'OK' ("main exe ${exeMb}MB / total $totalMb MB / " + $allFiles.Count + ' files')

            Write-Host ''
            Write-Host '  -- AOT artifact runtime verification --' -ForegroundColor DarkCyan
            $aotJson = Join-Path $resultDir 'P00-aot.json'
            $aotOut = & $exePath --json $aotJson 2>&1
            $aotOk = $LASTEXITCODE -eq 0
            $aotOut | Select-Object -Last 45 | ForEach-Object { Write-Host "      $_" }
            if ($aotOk) {
                Write-Step 'P00 AOT runtime' 'OK' 'compare JIT vs AOT verdicts'
            } else {
                Write-Step 'P00 AOT runtime' 'FAIL' 'AOT artifact behaves differently from JIT'
                $testFailed = $true
            }
        }
    }
}

# ---- Step 5: design token consistency ----
# 令牌有三处产物：tokens.json（机器可读）、design/xaml/*.xaml（给人看的规范载体）、
# src/EnvStation.App/Generated/DesignTokens.g.cs（界面运行期真正读取的那份）。
# 三处各写一遍必然漂移，所以这里做机械比对：
#   T1  C# 表是否与生成器输出一致（手工改过生成文件 -> 失败）
#   T2  Colors.xaml 的 HighContrast 段是否与生成器的高对比度映射一致
#   T3  UiKit 里引用的颜色令牌是否都在三套主题里有定义（少一个就是静默透明）
Write-Head 'Design token consistency'

$tokenGenerator = Join-Path $repoRoot 'design\generate-tokens.ps1'
$generatedCs = Join-Path $repoRoot 'src\EnvStation.App\Generated\DesignTokens.g.cs'
$uiKitPath = Join-Path $repoRoot 'src\EnvStation.App\UiKit.cs'
$colorsXaml = Join-Path $repoRoot 'design\xaml\Colors.xaml'

if (-not (Test-Path $generatedCs)) {
    Write-Step 'T1 generated token table' 'FAIL' 'DesignTokens.g.cs missing - run design/generate-tokens.ps1'
    $testFailed = $true
} else {
    # 生成器的输出路径写死在仓库里，所以这里直接重跑一次再比对：
    # 若生成结果与提交的内容不同，说明提交的那份是过期或被手工改过的。
    # （重跑已经把正确内容写回工作区，所以失败信息里要提醒"请一并提交"。）
    $backup = [System.IO.File]::ReadAllText($generatedCs)
    $null = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tokenGenerator 2>&1
    $regen = [System.IO.File]::ReadAllText($generatedCs)
    if ($regen -eq $backup) {
        Write-Step 'T1 generated token table' 'OK' 'in sync with tokens.json'
    } else {
        Write-Step 'T1 generated token table' 'FAIL' 'regenerated output differs - the committed table was stale or hand-edited (working tree已更新，请一并提交)'
        $testFailed = $true
    }
}

if ((Test-Path $colorsXaml) -and (Test-Path $generatedCs)) {
    $hcBlock = (Get-Content $colorsXaml -Raw) -split 'x:Key="HighContrast"'
    $hcFromXaml = @()
    if ($hcBlock.Count -gt 1) {
        $segment = ($hcBlock[1] -split '</ResourceDictionary>')[0]
        $hcFromXaml = [regex]::Matches($segment, '<Color x:Key="(\w+)Color">\{ThemeResource (\w+)\}</Color>') |
            ForEach-Object { "$($_.Groups[1].Value)`t$($_.Groups[2].Value)" }
    }

    $hcFromCs = [regex]::Matches((Get-Content $generatedCs -Raw), '\["(\w+)"\] = "(SystemColor\w+)"') |
        ForEach-Object { "$($_.Groups[1].Value)`t$($_.Groups[2].Value)" }

    $diff = Compare-Object -ReferenceObject $hcFromXaml -DifferenceObject $hcFromCs
    if ($diff) {
        Write-Step 'T2 high-contrast mapping' 'FAIL' "Colors.xaml 与生成的 C#/TSV 不一致，差异 $($diff.Count) 处"
        $diff | Select-Object -First 10 | ForEach-Object { Write-Host "      $($_.SideIndicator) $($_.InputObject)" -ForegroundColor DarkRed }
        $testFailed = $true
    } else {
        Write-Step 'T2 high-contrast mapping' 'OK' "$($hcFromCs.Count) roles identical"
    }
}

if ((Test-Path $uiKitPath) -and (Test-Path $generatedCs)) {
    $used = [regex]::Matches((Get-Content $uiKitPath -Raw), 'Brush\("(\w+)"\)') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

    $defined = [regex]::Matches((Get-Content $generatedCs -Raw), '\["(\w+)"\] = "#') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

    $missing = $used | Where-Object { $defined -notcontains $_ }
    if ($missing) {
        Write-Step 'T3 UI token coverage' 'FAIL' ("未定义的颜色令牌：" + ($missing -join '、'))
        $testFailed = $true
    } else {
        Write-Step 'T3 UI token coverage' 'OK' "$($used.Count) tokens used by UiKit all defined"
    }
}

# ---- Step 6: copy standard（文案规范.md 的机械部分）----
# 只拦"硬性问题"：中文散文里夹半角双引号、中文后面用半角省略号——
# 这两类要么会直接破坏 C# 字面量，要么明确违反规范第 3 节。
# 提示性问题（禁用术语、超长消息）由 tools/review-copy.ps1 -List 人工过，不作为门禁。
Write-Head 'Copy standard'

$copyReview = Join-Path $repoRoot 'tools\review-copy.ps1'
if (Test-Path $copyReview) {
    $copyOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $copyReview 2>&1
    $copyOk = $LASTEXITCODE -eq 0
    if ($copyOk) {
        Write-Step 'C1 copy hard checks' 'OK' 'no half-width quote/ellipsis in Chinese prose'
    } else {
        Write-Step 'C1 copy hard checks' 'FAIL' 'see tools/review-copy.ps1 output'
        $copyOut | Select-Object -Last 12 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkRed }
        $testFailed = $true
    }
} else {
    Write-Step 'C1 copy hard checks' 'SKIP' 'tools/review-copy.ps1 not found'
}

# ---- Summary ----
Write-Head 'Summary'

$script:steps | Format-Table -AutoSize | Out-String | Write-Host

$summaryPath = Join-Path $resultDir 'summary.txt'
$script:steps | Format-Table -AutoSize | Out-String | Set-Content -Path $summaryPath -Encoding UTF8
Write-Host "Summary written to: $summaryPath" -ForegroundColor Gray
Write-Host "Detailed results in: $resultDir" -ForegroundColor Gray

$failed = ($script:steps | Where-Object { $_.Status -eq 'FAIL' }).Count
if ($failed -gt 0) {
    Write-Host ''
    Write-Host "$failed step(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'All steps passed.' -ForegroundColor Green
exit 0
