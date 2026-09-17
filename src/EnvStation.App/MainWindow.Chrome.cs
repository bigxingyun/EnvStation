using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 主窗口的窗口外观（partial）：尺寸、标题栏与标题摘要、根容器背景材质。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独一个文件</b>：这三件事都作用于"窗口本身"而不是"窗口里的内容"——
/// 尺寸决定内容区多大，标题栏承载应用名与环境摘要，背景材质决定内容区透不透。
/// 它们与外壳布局、页面导航是两类关注点，混在一起会让"改导航"与"调外观"互相干扰。
/// 拆开同时让主文件回到纪律 X-4 的 800 行以内。
/// </para>
/// <para>
/// <b>为什么根容器不在这里</b>：<c>_root</c> 同时被外壳与页面宿主使用，它属于主文件；
/// 本文件只负责决定它铺不铺底色（<c>ApplyRootBackdrop</c>）。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    /// <summary>
    /// 设定窗口尺寸并居中（<b>优先用上次保存的尺寸</b>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// WinUI 的默认窗口尺寸只有 1152×592，竖着放不下"卡 1 + 卡 2 + 按钮条"，
    /// 用户第一眼就要滚动——这对一个"检测报告 + 能力授权"的界面是很差的初印象。
    /// 没有保存过尺寸时，按工作区取一个带下限的默认值：小屏笔记本上不至于比屏幕还大，
    /// 大屏上也不至于浪费。
    /// </para>
    /// <para>
    /// <b>保存过的尺寸必须重新夹一次</b>：显示器拔掉、分辨率变小之后，
    /// 上次那个 5000×3000 的窗口会让应用"启动后看不见"，而用户还不知道有设置文件这回事。
    /// 夹取在 <c>AppSettings.Clamp</c> 里做，读取时就已生效。
    /// </para>
    /// </remarks>
    internal void ApplyDefaultWindowSize()
    {
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea
                .GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary)
                .WorkArea;

            var maxWidth = Math.Max(900, area.Width - 160);
            var maxHeight = Math.Max(620, area.Height - 120);

            var saved = _settings.WindowWidth > 0 && _settings.WindowHeight > 0;
            var width = Math.Clamp(saved ? _settings.WindowWidth : 1240, 900, maxWidth);
            var height = Math.Clamp(saved ? _settings.WindowHeight : 820, 620, maxHeight);

            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

            // 位置：保存过且仍在工作区内就用保存值（多屏用户把窗口放在副屏上是常态），
            // 否则居中。判断"仍在工作区内"用工作区与本窗口尺寸的并集，避免把窗口放到看不见的地方。
            var x = _settings.WindowX;
            var y = _settings.WindowY;
            var onScreen = x is { } sx && y is { } sy
                && sx + width > area.X && sx < area.X + area.Width
                && sy + height > area.Y && sy < area.Y + area.Height;

            AppWindow.Move(new Windows.Graphics.PointInt32(
                onScreen ? x!.Value : area.X + ((area.Width - width) / 2),
                onScreen ? y!.Value : area.Y + ((area.Height - height) / 2)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            // 拿不到显示器信息（远程会话、无头环境）就一直用系统默认尺寸——不是致命问题。
            BootProbe.Write($"窗口尺寸设定失败：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 记住当前窗口尺寸与位置。
    /// </summary>
    /// <remarks>
    /// 在窗口关闭时调一次即可：拖拽过程中每帧写盘既无必要也伤磁盘。
    /// 保存的是"可见尺寸"而不是"最大化状态"——最大化时 <c>Size</c> 就是屏幕大小，
    /// 若用户在最大化状态下退出，下次启动会得到一个占满屏幕的普通窗口（不是最大化）。
    /// 这一点刻意接受：恢复最大化状态需要额外的状态位，而收益只是少按一次最大化按钮。
    /// </remarks>
    internal void CaptureWindowGeometry()
    {
        try
        {
            var size = AppWindow.Size;
            var position = AppWindow.Position;
            _settings = _settings with
            {
                WindowWidth = size.Width,
                WindowHeight = size.Height,
                WindowX = position.X,
                WindowY = position.Y,
            };

            PersistSettings();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            BootProbe.Write($"窗口尺寸记录失败：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 构建（或重建）窗口外壳。主题切换后必须整壳重建：
    /// 所有画刷都是在构造时解析成具体 Brush 实例的，不重建就还是旧主题的颜色。
    /// </summary>
    internal void BuildShell()
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

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = BuildTitleBar();
        var navigation = BuildNavigation();
        var statusBar = BuildStatusBar();

        Grid.SetRow(titleBar, 0);
        Grid.SetRow(navigation, 1);
        Grid.SetRow(statusBar, 2);

        _root.Children.Add(titleBar);
        _root.Children.Add(navigation);
        _root.Children.Add(statusBar);

        // 底色要在导航视图建好之后才定：材质生效时导航视图那层主题底色也得留透，
        // 否则 Mica 只在标题栏那一条可见，看起来像"标题栏是另一个程序"。
        ApplyRootBackdrop();

        // 外壳重建（换主题）后要把标题栏重新指一次：新实例 = 新的拖拽区域与新的画刷。
        _titleBar = titleBar;
        if (ExtendsContentIntoTitleBar)
        {
            SetTitleBar(titleBar);
        }

        UpdateTitleSummary();

        Content = _root;
    }

    /// <summary>
    /// 给根容器决定底色（TD-25：Mica 与不透明根底的冲突）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这两条要求本来是打架的：根容器铺了不透明底色，背后的 Mica 就一点也看不见；
    /// 不铺，深色主题下 XAML 文字变浅、背后的窗口材质却可能还是浅色，导航项会「消失」。
    /// </para>
    /// <para>
    /// 解法不是二选一，而是<b>让材质本身去跟随主题</b>：<see cref="Window.SystemBackdrop"/>
    /// 接上之后，Mica 的深浅由窗口自己的主题决定，与 XAML 的 <c>RequestedTheme</c> 同源，
    /// 于是「文字浅、材质也浅」这个组合不再可能出现，根容器就可以安心留透。
    /// </para>
    /// <para>
    /// 但材质只在 Windows 11 上可用。接不上时（旧系统、远程会话、远程桌面）
    /// <see cref="App.TryApplyBackdrop"/> 返回 false，此时必须退回不透明底色——
    /// 这正是原来那行 <c>_root.Background = UiKit.Surface</c> 存在的理由，不能直接删掉。
    /// </para>
    /// </remarks>
    private void ApplyRootBackdrop()
    {
        var materialActive = App.Current.IsBackdropActive;

        // Mica 生效时留透，让材质透出来；否则自己铺不透明底。
        _root.Background = materialActive ? null : UiKit.Surface;

        // 导航视图自带一层主题底色，不一起留透的话材质只在标题栏那一条可见。
        // 页面内容自身不铺底色（滚动区、卡片才有），所以这一层就是唯一挡住材质的东西。
        if (_navigation is not null)
        {
            _navigation.Background = materialActive ? null : UiKit.Surface;
        }
    }

    /// <summary>
    /// 标题栏：官方 <see cref="TitleBar"/> 控件，48px、跟随应用主题。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 布局是控件的四段（左到右）：<c>LeftHeader</c> → 图标 → <c>Title</c>/<c>Subtitle</c>
    /// → 剩余空白 → <c>RightHeader</c> → 系统caption 按钮保留区。
    /// </para>
    /// <para>
    /// <b>为什么应用名放 <c>Title</c>、状态摘要放 <c>LeftHeader</c></b>：
    /// 设计书要求「左侧放应用名 + 环境状态摘要」，但 <c>RightHeader</c> 右边紧挨着
    /// 系统 caption 按钮，那里放不下摘要；而 <c>LeftHeader</c> 在应用名左边、
    /// 是拖拽区之外的自由位。摘要本身是可点的状态点，放最左反而更像"仪表"。
    /// </para>
    /// <para>
    /// 拖拽区域由控件自己维护（<c>SetTitleBar</c> 指向它），不需要手工算
    /// <c>InputNonClientPointerSource</c> 的 passthrough 矩形——这正是换用官方控件
    /// 相比自己拼一个 Grid 的主要收益：拖拽、双击最大化、右键系统菜单都由它负责。
    /// </para>
    /// </remarks>
    internal TitleBar? TitleBarInstance => _titleBar;

    /// <summary>标题栏高度（设计书 M1-3 交付物 2 要求 48px）。</summary>
    private const double TitleBarHeight = 48;

    /// <summary>当前页面标签（供 App 在装配完成后把首屏导航进去）。</summary>
    internal string CurrentTag => _currentTag;

    /// <summary>
    /// 刷新标题栏左侧的环境状态摘要。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>本阶段是占位</b>（设计书 M1-3 交付物 2 明说「占位即可，M2 接真实数据」）。
    /// 但占位不等于假数据：这里只写内核当前真的报出来的事实——动作数、内核是否可用，
    /// 和状态栏用的是同一个来源。写一个编造的"环境良好"会让这一条在 M2 之前一直是谎话。
    /// </para>
    /// <para>
    /// 三重编码的雏形：形状（●/○）+ 颜色（状态点角色）+ 文字（事实本身），
    /// 换状态层之后这里应该改为订阅它的状态，而不是被各个动作回调顺手调一下。
    /// </para>
    /// </remarks>
    internal void UpdateTitleSummary()
    {
        var ready = _bridge is not null;

        _titleSummary.Text = ready
            ? $"● 就绪 · {_bridge!.ActionCount} 个动作"
            : "○ 内核未就绪";

        _titleSummary.Foreground = ready ? UiKit.Success : UiKit.Error;
    }

    private TitleBar BuildTitleBar()
    {
        var bar = new TitleBar
        {
            // 设计书要求 48px。刻意不新增设计令牌：令牌由 design/generate-tokens.ps1 生成，
            // 本任务不碰那条链（同轮另有工作流在改它），所以高度在这里声明为具名常量。
            Height = TitleBarHeight,
            Title = "环境站",
            Subtitle = "Windows 开发环境配置与回滚",
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = false,
        };

        // 环境状态摘要（占位）：M2 接真实数据后这里改成订阅状态层。
        _titleSummary = UiKit.Text(string.Empty, "LabelMedium");
        _titleSummary.VerticalAlignment = VerticalAlignment.Center;
        _titleSummary.TextTrimming = TextTrimming.CharacterEllipsis;

        var leftHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 8, 0),
        };
        leftHeader.Children.Add(_titleSummary);

        var themeButton = UiKit.SecondaryButton("主题", CycleTheme);
        themeButton.VerticalAlignment = VerticalAlignment.Center;
        themeButton.Margin = new Thickness(8, 0, 12, 0);

        bar.LeftHeader = leftHeader;
        bar.RightHeader = themeButton;

        return bar;
    }
}
