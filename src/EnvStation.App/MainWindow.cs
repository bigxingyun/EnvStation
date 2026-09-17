using System.Globalization;

using EnvStation.Abstractions;
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
internal sealed class MainWindow : Window
{
    private readonly KernelBridge? _bridge;
    private readonly Abstractions.Diagnostics.FindingBag _startupFindings = new();

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

    /// <summary>性能自检页里那张 1000 项列表（供启动探针测滚动帧率）。</summary>
    private ListView? _perfListView;

    internal MainWindow()
    {
        Title = "环境站 EnvStation";

        var bridge = KernelBridge.Create(_startupFindings);
        _bridge = bridge.IsSuccess ? bridge.Value : null;
        UiKit.Theme = App.Current.CurrentTheme;

        ApplyDefaultWindowSize();

        BuildShell();
        Navigate(_currentTag);

        // Mica 是 Windows 11 的材质；旧系统或远程会话下不可用，由 App 内部静默回退。
        App.Current.TryApplyBackdrop(useMica: true);

        // 标题栏（非客户区）的深浅必须单独设：它由窗口而非 XAML 主题决定，
        // 不设的话深色主题下标题栏仍是白的，和窗口里外不是一套。
        App.Current.ApplyTitleBarTheme();

        SetStatus(_bridge is null
            ? "内核初始化失败"
            : DefaultStatus);
    }

    /// <summary>
    /// 设定默认窗口尺寸并居中。
    /// </summary>
    /// <remarks>
    /// WinUI 的默认窗口尺寸只有 1152×592，竖着放不下"卡 1 + 卡 2 + 按钮条"，
    /// 用户第一眼就要滚动——这对一个"检测报告 + 能力授权"的界面是很差的初印象。
    /// 这里按工作区取尺寸并设下限：小屏笔记本上不至于比屏幕还大，大屏上也不至于浪费。
    /// </remarks>
    private void ApplyDefaultWindowSize()
    {
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea
                .GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary)
                .WorkArea;

