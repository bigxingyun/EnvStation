using System.Globalization;
using System.Text;

using Microsoft.UI;
using EnvStation.Core.Configuration;
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

        // 构造与装配刻意分成两步，为的是让 _window 在**建界面之前**就位。
        //
        // 这不是风格问题，是一个沉默了很久的缺陷（M1-3 查出来的）：
        // MainWindow 的构造函数里要调 App.Current.TryApplyBackdrop / ApplyTitleBarTheme，
        // 而那两个方法第一句都是 if (_window is null) return;。
        // 原来的写法是「var window = new MainWindow(); _window = window;」——
        // 构造函数跑的时候 _window 还是 null，于是这两句**一次都没真正执行过**：
        // Mica 从来没接上，标题栏配色也从来没生效。两者都不抛异常、不写日志，
        // 所以谁也看不出来——表现只是"材质好像没有"和"标题栏颜色像是系统默认的"。
        // 教训：构造函数里反向调用宿主（App.Current.*）时，宿主的字段必须先就位。
        // 设置要在建窗口之前读：主题与"上次所在页面"都是构造期就要用的东西。
        // 读失败（文件损坏、版本过新）不抛异常——设置是"用起来舒服"的辅助，不是核心数据，
        // 为了它让程序起不来是本末倒置。回落原因写进探针日志，便于用户排查"我的设置为什么丢了"。
        var settings = AppSettingsStore.Load(out var settingsNote);
        if (settingsNote is { Length: > 0 })
        {
            BootProbe.Write($"设置：{settingsNote}");
        }

        var window = new MainWindow(settings);
        _window = window;

        // 窗口尺寸必须在这里定：前面构造期还没法量显示器，
        // 而且 BuildShell 之后窗口会立刻可见，尺寸再改会看到一次跳变。
        // 有保存过的尺寸就用它（并按屏幕与最小尺寸夹一次，见 AppSettings.Clamp 的注释）。
        window.ApplyDefaultWindowSize();

        // 材质要在建外壳之前接上：BuildShell 里要根据"材质是否真的生效"
        // 决定根容器铺不铺不透明底色（TD-25），先接材质才有得判断。
        TryApplyBackdrop(useMica: true);

        // 现在 _window 与材质都已就位，可以建外壳了。
        //
        // 打点是为了让"启动变慢了"这件事可诊断：探针只报"进程启动"与"窗口可见"两端的话，
        // 中间一百多毫秒全是个黑盒，谁也说不清慢在自己的外壳还是框架初始化。
        // 实测（M1-8）：`构造主窗口` → `外壳就绪` 这一段约占 107 ms，是本产品自己代码的部分。
        window.BuildShell();
        BootProbe.Write("外壳就绪（标题栏 + 导航 + 状态栏 + 页面宿主）");

        window.Navigate(window.CurrentTag);
        BootProbe.Write($"首屏导航已排入队列（{window.CurrentTag}）");

        // 让 XAML 内容铺满整窗，非客户区（标题栏）由 TitleBar 控件自己画。
        // 这一步同时接管了原生标题栏——窗口标题因此不再被系统画一遍，
        // 应用名只出现在 TitleBar 里（去重的关键）。
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(window.TitleBarInstance);

        // caption 按钮的高度跟着标题栏走：48px 的标题栏配标准高度的按钮组，
        // 最小化/最大化/关闭三个字形会整体偏上一条边。
        // 必须在 ExtendsContentIntoTitleBar 之后设，否则抛异常（官方文档明确写了）。
        window.AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

        // 标题栏由系统画在内容之上：caption 按钮的深浅与 PreferredTheme 单独要设。
        ApplyTitleBarTheme();

        // 标题栏左边的环境状态摘要：内核是否可用的第一手事实。
        window.UpdateTitleSummary();
        window.SetStatus(window.DefaultStatus);

        window.Activate();

        // 记住窗口尺寸与位置：关闭时记一次即可。
        // 不记的是"切换过程中的中间尺寸"——那没有意义，用户要的是他调好的那个。
        window.Closed += (_, _) => window.CaptureWindowGeometry();

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
    /// <para>
    /// <b>M1-3 之后这里多了一层职责</b>：内容已经铺满整窗（<c>ExtendsContentIntoTitleBar</c>），
    /// 标题栏那一块是 XAML 画的，但右上角的 caption 按钮仍是系统画的浮层。
    /// 所以还要把 <c>PreferredTheme</c> 指成与应用一致——它同时决定材质与 caption 按钮的深浅；
    /// 漏掉它就会出现「XAML 全黑、按钮区还有一个浅色的框」。
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

            // 材质生效时 caption 按钮底要留透，让 Mica 透过来；否则按钮区会是
            // 一块与窗口不同色的方块。材质不可用时退回实心底色，与根容器保持一致。
            var buttonBackground = IsBackdropActive
                ? Colors.Transparent
                : surface.Color;

            titleBar.BackgroundColor = surface.Color;
            titleBar.ForegroundColor = onSurface.Color;
            titleBar.InactiveBackgroundColor = surface.Color;
            titleBar.InactiveForegroundColor = onSurface.Color;
            titleBar.ButtonBackgroundColor = buttonBackground;
            titleBar.ButtonForegroundColor = onSurface.Color;
            titleBar.ButtonHoverBackgroundColor = hover.Color;
            titleBar.ButtonHoverForegroundColor = onSurface.Color;
            titleBar.ButtonPressedBackgroundColor = hover.Color;
            titleBar.ButtonPressedForegroundColor = onSurface.Color;
            titleBar.ButtonInactiveBackgroundColor = buttonBackground;
            titleBar.ButtonInactiveForegroundColor = onSurface.Color;

            // 标题栏由系统画在内容之上；它的深浅必须与应用同源，否则浅色按钮压在深色内容上。
            // 注意枚举值的命名：跟随系统那一档叫 UseDefaultAppMode，不叫 UseDefault。
            titleBar.PreferredTheme = CurrentTheme switch
            {
                ElementTheme.Light => Microsoft.UI.Windowing.TitleBarTheme.Light,
                ElementTheme.Dark => Microsoft.UI.Windowing.TitleBarTheme.Dark,
                _ => Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            BootProbe.Write($"标题栏配色失败：{ex.GetType().Name}");
        }
    }

    /// <summary>背景材质当前是否真的生效（决定根容器铺不铺底色，见 TD-25）。</summary>
    internal bool IsBackdropActive { get; private set; }

    /// <summary>应用背景材质：Mica 在 Windows 11 上可用，失败时静默回退。</summary>
    /// <returns>材质是否真的接上了（旧系统/远程会话返回 false）。</returns>
    /// <remarks>
    /// 返回值是 M1-3 加的：<c>SystemBackdrop</c> 赋值本身不抛异常，即使材质根本画不出来。
    /// 而根容器要不要留透完全取决于它——所以必须让调用方知道真实结果，
    /// 不能靠"没抛异常"来推断材质生效（那正是 TD-25 沉默至今的原因）。
    /// </remarks>
    internal bool TryApplyBackdrop(bool useMica)
    {
        if (_window is null)
        {
            IsBackdropActive = false;
            return false;
        }

        try
        {
            _window.SystemBackdrop = useMica ? new MicaBackdrop() : new DesktopAcrylicBackdrop();

            // Mica 是 Windows 11 的材质；Windows 10 上看不到效果，此时根容器必须自己铺底。
            IsBackdropActive = IsMicaSupported();
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            // 旧系统或远程桌面会话下 Mica 不可用——回退到普通背景即可，不影响功能。
            _window.SystemBackdrop = null;
            IsBackdropActive = false;
        }

        BootProbe.Write($"背景材质：{(useMica ? "Mica" : "Acrylic")} {(IsBackdropActive ? "已接上" : "不可用，回退不透明底色")}");
        return IsBackdropActive;
    }

    /// <summary>本机是否支持 Mica（Windows 11 build 22000 起）。</summary>
    /// <remarks>
    /// 用 <c>Environment.OSVersion</c> 而不是 <c>AnalyticsInfo</c>：后者在非打包应用里
    /// 需要额外的清单声明，缺了会抛；而版本号判断没有依赖，且这正是 Mica 的官方门槛。
    /// </remarks>
    private static bool IsMicaSupported() =>
        Environment.OSVersion.Version.Build >= 22000;
}
