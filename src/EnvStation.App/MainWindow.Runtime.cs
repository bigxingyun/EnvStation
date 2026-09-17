using EnvStation.App.Controls;
using EnvStation.App.Mvvm;
using EnvStation.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 「运行时」页：这台机器上有哪些语言与工具链、各是什么版本、装在哪。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这一页此前不存在</b>：本产品自称"多语言运行时管理中枢"，而界面里<b>没有运行时页面</b>——
/// 用户只能在一个通用检测页上看到「系统版本、PATH、前置依赖、命令冲突」这五项的徽标，
/// 看不到"我的 Python 是几点几、装在哪"。这是定位与能力之间最直接的落差。
/// </para>
/// <para>
/// <b>形态：分组表格</b>。16 个运行时平铺会让用户在一堆陌生名字里找自己要的那个；
/// 按「C 系 / Java 系 / Python / 其他语言 / 工具与数据库」分组之后，
/// 某组里少了他预期的那个东西时，他能判断出是"没装"而不是"没找到"。
/// </para>
/// <para>
/// <b>"未检测到"不是问题</b>：用户没装 Go 完全正常。这一页只陈述事实，
/// 不把"没装"渲染成警告——那会制造 16 条噪音，把真正的问题（检测失败）淹掉。
/// 缺失项要变成待办，由「环境就绪」页按项目需求去提，而不是在这里无差别报警。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    private RuntimeViewModel? _runtimes;

    /// <summary>构建「运行时」页。</summary>
    private UIElement BuildRuntimePage()
    {
        var page = UiKit.Stack(DesignTokens.RhythmBetweenGroups);

        page.Children.Add(UiKit.Title("运行时"));
        page.Children.Add(UiKit.Body(
            "本机已安装的语言与工具链。只读检测，不修改系统。", secondary: true));

        var (headCard, headBody) = UiKit.CardWithBody(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(headCard);

        var body = UiKit.Stack(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(body);

        var viewModel = new RuntimeViewModel(_kernel);
        _runtimes = viewModel;

        _ = ScanRuntimesAsync(viewModel, headBody, body);
        return UiKit.Scroll(page);
    }

    private void ReloadRuntimes()
    {
        if (_runtimes is null)
        {
            return;
        }

        InvalidatePageCache();
        Navigate("runtime");
    }

    private async Task ScanRuntimesAsync(RuntimeViewModel viewModel, Panel headBody, Panel body)
    {
        headBody.Children.Clear();
        body.Children.Clear();

        // 结论区单独一个容器：进度卡与结论卡都放它里面。
        // 这样"收起进度、显示结论"只是清掉这一个容器再填，
        // 而不是去 clear 外层再 Finish 一个已经被摘下来的控件——
        // 那正是上一版界面"整块清空再重建"的老毛病：控件一旦被摘出可视树，
        // 再改它的属性不会有人画出来（进度条会永远停在那里）。
        var verdictHost = UiKit.Stack(DesignTokens.RhythmBetweenGroups);
        headBody.Children.Add(verdictHost);

        var progress = AppControls.Progress("正在检测本机运行时");
        verdictHost.Children.Add(progress.Root);

        // 分组容器先建好：每检出一个就往对应组里追加一行，
        // 而不是整表重建——增量追加是"分批检测"能有意义的前提。
        var groupHosts = new Dictionary<string, StackPanel>(StringComparer.Ordinal);
        var groupCards = new Dictionary<string, Border>(StringComparer.Ordinal);

        foreach (var group in RuntimeCatalogGroups.All)
        {
            var (card, groupBody) = UiKit.CardWithBody(DesignTokens.RhythmInGroup);
            card.Visibility = Visibility.Collapsed;

            groupBody.Children.Add(UiKit.SectionLabel(group.Title));

            var rows = UiKit.Stack(0);
            groupBody.Children.Add(rows);

            groupHosts[group.Title] = rows;
            groupCards[group.Title] = card;
            body.Children.Add(card);
        }

        await viewModel.ScanAsync(
            status => AppendRuntimeRow(status, groupHosts, groupCards)).ConfigureAwait(true);

        if (!ReferenceEquals(_runtimes, viewModel))
        {
            return;
        }

        verdictHost.Children.Clear();

        if (viewModel.State.Kind == PageStateKind.Error)
        {
            verdictHost.Children.Add(AppControls.InlineError(
                viewModel.State.ErrorMessage ?? "检测未能完成。",
                viewModel.State.ErrorCode,
                "重试",
                ReloadRuntimes));
            SetStatus("运行时检测未能完成", "runtime");
            return;
        }

        verdictHost.Children.Add(AppControls.StatusBadge(
            viewModel.FoundCount > 0 ? StatusTone.Success : StatusTone.Neutral,
            $"检出 {viewModel.FoundCount} / {viewModel.Total} 项"));
        verdictHost.Children.Add(UiKit.Body(
            $"清单共 {viewModel.Total} 项。未检出的条目表示本机尚未安装，不是错误。",
            secondary: true));
        verdictHost.Children.Add(UiKit.ButtonBar(UiKit.SecondaryButton("重新检测", ReloadRuntimes)));

        if (viewModel.FoundCount == 0)
        {
            body.Children.Insert(0, AppControls.EmptyState(
                "本机没有检出清单里的任何运行时。",
                "重新检测",
                ReloadRuntimes));
        }

        SetStatus($"运行时：检出 {viewModel.FoundCount} / {viewModel.Total} 项", "runtime");
    }

    /// <summary>追加一行运行时（含分组下钻与未检出行的弱化处理）。</summary>
    private static void AppendRuntimeRow(
        RuntimeStatus status,
        Dictionary<string, StackPanel> groupHosts,
        Dictionary<string, Border> groupCards)
    {
        var group = RuntimeCatalogGroups.All
            .First(g => g.Entries.Any(e => string.Equals(e.Kind, status.Entry.Kind, StringComparison.Ordinal)));

        if (!groupHosts.TryGetValue(group.Title, out var host))
        {
            return;
        }

        if (groupCards.TryGetValue(group.Title, out var card))
        {
            card.Visibility = Visibility.Visible;
        }

        // 列：状态点 | 名称 | 版本（等宽）| 位置（等宽，省略）| 来源
        var row = new Grid
        {
            ColumnSpacing = UiKit.Space3,
            MinHeight = DesignTokens.TableRowHeight,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        var dot = AppControls.StatusDot(status.Tone, 10);

        // 未检出的条目用次要色：它们不是错误，只是"这台机器上还没有"。
        var name = UiKit.Body(status.Entry.DisplayName, secondary: !status.Found);
        name.VerticalAlignment = VerticalAlignment.Center;

        var version = status.Found
            ? UiKit.Mono(status.Version.Length > 0 ? status.Version : "—", "MonoSmall")
            : UiKit.Text("—", "MonoSmall", UiKit.OnSurfaceVariant, mono: true);
        version.VerticalAlignment = VerticalAlignment.Center;

        var path = UiKit.Mono(status.Path.Length > 0 ? status.Path : "—", "MonoSmall");
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        path.VerticalAlignment = VerticalAlignment.Center;

        // 技术值可选中：用户要把版本或路径贴进工单时不必手打。
        var source = UiKit.Body(status.Found ? status.Source : string.Empty, secondary: true);
        source.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(version, 2);
        Grid.SetColumn(path, 3);
        Grid.SetColumn(source, 4);
        row.Children.Add(dot);
        row.Children.Add(name);
        row.Children.Add(version);
        row.Children.Add(path);
        row.Children.Add(source);

        host.Children.Add(row);

        // 检测本身失败时补一行说明：这一行与其他"未检出"看起来一样，但原因完全不同。
        if (!status.Succeeded)
        {
            var note = UiKit.Body($"检测未完成：{status.Message}", secondary: true);
            note.Margin = new Thickness(24, 0, 0, UiKit.Space2);
            host.Children.Add(note);
        }
    }
}