            var width = Math.Clamp(1240, 900, Math.Max(900, area.Width - 160));
            var height = Math.Clamp(820, 620, Math.Max(620, area.Height - 120));

            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            AppWindow.Move(new Windows.Graphics.PointInt32(
                area.X + ((area.Width - width) / 2),
                area.Y + ((area.Height - height) / 2)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            // 拿不到显示器信息（远程会话、无头环境）就一直用系统默认尺寸——不是致命问题。
            BootProbe.Write($"窗口尺寸设定失败：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 构建（或重建）窗口外壳。主题切换后必须整壳重建：
    /// 所有画刷都是在构造时解析成具体 Brush 实例的，不重建就还是旧主题的颜色。
    /// </summary>
    private void BuildShell()
    {
        // 先把页面宿主从旧外壳上摘下来。
        // 为什么必须摘：_host 是同一个实例被反复复用，而"从可视树里移除父级"并不会
        // 解除 ContentControl.Content 对它的持有。换主题时新建 NavigationView 再赋
        // navigation.Content = _host，WinRT 会直接抛
        // 「Element is already the child of another element」——整壳重建的第一步就炸。
        foreach (var child in _root.Children)
        {
            if (child is NavigationView previous && ReferenceEquals(previous.Content, _host))
            {
                previous.Content = null;
            }
        }

        _root.Children.Clear();
        _root.RowDefinitions.Clear();

        // 外壳重建 = 主题变了（或首次启动）：页面里解析过的画刷已经过期，整册作废。
        InvalidatePageCache();

        // 根容器必须自己铺一层不透明底色。
        // 为什么：本应用开了 Mica，而 Mica 的深浅由**窗口**决定，不随 XAML 的 RequestedTheme 走。
        // 不铺底色的话，切到深色主题后 XAML 文字变浅、背后却还是浅色 Mica——
        // 结果是导航项"消失"（浅字画在浅底上）。真机实测踩到过这一幕。
        _root.Background = UiKit.Surface;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader();
        var navigation = BuildNavigation();
        var statusBar = BuildStatusBar();

        Grid.SetRow(header, 0);
        Grid.SetRow(navigation, 1);
        Grid.SetRow(statusBar, 2);

        _root.Children.Add(header);
        _root.Children.Add(navigation);
        _root.Children.Add(statusBar);

        Content = _root;
    }

    private Grid BuildHeader()
    {
        var header = new Grid
        {
            Background = UiKit.Brush("SurfaceContainer"),
            MinHeight = UiKit.TopAppBarHeight,
            Padding = new Thickness(UiKit.Space6, UiKit.Space4, UiKit.Space6, UiKit.Space4),
        };

        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleBlock = new StackPanel { Spacing = 2 };
        titleBlock.Children.Add(new TextBlock
        {
            Text = "环境站",
            FontFamily = UiKit.UiFont,
            FontSize = UiKit.Type("TitleMedium").Size,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiKit.OnSurface,
        });
        titleBlock.Children.Add(UiKit.Body(
            "Windows 开发环境配置与回滚", secondary: true));

        var themeButton = UiKit.SecondaryButton("主题", ToggleTheme);
        themeButton.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumn(themeButton, 1);
        header.Children.Add(titleBlock);
        header.Children.Add(themeButton);
        return header;
    }

    private Grid BuildStatusBar()
    {
        var token = UiKit.Type("LabelMedium");

        _status = new TextBlock
        {
            Text = _statusText,
            FontFamily = UiKit.UiFont,
            FontSize = token.Size,
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

    private NavigationView BuildNavigation()
    {
        var navigation = new NavigationView
        {
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 200,
            CompactPaneLength = 44,
        };

        navigation.MenuItems.Add(Item("概览", "\uE80F", "overview"));
        navigation.MenuItems.Add(Item("环境检测", "\uE9D9", "doctor"));
        navigation.MenuItems.Add(Item("导入包", "\uE896", "packages"));
        navigation.MenuItems.Add(Item("快照与回滚", "\uE81C", "history"));
        navigation.MenuItems.Add(Item("动作库", "\uE8F1", "actions"));
        navigation.MenuItems.Add(Item("设置", "\uE713", "settings"));

        // 先选中、再挂事件：顺序反了会在启动时多走一遍 Navigate，
        // 页面被构建两次、概览页的探测动作也跟着跑两遍。
        // 页面构建由调用方在 BuildShell 之后显式 Navigate 一次。
        navigation.SelectedItem = navigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => (item.Tag as string) == _currentTag)
            ?? navigation.MenuItems[0];

        navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: string tag })
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

        if (target is not null && !ReferenceEquals(_navigation.SelectedItem, target))
        {
            _navigation.SelectedItem = target;
        }
    }

    /// <summary>导航控件（用于同步高亮）。</summary>
    private NavigationView? _navigation;

    private static NavigationViewItem Item(string text, string glyph, string tag) => new()
    {
        Content = text,
        Tag = tag,
        Icon = new FontIcon { Glyph = glyph, FontSize = 16 },
        MinHeight = UiKit.ListItemHeight,
    };

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
    private void Navigate(string tag)
    {
        _currentTag = tag;

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

        var page = tag switch
        {
            "doctor" => BuildDoctorPage(),
            "packages" => BuildPackagesPage(),
            "history" => BuildHistoryPage(),
            "actions" => BuildActionsPage(),
            "settings" => BuildSettingsPage(),
            "perf" => BuildPerfPage(mounted: true),
            _ => BuildOverviewPage(),
        };

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

    /// <summary>
    /// 从"点击"到"新页面第一帧画出来"之间发生了什么。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是唯一能回答"切页到底卡不卡"的测法：<c>点击响应</c>只说明处理器快不快，
    /// <c>构建耗时</c>只说明代码跑了多久；用户感知的是<b>画面什么时候换过去</b>，
    /// 而中间还有一趟布局与一次出帧。
    /// </para>
    /// <para>
    /// 同时数帧间隔：构建与布局都跑在 UI 线程上，只要它们超过 16.7 ms，
    /// 那一帧就交不出去——这正是"点了之后画面顿一下才换"的成因。
    /// </para>
    /// </remarks>
    internal async Task<string> MeasureClickToPaintAsync(string tag)
    {
        var gaps = new List<double>(120);
        var last = System.Diagnostics.Stopwatch.StartNew();
        var worst = 0.0;

        void OnRendering(object? sender, object e)
        {
            var gap = last.Elapsed.TotalMilliseconds;
            last.Restart();
            gaps.Add(gap);

            if (gap > worst)
            {
                worst = gap;
            }
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var loaded = new TaskCompletionSource();
        PageLoadedHook = loaded;

        // 先把上一次导航彻底落定：否则"点击响应"里会混进上一页还没跑完的构建。
        await WaitForPageAsync().ConfigureAwait(true);

        double clickMs;
        var loadedMs = 0.0;
        var reusedFromCache = false;

        try
        {
            Navigate(tag);
            clickMs = clock.Elapsed.TotalMilliseconds;

            // 等到新页面真的挂上可视树（Loaded 在首次布局与渲染之前触发）。
            // 缓存命中时内容没有发生替换，Loaded 不会再触发——所以必须带超时，
            // 否则测量自己会挂死在这里（第一次加缓存时就是这样，整个自检卡在第 45 秒）。
            var finished = await Task.WhenAny(loaded.Task, Task.Delay(1500)).ConfigureAwait(true);
            reusedFromCache = !ReferenceEquals(finished, loaded.Task);
            loadedMs = clock.Elapsed.TotalMilliseconds;

            // 再等几帧，把构建 + 布局 + 出帧这一段完整覆盖住。
            await Task.Delay(300).ConfigureAwait(true);
            clock.Stop();
        }
        finally
        {
            PageLoadedHook = null;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        var janky = gaps.Count(static gap => gap > 25.0);
        var paintMs = loadedMs - clickMs;

        if (reusedFromCache)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[{tag}] 复用已建页面：点击响应 {clickMs:F1} ms，无首次布局 · " +
                $"最长帧间隔 {worst:F1} ms · 卡顿 {janky} 帧 / 采样 {gaps.Count} 帧");
        }

        var layoutMs = paintMs - PageBuildMs;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{tag}] 点击 → 页面挂载 {paintMs:F1} ms · 其中造对象 {PageBuildMs:F1} + 布局出帧 {layoutMs:F1} · " +
            $"最长帧间隔 {worst:F1} ms · 卡顿 {janky} 帧 / 采样 {gaps.Count} 帧");
    }

    /// <summary>页面挂载回调；由 <c>Navigate</c> 在设置新内容时挂到页面根上。</summary>
    private static TaskCompletionSource? PageLoadedHook;

    /// <summary>最近一次页面"造对象"的耗时（不含布局与出帧）。</summary>
    private static double PageBuildMs;

    private void ToggleTheme()
    {
        var next = UiKit.Theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;

        App.Current.ApplyTheme(next);
        UiKit.Theme = next;

        // 画刷是在构造时解析为具体实例的，所以换主题必须整壳重建，否则颜色不变。
        BuildShell();
        Navigate(_currentTag);
        App.Current.ApplyTitleBarTheme();

        SetStatus(next == ElementTheme.Dark ? "深色主题" : "浅色主题");
    }

    /// <summary>
    /// 空闲状态文案：导航之后回到这一条，页面加载完成后再由各页覆盖成实测结果。
    /// </summary>
    /// <remarks>
    /// 刻意只写"就绪 + 动作数"这种可核对的事实。状态栏不是讲解区：
    /// 「已打开：动作库」这类文案既没信息量，又和左侧高亮的导航项重复。
    /// </remarks>
    private string DefaultStatus =>
        _bridge is null ? "内核未就绪" : $"就绪 · {_bridge.ActionCount} 个动作";

    internal void SetStatus(string text)
    {
        _statusText = text;
        _status.Text = text;
    }

    // ══════════════════════════ 性能自检（M0-P01 的测量入口） ══════════════════════════

    /// <summary>页面切换计时：构建 + 强制布局，单位毫秒。</summary>
    /// <remarks>
    /// 只对"构建 + 布局"计时，<b>不含 GPU 出帧</b>——出帧时刻在托管侧拿不到。
    /// 所以这个数是乐观下界，报告里必须写明；真实体感由录屏或 PresentMon 才能覆盖。
    /// </remarks>
    /// <summary>
    /// 页面切换计时：构建 + 强制布局，单位毫秒。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只对"构建 + 布局"计时，<b>不含 GPU 出帧</b>——出帧时刻在托管侧拿不到。
    /// 所以这个数是乐观下界，报告里必须写明。
    /// </para>
    /// <para>
    /// <b>这个数必须分成两段看</b>：<c>点击响应</c>是当帧真正被占住的时间（用户能感知的卡顿），
    /// <c>页面构建</c>是排在渲染之后的那一段（用户看到的是内容稍后出现，而不是点了没反应）。
    /// 只报一个合计数会把这两种完全不同的体感混为一谈——这正是第一版报告里
    /// "页面切换 30 ms"看起来不严重、实际每次都要丢两帧的原因。
    /// </para>
    /// </remarks>
    internal async Task<(double ClickMs, double TotalMs)> MeasureNavigateAsync(string tag)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Navigate(tag);
        var clickMs = clock.Elapsed.TotalMilliseconds;

        await WaitForPageAsync().ConfigureAwait(true);
        _root.UpdateLayout();
        clock.Stop();

        BootProbe.Write(
            $"页面 {tag} 点击响应 {clickMs:F1} ms + 构建 {clock.Elapsed.TotalMilliseconds - clickMs:F1} ms " +
            $"= 合计 {clock.Elapsed.TotalMilliseconds:F1} ms");
        return (clickMs, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 主题切换计时。
    /// </summary>
    /// <remarks>
    /// <b>必须把"重新显示当前页"算进去</b>：页面缓存让切页变快了，但也意味着换主题那一刻
    /// 缓存里全是旧画刷的页面、必须整册作废。用户按主题按钮时，感知到的是
    /// "窗口换色 + 当前页重新画出来"，只计前半段会把最贵的那一段漏掉。
    /// </remarks>
    internal async Task<double> MeasureThemeToggleAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ToggleTheme();
        await WaitForPageAsync().ConfigureAwait(true);
        _root.UpdateLayout();
        clock.Stop();

        BootProbe.Write($"主题切换（含整壳重建 + 当前页重画 + 布局）{clock.Elapsed.TotalMilliseconds:F1} ms");
        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>列出全部页面的构建耗时（自检脚本用）。</summary>
    internal void MeasureNavigationBuild()
    {
        foreach (var tag in (string[])["overview", "doctor", "packages", "history", "actions", "settings", "perf"])
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var page = tag switch
            {
                "doctor" => BuildDoctorPage(),
                "packages" => BuildPackagesPage(),
                "history" => BuildHistoryPage(),
                "actions" => BuildActionsPage(),
                "settings" => BuildSettingsPage(),
                "perf" => BuildPerfPage(),
                _ => BuildOverviewPage(),
            };
            clock.Stop();
            BootProbe.Write($"页面 {tag} 仅构建 {clock.Elapsed.TotalMilliseconds:F1} ms（未挂载）");
            GC.KeepAlive(page);
        }
    }

    /// <summary>
    /// 性能自检页：1000 项虚拟化列表 + 5000 行日志面板 + 帧率实测。
    /// </summary>
    /// <remarks>
    /// 这不是给用户看的页面，而是 M0-P01 要求的测量载体：
    /// 「1000 项列表滚动稳定 60FPS、无 &gt;100ms 长任务」这条阈值只有在真实的高密度列表上才测得出来，
    /// 空窗口的数字没有意义（M0技术验证任务书 P01 的页面要求）。
    /// 之所以做成产品内的一个页面而不是单独的测试程序：这样测的就是真实视图栈，
    /// 而不是一个恰好也用 WinUI 的样板工程。
    /// </remarks>
    private UIElement BuildPerfPage(bool mounted = false)
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("性能自检"));
        page.Children.Add(UiKit.Body(
            "1000 项列表与 5000 行日志，用于实测滚动帧率与长任务。", secondary: true));

