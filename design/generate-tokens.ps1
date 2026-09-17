<#
.SYNOPSIS
    从单一源头生成设计令牌产物（tokens.json + 文档用 Markdown 表）。

.DESCRIPTION
    为什么需要生成器：
      UI 设计规范（文档）、原型（HTML/CSS）、即将落地的 XAML 三者若各写一遍令牌，
      必然出现漂移 —— 本项目已经历过一轮"文档说 13/14.5/21px、原型却实现 M3 15 级"的漂移。
      因此把令牌定义收敛到本脚本的 $Tokens 数据结构，其余全部生成。

    本脚本产出：
      · design/tokens.json                     机器可读令牌（供 XAML 生成器、设计工具消费）
      · design/generated/typography-table.md   文档用 M3 Type Scale 表（桌面档 + 标准档对照）

    注意：本文件刻意使用 ASCII 之外的字符，必须带 UTF-8 BOM 才能被 Windows PowerShell 5.1 正确解析。

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File design/generate-tokens.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'generated' }
$null = New-Item -ItemType Directory -Force -Path $OutDir
$designDir = $PSScriptRoot
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# 统一以 LF 落盘。
#
# 为什么不用 WriteAllText 直接写：[System.IO.File]::WriteAllText 与 StringBuilder.AppendLine
# 用的都是 Environment.NewLine，在 Windows 上生成的是 CRLF，而本仓库约定 LF。
# 结果不是"多个回车"这么简单——校验步骤 T1 会把生成结果和已提交的那份逐字节比对，
# 于是每次跑校验都先被判成"令牌表是陈旧或被手改过"，本地与 CI 都红。
# 生成器与仓库的行尾约定必须一致，这个函数就是那条约定。
function Write-TokenFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][System.Text.Encoding]$Encoding
    )

    $normalized = $Content -replace "`r`n", "`n" -replace "`r", "`n"

    # 文件以单个换行结尾。Set-Content 本来会替我们补这一下，
    # 改成直接写文件之后必须自己补，否则生成结果与已提交的那份差一个字节，
    # 校验步骤照样判成"令牌表陈旧"。
    if (-not $normalized.EndsWith("`n", [System.StringComparison]::Ordinal)) {
        $normalized += "`n"
    }

    [System.IO.File]::WriteAllText($Path, $normalized, $Encoding)
}

Write-Host '环境站 EnvStation · 设计令牌生成' -ForegroundColor Cyan
Write-Host ('=' * 66) -ForegroundColor DarkCyan

# ============================================================================
#  ① M3 Tonal Palette（seed = #006A60 青绿）
#     色调 0~100，与 UI设计规范.md 第 2.1/2.2 节一致。
# ============================================================================
$Palette = [ordered]@{
    primary = [ordered]@{ p10='#00201C'; p20='#003731'; p30='#005048'; p40='#006A60'; p50='#008377'; p60='#00A494'; p70='#00C4B1'; p80='#4FDBD2'; p90='#6FF7EA'; p95='#B2FFF7'; p99='#F2FFFC'; p100='#FFFFFF' }
    secondary = [ordered]@{ p10='#0B1F1C'; p20='#213431'; p30='#374B47'; p40='#4F635E'; p50='#677C77'; p60='#819590'; p70='#9BB0AB'; p80='#B6CBC6'; p90='#D2E7E2'; p95='#E0F5F0'; p100='#FFFFFF' }
    neutral = [ordered]@{ n4='#0C0F0E'; n6='#101413'; n10='#191C1B'; n12='#1D201F'; n17='#272B29'; n20='#2E3130'; n22='#333635'; n24='#373B39'; n30='#444846'; n40='#5C605D'; n50='#757876'; n60='#8F9290'; n70='#A9ADAA'; n80='#C5C8C5'; n87='#DBDEDB'; n90='#E1E3E0'; n92='#E7E9E6'; n94='#EDEFEC'; n95='#EFF1EE'; n96='#F2F4F1'; n98='#F8FAF7'; n100='#FFFFFF' }
    error = [ordered]@{ p10='#410002'; p20='#690005'; p30='#93000A'; p40='#BA1A1A'; p80='#FFB4AB'; p90='#FFDAD6' }
}

