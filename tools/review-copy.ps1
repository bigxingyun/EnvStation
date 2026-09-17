<#
.SYNOPSIS
    文案审查：列出全部用户可见字符串，并按《文案规范.md》做机械检查。

.DESCRIPTION
    为什么需要它：文案规范（`文案规范.md`）里的规则若只靠人记，几轮之后必然漂回去。
    这里把能机械判定的部分做成检查——尤其是"字符串里出现半角双引号"这类会直接破坏
    C# 字面量的问题，以及禁用术语（干跑 / 体检 / 权限墙）。

    检查分两级：
      · 硬性问题（HARD）——必须修，会让编译失败或明确违反规范：字符串里含半角双引号、半角省略号
      · 提示性问题（SOFT）——需要人判断：禁用术语、礼貌语、超长消息

    用法：
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/review-copy.ps1
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/review-copy.ps1 -List
#>
[CmdletBinding()]
param(
    [switch]$List,
    [string[]]$Path = @('src')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Head([string]$text) {
    Write-Host ''
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
}

# 只扫"含中文的字符串行"，跳过整行注释。行尾注释里的中文会被一起扫到，
# 但那也值得看一眼（注释不该出现在用户可见位置）。
function Get-CopyLines {
    $files = foreach ($p in $Path) {
        Get-ChildItem (Join-Path $repoRoot $p) -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    }

    foreach ($file in $files) {
        $relative = $file.FullName.Replace($repoRoot + '\', '')
        $lines = Get-Content $file.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            $trimmed = $line.TrimStart()
            if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('*') -or $trimmed.StartsWith('/*')) { continue }
            if ($line -notmatch '[\u4e00-\u9fff]') { continue }
            if ($line -notmatch '"') { continue }

            [pscustomobject]@{ File = $relative; Line = $i + 1; Text = $line.Trim() }
        }
    }
}

$copy = @(Get-CopyLines)

Write-Host ''
Write-Host "环境站 · 文案审查（扫描 $($Path -join ', ')）" -ForegroundColor Cyan
Write-Host "含中文的字符串行：$($copy.Count)" -ForegroundColor Gray

# ── 硬性问题 ──────────────────────────────────────────────────────────────
#
# 判据刻意收窄成"事故签名"，不做宽泛匹配：
#   · 半角双引号：只有「中文"中文」这种夹在散文里的引号才是真事故
#     （转义 \" 与 $"…{expr}" 插值洞里的引号都是合法 C#，不该报）
#   · 半角省略号：只有紧跟在中文后面的 ... 才是散文里的省略号
#     （`[--allow <能力>]...` 与 `=\"...\"` 是语法占位符，不该报）
$hard = @()

$halfQuote = $copy | Where-Object { $_.Text -match '[\u4e00-\u9fff]"[\u4e00-\u9fff]' }
$hard += $halfQuote | ForEach-Object { [pscustomobject]@{ Kind = '中文里夹了半角双引号'; File = $_.File; Line = $_.Line; Text = $_.Text } }

$dots = $copy | Where-Object { $_.Text -match '[\u4e00-\u9fff]\.\.\.' }
$hard += $dots | ForEach-Object { [pscustomobject]@{ Kind = '中文后面用了半角省略号'; File = $_.File; Line = $_.Line; Text = $_.Text } }

# ── 提示性问题 ────────────────────────────────────────────────────────────
$soft = @()
$banned = [ordered]@{
    '干跑'   = '预演（DryRun）'
    '体检'   = '检测'
    '权限墙' = '能力授权'
    '您'     = '（去掉礼貌语）'
}

foreach ($term in $banned.Keys) {
    $hits = $copy | Where-Object { $_.Text -match [regex]::Escape($term) }
    $soft += $hits | ForEach-Object {
        [pscustomobject]@{ Kind = "$term → $($banned[$term])"; File = $_.File; Line = $_.Line; Text = $_.Text }
    }
}

# 超长消息：一句超过 60 个中文字符，通常意味着在解释设计而不是给结论
$longLines = $copy | Where-Object {
    $chinese = ([regex]::Matches($_.Text, '[\u4e00-\u9fff]')).Count
    $chinese -gt 60
}
$soft += $longLines | ForEach-Object { [pscustomobject]@{ Kind = '超过 60 个中文字符'; File = $_.File; Line = $_.Line; Text = $_.Text } }

# ── 输出 ──────────────────────────────────────────────────────────────────
Write-Head '硬性问题'
if ($hard.Count -eq 0) {
    Write-Host '  无。' -ForegroundColor Green
} else {
    foreach ($item in $hard) {
        Write-Host ("  [$($item.Kind)] $($item.File):$($item.Line)") -ForegroundColor Red
        Write-Host ("      $($item.Text)") -ForegroundColor DarkGray
    }
}

Write-Head '提示性问题'
$byKind = $soft | Group-Object Kind | Sort-Object Count -Descending
if ($byKind.Count -eq 0) {
    Write-Host '  无。' -ForegroundColor Green
} else {
    foreach ($group in $byKind) {
        Write-Host ("  {0,4} 处  {1}" -f $group.Count, $group.Name) -ForegroundColor Yellow
        if ($List) {
            foreach ($item in $group.Group) {
                Write-Host ("        $($item.File):$($item.Line)") -ForegroundColor DarkGray
                Write-Host ("            $($item.Text)") -ForegroundColor DarkGray
            }
        }
    }
    if (-not $List) { Write-Host '  （加 -List 可查看具体位置）' -ForegroundColor DarkGray }
}

Write-Host ''
if ($hard.Count -gt 0) {
    Write-Host "硬性问题 $($hard.Count) 处，必须修复。" -ForegroundColor Red
    exit 1
}

Write-Host '硬性问题为 0。' -ForegroundColor Green
exit 0
