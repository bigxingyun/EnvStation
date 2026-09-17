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
        'WARN' { $color = 'DarkYellow' }
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
    'tests\EnvStation.Tests.Config\EnvStation.Tests.Config.csproj',
    'tests\EnvStation.Tests.Ui\EnvStation.Tests.Ui.csproj'
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
        # 注意 @() 不能省：PowerShell 5.1 里"恰好一个匹配"时 Where-Object / Select-String
        # 返回的是单个对象而不是集合，直接取 .Count 得到 $null，与 0 比较恒为假。
        # 同一模式在汇总处曾让"有 FAIL 也报 All steps passed"（见文件末尾注释）。
        $warnCount = @($output | Select-String -Pattern 'warning' -SimpleMatch).Count
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
    @{ Id = 'UI';   Name = 'UI state & remedy model';    Proj = 'tests\EnvStation.Tests.Ui' }
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

# ---- Step 8: 界面架构纪律（X-1 / X-4）----
# 这两条对应《重构与优化方案.md》第 7 章的硬约束：
#   X-1  界面不得直接调用内核动作：KernelBridge 只能被 Mvvm/KernelService.cs 引用。
#        上一版的问题不是"没人写可写入口"，而是入口写好了却零调用、且没有任何地方能一眼看全。
#   X-4  单文件不得超过 800 行：MainWindow.cs 曾长到 1935 行，其中约三分之一是性能测量装置。
# 纪律不落成检查就只是愿望。
Write-Head 'UI architecture discipline'

$appDir = Join-Path $repoRoot 'src\EnvStation.App'
$kernelServicePath = Join-Path $appDir 'Mvvm\KernelService.cs'

