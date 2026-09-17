<#
.SYNOPSIS
    校验 XAML 资源字典的结构完整性、令牌一致性与引用有效性。

.DESCRIPTION
    为什么需要这个脚本：
      开发环境没有 Windows App SDK / WinUI（无网络、无离线包），XAML **无法编译**。
      但资源字典最容易出错的几类问题恰恰是"编译期查不到、运行期才炸"：

        1. Dark / Light / HighContrast 分支的键不一致
           → 主题切换时找不到键，控件退回默认样式或抛 XamlParseException。
        2. {StaticResource X} / {ThemeResource X} 引用了不存在的键。
        3. **在 Light/Dark 分支内部误用 {ThemeResource} 引用另一个主题资源**
           → 共享 Brush 被跨子树污染（官方文档明确警告：表现为"打开浅色浮出控件后，
             深色界面局部也变浅"）。这是最隐蔽的一类 bug，本脚本专项检测。
        4. XAML 与 tokens.json 漂移（改了源头没同步 XAML）。

    官方依据：
      · 主题分支必须为 Light / Dark / HighContrast 三套，且键名集合一致。
      · 分支内引用主题资源必须用 {StaticResource}；唯一例外是 HighContrast 中引用
        SystemColor* 系列（由系统对比度设置控制，必须用 {ThemeResource} 才跟随）。
      见 https://learn.microsoft.com/en-us/windows/apps/develop/platform/xaml/xaml-theme-resources

    校验项：
      V1  XML 格式良好
      V2  主题分支键名集合一致（且至少含 Dark + Light）
      V3  所有 {ThemeResource}/{StaticResource} 引用可解析
      V4  Light/Dark 分支内不得用 {ThemeResource} 引用非 SystemColor* 资源
      V5  Colors.xaml 色值与 tokens.json.colorRoles 一致
      V6  Typography.xaml 字号/行高与 tokens.json.typeScale+monoScale 一致
      V7  Shapes.xaml 密度/形状/间距与 tokens.json 一致
      V8  Style 均已指定 TargetType
      V9  x:Key 仅在**同一作用域**内唯一（跨主题分支重复是必需的，不算错）

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/validate-xaml.ps1
#>
[CmdletBinding()]
param(
    [string]$XamlDir,
    [string]$TokensPath
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
if (-not $XamlDir)    { $XamlDir = Join-Path $root 'design\xaml' }
if (-not $TokensPath) { $TokensPath = Join-Path $root 'design\tokens.json' }

$script:fail = 0
$script:warn = 0
$XNS = 'http://schemas.microsoft.com/winfx/2006/xaml'

function Write-Head([string]$t) {
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor DarkCyan
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ('=' * 70) -ForegroundColor DarkCyan
}
function Ok([string]$m)   { Write-Host "  [OK  ] $m" -ForegroundColor Green }
function Warn([string]$m) { $script:warn++; Write-Host "  [WARN] $m" -ForegroundColor DarkYellow }
function Bad([string]$m)  { $script:fail++; Write-Host "  [FAIL] $m" -ForegroundColor Red }

Write-Head 'XAML resource dictionary validation'

if (-not (Test-Path $TokensPath)) {
    Bad "tokens.json not found: $TokensPath (run design/generate-tokens.ps1 first)"
    exit 1
}
$tokens = Get-Content $TokensPath -Encoding UTF8 -Raw | ConvertFrom-Json

$files = @(Get-ChildItem $XamlDir -Filter '*.xaml' -File | Sort-Object Name)
if ($files.Count -eq 0) { Bad "no XAML under $XamlDir"; exit 1 }
Write-Host "  files: $(($files.Name) -join ', ')" -ForegroundColor Gray

# ============================================================================
#  Parse: build scoped key sets and reference list
# ============================================================================
$allKeys       = New-Object System.Collections.Generic.HashSet[string]
$refs          = New-Object System.Collections.Generic.List[object]
$themeBranches = @{}
$xmlDocs       = @{}
$parseErrors   = New-Object System.Collections.Generic.List[string]

foreach ($f in $files) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.XmlResolver = $null
    try { $xml.Load($f.FullName) }
    catch {
        $parseErrors.Add("$($f.Name): XML parse failed - $($_.Exception.Message)")
        continue
    }
    $xmlDocs[$f.Name] = $xml

    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('x', $XNS)

    # ---------- theme branches: each is its own scope ----------
    $branches = @{}
    $td = $xml.SelectSingleNode('//*[local-name()="ResourceDictionary.ThemeDictionaries"]', $ns)
    if ($td) {
        foreach ($branch in $td.ChildNodes) {
            if ($branch.NodeType -ne 'Element') { continue }
            $bk = $branch.GetAttribute('Key', $XNS)
            if ([string]::IsNullOrWhiteSpace($bk)) { continue }

            $ks = New-Object System.Collections.Generic.HashSet[string]
            foreach ($n in $branch.SelectNodes('.//*[@x:Key]', $ns)) {
                $k = $n.GetAttribute('Key', $XNS)
                if ([string]::IsNullOrWhiteSpace($k)) { continue }
                [void]$ks.Add($k)
                [void]$allKeys.Add($k)
            }
            $branches[$bk] = $ks

            # V4: inside Light/Dark, ThemeResource is only legal for SystemColor*
            $isContrast = ($bk -eq 'HighContrast')
            foreach ($m in [regex]::Matches($branch.OuterXml, '\{\s*ThemeResource\s+([A-Za-z0-9_]+)\s*\}')) {
                $rk = $m.Groups[1].Value
                if ($isContrast -and $rk -like 'SystemColor*') { continue }
                if (-not $isContrast -and $rk -like 'SystemColor*') {
                    Bad "V4 $($f.Name)/$bk uses {ThemeResource $rk} - SystemColor* is only meaningful in HighContrast"
                    continue
                }
                Bad "V4 $($f.Name)/$bk uses {ThemeResource $rk} - inside a theme branch this MUST be {StaticResource}, otherwise shared brushes leak across themed subtrees"
            }
        }
    }
    $themeBranches[$f.Name] = $branches

    # ---------- root scope (exclude theme branches to avoid double counting) ----------
    $rootKeys = New-Object System.Collections.Generic.HashSet[string]
    foreach ($n in $xml.SelectNodes('//*[@x:Key]', $ns)) {
        $inside = $false
        $p = $n.ParentNode
        while ($p) {
            if ($p.LocalName -eq 'ResourceDictionary.ThemeDictionaries') { $inside = $true; break }
            $p = $p.ParentNode
        }
        if ($inside) { continue }

        $k = $n.GetAttribute('Key', $XNS)
        if ([string]::IsNullOrWhiteSpace($k)) { continue }
        if (-not $rootKeys.Add($k)) { Bad "V9 $($f.Name): duplicate x:Key in root scope -> '$k'" }
        [void]$allKeys.Add($k)
    }

    # ---------- references ----------
    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    foreach ($m in [regex]::Matches($text, '\{\s*(ThemeResource|StaticResource)\s+([A-Za-z0-9_.]+)\s*\}')) {
        $refs.Add([pscustomobject]@{ File = $f.Name; Kind = $m.Groups[1].Value; Key = $m.Groups[2].Value })
    }
}

