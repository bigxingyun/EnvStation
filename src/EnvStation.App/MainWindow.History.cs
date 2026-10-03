using EnvStation.Abstractions.Serialization;
using EnvStation.App.Controls;
using EnvStation.App.Mvvm;
using EnvStation.Core.Diagnostics;
using AbsPkg = EnvStation.Abstractions.Packages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 「快照与回滚」页：变更历史的时间线。
/// </summary>
/// <remarks>
/// <para>
/// <b>它替换掉的是什么</b>：原页面把快照渲染成一行行「触发原因 + 时间 + ID」，
/// 三件事都没做好——
/// ① 头部写「{N} 个快照」而列表只画前 50 条，**超出的部分被静默丢掉**，
/// 用户看到"32 个快照"却只数出 30 行，只会怀疑程序坏了；
/// ② 基线快照（用户可回退的锚点）与自动快照混在一起，看不出哪个是可以依靠的；
/// ③ 没有回滚入口，而页面副标题写着"用于回滚"。
/// </para>
/// <para>
/// <b>本页提供时间线与恢复</b>：每条快照可预览差量，并经确认后调用
/// <c>envstation.env.restore</c> 恢复用户级变量（系统级默认不恢复）。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    /// <summary>时间线一次最多画多少条（超出时明确说明，而不是静默截断）。</summary>
    private const int TimelinePageSize = 40;

    private SnapshotViewModel? _snapshots;
    private bool _showAllSnapshots;

    /// <summary>构建「快照与回滚」页。</summary>
    private UIElement BuildHistoryPage()
    {
        var page = UiKit.Stack(DesignTokens.RhythmBetweenGroups);

        page.Children.Add(UiKit.Title("快照与回滚"));
        page.Children.Add(UiKit.Body(
            "改环境前自动留下的备份。可看差异，也可恢复到某一条。", secondary: true));

        var (headCard, headBody) = UiKit.CardWithBody(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(headCard);

        var body = UiKit.Stack(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(body);

        var viewModel = new SnapshotViewModel(_kernel);
        _snapshots = viewModel;

        _ = LoadSnapshotsAsync(viewModel, headBody, body);
        return UiKit.Scroll(page);
    }

    private void ReloadSnapshots()
    {
        InvalidatePageCache();
        Navigate("history");
    }

    private async Task LoadSnapshotsAsync(SnapshotViewModel viewModel, Panel headBody, Panel body)
    {
        headBody.Children.Clear();
        body.Children.Clear();

        var verdictHost = UiKit.Stack(DesignTokens.RhythmBetweenGroups);
        headBody.Children.Add(verdictHost);

        var progress = AppControls.Progress("正在读取快照");
        verdictHost.Children.Add(progress.Root);

        await viewModel.LoadAsync().ConfigureAwait(true);

        if (!ReferenceEquals(_snapshots, viewModel))
        {
            return;
        }

        verdictHost.Children.Clear();

        if (viewModel.State.Kind == PageStateKind.Error)
        {
            verdictHost.Children.Add(AppControls.InlineError(
                viewModel.State.ErrorMessage ?? "读取快照失败。",
                viewModel.State.ErrorCode,
                "重试",
                ReloadSnapshots));
            SetStatus("快照读取失败", "history");
            return;
        }

        var all = viewModel.Snapshots;

        if (all.Length == 0)
        {
            // 空态只出现在正文区，结论区不再重复一句"暂无快照"——
            // 同一件事说两遍会让页面显得啰嗦，而结论区在这一页本来就没有结论可报。
            verdictHost.Children.Add(UiKit.Body(
                "还没有快照。改环境变量或配置时会自动留下。",
                secondary: true));
        }
        else
        {
            verdictHost.Children.Add(AppControls.StatusBadge(
                StatusTone.Success,
                $"{all.Length} 个快照 · 基线 {viewModel.BaselineCount} 个"));

            var newest = all[0].CreatedAt.LocalDateTime;
            verdictHost.Children.Add(UiKit.Body(
                $"最近一次：{newest:yyyy-MM-dd HH:mm:ss}",
                secondary: true));
        }

        verdictHost.Children.Add(UiKit.ButtonBar(UiKit.SecondaryButton("刷新", ReloadSnapshots)));

        RenderTimeline(viewModel, body);
        SetStatus($"快照：共 {all.Length} 个", "history");
    }

    /// <summary>时间线：按时间倒序，基线单独标记。</summary>
    private void RenderTimeline(SnapshotViewModel viewModel, Panel host)
    {
        var all = viewModel.Snapshots;
        if (all.Length == 0)
        {
            host.Children.Add(AppControls.EmptyState(
                "还没有任何快照。",
                "刷新",
                ReloadSnapshots));
            return;
        }

        var (card, body) = UiKit.CardWithBody(DesignTokens.RhythmInGroup);
        body.Children.Add(UiKit.SectionLabel("变更时间线"));

        var shown = _showAllSnapshots ? all.Length : Math.Min(all.Length, TimelinePageSize);

        for (var i = 0; i < shown; i++)
        {
            body.Children.Add(BuildTimelineRow(all[i], isFirst: i == 0));
        }

        // 被折叠时必须说清楚，不能像上一版那样"头部写 N、列表画 50"。
        if (shown < all.Length)
        {
            var hiddenCount = all.Length - shown;
            body.Children.Add(UiKit.Body(
                $"还有 {hiddenCount} 条更早的没列出来。", secondary: true));
            body.Children.Add(UiKit.ButtonBar(
                UiKit.SecondaryButton($"显示全部 {all.Length} 条", () =>
                {
                    _showAllSnapshots = true;
                    ReloadSnapshots();
                })));
        }

        host.Children.Add(card);
    }

    private UIElement BuildTimelineRow(SnapshotIndexEntry snapshot, bool isFirst)
    {
        var row = new Grid { ColumnSpacing = UiKit.Space3 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var marker = AppControls.StatusDot(
            snapshot.IsBaseline ? StatusTone.Success : StatusTone.Neutral,
            isFirst ? 12 : 8);

        var text = UiKit.Stack(2);

        var title = UiKit.Body(snapshot.Trigger);
        title.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(title);

        var meta = new List<string>(3)
        {
            snapshot.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        };

        if (snapshot.IsBaseline)
        {
            meta.Add("基线");
        }

        if (snapshot.IsManual)
        {
            meta.Add("手动");
        }

        meta.Add(snapshot.SnapshotId);

        text.Children.Add(UiKit.Mono(string.Join(" · ", meta), "MonoSmall"));

        if (snapshot.Note is { Length: > 0 } note)
        {
            text.Children.Add(UiKit.Body(note, secondary: true));
        }

        text.Children.Add(UiKit.ButtonBar(
            UiKit.SecondaryButton("预览差量", () => PreviewSnapshotDiffAsync(snapshot.SnapshotId)),
            UiKit.PrimaryButton("恢复", () => RestoreSnapshotAsync(snapshot.SnapshotId))));

        Grid.SetColumn(marker, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(marker);
        row.Children.Add(text);

        if (snapshot.IsBaseline)
        {
            var badge = AppControls.StatusBadge(StatusTone.Success, "基线");
            Grid.SetColumn(badge, 2);
            row.Children.Add(badge);
        }

        return row;
    }

    private async void PreviewSnapshotDiffAsync(string snapshotId)
    {
        if (UiXamlRoot is null)
        {
            SetStatus("界面尚未就绪。", "history");
            return;
        }

        SetStatus("正在计算与快照的差量…", "history");
        try
        {
            var result = await _kernel.RunReadOnlyAsync(
                "envstation.env.diff",
                new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
                {
                    ["snapshot_id"] = new AbsPkg.ScriptString(snapshotId),
                }).ConfigureAwait(true);

            await Confirm.ConfirmAsync(
                UiXamlRoot,
                "和快照比一比",
                result.Success ? "当前环境相对该快照的差异（只看，不改）。" : result.Message,
                result.Success ? result.Message + Environment.NewLine + string.Join(Environment.NewLine, result.Outputs.Select(static kv => kv.Key + "=" + kv.Value)) : null,
                Abstractions.Transactions.RiskLevel.Safe,
                "关闭").ConfigureAwait(true);

            SetStatus(result.Success ? "已展示差量预览" : "差量预览失败：" + result.Message, "history");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            SetStatus("差量预览失败：" + ex.Message, "history");
        }
    }

    private async void RestoreSnapshotAsync(string snapshotId)
    {
        if (UiXamlRoot is null)
        {
            SetStatus("界面尚未就绪。", "history");
            return;
        }

        var plan = RemediationPlan.Single(
            RemedyStep.Of(
                "envstation.env.restore",
                $"恢复快照 {snapshotId}",
                RemedyArgument.Of("snapshot_id", snapshotId),
                RemedyArgument.Of("allow_machine", false)),
            $"把用户级环境变量恢复到快照 {snapshotId}");

        SetStatus("正在预览恢复…", "history");
        try
        {
            var outcome = await _apply.ApplyPlanAsync(
                plan,
                Abstractions.Transactions.RiskLevel.High,
                "恢复快照",
                "把用户级环境变量恢复到这条快照。系统级默认不动。",
                UiXamlRoot).ConfigureAwait(true);

            SetStatus(outcome.Message + (outcome.Succeeded ? " 新开一个终端后再看效果。" : string.Empty), "history");
            if (outcome.Succeeded)
            {
                ReloadSnapshots();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetStatus("恢复失败：" + ex.Message, "history");
        }
    }
}
