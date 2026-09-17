using EnvStation.Core.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnvStation.App.Controls;

/// <summary>
/// 轻量反馈条：出现在窗口底部，2 秒后自动消失，不阻塞操作。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：上一版整个界面只有一条 24px 的状态栏可以反馈，
/// 而状态栏是"当前状态"语义，不是"刚才发生了什么"语义——两者挤在一起就会出现
/// 慢任务完成后把消息盖在别的页面上的问题（重构方案 4.3 节的"状态栏串页"）。
/// </para>
/// <para>
/// <b>为什么不做成模态</b>：复制成功、已切换、已保存这类反馈不需要用户回应，
/// 弹一个要点确认的框是打断。模态留给真正危险的操作（见 <see cref="ConfirmAsync"/>）。
/// </para>
/// </remarks>
internal static class Toast
{
    /// <summary>默认停留时长。</summary>
    internal static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 在指定宿主里弹出一条提示。
    /// </summary>
    /// <param name="host">覆盖层宿主（通常是页面根 Grid）。</param>
    /// <param name="message">提示文字（一句事实）。</param>
    /// <param name="style">语气；null 用中性。</param>
    internal static void Show(Grid host, string message, ToneStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var tone = style ?? StatusTone.Neutral;

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiKit.Space2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new FontIcon
        {
            Glyph = tone.Glyph,
            FontSize = UiKit.Type("BodyMedium").Size,
            Foreground = UiKit.OnSurface,
        });
        content.Children.Add(new TextBlock
        {
            Text = message,
            FontFamily = UiKit.UiFont,
            FontSize = UiKit.Type("BodyMedium").Size,
            Foreground = UiKit.OnSurface,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var shell = new Border
        {
            Background = UiKit.Brush("SurfaceContainerHighest"),
            BorderBrush = UiKit.Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DesignTokens.ShapeS),
            Padding = new Thickness(UiKit.Space4, UiKit.Space2, UiKit.Space4, UiKit.Space2),
            Margin = new Thickness(0, 0, 0, UiKit.Space6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = content,
            // IsHitTestVisible=false：提示条不该拦住用户的下一次点击——
            // "复制成功"挡住"再复制一次"是很恼人的。
            IsHitTestVisible = false,
        };

        host.Children.Add(shell);

        var timer = new DispatcherTimer { Interval = DefaultDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            host.Children.Remove(shell);
        };
        timer.Start();
    }
}

/// <summary>
/// 危险操作确认框。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有它</b>：产品原则 G1/G2 要求"任何可能破坏现有环境的操作，宁可多一次确认"，
/// 而上一版整个界面<b>没有 ContentDialog、没有 Flyout，连"确定/取消"两个字符串都不存在</b>——
/// 也就是说没有任何一次写操作被真正拦过。
/// </para>
/// <para>
/// <b>按风险级改变确认方式</b>（需求 9.1）：L0/L1 一次确认；L2 起在标题里写明影响面；
/// L3 不在这里处理——那种操作压根不该给出确认入口（<c>ShouldOfferFix</c> 已把它挡掉）。
/// </para>
/// </remarks>
internal static class Confirm
{
    /// <summary>
    /// 弹出确认框。
    /// </summary>
    /// <param name="xamlRoot">当前窗口的 XamlRoot（WinUI 要求）。</param>
    /// <param name="title">标题（一句话说清要做什么）。</param>
    /// <param name="impact">影响面：会发生什么、影响范围多大。</param>
    /// <param name="diffText">差量预览文本（可空）；有则在框内以等宽展示。</param>
    /// <param name="risk">风险级；决定确认按钮的视觉重量与是否加二次说明。</param>
    /// <param name="confirmText">确认按钮文字（动词 + 宾语）。</param>
    /// <returns>用户是否确认。</returns>
    internal static async Task<bool> ConfirmAsync(
        XamlRoot xamlRoot,
        string title,
        string impact,
        string? diffText,
        Abstractions.Transactions.RiskLevel risk,
        string confirmText = "执行")
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var body = new StackPanel { Spacing = UiKit.Space3 };

        var impactText = UiKit.Body(impact);
        body.Children.Add(impactText);

        // L2 起把风险明确写出来：用户需要知道这一步影响的是"整台机器/所有用户"还是"只有我"。
        if (risk >= Abstractions.Transactions.RiskLevel.High)
        {
            body.Children.Add(AppControls.StatusBadge(
                AsyncRiskTone(risk),
                "本次变更影响面较大，且需要管理员权限。"));
        }