# ---- V1 ----
Write-Host ''
foreach ($e in $parseErrors) { Bad "V1 $e" }
if ($parseErrors.Count -eq 0) { Ok "V1 XML well-formed: all $($files.Count) file(s) OK" }

# ---- V2 ----
Write-Host ''
$hosts = @($themeBranches.Keys | Where-Object { $themeBranches[$_].Count -gt 0 })
foreach ($h in $hosts) {
    $branches = $themeBranches[$h]
    $names = @($branches.Keys)
    $firstCount = 0
    if ($names.Count -gt 0) { $firstCount = $branches[$names[0]].Count }
    Write-Host "  $h : branches = $($names -join ', ') ($firstCount keys each)" -ForegroundColor Gray

    if (($names -notcontains 'Dark') -or ($names -notcontains 'Light')) {
        Bad "V2 $h must declare both Dark and Light (a Default-only dictionary breaks partial re-theming)"
    }

    $base = $branches[$names[0]]
    for ($i = 1; $i -lt $names.Count; $i++) {
        $cur = $branches[$names[$i]]
        $onlyBase = @($base | Where-Object { -not $cur.Contains($_) })
        $onlyCur  = @($cur  | Where-Object { -not $base.Contains($_) })
        if ($onlyBase.Count -gt 0) { Bad "V2 $h : only in [$($names[0])] -> $($onlyBase -join ', ')" }
        if ($onlyCur.Count -gt 0)  { Bad "V2 $h : only in [$($names[$i])] -> $($onlyCur -join ', ')" }
    }
}
if ($hosts.Count -eq 0) { Warn 'V2 no ThemeDictionaries found' }
elseif ($script:fail -eq 0) { Ok "V2 theme branch key parity: $($hosts.Count) file(s) OK" }

