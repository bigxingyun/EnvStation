using EnvStation.App.Controls;
using EnvStation.App.Mvvm;
using EnvStation.Core.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 「环境就绪」页（原「概览」）：首屏回答"我能不能开工、缺什么"。
/// </summary>
/// <remarks>
/// <para>
/// <b>它替换掉的是什么</b>：原「概览」页首屏显示的是操作系统版本、构建号、区域设置、CPU 架构、
/// 系统盘可用空间，再加一张「动作库 74 个预制动作」的说明卡。
/// 实测这些占满了首屏 656 逻辑像素里的 290px，把真正有用的内容挤到了折叠线以下——
/// 而用户打开这个程序想知道的是「我缺什么」，不是「我的系统叫什么名字」。
/// </para>
/// <para>
/// <b>形态</b>：结论卡 + 待办列表。结论卡一眼给出"就绪 / 缺 N 项"，待办列表按严重度分组，
/// 每条带四段式说明与修复入口。本机的技术信息没有丢，只是降级成结论正确时才显示的次要内容。
/// </para>
/// <para>
/// <b>这一页是本项目第一个真正走「视图模型 → 渲染」的页面</b>：
/// 加载态、空态、错误态、部分成功四种情况都由 <see cref="TriageViewModel"/> 的状态驱动，
/// 页面不再自己发异步调用、不再用「读取中…」四个字冒充加载态。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    private TriageViewModel? _readiness;

    /// <summary>构建「环境就绪」页。</summary>
    private UIElement BuildReadinessPage()
    {
        var page = UiKit.Stack(DesignTokens.RhythmBetweenGroups);

        page.Children.Add(UiKit.Title("环境就绪"));
        page.Children.Add(UiKit.Body("检测本机是否具备开始开发的条件。只读检测，不修改系统。", secondary: true));

        // ── 结论卡：一眼看到"能不能开工" ──
        var (verdictCard, verdictBody) = UiKit.CardWithBody(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(verdictCard);

        // ── 正文区：加载 / 空 / 就绪 / 出错都在这里换 ──
        var body = UiKit.Stack(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(body);

        var viewModel = new TriageViewModel(_kernel);
        _readiness = viewModel;

        // 首屏直接跑一次：让用户打开就能看到"缺什么"，而不是先看到一句"尚未检测"再点一下。
        // 这是刻意的——上一版首页也是自动加载的，问题不在自动加载，在于加载完只显示系统信息。
        _ = LoadReadinessAsync(viewModel, verdictBody, body);

        return UiKit.Scroll(page);
    }

    /// <summary>重新体检（结论卡上的按钮用）。</summary>
    private void ReloadReadiness()
    {
        if (_readiness is null)
        {
            return;
        }

        // 页面被缓存，重建一次即可拿到全新的结论卡与正文区。
        // 直接改现有控件会让"重跑"与"首跑"走两条不同的渲染路径，两边迟早不一致。
        InvalidatePageCache();
        Navigate("overview");
    }

    private async Task LoadReadinessAsync(TriageViewModel viewModel, Panel verdictBody, Panel body)
    {
        verdictBody.Children.Clear();
        body.Children.Clear();

        var progress = AppControls.Progress("正在检测本机环境");
        verdictBody.Children.Add(progress.Root);
        body.Children.Add(AppControls.EmptyState("检测进行中，请稍候。"));

        await viewModel.RunAsync().ConfigureAwait(true);

        // 取消或页面已切走：不再回写界面（视图模型的状态仍然是权威的）。
        if (!ReferenceEquals(_readiness, viewModel))
        {
            return;
        }

        RenderVerdict(viewModel, verdictBody, progress);
        RenderBody(viewModel, body);
        SetStatus(viewModel.Summary, "overview");
    }

    /// <summary>渲染结论卡。</summary>
    private void RenderVerdict(TriageViewModel viewModel, Panel host, ProgressCard progress)
    {
        host.Children.Clear();

        if (viewModel.State.Kind == PageStateKind.Error)
        {
            host.Children.Add(AppControls.InlineError(
                viewModel.State.ErrorMessage ?? "检测未能完成。",
                viewModel.State.ErrorCode,
                "重试",
                ReloadReadiness));
            return;
        }

        progress.Finish(viewModel.Summary, succeeded: true);
        host.Children.Remove(progress.Root);

        var report = viewModel.Report;
        var tone = report?.Tone ?? StatusTone.Neutral;

        // 一行：徽标（形状 + 颜色 + 文字）+ 汇总句。三重编码由 StatusTone 统一决定，页面不自己拼。
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
        host.Children.Add(head);

        host.Children.Add(UiKit.ButtonBar(
            UiKit.SecondaryButton("重新检测", ReloadReadiness)));
    }

    /// <summary>渲染正文：空态 / 待办列表 / 本机信息。</summary>
    private void RenderBody(TriageViewModel viewModel, Panel host)
    {
        host.Children.Clear();
        var report = viewModel.Report;

        if (report is null)
        {
            host.Children.Add(AppControls.EmptyState("尚未检测。", "开始检测", ReloadReadiness));
            return;
        }

        // 有待办项：先给待办（这是用户最需要看的），本机信息降到后面。
        var groups = viewModel.Groups;
        if (!groups.IsEmpty)
        {
            host.Children.Add(UiKit.SectionLabel($"待处理 {groups.Total} 项"));
            foreach (var (title, items) in groups.NonEmpty())
            {
                host.Children.Add(UiKit.SectionLabel($"{title} {items.Length}"));
                foreach (var item in items)
                {
                    host.Children.Add(BuildRemedyCard(item));
                }
            }
        }
        else if (report.IsHealthy)
        {
            host.Children.Add(AppControls.EmptyState(
                "环境已就绪，没有需要处理的问题。",
                "重新检测",
                ReloadReadiness));
        }
        else
        {
            // 没有待办项但有检测未完成：既不能说"就绪"，也不该说"有问题"。
            host.Children.Add(AppControls.EmptyState(
                "有检测项未能完成，暂时无法判断环境是否就绪。",
                "重试",
                ReloadReadiness));
        }

        // 本机信息：结论正确时才作为次要内容展示。
        host.Children.Add(BuildHostInfoCard(report));
    }

    /// <summary>一条待办项：标题 + 四段式说明 + 修复入口。</summary>
    private UIElement BuildRemedyCard(RemedyItem item)
    {
        var tone = StatusTone.For(item.Severity);

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

        var content = UiKit.Stack(DesignTokens.RhythmInGroup);
        content.Children.Add(head);
        content.Children.Add(DescribePair("现象", item.Symptom));
        content.Children.Add(DescribePair("原因", item.Cause));
        content.Children.Add(DescribePair("影响", item.Impact));

        if (item.CanAutoFix)
        {
            content.Children.Add(DescribePair("怎么办", item.Plan.Summary));

            var fix = UiKit.PrimaryButton(
                item.NeedsSecondConfirmation ? "查看变更" : "修复",
                () => ShowFixPending(item));

            if (!item.ShouldOfferFix)
            {
                fix.IsEnabled = false;
            }

            content.Children.Add(UiKit.ButtonBar(fix));
        }
        else
        {
            // 只提示不可修的条目：不给出假的修复按钮（规范里"禁止假按钮"）。
            content.Children.Add(DescribePair("怎么办", "此项需要手工处理，环境站不代改。"));
        }

        return UiKit.Card(content);
    }

    /// <summary>一段「标签 + 说明」。</summary>
    private static UIElement DescribePair(string label, string text)
    {
        var panel = UiKit.Stack(2);

        var caption = UiKit.Text(label, "LabelMedium", UiKit.OnSurfaceVariant);
        panel.Children.Add(caption);

        var body = UiKit.Body(text);
        body.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(body);

        return panel;
    }

    /// <summary>
    /// 修复入口的临时处理。
    /// </summary>
    /// <remarks>
    /// 执行链路（差量预览 → 快照 → 执行 → 复检）在 M2-10/M2-11 交付，本阶段刻意不给假按钮：
    /// 用户点下去会看到一句"尚未接通"的实话，而不是一个看起来能修、点完什么也没发生的按钮。
    /// </remarks>
    private void ShowFixPending(RemedyItem item) =>
        SetStatus($"修复链路尚未接通：{item.Title}", "overview");

    /// <summary>本机信息卡（次要内容）。</summary>
    private static UIElement BuildHostInfoCard(DiagnosticReport report)
    {
        var detail = UiKit.Stack(DesignTokens.RhythmInGroup);
        detail.Children.Add(UiKit.SectionLabel("本机与检测明细"));

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