# ============================================================================
#  ② M3 Color Roles —— 深浅两套 scheme
#     这是唯一的"语义颜色"来源；控件只允许引用 role，不得引用 palette 原色。
# ============================================================================
$RolesDark = [ordered]@{
    'Primary'              = $Palette.primary.p80;  'OnPrimary'              = $Palette.primary.p20
    'PrimaryContainer'     = $Palette.primary.p30;  'OnPrimaryContainer'     = $Palette.primary.p90
    'Secondary'            = $Palette.secondary.p80;'OnSecondary'            = $Palette.secondary.p20
    'SecondaryContainer'   = $Palette.secondary.p30;'OnSecondaryContainer'   = $Palette.secondary.p90
    'Tertiary'             = $Palette.secondary.p80;'OnTertiary'             = $Palette.secondary.p20
    'Error'                = $Palette.error.p80;    'OnError'                = $Palette.error.p20
    'ErrorContainer'       = $Palette.error.p30;    'OnErrorContainer'       = $Palette.error.p90
    'Surface'              = $Palette.neutral.n6;   'OnSurface'              = $Palette.neutral.n90
    'SurfaceVariant'       = $Palette.neutral.n30;  'OnSurfaceVariant'       = $Palette.neutral.n80
    'SurfaceDim'           = $Palette.neutral.n6;   'SurfaceBright'          = $Palette.neutral.n24
    'SurfaceContainerLowest' = $Palette.neutral.n4; 'SurfaceContainerLow'    = $Palette.neutral.n10
    'SurfaceContainer'     = $Palette.neutral.n12;  'SurfaceContainerHigh'   = $Palette.neutral.n17
    'SurfaceContainerHighest' = $Palette.neutral.n22
    'Outline'              = $Palette.neutral.n60;  'OutlineVariant'         = $Palette.neutral.n30
    'InverseSurface'       = $Palette.neutral.n90;  'InverseOnSurface'       = $Palette.neutral.n20
    'Scrim'                = '#000000'
    # 语义扩展（M3 无 success/warn role，按 tonal 规则派生）
    'Success'              = $Palette.primary.p80;  'OnSuccess'              = $Palette.primary.p20
    'SuccessContainer'     = $Palette.primary.p30;  'OnSuccessContainer'     = $Palette.primary.p90
    'Warn'                 = '#F0C24B';             'OnWarn'                 = '#3A2D00'
    'WarnContainer'        = '#4A3B10';             'OnWarnContainer'        = '#FFE79A'
    # 技术值专用（等宽文本着色）
    'TechSurface'          = '#080A09';  'TechText'    = '#CBD5D1'
    'TechPath'             = '#6FF7EA';  'TechVersion' = '#9CCDFF'
    'TechCommand'          = '#FFC48A';  'TechHash'    = '#C6B4FF'
}

$RolesLight = [ordered]@{
    'Primary'              = $Palette.primary.p40;  'OnPrimary'              = $Palette.primary.p100
    'PrimaryContainer'     = $Palette.primary.p90;  'OnPrimaryContainer'     = $Palette.primary.p10
    'Secondary'            = $Palette.secondary.p40;'OnSecondary'            = $Palette.secondary.p100
    'SecondaryContainer'   = $Palette.secondary.p90;'OnSecondaryContainer'   = $Palette.secondary.p10
    'Tertiary'             = $Palette.secondary.p40;'OnTertiary'             = $Palette.secondary.p100
    'Error'                = $Palette.error.p40;    'OnError'                = $Palette.error.p90
    'ErrorContainer'       = $Palette.error.p90;    'OnErrorContainer'       = $Palette.error.p10
    'Surface'              = $Palette.neutral.n98;  'OnSurface'              = $Palette.neutral.n10
    'SurfaceVariant'       = $Palette.neutral.n90;  'OnSurfaceVariant'       = $Palette.neutral.n30
    'SurfaceDim'           = $Palette.neutral.n87;  'SurfaceBright'          = $Palette.neutral.n98
    'SurfaceContainerLowest' = $Palette.neutral.n100;'SurfaceContainerLow'    = $Palette.neutral.n96
    'SurfaceContainer'     = $Palette.neutral.n94;  'SurfaceContainerHigh'   = $Palette.neutral.n92
    'SurfaceContainerHighest' = $Palette.neutral.n90
    'Outline'              = $Palette.neutral.n50;  'OutlineVariant'         = $Palette.neutral.n80
    'InverseSurface'       = $Palette.neutral.n20;  'InverseOnSurface'       = $Palette.neutral.n95
    'Scrim'                = '#000000'
    'Success'              = $Palette.primary.p40;  'OnSuccess'              = $Palette.primary.p100
    'SuccessContainer'     = $Palette.primary.p90;  'OnSuccessContainer'     = $Palette.primary.p10
    # 浅色下警告必须加深，否则白底对比度不足（约 2.4:1）——这是浅色主题的第一大坑
    'Warn'                 = '#7A5900';             'OnWarn'                 = '#FFFFFF'
    'WarnContainer'        = '#FFE08A';             'OnWarnContainer'        = '#261A00'
    'TechSurface'          = $Palette.neutral.n94;  'TechText'    = $Palette.neutral.n10
    'TechPath'             = '#00635A';  'TechVersion' = '#0B4F9E'
    'TechCommand'          = '#8A4A00';  'TechHash'    = '#5B3FA8'
}

