using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace EnvStation.App;

/// <summary>
/// 界面构件工厂。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么样式集中在这里，而不是散在各个页面里</b>：
/// UI 设计规范 8.2 节 IM-1 要求「控件只允许引用语义令牌，禁止直接写十六进制色值」。
/// 把这条件做成纪律靠人记是不牢的；做成「页面里根本没有写颜色的地方」才是牢的——
/// 页面只能用这里的工厂方法，而工厂方法只认令牌键。
/// </para>
/// <para>
/// <b>令牌来自 <see cref="DesignTokens"/>（生成文件），不来自 XAML 字典</b>：
/// 界面全部由 C# 构建，而散装的 XAML 字典在运行期加载不了——<c>XamlReader</c> 不支持
/// <c>&lt;sys:Double&gt;</c> 这类编译期类型，<c>ResourceDictionary.Source</c> 指向散文件则直接
/// COMException；即便加载成功，代码里的普通索引器也查不到 <c>ThemeDictionaries</c> 里的键，
/// 结果是每个颜色都静默退化成透明（这三条都是本机实测踩出来的）。
/// 因此颜色/字体/字阶/间距/圆角/密度统一由 <c>design/generate-tokens.ps1</c> 生成 C# 表。
/// </para>
/// <para>
/// <b>键是编译期常量字符串</b>：不用反射扫资源，符合 Native AOT 约束（需求 9.4 节）。
/// </para>
/// </remarks>
internal static class UiKit
{
    /// <summary>M3 桌面密度档的间距刻度（4px 基准，见 UI设计规范 2.5 节）。</summary>
    /// <remarks>只压缩垂直节奏，不动字号——密度档切换的唯一纪律（UI设计规范 DN-1）。</remarks>
    internal const double Space1 = DesignTokens.Space1;
    internal const double Space2 = DesignTokens.Space2;
    internal const double Space3 = DesignTokens.Space3;
    internal const double Space4 = DesignTokens.Space4;
    internal const double Space5 = DesignTokens.Space5;
    internal const double Space6 = DesignTokens.Space6;
    internal const double Space8 = DesignTokens.Space8;

    /// <summary>列表项高度（桌面密度档 48px，见 UI设计规范 3.3 节）。</summary>
    internal const double ListItemHeight = DesignTokens.ListItemHeight;

    /// <summary>按钮高度（桌面密度档 36px）。</summary>
    internal const double ButtonHeight = DesignTokens.ButtonHeight;

    /// <summary>标题栏高度（桌面密度档 56px）。</summary>
    internal const double TopAppBarHeight = DesignTokens.TopAppBarHeight;

    /// <summary>
    /// 当前主题。代码构建界面时必须自己记住它，不能靠 <c>{ThemeResource}</c> 那种框架托管查找。
    /// </summary>
    internal static ElementTheme Theme { get; set; } = ElementTheme.Default;

    private static Windows.UI.ViewManagement.AccessibilitySettings? _accessibility;
    private static bool _highContrastProbed;
    private readonly static Dictionary<string, Brush> BrushCache = new(StringComparer.Ordinal);

    /// <summary>当前生效的主题键（Dark / Light / HighContrast）。</summary>
    internal static string ThemeKey
    {
        get
        {
            if (!_highContrastProbed)
            {
                _highContrastProbed = true;
                try
                {
                    _accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
                }
                catch (Exception ex) when (ex is InvalidOperationException or TypeInitializationException
                    or System.Runtime.InteropServices.COMException)
                {
                    // 非 UI 线程或组件缺失：不做高对比度适配，按普通主题走。
                    _accessibility = null;
                }
            }

            if (_accessibility?.HighContrast == true)
            {
                return "HighContrast";
            }

            var effective = Theme == ElementTheme.Default
                ? (Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light)
                : Theme;

            return effective == ElementTheme.Dark ? "Dark" : "Light";
        }
    }

