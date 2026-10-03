using System.Globalization;

using EnvStation.Abstractions;
using EnvStation.App.Mvvm;
using EnvStation.Core.Configuration;
using EnvStation.Core.Diagnostics;
using AbsActions = EnvStation.Abstractions.Actions;
using CoreActions = EnvStation.Core.Actions;
using AbsPkg = EnvStation.Abstractions.Packages;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 主窗口：M3 导航外壳 + 页面宿主。
/// </summary>
/// <remarks>
/// <para>
/// <b>界面用 C# 构建，不用 XAML 文件。</b>这是刻意的取舍：本期目标是"把内核能力完整暴露出来
/// 并实测性能"，而不是视觉打磨。代码构建的界面没有 XAML 编译环节，构建失败点更少；
/// 设计稿定稿后再按令牌逐页替换为 XAML 是纯机械工作。
/// </para>
/// <para>
/// <b>桌面密度</b>：列表项 48px、按钮 36px、页面边距 24px —— 对应 UI设计规范 3.3 节的
/// <c>VisualDensity.compact = (-2, -2)</c>。纪律是<b>只压缩垂直节奏，不动字号</b>。
/// </para>
/// </remarks>
internal sealed partial class MainWindow : Window
{
    private readonly KernelBridge? _bridge;
    private readonly Abstractions.Diagnostics.FindingBag _startupFindings = new();

    /// <summary>
    /// 界面访问内核的入口（<see cref="IKernelService"/>），供视图模型使用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>它是纪律 X-1 的落点</b>：新页面（环境就绪、待处理问题）只拿这个接口，
    /// 不直接碰 <c>KernelBridge</c>。因此它们能脱离真实内核测试，
    /// 而"界面能对机器做什么"也只剩一处需要审。
    /// </para>
    /// <para>
    /// 内核建不起来时它仍然非空，只是 <c>IsAvailable</c> 为 false——
    /// 视图模型据此落到错误态，而不是永久停在加载中。
    /// </para>
    /// </remarks>
    private readonly IKernelService _kernel;

    /// <summary>写操作统一入口：预演 → 确认 → 执行。</summary>
    private readonly ApplySession _apply;

    /// <summary>
    /// 当前的用户设置（主题、窗口尺寸、上次所在页面）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>它是本轮才有的东西</b>：在此之前整个工程没有任何设置持久化，主题与窗口状态重启即丢。
    /// 设置由 <see cref="App"/> 在启动时读一次、经构造函数交进来，之后每次改动就地保存。
    /// </para>
    /// <para>
    /// 刻意做成可变字段而不是"退出时统一保存"：崩溃或强杀时后者会丢掉全部改动，
    /// 而用户改完主题立刻看到界面变色、下次启动还在——这个反馈闭环不能等到退出。
    /// </para>
    /// </remarks>
    private AppSettings _settings;

    /// <summary>当前设置（只读快照）。</summary>
    internal AppSettings CurrentSettings => _settings;

    /// <summary>
    /// 页面宿主：一个装着<b>全部已建页面</b>的网格，靠可见性切换当前页。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不是 ContentControl 每次换 Content</b>：换内容意味着把页面从可视树上摘下来、
    /// 再挂回去，而"重新挂上去"要付一整棵树的首次测量与模板展开——实测这正是切页最贵的一段
    /// （轻页面 7~22 ms，动作库 30 ms 以上，而造对象只要 0~10 ms）。
    /// 页面一直挂在树上、只切可见性，就没有这段代价。
    /// </para>
    /// <para>
    /// 放进 NavigationView 的内容区，导航栏收放时自动重排。
    /// </para>
    /// </remarks>
    private readonly Grid _host = new();

    /// <summary>
    /// 根容器。<b>刻意复用同一个实例</b>：主题是设在根元素上的（<c>RequestedTheme</c>），
    /// 每次切换主题都换一个新的根，就会把刚设好的主题丢掉。所以重建的是它的子元素，不是它自己。
    /// </summary>
    private readonly Grid _root = new();

    private TextBlock _status = new();
    private string _statusText = string.Empty;
    private string _currentTag = "overview";