# ============================================================================
#  ③ M3 Type Scale（15 级）
#     lineHeightDesktop 为"桌面密度档"行高（见 UI设计规范 2.5.0 节）
# ============================================================================
$TypeScale = @(
    @{ key='DisplayLarge';   size=57; lh=64; lhd=56; weight=400; tracking=-0.25 }
    @{ key='DisplayMedium';  size=45; lh=52; lhd=46; weight=400; tracking=0    }
    @{ key='DisplaySmall';   size=36; lh=44; lhd=40; weight=400; tracking=0    }
    @{ key='HeadlineLarge';  size=32; lh=40; lhd=36; weight=400; tracking=0    }
    @{ key='HeadlineMedium'; size=28; lh=36; lhd=32; weight=400; tracking=0    }
    @{ key='HeadlineSmall';  size=24; lh=32; lhd=28; weight=400; tracking=0    }
    @{ key='TitleLarge';     size=22; lh=28; lhd=24; weight=400; tracking=0    }
    @{ key='TitleMedium';    size=16; lh=24; lhd=20; weight=500; tracking=0.15 }
    @{ key='TitleSmall';     size=14; lh=20; lhd=18; weight=500; tracking=0.1  }
    @{ key='BodyLarge';      size=16; lh=24; lhd=22; weight=400; tracking=0.5  }
    @{ key='BodyMedium';     size=14; lh=20; lhd=18; weight=400; tracking=0.25 }
    @{ key='BodySmall';      size=12; lh=16; lhd=16; weight=400; tracking=0.4  }
    @{ key='LabelLarge';     size=14; lh=20; lhd=18; weight=500; tracking=0.1  }
    @{ key='LabelMedium';    size=12; lh=16; lhd=16; weight=500; tracking=0.5  }
    @{ key='LabelSmall';     size=11; lh=16; lhd=14; weight=500; tracking=0.5  }
)

# 项目自有的等宽/技术值样式（M3 无对应层级，属产品扩展）
$MonoScale = @(
    @{ key='MonoBody';  size=13; lh=20; lhd=18; weight=400 }
    @{ key='MonoSmall'; size=12; lh=18; lhd=16; weight=400 }
    @{ key='MonoLog';   size=12; lh=18; lhd=16; weight=400 }
    @{ key='MonoKbd';   size=11; lh=14; lhd=14; weight=600 }
)

# ============================================================================
#  ④ 形状 / 间距 / 动效（桌面密度档）
# ============================================================================
$Shape = [ordered]@{ XS=4; S=8; M=10; L=14; XL=20; Full=999 }
$Spacing = [ordered]@{ '1'=4; '2'=8; '3'=12; '4'=16; '5'=20; '6'=24; '8'=32 }

$Motion = [ordered]@{
    EasingEmphasized      = '0.2,0,0,1'
    EasingEmphasizedDecel = '0.05,0.7,0.1,1'
    EasingEmphasizedAccel = '0.3,0,0.8,0.15'
    EasingStandard        = '0.2,0,0,1'
    EasingStandardDecel   = '0,0,0,1'
    EasingStandardAccel   = '0.3,0,1,1'
    DurationShort1=50; DurationShort2=100; DurationShort3=150; DurationShort4=200
    DurationMedium1=250; DurationMedium2=300; DurationMedium3=350; DurationMedium4=400
    DurationLong1=450; DurationLong2=500
}

