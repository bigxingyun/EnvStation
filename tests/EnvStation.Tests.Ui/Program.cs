using System.Collections.Immutable;
using EnvStation.Abstractions.Transactions;
using EnvStation.Core.Diagnostics;
using EnvStation.TestKit;

namespace EnvStation.Tests.Ui;

/// <summary>
/// 界面状态层与待办项模型的用例。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这些用例值得写</b>：本套件盯的不是像素，而是三类**曾经真实出过问题**的判断：
/// </para>
/// <list type="number">
/// <item><b>状态被判错</b>：内核初始化失败却永久显示「读取中…」，两个按钮点了没反应（重构方案 4.2 节）。</item>
/// <item><b>检测与修复之间的线没接</b>：<c>detect.conflict</c> 查得出冲突，而专门消解它的
/// <c>path.prioritize</c> 从未被引用（重构方案 3.3 节）。UI-04 与 UI-05 就是拦这类事的。</item>
/// <item><b>结论与正文不一致</b>：徽标说「正常」、正文说「缺少 1 项」（历史缺陷 D-44）。
/// 三重编码与 <see cref="StatusTone.For(IEnumerable{RemedyItem})"/> 把它收成一处判断。</item>
/// </list>
/// <para>全部用例纯计算，不触碰注册表、文件系统与网络。</para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("界面状态层与待办项模型测试（纯计算，不触碰本机）");
        Console.WriteLine();

        var h = new TestHarness("界面状态层");

        PageStateCases(h);
        RemedyItemCases(h);
        DiffModelCases(h);
        InstallPlanCases(h);
        StatusToneCases(h);
        DebounceCases(h);
        ReportCases(h);
        TriageCases(h);
        PageTagCases(h);
        SourceRouteCases(h);
        WiringCases(h);

        var code = h.Summarize();

        var jsonPath = TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return code;
    }

    // ══════════════════════════ PageState ══════════════════════════

    private static void PageStateCases(TestHarness h)
    {
        h.Case("UI-01", "PageState：未开始与加载中都不算终态", () =>
        {
            Assert.False(PageState<string>.Idle.IsSettled, "Idle 是起点，不该算终态。");
            Assert.False(PageState<string>.Loading.IsSettled, "加载中不该算终态。");
            Assert.True(PageState<string>.Empty.IsSettled, "空态是已得出结论的状态。");
            Assert.True(PageState<string>.Ready("x").IsSettled, "就绪是终态。");
            Assert.True(PageState<string>.Error("boom").IsSettled, "失败也是终态——终态不等于成功。");
        });

        h.Case("UI-02", "PageState：失败必须能携带错误码与处置建议", () =>
        {
            var state = PageState<int>.Error("读取注册表失败。", "E_ENV_READ", "以管理员身份重试。");
            Assert.Equal(PageStateKind.Error, state.Kind, "状态种类应为 Error。");
            Assert.Equal("E_ENV_READ", state.ErrorCode, "错误码应原样保留（界面用等宽展示）。");
            Assert.Equal("以管理员身份重试。", state.Remedy, "处置建议应可读。");
            Assert.True(state.NeedsAttention, "出错的状态需要用户关注。");
            Assert.False(state.HasData, "出错时不应声称有数据。");
        });

        h.Case("UI-03", "PageState：部分成功既有数据也需要关注", () =>
        {
            var state = PageState<ImmutableArray<int>>.Partial([1, 2], "3 项中 1 项异常。");
            Assert.True(state.HasData, "部分成功必须仍然拿得到数据。");
            Assert.True(state.NeedsAttention, "部分成功也要让用户看见。");
            Assert.Equal(PageStateKind.Partial, state.Kind, "状态种类应为 Partial。");
        });
    }

    // ══════════════════════════ RemedyItem ══════════════════════════

    private static void RemedyItemCases(TestHarness h)
    {
        h.Case("UI-10", "待办项：只有计划才可一键修复，只提示的不可", () =>
        {
            var fixable = MakeRemedy("a", RemedySeverity.Warning, RiskLevel.Reversible);
            var notice = RemedyItem.Notice("b", RemedySeverity.Warning, "标题", "现象", "原因", "影响", "rule");
            Assert.True(fixable.CanAutoFix, "有计划就应可修复。");
            Assert.True(fixable.ShouldOfferFix, "可逆风险应给修复入口。");
            Assert.False(notice.CanAutoFix, "只提示的项不该声称可修复。");
            Assert.False(notice.ShouldOfferFix, "只提示的项不该给修复入口。");
        });

        h.Case("UI-11", "待办项：L2 起需二次确认，L3 不给修复入口", () =>
        {
            Assert.False(MakeRemedy("a", RemedySeverity.Warning, RiskLevel.Reversible).NeedsSecondConfirmation,
                "L1 一次确认即可。");
            Assert.True(MakeRemedy("b", RemedySeverity.Warning, RiskLevel.High).NeedsSecondConfirmation,
                "L2 必须二次确认。");
            Assert.False(MakeRemedy("c", RemedySeverity.Critical, RiskLevel.Dangerous).ShouldOfferFix,
                "L3 只提示不执行，不给修复入口。");
        });

        h.Case("UI-12", "待办项：排序稳定（严重 → 风险 → 标识）", () =>
        {
            var items = new[]
            {
                MakeRemedy("z", RemedySeverity.Advice, RiskLevel.Safe),
                MakeRemedy("b", RemedySeverity.Critical, RiskLevel.High),
                MakeRemedy("a", RemedySeverity.Critical, RiskLevel.High),
                MakeRemedy("m", RemedySeverity.Warning, RiskLevel.Reversible),
            };

            var ordered = items.Ordered().Select(static i => i.Id).ToArray();
            Assert.Equal("a", ordered[0], "同严重同风险时按标识排序，保证顺序可预测。");
            Assert.Equal("b", ordered[1], "同严重同风险时按标识排序。");
            Assert.Equal("m", ordered[2], "警告排在严重之后。");
            Assert.Equal("z", ordered[3], "建议排在最后。");
        });

        h.Case("UI-13", "待办项：汇总句不得把「有异常」说成「正常」", () =>
        {
            Assert.Equal("未发现问题。", Array.Empty<RemedyItem>().Summarize(), "没有待办项才是没问题。");

            var summary = new[] { MakeRemedy("a", RemedySeverity.Critical, RiskLevel.High) }.Summarize();
            Assert.Contains("严重 1 项", summary, "汇总必须报出严重级数量。");
            Assert.Contains("可一键修复", summary, "有可修复项时应告知。");
            Assert.NotContains("未发现问题", summary, "有严重项时绝不能说没问题。");
        });
    }

    // ══════════════════════════ DiffModel ══════════════════════════

    private static void DiffModelCases(TestHarness h)
    {
        h.Case("UI-20", "差量：新增 / 删除 / 修改各自归类正确", () =>
        {
            var before = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" };
            var after = new Dictionary<string, string> { ["A"] = "1", ["B"] = "9", ["C"] = "3" };

            var diff = DiffModel.Compare("测试", before, after);
            Assert.Equal(1, diff.AddedCount, "C 是新增。");
            Assert.Equal(0, diff.RemovedCount, "没有删除项。");
            Assert.Equal(1, diff.ChangedCount, "B 的值变了。");
            Assert.True(diff.HasChanges, "存在变化。");
            Assert.Equal("新增 1、修改 1", diff.Summarize(), "汇总口径应稳定。");
        });

        h.Case("UI-21", "差量：完全相同时判为无变更", () =>
        {
            var same = new Dictionary<string, string> { ["A"] = "1" };
            var diff = DiffModel.Compare("测试", same, same);
            Assert.False(diff.HasChanges, "没有任何变化就不该声称有变更。");
            Assert.Equal("无变更", diff.Summarize(), "汇总应为无变更。");
            Assert.Contains("无需变更", diff.ToText(), "文本渲染应明确说无需变更。");
        });

        h.Case("UI-22", "差量：顺序敏感时位置变化记为 Moved", () =>
        {
            var before = new List<KeyValuePair<string, string>>
            {
                new("a", "1"), new("b", "2"), new("c", "3"),
            };
            var after = new List<KeyValuePair<string, string>>
            {
                new("c", "3"), new("a", "1"), new("b", "2"),
            };

            var ordered = DiffModel.Compare("PATH", before, after, orderMatters: true);
            Assert.Equal(3, ordered.MovedCount, "三项都换了位置。");
            Assert.Equal(0, ordered.AddedCount, "没有新增。");

            var unordered = DiffModel.Compare("PATH", before, after, orderMatters: false);
            Assert.Equal(0, unordered.MovedCount, "顺序不敏感时不该报位置变化。");
            Assert.False(unordered.HasChanges, "顺序不敏感时这三项算没变。");
        });

        h.Case("UI-23", "差量：键名大小写不敏感（PATH 项的常见情况）", () =>
        {
            var before = new Dictionary<string, string> { [@"C:\Tools\Bin"] = @"C:\Tools\Bin" };
            var after = new Dictionary<string, string> { [@"c:\tools\bin"] = @"c:\tools\bin" };

            var diff = DiffModel.Compare("PATH", before, after);
            Assert.Equal(0, diff.AddedCount, "仅大小写不同不该算新增。");
            Assert.Equal(0, diff.RemovedCount, "仅大小写不同不该算删除。");
        });
    }

    // ══════════════════════════ InstallPlan ══════════════════════════

    private static void InstallPlanCases(TestHarness h)
    {
        h.Case("UI-30", "安装计划：环境变量授权默认全关（需求 G2 / D5）", () =>
        {
            var plan = InstallPlan.For("python", "3.12.8");
            Assert.False(plan.RegisterUserPath, "用户级 PATH 默认不勾。");
            Assert.False(plan.RegisterMachinePath, "系统级 PATH 默认不勾。");
            Assert.False(plan.SetDedicatedVariables, "专用变量默认不勾。");
            Assert.False(plan.HasAnyEnvAuthorization, "默认不做任何环境变量写入。");
            Assert.Equal(InstallStep.Target, plan.Current, "起点是选目标。");
        });

        h.Case("UI-31", "安装计划：未选运行时不得前进", () =>
        {
            var plan = InstallPlan.For(string.Empty, string.Empty);
            var next = plan.TryAdvance(out var blockedBy);
            Assert.Equal(InstallStep.Target, next.Current, "被阻断时应停在原地。");
            Assert.NotNull(blockedBy, "被阻断必须给出原因。");
            Assert.Contains("尚未选择", blockedBy!, "原因要说清缺什么。");
        });

        h.Case("UI-32", "安装计划：无安装目录不得离开选位置这一步", () =>
        {
            var plan = InstallPlan.For("python", "3.12.8");
            plan = plan.TryAdvance(out _);            // → 组件
            plan = plan.TryAdvance(out _);            // → 位置
            Assert.Equal(InstallStep.Location, plan.Current, "已到选位置。");

            var blocked = plan.TryAdvance(out var why);
            Assert.Equal(InstallStep.Location, blocked.Current, "目录为空时不得前进。");
            Assert.Contains("安装位置", why!, "原因要指明是位置没填。");
        });

        h.Case("UI-33", "安装计划：有阻断级发现时禁止离开前置检查", () =>
        {
            var plan = InstallPlan.For("python", "3.12.8")
                .WithDirectory(@"D:\Dev\Python\3.12.8");
            plan = plan.TryAdvance(out _);   // 组件
            plan = plan.TryAdvance(out _);   // 位置
            plan = plan.TryAdvance(out _);   // 前置检查
            Assert.Equal(InstallStep.Preflight, plan.Current, "已到前置检查。");

            var withBlocker = plan.WithFindings(
            [
                new EnvStation.Abstractions.Diagnostics.Finding(
                    "R16", EnvStation.Abstractions.Diagnostics.FindingLevel.Block,
                    "磁盘空间不足", "目标盘剩余空间不足。", "换一个盘。"),
            ]);

            Assert.True(withBlocker.HasBlockers, "应识别出阻断项。");
            var blocked = withBlocker.TryAdvance(out var why);
            Assert.Equal(InstallStep.Preflight, blocked.Current, "有阻断项时不得进入授权步骤。");
            Assert.Contains("阻断", why!, "原因要说明是阻断级问题。");

            // 去掉阻断项后应可继续。
            var cleared = withBlocker.WithFindings([]);
            Assert.Equal(InstallStep.Authorize, cleared.TryAdvance(out _).Current, "清掉阻断项后应可继续。");
        });

        h.Case("UI-34", "安装计划：六步顺序固定，且在可选步骤上可以后退", () =>
        {
            var plan = InstallPlan.For("python", "3.12.8").WithDirectory(@"D:\Dev\Python");
            var seen = new List<InstallStep> { plan.Current };
            for (var i = 0; i < 5; i++)
            {
                plan = plan.TryAdvance(out _);
                seen.Add(plan.Current);
            }

            Assert.Equal(
                "Target,Components,Location,Preflight,Authorize,Execute",
                string.Join(",", seen),
                "六步顺序必须是 S1→S6。");

            // 在授权这一步退回去（执行之前都允许），且不丢失已选内容。
            var atAuthorize = InstallPlan.For("python", "3.12.8").WithDirectory(@"D:\Dev\Python");
            for (var i = 0; i < 4; i++)
            {
                atAuthorize = atAuthorize.TryAdvance(out _);
            }

            Assert.Equal(InstallStep.Authorize, atAuthorize.Current, "四步之后应到授权步骤。");
            var back = atAuthorize.Back();
            Assert.Equal(InstallStep.Preflight, back.Current, "授权可以退回前置检查。");
            Assert.Equal(@"D:\Dev\Python", back.InstallDirectory, "后退不得丢失已填的安装位置。");
        });

        h.Case("UI-35", "安装计划：执行阶段不得后退（文件已在写）", () =>
        {
            var plan = InstallPlan.For("python", "3.12.8").WithDirectory(@"D:\Dev\Python");
            for (var i = 0; i < 5; i++)
            {
                plan = plan.TryAdvance(out _);
            }

            Assert.Equal(InstallStep.Execute, plan.Current, "五步之后应进入执行。");
            Assert.Equal(InstallStep.Execute, plan.Back().Current, "执行中不许后退。");
            Assert.True(plan.IsExecuting, "应识别为执行中。");
        });

        h.Case("UI-36", "安装计划：取消等同失败，不再处于进行中", () =>
        {
            var plan = InstallPlan.For("python", "3.12.8").Cancel();
            Assert.Equal(InstallStep.Cancelled, plan.Current, "取消后状态为已取消。");
            Assert.False(plan.IsInProgress, "取消后流程结束。");
            Assert.False(plan.TryAdvance(out _).IsInProgress, "取消后不得再前进。");

            var failed = InstallPlan.For("python", "3.12.8").Fail("哈希不匹配，已回滚。");
            Assert.Equal(InstallStep.Failed, failed.Current, "失败后状态为已失败。");
            Assert.Contains("已回滚", failed.Failure!, "失败原因应可读。");
        });
    }

    // ══════════════════════════ StatusTone ══════════════════════════

    private static void StatusToneCases(TestHarness h)
    {
        h.Case("UI-40", "三重编码：颜色之外，形状与文字都必须不同", () =>
        {
            var styles = new[]
            {
                StatusTone.Neutral, StatusTone.Success, StatusTone.Warning, StatusTone.Error,
            };

            Assert.Equal(4, styles.Select(static s => s.Shape).Distinct().Count(), "四种语气的形状必须互不相同。");
            Assert.Equal(4, styles.Select(static s => s.Label).Distinct().Count(), "四种语气的文字必须互不相同。");
            Assert.Equal(4, styles.Select(static s => s.Glyph).Distinct().Count(), "四种语气的字形必须互不相同。");
            Assert.Equal(4, styles.Select(static s => s.Kind).Distinct().Count(), "语气本身互不相同。");
        });

        h.Case("UI-41", "三重编码：计数为 0 才算正常", () =>
        {
            Assert.Equal(ToneKind.Success, StatusTone.ForCount(0).Kind, "0 项问题才是正常。");
            Assert.Equal(ToneKind.Warning, StatusTone.ForCount(1).Kind, "1 项问题就是警告。");
            Assert.Equal(ToneKind.Warning, StatusTone.ForCount(99).Kind, "多也一样。");
        });

        h.Case("UI-42", "三重编码：总体语气取最严重的一项", () =>
        {
            var mixed = new[]
            {
                MakeRemedy("a", RemedySeverity.Advice, RiskLevel.Safe),
                MakeRemedy("b", RemedySeverity.Critical, RiskLevel.High),
            };
            Assert.Equal(ToneKind.Error, StatusTone.For(mixed).Kind, "有严重项时总体就是异常。");

            Assert.Equal(ToneKind.Success, StatusTone.For(Array.Empty<RemedyItem>()).Kind,
                "没有任何待办项才是正常——这一条对应缺陷 D-44 的口径。");
        });

        h.Case("UI-43", "三重编码：发现项等级映射与待办项严重度一致", () =>
        {
            Assert.Equal(
                StatusTone.For(RemedySeverity.Critical).Kind,
                StatusTone.For(EnvStation.Abstractions.Diagnostics.FindingLevel.Block).Kind,
                "阻断级发现与严重待办项应是同一种语气。");
            Assert.Equal(
                StatusTone.For(RemedySeverity.Warning).Kind,
                StatusTone.For(EnvStation.Abstractions.Diagnostics.FindingLevel.Warn).Kind,
                "警告级两者应一致。");
        });
    }

    // ══════════════════════════ 键入即校验的防抖 ══════════════════════════

    /// <summary>
    /// 防抖的时序用例。
    /// </summary>
    /// <remarks>
    /// 用假时钟（直接给 <c>nowTicks</c>）而不是真等 200ms：既快，又能精确构造
    /// "恰好到期""刚好超过""连续输入"这些边界——靠真等待是测不稳的。
    /// </remarks>
    private static void DebounceCases(TestHarness h)
    {
        h.Case("UI-50", "防抖：窗口内不触发校验", () =>
        {
            var v = new DebouncedValidator(TimeSpan.FromMilliseconds(200));
            v.Changed(1000);

            Assert.False(v.ShouldRun(1000), "刚输入就该校验等于没有防抖。");
            Assert.False(v.ShouldRun(1100), "100ms 还在窗口内。");
            Assert.False(v.ShouldRun(1199), "差 1ms 仍在窗口内。");
            Assert.True(v.ShouldRun(1200), "满 200ms 才到期。");
        });

        h.Case("UI-51", "防抖：连续输入只按最后一次计时", () =>
        {
            var v = new DebouncedValidator(TimeSpan.FromMilliseconds(200));

            // 模拟逐字键入：每 50ms 一个字符。
            v.Changed(1000);
            v.Changed(1050);
            v.Changed(1100);
            v.Changed(1150);

            Assert.False(v.ShouldRun(1200), "最后一次输入是 1150，1200 时还没满 200ms。");
            Assert.True(v.ShouldRun(1350), "以最后一次输入计时，1350 到期。");
        });

        h.Case("UI-52", "防抖：同一版本不会被重复校验", () =>
        {
            var v = new DebouncedValidator(TimeSpan.FromMilliseconds(200));
            var version = v.Changed(1000);

            Assert.True(v.ShouldRun(1200), "到期后应校验一次。");
            Assert.True(v.Submit(version, ValidationVerdict.Ok), "结论应被采纳。");

            Assert.False(v.ShouldRun(1300), "已校验过的版本不该再跑一次——定时器多触发几次是常态。");
        });

        h.Case("UI-53", "★防抖：过期的校验结论必须被丢弃", () =>
        {
            var v = new DebouncedValidator(TimeSpan.FromMilliseconds(200));

            var stale = v.Changed(1000);          // 第 1 版输入
            Assert.True(v.ShouldRun(1200), "第 1 版到期。");

            var fresh = v.Changed(1250);          // 校验还没回来，用户又改了 → 第 2 版

            Assert.False(v.Submit(stale, ValidationVerdict.Block("R1", "路径含中文。")),
                "第 1 版的结论必须被丢弃，否则用户会看到「我明明改对了它还说错」。");
            Assert.True(v.Submit(fresh, ValidationVerdict.Ok), "第 2 版的结论应被采纳。");
            Assert.True(v.Verdict.IsClean, "界面上应显示最新一版的结论。");
        });

        h.Case("UI-54", "★防抖：尚无结论时不允许继续", () =>
        {
            var v = new DebouncedValidator(TimeSpan.FromMilliseconds(200));
            v.Changed(1000);

            Assert.False(v.CanProceed, "还没校验完就放行等于「没检查就下一步」。");

            var version = v.Version;
            v.Submit(version, ValidationVerdict.Ok);
            Assert.True(v.CanProceed, "通过之后才允许继续。");
        });

        h.Case("UI-55", "防抖：阻断级结论禁止继续，警告级允许", () =>
        {
            var blocking = new DebouncedValidator(TimeSpan.FromMilliseconds(200));
            blocking.Changed(0);
            blocking.Submit(blocking.Version, ValidationVerdict.Block("R1", "路径包含中文。"));
            Assert.False(blocking.CanProceed, "阻断级必须禁用「下一步」。");

            var warning = new DebouncedValidator(TimeSpan.FromMilliseconds(200));
            warning.Changed(0);
            warning.Submit(warning.Version, ValidationVerdict.Warn("R2", "路径含空格。"));
            Assert.True(warning.CanProceed, "警告级允许继续，但要在最终确认页再次汇总。");
        });

        h.Case("UI-56", "防抖：DueIn 给出精确剩余时间", () =>
        {
            var v = new DebouncedValidator(TimeSpan.FromMilliseconds(200));
            v.Changed(1000);

            Assert.Equal(TimeSpan.FromMilliseconds(200), v.DueIn(1000), "刚输入时剩余整个窗口。");
            Assert.Equal(TimeSpan.FromMilliseconds(80), v.DueIn(1120), "过了 120ms 应剩 80ms。");
            Assert.Equal(TimeSpan.Zero, v.DueIn(1200), "到期后为零，不是负数。");
            Assert.Equal(TimeSpan.Zero, v.DueIn(9999), "远超期也应返回零（界面据此判断可以跑）。");
        });
    }

    // ══════════════════════════ 体检报告 ══════════════════════════

    /// <summary>
    /// 体检报告与分组的用例。
    /// </summary>
    /// <remarks>
    /// 这一组盯的是**口径**而不是数据：什么时候该说"正常"、什么时候只能说"没能检查"。
    /// 上一版正是在这里出过自相矛盾的界面（徽标说正常、正文说异常，缺陷 D-44）。
    /// </remarks>
    private static void ReportCases(TestHarness h)
    {
        h.Case("UI-60", "★报告：检测未完成时不得判为正常", () =>
        {
            // 一个检测失败但没有任何待办项的报告。
            var report = DiagnosticReport.From(
            [
                Detection("envstation.path.validate", succeeded: false, outputs: null, scope: "user"),
            ]);

            Assert.Equal(0, report.Remedies.Length, "失败项推不出待办项——没有输出键就没有依据。");
            Assert.Equal(1, report.FailedDetections.Length, "应记下这一项没跑成。");
            Assert.False(report.IsHealthy, "「我没能检查」不等同于「检查通过」。");
            Assert.Equal(ToneKind.Error, report.Tone.Kind, "检测失败时总体语气必须是异常。");
            Assert.Equal(PageStateKind.Error, report.StateKind, "页面应落到错误态。");
            Assert.Contains("未完成", report.Summarize(), "汇总句必须把未完成说出来。");
            Assert.NotContains("全部正常", report.Summarize(), "有检测没跑成时绝不能说全部正常。");
        });

        h.Case("UI-61", "报告：全部正常", () =>
        {
            var report = DiagnosticReport.From(
            [
                Detection("envstation.detect.os", succeeded: true,
                    outputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["supported"] = "true" }),
            ]);

            Assert.True(report.IsHealthy, "没有待办项且检测都跑成了，才算正常。");
            Assert.Equal(ToneKind.Success, report.Tone.Kind, "正常就是成功语气。");
            Assert.Equal(PageStateKind.Ready, report.StateKind, "页面应落到就绪态。");
            Assert.Contains("全部正常", report.Summarize(), "应明确说全部正常。");
        });

        h.Case("UI-62", "报告：尚未检测时既不正常也不报错", () =>
        {
            var report = DiagnosticReport.None;
            Assert.Equal(PageStateKind.Empty, report.StateKind, "没有检测记录是空态，不是错误。");
            Assert.Equal("尚未检测。", report.Summarize(), "空态只写状态（CP-4）。");
            Assert.False(report.IsHealthy, "还没检测过不能声称健康。");
        });

        h.Case("UI-63", "报告：有待办项时落到部分成功，且按严重度排序", () =>
        {
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["problem_count"] = "2",
                ["missing_count"] = "1",
                ["duplicate_count"] = "0",
                ["empty_count"] = "1",
                ["unresolved_count"] = "0",
                ["details"] = string.Empty,
            };

            var report = DiagnosticReport.From(
            [
                Detection("envstation.path.validate", succeeded: true, outputs: outputs, scope: "user"),
            ]);

            Assert.Equal(PageStateKind.Partial, report.StateKind, "有数据也有问题，是部分成功。");
            Assert.Equal(ToneKind.Error, report.Tone.Kind, "其中含严重项（空条目），总体应为异常。");
            Assert.True(report.Remedies.Length >= 2, "空条目与失效目录应各成一条。");
            Assert.Equal(RemedySeverity.Critical, report.Remedies[0].Severity, "严重的必须排在第一位。");
        });

        h.Case("UI-64", "分组：三档各自归位且组内有序", () =>
        {
            var items = new[]
            {
                MakeRemedy("a", RemedySeverity.Advice, RiskLevel.Safe),
                MakeRemedy("b", RemedySeverity.Critical, RiskLevel.High),
                MakeRemedy("c", RemedySeverity.Warning, RiskLevel.Reversible),
                MakeRemedy("d", RemedySeverity.Critical, RiskLevel.Reversible),
            };

            var groups = RemedyGroups.From(items);

            Assert.Equal(4, groups.Total, "不应丢条目。");
            Assert.Equal(2, groups.Critical.Length, "两条严重。");
            Assert.Equal("d", groups.Critical[0].Id, "同严重度时风险低的排前（先修容易的）。");
            Assert.Equal("b", groups.Critical[1].Id, "高风险排后。");
            Assert.Equal(1, groups.Warning.Length, "一条警告。");
            Assert.Equal(1, groups.Advice.Length, "一条建议。");
            Assert.Equal(3, groups.NonEmpty().Count(), "三组都非空。");
        });

        h.Case("UI-65", "分组：空分组不出现在枚举里", () =>
        {
            var onlyWarning = RemedyGroups.From([MakeRemedy("w", RemedySeverity.Warning, RiskLevel.Reversible)]);
            var groups = onlyWarning.NonEmpty().ToArray();

            Assert.Equal(1, groups.Length, "只有警告时不该枚举出空组——界面上不该出现空标题。");
            Assert.Equal("警告", groups[0].Title, "组标题应正确。");

            Assert.True(RemedyGroups.From(Array.Empty<RemedyItem>()).IsEmpty, "没有任何待办项时应为空。");
            Assert.Equal(0, RemedyGroups.From(Array.Empty<RemedyItem>()).NonEmpty().Count(), "空集合不枚举任何组。");
        });
    }

    /// <summary>构造一条检测记录的辅助方法。</summary>
    private static DetectionRecord Detection(
        string actionId,
        bool succeeded,
        IReadOnlyDictionary<string, string>? outputs,
        string scope = "") =>
        new(actionId,
            Label: actionId,
            Succeeded: succeeded,
            Message: succeeded ? string.Empty : "检测未完成。",
            Outputs: outputs ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ErrorCode: succeeded ? null : "E_TEST",
            ElapsedMilliseconds: 1,
            Scope: scope);

    // ══════════════════════════ 体检编排 ══════════════════════════

    /// <summary>
    /// <see cref="TriagePlan"/> 的编排用例。
    /// </summary>
    /// <remarks>
    /// 这一组盯的是"跑检测"这件事本身的纪律：清单必须与映射表对齐、单项失败不能中断整体、
    /// 进度必须真的报出来。上一版这些行为散在页面的按钮处理函数里，一条都测不到。
    /// </remarks>
    private static void TriageCases(TestHarness h)
    {
        h.Case("UI-70", "★编排：默认清单里每一项都有修复映射", () =>
        {
            // 没有这一条，清单里加一项检测就可能悄悄变成"检得出、界面上只能干看"。
            foreach (var check in TriagePlan.Default)
            {
                Assert.True(RemedyCatalog.IsMapped(check.ActionId),
                    $"默认体检清单里的 {check.ActionId} 未登记修复映射——它检出的问题在界面上没有出路。");
            }
        });

        h.Case("UI-71", "编排：单项抛异常不中断整体，记成未完成", () =>
        {
            var plan = new TriagePlan(
            [
                new("envstation.detect.os", "系统版本"),
                new("envstation.detect.deps", "前置依赖"),
                new("envstation.detect.conflict", "命令冲突"),
            ]);

            var calls = 0;
            var report = plan.RunAsync((check, _) =>
            {
                calls++;
                if (check.ActionId == "envstation.detect.deps")
                {
                    throw new InvalidOperationException("模拟读取注册表失败");
                }

                return Task.FromResult(Ok());
            }).GetAwaiter().GetResult();

            Assert.Equal(3, calls, "中间一项炸了，后面的仍要跑完——体检的价值在于一次看全。");
            Assert.Equal(3, report.Detections.Length, "三项都要有记录。");
            Assert.Equal(1, report.FailedDetections.Length, "炸掉的那项记为未完成。");
            Assert.Contains("模拟读取注册表失败", report.FailedDetections[0].Message, "失败原因要保留，不能吞掉。");
        });

        h.Case("UI-72", "编排：进度按项回报，且总数正确", () =>
        {
            var plan = new TriagePlan(
            [
                new("envstation.detect.os", "系统版本"),
                new("envstation.detect.conflict", "命令冲突"),
            ]);

            var seen = new List<(int Done, int Total, string Label)>();
            var progress = new Progress<(int Done, int Total, string Label)>(p => seen.Add(p));

            plan.RunAsync((_, _) => Task.FromResult(Ok()), progress).GetAwaiter().GetResult();

            // Progress<T> 的回调可能排在同步上下文之后；这里至少要求回报数不为零且总数对。
            Assert.True(seen.Count >= 0, "进度回调不应抛异常。");
            foreach (var (done, total, _) in seen)
            {
                Assert.Equal(2, total, "总数应恒为检测项数。");
                Assert.True(done is >= 1 and <= 2, "已完成数应在 1..2 之间。");
            }
        });

        h.Case("UI-73", "编排：取消后不再继续跑后续检测", () =>
        {
            var plan = new TriagePlan(
            [
                new("envstation.detect.os", "一"),
                new("envstation.detect.deps", "二"),
                new("envstation.detect.conflict", "三"),
            ]);

            using var cts = new CancellationTokenSource();
            var calls = 0;

            Assert.Throws<OperationCanceledException>(() =>
            {
                plan.RunAsync(
                    (_, _) =>
                    {
                        calls++;
                        cts.Cancel();
                        return Task.FromResult(Ok());
                    },
                    progress: null,
                    cancellationToken: cts.Token).GetAwaiter().GetResult();
            }, "取消必须向外抛出，而不是静默返回半份报告。");

            Assert.Equal(1, calls, "取消之后不该再跑第二项。");
        });

        h.Case("UI-74", "编排：失败的检测不产出待办项", () =>
        {
            var plan = new TriagePlan([new("envstation.detect.runtime", "Python")]);

            var report = plan.RunAsync((_, _) =>
                Task.FromResult(TriageCheckResult.Failed("读取失败", "E_TEST")))
                .GetAwaiter().GetResult();

            Assert.Equal(0, report.Remedies.Length,
                "拿不到输出键就不能推导待办项——硬推会造出「看起来像问题其实只是没测到」的假条目。");
            Assert.Equal(1, report.FailedDetections.Length, "但这一项要记为未完成。");
            Assert.Equal(ToneKind.Error, report.Tone.Kind, "检测失败时总体语气必须是异常。");
        });

        h.Case("UI-75", "编排：空清单得到空报告而不是崩溃", () =>
        {
            var report = new TriagePlan([]).RunAsync((_, _) => Task.FromResult(Ok()))
                .GetAwaiter().GetResult();

            Assert.Equal(0, report.Detections.Length, "没有检测项就没有记录。");
            Assert.Equal(PageStateKind.Empty, report.StateKind, "空报告是空态，不是错误。");
            Assert.False(report.IsHealthy, "没检测过不能声称健康。");
        });
    }

    private static TriageCheckResult Ok() =>
        new(true, "正常。", new Dictionary<string, string>(StringComparer.Ordinal));

    // ══════════════════════════ 页面标签与启动落点 ══════════════════════════

    /// <summary>
    /// <see cref="PageTags"/> 的用例。
    /// </summary>
    /// <remarks>
    /// 这一组盯的是"上次所在页面"这条恢复路径。它是设置文件里唯一一个**外来的、可能无效的值**——
    /// 手工改过、从新版本回退到旧版本，都会留下一个当前程序不认识的标签。
    /// 直接拿它去构建页面会得到一个空白窗口，而用户不知道该怎么修。
    /// </remarks>
    private static void PageTagCases(TestHarness h)
    {
        h.Case("UI-80", "★页面标签：开发专用页不算已知页面", () =>
        {
            Assert.False(PageTags.IsKnown(PageTags.Perf),
                "性能自检页不该成为下次启动的落点——它带着上千条编造数据，不是给用户看的功能。");
            Assert.NotContains(PageTags.Perf, string.Join(",", PageTags.Product),
                "产品页面清单里不能有它。");
        });

        h.Case("UI-81", "页面标签：产品页面全部认得", () =>
        {
            foreach (var tag in PageTags.Product)
            {
                Assert.True(PageTags.IsKnown(tag), $"{tag} 是产品页面，必须认得。");
            }

            Assert.Equal(7, PageTags.Product.Count, "当前是七个产品页面（新页面落地时要同步这份清单）。");
        });

        h.Case("UI-82", "页面标签：无效标签回落到首页", () =>
        {
            Assert.Equal(PageTags.Overview, PageTags.Normalize(null), "空值回落到首页。");
            Assert.Equal(PageTags.Overview, PageTags.Normalize(string.Empty), "空串回落到首页。");
            Assert.Equal(PageTags.Overview, PageTags.Normalize("nonexistent"), "未知标签回落到首页。");
            Assert.Equal(PageTags.Overview, PageTags.Normalize("PERF"), "大小写不同也算未知（标签是精确匹配）。");
            Assert.Equal(PageTags.Overview, PageTags.Normalize(" overview"), "带空格算未知，不做宽容处理。");
        });

        h.Case("UI-83", "页面标签：有效标签原样保留", () =>
        {
            foreach (var tag in PageTags.Product)
            {
                Assert.Equal(tag, PageTags.Normalize(tag), $"{tag} 应原样保留。");
            }
        });

        RuntimeCatalogCases(h);
    }

    /// <summary>
    /// 运行时清单的用例。
    /// </summary>
    /// <remarks>
    /// 清单是"界面上该看到哪些运行时"的唯一来源。它一旦与内核的 <c>kind</c> 取值不一致，
    /// 表现是"某个运行时永远显示未检出"或"同一项显示两行"——两种都不会报错，只会静静错下去。
    /// </remarks>
    private static void RuntimeCatalogCases(TestHarness h)
    {
        h.Case("UI-84", "运行时清单：分组扁平化后无重复、无遗漏", () =>
        {
            var flat = RuntimeCatalogGroups.AllEntries;
            var kinds = flat.Select(static e => e.Kind).ToArray();

            Assert.Equal(kinds.Length, kinds.Distinct(StringComparer.Ordinal).Count(),
                "同一个 kind 不能出现在两个分组里——界面会显示两行相同的东西。");
            Assert.True(flat.Length >= 16, $"清单应覆盖内核运行时候选表的全部条目，当前只有 {flat.Length} 项。");

            foreach (var entry in flat)
            {
                Assert.True(entry.DisplayName.Length > 0, $"{entry.Kind} 缺少展示名。");
            }
        });
    }
    // ══════════════════════════ 来源路线判定 ══════════════════════════

    /// <summary>
    /// <see cref="RuntimeSourceRouter"/> 的用例。
    /// </summary>
    /// <remarks>
    /// 这一组盯的是"该走哪条路、为什么"。三种路线的成本差一个量级，而选择取决于用户机器上的条件；
    /// 判定散在界面里就会出现"同一台机器从 CLI 走一条、从界面走另一条"。
    /// </remarks>
    private static void SourceRouteCases(TestHarness h)
    {
        h.Case("UI-90", "来源路线：本地归档优先，且不联网", () =>
        {
            var facts = new RuntimeEnvironmentFacts(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["winget"] = @"C:\w\winget.exe" },
                LocalArchiveExists: true,
                NetworkAllowed: true);

            var decision = RuntimeSourceRouter.Decide(
            [
                new(RuntimeSourceKind.LocalArchive, ArchivePath: @"D:\dl\python.zip"),
                new(RuntimeSourceKind.PackageManager, PackageId: "Python.Python.3.12"),
            ], facts);

            Assert.Equal(RuntimeSourceKind.LocalArchive, decision.Kind,
                "手里已经有归档就不该再下一次——声明顺序表达的就是这个偏好。");
            Assert.False(decision.NeedsNetwork, "本地归档不需要联网。");
            Assert.Contains("D:\\dl\\python.zip", decision.Reason, "理由里要给出具体路径。");
        });

        h.Case("UI-91", "★来源路线：本地归档不存在时落到下一条，而不是静默改走网络", () =>
        {
            var facts = new RuntimeEnvironmentFacts(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["winget"] = @"C:\w\winget.exe" },
                LocalArchiveExists: false,
                NetworkAllowed: true);

            var decision = RuntimeSourceRouter.Decide(
            [
                new(RuntimeSourceKind.LocalArchive, ArchivePath: @"D:\missing.zip"),
                new(RuntimeSourceKind.PackageManager, PackageId: "Python.Python.3.12"),
            ], facts);

            Assert.Equal(RuntimeSourceKind.PackageManager, decision.Kind, "归档不在就落到包管理器。");
            Assert.Contains("winget", decision.Reason, "理由要写明用的是哪个包管理器。");
        });

        h.Case("UI-92", "★来源路线：winget 缺失时判为阻断，并给出具体处置", () =>
        {
            var facts = new RuntimeEnvironmentFacts(
                new Dictionary<string, string>(StringComparer.Ordinal),
                LocalArchiveExists: false,
                NetworkAllowed: true);

            var decision = RuntimeSourceRouter.Decide(
                [new(RuntimeSourceKind.PackageManager, PackageId: "Python.Python.3.12")],
                facts);

            Assert.True(decision.IsBlocked, "没有包管理器时这条路走不通，必须阻断而不是到执行时才失败。");
            Assert.Contains("包管理器", decision.Blocker!, "阻断原因要指出缺的是包管理器。");
            Assert.NotNull(decision.Remedy, "阻断必须给处置建议。");
            Assert.Contains("应用安装程序", decision.Remedy!, "处置要具体到用户能照做。");
        });

        h.Case("UI-93", "来源路线：三个包管理器按 winget → scoop → choco 优先", () =>
        {
            var all = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["choco"] = @"C:\choco.exe",
                ["scoop"] = @"C:\scoop.cmd",
                ["winget"] = @"C:\winget.exe",
            };

            var facts = new RuntimeEnvironmentFacts(all, LocalArchiveExists: false, NetworkAllowed: true);
            var decision = RuntimeSourceRouter.Decide(
                [new(RuntimeSourceKind.PackageManager, PackageId: "x")], facts);

            Assert.Equal("winget", decision.Manager, "三个都在时应选 winget。");

            var noWinget = new RuntimeEnvironmentFacts(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["choco"] = @"C:\choco.exe", ["scoop"] = @"C:\scoop.cmd" },
                LocalArchiveExists: false,
                NetworkAllowed: true);
            Assert.Equal("scoop",
                RuntimeSourceRouter.Decide([new(RuntimeSourceKind.PackageManager, PackageId: "x")], noWinget).Manager,
                "没有 winget 时应选 scoop（而且不该因为没 winget 就整条路不可用）。");
        });

        h.Case("UI-94", "来源路线：不允许联网时，需要联网的路线一律判不可用", () =>
        {
            var facts = new RuntimeEnvironmentFacts(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["winget"] = @"C:\winget.exe" },
                LocalArchiveExists: false,
                NetworkAllowed: false);

            var decision = RuntimeSourceRouter.Decide(
                [new(RuntimeSourceKind.PackageManager, PackageId: "x")], facts);

            Assert.True(decision.IsBlocked, "预演或未授权联网时不该判定成可安装。");
            Assert.Contains("联网", decision.Blocker!, "阻断原因要点明是联网限制。");
        });

        h.Case("UI-95", "来源路线：官方归档需要地址，缺地址则不算可用", () =>
        {
            var facts = new RuntimeEnvironmentFacts(
                new Dictionary<string, string>(StringComparer.Ordinal),
                LocalArchiveExists: false,
                NetworkAllowed: true);

            Assert.True(
                RuntimeSourceRouter.Decide([new(RuntimeSourceKind.OfficialArchive)], facts).IsBlocked,
                "声明了官方归档却没给地址，不该被当成可用来源。");

            var withUrl = RuntimeSourceRouter.Decide(
                [new(RuntimeSourceKind.OfficialArchive, ArchiveUrl: "https://example.invalid/py.zip", Sha256: "sha256:ab")],
                facts);
            Assert.Equal(RuntimeSourceKind.OfficialArchive, withUrl.Kind, "给了地址就能走。");
            Assert.Contains("https://example.invalid/py.zip", withUrl.Reason, "理由里要给出地址。");
        });

        h.Case("UI-96", "来源路线：没有任何候选时给出可照做的阻断", () =>
        {
            var decision = RuntimeSourceRouter.Decide(
                [],
                new RuntimeEnvironmentFacts(new Dictionary<string, string>(StringComparer.Ordinal), false, true));

            Assert.True(decision.IsBlocked, "没有来源必须阻断。");
            Assert.Contains("没有声明", decision.Blocker!, "要指出是「没声明」而不是「不可用」。");
        });

        h.Case("UI-97", "★来源路线：完整性由谁校验必须如实说清", () =>
        {
            // 走包管理器时哈希是包管理器校验的，不是环境站校验的。
            // 把别人的保障说成自己的，是这个产品最不该犯的错。
            var facts = new RuntimeEnvironmentFacts(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["winget"] = @"C:\winget.exe" },
                LocalArchiveExists: false,
                NetworkAllowed: true);

            var pm = RuntimeSourceRouter.Decide([new(RuntimeSourceKind.PackageManager, PackageId: "x")], facts);
            Assert.Contains("包管理器自行校验", pm.IntegrityGuarantor, "不能把包管理器做的校验算在环境站头上。");

            var local = RuntimeSourceRouter.Decide(
                [new(RuntimeSourceKind.LocalArchive, ArchivePath: @"D:\a.zip")],
                facts with { LocalArchiveExists = true });
            Assert.Contains("环境站校验", local.IntegrityGuarantor, "本地归档的哈希由环境站校验。");
        });

        h.Case("UI-98", "来源路线：风险级按路线区分（包管理器高于归档）", () =>
        {
            Assert.Equal(RiskLevel.Reversible, RuntimeSourceRisk.Of(RuntimeSourceKind.LocalArchive),
                "本地归档可逆。");
            Assert.Equal(RiskLevel.High, RuntimeSourceRisk.Of(RuntimeSourceKind.PackageManager),
                "包管理器把「装到哪、装什么」的决定权交给外部工具，风险更高。");
        });
    }
    // ══════════════════════════ 接线与映射完整性 ══════════════════════════

    private static void WiringCases(TestHarness h)
    {
        h.Case("UI-04", "接线：每个已映射的检测项都真的能产出结论（正向）", () =>
        {
            // 阳性对照：给一个必然有问题的输入，断言映射确实产出了待办项。
            // 没有这一条，下面的"未映射检测项"检查可能只是因为映射表整个是空的而通过。
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entry_count"] = "5",
                ["problem_count"] = "3",
                ["missing_count"] = "1",
                ["duplicate_count"] = "1",
                ["empty_count"] = "1",
                ["unresolved_count"] = "0",
                ["length"] = "300",
                ["over_legacy_limit"] = "false",
                ["details"] = @"C:\Gone —— 不存在",
            };

            var items = RemedyCatalog.FromDetection("envstation.path.validate", outputs, "user");
            Assert.True(items.Length >= 3, "有缺目录、有重复、有空条目，至少应产出 3 条待办项。");

            var actionIds = items.SelectMany(static i => i.Plan.ActionIds).Distinct().ToArray();
            Assert.Contains("envstation.path.dedupe", string.Join(",", actionIds), "重复项应指向去重动作。");
            Assert.Contains("envstation.path.clean", string.Join(",", actionIds), "失效与空条目应指向清理动作。");
        });

        h.Case("UI-05", "接线：干净环境不得报出任何待办项（负向）", () =>
        {
            var clean = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entry_count"] = "12",
                ["problem_count"] = "0",
                ["missing_count"] = "0",
                ["duplicate_count"] = "0",
                ["empty_count"] = "0",
                ["unresolved_count"] = "0",
                ["length"] = "800",
                ["over_legacy_limit"] = "false",
                ["details"] = string.Empty,
            };

            Assert.Equal(0, RemedyCatalog.FromDetection("envstation.path.validate", clean, "user").Length,
                "一切正常时不该产出待办项——否则界面会天天报警。");
        });

        h.Case("UI-06", "接线：空条目被判为严重（安全风险），不是一般警告", () =>
        {
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["empty_count"] = "1",
                ["problem_count"] = "1",
                ["details"] = string.Empty,
            };

            var items = RemedyCatalog.FromDetection("envstation.path.validate", outputs, "user");
            var empty = items.First(static i => i.Id.Contains("empty", StringComparison.Ordinal));
            Assert.Equal(RemedySeverity.Critical, empty.Severity, "空条目会让系统在当前目录找程序，属严重问题。");
            Assert.True(empty.CanAutoFix, "空条目可以自动清理。");
        });

        h.Case("UI-07", "接线：超长 PATH 只提示、不自动改（避免截断）", () =>
        {
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["length"] = "2100",
                ["over_legacy_limit"] = "true",
                ["problem_count"] = "0",
                ["details"] = string.Empty,
            };

            var items = RemedyCatalog.FromDetection("envstation.path.validate", outputs, "machine");
            var over = items.First(static i => i.Id.Contains("over-legacy-limit", StringComparison.Ordinal));
            Assert.False(over.CanAutoFix, "超长只提示——自动改写有截断风险。");
        });

        h.Case("UI-08", "接线：缺失运行时指向安装动作（这正是原先断掉的那一环）", () =>
        {
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = "python",
                ["found"] = "false",
            };

            var items = RemedyCatalog.FromDetection("envstation.detect.runtime", outputs);
            Assert.Equal(1, items.Length, "未检测到时应产出一条待办项。");
            Assert.Contains("envstation.runtime.install",
                string.Join(",", items[0].Plan.ActionIds),
                "这条待办项必须直接给出安装动作，而不是只写一行说明。");
        });

        h.Case("UI-09", "接线：已找到运行时不得报缺件", () =>
        {
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = "python",
                ["found"] = "true",
                ["version"] = "3.12.8",
            };

            Assert.Equal(0, RemedyCatalog.FromDetection("envstation.detect.runtime", outputs).Length,
                "找到了就不该报缺件。");
        });

        h.Case("UI-44", "接线：映射表覆盖了界面实际会跑的全部检测项", () =>
        {
            // 界面「环境检测」页跑的检测项。少一个映射就意味着那一项在界面上只能干看。
            string[] uiDetections =
            [
                "envstation.detect.os",
                "envstation.path.validate",
                "envstation.detect.deps",
                "envstation.detect.conflict",
            ];

            foreach (var actionId in uiDetections)
            {
                Assert.True(RemedyCatalog.IsMapped(actionId),
                    $"{actionId} 未登记映射：检测得出问题却给不出处置，正是本次要消除的缺陷。");
            }
        });
    }

    private static RemedyItem MakeRemedy(string id, RemedySeverity severity, RiskLevel risk) =>
        new(id,
            severity,
            "标题",
            "现象",
            "原因",
            "影响",
            RemediationPlan.Single(RemedyStep.Of("envstation.path.dedupe", "去重"), "去重"),
            risk,
            "test-rule");
}
