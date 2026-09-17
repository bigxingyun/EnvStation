using EnvStation.App.Controls;
using EnvStation.App.Mvvm;
using EnvStation.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 「待处理问题」页（原「环境检测」）：逐条看清问题，给出处置。
/// </summary>
/// <remarks>
/// <para>
/// <b>它替换掉的是什么</b>：原页面跑 5 项检测，每项渲染成一行「徽标 + 一句话」，
/// 然后数一下有几个问题写进状态栏。用户看到 5 行灰字，既不知道先处理哪个，也不知道每条能怎么办。
/// 更根本的是，**检测项清单硬编码在页面的按钮处理函数里**，而 CLI 的 <c>doctor</c> 用的是
/// <c>ProbeHealth</c> 的另一套判据——两边各写一遍，迟早出现"命令行说 4 项异常、界面说 2 项"。
/// </para>
/// <para>
/// <b>现在两页共用一份结论</b>：本页与「环境就绪」页都用 <see cref="TriageViewModel"/>。
/// 这不是为了省代码，而是为了**不自相矛盾**：首页说 3 项、点进去只有 2 项，
/// 用户就再也不信这个程序了。
/// </para>
/// <para>
/// <b>形态</b>：分级列表 + 就地展开的详情。宽屏下的"右侧详情面板"留到 M3，
/// 因为那需要先有选中态与键盘上下键导航——现在先把内容与分组做对。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    private TriageViewModel? _issues;

    /// <summary>构建「待处理问题」页。</summary>
    private UIElement BuildIssuesPage()
    {
        var page = UiKit.Stack(DesignTokens.RhythmBetweenGroups);

        page.Children.Add(UiKit.Title("待处理问题"));
        page.Children.Add(UiKit.Body(
            "按严重度列出本机环境的问题与处置。只读检测，不修改系统。", secondary: true));

        var (verdictCard, verdictBody) = UiKit.CardWithBody(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(verdictCard);

        var body = UiKit.Stack(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(body);

        var viewModel = new TriageViewModel(_kernel);
        _issues = viewModel;

        _ = LoadIssuesAsync(viewModel, verdictBody, body);
        return UiKit.Scroll(page);
    }

    private void ReloadIssues()
    {
        if (_issues is null)
        {
            return;
        }

        InvalidatePageCache();
        Navigate("doctor");
    }

    private async Task LoadIssuesAsync(TriageViewModel viewModel, Panel verdictBody, Panel body)
    {
        verdictBody.Children.Clear();
        body.Children.Clear();

        var progress = AppControls.Progress("正在检测本机环境");
        verdictBody.Children.Add(progress.Root);
        body.Children.Add(AppControls.EmptyState("检测进行中，请稍候。"));

        await viewModel.RunAsync().ConfigureAwait(true);

        if (!ReferenceEquals(_issues, viewModel))
        {
            return;
        }

        verdictBody.Children.Clear();
        body.Children.Clear();

        if (viewModel.State.Kind == PageStateKind.Error)
        {
            verdictBody.Children.Add(AppControls.InlineError(
                viewModel.State.ErrorMessage ?? "检测未能完成。",
                viewModel.State.ErrorCode,
                "重试",
                ReloadIssues));
            SetStatus("检测未能完成", "doctor");
            return;
        }

        var report = viewModel.Report;
        var tone = report?.Tone ?? StatusTone.Neutral;

        var head = new Grid { ColumnSpacing = UiKit.Space3 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = AppControls.StatusBadge(tone);
        var summary = UiKit.Text(viewModel.Summary, "TitleSmall", UiKit.OnSurface);
        summary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(summary, 1);
        head.Children.Add(badge);
        head.Children.Add(summary);
        verdictBody.Children.Add(head);
        verdictBody.Children.Add(UiKit.ButtonBar(UiKit.SecondaryButton("重新检测", ReloadIssues)));

        RenderIssuesBody(viewModel, body);
        SetStatus(viewModel.Summary, "doctor");
    }

    private void RenderIssuesBody(TriageViewModel viewModel, Panel host)
    {
        host.Children.Clear();
        var report = viewModel.Report;

        if (report is null)
        {
            host.Children.Add(AppControls.EmptyState("尚未检测。", "开始检测", ReloadIssues));
            return;
        }

        var groups = viewModel.Groups;

        if (!groups.IsEmpty)
        {
            foreach (var (title, items) in groups.NonEmpty())
            {
                host.Children.Add(UiKit.SectionLabel($"{title} {items.Length}"));
                foreach (var item in items)
                {
                    host.Children.Add(BuildIssueCard(item));
                }
            }
        }
        else if (report.IsHealthy)
        {
            host.Children.Add(AppControls.EmptyState(
                "没有需要处理的问题。",
                "重新检测",
                ReloadIssues));
        }
        else
        {
            host.Children.Add(AppControls.EmptyState(
                "有检测项未能完成，暂时无法判断是否存在问题。",
                "重试",
                ReloadIssues));
        }

        // 检测明细：每项检测本身跑成没跑成。这是"结论的依据"，放在问题列表之后。
        host.Children.Add(BuildDetectionDetail(report));
    }

    /// <summary>一条问题的完整卡片（四段式 + 出处）。</summary>
    private UIElement BuildIssueCard(RemedyItem item)
    {
        var tone = StatusTone.For(item.Severity);
        var content = UiKit.Stack(DesignTokens.RhythmInGroup);

        var head = new Grid { ColumnSpacing = UiKit.Space3 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = AppControls.StatusBadge(tone);
        var title = UiKit.Text(item.Title, "TitleSmall", UiKit.OnSurface);
        title.TextWrapping = TextWrapping.Wrap;
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(title, 1);
        head.Children.Add(badge);
        head.Children.Add(title);
        content.Children.Add(head);

        content.Children.Add(IssueSection("现象", item.Symptom));
        content.Children.Add(IssueSection("原因", item.Cause));
        content.Children.Add(IssueSection("影响", item.Impact));

        if (item.CanAutoFix && item.ShouldOfferFix)
        {
            content.Children.Add(IssueSection("怎么办", item.Plan.Summary));
            content.Children.Add(UiKit.ButtonBar(
                UiKit.PrimaryButton(
                    item.NeedsSecondConfirmation ? "查看变更" : "修复",
                    () => SetStatus($"修复链路尚未接通：{item.Title}", "doctor"))));
        }
        else
        {
            content.Children.Add(IssueSection("怎么办", "此项需要手工处理，环境站不代改。"));
        }

        // 出处：这条结论由哪个检测项、依据哪条规则得出。用户报问题时这行最有用。
        content.Children.Add(UiKit.Mono($"{item.RuleId} · {item.Id}", "MonoSmall"));

        return UiKit.Card(content);
    }

    private static UIElement IssueSection(string label, string text)
    {
        var panel = UiKit.Stack(2);
        panel.Children.Add(UiKit.Text(label, "LabelMedium", UiKit.OnSurfaceVariant));

        var body = UiKit.Body(text);
        body.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(body);
        return panel;
    }

    private static UIElement BuildDetectionDetail(DiagnosticReport report)
    {
        var detail = UiKit.Stack(DesignTokens.RhythmInGroup);
        detail.Children.Add(UiKit.SectionLabel("检测明细"));

        foreach (var detection in report.Detections)
        {
            var tone = detection.Succeeded
                ? (RemedyCatalog.FromDetection(detection.ActionId, detection.Outputs, detection.Scope).IsEmpty
                    ? StatusTone.Success
                    : StatusTone.Warning)
                : StatusTone.Error;

            var row = new Grid { ColumnSpacing = UiKit.Space3 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = AppControls.StatusDot(tone, 10);

            var label = UiKit.Body(detection.Label);
            label.VerticalAlignment = VerticalAlignment.Center;

            var message = UiKit.Body(detection.Message, secondary: true);
            message.TextWrapping = TextWrapping.Wrap;

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(message, 2);
            row.Children.Add(dot);
            row.Children.Add(label);
            row.Children.Add(message);
            detail.Children.Add(row);
        }

        return UiKit.Card(detail);
    }
}