    /// <summary>
    /// 官方 <see cref="TitleBar"/> 控件（Windows App SDK 1.7+）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么换成它</b>：应用自己画的 Header 与 Windows 原生标题栏都在显示应用名，
    /// 一屏之内出现两遍（实测 30 + 89 逻辑像素）。原生标题栏又由 DWM 绘制，
    /// 与应用自身的深浅主题是两套颜色。官方控件把这两件事合成一件：
    /// 非客户区交给 XAML，于是标题栏与应用同色，也不再有第二份应用名。
    /// </para>
    /// <para>
    /// 每次重建外壳都会换一个新实例（画刷在构造时解析，旧实例的颜色已经过期），
    /// 所以引用是字段而不是局部变量——<c>SetTitleBar</c> 每次都要重新指一次。
    /// </para>
    /// </remarks>
    private TitleBar? _titleBar;

    /// <summary>标题栏左侧的环境状态摘要（M2 接真实数据，本阶段是占位）。</summary>
    private TextBlock _titleSummary = new();

    /// <summary>性能自检页里那张 1000 项列表（供启动探针测滚动帧率）。</summary>
    private ListView? _perfListView;

    /// <param name="settings">
    /// 启动时读到的用户设置。由 <see cref="App"/> 读取并传入——设置文件的位置与容错策略
    /// 属于应用层的事，窗口只负责用。
    /// </param>
    internal MainWindow(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        // 窗口标题（任务栏 / Alt-Tab 用）。内容区里不再出现它——
        // 应用名只由标题栏的 TitleBar 控件画一次，这正是 M1-3 要去掉的重复。
        // 这里保留 "环境站 EnvStation" 这个更完整的形式：任务栏上没有副标题可看，
        // 英文名是用户在其他工具里认得这个程序的那一半。
        Title = "环境站 EnvStation";

        // 上次所在页面生效：用户上次在看"待处理问题"，这次打开就该回到那一页。
        // 兜底是 overview——设置文件里可能存着一个已经不存在的标签（版本回退、手工改过）。
        _currentTag = PageTags.Normalize(settings.LastPage);

        // 内核经 KernelService 建立：建不起来时它仍返回一个实例（IsAvailable=false + 原因），
        // 这样视图模型总是拿得到一个非空的服务，"内核挂了"就成了一种可显示的状态而不是空引用。
        // _bridge 保留给尚未迁移到视图模型的旧页面使用，M1-6 逐页拆完后即可删除。
        var service = KernelService.Create(_startupFindings);
        _kernel = service;
        _apply = new ApplySession(_kernel);
        _bridge = service.IsAvailable ? KernelBridge.Create(_startupFindings).Value : null;
        UiKit.Theme = ToElementTheme(settings.Theme);

        // 窗口尺寸、背景材质、外壳装配一律由 App.OnLaunched 按固定顺序驱动，
        // 不在这里做。原因见那里的长注释：构造函数跑的时候 App._window 还没就位，
        // 凡是要经过 App.Current.* 的调用都会静默变成空操作——踩过一次，代价是
        // Mica 与标题栏配色从升级前就一直是"没生效但也不报错"。
    }

    /// <summary>把主题选择映射成 XAML 的主题值。</summary>
    /// <remarks>
    /// <see cref="ElementTheme.Default"/> 的语义就是"跟随系统"，因此三态可以直接映射，
    /// 不需要自己去读系统的应用模式——少一处会过期的判断。
    /// </remarks>
    internal static ElementTheme ToElementTheme(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Dark => ElementTheme.Dark,
        ThemeChoice.Light => ElementTheme.Light,
        _ => ElementTheme.Default,
    };

    /// <summary>把设置落盘。失败只记一行日志——设置存不下不该影响用户正在做的事。</summary>
    internal void PersistSettings()
    {
        if (!AppSettingsStore.Save(_settings, out var error))
        {
            BootProbe.Write($"设置保存失败：{error}");
        }
    }

    /// <summary>
    /// 应用一个主题选择：立刻生效、整壳重建、并落盘。
    /// </summary>
    /// <remarks>
    /// 落盘放在这里而不是调用点：主题有三个入口（标题栏按钮、设置页、启动时读文件），
    /// 让每个入口各自记得保存，迟早会漏掉一个。
    /// </remarks>
    private void ApplyThemeChoice(ThemeChoice choice)
    {
        _settings = _settings with { Theme = choice };
        PersistSettings();

        var next = ToElementTheme(choice);
        App.Current.ApplyTheme(next);
        UiKit.Theme = next;

        // 画刷是在构造时解析为具体实例的，所以换主题必须整壳重建，否则颜色不变。
        BuildShell();
        Navigate(_currentTag);
        App.Current.ApplyTitleBarTheme();
        UpdateTitleSummary();

        SetStatus(choice switch
        {
            ThemeChoice.Dark => "深色主题",
            ThemeChoice.Light => "浅色主题",
            _ => "跟随系统主题",
        });
    }