if (Test-Path $appDir) {
    # X-1：允许出现 KernelBridge 的文件白名单。
    #   · KernelBridge.cs 是类自身的定义，必须允许；
    #   · Mvvm/KernelService.cs 是唯一的包装处，界面一律经它访问内核。
    # IKernelService.cs 只是接口声明，不引用实现，故不在名单内。
    $bridgeAllowed = @(
        (Join-Path $appDir 'KernelBridge.cs'),
        (Join-Path $appDir 'Mvvm\KernelService.cs')
    )

    # MainWindow 是 partial 类，分散在多个文件里，且仍持有 _bridge。
    # 这是 M1-4/M1-6 要拆掉的存量，先记为 WARN；拆完后把 $mainWindowStillHoldsBridge 改成 $false，
    # 它立刻变成硬门禁。
    #
    # 用前缀匹配而不是逐个列文件名：partial 的文件名会随拆分过程变动
    # （MainWindow.cs → MainWindow.Pages.cs / MainWindow.Probes.cs），
    # 每次拆一个文件就要回来补白名单，这样的检查很快就会被人嫌麻烦而关掉。
    # 认"这个类"而不是"这些文件"，才经得起后续重构。
    $mainWindowStillHoldsBridge = $true
    $pendingPrefix = Join-Path $appDir 'MainWindow'

    $bridgeOffenders = @()
    $bridgePending = @()
    Get-ChildItem -Path $appDir -Recurse -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
        ForEach-Object {
            if ($bridgeAllowed -contains $_.FullName) { return }

            # 只认"真的在用"：类型名出现，或持有 _bridge 字段。
            # 注释行不算——文档里提到 KernelBridge 是在解释架构，不是在绕过它；
            # 把注释也算上会逼着人不写解释，那不是纪律想要的效果。
            $hits = @()
            $lineNo = 0
            foreach ($line in (Get-Content -Path $_.FullName)) {
                $lineNo++
                $trimmed = $line.TrimStart()
                if ($trimmed.StartsWith('//', [System.StringComparison]::Ordinal) -or
                    $trimmed.StartsWith('*', [System.StringComparison]::Ordinal)) { continue }
                if ($trimmed -match 'KernelBridge|_bridge\b') { $hits += "$($_.Name):$lineNo" }
            }

            if ($hits.Count -eq 0) { return }

            if ($mainWindowStillHoldsBridge -and $_.FullName.StartsWith($pendingPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $bridgePending += $hits
            } else {
                $bridgeOffenders += $hits
            }
        }

    if ($bridgeOffenders.Count -gt 0) {
        Write-Step 'X-1 kernel access is funneled' 'FAIL' ("KernelBridge 出现在白名单之外：" + ($bridgeOffenders -join '、'))
        $testFailed = $true
    } elseif ($bridgePending.Count -gt 0) {
        # 只报数量与文件，不逐行罗列：存量有几十处时，一长串行号会把整张汇总表撑乱，
        # 而这张表是给人扫一眼看的。要看具体位置就去看上面 FAIL 分支的格式。
        $pendingFilesHit = @($bridgePending | ForEach-Object { ($_ -split ':')[0] } | Sort-Object -Unique)
        Write-Step 'X-1 kernel access is funneled' 'WARN' `
            ("MainWindow 待拆存量 $($bridgePending.Count) 处，分布在 " + ($pendingFilesHit -join '、') + "（M1-4/M1-6 处理）")
    } else {
        Write-Step 'X-1 kernel access is funneled' 'OK' 'KernelBridge referenced only by Mvvm/KernelService.cs'
    }

    # X-4：单文件行数上限。
    $lineLimit = 800
    $tooLong = @()
    Get-ChildItem -Path $appDir -Recurse -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
        ForEach-Object {
            $count = (Get-Content -Path $_.FullName).Count
            if ($count -gt $lineLimit) { $tooLong += "$($_.Name)=$count" }
        }

    if ($tooLong.Count -gt 0) {
        # 说明：MainWindow.cs 当前的超限是已知存量，M1-4/M1-6 会把它拆开。
        # 这里先只报告不拦截，等拆分完成后再把它改成硬门禁（见 M1 任务书 M1-6 验收）。
        Write-Step 'X-4 file size limit' 'WARN' ("超过 $lineLimit 行（待 M1-6 拆分）：" + ($tooLong -join '、'))
    } else {
        Write-Step 'X-4 file size limit' 'OK' "no file exceeds $lineLimit lines"
    }
} else {
    Write-Step 'X-1 kernel access is funneled' 'SKIP' 'src/EnvStation.App not found'
}

# ---- Step 9: 汇总逻辑的阳性对照 ----
# 为什么需要这一步：本脚本原先的失败判据写成
#     $failed = ($script:steps | Where-Object { $_.Status -eq 'FAIL' }).Count
# 而 PowerShell 5.1 在「恰好一个结果」时返回的是单个对象、不是集合，取 .Count 得到 $null，
# $null -gt 0 恒为假 —— 于是**任何一步 FAIL 都会被汇总成 All steps passed 并以 0 退出**。
# 2026-09 给设计令牌加角色时，T2 明明报了 FAIL，脚本仍然绿着退出，就是这个缺陷。
# 光修一行不够：判据本身必须有一份阳性对照，否则下次改回同样的写法没人会发现。
Write-Head 'Self-check: summary failure detection'

$probeName = '__selfcheck_probe__'
$null = $script:steps.Add([pscustomobject]@{ Step = $probeName; Status = 'FAIL'; Detail = 'injected on purpose' })
$probeCount = @($script:steps | Where-Object { $_.Status -eq 'FAIL' }).Count
$script:steps.RemoveAt($script:steps.Count - 1)   # 立即移除，绝不影响最终汇总

if ($probeCount -ge 1) {
    Write-Step 'S1 failure detection works' 'OK' "injected FAIL counted as $probeCount"
    $script:steps.Remove(($script:steps | Where-Object { $_.Step -eq $probeName }))
} else {
    Write-Step 'S1 failure detection works' 'FAIL' 'injected FAIL was NOT counted - the summary would report a false pass'
    $testFailed = $true
}

if (@($script:steps | Where-Object { $_.Step -eq $probeName }).Count -gt 0) {
    Write-Step 'S2 probe removed' 'FAIL' 'self-check probe leaked into the summary'
    $testFailed = $true
} else {
    Write-Step 'S2 probe removed' 'OK' 'no leftover state'
}

# ---- Summary ----
Write-Head 'Summary'

$script:steps | Format-Table -AutoSize | Out-String | Write-Host

$summaryPath = Join-Path $resultDir 'summary.txt'
$script:steps | Format-Table -AutoSize | Out-String | Set-Content -Path $summaryPath -Encoding UTF8
Write-Host "Summary written to: $summaryPath" -ForegroundColor Gray
Write-Host "Detailed results in: $resultDir" -ForegroundColor Gray

$failed = @($script:steps | Where-Object { $_.Status -eq 'FAIL' }).Count
if ($failed -gt 0) {
    Write-Host ''
    Write-Host "$failed step(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'All steps passed.' -ForegroundColor Green
exit 0
