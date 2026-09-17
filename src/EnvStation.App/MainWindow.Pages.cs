using System.Globalization;

using EnvStation.Abstractions;
using AbsActions = EnvStation.Abstractions.Actions;
using CoreActions = EnvStation.Core.Actions;
using AbsPkg = EnvStation.Abstractions.Packages;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 主窗口的六个页面（partial）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独一个文件</b>：这些页面此前和外壳、导航、状态栏、性能探针一起挤在同一个
/// 2048 行的文件里。外壳要改导航，页面要改布局，两件事改的是同一个文件——这是"改不动"的直接来源。
/// 拆开之后每个文件只承担一件事（纪律 X-4：单文件 ≤ 800 行）。
/// </para>
/// <para>
/// <b>它们是"过渡形态"，不是终态。</b>本文件里的页面仍沿用旧的构建方式：造控件 + 直接发异步调用，
/// 状态只能靠一句「读取中…」表达。<c>EnvStation.Core\Diagnostics</c> 里的状态层与
/// <c>MainWindow.Mvvm</c> 里的骨架已经就绪，后续按页替换为"视图模型驱动渲染"，
/// 每次替换一页、每次都能单独验收。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    // ══════════════════════════ 概览 ══════════════════════════

    private UIElement BuildOverviewPage()
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("概览"));
        page.Children.Add(UiKit.Body(
            "只读检测，不修改系统。", secondary: true));

        var (infoCard, infoBody) = UiKit.CardWithBody(UiKit.Space1);
        infoBody.Children.Add(UiKit.Body("读取中…", secondary: true));
        page.Children.Add(infoCard);

        page.Children.Add(UiKit.Card(
            UiKit.SectionLabel("动作库"),
            UiKit.Body($"{_bridge?.ActionCount ?? 0} 个预制动作：探测、下载、解压、安装、环境变量、PATH、" +
                       "配置与镜像源、验证、清理。写入类动作需逐项授权，执行前自动创建快照。")));

        LoadOverviewAsync(infoBody);
        return UiKit.Scroll(page);
    }

    private async void LoadOverviewAsync(Panel host)
    {
        if (_bridge is null)
        {
            return;
        }

        try
        {
            var os = await _bridge.RunReadOnlyAsync("envstation.detect.os").ConfigureAwait(true);
            var arch = await _bridge.RunReadOnlyAsync("envstation.detect.arch").ConfigureAwait(true);
            var disk = await _bridge.RunReadOnlyAsync(
                "envstation.detect.disk",
                new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
                {
                    ["path"] = new AbsPkg.ScriptString(Path.GetTempPath()),
                }).ConfigureAwait(true);

            host.Children.Clear();
            host.Children.Add(UiKit.SectionLabel("本机"));

            if (os.Success)
            {
                host.Children.Add(UiKit.Row("操作系统", UiKit.Body(os.Outputs.GetValueOrDefault("product_name", "Windows"))));
                host.Children.Add(UiKit.Row("构建号", UiKit.Mono(
                    os.Outputs.GetValueOrDefault("build", "?") + "." + os.Outputs.GetValueOrDefault("revision", "?"))));
                host.Children.Add(UiKit.Row("区域设置", UiKit.Body(os.Outputs.GetValueOrDefault("culture", "?"))));
            }

            if (arch.Success)
            {
                var emulated = arch.Outputs.GetValueOrDefault("emulated") == "true";
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiKit.Space2 };
                panel.Children.Add(UiKit.Mono(arch.Outputs.GetValueOrDefault("os_arch", "?")));
                panel.Children.Add(UiKit.Badge(emulated ? "仿真" : "原生", emulated ? FindingTone.Warn : FindingTone.Success));
                host.Children.Add(UiKit.Row("CPU 架构", panel));
            }

            if (disk.Success)
            {
                var gigabytes = long.TryParse(disk.Outputs.GetValueOrDefault("available_bytes"), out var bytes)
                    ? bytes / 1024.0 / 1024.0 / 1024.0
                    : 0;
                host.Children.Add(UiKit.Row("系统盘可用", UiKit.Body($"{gigabytes:F1} GB")));
            }

            SetStatus("检测完成", "overview");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            host.Children.Clear();
            host.Children.Add(UiKit.Body("检测失败：" + ex.Message, secondary: true));
        }
    }

    // ══════════════════════════ 安装包 ══════════════════════════

    private UIElement BuildPackagesPage()
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("导入包"));
        page.Children.Add(UiKit.Body(
            "导入 .envstation 包：校验内容与声明，逐项确认能力授权后执行。", secondary: true));

        var pathBox = new TextBox
        {
            PlaceholderText = @"例如 D:\Downloads\python-env.envstation",
            FontFamily = UiKit.UiFont,
            FontSize = UiKit.Type("BodyMedium").Size,
            MinHeight = 34,
        };

        var (pickerCard, pickerBody) = UiKit.CardWithBody(UiKit.Space3);
        pickerBody.Children.Add(UiKit.SectionLabel("包文件"));
        pickerBody.Children.Add(pathBox);

        var (reportCard, reportBody) = UiKit.CardWithBody(UiKit.Space2);
        reportBody.Children.Add(UiKit.Body("尚未校验。", secondary: true));

        var (wallCard, wallBody) = UiKit.CardWithBody(UiKit.Space2);
        wallBody.Children.Add(UiKit.Body("校验通过后列出该包申请的能力。", secondary: true));

        var (trialCard, trialBody) = UiKit.CardWithBody(UiKit.Space2);
        trialBody.Children.Add(UiKit.SectionLabel("沙箱试运行"));
        trialBody.Children.Add(UiKit.Body(
            "在隔离目录中执行，核对声明与实际是否一致。不修改本机环境变量，不联网。", secondary: true));
        trialBody.Children.Add(UiKit.Body("尚未试运行。", secondary: true));

        pickerBody.Children.Add(UiKit.ButtonBar(
            UiKit.PrimaryButton("校验", () => ValidateAsync(pathBox.Text, reportBody, wallBody)),
            UiKit.SecondaryButton("试运行", () => RunTrialAsync(pathBox.Text, trialBody))));

        page.Children.Add(pickerCard);
        page.Children.Add(reportCard);
        page.Children.Add(wallCard);
        page.Children.Add(trialCard);
        return UiKit.Scroll(page);
    }

    /// <summary>
    /// 跑一次 V4 沙箱试运行并把结论渲染出来。
    /// </summary>
    /// <remarks>
    /// 界面上的这一步与 CLI 的 <c>envstation check --v4</c> 是同一段逻辑，
    /// 界面这里只是把它变成"一个按钮 + 一段结论"。之所以值得放进产品界面：
    /// 用户在导入别人分享的包之前，最想知道的是"它说的和它做的到底一不一样"，
    /// 而这件事只有跑一遍才知道。
    /// </remarks>
    private async void RunTrialAsync(string path, Panel host)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            host.Children.Clear();
            host.Children.Add(UiKit.SectionLabel("沙箱试运行"));
            host.Children.Add(UiKit.Body("未填写包文件路径。", secondary: true));
            return;
        }

        host.Children.Clear();
        host.Children.Add(UiKit.SectionLabel("沙箱试运行"));
        host.Children.Add(UiKit.Body("试运行中…", secondary: true));

        try
        {
            var trial = await KernelBridge.RunSandboxTrialAsync(path.Trim()).ConfigureAwait(true);

            host.Children.Clear();
            host.Children.Add(UiKit.SectionLabel("沙箱试运行"));

            if (trial is null)
            {
                host.Children.Add(UiKit.Body("无法读取包内容，请先校验。", secondary: true));
                return;
            }

            host.Children.Add(UiKit.Badge(
                trial.Report.HasBlockers ? "声明与实际不符" : "声明与实际一致",
                trial.Report.HasBlockers ? FindingTone.Error : FindingTone.Success));

            host.Children.Add(UiKit.Row("结果", UiKit.Body(trial.Outcome.Message)));
            host.Children.Add(UiKit.Row("实际使用", UiKit.Mono(
                trial.ExercisedCapabilities.Length == 0 ? "（无）" : string.Join("、", trial.ExercisedCapabilities),
                "MonoSmall")));

            if (trial.UndeclaredCapabilities.Length > 0)
            {
                host.Children.Add(UiKit.Row("未声明能力", UiKit.Body(
                    string.Join("、", trial.UndeclaredCapabilities) + "，未出现在权限项中。")));
            }

            host.Children.Add(UiKit.Body(trial.Report.ToText(), secondary: true));
            SetStatus(trial.Report.HasBlockers ? "试运行：声明与实际不符" : "试运行：声明与实际一致", "packages");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            host.Children.Clear();
            host.Children.Add(UiKit.SectionLabel("沙箱试运行"));
            host.Children.Add(UiKit.Body("试运行失败：" + ex.Message, secondary: true));
        }
    }

    private async void ValidateAsync(string path, Panel report, Panel wall)
    {
        if (_bridge is null || string.IsNullOrWhiteSpace(path))
        {
            report.Children.Clear();
            report.Children.Add(UiKit.Body("未填写包文件路径。", secondary: true));
            return;
        }

        report.Children.Clear();
        report.Children.Add(UiKit.Body("校验中…", secondary: true));

        try
        {
            var result = await KernelBridge.ValidatePackageAsync(path.Trim()).ConfigureAwait(true);

            report.Children.Clear();
            report.Children.Add(UiKit.SectionLabel("校验结果"));
            report.Children.Add(UiKit.Badge(
                result.CanImport ? "可以导入" : "禁止导入",
                result.CanImport ? FindingTone.Success : FindingTone.Error));
            report.Children.Add(UiKit.Body(result.Report.ToText()));

            wall.Children.Clear();

            if (result.Manifest is not { } manifest)
            {
                return;
            }

            report.Children.Add(UiKit.Row("包名", UiKit.Body(manifest.Name)));
            report.Children.Add(UiKit.Row("包 ID", UiKit.Mono(manifest.Id)));
            report.Children.Add(UiKit.Row("版本", UiKit.Mono(
                $"{manifest.Version}（标准 {manifest.SpecVersion} / 档位 {manifest.Tier}）")));
            report.Children.Add(UiKit.Row("质量评分", UiKit.Body($"{result.QualityScore} / 100")));

            // 能力授权（需求 IMP-2 / IMP-3）：逐项独立勾选，且必须用人话说明。
            wall.Children.Add(UiKit.SectionLabel("能力授权（逐项确认）"));
            wall.Children.Add(UiKit.Body(
                "未勾选的能力，对应动作执行时将被拒绝。", secondary: true));

            foreach (var (capability, explanation) in manifest.Permissions.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                var content = new StackPanel { Spacing = 2 };
                content.Children.Add(UiKit.Mono(capability, "MonoSmall"));
                content.Children.Add(UiKit.Body(explanation, secondary: true));

                wall.Children.Add(new CheckBox
                {
                    MinHeight = 32,
                    Tag = capability,
                    Content = content,
                });
            }

            SetStatus($"已校验 {Path.GetFileName(path)} · 质量评分 {result.QualityScore}", "packages");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            report.Children.Clear();
            report.Children.Add(UiKit.Body("校验失败：" + ex.Message, secondary: true));
        }
    }


    private UIElement BuildActionsPage()
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("动作库"));
        page.Children.Add(UiKit.Body(
            "包可调用的全部预制动作，不含任意命令执行。", secondary: true));

        if (_bridge is null)
        {
            page.Children.Add(UiKit.Body("内核未就绪。", secondary: true));
            return UiKit.Scroll(page);
        }

        // 一行一动作用两段纯文本，不再各自套卡片、套栈：
        // 卡片边框对"扫一眼有哪些动作"没有任何帮助，却让每个动作多出两个测量节点。
        // 按能力家族分组保留——它是这一页唯一的结构信息，去掉就只剩一长串 ID。
        var (card, body) = UiKit.CardWithBody(UiKit.Space1);

        foreach (var group in _bridge.Descriptors
            .GroupBy(static d => ActionGroup(d.ActionId), StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            var label = UiKit.SectionLabel($"{group.Key} · {group.Count()}");
            label.Margin = new Thickness(0, UiKit.Space4, 0, UiKit.Space2);
            body.Children.Add(label);

            foreach (var descriptor in group.OrderBy(static d => d.ActionId, StringComparer.Ordinal))
            {
                body.Children.Add(ActionRow(descriptor));
            }
        }

        page.Children.Add(card);
        return UiKit.Scroll(page);
    }

    /// <summary>
    /// 动作库的一行：动作 ID + 一行说明。
    /// </summary>
    /// <remarks>
    /// <b>不换行是刻意的</b>：这一页有 74 个动作、每行两段文本，全部允许换行意味着
    /// 每趟布局都要重新做 150 次断行计算；而这两段文字本来就短，从来不需要换行——
    /// 换行只会在窄窗口下把行高撑成两行，让整页节奏变成深浅不一的条纹。
    /// 超长时用省略号截断，是列表的正确行为。
    /// </remarks>
    private static UIElement ActionRow(AbsActions.ActionDescriptor descriptor)
    {
        var id = UiKit.Mono(descriptor.ActionId, "MonoSmall");
        id.TextWrapping = TextWrapping.NoWrap;
        id.TextTrimming = TextTrimming.CharacterEllipsis;

        var summary = UiKit.Body(
            $"{descriptor.DisplayName} · {descriptor.CapabilityId}" +
            (descriptor.IsIdempotent ? " · 幂等" : string.Empty) +
            (descriptor.HasInverse ? " · 可逆" : string.Empty),
            secondary: true);
        summary.TextWrapping = TextWrapping.NoWrap;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;

        var row = new StackPanel { Spacing = 0, MinHeight = 40 };
        row.Children.Add(id);
        row.Children.Add(summary);
        return row;
    }


    /// <summary>取动作 ID 的中段作为分组名（<c>envstation.path.edit</c> → <c>path</c>）。</summary>
    private static string ActionGroup(string actionId)
    {
        var parts = actionId.Split('.');
        return parts.Length >= 2 ? parts[1] : actionId;
    }
}