    /// <summary>主题按钮：在三种状态之间轮转（跟随系统 → 深色 → 浅色 → 跟随系统）。</summary>
    private void CycleTheme()
    {
        var next = _settings.Theme switch
        {
            ThemeChoice.System => ThemeChoice.Dark,
            ThemeChoice.Dark => ThemeChoice.Light,
            _ => ThemeChoice.System,
        };

        ApplyThemeChoice(next);
    }



    private Grid BuildStatusBar()
    {
        var token = UiKit.MonoType("MonoSmall");

        // 状态栏按规范 3.1 节是「24px 等宽小字」。上一版用的是 UI 字体（比例字体），
        // 于是同一条状态栏在页面切换时宽度会跳——数字与中文混排时尤其明显。
        // 状态栏里出现的是条数、耗时、路径这类技术值，等宽才对。
        _status = new TextBlock
        {
            Text = _statusText,
            FontFamily = UiKit.MonoFont,
            FontSize = token.Size,
            LineHeight = token.LineHeight,
            Foreground = UiKit.OnSurfaceVariant,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(UiKit.Space5, 0, UiKit.Space5, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var bar = new Grid
        {
            Background = UiKit.Brush("SurfaceContainerLow"),
            MinHeight = DesignTokens.StatusBarHeight,
            Padding = new Thickness(0, UiKit.Space1, 0, UiKit.Space1),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = UiKit.Outline,
        };

        bar.Children.Add(_status);
        return bar;
    }

    /// <summary>
    /// 空闲状态文案：导航之后回到这一条，页面加载完成后再由各页覆盖成实测结果。
    /// </summary>
    /// <remarks>
    /// 刻意只写「就绪 + 动作数」这种可核对的事实。状态栏不是讲解区：
    /// 「已打开：动作库」这类文案既没信息量，又和左侧高亮的导航项重复。
    /// </remarks>
    internal string DefaultStatus =>
        _kernel.IsAvailable ? $"就绪 · {_kernel.ActionCount} 个动作" : "内核未就绪";

    /// <summary>当前窗口的 XamlRoot（确认框必需）。</summary>
    private XamlRoot? UiXamlRoot => _root.XamlRoot;

    /// <summary>
    /// 对一条待办项走完预演 → 确认 → 执行 → 复检。
    /// </summary>
    private async void RunRemedyFixAsync(RemedyItem item, string pageTag, Action reload)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(reload);

        if (UiXamlRoot is null)
        {
            SetStatus("界面尚未就绪，请稍后再试。", pageTag);
            return;
        }

        if (!item.ShouldOfferFix)
        {
            SetStatus("此项不可自动修复。", pageTag);
            return;
        }

        SetStatus($"正在预览修复：{item.Title}", pageTag);

        try
        {
            var outcome = await _apply.ApplyRemedyAsync(item, UiXamlRoot).ConfigureAwait(true);
            var suffix = outcome.Succeeded ? " 新开一个终端后再看效果。" : string.Empty;
            SetStatus(outcome.Message + suffix, pageTag);

            if (outcome.Succeeded)
            {
                reload();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetStatus("修复失败：" + ex.Message, pageTag);
        }
    }

    /// <summary>
    /// 写状态栏。不带页面标签时视为"任何页面都可以显示"。
    /// </summary>
    internal void SetStatus(string text) => SetStatus(text, null);

    /// <summary>
    /// 写状态栏，并声明这条消息属于哪个页面。
    /// </summary>
    /// <param name="text">要显示的文字。</param>
    /// <param name="pageTag">
    /// 这条消息归属的页面标签；<c>null</c> 表示与页面无关（如主题切换）。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>为什么需要页面标签</b>：上一版所有异步完成都写同一个全局字段，
    /// 于是一个慢任务（比如概览页的三个探测）在用户已经翻到动作库页之后才返回，
    /// 就会把「检测完成」盖在动作库页的状态栏上——状态栏在报告一件与当前页面无关的事。
    /// </para>
    /// <para>
    /// 判据只有一条：<b>消息属于的页面必须还是当前页面</b>。
    /// 不是"当前页面"就整条丢弃，不做排队也不做延迟显示——用户已经不在那一页了，
    /// 那条消息对他没有意义。
    /// </para>
    /// </remarks>
    internal void SetStatus(string text, string? pageTag)
    {
        if (pageTag is not null && !string.Equals(pageTag, _currentTag, StringComparison.Ordinal))
        {
            return;
        }

        _statusText = text;
        _status.Text = text;
    }

    private NavigationView BuildNavigation()
    {
        var navigation = new NavigationView
        {
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 200,

            // 56 而不是 44：与 UI设计规范 3.1 节的折叠宽度一致。
            // 44 比规范窄 12px，收起后图标显得贴边，与规范里"折叠时仅图标 + Tooltip"的排版对不上。
            CompactPaneLength = 56,
            IsPaneToggleButtonVisible = true,
        };

        // 按用途分组。上一版是六个平铺项，用户看不出"检测"和"导入包"是什么关系；
        // 分组之后导航栏自己回答了"这个程序分几块"。
        navigation.MenuItems.Add(GroupHeader("诊断"));
        navigation.MenuItems.Add(Item("概览", "\uE80F", PageTags.Overview));
        navigation.MenuItems.Add(Item("环境检测", "\uE9D9", PageTags.Doctor));
        navigation.MenuItems.Add(Item("运行时", "\uE950", PageTags.Runtime));

        navigation.MenuItems.Add(new NavigationViewItemSeparator());
        navigation.MenuItems.Add(GroupHeader("配置"));
        navigation.MenuItems.Add(Item("环境变量", "\uE8EF", PageTags.Env));
        navigation.MenuItems.Add(Item("导入包", "\uE896", PageTags.Packages));
        navigation.MenuItems.Add(Item("快照与回滚", "\uE81C", PageTags.History));

        navigation.MenuItems.Add(new NavigationViewItemSeparator());
        navigation.MenuItems.Add(GroupHeader("工具"));
        navigation.MenuItems.Add(Item("设置", "\uE713", PageTags.Settings));

        // 先选中、再挂事件：顺序反了会在启动时多走一遍 Navigate，
        // 页面被构建两次、概览页的探测动作也跟着跑两遍。
        // 页面构建由调用方在 BuildShell 之后显式 Navigate 一次。
        navigation.SelectedItem = navigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => (item.Tag as string) == _currentTag)
            ?? navigation.MenuItems.OfType<NavigationViewItem>().First();

        navigation.SelectionChanged += (_, args) =>
        {
            // SelectedItem 为 null 是"清空高亮"（目标页没有导航项），不是导航意图。
            // 不拦这一下会以 null 标签去 Navigate，把当前页冲掉。
            if (args.SelectedItem is NavigationViewItem { Tag: string tag } && tag.Length > 0)
            {
                Navigate(tag);
            }
        };

        _navigation = navigation;

        // 页面宿主放进 NavigationView 的内容区：这样导航栏收起/展开时页面自动重排。
        navigation.Content = _host;
        return navigation;
    }

    /// <summary>
    /// 让左侧高亮跟随当前页面。
    /// </summary>
    /// <remarks>
    /// 点击导航项时高亮由控件自己维护，但"程序内导航"（主题切换后重建外壳、截图工具指定页面、
    /// 设置页上的入口）不经过点击，高亮就会停在原地——表现为"人在动作库页、高亮在概览"。
    /// 与其在每个调用点记得手动同步，不如让 Navigate 负责把高亮拉回来。
    /// </remarks>
    private void SyncNavigationSelection()
    {
        if (_navigation is null)
        {
            return;
        }

        var target = _navigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => (item.Tag as string) == _currentTag);

        // 目标页没有对应的导航项时（性能自检页就是这种），必须把选中<b>清掉</b>。
        //
        // 为什么：NavigationView 只在选中项真的变化时才抛 SelectionChanged。
        // 若这里留着上一次的选中项（比如"设置"），就会出现两件坏事：
        //   ① 高亮停在"设置"上，而屏幕上是性能自检页——界面在骗人；
        //   ② 用户再点"设置"，因为选中项没变化，事件不触发，点了没反应——
        //      而返回按钮又是收起的，于是这一页变成一个死胡同。
        // 清空选中之后，第 ② 条的"再点一次"会真的触发事件，用户有路可走。
        if (target is null)
        {
            if (_navigation.SelectedItem is not null)
            {
                _navigation.SelectedItem = null;
            }

            return;
        }

        if (!ReferenceEquals(_navigation.SelectedItem, target))
        {
            _navigation.SelectedItem = target;
        }
    }