# 组件高度（桌面档 vs 标准档）
$Density = [ordered]@{
    ListItem     = [ordered]@{ desktop=48; standard=56; comfortable=64 }
    Button       = [ordered]@{ desktop=36; standard=40; comfortable=44 }
    IconButton   = [ordered]@{ desktop=32; standard=40; comfortable=44 }
    Chip         = [ordered]@{ desktop=28; standard=32; comfortable=32 }
    TopAppBar    = [ordered]@{ desktop=56; standard=64; comfortable=64 }
    NavItem      = [ordered]@{ desktop=48; standard=56; comfortable=64 }
    DialogAction = [ordered]@{ desktop=48; standard=52; comfortable=52 }
    StatusBar    = [ordered]@{ desktop=24; standard=32; comfortable=32 }
    TableRow     = [ordered]@{ desktop=48; standard=56; comfortable=64 }
    TableHeader  = [ordered]@{ desktop=44; standard=56; comfortable=56 }
    TextField    = [ordered]@{ desktop=48; standard=56; comfortable=56 }
    SwitchTrack  = [ordered]@{ desktop=44; standard=52; comfortable=52 }
    SwitchThumb  = [ordered]@{ desktop=20; standard=24; comfortable=24 }
}

# ============================================================================
#  ⑤ 输出 tokens.json
# ============================================================================
$FontUiStack = 'Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI'
$FontMonoStack = 'Cascadia Mono, JetBrains Mono, Consolas, Courier New'

$tokens = [ordered]@{
    '$schema'      = 'envstation.design-tokens/1.0'
    generatedAt    = (Get-Date -Format 'yyyy-MM-dd')
    generatedBy    = 'design/generate-tokens.ps1（请勿手工编辑本文件）'
    palette        = $Palette
    colorRoles     = [ordered]@{ dark = $RolesDark; light = $RolesLight }
    typeScale      = $TypeScale
    monoScale      = $MonoScale
    shape          = $Shape
    spacing        = $Spacing
    motion         = $Motion
    density        = $Density
    fonts          = [ordered]@{
        ui   = $FontUiStack
        mono = $FontMonoStack
    }
}

$jsonPath = Join-Path $designDir 'tokens.json'
Write-TokenFile -Path $jsonPath -Content ($tokens | ConvertTo-Json -Depth 10) -Encoding $utf8Bom
Write-Host "  已生成 tokens.json            $jsonPath" -ForegroundColor Green

# ============================================================================
#  ⑥ 输出文档用 Markdown 表（避免文档与代码漂移）
# ============================================================================
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<!-- 本文件由 design/generate-tokens.ps1 生成，请勿手工编辑 -->')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| M3 层级 | 字号 | 标准档行高 | **桌面档行高** | 字重 | 字距 | 用途 |')
[void]$sb.AppendLine('| --- | --- | --- | --- | --- | --- | --- |')

$usage = @{
    DisplayLarge='产品名、大型空态'; DisplayMedium='—'; DisplaySmall='体检结论数字、指标卡数值'
    HeadlineLarge='—'; HeadlineMedium='—'; HeadlineSmall='体检结论标题、空态主标题'
    TitleLarge='页面标题（顶部栏）'; TitleMedium='卡片标题、列表主文本、命令名'
    TitleSmall='次级标题、表单项标签'; BodyLarge='对话框正文、大段说明'
    BodyMedium='正文、列表次要文本'; BodySmall='元数据、时间戳、辅助说明'
    LabelLarge='按钮文案'; LabelMedium='Chip 文案、状态标签'; LabelSmall='大写小标签、徽标'
}

foreach ($t in $TypeScale) {
    $u = $usage[$t.key]
    [void]$sb.AppendLine("| ``Type.$($t.key)`` | $($t.size)px | $($t.lh)px | **$($t.lhd)px** | $($t.weight) | $($t.tracking) | $u |")
}