        // ── 1000 项虚拟化列表 ──
        var list = new ListView
        {
            Height = 300,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
        };

        var items = new List<string>(1000);
        for (var i = 1; i <= 1000; i++)
        {
            items.Add($"PATH 项 {i:D4} · D:\\tools\\runtime-{i % 37:D2}\\bin");
        }

        list.ItemsSource = items;

        var (listCard, listBody) = UiKit.CardWithBody(UiKit.Space2);
        listBody.Children.Add(UiKit.SectionLabel("1000 项列表"));
        listBody.Children.Add(list);
        page.Children.Add(listCard);

        // ── 5000 行日志面板 ──
        var logList = new ListView
        {
            Height = 220,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
        };

        var lines = new List<string>(5000);
        for (var i = 1; i <= 5000; i++)
        {
            lines.Add($"[{i:D5}] envstation.step 执行中 · 状态 ok · 耗时 {i % 97} ms");
        }

        logList.ItemsSource = lines;

        var (logCard, logBody) = UiKit.CardWithBody(UiKit.Space2);
        logBody.Children.Add(UiKit.SectionLabel("5000 行日志"));
        logBody.Children.Add(logList);
        page.Children.Add(logCard);

        // ── 帧率实测 ──
        var (fpsCard, fpsBody) = UiKit.CardWithBody(UiKit.Space2);
        fpsBody.Children.Add(UiKit.SectionLabel("滚动帧率"));
        fpsBody.Children.Add(UiKit.Body(
            "滚动上方列表 5 秒，统计平均帧率与最长帧间隔。", secondary: true));

        var fpsReport = UiKit.Body("未测量。", secondary: true);
        fpsBody.Children.Add(fpsReport);
        fpsBody.Children.Add(UiKit.ButtonBar(
            UiKit.PrimaryButton("测量（5 秒）", () => _ = MeasureAndReportAsync(list, fpsReport))));
        page.Children.Add(fpsCard);

        // 只有真正挂到窗口上的那一份才登记：探针要靠它拿列表内部的 ScrollViewer，
        // 而未挂载的 ListView 模板还没实例化，根本取不到滚动容器。
        if (mounted)
        {
            _perfListView = list;
        }