    /// <summary>取一个语义画刷令牌。同一个令牌在同一主题下复用同一个实例。</summary>
    internal static Brush Brush(string token)
    {
        var key = ThemeKey + "/" + token;
        if (BrushCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(ResolveColor(ThemeKey, token));
        BrushCache[key] = brush;
        return brush;
    }

    /// <summary>把令牌解析成具体颜色。</summary>
    private static Windows.UI.Color ResolveColor(string themeKey, string token)
    {
        if (themeKey == "HighContrast")
        {
            // 高对比度下产品不自带配色：把语义角色映射到系统色，配色权交给用户。
            return DesignTokens.HighContrastColors.TryGetValue(token, out var systemKey)
                && FindSystemColor(systemKey) is { } system
                    ? system
                    : Microsoft.UI.Colors.Transparent;
        }

        var map = themeKey == "Dark" ? DesignTokens.DarkColors : DesignTokens.LightColors;

        return map.TryGetValue(token, out var hex)
            ? ParseHex(hex)
            : Microsoft.UI.Colors.Transparent;
    }

    /// <summary>解析 <c>#RRGGBB</c>。</summary>
    private static Windows.UI.Color ParseHex(string hex)
    {
        var text = hex.AsSpan().TrimStart('#');

        return text.Length == 6
            && byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(text[4..], System.Globalization.NumberStyles.HexNumber, null, out var b)
                ? Windows.UI.Color.FromArgb(255, r, g, b)
                : Microsoft.UI.Colors.Transparent;
    }

    /// <summary>
    /// 取一个系统色（<c>SystemColor*</c>）。这些键由 WinUI 定义在主题字典里，
    /// 所以必须走合并字典 + 主题字典的递归查找，普通索引器查不到。
    /// </summary>
    private static Windows.UI.Color? FindSystemColor(string key) =>
        Find(Application.Current.Resources, key, "HighContrast") switch
        {
            Windows.UI.Color color => color,
            SolidColorBrush brush => brush.Color,
            _ => null,
        };

    private static object? Find(ResourceDictionary dictionary, string key, string themeKey)
    {
        // 后合并的字典优先（与框架的资源查找顺序一致），所以先递归再查自身。
        var merged = dictionary.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (Find(merged[i], key, themeKey) is { } found)
            {
                return found;
            }
        }

        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themed)
            && themed is ResourceDictionary themeDictionary
            && themeDictionary.TryGetValue(key, out var themeValue))
        {
            return themeValue;
        }

        return dictionary.TryGetValue(key, out var direct) ? direct : null;
    }

    /// <summary>主色。</summary>
    internal static Brush Primary => Brush("Primary");

    /// <summary>正文色。</summary>
    internal static Brush OnSurface => Brush("OnSurface");

    /// <summary>次要文本色。</summary>
    internal static Brush OnSurfaceVariant => Brush("OnSurfaceVariant");

    /// <summary>表层背景。</summary>
    internal static Brush Surface => Brush("Surface");

    /// <summary>较高层的容器背景（用于卡片）。</summary>
    internal static Brush CardSurface => Brush("SurfaceContainerLow");

    /// <summary>描边。</summary>
    internal static Brush Outline => Brush("OutlineVariant");

    /// <summary>错误色。</summary>
    internal static Brush Error => Brush("Error");

    /// <summary>成功色。</summary>
    internal static Brush Success => Brush("Success");

    /// <summary>警告色。</summary>
    internal static Brush Warn => Brush("Warn");

    /// <summary>等宽字体（路径、版本号、命令一律用它）。</summary>
    internal static FontFamily MonoFont { get; } = new(DesignTokens.MonoFontFamily);

    /// <summary>界面字体。</summary>
    internal static FontFamily UiFont { get; } = new(DesignTokens.UiFontFamily);

    /// <summary>
    /// 界面实际用到的令牌键。用于启动自检：任何一个取不到，界面都会静默退化成透明，
    /// 而「静默退化」正是最难被发现的一类故障——所以宁可开机时主动核对一遍。
    /// </summary>
    private static readonly string[] UsedTokens =
    [
        "Primary", "OnSurface", "OnSurfaceVariant", "Surface", "SurfaceContainer",
        "SurfaceContainerLow", "SurfaceContainerHighest", "OutlineVariant",
        "Error", "ErrorContainer", "OnErrorContainer",
        "Success", "SuccessContainer", "OnSuccessContainer",
        "Warn", "WarnContainer", "OnWarnContainer",
    ];

    /// <summary>核对界面用到的令牌在三个主题下是否都有定义；返回缺失项（形如 <c>Dark:Primary</c>）。</summary>
    internal static string[] AuditTokens()
    {
        var missing = new List<string>();

        foreach (var themeKey in (string[])["Dark", "Light", "HighContrast"])
        {
            foreach (var token in UsedTokens)
            {
                var defined = themeKey == "HighContrast"
                    ? DesignTokens.HighContrastColors.ContainsKey(token)
                    : (themeKey == "Dark" ? DesignTokens.DarkColors : DesignTokens.LightColors).ContainsKey(token);

                if (!defined)
                {
                    missing.Add($"{themeKey}:{token}");
                }
            }
        }

        return [.. missing];
    }

    /// <summary>取字阶令牌（字号 / 行高 / 字重 / 字距）。</summary>
    internal static TypeToken Type(string key) =>
        DesignTokens.TypeScale.TryGetValue(key, out var token) ? token : DesignTokens.TypeScale["BodyMedium"];

    /// <summary>取等宽字阶令牌。</summary>
    internal static TypeToken MonoType(string key) =>
        DesignTokens.MonoScale.TryGetValue(key, out var token) ? token : DesignTokens.MonoScale["MonoBody"];

    private static FontWeight Weight(int weight) => weight switch
    {
        500 => FontWeights.Medium,
        600 => FontWeights.SemiBold,
        700 => FontWeights.Bold,
        _ => FontWeights.Normal,
    };

    /// <summary>按字阶令牌设置一个文本块。</summary>
    internal static TextBlock Text(string text, string typeKey, Brush? foreground = null, bool mono = false)
    {
        var token = mono ? MonoType(typeKey) : Type(typeKey);

        return new TextBlock
        {
            Text = text,
            FontFamily = mono ? MonoFont : UiFont,
            FontSize = token.Size,
            LineHeight = token.LineHeight,
            FontWeight = Weight(token.Weight),
            CharacterSpacing = (int)Math.Round(token.Tracking * 1000 / token.Size),
            Foreground = foreground ?? OnSurface,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = mono,
        };
    }

    /// <summary>页面主标题（Type.TitleLarge）。</summary>
    internal static TextBlock Title(string text, string typeKey = "TitleLarge") => new()
    {
        Text = text,
        FontFamily = UiFont,
        FontSize = Type(typeKey).Size,
        LineHeight = Type(typeKey).LineHeight,
        FontWeight = FontWeights.SemiBold,
        Foreground = OnSurface,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, Space3),
    };

    /// <summary>小节标题（Type.LabelMedium）。</summary>
    internal static TextBlock SectionLabel(string text)
    {
        var token = Type("LabelMedium");

        return new TextBlock
        {
            Text = text,
            FontFamily = UiFont,
            FontSize = token.Size,
            LineHeight = token.LineHeight,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = (int)Math.Round(token.Tracking * 1000 / token.Size),
            Foreground = OnSurfaceVariant,
            Margin = new Thickness(0, Space4, 0, Space2),
        };
    }

    /// <summary>正文（Type.BodyMedium）。</summary>
    internal static TextBlock Body(string text, bool secondary = false)
    {
        var token = Type("BodyMedium");

        return new TextBlock
        {
            Text = text,
            FontFamily = UiFont,
            FontSize = token.Size,
            LineHeight = token.LineHeight,
            CharacterSpacing = (int)Math.Round(token.Tracking * 1000 / token.Size),
            Foreground = secondary ? OnSurfaceVariant : OnSurface,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    /// <summary>等宽的技术信息（路径、版本）。</summary>
    internal static TextBlock Mono(string text, string typeKey = "MonoBody")
    {
        var token = MonoType(typeKey);

        return new TextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = token.Size,
            LineHeight = token.LineHeight,
            Foreground = OnSurface,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
    }

    /// <summary>
    /// 徽标 + 说明文字的整行。
    /// </summary>
    /// <remarks>
    /// <b>为什么用 Grid 而不是横向 StackPanel</b>：横向 StackPanel 给子元素的是无限宽度，
    /// 里面的 TextBlock 因此永不换行，长说明会被右侧裁掉（真机上表现为"这句话怎么断在半截"）。
    /// Grid 的星号列会把可用宽度真正传给 TextBlock，换行才生效。
    /// </remarks>
    internal static Grid StatusLine(string badgeText, FindingTone tone, string message)
    {
        var grid = new Grid { ColumnSpacing = Space3, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = Badge(badgeText, tone);
        badge.Margin = new Thickness(0);
        badge.VerticalAlignment = VerticalAlignment.Top;

        var text = Body(message);
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(badge);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>卡片容器：圆角 + 表层容器色 + 细描边。子元素自动装进竖排面板。</summary>
    internal static Border Card(params UIElement[] children)
    {
        var panel = Stack(Space3);
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return CardShell(panel);
    }

    /// <summary>需要边构建边追加内容时使用：返回外层卡片与内层面板。</summary>
    internal static (Border Root, StackPanel Body) CardWithBody(double spacing = Space3)
    {
        var panel = Stack(spacing);
        return (CardShell(panel), panel);
    }

    /// <summary>卡片外壳（Shape.M 圆角，1px 细描边——这是本产品「精密感」的主要来源）。</summary>
    private static Border CardShell(UIElement content) => new()
    {
        Background = CardSurface,
        BorderBrush = Outline,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(DesignTokens.ShapeM),
        Padding = new Thickness(Space5),
        Margin = new Thickness(0, 0, 0, Space4),
        Child = content,
    };

    /// <summary>状态徽标（成功 / 警告 / 错误）。圆角走 Shape.XS，不用全圆——全圆留给导航指示器与状态点。</summary>
    internal static Border Badge(string text, FindingTone tone)
    {
        var (background, foreground) = tone switch
        {
            FindingTone.Success => (Brush("SuccessContainer"), Brush("OnSuccessContainer")),
            FindingTone.Warn => (Brush("WarnContainer"), Brush("OnWarnContainer")),
            FindingTone.Error => (Brush("ErrorContainer"), Brush("OnErrorContainer")),
            _ => (Brush("SurfaceContainerHighest"), OnSurfaceVariant),
        };

        var token = Type("LabelSmall");

        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(DesignTokens.ShapeXS),
            Padding = new Thickness(Space2, 2, Space2, 2),
            Margin = new Thickness(0, 0, Space2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = token.Size,
                FontWeight = FontWeights.Medium,
                Foreground = foreground,
            },
        };
    }

    /// <summary>表单行：左标签右值，桌面密度下一行 48px。</summary>
    internal static Grid Row(string label, FrameworkElement value)
    {
        var grid = new Grid { MinHeight = ListItemHeight, Padding = new Thickness(0, Space1, 0, Space1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caption = Body(label, secondary: true);
        caption.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(caption, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(caption);
        grid.Children.Add(value);
        return grid;
    }

    /// <summary>主按钮（Shape.S 圆角、LabelLarge 文案、桌面档 36px 高）。</summary>
    internal static Button PrimaryButton(string text, Action onClick, bool enabled = true)
    {
        var button = new Button
        {
            Content = text,
            IsEnabled = enabled,
            MinHeight = ButtonHeight,
            MinWidth = 96,
            CornerRadius = new CornerRadius(DesignTokens.ShapeS),
            Padding = new Thickness(Space5, Space1, Space5, Space1),
            FontFamily = UiFont,
            FontSize = Type("LabelLarge").Size,
            FontWeight = FontWeights.Medium,
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>次要按钮。</summary>
    internal static Button SecondaryButton(string text, Action onClick, bool enabled = true)
    {
        var button = new Button
        {
            Content = text,
            IsEnabled = enabled,
            MinHeight = ButtonHeight,
            MinWidth = 84,
            CornerRadius = new CornerRadius(DesignTokens.ShapeS),
            Padding = new Thickness(Space4, Space1, Space4, Space1),
            FontFamily = UiFont,
            FontSize = Type("LabelLarge").Size,
            Background = Brush("SurfaceContainerHighest"),
            BorderThickness = new Thickness(1),
            BorderBrush = Outline,
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>滚动容器：页面统一用竖向滚动，桌面窗口小的时候不至于内容被截断。</summary>
    internal static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(Space6, Space5, Space6, Space5),
    };

    /// <summary>竖向堆叠。</summary>
    internal static StackPanel Stack(double spacing = Space3) => new()
    {
        Spacing = spacing,
        Orientation = Orientation.Vertical,
    };

    /// <summary>横向排列的按钮条。</summary>
    internal static StackPanel ButtonBar(params UIElement[] buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Space3,
            Margin = new Thickness(0, Space3, 0, 0),
        };

        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }
}

/// <summary>状态色调。</summary>
internal enum FindingTone
{
    /// <summary>中性。</summary>
    Neutral,

    /// <summary>成功。</summary>
    Success,

    /// <summary>警告。</summary>
    Warn,

    /// <summary>错误。</summary>
    Error,
}