[void]$sb.AppendLine('')
[void]$sb.AppendLine('**产品扩展：等宽技术值样式**（M3 无对应层级）')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| 令牌 | 字号 | 标准行高 | **桌面档行高** | 字重 | 用途 |')
[void]$sb.AppendLine('| --- | --- | --- | --- | --- | --- |')
$monoUsage = @{ MonoBody='技术值正文（路径/版本）'; MonoSmall='表格内技术值'; MonoLog='日志面板'; MonoKbd='快捷键提示' }
foreach ($m in $MonoScale) {
    [void]$sb.AppendLine("| ``Type.$($m.key)`` | $($m.size)px | $($m.lh)px | **$($m.lhd)px** | $($m.weight) | $($monoUsage[$m.key]) |")
}

$mdPath = Join-Path $OutDir 'typography-table.md'
Write-TokenFile -Path $mdPath -Content $sb.ToString() -Encoding $utf8Bom
Write-Host "  已生成 typography-table.md   $mdPath" -ForegroundColor Green

# ============================================================================
#  ⑦ 输出 C# 令牌表（界面运行期取值用）
#
#     为什么需要它：本产品的界面**全部由 C# 构建**，而散装的 XAML 令牌字典在运行期
#     加载不了 —— XamlReader 不支持 <sys:Double> 这类编译期类型，
#     ResourceDictionary.Source 指向散文件则直接 COMException（两者均为本机实测结果）。
#     所以凡是要被代码读取的令牌（颜色、字体、间距、圆角、密度）另外生成一份 C# 表。
#
#     高对比度映射（role → SystemColor*）同时写出 TSV，
#     供 tools/verify-m0.ps1 与 design/xaml/Colors.xaml 的 HighContrast 段做一致性比对：
#     两处内容必须逐行相同，否则校验失败。这是防"两处漂移"的机械手段。
# ============================================================================

$HcMap = [ordered]@{
    'Primary'                 = 'SystemColorHighlightColor'
    'OnPrimary'               = 'SystemColorHighlightTextColor'
    'PrimaryContainer'        = 'SystemColorButtonFaceColor'
    'OnPrimaryContainer'      = 'SystemColorButtonTextColor'
    'Secondary'               = 'SystemColorButtonFaceColor'
    'OnSecondary'             = 'SystemColorButtonTextColor'
    'SecondaryContainer'      = 'SystemColorButtonFaceColor'
    'OnSecondaryContainer'    = 'SystemColorButtonTextColor'
    'Tertiary'                = 'SystemColorButtonFaceColor'
    'OnTertiary'              = 'SystemColorButtonTextColor'
    'Error'                   = 'SystemColorWindowTextColor'
    'OnError'                 = 'SystemColorWindowColor'
    'ErrorContainer'          = 'SystemColorWindowColor'
    'OnErrorContainer'        = 'SystemColorWindowTextColor'
    'Surface'                 = 'SystemColorWindowColor'
    'OnSurface'               = 'SystemColorWindowTextColor'
    'SurfaceVariant'          = 'SystemColorWindowColor'
    'OnSurfaceVariant'        = 'SystemColorWindowTextColor'
    'SurfaceDim'              = 'SystemColorWindowColor'
    'SurfaceBright'           = 'SystemColorWindowColor'
    'SurfaceContainerLowest'  = 'SystemColorWindowColor'
    'SurfaceContainerLow'     = 'SystemColorWindowColor'
    'SurfaceContainer'        = 'SystemColorWindowColor'
    'SurfaceContainerHigh'    = 'SystemColorWindowColor'
    'SurfaceContainerHighest' = 'SystemColorWindowColor'
    'Outline'                 = 'SystemColorWindowTextColor'
    'OutlineVariant'          = 'SystemColorWindowTextColor'
    'InverseSurface'          = 'SystemColorWindowTextColor'
    'InverseOnSurface'        = 'SystemColorWindowColor'
    'Scrim'                   = 'SystemColorWindowColor'
    'Success'                 = 'SystemColorWindowTextColor'
    'OnSuccess'               = 'SystemColorWindowColor'
    'SuccessContainer'        = 'SystemColorWindowColor'
    'OnSuccessContainer'      = 'SystemColorWindowTextColor'
    'Warn'                    = 'SystemColorWindowTextColor'
    'OnWarn'                  = 'SystemColorWindowColor'
    'WarnContainer'           = 'SystemColorWindowColor'
    'OnWarnContainer'         = 'SystemColorWindowTextColor'
    'TechSurface'             = 'SystemColorWindowColor'
    'TechText'                = 'SystemColorWindowTextColor'
    'TechPath'                = 'SystemColorWindowTextColor'
    'TechVersion'             = 'SystemColorWindowTextColor'
    'TechCommand'             = 'SystemColorWindowTextColor'
    'TechHash'                = 'SystemColorWindowTextColor'
}

