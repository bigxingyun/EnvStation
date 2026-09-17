using System.Globalization;
using System.Text;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnvStation.App;

/// <summary>
/// 应用入口。
/// </summary>
/// <remarks>
/// <para>
/// <b>非打包运行</b>（<c>WindowsPackageType=None</c>）：直接产出可双击的 exe。
/// 这样 M0-P01 的冷启动与内存可以本机实测，用户也能"下载即用"而不必先信任一个安装包。
/// </para>
/// <para>
/// <b>不做任何需要提权的初始化</b>：主界面以标准用户运行（见 app.manifest 的 asInvoker）。
/// 需要管理员的操作由独立的 Helper 进程按需触发 UAC——这既是安全要求，
/// 也让"看一眼我的环境"这件事完全不需要提权。
/// </para>
/// </remarks>
public partial class App : Application
{
    private Window? _window;

    /// <summary>冷启动计时起点（M0-P01 用它测"窗口可见"耗时）。</summary>
    internal static readonly System.Diagnostics.Stopwatch StartupClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>当前应用实例。</summary>
    internal static new App Current => (App)Application.Current;

    /// <summary>主窗口。</summary>
    internal MainWindow? MainWindow => _window as MainWindow;

    public App()
    {
        BootProbe.Write("App 构造开始");
        InitializeComponent();
        BootProbe.Write("App.xaml 已加载");

        // 未处理异常必须留痕：GUI 崩溃若只有"程序已停止工作"一句，用户无法反馈任何有用信息。
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        BootProbe.Write("进程启动，开始构造主窗口");

        var window = new MainWindow();
        _window = window;
        window.Activate();

        // 窗口激活之后再记录：这才是用户真正"看到界面"的时刻。
        // 注意：StartupClock 记下数字后要重新起表——启动探针后续还要给页面切换、
        // 主题切换打时间戳；一旦停表，后面所有行的耗时都会是同一个冻结值（第一版就是这样）。
        StartupClock.Stop();
        var coldStartMs = StartupClock.ElapsedMilliseconds;
        StartupClock.Start();
        BootProbe.Write($"窗口可见，冷启动 {coldStartMs} ms");

        BootProbe.OnWindowVisible(window);

        // 截图工具用：把界面开到指定页面与主题。两个环境变量都不设时是空操作。
        _ = window.ApplyStartupUiStateAsync();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EnvStation",
                "logs");
            Directory.CreateDirectory(logDirectory);

            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
                .Append("  UI 未处理异常：")
                .Append(e.Message)
                .Append('\n')
                .Append(e.Exception)
                .Append("\n\n")
                .ToString();

            // 与启动探针同理：日志带 UTF-8 BOM，否则 Windows PowerShell 5.1 的 Get-Content
            // 会按 ANSI 读，中文全是乱码——崩溃日志读不出内容就等于没记。
            var crashLog = Path.Combine(logDirectory, "ui-crash.log");
            File.AppendAllText(
                crashLog,
                line,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: !File.Exists(crashLog)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 连日志都写不了也不能再抛——否则会变成崩溃循环。
        }

        e.Handled = true;
    }

    /// <summary>切换浅色 / 深色 / 跟随系统。</summary>
    internal void ApplyTheme(ElementTheme theme)
    {
        if (_window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    /// <summary>读取当前主题。</summary>
    internal ElementTheme CurrentTheme =>
        _window?.Content is FrameworkElement root ? root.RequestedTheme : ElementTheme.Default;

    /// <summary>
    /// 把标题栏（非客户区）改成与当前主题一致的颜色。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么标题栏要单独处理</b>：<c>RequestedTheme</c> 只管 XAML 内容区，
    /// 窗口的非客户区由 DWM 画，两者不会自动同步。不处理的话深色主题下会出现
    /// 「标题栏白、窗口内黑」的割裂感，这在本产品里尤其明显——它一屏之内既有卡片又有状态栏。
    /// </para>
    /// <para>
    /// 颜色取自设计令牌（Surface / OnSurface / SurfaceContainerHigh），
    /// 这样标题栏与窗口是同一套色，而不是另配一套。
    /// <c>IsCustomizationSupported()</c> 在 Windows 10 上返回 false，此时直接跳过。
    /// </para>
    /// </remarks>
    internal void ApplyTitleBarTheme()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            var titleBar = _window.AppWindow.TitleBar;
            if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            if (UiKit.Brush("Surface") is not SolidColorBrush surface
                || UiKit.Brush("OnSurface") is not SolidColorBrush onSurface
                || UiKit.Brush("SurfaceContainerHigh") is not SolidColorBrush hover)
            {
                return;
            }

            titleBar.BackgroundColor = surface.Color;
            titleBar.ForegroundColor = onSurface.Color;
            titleBar.InactiveBackgroundColor = surface.Color;
            titleBar.InactiveForegroundColor = onSurface.Color;
            titleBar.ButtonBackgroundColor = surface.Color;
            titleBar.ButtonForegroundColor = onSurface.Color;
            titleBar.ButtonHoverBackgroundColor = hover.Color;
            titleBar.ButtonHoverForegroundColor = onSurface.Color;
            titleBar.ButtonPressedBackgroundColor = hover.Color;
            titleBar.ButtonPressedForegroundColor = onSurface.Color;
            titleBar.ButtonInactiveBackgroundColor = surface.Color;
            titleBar.ButtonInactiveForegroundColor = onSurface.Color;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            BootProbe.Write($"标题栏配色失败：{ex.GetType().Name}");
        }
    }

    /// <summary>应用背景材质：Mica 在 Windows 11 上可用，失败时静默回退。</summary>
    internal void TryApplyBackdrop(bool useMica)
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            _window.SystemBackdrop = useMica ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            // 旧系统或远程桌面会话下 Mica 不可用——回退到普通背景即可，不影响功能。
            _window.SystemBackdrop = null;
        }
    }
}