        if (diffText is { Length: > 0 })
        {
            // 差量用等宽展示：这是用户核对"到底会改什么"的依据，不能被比例字体挤成一团。
            var diffBox = new Border
            {
                Background = UiKit.Brush("TechSurface"),
                CornerRadius = new CornerRadius(DesignTokens.ShapeXS),
                Padding = new Thickness(UiKit.Space3),
                Child = UiKit.Mono(diffText, "MonoSmall"),
            };
            body.Children.Add(diffBox);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = body,
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = risk >= Abstractions.Transactions.RiskLevel.High
                ? ContentDialogButton.Close   // 高风险时默认焦点落在「取消」上，避免一个回车就把环境改了
                : ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static ToneStyle AsyncRiskTone(Abstractions.Transactions.RiskLevel risk) =>
        risk == Abstractions.Transactions.RiskLevel.Dangerous ? StatusTone.Error : StatusTone.Warning;
}

/// <summary>
/// 带校验的表单字段：标签在上、输入居中、错误在下。
/// </summary>
/// <remarks>
/// <para>
/// 上一版只有一个 <c>TextBox</c>：没有校验、没有防抖、没有错误槽位、<b>也没有"浏览…"按钮</b>——
/// 包路径只能手打（规范 4.2 要求键入即校验 + 浏览）。
/// </para>
/// <para>
/// 防抖与"陈旧结论作废"的时序逻辑在 <see cref="DebouncedValidator"/> 里，可脱离界面测；
/// 本控件只负责把它接到一个定时器与三个视觉元素上。
/// </para>
/// </remarks>
internal sealed class FormField
{
    private readonly TextBox _input;
    private readonly TextBlock _helper;
    private readonly StackPanel _errorSlot;
    private readonly StackPanel _root;
    private readonly DebouncedValidator _validator;

    /// <param name="label">字段标签（显示在输入框上方）。</param>
    /// <param name="placeholder">占位提示。</param>
    /// <param name="onChanged">输入变化时的回调（已防抖）。</param>
    /// <param name="browseAction">"浏览…"按钮的回调；null 表示不需要。</param>
    /// <param name="browseText">"浏览…"按钮文字。</param>
    internal FormField(
        string label,
        string placeholder,
        Action<FormField>? onChanged = null,
        Action<FormField>? browseAction = null,
        string browseText = "浏览…")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        _validator = new DebouncedValidator();

        var caption = UiKit.SectionLabel(label);
        caption.Margin = new Thickness(0);

        _input = new TextBox
        {
            PlaceholderText = placeholder,
            FontFamily = UiKit.UiFont,
            FontSize = UiKit.Type("BodyMedium").Size,
            MinHeight = DesignTokens.TextFieldHeight,
        };

        _helper = UiKit.Body(string.Empty, secondary: true);
        _helper.Visibility = Visibility.Collapsed;

        _errorSlot = new StackPanel { Spacing = UiKit.Space1 };

        var inputRow = new Grid { ColumnSpacing = UiKit.Space2 };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_input, 0);
        inputRow.Children.Add(_input);

        if (browseAction is not null)
        {
            var browse = UiKit.SecondaryButton(browseText, () => browseAction(this));
            Grid.SetColumn(browse, 1);
            inputRow.Children.Add(browse);
        }

        _root = new StackPanel { Spacing = UiKit.Space2 };
        _root.Children.Add(caption);
        _root.Children.Add(inputRow);
        _root.Children.Add(_helper);
        _root.Children.Add(_errorSlot);

        _input.TextChanged += (_, _) =>
        {
            var version = _validator.Changed(Environment.TickCount64);
            ShowVerdict(ValidationVerdict.Ok, pending: true);
            ScheduleValidation(version, onChanged);
        };
    }

    /// <summary>可直接挂到页面上的控件。</summary>
    internal StackPanel Root => _root;

    /// <summary>当前输入文本。</summary>
    internal string Text
    {
        get => _input.Text;
        set => _input.Text = value;
    }

    /// <summary>输入框控件本身（需要设宽度或只读时用）。</summary>
    internal TextBox Input => _input;

    /// <summary>辅助说明（常驻，不随校验变化）。</summary>
    internal void SetHelper(string? text)
    {
        _helper.Text = text ?? string.Empty;
        _helper.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>是否允许继续（没有新鲜结论时禁止继续——不能"没检查就下一步"）。</summary>
    internal bool CanProceed => _validator.CanProceed;

    /// <summary>显示一条结论。</summary>
    /// <param name="verdict">结论。</param>
    /// <param name="pending">是否处于"正在检查"中间态。</param>
    internal void ShowVerdict(ValidationVerdict verdict, bool pending = false)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        _errorSlot.Children.Clear();
        if (pending)
        {
            _errorSlot.Children.Add(UiKit.Body("检查中…", secondary: true));
            return;
        }

        if (verdict.IsClean)
        {
            return;
        }

        _errorSlot.Children.Add(AppControls.InlineError(
            verdict.Message,
            verdict.RuleId.Length > 0 ? verdict.RuleId : null));
    }

    private void ScheduleValidation(long version, Action<FormField>? onChanged)
    {
        var timer = new DispatcherTimer { Interval = _validator.Window };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_validator.IsCurrent(version))
            {
                // 用户又改了：这一版不用校验了，等下一版。
                return;
            }

            onChanged?.Invoke(this);
        };
        timer.Start();
    }
}