    /// <summary>导航控件（用于同步高亮）。</summary>
    private NavigationView? _navigation;

    /// <summary>
    /// 构造一个导航项。
    /// </summary>
    /// <remarks>
    /// <b>必须设 <c>ToolTip</c></b>：导航栏收起后只剩图标，没有提示就是一排不知含义的方块。
    /// 上一版整个工程没有一处 <c>ToolTip</c>（见《重构与优化方案.md》4.3 节），
    /// 而收起态又是键盘用户与窄窗口下的常态。
    /// </remarks>
    private static NavigationViewItem Item(string text, string glyph, string tag)
    {
        var item = new NavigationViewItem
        {
            Content = text,
            Tag = tag,
            Icon = new FontIcon { Glyph = glyph, FontSize = 16 },
            MinHeight = UiKit.ListItemHeight,
        };

        ToolTipService.SetToolTip(item, text);
        return item;
    }

    /// <summary>导航分组标题（只在展开态显示）。</summary>
    private static NavigationViewItemHeader GroupHeader(string text) => new() { Content = text };


    /// <summary>
    /// 切页：先让点击立刻得到响应，再把页面的构建放到本帧渲染之后。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须分两拍</b>：页面是纯 C# 构建的，动作库一页要造三百多个元素。
    /// 如果这段构建跟着点击同步跑，UI 线程就被占住了——实测动作库 28~30 ms、性能自检 33 ms，
    /// 而一个 60Hz 帧只有 16.7 ms。也就是说<b>每次切到这两页都要丢 1~2 帧</b>，
    /// 表现为"点了之后顿一下才出来"。丢帧的是导航，不是内容。
    /// </para>
    /// <para>
    /// 拆成两拍之后：点击当帧只做导航高亮与状态栏（微秒级），构建排在
    /// <see cref="DispatcherQueuePriority.Low"/>——低优先级排在当帧渲染之后才执行，
    /// 于是首帧按时提交，内容紧随其后出现。用户感知到的是"立刻响应"，而不是"卡了一下"。
    /// </para>
    /// <para>
    /// 页面本身不做缓存：构建耗时已经不在关键路径上，而缓存会带来另一串问题
    /// （数据陈旧、主题切换后画刷过期、性能自检页要重新登记列表），
    /// 收益远小于维护成本。
    /// </para>
    /// </remarks>
    internal void Navigate(string tag)
    {
        _currentTag = tag;

        // 记住上次所在页面：下次打开回到这一页。
        // 只记产品页面——开发专用的性能自检页不该出现在下次启动的落点上。
        if (PageTags.IsKnown(tag) && !string.Equals(_settings.LastPage, tag, StringComparison.Ordinal))
        {
            _settings = _settings with { LastPage = tag };
            PersistSettings();
        }

        if (BootProbe.Enabled)
        {
            var trace = new System.Diagnostics.StackTrace(1, fNeedFileInfo: false);
            var frames = trace.GetFrames()
                .Take(3)
                .Select(static f => $"{f.GetMethod()?.DeclaringType?.Name}.{f.GetMethod()?.Name}")
                .ToArray();
            BootProbe.Write($"Navigate({tag}) 来自 {string.Join(" <- ", frames)}");
        }

        // 导航本身立刻生效：高亮、状态栏都不该等页面构建。
        SetStatus(DefaultStatus);
        SyncNavigationSelection();

        // 上一次还没落地的构建就此作废——用户已经点去别处了，
        // 再把旧页面贴进内容区就是"点在动作库、显示的是设置"。
        _pendingBuild?.TrySetResult();
        _pendingBuild = new TaskCompletionSource();

        _ = BuildCurrentPageAsync(tag, _pendingBuild);
    }

