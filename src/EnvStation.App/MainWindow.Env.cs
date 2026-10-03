using System.Collections.Immutable;
using EnvStation.App.Controls;
using EnvStation.App.Mvvm;
using EnvStation.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 「环境变量」页：变量的真相，以及 PATH 的分条视图。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么 PATH 必须分条而不是一行字符串</b>：这是本产品最核心的一个数据对象。
/// 系统自带的编辑框把 PATH 当作一整行 2000 多字符的文本，于是"哪一项失效了""哪一项重复了"
/// "到底哪个 python 先生效"全都要靠人数分号。分条之后，每一项都能单独标记状态——
/// 这是需求 8.1 的 M1-3 条目，也是本项目存在的理由之一。
/// </para>
/// <para>
/// <b>用户级与系统级并排</b>：同名变量在两级同时存在时，用户级覆盖系统级。
/// 两栏并列能让这件事一眼可见，而不是让用户去猜"我改的那个为什么没生效"。
/// </para>
/// <para>
/// <b>本页只读</b>：编辑（拖拽排序、单条禁用、提交变更）需要"待提交态 + 差量预览 + 快照"，
/// 那套链路在 M3-3。现在先把"看清真相"做对——路径的读与写是两种复杂度完全不同的东西。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    private EnvPageViewModel? _environment;

    /// <summary>构建「环境变量」页。</summary>
    private UIElement BuildEnvPage()
    {
        var page = UiKit.Stack(DesignTokens.RhythmBetweenGroups);

        page.Children.Add(UiKit.Title("环境变量"));
        page.Children.Add(UiKit.Body(
            "用户级 / 系统级变量；PATH 按条列出。用户级可清理失效和重复项。", secondary: true));

        var (headCard, headBody) = UiKit.CardWithBody(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(headCard);

        var body = UiKit.Stack(DesignTokens.RhythmBetweenGroups);
        page.Children.Add(body);

        var viewModel = new EnvPageViewModel(_kernel);
        _environment = viewModel;

        _ = LoadEnvironmentAsync(viewModel, headBody, body);
        return UiKit.Scroll(page);
    }

    private void ReloadEnvironment()
    {
        if (_environment is null)
        {
            return;
        }

        InvalidatePageCache();
        Navigate("env");
    }

    private async Task LoadEnvironmentAsync(EnvPageViewModel viewModel, Panel headBody, Panel body)
    {
        headBody.Children.Clear();
        body.Children.Clear();

        var verdictHost = UiKit.Stack(DesignTokens.RhythmBetweenGroups);
        headBody.Children.Add(verdictHost);

        var progress = AppControls.Progress("正在读取环境变量");
        verdictHost.Children.Add(progress.Root);

        await viewModel.LoadAsync().ConfigureAwait(true);

        if (!ReferenceEquals(_environment, viewModel))
        {
            return;
        }

        verdictHost.Children.Clear();

        if (viewModel.State.Kind == PageStateKind.Error)
        {
            verdictHost.Children.Add(AppControls.InlineError(
                viewModel.State.ErrorMessage ?? "读取环境变量失败。",
                viewModel.State.ErrorCode,
                "重试",
                ReloadEnvironment));
            SetStatus("环境变量读取失败", "env");
            return;
        }

        // 徽标说的是 PATH 条目的状况，不是变量总数。
        // 两个数字混在一处会让用户对不上账——"31 项"是变量数、"24 项需要注意"是 PATH 条目数，
        // 并排读起来像自相矛盾。分开说，各是各的。
        var problems = viewModel.TotalProblemCount;
        verdictHost.Children.Add(AppControls.StatusBadge(
            problems > 0 ? StatusTone.Warning : StatusTone.Success,
            $"PATH {viewModel.PathEntries.Length} 条 · 需注意 {problems} 条"));

        verdictHost.Children.Add(UiKit.Body(
            problems > 0
                ? $"PATH 有 {problems} 条有问题（路径不存在、重复、空项、变量解不开等）。"
                : "PATH 看起来正常。",
            secondary: true));
        verdictHost.Children.Add(UiKit.Body(
            $"用户级变量 {viewModel.UserVariables.Length} 个，系统级 {viewModel.MachineVariables.Length} 个。",
            secondary: true));
        verdictHost.Children.Add(UiKit.ButtonBar(
            UiKit.SecondaryButton("重新读取", ReloadEnvironment),
            UiKit.PrimaryButton("清理用户 PATH", () => CleanUserPathAsync())));

        RenderPath(viewModel, body);
        RenderVariables(viewModel, body);

        SetStatus(
            $"环境变量：用户级 {viewModel.UserVariables.Length} 项 · 系统级 {viewModel.MachineVariables.Length} 项",
            "env");
    }

    /// <summary>PATH 分条视图（用户级 + 系统级）。</summary>
    private void RenderPath(EnvPageViewModel viewModel, Panel host)
    {
        if (viewModel.PathEntries.Length == 0)
        {
            host.Children.Add(AppControls.EmptyState("没有读到 PATH 条目。"));
            return;
        }

        var (card, body) = UiKit.CardWithBody(DesignTokens.RhythmInGroup);
        body.Children.Add(UiKit.SectionLabel($"PATH 条目（{viewModel.PathEntries.Length} 项）"));

        foreach (var entry in viewModel.PathEntries)
        {
            body.Children.Add(BuildPathRow(entry));
            if (!entry.IsHealthy && entry.Scope == "user" && entry.Raw.Length > 0)
            {
                var removeBar = UiKit.ButtonBar(
                    UiKit.SecondaryButton("移除该项", () => RemovePathEntryAsync(entry)));
                removeBar.Margin = new Thickness(46, 0, 0, UiKit.Space2);
                body.Children.Add(removeBar);
            }
        }

        body.Children.Add(UiKit.Body(
            "状态来自 path.validate：路径不存在、重复、空项、变量解不开。",
            secondary: true));

        host.Children.Add(card);
    }

    private async void CleanUserPathAsync()
    {
        if (UiXamlRoot is null)
        {
            SetStatus("界面尚未就绪。", "env");
            return;
        }

        var plan = new RemediationPlan(
            [
                RemedyStep.Of(
                    "envstation.path.clean",
                    "清理用户级 PATH 失效项",
                    RemedyArgument.Of("scope", "user"),
                    RemedyArgument.Of("include_duplicates", false)),
                RemedyStep.Of(
                    "envstation.path.dedupe",
                    "去掉用户级 PATH 重复项",
                    RemedyArgument.Of("scope", "user")),
            ],
            "清理并去重用户级 PATH");

        SetStatus("正在预览 PATH 清理…", "env");
        try
        {
            var outcome = await _apply.ApplyPlanAsync(
                plan,
                Abstractions.Transactions.RiskLevel.Reversible,
                "清理用户级 PATH",
                "将移除不存在的目录、空条目，并去掉重复项。系统级 PATH 不会改动。",
                UiXamlRoot).ConfigureAwait(true);

            SetStatus(outcome.Message + (outcome.Succeeded ? " 新开一个终端后再看效果。" : string.Empty), "env");
            if (outcome.Succeeded)
            {
                ReloadEnvironment();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetStatus("清理失败：" + ex.Message, "env");
        }
    }

    private async void RemovePathEntryAsync(PathEntryView entry)
    {
        if (UiXamlRoot is null || entry.Scope != "user")
        {
            SetStatus(entry.Scope != "user" ? "系统级 PATH 目前不能在界面里改。" : "界面还没就绪。", "env");
            return;
        }

        var plan = RemediationPlan.Single(
            RemedyStep.Of(
                "envstation.path.remove",
                $"移除 {entry.Raw}",
                RemedyArgument.Of("scope", "user"),
                RemedyArgument.Of("entry", entry.Raw)),
            $"从用户级 PATH 移除 {entry.Raw}");

        try
        {
            var outcome = await _apply.ApplyPlanAsync(
                plan,
                Abstractions.Transactions.RiskLevel.Reversible,
                "移除 PATH 条目",
                $"将从用户级 PATH 删除：{entry.Raw}",
                UiXamlRoot).ConfigureAwait(true);

            SetStatus(outcome.Message + (outcome.Succeeded ? " 新开一个终端后再看效果。" : string.Empty), "env");
            if (outcome.Succeeded)
            {
                ReloadEnvironment();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetStatus("移除失败：" + ex.Message, "env");
        }
    }

    private static UIElement BuildPathRow(PathEntryView entry)
    {
        var row = new Grid
        {
            ColumnSpacing = UiKit.Space3,
            MinHeight = DesignTokens.ListItemHeight,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = AppControls.StatusDot(entry.Tone, 10);

        var ordinal = UiKit.Text(
            $"{entry.Index + 1}",
            "MonoSmall",
            UiKit.OnSurfaceVariant,
            mono: true);
        ordinal.VerticalAlignment = VerticalAlignment.Center;

        var path = UiKit.Mono(entry.Raw.Length > 0 ? entry.Raw : "（空条目）", "MonoSmall");
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        path.VerticalAlignment = VerticalAlignment.Center;

        var badge = AppControls.StatusBadge(entry.Tone, entry.ScopeLabel);

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(ordinal, 1);
        Grid.SetColumn(path, 2);
        Grid.SetColumn(badge, 3);
        row.Children.Add(dot);
        row.Children.Add(ordinal);
        row.Children.Add(path);
        row.Children.Add(badge);

        if (entry.IssueText.Length == 0)
        {
            return row;
        }

        var stack = UiKit.Stack(2);
        stack.Children.Add(row);

        var note = UiKit.Body(entry.IssueText, secondary: true);
        note.Margin = new Thickness(46, 0, 0, 0);
        stack.Children.Add(note);
        return stack;
    }

    /// <summary>变量列表（用户级 / 系统级两段）。</summary>
    private static void RenderVariables(EnvPageViewModel viewModel, Panel host)
    {
        host.Children.Add(BuildVariableGroup("用户级变量", viewModel.UserVariables, "user"));
        host.Children.Add(BuildVariableGroup("系统级变量", viewModel.MachineVariables, "machine"));
    }

    private static UIElement BuildVariableGroup(
        string title,
        ImmutableArray<EnvVariableView> variables,
        string scope)
    {
        var (card, body) = UiKit.CardWithBody(DesignTokens.RhythmInGroup);
        body.Children.Add(UiKit.SectionLabel($"{title}（{variables.Length} 项）"));

        if (variables.Length == 0)
        {
            body.Children.Add(UiKit.Body("没有读到变量。", secondary: true));
            return card;
        }

        foreach (var variable in variables)
        {
            body.Children.Add(BuildVariableRow(variable, scope));
        }

        return card;
    }

    private static UIElement BuildVariableRow(EnvVariableView variable, string scope)
    {
        var row = new Grid
        {
            ColumnSpacing = UiKit.Space3,
            MinHeight = DesignTokens.TableRowHeight,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = UiKit.Mono(variable.Name, "MonoSmall");
        name.VerticalAlignment = VerticalAlignment.Center;

        var valueHost = UiKit.Stack(2);
        var value = UiKit.Mono(variable.DisplayValue, "MonoSmall");
        value.TextWrapping = TextWrapping.NoWrap;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        valueHost.Children.Add(value);

        if (variable.IsLong)
        {
            valueHost.Children.Add(UiKit.Body(
                $"值长度 {variable.ValueLength} 字符，超过旧版编辑对话框的 2047 上限。",
                secondary: true));
        }

        Grid.SetColumn(name, 0);
        Grid.SetColumn(valueHost, 1);
        row.Children.Add(name);
        row.Children.Add(valueHost);

        // 复制按钮：用户常要把某个变量值贴进工单或另一个终端。
        var stack = UiKit.Stack(2);
        stack.Children.Add(row);
        stack.Children.Add(UiKit.ButtonBar(
            AppControls.CopyButton(
                variable.RawValue,
                _ => { })));
        return stack;
    }
}
