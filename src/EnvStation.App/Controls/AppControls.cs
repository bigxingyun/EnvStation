using EnvStation.Core.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnvStation.App.Controls;

/// <summary>
/// 这一层补齐的原语：状态徽标、空态、行内错误、进度卡、确认对话框、复制按钮。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单开一层</b>：上一版界面的缺陷几乎全部可以归结为"缺原语"——
/// 错误只能写一行灰字（没有 InlineError）、空态只能写一句话（没有 EmptyState）、
/// 进度只能写「检测中…」（没有 ProgressCard）、状态只有颜色（Badge 无形状）。
/// 页面级的小修小补解决不了这类问题，得先把积木补上。
/// </para>
/// <para>
/// 与 <see cref="UiKit"/> 的分工：UiKit 提供排版与容器（Text/Card/Scroll/Row），
/// 本层提供**有状态、有语义**的控件。本层只依赖 UiKit 与设计令牌，不反向依赖页面。
/// </para>
/// </remarks>
internal static class AppControls
{
    private const string GlyphInfo = "\uE9CE";
    private const string GlyphCheck = "\uE73E";
    private const string GlyphWarning = "\uE7BA";
    private const string GlyphError = "\uEA39";
    private const string GlyphCopy = "\uE8C8";

    /// <summary>
    /// 状态徽标：颜色 + 形状字形 + 文字，三重编码。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须有字形</b>：只用颜色编码时，红绿色盲用户无法分辨「正常」与「异常」——
    /// 而这两个状态在本产品里决定了用户要不要动手改环境（规范 A11Y-2）。
    /// 形状取自 <see cref="ToneStyle.Shape"/>，颜色取自语气，两者都由
    /// <see cref="StatusTone"/> 统一决定，页面不自己拼。
    /// </remarks>
    internal static Border StatusBadge(ToneStyle style, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(style);

        var (background, foreground) = style.Kind switch
        {
            ToneKind.Success => (UiKit.Brush("SuccessContainer"), UiKit.Brush("OnSuccessContainer")),
            ToneKind.Warning => (UiKit.Brush("WarnContainer"), UiKit.Brush("OnWarnContainer")),
            ToneKind.Error => (UiKit.Brush("ErrorContainer"), UiKit.Brush("OnErrorContainer")),
            _ => (UiKit.Brush("SurfaceContainerHighest"), UiKit.OnSurfaceVariant),
        };

        var token = UiKit.Type("LabelSmall");
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiKit.Space1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(new FontIcon
        {
            Glyph = style.Glyph,
            FontSize = token.Size,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text = text ?? style.Label,
            FontFamily = UiKit.UiFont,
            FontSize = token.Size,
            FontWeight = FontWeights.Medium,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(DesignTokens.ShapeXS),
            Padding = new Thickness(UiKit.Space2, 2, UiKit.Space2, 2),
            VerticalAlignment = VerticalAlignment.Center,

            // HorizontalAlignment 必须显式设为 Left。
            // Border 的默认水平对齐是 Stretch，而在竖向 StackPanel 里（子元素宽度不受约束）
            // 它就会横向拉满整个可用宽度——一个"徽标"被渲染成一条通栏色带。
            // 这个缺陷是在运行时页第一次截图时被抓到的：整条青绿横条，看着像进度条。
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = panel,
        };
    }

    /// <summary>
    /// 状态点：只有一个圆点，用于密集列表行首。
    /// </summary>
    /// <remarks>用实心圆 + 各行不同的字形来双重编码，供列表里"扫一眼看出哪几行有问题"。</remarks>
    internal static FontIcon StatusDot(ToneStyle style, double size = 8)
    {
        ArgumentNullException.ThrowIfNull(style);

        var brush = style.Kind switch
        {
            ToneKind.Success => UiKit.Brush("StatusDotSuccess"),
            ToneKind.Warning => UiKit.Brush("StatusDotWarn"),
            ToneKind.Error => UiKit.Brush("StatusDotError"),
            _ => UiKit.Brush("StatusDotNeutral"),
        };

        return new FontIcon
        {
            Glyph = "\uEA3B", // 实心圆
            FontSize = size,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// 空态：图标 + 一句状态 + 一个主操作。
    /// </summary>
    /// <param name="status">一句状态（文案规范 CP-4：只说状态，不写"该怎么做"）。</param>
    /// <param name="actionText">主操作按钮文字；null 表示这一页确实无事可做。</param>
    /// <param name="onAction">主操作。</param>
    /// <remarks>
    /// <b>为什么空态必须带出口</b>：上一版的空态是一句「尚未检测。」，用户看完不知道能做什么，
    /// 而规范 4.13 明确要求「空态 = 图标 + 一句状态 + 一个主操作」。
    /// </remarks>
    internal static StackPanel EmptyState(string status, string? actionText = null, Action? onAction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var panel = new StackPanel
        {
            Spacing = UiKit.Space3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(UiKit.Space6, UiKit.Space8, UiKit.Space6, UiKit.Space8),
        };

        panel.Children.Add(new FontIcon
        {
            Glyph = GlyphInfo,
            FontSize = 32,
            Foreground = UiKit.OnSurfaceVariant,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var text = UiKit.Body(status, secondary: true);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        panel.Children.Add(text);

        if (actionText is not null && onAction is not null)
        {
            var button = UiKit.PrimaryButton(actionText, onAction);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(button);
        }

        return panel;
    }

    /// <summary>
    /// 行内错误：图标 + 一句事实 + 可选处置按钮。
    /// </summary>
    /// <param name="message">一句事实（"发生了什么"，含具体对象）。</param>
    /// <param name="errorCode">错误码；非空时以等宽小字展示，便于用户搜索或上报。</param>
    /// <param name="retryText">重试按钮文字；null 表示不给重试。</param>
    /// <param name="onRetry">重试回调。</param>
    /// <remarks>
    /// <b>它替换掉的是"一行灰字"</b>：上一版五处错误处理全部只写 <c>Body("…失败：" + ex.Message, secondary: true)</c>，
    /// 没有图标、没有颜色、没有错误码、没有重试（重构方案 4.3 节）。
    /// 出错和普通说明文字长得一样，用户会直接滑过去。
    /// </remarks>
    internal static Border InlineError(
        string message,
        string? errorCode = null,
        string? retryText = null,
        Action? onRetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var panel = new StackPanel { Spacing = UiKit.Space2 };

        // 用 Grid 的星号列保证长文本能换行：横向 StackPanel 会给子元素无限宽度，
        // TextBlock 便永不换行、右侧被裁掉（这个坑 UiKit.StatusLine 的注释里记过一次）。
        var grid = new Grid { ColumnSpacing = UiKit.Space2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = GlyphError,
            FontSize = UiKit.Type("BodyMedium").Size,
            Foreground = UiKit.Error,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var text = UiKit.Body(message);
        text.Foreground = UiKit.Error;

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        panel.Children.Add(grid);

        if (errorCode is { Length: > 0 })
        {
            panel.Children.Add(UiKit.Mono(errorCode, "MonoSmall"));
        }

        if (retryText is not null && onRetry is not null)
        {
            panel.Children.Add(UiKit.ButtonBar(UiKit.SecondaryButton(retryText, onRetry)));
        }

        return new Border
        {
            Background = UiKit.Brush("ErrorContainer"),
            CornerRadius = new CornerRadius(DesignTokens.ShapeS),
            Padding = new Thickness(UiKit.Space3),
            Child = panel,
        };
    }

    /// <summary>
    /// 进度卡：确定/不确定进度 + 当前步骤 + 取消。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 返回控件本身，调用方拿 <see cref="ProgressCard.Report"/> 更新、
    /// <see cref="ProgressCard.Finish"/> 收尾。做成有状态的控件而不是"每次重建一个 ProgressBar"，
    /// 是为了避免进度更新引起整块页面重建（上一版每完成一步都 <c>Children.Clear()</c>）。
    /// </para>
    /// <para>
    /// <b>不确定进度用线性扫过而不是转圈</b>（规范 MO-5）：本产品的等待大多是"下载/校验/解压"
    /// 这类有明确阶段的活儿，线性扫过更贴合"流水线在走"的观感，也比无限旋转的菊花更克制。
    /// </para>
    /// </remarks>
    internal static ProgressCard Progress(string title, Action? onCancel = null) =>
        new(title, onCancel);

    /// <summary>复制按钮：把技术值复制到剪贴板。</summary>
    /// <remarks>
    /// 上一版整个界面没有一处 <c>Clipboard</c> 调用——路径、命令、错误码都只能靠鼠标拖选。
    /// 对"要把路径贴进终端"这种高频动作来说，这是明显的缺口。
    /// </remarks>
    internal static Button CopyButton(string text, Action<string>? onCopied = null)
    {
        var button = UiKit.SecondaryButton("复制", () =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            onCopied?.Invoke(text);
        });

        button.MinWidth = 0;
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiKit.Space2,
            Children =
            {
                new FontIcon
                {
                    Glyph = GlyphCopy,
                    FontSize = UiKit.Type("LabelLarge").Size,
                    Foreground = UiKit.OnSurface,
                },
                new TextBlock
                {
                    Text = "复制",
                    FontFamily = UiKit.UiFont,
                    FontSize = UiKit.Type("LabelLarge").Size,
                    Foreground = UiKit.OnSurface,
                },
            },
        };

        return button;
    }

    /// <summary>把一个语气映射成 UiKit 的画面色调（供仍在使用 FindingTone 的旧代码过渡）。</summary>
    /// <remarks>
    /// <c>FindingTone</c> 是命名空间级的枚举，不是 <c>UiKit</c> 的嵌套类型，因此这里不加 <c>UiKit.</c> 前缀。
    /// </remarks>
    internal static FindingTone ToFindingTone(ToneStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return style.Kind switch
        {
            ToneKind.Success => FindingTone.Success,
            ToneKind.Warning => FindingTone.Warn,
            ToneKind.Error => FindingTone.Error,
            _ => FindingTone.Neutral,
        };
    }
}