# ---- V3 ----
Write-Host ''
$frameworkKeys = @(
    'SystemColorHighlightColor','SystemColorHighlightTextColor',
    'SystemColorButtonFaceColor','SystemColorButtonTextColor',
    'SystemColorWindowColor','SystemColorWindowTextColor',
    'SystemColorHotlightColor','SystemColorGrayTextColor'
)
$unresolved = @()
foreach ($r in $refs) {
    if ($allKeys.Contains($r.Key)) { continue }
    if ($frameworkKeys -contains $r.Key) { continue }
    $unresolved += "$($r.File) -> $($r.Kind) $($r.Key)"
}
if ($unresolved.Count -gt 0) {
    foreach ($u in ($unresolved | Select-Object -Unique)) { Bad "V3 unresolved reference: $u" }
} else {
    Ok "V3 references: all $($refs.Count) resolvable ($($frameworkKeys.Count) framework keys whitelisted)"
}

# ---- V5 ----
Write-Host ''
$cf = $files | Where-Object Name -eq 'Colors.xaml'
if (-not $cf) { Warn 'V5 Colors.xaml not found, skipped' }
else {
    $ctext = [System.IO.File]::ReadAllText($cf.FullName, [System.Text.Encoding]::UTF8)
    $mis = 0
    $roleCount = 0
    foreach ($scheme in @('dark','light')) {
        $branchKey = if ($scheme -eq 'dark') { 'Dark' } else { 'Light' }
        $i0 = $ctext.IndexOf("x:Key=`"$branchKey`"")
        if ($i0 -lt 0) { Bad "V5 Colors.xaml missing $branchKey branch"; $mis++; continue }
        $i1 = $ctext.IndexOf('</ResourceDictionary>', $i0)
        $branch = $ctext.Substring($i0, $i1 - $i0)

        foreach ($prop in $tokens.colorRoles.$scheme.PSObject.Properties) {
            $roleCount++
            $expect = ([string]$prop.Value).ToUpperInvariant()
            $key = "$($prop.Name)Color"
            $m = [regex]::Match($branch, "<Color x:Key=`"$([regex]::Escape($key))`">([^<]+)</Color>")
            if (-not $m.Success) { Bad "V5 $branchKey missing color key $key (token value $expect)"; $mis++; continue }
            $actual = $m.Groups[1].Value.Trim().ToUpperInvariant()
            if ($actual -ne $expect) { Bad "V5 $branchKey.$key mismatch: XAML=$actual tokens=$expect"; $mis++ }
        }
    }
    if ($mis -eq 0) { Ok "V5 Colors.xaml matches tokens.json ($roleCount role values checked)" }
}