    /// <summary>等当前这一次导航的页面构建落地（测量入口用；正常导航不需要等）。</summary>
    internal Task WaitForPageAsync() => _pendingBuild?.Task ?? Task.CompletedTask;

    /// <summary>
    /// 按环境变量把界面开到指定状态（截图工具用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ENVSTATION_BOOT_UI_TAG</c> 指定页面，<c>ENVSTATION_BOOT_UI_THEME</c> 指定 <c>light</c>/<c>dark</c>。
    /// 两个都不设时本方法什么都不做——截图是人工审视的手段，不该成为常态路径的一部分。
    /// </para>
    /// <para>
    /// <b>为什么截图需要一个应用内的开关</b>：界面是自己画的，外部脚本只能拍"启动后停在的那一页"。
    /// 要审视动作库、导入包这些页面的视觉结果，就必须让应用自己走过去；
    /// 而"点一下导航项"这件事没法从外部可靠地做——坐标会随窗口尺寸与缩放变化。
    /// </para>
    /// </remarks>
    internal async Task ApplyStartupUiStateAsync()
    {
        var tag = System.Environment.GetEnvironmentVariable("ENVSTATION_BOOT_UI_TAG");
        var theme = System.Environment.GetEnvironmentVariable("ENVSTATION_BOOT_UI_THEME");

        if (string.IsNullOrWhiteSpace(tag) && string.IsNullOrWhiteSpace(theme))
        {
            return;
        }

        try
        {
            if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
                || string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
            {
                var next = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
                    ? ElementTheme.Dark
                    : ElementTheme.Light;

                if (UiKit.Theme != next)
                {
                    App.Current.ApplyTheme(next);
                    UiKit.Theme = next;
                    BuildShell();
                    App.Current.ApplyTitleBarTheme();
                    UpdateTitleSummary();
                }
            }

            if (!string.IsNullOrWhiteSpace(tag))
            {
                Navigate(tag);
                await WaitForPageAsync().ConfigureAwait(true);
            }

            // 给滚动条、异步内容一点时间落定，避免拍到"正在加载"的中间态。
            await Task.Delay(600).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            BootProbe.Write($"界面状态设定失败：{ex.GetType().Name}");
        }
    }

