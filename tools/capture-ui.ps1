# 界面截图：把应用跑到指定页面，抓一张窗口位图，用于人工审视视觉结果。
#
# 为什么需要它：自动化测量能说明"快不快"，说明不了"对不对"。
# 每一次动到布局或配色之后，必须有一张真实的窗口截图可以看——否则"改好了"就只是一句自评。
#
# 用法：powershell -File tools/capture-ui.ps1 -Tag actions -Out C:\Temp\actions.png -Theme dark
[CmdletBinding()]
param(
    [string]$Tag = 'overview',

    [string]$Out = 'ui.png',

    [ValidateSet('light', 'dark')]
    [string]$Theme = 'light',

    [string]$Exe = 'src\EnvStation.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\EnvStation.exe',

    [int]$WarmupMs = 2600,

    [int]$Width = 1240,

    [int]$Height = 820
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class UiShot
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);

    public static bool Focus(IntPtr hwnd)
    {
        // 截图必须拍到"前台窗口"的样子：不抢焦点的话拍到的是被遮住的窗口。
        // 跨进程抢焦点需要先附加到目标线程的输入队列，否则 SetForegroundWindow 会被系统忽略。
        // 注：这里不能用 C# 的弃元 out _——Windows PowerShell 5.1 的 Add-Type 只认旧版语法。
        uint pid;
        uint other = GetWindowThreadProcessId(hwnd, out pid);
        uint self = GetCurrentThreadId();
        AttachThreadInput(self, other, true);
        try
        {
            ShowWindow(hwnd, 9); // SW_RESTORE
            SetForegroundWindow(hwnd);
        }
        finally
        {
            AttachThreadInput(self, other, false);
        }

        System.Threading.Thread.Sleep(250);
        return GetForegroundWindow() == hwnd;
    }

    public static int[] Rect(IntPtr hwnd)
    {
        RECT r;
        GetWindowRect(hwnd, out r);
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    /// <summary>
    /// 把窗口矩形换算到物理像素。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须换算</b>：Windows PowerShell 5.1 自己不是 DPI 感知的，
    /// 它拿到的 <c>GetWindowRect</c> 是系统按 96 DPI 虚拟化过的坐标
    /// （本机实测：窗口实际 1240x820，读出来是 992x656，正好差 0.8 = 96/120）。
    /// 直接按虚拟坐标抓屏，抓到的是一块<b>被裁掉四周</b>的图——第一版截图就是这样，
    /// 窗口左边和标题栏都缺了一条，却看不出是工具的问题。
    /// </remarks>
    public static int[] PhysicalRect(IntPtr hwnd)
    {
        int[] logical = Rect(hwnd);
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        double scale = dpi / 96.0;

        return new int[]
        {
            (int)Math.Round(logical[0] * scale),
            (int)Math.Round(logical[1] * scale),
            (int)Math.Round(logical[2] * scale),
            (int)Math.Round(logical[3] * scale),
        };
    }

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    /// <summary>
    /// 让窗口把自己画到给定位图上（与前后台无关）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不抄屏幕</b>：<c>CopyFromScreen</c> 要求目标窗口真的在最前面，
    /// 而抢焦点在多窗口环境下并不可靠——实测有一次 <c>SetForegroundWindow</c> 没成功，
    /// 抓回来的是<b>另一个程序的窗口</b>，脚本却照样报"已截图"。
    /// 静默抓错和静默测错是同一类问题：不报错，但结论全废。
    /// </para>
    /// <para>
    /// <c>PW_RENDERFULLCONTENT</c>（2）是必需的：WinUI 3 的内容由合成器绘制，
    /// 不带这个标志时经常抓到一片空白。
    /// </para>
    /// </remarks>
    public static bool CaptureWindow(IntPtr hwnd, IntPtr hdc)
    {
        return PrintWindow(hwnd, hdc, 2);
    }
}
'@

if (-not (Test-Path $Exe)) {
    throw "找不到可执行文件：$Exe（先跑一次 dotnet build）"
}

Get-Process -Name 'EnvStation' -ErrorAction SilentlyContinue | Stop-Process -Force

$env:ENVSTATION_BOOT_UI_TAG = $Tag
$env:ENVSTATION_BOOT_UI_THEME = $Theme
$env:ENVSTATION_BOOT_EXIT_MS = '0'
Remove-Item Env:\ENVSTATION_BOOT_LOG -ErrorAction SilentlyContinue

$process = Start-Process -FilePath $Exe -PassThru

try {
    Start-Sleep -Milliseconds $WarmupMs

    $process.Refresh()
    if ($process.HasExited) {
        throw "应用在截图前就退出了（退出码 $($process.ExitCode)）"
    }

    $hwnd = $process.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) {
        throw '拿不到主窗口句柄'
    }

    # 顺手置前只为了让窗口处于正常显示状态（最小化状态下抓不到内容），
    # 但抓图本身不依赖它成功——见 CaptureWindow 的说明。
    $focused = [UiShot]::Focus($hwnd)
    $rect = [UiShot]::PhysicalRect($hwnd)

    $bitmap = New-Object System.Drawing.Bitmap($rect[2], $rect[3])
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $hdc = $graphics.GetHdc()
        try {
            $captured = [UiShot]::CaptureWindow($hwnd, $hdc)
        }
        finally {
            $graphics.ReleaseHdc($hdc)
        }

        if (-not $captured) {
            throw 'PrintWindow 失败，无法抓取该窗口内容'
        }
    }
    finally {
        $graphics.Dispose()
    }

    # 自检：全白或全黑的图基本可以断定是抓失败了，宁可报错也不要交一张假图。
    $sample = @{}
    for ($x = 0; $x -lt $bitmap.Width; $x += 17) {
        for ($y = 0; $y -lt $bitmap.Height; $y += 17) {
            $sample[$bitmap.GetPixel($x, $y).ToArgb()] = $true
        }
    }

    if ($sample.Count -lt 8) {
        $bitmap.Dispose()
        throw "抓到的图几乎是纯色（不同颜色仅 $($sample.Count) 种），判定为抓取失败"
    }

    $full = [System.IO.Path]::GetFullPath($Out)
    $directory = [System.IO.Path]::GetDirectoryName($full)
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $bitmap.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    Write-Host "已截图 $Tag / $Theme -> $full（$($rect[2])x$($rect[3])，颜色 $($sample.Count) 种，前台=$focused）"
}
finally {
    Remove-Item Env:\ENVSTATION_BOOT_UI_TAG, Env:\ENVSTATION_BOOT_UI_THEME, Env:\ENVSTATION_BOOT_EXIT_MS -ErrorAction SilentlyContinue

    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(5000)
    }
}
