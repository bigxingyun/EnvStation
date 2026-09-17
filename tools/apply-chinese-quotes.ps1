# 中文直角引号写入工具（README 与其它 Markdown 正文）。
#
# 为什么需要它：本项目《文案规范.md》CP-7 规定中文正文不得出现半角引号与全角引号，
# 正文引用一律使用直角引号（U+300C / U+300D）。但这些字符在多数文本编辑路径上
# 会被规范化成半角引号——直接编辑写不进正确的字符，只能靠脚本按码位写入。
#
# 用法：正文里先用占位符 【Q】 成对地写，然后跑这个脚本：
#
#     powershell -NoProfile -ExecutionPolicy Bypass -File tools\apply-readme-quotes.ps1
#
# 之所以是"占位符 + 成对替换"而不是"全文正则替换半角引号"：
# 后者会连 TOML / PowerShell 代码块里的引号一起换掉——本仓库真的发生过一次，
# 三行 TOML 的收尾引号被改成直角引号，包清单直接失效。
# **能改坏数据的自动化比手工更危险**，所以这里把"只碰占位符"作为硬约束。

param(
    [string]$Path = 'README.md'
)

$ErrorActionPreference = 'Stop'

$full = [System.IO.Path]::GetFullPath($Path)
if (-not (Test-Path $full)) {
    throw "找不到文件：$full"
}

$text = [System.IO.File]::ReadAllText($full, [System.Text.Encoding]::UTF8)

$placeholder = '【Q】'
$open = [string][char]0x300C    # 左直角引号
$close = [string][char]0x300D   # 右直角引号

$count = ([regex]::Matches($text, [regex]::Escape($placeholder))).Count

if ($count -eq 0) {
    Write-Host "没有占位符 $placeholder，无需处理。"
    return
}

if ($count % 2 -ne 0) {
    throw "占位符数量为奇数（$count）——引号不成对，已中止，未写入任何内容"
}

# 逐个交替替换：第 1、3、5… 个为左引号，第 2、4、6… 个为右引号。
# 不用正则一次性替换，是为了让"成对"这件事在代码里显式可见。
$builder = New-Object System.Text.StringBuilder
$index = 0
$toggle = 0

while ($true) {
    $next = $text.IndexOf($placeholder, $index, [System.StringComparison]::Ordinal)
    if ($next -lt 0) {
        [void]$builder.Append($text, $index, $text.Length - $index)
        break
    }

    [void]$builder.Append($text, $index, $next - $index)
    [void]$builder.Append($(if ($toggle % 2 -eq 0) { $open } else { $close }))
    $toggle++
    $index = $next + $placeholder.Length
}

$result = $builder.ToString()

# 写入前三重校验：占位符清零、直角引号数量吻合、总长度变化可解释。
# 任何一条不成立就中止——宁可什么都不做，也不要写进一个半成品。
$remaining = ([regex]::Matches($result, [regex]::Escape($placeholder))).Count
$corners = ([regex]::Matches($result, '[\u300C\u300D]')).Count
$expectedLength = $text.Length - ($count * ($placeholder.Length - 1))

if ($remaining -ne 0) { throw "仍有 $remaining 个占位符未替换，已中止" }
if ($corners -ne $count) { throw "直角引号 $corners 个，应为 $count 个，已中止" }
if ($result.Length -ne $expectedLength) { throw '长度校验失败，已中止' }

[System.IO.File]::WriteAllText($full, $result, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("已替换 {0} 个占位符 -> 左引号 {1} 个 / 右引号 {2} 个" -f `
    $count, [Math]::Ceiling($count / 2), [Math]::Floor($count / 2))