    /// <summary>把页面构建排到低优先级；构建完成或作废后结束 <paramref name="completion"/>。</summary>
    private Task BuildCurrentPageAsync(string tag, TaskCompletionSource completion)
    {
        // Low 优先级是关键：它排在当帧的渲染之后，所以首帧不会被这一段构建挡在后面。
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                // 期间用户可能又点了别处：只有仍然是"当前这一页"时才落地。
                if (_currentTag != tag)
                {
                    return;
                }

                var buildClock = System.Diagnostics.Stopwatch.StartNew();
                var wasCached = _pageCache.ContainsKey(tag);
                var page = GetOrBuildPage(tag);
                buildClock.Stop();

                var swapped = ShowPage(tag, page);

                if (BootProbe.Enabled)
                {
                    BootProbe.Write(
                        $"页面 {tag}：{(wasCached ? "复用" : "首次构建")} · " +
                        $"造对象 {buildClock.Elapsed.TotalMilliseconds:F1} ms · {(swapped ? "已切换" : "内容未变")}");
                }

                // 只记"造对象"这一段：剩下的部分是布局与出帧，由测量方自己量。
                if (PageLoadedHook is not null)
                {
                    PageBuildMs = buildClock.Elapsed.TotalMilliseconds;
                }

                // 测量用：页面首次挂上可视树时通知一声。
                // 页面一直留在树上，所以这个事件每页只会触发一次——重复导航时靠超时兜底。
                if (PageLoadedHook is { } hook && swapped && page is FrameworkElement pageRoot)
                {
                    pageRoot.Loaded += (_, _) => hook.TrySetResult();
                }

                if (BootProbe.Enabled)
                {
                    var missing = UiKit.AuditTokens();
                    BootProbe.Write(missing.Length == 0
                        ? $"页面 {tag} 已构建，令牌 {UiKit.ThemeKey} 全部解析成功"
                        : $"页面 {tag} 已构建，但以下令牌解析失败：{string.Join('、', missing)}");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                or System.Runtime.InteropServices.COMException)
            {
                // 页面构建失败不能让整个外壳失效：把错误显示在内容区，导航仍然可用。
                ShowErrorPage("页面构建失败：" + ex.Message);
                BootProbe.Write($"页面 {tag} 构建失败：{ex.GetType().Name}");
            }
            finally
            {
                completion.TrySetResult();
            }
        }))
        {
            completion.TrySetResult();
        }

        return completion.Task;
    }

    /// <summary>当前这一次导航的构建凭据。</summary>
    private TaskCompletionSource? _pendingBuild;

    /// <summary>
    /// 已经建好的页面（按标签缓存）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须缓存</b>：实测"造对象"只要 2~7 ms，而<b>把它排进布局并画出来要 25~81 ms</b>。
    /// 也就是说每次切页真正贵的不是构建，而是那棵树的首次测量与模板展开。
    /// 每一页都留着实例，"回到刚才那页"就只剩一次内容替换，不再有首次布局的代价。
    /// </para>
    /// <para>
    /// <b>为什么不担心数据陈旧</b>：页面里的数据都是只读检测结果或快照列表，
    /// 进入只读页面＝看一眼当前状态，重新导航一次就是重新读一次；而写入类操作
    /// （校验、试运行、执行）都在页面上的按钮后面，用户自己会再点一次。
    /// 需要刷新的页面自己在按钮里调刷新方法，不依赖"每次重建"这件事。
    /// </para>
    /// <para>
    /// <b>主题一变必须整册作废</b>：画刷是在构造页面时解析成具体实例的，
    /// 留着旧页面就会出现"深色外壳里嵌着一页浅色内容"。因此缓存以主题为键，
    /// 切主题时整册清空——这也正是整壳重建时本来就要付的一次代价。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, UIElement> _pageCache = new(StringComparer.Ordinal);

    /// <summary>页面缓存在哪个主题下建立的（不一致则整册作废）。</summary>
    private string _pageCacheTheme = string.Empty;

    /// <summary>
    /// 作废并重建当前页（供"首次进入"测量使用）。
    /// </summary>
    /// <remarks>
    /// 页面缓存让"回到刚才那页"变得极快，但也让"第一次进入"这条路测量不到。
    /// 这个入口只服务于测量：把当前页从缓存与宿主里拿掉，下一次导航就是一次真正的首次进入。
    /// </remarks>
    internal void EvictCurrentPage()
    {
        if (_pageCache.Remove(_currentTag, out var page))
        {
            _host.Children.Remove(page);
        }

        _shownTag = null;
    }

    /// <summary>取页面：缓存里有就用缓存，没有就建一次并留下。</summary>
    private UIElement GetOrBuildPage(string tag)
    {
        if (!string.Equals(_pageCacheTheme, UiKit.ThemeKey, StringComparison.Ordinal))
        {
            _pageCache.Clear();
            _pageCacheTheme = UiKit.ThemeKey;
        }

        if (_pageCache.TryGetValue(tag, out var cached))
        {
            return cached;
        }

        // 路由只此一份实现（BuildPageForTag），测量与正常导航共用——
        // 两处各写一份 switch，早晚会出现"测的是这一页、显示的是那一页"。
        var page = tag == PageTags.Perf && BootProbe.MeasurementEnabled
            ? BuildPerfPage(mounted: true)
            : BuildPageForTag(tag);

        _pageCache[tag] = page;
        return page;
    }

    /// <summary>整册作废（换主题、重建外壳时调用）。</summary>
    private void InvalidatePageCache()
    {
        _pageCache.Clear();
        _host.Children.Clear();
        _shownTag = null;
        _pageCacheTheme = string.Empty;
    }

    /// <summary>
    /// 把某一页显示出来：首次则挂进宿主，之后只切可见性。
    /// </summary>
    /// <returns>是否真的发生了切换（内容未变时为 false）。</returns>
    /// <remarks>
    /// 页面全程留在可视树上，所以"切回来"不涉及摘挂与重新测量——
    /// 这正是切页最贵的那一段。代价是每一页都占着内存，
    /// 对七个页面、每个几百个元素来说完全可以接受。
    /// </remarks>
    private bool ShowPage(string tag, UIElement page)
    {
        if (string.Equals(_shownTag, tag, StringComparison.Ordinal))
        {
            return false;
        }

        if (!_host.Children.Contains(page))
        {
            _host.Children.Add(page);
        }

        foreach (var child in _host.Children)
        {
            child.Visibility = ReferenceEquals(child, page) ? Visibility.Visible : Visibility.Collapsed;
        }

        _shownTag = tag;
        return true;
    }

    /// <summary>用一个错误页替换内容区（内核或页面构建失败时）。</summary>
    private void ShowErrorPage(string message)
    {
        _pageCache.Clear();
        _host.Children.Clear();
        _host.Children.Add(UiKit.Scroll(UiKit.Body(message, secondary: true)));
        _shownTag = null;
    }

    /// <summary>当前显示的页面标签。</summary>
    private string? _shownTag;

}
