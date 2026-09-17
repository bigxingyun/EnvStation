using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnvStation.App.Controls;

/// <summary>
/// 进度卡：确定或不确定进度 + 当前步骤 + 取消。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么做成控件而不是每次新建</b>：上一版每完成一步都 <c>Children.Clear()</c> 再全量重建，
/// 于是进度更新本身会引起整块页面重建——这正是"点了之后顿一下"的来源之一。
/// 进度是高频更新（每帧或每次回调），它必须只改自己那几个属性。
/// </para>
/// <para>
/// <b>取消是必需的，不是可选的</b>：规范要求任何超过 200ms 的操作都能取消。
/// 上一版五个长操作（检测 / 校验 / 试运行 / 读取 / 5 秒测量）全部只能等，
/// 且按钮从不置灰，连点两次会跑出两个并发任务抢同一个结果框。
/// </para>
/// </remarks>
internal sealed class ProgressCard
{
    private readonly TextBlock _step;
    private readonly TextBlock _percent;
    private readonly ProgressBar _bar;
    private readonly Button? _cancel;
    private readonly StackPanel _root;
    private readonly Border _shell;
    private int _lastPercent = -1;

    internal ProgressCard(string title, Action? onCancel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = UiKit.SectionLabel(title);
        titleText.Margin = new Thickness(0);

        _percent = UiKit.Text(string.Empty, "MonoSmall", UiKit.OnSurfaceVariant, mono: true);
        _percent.VerticalAlignment = VerticalAlignment.Center;
        _percent.HorizontalAlignment = HorizontalAlignment.Right;

        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(_percent, 1);
        header.Children.Add(titleText);
        header.Children.Add(_percent);

        // 用「线性扫过」表示不确定进度（规范 MO-5：不用旋转菊花）。
        _bar = new ProgressBar
        {
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 100,
            Height = 4,
        };

        _step = UiKit.Body("准备中…", secondary: true);

        _root = new StackPanel { Spacing = UiKit.Space2 };
        _root.Children.Add(header);
        _root.Children.Add(_bar);
        _root.Children.Add(_step);

        if (onCancel is not null)
        {
            _cancel = UiKit.SecondaryButton("取消", onCancel);
            _root.Children.Add(UiKit.ButtonBar(_cancel));
        }

        _shell = new Border
        {
            Background = UiKit.CardSurface,
            BorderBrush = UiKit.Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DesignTokens.ShapeM),
            Padding = new Thickness(UiKit.Space5),
            Child = _root,
        };
    }

    /// <summary>可直接挂到页面上的控件。</summary>
    internal Border Root => _shell;

    /// <summary>取消按钮；未提供取消回调时为 null。供调用方在开始/结束时置灰。</summary>
    internal Button? CancelButton => _cancel;

    /// <summary>
    /// 报告一次进度。
    /// </summary>
    /// <param name="percent">百分比（0~100）。</param>
    /// <param name="step">当前步骤说明；null 表示不更新步骤文字。</param>
    /// <remarks>
    /// 步骤文字变了才写控件：TextBlock 的赋值会触发布局，
    /// 在每帧回调里无条件赋值会让"进度本身"变成掉帧的原因。
    /// </remarks>
    internal void Report(int percent, string? step = null)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped != _lastPercent)
        {
            _lastPercent = clamped;
            _bar.IsIndeterminate = false;
            _bar.Value = clamped;
            _percent.Text = clamped.ToString(System.Globalization.CultureInfo.InvariantCulture) + " %";
        }

        if (step is { Length: > 0 } && !string.Equals(_step.Text, step, StringComparison.Ordinal))
        {
            _step.Text = step;
        }
    }

    /// <summary>
    /// 收尾。
    /// </summary>
    /// <param name="message">结果说明。</param>
    /// <param name="succeeded">是否成功（只影响取消按钮的去留，不影响文案口径）。</param>
    internal void Finish(string message, bool succeeded)
    {
        _bar.IsIndeterminate = false;
        _bar.Value = succeeded ? 100 : _lastPercent < 0 ? 0 : _lastPercent;
        _percent.Text = succeeded ? "100 %" : _percent.Text;
        _step.Text = message;
        if (_cancel is not null)
        {
            _cancel.IsEnabled = false;
        }
    }
}