        return UiKit.Scroll(page);
    }

    /// <summary>按钮入口：测量并把结论写进页面上那块文字。</summary>
    private async Task MeasureAndReportAsync(ListView list, TextBlock report)
    {
        report.Text = "测量中…";

        var scroller = await WaitForScrollViewerAsync(list).ConfigureAwait(true);
        report.Text = scroller is null
            ? "未找到滚动容器。"
            : await MeasureScrollFrameRateCoreAsync(scroller).ConfigureAwait(true);
    }

    /// <summary>供启动探针调用：对当前挂载的性能自检页测一次滚动帧率。</summary>
    /// <remarks>
    /// 探针在 <c>OnLaunched</c> 里被调用，此时窗口还没走过第一帧，ListView 的模板尚未实例化，
    /// 直接找滚动容器必然落空（第一版就是这样）。所以这里等一小拍、强制布局、再找，最多重试 1 秒。
    /// </remarks>
    internal async Task<string> MeasureFrameRateForProbeAsync()
    {
        if (_perfListView is not { } list)
        {
            return "性能自检页未挂载。";
        }

        var scroller = await WaitForScrollViewerAsync(list).ConfigureAwait(true);
        return scroller is null
            ? "未找到滚动容器。"
            : await MeasureScrollFrameRateCoreAsync(scroller).ConfigureAwait(true);
    }

    /// <summary>等到列表的模板实例化出 ScrollViewer 为止（最多 ~1 秒）。</summary>
    private async Task<ScrollViewer?> WaitForScrollViewerAsync(ListView list)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            _root.UpdateLayout();
            if (FindScrollViewer(list) is { } scroller)
            {
                return scroller;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return null;
    }

    /// <summary>
    /// 实测列表滚动的帧率。
    /// </summary>
    /// <remarks>
    /// <b>为什么要自研帧采样</b>：PresentMon 需要额外安装，而 <c>CompositionTarget.Rendering</c>
    /// 本来就是 XAML 的每帧回调——数它就是在数真实出帧次数，且零依赖。
    /// 滚动由代码驱动（每帧推进一段偏移），保证测量期间一定有渲染负载。
    /// </remarks>
    private async Task<string> MeasureScrollFrameRateCoreAsync(ScrollViewer scroller)
    {
        var frames = 0;
        var longestGapMs = 0.0;
        var lastFrame = System.Diagnostics.Stopwatch.StartNew();
        var total = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, object e)
        {
            var gap = lastFrame.Elapsed.TotalMilliseconds;
            lastFrame.Restart();

            if (gap > longestGapMs)
            {
                longestGapMs = gap;
            }

            frames++;

            // 每帧推进一段偏移：到底后回到顶部，保证 5 秒里一直在滚。
            var next = scroller.VerticalOffset + 24;
            if (next + scroller.ViewportHeight >= scroller.ExtentHeight)
            {
                next = 0;
            }

            scroller.ChangeView(null, next, null, disableAnimation: true);
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        try
        {
            await Task.Delay(5000).ConfigureAwait(true);
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        var seconds = total.Elapsed.TotalSeconds;
        var fps = frames / seconds;

        var report =
            $"{frames} 帧 / {seconds:F1} s · 平均 {fps:F1} FPS · " +
            $"最长帧 {longestGapMs:F1} ms · 可滚动 {scroller.ExtentHeight:F0} px";

        BootProbe.Write(
            $"滚动帧率：平均 {fps:F1} FPS（{frames} 帧 / {seconds:F1} s），最长帧间隔 {longestGapMs:F1} ms");
        return report;
    }

    /// <summary>在可视树里找第一个 ScrollViewer（ListView 的模板里有一个）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller)
            {
                return scroller;
            }

            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 统计当前页面的可视树规模（元素总数 + 各类型分布）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 渲染开销几乎正比于可视树里的元素数：WinUI 的布局是两趟递归遍历，每个元素两趟都要参与
    /// 测量与排列，而文本元素还要额外做一次成型（glyph run 生成）。元素数是"这棵树贵不贵"的
    /// 第一手证据，比任何体感描述都可靠。
    /// </para>
    /// <para>
    /// <b>刻意不在这里做计时</b>：对一个已经算好的布局调 <c>UpdateLayout()</c> 是空转，
    /// 量出来永远是 0.00 ms，看着像"布局不要钱"。真正的布局代价只能由
    /// <see cref="MeasureClickToPaintAsync"/> 从"点击到页面挂上"这段时间里量。
    /// 一个会给出误导性数字的指标，比没有指标更糟。
    /// </para>
    /// </remarks>
    internal string DescribeVisualTree(string tag)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = CountElements(_host, counts);
        counts.TryGetValue("TextBlock", out var textBlocks);

        var top = counts
            .OrderByDescending(static pair => pair.Value)
            .Take(6)
            .Select(static pair => $"{pair.Key} {pair.Value}")
            .ToArray();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{tag}] 可视树 {total} 元素（文本 {textBlocks}）· {string.Join(" / ", top)}");
    }

    private static int CountElements(DependencyObject node, Dictionary<string, int> counts)
    {
        var name = node.GetType().Name;
        counts[name] = counts.TryGetValue(name, out var existing) ? existing + 1 : 1;

        var total = 1;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            total += CountElements(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), counts);
        }

        return total;
    }

    /// <summary>一行 CSV，记录帧间隔样本（供渲染开销分析）。</summary>
    /// <remarks>
    /// 写原始样本而不是写聚合值：聚合值只能回答"平均多少"，而"哪里卡了一下、卡在第几帧"
    /// 只有原始序列答得上来。这些文件放在临时目录，供 before/after 对照，不进入产品数据目录。
    /// </remarks>
    private static void WriteFrameSamples(string name, double[] samples)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "envstation-frames-" + name + ".csv");

            // 与启动探针同理：日志给人看，必须带 BOM。
            File.WriteAllText(
                path,
                string.Join('\n', samples.Select(static sample => sample.ToString("F2", CultureInfo.InvariantCulture))),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 采样落盘失败不影响测量结论本身。
        }
    }

    /// <summary>屏幕尺寸 / 缩放 / 刷新率。</summary>
    /// <remarks>
    /// <para>
    /// 前两项目走 Win32 直读；刷新率这里<b>不</b>从 <c>DEVMODE</c> 取——那个结构里有一个联合体，
    /// 字段偏移写错一位就会读出一个"看着像数、其实全错"的答案（本机实测读到过 1080x0 @ 0 Hz），
    /// 而错误的参照物比没有参照物更危险。
    /// </para>
    /// <para>
    /// 这两个数之所以值得记：<b>125% 缩放意味着界面上每一个逻辑像素都会落在非整数物理像素上</b>，
    /// 滚动时文本与 1px 描边是否稳定，取决于偏移量取整的方式——这是很多"看着不流畅"
    /// 与"帧率无关"的真实成因。
    /// </para>
    /// </remarks>
    private static string DescribeDisplay()
    {
        var width = GetSystemMetrics(SystemMetricCxScreen);
        var height = GetSystemMetrics(SystemMetricCyScreen);
        var dpi = GetDpiForSystem();
        var scaling = dpi / 96.0 * 100;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"屏幕 {width}x{height} · 缩放 {scaling:F0}%（{dpi} DPI）");
    }

    private const int SystemMetricCxScreen = 0;
    private const int SystemMetricCyScreen = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>
    /// 用真实滚轮消息驱动滚动，测量帧间隔。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不满足于 <c>ChangeView</c> 测出来的数字</b>：<c>ChangeView</c> 走的是合成器内插，
    /// 而用户的手走的是输入路径——消息泵、命中测试、XAML 输入路由、滚动抑制（direct manipulation）。
    /// 这条路径上的卡顿，<c>ChangeView</c> 一次都测不到。
    /// </para>
    /// <para>
    /// 这里用真实的 <c>WM_MOUSEWHEEL</c> 让输入系统按正常顺序处理它，
    /// 滚动的是用户正盯着的那块内容，而不是"某个 ScrollViewer"。
    /// </para>
    /// </remarks>
    internal async Task<string> MeasureMouseWheelScrollAsync(string tag, int notches = 60, int intervalMs = 40)
    {
        Navigate(tag);
        await WaitForPageAsync().ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
        {
            return $"[{tag}] 拿不到窗口句柄。";
        }

        if (FindScrollViewer(_host) is { } scroller)
        {
            scroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        var gaps = new List<double>(300);
        var last = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, object e)
        {
            gaps.Add(last.Elapsed.TotalMilliseconds);
            last.Restart();
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        try
        {
            for (var i = 0; i < notches; i++)
            {
                // 滚轮每格 120；负值 = 向下滚。一次一整格，与真手一致。
                SendMouseWheel(hwnd, -120);
                await Task.Delay(intervalMs).ConfigureAwait(true);
            }

            await Task.Delay(500).ConfigureAwait(true);
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        if (gaps.Count < 2)
        {
            return $"[{tag}] 未采到帧。";
        }

        gaps.RemoveAt(0);
        WriteFrameSamples("wheel-" + tag, [.. gaps]);

        var sorted = new List<double>(gaps);
        sorted.Sort();

        var average = gaps.Sum() / gaps.Count;
        var worstIndex = gaps.IndexOf(sorted[^1]);
        var janky = gaps.Count(static gap => gap > 25.0);

        var offsetText = FindScrollViewer(_host) is { } after
            ? string.Create(CultureInfo.InvariantCulture, $"滚动到 {after.VerticalOffset:F0} px")
            : "未取到偏移";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{tag}] 滚轮 {notches} 格 · {gaps.Count} 帧 · 平均 {average:F1} ms（{1000.0 / average:F1} FPS）· " +
            $"P99 {sorted[(int)(sorted.Count * 0.99)]:F1} · 最长 {sorted[^1]:F1} ms（第 {worstIndex} 帧）· " +
            $"卡顿 {janky} 帧 · {offsetText}");
    }

    /// <summary>把光标移到窗口中心，再投一条滚轮消息。</summary>
    private static void SendMouseWheel(IntPtr hwnd, int delta)
    {
        _ = GetWindowRect(hwnd, out var rect);

        var x = (rect.Left + rect.Right) / 2;
        var y = (rect.Top + rect.Bottom) / 2;

        _ = SetCursorPos(x, y);

        // wParam 高字是滚动量；lParam 是屏幕坐标。
        // 先按无符号 32 位拼好再转指针：屏幕坐标的高位为 1 时数值会"看起来是负的"，
        // 用有符号路径去转就会触发 CA2020；而这本来就是一段位模式，无符号才是它的本意。
        var wParam = (IntPtr)((uint)(delta << 16) & 0xFFFF0000u);
        var lParam = (IntPtr)(((uint)y << 16) | ((uint)x & 0xFFFFu));

        _ = PostMessage(hwnd, WmMouseWheel, wParam, lParam);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private const int WmMouseWheel = 0x020A;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 窗口尺寸连续变化时的帧间隔（拖拽窗口边缘 / 最大化时的体感来源）。
    /// </summary>
    /// <remarks>
    /// 尺寸变化会让<b>整棵可视树</b>重新测量与排列，代价与元素数成正比；
    /// 而下面这张表里最贵的一项往往是"会换行的文本"——换行要在每趟布局里重新做一次
    /// 断行计算，宽度每次都不一样，于是每一次都省不掉。
    /// </remarks>
    internal async Task<string> MeasureResizeStormAsync(int milliseconds = 2000)
    {
        var original = AppWindow.Size;
        var gaps = new List<double>(600);
        var last = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, object e)
        {
            gaps.Add(last.Elapsed.TotalMilliseconds);
            last.Restart();
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        var stop = false;
        var resizer = Task.Run(async () =>
        {
            var step = 0;
            while (!stop)
            {
                // 在 900~1240 之间来回：宽度每次都不同，布局缓存全部失效。
                var width = 900 + (step % 18) * 20;
                try
                {
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(width, original.Height));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    break;
                }

                step++;
                await Task.Delay(33).ConfigureAwait(false);
            }
        });

        try
        {
            await Task.Delay(milliseconds).ConfigureAwait(true);
        }
        finally
        {
            stop = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            await resizer.ConfigureAwait(true);

            try
            {
                AppWindow.Resize(original);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // 恢复原尺寸失败不影响结论。
            }
        }

        if (gaps.Count < 2)
        {
            return "尺寸变化期间未采到帧。";
        }

        gaps.RemoveAt(0);
        WriteFrameSamples("resize", [.. gaps]);

        var sorted = new List<double>(gaps);
        sorted.Sort();

        var average = gaps.Sum() / gaps.Count;
        var worst = sorted[^1];
        var janky = gaps.Count(static gap => gap > 25.0);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"尺寸连续变化 {milliseconds} ms：{gaps.Count} 帧 · 平均 {average:F1} ms/帧" +
            $"（{1000.0 / average:F1} FPS）· P99 {sorted[(int)(sorted.Count * 0.99)]:F1} · " +
            $"最长 {worst:F1} · 卡顿 {janky} 帧");
    }

    /// <summary>导航 → 新页面真实内容滚动，逐页测一遍（导航后的第一屏往往才是卡的所在）。</summary>
    internal async Task<string> MeasureNavigationThenScrollAsync(string tag)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Navigate(tag);
        await WaitForPageAsync().ConfigureAwait(true);
        clock.Stop();

        var navigateMs = clock.Elapsed.TotalMilliseconds;
        await Task.Delay(250).ConfigureAwait(true);

        var scroll = await MeasureContentScrollAsync().ConfigureAwait(true);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{tag}] 导航 {navigateMs:F1} ms · {scroll}");
    }

    /// <summary>
    /// 实测"当前页面滚动"的帧间隔分布。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与性能自检页里那个测量有三处不同，每一处都是为了测出真实体感：
    /// </para>
    /// <list type="number">
    ///   <item><b>测当前页面</b>，而不是固定的那个空列表——重的页面才有问题。</item>
    ///   <item><b>让合成器自己滚</b>（<c>ChangeView</c> 带动画），而不是每帧由代码硬推偏移。
    ///         代码硬推等于每帧强制一次同步布局，测的是"最坏情况"，不是用户滚动时的情况。</item>
    ///   <item><b>看帧间隔分布</b>，不只看平均值。人眼感觉到的是掉帧（长间隔），
    ///         而"平均 60 FPS"完全可以在若干次 100 ms 卡顿之后仍然成立。</item>
    /// </list>
    /// </remarks>
    internal async Task<string> MeasureContentScrollAsync()
    {
        if (FindScrollViewer(_host) is not { } scroller)
        {
            return "未找到滚动容器。";
        }

        var extent = scroller.ExtentHeight;
        var viewport = scroller.ViewportHeight;
        var scrollable = extent - viewport;

        if (scrollable < 200)
        {
            return $"内容不足一屏（可滚动 {scrollable:F0} px），跳过。";
        }

        var gaps = new List<double>(600);
        var last = System.Diagnostics.Stopwatch.StartNew();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, object e)
        {
            gaps.Add(last.Elapsed.TotalMilliseconds);
            last.Restart();
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        try
        {
            // 一个来回：向下滚一遍、再滚回来，全程由合成器插值，UI 线程不被强制同步布局。
            var oneWayMs = 1200;
            scroller.ChangeView(null, scrollable, null, disableAnimation: false);
            await Task.Delay(oneWayMs).ConfigureAwait(true);
            scroller.ChangeView(null, 0, null, disableAnimation: false);
            await Task.Delay(oneWayMs).ConfigureAwait(true);
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        clock.Stop();

        // 第一帧的间隔没有意义（它从订阅时刻算起），丢掉。
        if (gaps.Count > 1)
        {
            gaps.RemoveAt(0);
        }

        if (gaps.Count == 0)
        {
            return "未采到帧。";
        }

        var sorted = new List<double>(gaps);
        sorted.Sort();

        var average = gaps.Sum() / gaps.Count;
        var p50 = sorted[sorted.Count / 2];
        var p99 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];
        var worst = sorted[^1];

        // 掉帧判据：超过 1.5 个 60Hz 帧周期（25 ms）就算一次可感知的卡顿。
        var janky = gaps.Count(static gap => gap > 25.0);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"滚动 {clock.Elapsed.TotalSeconds:F1} s · {gaps.Count} 帧 · 平均 {average:F1} ms/帧" +
            $"（{1000.0 / average:F1} FPS）· 中位 {p50:F1} · P99 {p99:F1} · 最长 {worst:F1} · " +
            $"卡顿 {janky} 帧 · 可滚动 {scrollable:F0} px");
    }
    // ══════════════════════════ 概览 ══════════════════════════

    private UIElement BuildOverviewPage()
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("概览"));
        page.Children.Add(UiKit.Body(
            "只读检测，不修改系统。", secondary: true));

        var (infoCard, infoBody) = UiKit.CardWithBody(UiKit.Space1);
        infoBody.Children.Add(UiKit.Body("读取中…", secondary: true));
        page.Children.Add(infoCard);

        page.Children.Add(UiKit.Card(
            UiKit.SectionLabel("动作库"),
            UiKit.Body($"{_bridge?.ActionCount ?? 0} 个预制动作：探测、下载、解压、安装、环境变量、PATH、" +
                       "配置与镜像源、验证、清理。写入类动作需逐项授权，执行前自动创建快照。")));

        LoadOverviewAsync(infoBody);
        return UiKit.Scroll(page);
    }

    private async void LoadOverviewAsync(Panel host)
    {
        if (_bridge is null)
        {
            return;
        }

        try
        {
            var os = await _bridge.RunReadOnlyAsync("envstation.detect.os").ConfigureAwait(true);
            var arch = await _bridge.RunReadOnlyAsync("envstation.detect.arch").ConfigureAwait(true);
            var disk = await _bridge.RunReadOnlyAsync(
                "envstation.detect.disk",
                new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
                {
                    ["path"] = new AbsPkg.ScriptString(Path.GetTempPath()),
                }).ConfigureAwait(true);

            host.Children.Clear();
            host.Children.Add(UiKit.SectionLabel("本机"));

            if (os.Success)
            {
                host.Children.Add(UiKit.Row("操作系统", UiKit.Body(os.Outputs.GetValueOrDefault("product_name", "Windows"))));
                host.Children.Add(UiKit.Row("构建号", UiKit.Mono(
                    os.Outputs.GetValueOrDefault("build", "?") + "." + os.Outputs.GetValueOrDefault("revision", "?"))));
                host.Children.Add(UiKit.Row("区域设置", UiKit.Body(os.Outputs.GetValueOrDefault("culture", "?"))));
            }

            if (arch.Success)
            {
                var emulated = arch.Outputs.GetValueOrDefault("emulated") == "true";
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiKit.Space2 };
                panel.Children.Add(UiKit.Mono(arch.Outputs.GetValueOrDefault("os_arch", "?")));
                panel.Children.Add(UiKit.Badge(emulated ? "仿真" : "原生", emulated ? FindingTone.Warn : FindingTone.Success));
                host.Children.Add(UiKit.Row("CPU 架构", panel));
            }

            if (disk.Success)
            {
                var gigabytes = long.TryParse(disk.Outputs.GetValueOrDefault("available_bytes"), out var bytes)
                    ? bytes / 1024.0 / 1024.0 / 1024.0
                    : 0;
                host.Children.Add(UiKit.Row("系统盘可用", UiKit.Body($"{gigabytes:F1} GB")));
            }

            SetStatus("检测完成");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            host.Children.Clear();
            host.Children.Add(UiKit.Body("检测失败：" + ex.Message, secondary: true));
        }
    }

    // ══════════════════════════ 环境检测 ══════════════════════════

    private UIElement BuildDoctorPage()
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("环境检测"));
        page.Children.Add(UiKit.Body(
            "系统版本、磁盘空间、PATH、前置依赖、命令冲突。只读检测。", secondary: true));

        var (card, body) = UiKit.CardWithBody(UiKit.Space3);
        body.Children.Add(UiKit.Body("尚未检测。", secondary: true));
        page.Children.Add(card);

        page.Children.Add(UiKit.ButtonBar(UiKit.PrimaryButton("开始检测", () => RunDoctorAsync(body))));
        return UiKit.Scroll(page);
    }

    private async void RunDoctorAsync(Panel host)
    {
        if (_bridge is null)
        {
            return;
        }

        host.Children.Clear();
        host.Children.Add(UiKit.Body("检测中…", secondary: true));

        try
        {
            // 检测项清单。判据不在这里写：统一由 CoreActions.ProbeHealth 按各动作自己声明的
            // 输出契约判断，CLI 的 doctor 用的是同一个入口——两边各猜一次输出键，必然有一天猜到不一样
            // （第一版就是这个下场：徽标说"正常"、正文说"缺少 1 项"，见 D-44）。
            (string ActionId, string Label, string Scope)[] checks =
            [
                ("envstation.detect.os", "系统版本", string.Empty),
                ("envstation.path.validate", "用户 PATH", "user"),
                ("envstation.path.validate", "系统 PATH", "machine"),
                ("envstation.detect.deps", "前置依赖", string.Empty),
                ("envstation.detect.conflict", "命令冲突", string.Empty),
            ];

            var problems = 0;
            var rows = new List<UIElement>();

            foreach (var (actionId, label, scope) in checks)
            {
                var arguments = new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal);
                if (scope.Length > 0)
                {
                    arguments["scope"] = new AbsPkg.ScriptString(scope);
                }

                var result = await _bridge.RunReadOnlyAsync(actionId, arguments).ConfigureAwait(true);

                var isProblem = CoreActions.ProbeHealth.IsProblem(actionId, result);
                if (isProblem)
                {
                    problems++;
                }

                var tone = !result.Success ? FindingTone.Error : isProblem ? FindingTone.Warn : FindingTone.Success;

                var block = new StackPanel { Spacing = 2 };
                block.Children.Add(UiKit.SectionLabel(label));
                block.Children.Add(UiKit.StatusLine(
                    CoreActions.ProbeHealth.Describe(actionId, result),
                    tone,
                    result.Message));
                rows.Add(block);
            }

            host.Children.Clear();
            host.Children.Add(UiKit.Body(problems == 0
                ? $"{checks.Length} 项全部正常。"
                : $"{checks.Length} 项中 {problems} 项异常。"));

            foreach (var row in rows)
            {
                host.Children.Add(row);
            }

            SetStatus(problems == 0 ? "检测完成：无异常" : $"检测完成：{problems} 项异常");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            host.Children.Clear();
            host.Children.Add(UiKit.Body("检测失败：" + ex.Message, secondary: true));
        }
    }

    // ══════════════════════════ 安装包 ══════════════════════════

    private UIElement BuildPackagesPage()
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("导入包"));
        page.Children.Add(UiKit.Body(
            "导入 .envstation 包：校验内容与声明，逐项确认能力授权后执行。", secondary: true));

        var pathBox = new TextBox
        {
            PlaceholderText = @"例如 D:\Downloads\python-env.envstation",
            FontFamily = UiKit.UiFont,
            FontSize = UiKit.Type("BodyMedium").Size,
            MinHeight = 34,
        };

        var (pickerCard, pickerBody) = UiKit.CardWithBody(UiKit.Space3);
        pickerBody.Children.Add(UiKit.SectionLabel("包文件"));
        pickerBody.Children.Add(pathBox);

        var (reportCard, reportBody) = UiKit.CardWithBody(UiKit.Space2);
        reportBody.Children.Add(UiKit.Body("尚未校验。", secondary: true));

        var (wallCard, wallBody) = UiKit.CardWithBody(UiKit.Space2);
        wallBody.Children.Add(UiKit.Body("校验通过后列出该包申请的能力。", secondary: true));

        var (trialCard, trialBody) = UiKit.CardWithBody(UiKit.Space2);
        trialBody.Children.Add(UiKit.SectionLabel("沙箱试运行"));
        trialBody.Children.Add(UiKit.Body(
            "在隔离目录中执行，核对声明与实际是否一致。不修改本机环境变量，不联网。", secondary: true));
        trialBody.Children.Add(UiKit.Body("尚未试运行。", secondary: true));

        pickerBody.Children.Add(UiKit.ButtonBar(
            UiKit.PrimaryButton("校验", () => ValidateAsync(pathBox.Text, reportBody, wallBody)),
            UiKit.SecondaryButton("试运行", () => RunTrialAsync(pathBox.Text, trialBody))));

        page.Children.Add(pickerCard);
        page.Children.Add(reportCard);
        page.Children.Add(wallCard);
        page.Children.Add(trialCard);
        return UiKit.Scroll(page);
    }

    /// <summary>
    /// 跑一次 V4 沙箱试运行并把结论渲染出来。
    /// </summary>
    /// <remarks>
    /// 界面上的这一步与 CLI 的 <c>envstation check --v4</c> 是同一段逻辑，
    /// 界面这里只是把它变成"一个按钮 + 一段结论"。之所以值得放进产品界面：
    /// 用户在导入别人分享的包之前，最想知道的是"它说的和它做的到底一不一样"，
    /// 而这件事只有跑一遍才知道。
    /// </remarks>
    private async void RunTrialAsync(string path, Panel host)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            host.Children.Clear();
            host.Children.Add(UiKit.SectionLabel("沙箱试运行"));
            host.Children.Add(UiKit.Body("未填写包文件路径。", secondary: true));
            return;
        }

        host.Children.Clear();
        host.Children.Add(UiKit.SectionLabel("沙箱试运行"));
        host.Children.Add(UiKit.Body("试运行中…", secondary: true));

        try
        {
            var trial = await KernelBridge.RunSandboxTrialAsync(path.Trim()).ConfigureAwait(true);

            host.Children.Clear();
            host.Children.Add(UiKit.SectionLabel("沙箱试运行"));

            if (trial is null)
            {
                host.Children.Add(UiKit.Body("无法读取包内容，请先校验。", secondary: true));
                return;
            }

            host.Children.Add(UiKit.Badge(
                trial.Report.HasBlockers ? "声明与实际不符" : "声明与实际一致",
                trial.Report.HasBlockers ? FindingTone.Error : FindingTone.Success));

            host.Children.Add(UiKit.Row("结果", UiKit.Body(trial.Outcome.Message)));
            host.Children.Add(UiKit.Row("实际使用", UiKit.Mono(
                trial.ExercisedCapabilities.Length == 0 ? "（无）" : string.Join("、", trial.ExercisedCapabilities),
                "MonoSmall")));

            if (trial.UndeclaredCapabilities.Length > 0)
            {
                host.Children.Add(UiKit.Row("未声明能力", UiKit.Body(
                    string.Join("、", trial.UndeclaredCapabilities) + "，未出现在权限项中。")));
            }

            host.Children.Add(UiKit.Body(trial.Report.ToText(), secondary: true));
            SetStatus(trial.Report.HasBlockers ? "试运行：声明与实际不符" : "试运行：声明与实际一致");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            host.Children.Clear();
            host.Children.Add(UiKit.SectionLabel("沙箱试运行"));
            host.Children.Add(UiKit.Body("试运行失败：" + ex.Message, secondary: true));
        }
    }

    private async void ValidateAsync(string path, Panel report, Panel wall)
    {
        if (_bridge is null || string.IsNullOrWhiteSpace(path))
        {
            report.Children.Clear();
            report.Children.Add(UiKit.Body("未填写包文件路径。", secondary: true));
            return;
        }

        report.Children.Clear();
        report.Children.Add(UiKit.Body("校验中…", secondary: true));

        try
        {
            var result = await KernelBridge.ValidatePackageAsync(path.Trim()).ConfigureAwait(true);

            report.Children.Clear();
            report.Children.Add(UiKit.SectionLabel("校验结果"));
            report.Children.Add(UiKit.Badge(
                result.CanImport ? "可以导入" : "禁止导入",
                result.CanImport ? FindingTone.Success : FindingTone.Error));
            report.Children.Add(UiKit.Body(result.Report.ToText()));

            wall.Children.Clear();

            if (result.Manifest is not { } manifest)
            {
                return;
            }

            report.Children.Add(UiKit.Row("包名", UiKit.Body(manifest.Name)));
            report.Children.Add(UiKit.Row("包 ID", UiKit.Mono(manifest.Id)));
            report.Children.Add(UiKit.Row("版本", UiKit.Mono(
                $"{manifest.Version}（标准 {manifest.SpecVersion} / 档位 {manifest.Tier}）")));
            report.Children.Add(UiKit.Row("质量评分", UiKit.Body($"{result.QualityScore} / 100")));

            // 能力授权（需求 IMP-2 / IMP-3）：逐项独立勾选，且必须用人话说明。
            wall.Children.Add(UiKit.SectionLabel("能力授权（逐项确认）"));
            wall.Children.Add(UiKit.Body(
                "未勾选的能力，对应动作执行时将被拒绝。", secondary: true));

            foreach (var (capability, explanation) in manifest.Permissions.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                var content = new StackPanel { Spacing = 2 };
                content.Children.Add(UiKit.Mono(capability, "MonoSmall"));
                content.Children.Add(UiKit.Body(explanation, secondary: true));

                wall.Children.Add(new CheckBox
                {
                    MinHeight = 32,
                    Tag = capability,
                    Content = content,
                });
            }

            SetStatus($"已校验 {Path.GetFileName(path)} · 质量评分 {result.QualityScore}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            report.Children.Clear();
            report.Children.Add(UiKit.Body("校验失败：" + ex.Message, secondary: true));
        }
    }

    // ══════════════════════════ 快照与回滚 ══════════════════════════

    private UIElement BuildHistoryPage()
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("快照与回滚"));
        page.Children.Add(UiKit.Body(
            "环境变量与配置文件的修改快照，用于回滚。", secondary: true));

        var (card, body) = UiKit.CardWithBody(UiKit.Space2);
        body.Children.Add(UiKit.Body("读取中…", secondary: true));
        page.Children.Add(card);

        LoadSnapshotsAsync(body);
        return UiKit.Scroll(page);
    }

    private async void LoadSnapshotsAsync(Panel host)
    {
        try
        {
            var snapshots = await KernelBridge.ListSnapshotsAsync().ConfigureAwait(true);

            host.Children.Clear();
            host.Children.Add(UiKit.SectionLabel($"{snapshots.Count} 个快照"));

            if (snapshots.Count == 0)
            {
                host.Children.Add(UiKit.Body("暂无快照。首次修改环境变量时自动创建。", secondary: true));
                return;
            }

            foreach (var snapshot in snapshots.Take(50))
            {
                var row = new StackPanel { Spacing = 2, MinHeight = 52 };
                row.Children.Add(UiKit.Body(snapshot.Trigger));
                row.Children.Add(UiKit.Mono(
                    $"{snapshot.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {snapshot.SnapshotId}" +
                    (snapshot.IsManual ? " · 手动" : string.Empty)));
                host.Children.Add(row);
            }

            SetStatus($"共 {snapshots.Count} 个快照");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            host.Children.Clear();
            host.Children.Add(UiKit.Body("读取快照失败：" + ex.Message, secondary: true));
        }
    }

    // ══════════════════════════ 动作库 ══════════════════════════

    private UIElement BuildActionsPage()
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("动作库"));
        page.Children.Add(UiKit.Body(
            "包可调用的全部预制动作，不含任意命令执行。", secondary: true));

        if (_bridge is null)
        {
            page.Children.Add(UiKit.Body("内核未就绪。", secondary: true));
            return UiKit.Scroll(page);
        }

        // 一行一动作用两段纯文本，不再各自套卡片、套栈：
        // 卡片边框对"扫一眼有哪些动作"没有任何帮助，却让每个动作多出两个测量节点。
        // 按能力家族分组保留——它是这一页唯一的结构信息，去掉就只剩一长串 ID。
        var (card, body) = UiKit.CardWithBody(UiKit.Space1);

        foreach (var group in _bridge.Descriptors
            .GroupBy(static d => ActionGroup(d.ActionId), StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            var label = UiKit.SectionLabel($"{group.Key} · {group.Count()}");
            label.Margin = new Thickness(0, UiKit.Space4, 0, UiKit.Space2);
            body.Children.Add(label);

            foreach (var descriptor in group.OrderBy(static d => d.ActionId, StringComparer.Ordinal))
            {
                body.Children.Add(ActionRow(descriptor));
            }
        }

        page.Children.Add(card);
        return UiKit.Scroll(page);
    }

    /// <summary>
    /// 动作库的一行：动作 ID + 一行说明。
    /// </summary>
    /// <remarks>
    /// <b>不换行是刻意的</b>：这一页有 74 个动作、每行两段文本，全部允许换行意味着
    /// 每趟布局都要重新做 150 次断行计算；而这两段文字本来就短，从来不需要换行——
    /// 换行只会在窄窗口下把行高撑成两行，让整页节奏变成深浅不一的条纹。
    /// 超长时用省略号截断，是列表的正确行为。
    /// </remarks>
    private static UIElement ActionRow(AbsActions.ActionDescriptor descriptor)
    {
        var id = UiKit.Mono(descriptor.ActionId, "MonoSmall");
        id.TextWrapping = TextWrapping.NoWrap;
        id.TextTrimming = TextTrimming.CharacterEllipsis;

        var summary = UiKit.Body(
            $"{descriptor.DisplayName} · {descriptor.CapabilityId}" +
            (descriptor.IsIdempotent ? " · 幂等" : string.Empty) +
            (descriptor.HasInverse ? " · 可逆" : string.Empty),
            secondary: true);
        summary.TextWrapping = TextWrapping.NoWrap;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;

        var row = new StackPanel { Spacing = 0, MinHeight = 40 };
        row.Children.Add(id);
        row.Children.Add(summary);
        return row;
    }

    // ══════════════════════════ 设置 ══════════════════════════

    private UIElement BuildSettingsPage()
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("设置"));

        page.Children.Add(UiKit.Card(
            UiKit.SectionLabel("外观"),
            UiKit.Row("主题", UiKit.Body("深色 / 浅色，右上角切换")),
            UiKit.Row("界面密度", UiKit.Body($"桌面档 · 列表项 {DesignTokens.ListItemHeight:0}px · 按钮 {DesignTokens.ButtonHeight:0}px")),
            UiKit.Row("字体", UiKit.Body("标准字号"))));

        page.Children.Add(UiKit.Card(
            UiKit.SectionLabel("安全与还原"),
            UiKit.Row("环境变量快照", UiKit.Body("写入前自动创建")),
            UiKit.Row("配置文件备份", UiKit.Body("写入前备份，校验失败自动还原")),
            UiKit.Row("权限", UiKit.Body("标准用户运行，系统级操作按需提权"))));

        page.Children.Add(UiKit.Card(
            UiKit.SectionLabel("关于"),
            UiKit.Row("版本", UiKit.Mono("0.1.0")),
            UiKit.Row("动作库", UiKit.Body($"{_bridge?.ActionCount ?? 0} 个动作")),
            UiKit.Row("数据目录", UiKit.Mono(Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "EnvStation")))));

        page.Children.Add(UiKit.ButtonBar(
            UiKit.SecondaryButton("性能自检", () => Navigate("perf"))));

        return UiKit.Scroll(page);
    }

    /// <summary>取动作 ID 的中段作为分组名（<c>envstation.path.edit</c> → <c>path</c>）。</summary>
    private static string ActionGroup(string actionId)
    {
        var parts = actionId.Split('.');
        return parts.Length >= 2 ? parts[1] : actionId;
    }
}