# 高对比度映射的独立产物：给人看，也给校验脚本读。
$hcTsv = New-Object System.Text.StringBuilder
foreach ($k in $HcMap.Keys) { [void]$hcTsv.AppendLine("$k`t$($HcMap[$k])") }
$hcPath = Join-Path $OutDir 'high-contrast-map.tsv'
Write-TokenFile -Path $hcPath -Content $hcTsv.ToString() -Encoding $utf8Bom
Write-Host "  已生成 high-contrast-map.tsv $hcPath" -ForegroundColor Green

# WinUI 的 FontFamily 不接受逗号分隔的字体栈（那是 CSS 的写法），只取首个族名；
# 缺失字形由 DirectWrite 的系统回退链负责（中文因此仍能正常显示）。
$uiFontFamily = ($FontUiStack -split ',')[0].Trim()
$monoFontFamily = ($FontMonoStack -split ',')[0].Trim()

$cs = New-Object System.Text.StringBuilder
[void]$cs.AppendLine('// <auto-generated>')
[void]$cs.AppendLine('//     本文件由 design/generate-tokens.ps1 生成，请勿手工编辑。')
[void]$cs.AppendLine('//     源头：design/tokens.json（同一脚本的 $RolesDark / $RolesLight / $HcMap / $Shape / $Spacing / $Density）。')
[void]$cs.AppendLine('//     校验：tools/verify-m0.ps1 会重新生成并逐字节比对，手工改动会让校验失败。')
[void]$cs.AppendLine('// </auto-generated>')
[void]$cs.AppendLine('')
[void]$cs.AppendLine('namespace EnvStation.App;')
[void]$cs.AppendLine('')
[void]$cs.AppendLine('/// <summary>字号令牌：字号、桌面档行高、字重、字距。</summary>')
[void]$cs.AppendLine('/// <param name="Size">字号（px）。</param>')
[void]$cs.AppendLine('/// <param name="LineHeight">行高（px，桌面密度档）。</param>')
[void]$cs.AppendLine('/// <param name="Weight">字重（400/500/600）。</param>')
[void]$cs.AppendLine('/// <param name="Tracking">字距（px）。</param>')
[void]$cs.AppendLine('internal readonly record struct TypeToken(double Size, double LineHeight, int Weight, double Tracking);')
[void]$cs.AppendLine('')
[void]$cs.AppendLine('/// <summary>设计令牌的 C# 载体。界面代码只从这里取颜色、字体与尺寸。</summary>')
[void]$cs.AppendLine('/// <remarks>')
[void]$cs.AppendLine('/// <para>')
[void]$cs.AppendLine('/// 界面全部由 C# 构建，因此颜色等令牌必须以代码可读的形式存在；')
[void]$cs.AppendLine('/// XAML 字典（design/xaml/）仍然是给人看的规范载体，由 tools/validate-xaml.ps1 校验。')
[void]$cs.AppendLine('/// </para>')
[void]$cs.AppendLine('/// <para>')
[void]$cs.AppendLine('/// 高对比度一栏的值是 WinUI 的 <c>SystemColor*</c> 资源键而不是色值：')
[void]$cs.AppendLine('/// 高对比度配色由用户在系统里定，产品只能引用，不能自带一套。')
[void]$cs.AppendLine('/// </para>')
[void]$cs.AppendLine('/// </remarks>')
[void]$cs.AppendLine('internal static class DesignTokens')
[void]$cs.AppendLine('{')
[void]$cs.AppendLine('    /// <summary>界面字体族（tokens.json 的 fonts.ui 取首个族名）。</summary>')
[void]$cs.AppendLine("    internal const string UiFontFamily = `"$uiFontFamily`";")
[void]$cs.AppendLine('')
[void]$cs.AppendLine('    /// <summary>等宽字体族（tokens.json 的 fonts.mono 取首个族名）。</summary>')
[void]$cs.AppendLine("    internal const string MonoFontFamily = `"$monoFontFamily`";")
[void]$cs.AppendLine('')

function Write-ColorMap {
    param([System.Text.StringBuilder]$Builder, [string]$Name, $Map, [string]$Summary, [string]$Remarks)
    [void]$Builder.AppendLine("    /// <summary>$Summary</summary>")
    if ($Remarks) { [void]$Builder.AppendLine("    /// <remarks>$Remarks</remarks>") }
    [void]$Builder.AppendLine("    internal static readonly Dictionary<string, string> $Name = new(StringComparer.Ordinal)")
    [void]$Builder.AppendLine('    {')
    foreach ($k in $Map.Keys) {
        [void]$Builder.AppendLine("        [`"$k`"] = `"$($Map[$k])`",")
    }
    [void]$Builder.AppendLine('    };')
    [void]$Builder.AppendLine('')
}

Write-ColorMap -Builder $cs -Name 'DarkColors' -Map $RolesDark `
    -Summary '深色主题的颜色令牌（role → #RRGGBB）。' -Remarks ''
Write-ColorMap -Builder $cs -Name 'LightColors' -Map $RolesLight `
    -Summary '浅色主题的颜色令牌（role → #RRGGBB）。' -Remarks ''
Write-ColorMap -Builder $cs -Name 'HighContrastColors' -Map $HcMap `
    -Summary '高对比度主题的令牌映射（role → SystemColor* 资源键）。' `
    -Remarks '与 design/xaml/Colors.xaml 的 HighContrast 段逐行一致，由 verify-m0.ps1 比对。'

function Write-TypeScale {
    param([System.Text.StringBuilder]$Builder, [string]$Name, $Scale, [string]$Summary, [string]$Remarks)
    [void]$Builder.AppendLine("    /// <summary>$Summary</summary>")
    if ($Remarks) { [void]$Builder.AppendLine("    /// <remarks>$Remarks</remarks>") }
    [void]$Builder.AppendLine("    internal static readonly Dictionary<string, TypeToken> $Name = new(StringComparer.Ordinal)")
    [void]$Builder.AppendLine('    {')
    foreach ($t in $Scale) {
        $tracking = if ($null -ne $t.tracking) { $t.tracking } else { 0 }
        [void]$Builder.AppendLine("        [`"$($t.key)`"] = new($($t.size), $($t.lhd), $($t.weight), $tracking),")
    }
    [void]$Builder.AppendLine('    };')
    [void]$Builder.AppendLine('')
}

Write-TypeScale -Builder $cs -Name 'TypeScale' -Scale $TypeScale `
    -Summary 'M3 15 级字阶（行高取桌面密度档）。' `
    -Remarks '字号在任何密度档下都不变，只有行高变（UI设计规范 DN-1）。'
Write-TypeScale -Builder $cs -Name 'MonoScale' -Scale $MonoScale `
    -Summary '产品扩展的等宽技术值字阶（M3 无对应层级）。' -Remarks ''

[void]$cs.AppendLine('    /// <summary>4px 基准间距刻度（UI设计规范 2.5 节 Space.1~Space.8）。</summary>')
foreach ($k in $Spacing.Keys) {
    [void]$cs.AppendLine("    internal const double Space$k = $($Spacing[$k]);")
}
[void]$cs.AppendLine('')
[void]$cs.AppendLine('    /// <summary>圆角刻度（桌面档取紧凑半径）。</summary>')
foreach ($k in $Shape.Keys) {
    [void]$cs.AppendLine("    internal const double Shape$k = $($Shape[$k]);")
}
[void]$cs.AppendLine('')
[void]$cs.AppendLine('    /// <summary>桌面密度档的容器高度（UI设计规范 3.3 节 / tokens.json density.*.desktop）。</summary>')
[void]$cs.AppendLine('    /// <remarks>切换密度档只改这些值，**不动字号**（UI设计规范 DN-1）。</remarks>')
foreach ($k in $Density.Keys) {
    [void]$cs.AppendLine("    internal const double ${k}Height = $($Density[$k].desktop);")
}
[void]$cs.AppendLine('}')

$csPath = Join-Path $root 'src\EnvStation.App\Generated\DesignTokens.g.cs'
$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $csPath)
Write-TokenFile -Path $csPath -Content $cs.ToString() -Encoding $utf8Bom
Write-Host "  已生成 DesignTokens.g.cs      $csPath" -ForegroundColor Green

# ============================================================================
#  ⑧ 自检：对比度校验（WCAG AA）
# ============================================================================
function Get-Luminance([string]$hex) {
    $h = $hex.TrimStart('#')
    $r = [Convert]::ToInt32($h.Substring(0,2),16) / 255
    $g = [Convert]::ToInt32($h.Substring(2,2),16) / 255
    $b = [Convert]::ToInt32($h.Substring(4,2),16) / 255
    $f = { param($c) if ($c -le 0.03928) { $c / 12.92 } else { [Math]::Pow(($c + 0.055) / 1.055, 2.4) } }
    return 0.2126 * (& $f $r) + 0.7152 * (& $f $g) + 0.0722 * (& $f $b)
}
function Get-Contrast([string]$a, [string]$b) {
    $la = Get-Luminance $a; $lb = Get-Luminance $b
    $hi = [Math]::Max($la,$lb); $lo = [Math]::Min($la,$lb)
    return [Math]::Round(($hi + 0.05) / ($lo + 0.05), 2)
}

Write-Host ''
Write-Host '  对比度自检（WCAG AA：正文 ≥ 4.5，大字/图形 ≥ 3.0）' -ForegroundColor Cyan

$contrastPairs = @(
    @{ name='暗 · OnSurface / Surface';        fg='OnSurface';  bg='Surface';  min=4.5 }
    @{ name='暗 · OnSurfaceVariant / Surface'; fg='OnSurfaceVariant'; bg='Surface'; min=4.5 }
    @{ name='暗 · OnPrimary / Primary';        fg='OnPrimary';  bg='Primary';  min=4.5 }
    @{ name='暗 · OnError / Error';            fg='OnError';    bg='Error';    min=4.5 }
    @{ name='暗 · OnWarnContainer / WarnContainer'; fg='OnWarnContainer'; bg='WarnContainer'; min=4.5 }
    @{ name='亮 · OnSurface / Surface';        fg='OnSurface';  bg='Surface';  min=4.5 }
    @{ name='亮 · OnSurfaceVariant / Surface'; fg='OnSurfaceVariant'; bg='Surface'; min=4.5 }
    @{ name='亮 · OnPrimary / Primary';        fg='OnPrimary';  bg='Primary';  min=4.5 }
    @{ name='亮 · OnError / Error';            fg='OnError';    bg='Error';    min=4.5 }
    @{ name='亮 · Warn / Surface（浅色警告加深是否达标）'; fg='Warn'; bg='Surface'; min=4.5 }
    @{ name='亮 · OnWarnContainer / WarnContainer'; fg='OnWarnContainer'; bg='WarnContainer'; min=4.5 }
)

$fail = 0
foreach ($p in $contrastPairs) {
    foreach ($scheme in @(@{n='dark';r=$RolesDark}, @{n='light';r=$RolesLight})) {
        if (-not $p.name.StartsWith($(if($scheme.n -eq 'dark'){'暗'}else{'亮'}))) { continue }
        $fg = $scheme.r[$p.fg]; $bg = $scheme.r[$p.bg]
        if (-not $fg -or -not $bg) { continue }
        $c = Get-Contrast $fg $bg
        $ok = $c -ge $p.min
        if (-not $ok) { $fail++ }
        $mark = if ($ok) { 'OK  ' } else { 'FAIL' }
        $color = if ($ok) { 'Green' } else { 'Red' }
        Write-Host ("    [{0}] {1,-52} {2,6}:1  (需 ≥ {3})" -f $mark, $p.name, $c, $p.min) -ForegroundColor $color
    }
}

Write-Host ''
if ($fail -gt 0) {
    Write-Host "  $fail 项对比度未达标 —— 必须修正令牌后才能落地 XAML" -ForegroundColor Red
    exit 1
}
Write-Host '  全部通过。' -ForegroundColor Green
exit 0