# ---- V6 ----
Write-Host ''
$tf = $files | Where-Object Name -eq 'Typography.xaml'
if (-not $tf) { Warn 'V6 Typography.xaml not found, skipped' }
else {
    $ttext = [System.IO.File]::ReadAllText($tf.FullName, [System.Text.Encoding]::UTF8)
    $mis = 0
    $checkStyle = {
        param([string]$styleKey, [double]$size, [double]$lh)
        $m = [regex]::Match($ttext, "<Style x:Key=`"$([regex]::Escape($styleKey))`"[\s\S]*?</Style>")
        if (-not $m.Success) { return "missing style $styleKey" }
        $fs = [regex]::Match($m.Value, 'Property="FontSize" Value="([0-9.]+)"')
        $ln = [regex]::Match($m.Value, 'Property="LineHeight" Value="([0-9.]+)"')
        if (-not $fs.Success) { return "$styleKey has no FontSize" }
        if ([double]$fs.Groups[1].Value -ne $size) { return "$styleKey FontSize: XAML=$($fs.Groups[1].Value) tokens=$size" }
        if (-not $ln.Success) { return "$styleKey has no LineHeight" }
        if ([double]$ln.Groups[1].Value -ne $lh) { return "$styleKey LineHeight: XAML=$($ln.Groups[1].Value) tokens=$lh" }
        return $null
    }
    foreach ($t in $tokens.typeScale) {
        $r = & $checkStyle "Type.$($t.key)" ([double]$t.size) ([double]$t.lhd)
        if ($r) { Bad "V6 $r"; $mis++ }
    }
    foreach ($t in $tokens.monoScale) {
        $r = & $checkStyle "Type.$($t.key)" ([double]$t.size) ([double]$t.lhd)
        if ($r) { Bad "V6 $r"; $mis++ }
    }
    if ($mis -eq 0) { Ok "V6 Typography.xaml matches tokens.json ($($tokens.typeScale.Count) type levels + $($tokens.monoScale.Count) mono styles)" }
}

# ---- V7 ----
Write-Host ''
$sf = $files | Where-Object Name -eq 'Shapes.xaml'
if (-not $sf) { Warn 'V7 Shapes.xaml not found, skipped' }
else {
    $stext = [System.IO.File]::ReadAllText($sf.FullName, [System.Text.Encoding]::UTF8)
    $mis = 0
    $densityMap = [ordered]@{
        ListItem='Height.ListItem'; Button='Height.Button'; IconButton='Height.IconButton'
        Chip='Height.Chip'; TopAppBar='Height.TopAppBar'; NavItem='Height.NavItem'
        StatusBar='Height.StatusBar'; TableRow='Height.TableRow'; TableHeader='Height.TableHeader'
        TextField='Height.TextField'; DialogAction='Height.DialogAction'
    }
    foreach ($n in $densityMap.Keys) {
        $expect = [double]$tokens.density.$n.desktop
        $key = $densityMap[$n]
        $m = [regex]::Match($stext, "<sys:Double x:Key=`"$([regex]::Escape($key))`">([0-9.]+)</sys:Double>")
        if (-not $m.Success) { Bad "V7 missing $key"; $mis++; continue }
        if ([double]$m.Groups[1].Value -ne $expect) { Bad "V7 $key : XAML=$($m.Groups[1].Value) tokens.desktop=$expect"; $mis++ }
    }
    foreach ($p in $tokens.shape.PSObject.Properties) {
        $m = [regex]::Match($stext, "<CornerRadius x:Key=`"Radius\.$($p.Name)`">([0-9.]+)</CornerRadius>")
        if (-not $m.Success) { Bad "V7 missing Radius.$($p.Name)"; $mis++; continue }
        if ([double]$m.Groups[1].Value -ne [double]$p.Value) { Bad "V7 Radius.$($p.Name) mismatch"; $mis++ }
    }
    foreach ($p in $tokens.spacing.PSObject.Properties) {
        $m = [regex]::Match($stext, "<Thickness x:Key=`"Space$($p.Name)`">([0-9.]+)</Thickness>")
        if (-not $m.Success) { Bad "V7 missing Space$($p.Name)"; $mis++; continue }
        if ([double]$m.Groups[1].Value -ne [double]$p.Value) { Bad "V7 Space$($p.Name) mismatch"; $mis++ }
    }
    if ($mis -eq 0) { Ok 'V7 Shapes.xaml matches tokens.json (density / shape / spacing)' }
}

# ---- V8 ----
Write-Host ''
$bad = 0
foreach ($name in $xmlDocs.Keys) {
    foreach ($s in $xmlDocs[$name].SelectNodes('//*[local-name()="Style"]')) {
        if ([string]::IsNullOrWhiteSpace($s.GetAttribute('TargetType'))) {
            Bad "V8 $name : Style without TargetType"
            $bad++
        }
    }
}
if ($bad -eq 0) { Ok 'V8 every Style declares TargetType' }

# ============================================================================
#  Summary
# ============================================================================
Write-Head 'Summary'
$themeKeyCount = 0
foreach ($h in $hosts) { foreach ($k in $themeBranches[$h].Values) { $themeKeyCount += $k.Count } }
Write-Host "  distinct resource keys      : $($allKeys.Count)"
Write-Host "  theme branch key instances  : $themeKeyCount"
Write-Host "  resource references         : $($refs.Count)"
Write-Host "  failures / warnings         : $script:fail / $script:warn"
Write-Host ''

if ($script:fail -gt 0) {
    Write-Host '  VALIDATION FAILED - fix before wiring into the WinUI project.' -ForegroundColor Red
    exit 1
}
Write-Host '  VALIDATION PASSED.' -ForegroundColor Green
Write-Host ''
Write-Host '  Limitation: this script is NOT a XAML compiler (Windows App SDK unavailable here).' -ForegroundColor DarkGray
Write-Host '              Once the SDK is available, a real XAML compile + theme-switch test is still required.' -ForegroundColor DarkGray
exit 0
