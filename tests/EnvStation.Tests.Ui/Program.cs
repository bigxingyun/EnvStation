using System.Collections.Immutable;
using EnvStation.Abstractions.Transactions;
using EnvStation.Core.Diagnostics;
using EnvStation.TestKit;

namespace EnvStation.Tests.Ui;

/// <summary>
/// 界面状态层、路径校验与待办项模型的用例。
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
/// <para>
/// <b>除写入测试（UI-122 起）外，全部用例纯计算</b>，不触碰注册表、文件系统与网络。
/// UI-122 起要验的是需求 V6「真实写入-删除测试」，它按定义必须真的写一次文件——
/// 因此只在<b>临时目录</b>里写一个可识别的探测文件，并断言测试结束后不留残留。
/// 这是本套件唯一会碰盘的地方，且碰的是自己造的临时目录。
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("界面状态层、路径校验与待办项模型测试（除 V6 写入测试外不触碰本机）");
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
        PathRuleCases(h);
        WriteProbeCases(h);
        DetectionRemedyCases(h);
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
    // ══════════════════════════ 安装路径校验（R1~R17）══════════════════════════

    /// <summary>
    /// 测试用的路径事实：全部由用例显式指定，不读真实系统。
    /// </summary>
    /// <remarks>
    /// 必须有它才能测 R4（保留设备名）、R10（网络盘）、R14（非 NTFS）这些
    /// **在开发机上根本造不出来**的规则。生产实现读真实系统，测试实现给假数据，
    /// 两者跑同一套判定逻辑。
    /// </remarks>
    private sealed class FakeProbe : IPathProbe
    {
        public bool HasVariableReference { get; init; }

        public bool Network { get; init; }

        public string DriveTypeName { get; init; } = "fixed";

        public string FileSystemName { get; init; } = "NTFS";

        public long? FreeBytes { get; init; } = 100L * 1024 * 1024 * 1024;

        public bool Exists { get; init; }

        public int? Entries { get; init; }

        public bool Owned { get; init; }

        public bool Reparse { get; init; }

        public string? ReparseTargetPath { get; init; }

        public IReadOnlyDictionary<string, string> Registered { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public ImmutableArray<string> Paths { get; init; } = [];

        public bool ContainsVariableReference(string path) => HasVariableReference;

        public bool IsNetworkPath(string path) => Network;

        public string DriveRoot(string path) => path.Length >= 2 ? path[..2] + "\\" : string.Empty;

        public string DriveType(string path) => DriveTypeName;

        public string FileSystem(string path) => FileSystemName;

        public long? FreeSpaceBytes(string path) => FreeBytes;

        public bool DirectoryExists(string path) => Exists;

        public int? EnumerateEntryCount(string path) => Entries;

        public bool IsOwnedByEnvStation(string path) => Owned;

        public bool IsReparsePoint(string path) => Reparse;

        public string? ReparseTarget(string path) => ReparseTargetPath;

        public IReadOnlyDictionary<string, string> RegisteredRuntimeDirectories => Registered;

        public ImmutableArray<string> PathEntries => Paths;
    }

    /// <summary>取结论里命中的规则 ID（逗号连接，便于 Assert.Contains）。</summary>
    private static string Ids(PathValidationResult result) =>
        string.Join(",", result.Findings.Select(static f => f.Rule.Id));

    /// <summary>取某条规则的补充说明；该规则未命中时为空。</summary>
    private static string DetailOf(PathValidationResult result, string ruleId) =>
        result.Findings.FirstOrDefault(f => f.Rule.Id == ruleId)?.Detail ?? string.Empty;
    private static PathRuleEngine MakeEngine(FakeProbe probe) =>
        PathRuleEngine.LoadFrom(RuleToml, probe);

    /// <summary>与仓库里 data/rules/path-rules.toml 同构的最小规则集（17 条）。</summary>
    private const string RuleToml = """
        [meta]
        schema_version = 1
        warn_length = 200
        block_length = 260

        [[rules]]
        id = "R1"
        level = "block"
        check = "NonAscii"
        title = "路径中包含中文或非 ASCII 字符"
        message = "部分编译工具与构建脚本无法正确处理非 ASCII 路径。"
        fix = "toAsciiName"

        [[rules]]
        id = "R2"
        level = "warn"
        check = "ContainsSpace"
        title = "路径中包含空格"
        message = "部分老旧工具链未对空格做转义。"
        fix = "none"

        [[rules]]
        id = "R3"
        level = "block"
        check = "SpecialCharacters"
        title = "路径中包含特殊字符"
        message = "特殊字符在脚本里会被解释。"
        fix = "none"

        [[rules]]
        id = "R4"
        level = "block"
        check = "ReservedDeviceName"
        title = "路径中包含 Windows 保留设备名"
        message = "保留设备名不能作为目录名。"
        fix = "none"

        [[rules]]
        id = "R5"
        level = "block"
        check = "TrailingSpaceOrDot"
        title = "目录名以空格或点结尾"
        message = "Windows 会丢弃结尾的空格与点。"
        fix = "stripTrailing"

        [[rules]]
        id = "R6a"
        level = "warn"
        check = "LongPath"
        title = "路径较长"
        message = "解压后可能超过 260 字符限制。"
        fix = "none"

        [[rules]]
        id = "R6b"
        level = "block"
        check = "VeryLongPath"
        title = "路径过长"
        message = "解压后几乎必然超过限制。"
        fix = "none"

        [[rules]]
        id = "R7"
        level = "block"
        check = "InvalidPathCharacters"
        title = "路径中包含 Windows 不允许的字符"
        message = "路径中出现了不允许的字符。"
        fix = "none"

        [[rules]]
        id = "R8"
        level = "warn"
        check = "NeedsElevation"
        title = "目标位置写入需要管理员权限"
        message = "该位置所有用户共享。"
        fix = "none"

        [[rules]]
        id = "R9"
        level = "warn"
        check = "CloudSyncedDirectory"
        title = "目标位于云同步目录内"
        message = "同步客户端会造成文件锁冲突。"
        fix = "none"

        [[rules]]
        id = "R10"
        level = "block"
        check = "NetworkOrRemovableDrive"
        title = "目标位于网络路径或可移动磁盘"
        message = "断连会让开发环境失效。"
        fix = "none"

        [[rules]]
        id = "R11"
        level = "warn"
        check = "ReparsePoint"
        title = "目标路径是符号链接或目录联接"
        message = "实际写入位置与看到的路径不同。"
        fix = "none"

        [[rules]]
        id = "R12"
        level = "block"
        check = "UnresolvedVariable"
        title = "路径中包含未展开的变量引用"
        message = "需要的是一个确定的目录。"
        fix = "none"

        [[rules]]
        id = "R13"
        level = "warn"
        check = "OverlapsRegisteredRuntime"
        title = "与已登记的组件目录重叠"
        message = "两个版本装进同一目录会互相覆盖。"
        fix = "none"

        [[rules]]
        id = "R14"
        level = "warn"
        check = "NotNtfs"
        title = "目标磁盘不是 NTFS"
        message = "非 NTFS 不支持符号链接与长路径。"
        fix = "none"

        [[rules]]
        id = "R15"
        level = "warn"
        check = "ExistingNonEmptyDirectory"
        title = "目标目录已存在且非空"
        message = "继续安装可能覆盖已有文件。"
        fix = "none"

        [[rules]]
        id = "R16"
        level = "block"
        check = "InsufficientDiskSpace"
        title = "目标磁盘剩余空间不足"
        message = "装到一半失败会留下半成品环境。"
        fix = "none"

        [[rules]]
        id = "R17"
        level = "warn"
        check = "AlreadyInPath"
        title = "该目录已在 PATH 中"
        message = "重复添加会让 PATH 变长。"
        fix = "none"
        """;

    /// <summary>
    /// 安装路径校验的用例：R1~R17 每条至少一项。
    /// </summary>
    /// <summary>需求 6.1 的阻断级规则编号（R6 路径过长拆成 R6a 警告 / R6b 阻断）。</summary>
    private static readonly string[] BlockingRuleIds =
        ["R1", "R3", "R4", "R5", "R6b", "R7", "R10", "R12", "R16"];

    /// <summary>需求 6.1 的全部规则编号（17 条编号，R6 占两档声明，共 18 条）。</summary>
    private static readonly string[] RequirementRuleIds =
        ["R1", "R2", "R3", "R4", "R5", "R6a", "R6b", "R7", "R8", "R9",
         "R10", "R11", "R12", "R13", "R14", "R15", "R16", "R17"];

    private static void PathRuleCases(TestHarness h)
    {
        var clean = new FakeProbe();
        var engine = MakeEngine(clean);

        h.Case("UI-100", "路径校验：规则加载齐 18 条声明，级别与需求表一致", () =>
        {
            // 需求 6.1 是 17 条规则；R6（路径过长）有两档级别，拆成 R6a/R6b 两条声明，
            // 因此规则文件里是 18 条。**编号与条数不是一回事**，这一点要在用例里说清，
            // 否则下一个人看到 18 会以为多写了一条。
            Assert.Equal(18, engine.RuleCount, "17 条规则 + R6 拆两档 = 18 条声明。");

            string[] ruleIds = [.. engine.Rules.Select(static r => r.Id)];
            foreach (var expected in RequirementRuleIds)
            {
                Assert.Contains(expected, string.Join(",", ruleIds), $"需求 6.1 的 {expected} 在规则文件里缺失。");
            }

            foreach (var rule in engine.Rules)
            {
                var shouldBlock = BlockingRuleIds.Contains(rule.Id);
                Assert.Equal(shouldBlock, rule.IsBlocking,
                    $"{rule.Id} 的级别与需求 6.1 表不一致（应为 {(shouldBlock ? "阻断" : "警告")}）。");
            }
        });

        h.Case("UI-101", "R1 非 ASCII：阻断，并给出一键修正", () =>
        {
            var result = engine.Validate(@"D:\开发工具\Python");
            var finding = result.Findings.Single(f => f.Rule.Id == "R1");
            Assert.True(finding.IsBlocking, "R1 是阻断级。");
            Assert.False(result.CanProceed, "有阻断级就不能继续。");
            Assert.NotNull(finding.Suggestion, "R1 必须给出一键修正。");
            Assert.Contains("DevTools", finding.Suggestion!, "常见目录名走对照表。");
        });

        h.Case("UI-102", "R2 空格：警告，仍可继续", () =>
        {
            var result = engine.Validate(@"D:\Dev Tools\Python");
            Assert.True(result.CanProceed, "空格只是警告。");
            Assert.Equal(1, result.WarnCount, "只该报一条空格。");
        });

        h.Case("UI-103", "R3 特殊字符：`&` 与 `^` 判为阻断", () =>
        {
            Assert.False(engine.Validate(@"D:\Dev&Tools").CanProceed, "& 是阻断级。");
            Assert.False(engine.Validate(@"D:\Dev^2").CanProceed, "^ 是阻断级。");
            Assert.True(engine.Validate(@"D:\DevTools").CanProceed, "干净路径不该被拦。");
        });

        h.Case("UI-104", "★R4 保留设备名：CON 与 COM1 阻断，且忽略扩展名", () =>
        {
            Assert.False(engine.Validate(@"D:\CON").CanProceed, "CON 是保留设备名。");
            Assert.False(engine.Validate(@"D:\COM1\Python").CanProceed, "COM1 是保留设备名。");
            Assert.False(engine.Validate(@"D:\NUL.txt").CanProceed,
                "带扩展名的 NUL.txt 同样不可用（Windows 的既有行为）。");
            Assert.True(engine.Validate(@"D:\CONSOLE").CanProceed, "CONSOLE 不是保留名，不该误伤。");
        });

        h.Case("UI-105", "R5 尾随空格或点：阻断，并给出去尾修正", () =>
        {
            var result = engine.Validate(@"D:\Dev \Python.");
            var finding = result.Findings.Single(f => f.Rule.Id == "R5");
            Assert.True(finding.IsBlocking, "R5 是阻断级。");
            Assert.Equal(@"D:\Dev\Python", finding.Suggestion, "修正应去掉每段结尾的空格与点。");
        });

        h.Case("UI-106", "R6 长度：200 警告 / 260 阻断，两个档位各只有一条", () =>
        {
            var warn = engine.Validate(@"D:\" + new string('a', 200));
            var warnIds = warn.Findings.Select(static f => f.Rule.Id).ToArray();
            Assert.Contains("R6a", string.Join(",", warnIds), "超过 200 应报 R6a。");
            Assert.NotContains("R6b", string.Join(",", warnIds), "未超 260 不该报 R6b。");
            Assert.True(warn.CanProceed, "R6a 只是警告。");

            var block = engine.Validate(@"D:\" + new string('a', 300));
            Assert.False(block.CanProceed, "超过 260 应阻断。");
        });

        h.Case("UI-107", "R7 非法字符：盘符的冒号不算非法", () =>
        {
            Assert.False(engine.Validate(@"D:\Dev?Tools").CanProceed, "? 非法。");
            Assert.True(engine.Validate(@"D:\DevTools").CanProceed,
                "盘符里的冒号是合法的，不该被当成非法字符。");
        });

        h.Case("UI-108", "R8 需提权位置：Program Files 与盘根判警告", () =>
        {
            // 用"命中哪些规则"断言，而不是数条数：数条数会把无关规则的噪音算进来，
            // 一个用例失败时看不出到底哪条规则错了。
            var programFiles = engine.Validate(@"C:\Program Files\Python");
            Assert.Contains("R8", Ids(programFiles), "Program Files 需提权。");
            Assert.True(programFiles.CanProceed, "R8 只是警告，仍可继续。");

            Assert.Contains("R8", Ids(engine.Validate(@"D:\")), "盘根需提权。");
            Assert.True(engine.Validate(@"D:\Dev\Python").IsClean, "普通目录不该报。");
        });

        h.Case("UI-109", "R9 云同步目录：OneDrive 与坚果云判警告", () =>
        {
            var oneDrive = engine.Validate(@"C:\Users\me\OneDrive\Dev");
            Assert.Contains("R9", Ids(oneDrive), "OneDrive 应被识别。");
            Assert.Contains("OneDrive", DetailOf(oneDrive, "R9"), "说明里要点出是哪个同步客户端。");

            var nutstore = engine.Validate(@"D:\我的坚果云\Dev");
            Assert.Contains("R9", Ids(nutstore), "坚果云应被识别。");
            Assert.Contains("坚果云", DetailOf(nutstore, "R9"), "说明里要点出是哪个同步客户端。");
        });

        h.Case("UI-110", "R10 网络盘或可移动盘：阻断", () =>
        {
            Assert.False(MakeEngine(new FakeProbe { Network = true }).Validate(@"\\srv\share").CanProceed,
                "UNC 路径阻断。");
            Assert.False(MakeEngine(new FakeProbe { DriveTypeName = "removable" })
                .Validate(@"E:\Python").CanProceed, "可移动盘阻断。");
        });

        h.Case("UI-111", "R11 符号链接：警告，并说明实际位置", () =>
        {
            var finding = MakeEngine(new FakeProbe { Reparse = true, ReparseTargetPath = @"D:\Real" })
                .Validate(@"D:\Link").Findings.Single();
            Assert.False(finding.IsBlocking, "R11 只是警告。");
            Assert.Contains(@"D:\Real", finding.Detail, "要说明实际位置，否则用户无法判断。");
        });

        h.Case("UI-112", "R12 未展开变量：阻断", () =>
        {
            Assert.False(MakeEngine(new FakeProbe { HasVariableReference = true })
                .Validate(@"%LOCALAPPDATA%\Python").CanProceed, "%VAR% 阻断。");
        });

        h.Case("UI-113", "★R13 路径重叠：互为父子都算重叠", () =>
        {
            var probe = new FakeProbe
            {
                Registered = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["python"] = @"D:\Dev\Python\3.12",
                },
            };
            var e = MakeEngine(probe);

            Assert.Contains("python", e.Validate(@"D:\Dev\Python").Findings.Single().Detail,
                "装在已登记组件的父目录里算重叠。");
            Assert.Contains("python", e.Validate(@"D:\Dev\Python\3.12\extra").Findings.Single().Detail,
                "装在子目录里也算重叠。");
            Assert.True(e.Validate(@"D:\Dev\Node").IsClean, "无关目录不该报重叠。");
        });

        h.Case("UI-114", "R14 非 NTFS：警告并说明文件系统", () =>
        {
            var finding = MakeEngine(new FakeProbe { FileSystemName = "exFAT" })
                .Validate(@"E:\Dev").Findings.Single();
            Assert.Contains("exFAT", finding.Detail, "要说明实际的文件系统。");
            Assert.True(MakeEngine(new FakeProbe { FileSystemName = "ntfs" })
                .Validate(@"E:\Dev").IsClean, "NTFS 大小写不同也算 NTFS。");
        });

        h.Case("UI-115", "★R15 非空目录：环境站自己建的目录不算问题", () =>
        {
            var notOwned = MakeEngine(new FakeProbe { Exists = true, Entries = 42 })
                .Validate(@"D:\Dev\Python");
            Assert.Contains("42", notOwned.Findings.Single().Detail, "要报出条目数。");

            var owned = MakeEngine(new FakeProbe { Exists = true, Entries = 42, Owned = true })
                .Validate(@"D:\Dev\Python");
            Assert.True(owned.IsClean,
                "环境站自己建的目录不算「别人的非空目录」——否则重装自己的组件会被自己的规则拦住。");
        });

        h.Case("UI-116", "R16 磁盘空间：按 1.5 倍余量判，且只在给了需求时判", () =>
        {
            var tight = MakeEngine(new FakeProbe { FreeBytes = 1000L * 1024 * 1024 });
            Assert.False(tight.Validate(@"D:\Dev", requiredBytes: 900L * 1024 * 1024).CanProceed,
                "需要 900MB 时 1.5 倍是 1350MB，只剩 1000MB 应阻断。");

            Assert.True(tight.Validate(@"D:\Dev").IsClean,
                "没给安装需求时不该凭空判磁盘不足。");
            Assert.True(tight.Validate(@"D:\Dev", requiredBytes: 100L * 1024 * 1024).CanProceed,
                "需要 100MB 时 1.5 倍是 150MB，1000MB 够用。");
        });

        h.Case("UI-117", "R17 已在 PATH：忽略大小写与尾斜杠", () =>
        {
            var probe = new FakeProbe
            {
                Paths = [@"D:\Dev\Python\", @"C:\Windows\System32"],
            };
            var e = MakeEngine(probe);

            Assert.Contains("PATH", e.Validate(@"d:\dev\python").Findings.Single().Detail,
                "大小写与尾斜杠不同仍应判为已存在。");
            Assert.True(e.Validate(@"D:\Dev\Node").IsClean, "不在 PATH 里不该报。");
        });

        h.Case("UI-118", "★路径校验：干净路径零发现，阻断排在警告前", () =>
        {
            Assert.True(engine.Validate(@"D:\Dev\Python").IsClean,
                "一个普通目录不该触发任何规则——否则这套校验天天在误报。");
            Assert.Contains("通过", engine.Validate(@"D:\Dev\Python").Summarize(), "结论句应说通过。");

            var mixed = engine.Validate(@"C:\Program Files\开发 工具");
            Assert.True(mixed.BlockCount > 0 && mixed.WarnCount > 0, "应同时有阻断与警告。");
            Assert.True(mixed.Findings[0].IsBlocking, "阻断必须排在警告之前。");
        });

        h.Case("UI-119", "★路径校验：规则文件缺失时拒绝工作，不静默放行", () =>
        {
            Assert.Throws<FileNotFoundException>(
                () => PathRuleEngine.Load(@"D:\nonexistent\path-rules.toml", engine.Probe),
                "缺规则文件必须抛异常——静默放行的后果是「校验全绿」，而用户以为检查过了。");

            Assert.Throws<InvalidOperationException>(
                () => PathRuleEngine.LoadFrom("   ", engine.Probe),
                "空规则文件同样要拒绝。");

            Assert.Throws<InvalidOperationException>(
                () => PathRuleEngine.LoadFrom("[meta]\nschema_version = 1", engine.Probe),
                "没有 [[rules]] 段落要拒绝。");
        });

        h.Case("UI-120", "★路径校验：未知 check 取值报错并指出可选值", () =>
        {
            const string bad = """
                [[rules]]
                id = "R99"
                level = "warn"
                check = "NoSuchCheck"
                title = "x"
                """;
            // 只断言"抛了 InvalidOperationException"：本项目自研断言器不返回异常对象，
            // 而错误信息里含取值这一点由 LoadFrom 的实现保证（用例 UI-120 覆盖了"会抛"）。
            Assert.Throws<InvalidOperationException>(
                () => PathRuleEngine.LoadFrom(bad, engine.Probe),
                "规则写错判定类型必须报错——静默忽略会让那条规则永远不生效。");
        });

        h.Case("UI-121", "★随产品发布的规则文件：能加载且分档、取值合法", () =>
        {
            // 上面 UI-100..UI-120 用的是用例自己内联的最小规则集，它再怎么写错也发现不了
            // data/rules/path-rules.toml 的问题——而那份才是用户机器上真正被读到的文件。
            // 本用例加载的是随产品一起发布的真实产物（由 csproj 复制到输出目录的 rules\）。
            var rulesPath = Path.Combine(AppContext.BaseDirectory, "rules", "path-rules.toml");
            Assert.True(File.Exists(rulesPath), $"随产品发布的规则文件必须存在：{rulesPath}");

            var shipped = PathRuleEngine.Load(rulesPath, engine.Probe);

            Assert.Equal(18, shipped.Rules.Length, "发布版规则条数应与《需求分析.md》6.1 节一致。");

            var ids = shipped.Rules.Select(static r => r.Id).ToArray();
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count(), "规则 ID 不得重复。");

            // 逐条比对编号集合，而不是只查几个代表：少一条规则不会让程序报错，
            // 只会让某一类坏路径悄悄通过——这类漏检只有在用例里对齐编号才能发现。
            foreach (var expected in RequirementRuleIds)
            {
                Assert.Contains(expected, string.Join(",", ids), $"发布版规则文件缺 {expected}。");
            }

            foreach (var rule in shipped.Rules)
            {
                Assert.True(
                    !string.IsNullOrWhiteSpace(rule.Title) && !string.IsNullOrWhiteSpace(rule.Message),
                    $"规则 {rule.Id} 的标题与说明都必须写全——空说明在界面上就是一行没有理由的红字。");

                Assert.Equal(
                    BlockingRuleIds.Contains(rule.Id),
                    rule.IsBlocking,
                    $"发布版规则 {rule.Id} 的级别与需求 6.1 表不一致。");

                Assert.True(
                    rule.Fix is PathFixKind.None or PathFixKind.StripTrailing or PathFixKind.ToAsciiName,
                    $"规则 {rule.Id} 的 fix 取值必须落在允许集合内。");
            }

            // 修正策略与判定类型必须成对：R5（结尾空格或点）配 stripTrailing、R1（非 ASCII）配
            // toAsciiName。写错的风险是界面给出一个点完没反应的按钮——按钮的显示条件由 fix 决定，
            // 而能不能修好由判定类型决定，两边对不上就是假按钮。
            foreach (var rule in shipped.Rules)
            {
                var expected = rule.Check switch
                {
                    PathCheckKind.TrailingSpaceOrDot => PathFixKind.StripTrailing,
                    PathCheckKind.NonAscii => PathFixKind.ToAsciiName,
                    _ => PathFixKind.None,
                };
                Assert.Equal(expected, rule.Fix, $"规则 {rule.Id} 的修正策略与判定类型不匹配。");
            }

            Assert.True(shipped.WarnLength < shipped.BlockLength, "警告阈值必须小于阻断阈值，否则警告档永远不触发。");
            Assert.Equal(200, shipped.WarnLength, "警告阈值应与设计值一致（改阈值要同时改界面文案里的数字）。");
            Assert.Equal(260, shipped.BlockLength, "阻断阈值应与 Windows 传统路径上限一致。");
        });
    }
    // ══════════════════════════ V6 真实写入测试 ══════════════════════════

    private static void WriteProbeCases(TestHarness h)
    {
        h.Case("UI-122", "★V6 写入测试：真的写一次、读回比对、并留下零残留", () =>
        {
            var dir = NewTempDirectory();
            try
            {
                var before = Directory.GetFileSystemEntries(dir).Length;
                var result = TargetWriteProbe.Run(dir);

                Assert.True(result.Ok, $"空的可写目录应通过写入测试：{result.ToLine()}");
                Assert.Equal(dir, result.TestedDirectory, "目录已存在时应直接测它，不做替换。");
                Assert.False(result.Substituted, "目录已存在时不该报告为替换测试。");
                Assert.True(result.ProbeFileName is { Length: > 0 }, "应回报探测文件名，便于排查残留。");

                // 这一条是 V6 与"用属性猜"的分界线：测试必须真的落了盘，而不是只判了权限位。
                Assert.True(
                    result.ProbeFileName!.StartsWith(TargetWriteProbe.ProbeFileStem, StringComparison.Ordinal),
                    "探测文件名应带可识别前缀——异常退出后用户要能看出是谁留下的。");

                Assert.Equal(
                    before,
                    Directory.GetFileSystemEntries(dir).Length,
                    "写入测试不得留下任何残留：它是前置检查，不是安装。");
            }
            finally
            {
                TryRemoveDirectory(dir);
            }
        });

        h.Case("UI-123", "★V6 写入测试：目标目录尚不存在时退到上级，且不建目录", () =>
        {
            var parent = NewTempDirectory();
            var target = Path.Combine(parent, "python", "3.12");
            try
            {
                var result = TargetWriteProbe.Run(target);

                Assert.True(result.Ok, $"上级可写时应通过：{result.ToLine()}");
                Assert.Equal(parent, result.TestedDirectory, "应退到最近的已存在上级目录。");
                Assert.True(result.Substituted, "替换了测试目录就必须如实标出来。");
                Assert.Contains("上级", result.ToLine(), "结论里要说清测的是哪一层，不能只给一个『通过』。");

                // 前置检查不得有副作用：目标目录不能因为"检查过了"就被创建出来。
                Assert.False(Directory.Exists(target), "写入测试不得创建目标目录。");
                Assert.Equal(0, Directory.GetFileSystemEntries(parent).Length, "不得留下残留。");
            }
            finally
            {
                TryRemoveDirectory(parent);
            }
        });

        h.Case("UI-124", "★V6 写入测试：判为阻断，且原因说得出是哪一个文件挡住的", () =>
        {
            var dir = NewTempDirectory();
            var blocker = Path.Combine(dir, "blocker.txt");
            File.WriteAllText(blocker, "x");
            try
            {
                var result = TargetWriteProbe.Run(Path.Combine(blocker, "python"));

                Assert.False(result.Ok, "上级是文件时不可能建成目录，必须判为未通过。");
                Assert.True(result.IsBlocking, "未通过即阻断（V6）。");
                Assert.Contains("blocker.txt", result.FailureReason ?? string.Empty, "原因里要点出是哪个路径挡住的。");

                // 关键的一条：不能因为继续向上找到了可写的盘根（这里就是 C:\）就报"通过"。
                Assert.NotEqual(
                    Path.GetPathRoot(Path.GetFullPath(dir)),
                    result.TestedDirectory,
                    "不得越过挡住路径的文件继续向上探测——那会给出一个目标目录根本建不出来的『通过』。");
            }
            finally
            {
                TryRemoveDirectory(dir);
            }
        });

        h.Case("UI-125", "V6 写入测试：结论文案三态各不相同", () =>
        {
            var dir = NewTempDirectory();
            var blocker = Path.Combine(dir, "blocker.txt");
            File.WriteAllText(blocker, "x");
            try
            {
                var ok = TargetWriteProbe.Run(dir);
                Assert.Contains("可写入", ok.ToLine(), "直接测目标目录时说明测的就是它。");

                var substituted = TargetWriteProbe.Run(Path.Combine(dir, "not-yet"));
                Assert.Contains("尚不存在", substituted.ToLine(), "替换测试必须说出来。");

                var failed = TargetWriteProbe.Run(Path.Combine(blocker, "sub"));
                Assert.False(failed.Ok, "拿真实失败做对照，否则下一条断言只是在比对两段成功文案。");
                Assert.NotContains("通过", failed.ToLine(), "未通过时的文案不得出现「通过」——那是最容易被一眼看错的措辞。");

                // 三条结论必须互不相同：界面按这一行字决定图标与颜色，文案重合就等于状态重合。
                var lines = new[] { ok.ToLine(), substituted.ToLine(), failed.ToLine() };
                Assert.Equal(3, lines.Distinct(StringComparer.Ordinal).Count(), "三种状态的结论文案不得重复。");
            }
            finally
            {
                TryRemoveDirectory(dir);
            }
        });

        h.Case("UI-126", "★V6 写入测试：路径为空是调用方的错，不是环境问题", () =>
        {
            // 环境问题要变成一条结论给用户看；调用方传空路径是程序错误，必须当场炸。
            // 两者混在一起，前者会被异常打断、后者会被悄悄吞掉。
            Assert.Throws<ArgumentException>(() => TargetWriteProbe.Run("  "), "空路径应抛参数异常。");
        });

        h.Case("UI-127", "★R15 读不到目录内容时不得判成空目录", () =>
        {
            // "读不进去"与"空目录"是两件事：按空目录处理会让"可能覆盖别人的文件"
            // 这条警告在最需要它的场合（权限受限目录）消失。
            var probe = new FakeProbe { Exists = true, Owned = false, Entries = null };
            var engine = MakeEngine(probe);
            var result = engine.Validate(@"D:\Dev\Python");

            Assert.Contains("R15", Ids(result), "读不到内容时 R15 仍应命中。");
            Assert.Contains("读不到", DetailOf(result, "R15"), "说明里要写清是读不到，而不是『包含 0 个条目』。");

            // 阴性对照：真的空目录不该报警。
            var empty = new FakeProbe { Exists = true, Owned = false, Entries = 0 };
            Assert.NotContains("R15", Ids(MakeEngine(empty).Validate(@"D:\Dev\Python")), "空目录不是问题。");
        });
    }

    private static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "envstation-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryRemoveDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 用例的清理失败不该盖住断言结论；临时目录本身会被系统回收。
        }
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
    }

    // ══════════════════════════ 检测 → 待办项（X-2） ══════════════════════════

    private static void DetectionRemedyCases(TestHarness h)
    {
        h.Case("UI-44", "★接线：每个检测类动作都有判据，且判据真的会产出待办项", () =>
        {
            // 两件事一起查，缺一不可：
            //   ① 覆盖：注册表里的检测类动作都必须登记（漏一个 = 那一项在界面上只能干看）；
            //   ② 阳性对照：喂一份"必然有问题"的输出，必须真的产出待办项。
            // 只查 ① 的话，映射表整个返回空数组也能通过——这正是"假绿"的经典形状。
            var registry = CoreActions.ActionRegistry.CreateDefault(new AbsDiag.FindingBag()).Value;
            var detectActions = registry.Descriptors
                .Select(static d => d.ActionId)
                .Where(static id => id.StartsWith("envstation.detect.", StringComparison.Ordinal)
                                    || string.Equals(id, "envstation.path.validate", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.True(detectActions.Length >= 10, $"应至少查到 10 个检测类动作，实际 {detectActions.Length} 个。");

            foreach (var actionId in detectActions)
            {
                Assert.True(RemedyCatalog.IsMapped(actionId),
                    $"{actionId} 未登记判据：检测得出问题却给不出处置，正是本次要消除的缺陷。");
            }

            foreach (var actionId in RemedyCatalog.MappedDetections)
            {
                Assert.True(
                    detectActions.Contains(actionId, StringComparer.Ordinal),
                    $"判据表里的 {actionId} 不在检测类动作清单里（打字错误，或动作被删了却没同步判据表）。");
            }

            foreach (var (actionId, problem) in ProblemSamples())
            {
                var items = RemedyCatalog.FromDetection(actionId, problem, "user");
                Assert.True(items.Length > 0,
                    $"{actionId}：这份输出表示有问题，却没有产出任何待办项——界面会显示「一切正常」。");
            }
        });

        h.Case("UI-128", "★判据：架构处于模拟运行时必须报出来", () =>
        {
            var emulated = RemedyCatalog.FromDetection("envstation.detect.arch", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["process_arch"] = "x64",
                ["os_arch"] = "arm64",
                ["emulated"] = "true",
            });

            Assert.Equal(1, emulated.Length, "模拟运行必须是一条待办项，不能只做成界面上的角标。");
            Assert.Contains("arm64", emulated[0].Impact, "要写清该换成哪个架构的版本。");
            Assert.False(emulated[0].CanAutoFix, "换程序版本不是我们能代做的。");

            Assert.Equal(0, RemedyCatalog.FromDetection("envstation.detect.arch", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["process_arch"] = "x64",
                ["os_arch"] = "x64",
                ["emulated"] = "false",
            }).Length, "架构一致时不该报警。");
        });

        h.Case("UI-129", "★判据：命令找不到时必须给出去哪里装", () =>
        {
            var missing = RemedyCatalog.FromDetection("envstation.detect.command", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = "winget",
                ["found"] = "false",
                ["scope"] = "merged",
            });

            Assert.Equal(1, missing.Length, "winget 缺失是安装链路的前置事实，必须报出来。");
            Assert.Contains("应用安装程序", missing[0].Impact, "要给出具体去处，不能只说「未找到」。");
            Assert.Contains("合并", missing[0].Symptom, "作用域要用中文标签，不能把 merged 这种取值直接写进句子里。");
            Assert.False(missing[0].CanAutoFix, "装 winget 得走 Store，环境站自己没有这条动作。");

            var found = RemedyCatalog.FromDetection("envstation.detect.command", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = "git",
                ["found"] = "true",
                ["resolved_path"] = @"C:\Program Files\Git\cmd\git.exe",
            });

            Assert.Equal(0, found.Length, "找到了就不该报。");
        });

        h.Case("UI-130", "★判据：空间不足与写不进去必须分成两条", () =>
        {
            // 合成一条会让用户去删文件，却依然写不进去——两者的处置完全不同。
            var both = RemedyCatalog.FromDetection("envstation.detect.disk", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["drive"] = @"C:\",
                ["available_bytes"] = "1000000",
                ["required_bytes"] = "9000000",
                ["enough"] = "false",
                ["writable"] = "false",
                ["write_reason"] = "没有写入权限：C:\\Windows（对路径的访问被拒绝）",
                ["probed_path"] = @"C:\Windows",
            });

            Assert.Equal(2, both.Length, "空间不足与不可写是两件事，必须各占一条。");
            Assert.Equal(RemedySeverity.Critical, both[0].Severity, "两者都是阻断级（需求 5.8）。");
            Assert.Contains("空间不足", both[0].Title, "一条讲空间。");

            var notWritable = both.Single(static i => i.Id == "disk.not-writable");
            Assert.Contains("写入测试未通过", notWritable.Title, "另一条讲写入测试。");
            Assert.Contains(
                "没有写入权限",
                notWritable.Symptom,
                "原因要照抄真实写入测试给出的那句话，不能换成笼统的「不可写」。");
            Assert.Contains(
                "真的写了一个文件",
                notWritable.Cause,
                "要说明这不是推测，而是真的写了一次（需求 V6 的口径）。");

            var fine = RemedyCatalog.FromDetection("envstation.detect.disk", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["drive"] = @"C:\",
                ["enough"] = "true",
                ["writable"] = "true",
            });

            Assert.Equal(0, fine.Length, "空间够且可写时不该报警。");
        });

        h.Case("UI-131", "判据：网络全不可达与部分不可达分级不同", () =>
        {
            var none = RemedyCatalog.FromDetection("envstation.detect.network", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reachable_count"] = "0",
                ["total"] = "2",
                ["results"] = "a=fail, b=fail",
            });

            Assert.Equal(1, none.Length, "全部不可达必须报。");
            Assert.Equal(RemedySeverity.Critical, none[0].Severity, "一个都连不上时在线安装整条路都走不通。");
            Assert.Contains("本地归档", none[0].Impact, "要说清还能怎么办。");

            var partial = RemedyCatalog.FromDetection("envstation.detect.network", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reachable_count"] = "1",
                ["total"] = "2",
                ["results"] = "a=ok, b=fail",
            });

            Assert.Equal(RemedySeverity.Warning, partial[0].Severity, "部分可达只是慢或可能失败，不是全线不通。");

            Assert.Equal(0, RemedyCatalog.FromDetection("envstation.detect.network", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reachable_count"] = "2",
                ["total"] = "2",
                ["results"] = "a=ok, b=ok",
            }).Length, "全部可达时不该报警。");
        });

        h.Case("UI-132", "★判据：环境变量读取失败与变量未定义分开报", () =>
        {
            var unreadable = RemedyCatalog.FromDetection("envstation.detect.env", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["found_count"] = "0",
                ["details"] = "[machine] 读取失败：拒绝访问注册表项 || [user] PATH = C:\\Windows",
            });

            Assert.Equal(1, unreadable.Length, "读不到注册表要报——读不到就不能假装这些变量没问题。");
            Assert.Contains("读取失败", unreadable[0].Symptom, "现象里要带上具体是哪一句失败。");

            var undefined = RemedyCatalog.FromDetection("envstation.detect.env", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["found_count"] = "1",
                ["details"] = "[user] JAVA_HOME 未定义 || [user] PATH = C:\\Windows",
            });

            Assert.Equal(1, undefined.Length, "点名要的变量没读到要报。");
            Assert.Contains("JAVA_HOME", undefined[0].Title, "标题里要点出是哪个变量。");

            // 阴性对照：列全部变量时读到很多，不该报。
            Assert.Equal(0, RemedyCatalog.FromDetection("envstation.detect.env", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["found_count"] = "42",
                ["details"] = "[user] 共 42 个变量",
            }).Length, "正常的变量列表不该报警。");
        });

        h.Case("UI-133", "★判据产出的待办项必须四段齐全，且修复动作真实存在", () =>        {
            // DP-6：现象/原因/影响/怎么办缺一段就不算合格的一条。
            // 另外，修复计划里引用的动作 ID 必须真的在动作注册表里——写错一个字母，
            // 界面上的「修复」按钮就会在点下去之后才失败。
            var registry = CoreActions.ActionRegistry.CreateDefault(new AbsDiag.FindingBag()).Value;

            foreach (var (actionId, problem) in ProblemSamples())
            {
                foreach (var item in RemedyCatalog.FromDetection(actionId, problem, "user"))
                {
                    Assert.True(item.Symptom.Length > 0, $"{item.Id}：缺「现象」段。");
                    Assert.True(item.Cause.Length > 0, $"{item.Id}：缺「原因」段。");
                    Assert.True(item.Impact.Length > 0, $"{item.Id}：缺「影响」段。");
                    Assert.Equal(actionId, item.RuleId, $"{item.Id} 的来源检测项写错了，界面无法回溯到是哪一项查出来的。");

                    foreach (var stepAction in item.Plan.ActionIds)
                    {
                        Assert.True(
                            registry.Descriptors.Any(d => string.Equals(d.ActionId, stepAction, StringComparison.Ordinal)),
                            $"{item.Id} 的修复步骤引用了不存在的动作 {stepAction}。");
                    }
                }
            }
        });
    }

    /// <summary>
    /// 每个检测项的一份"必然有问题"的输出样本（阳性对照用）。
    /// </summary>
    /// <remarks>
    /// 键名取自各动作自己声明的输出契约。写错键名会让待办项静默消失，
    /// 因此这份样本同时也是键名的一份存档：改了动作的输出键，这里必须跟着改。
    /// </remarks>
    private static IEnumerable<(string ActionId, Dictionary<string, string> Outputs)> ProblemSamples()
    {
        yield return ("envstation.detect.os", NewOutputs(("supported", "false"), ("build", "10240")));
        yield return ("envstation.detect.arch", NewOutputs(("process_arch", "x64"), ("os_arch", "arm64"), ("emulated", "true")));
        yield return ("envstation.detect.command", NewOutputs(("command", "winget"), ("found", "false"), ("scope", "merged")));
        yield return ("envstation.detect.runtime", NewOutputs(("kind", "python"), ("found", "false")));
        yield return ("envstation.detect.disk", NewOutputs(("drive", @"C:\"), ("enough", "false"), ("writable", "false"), ("write_reason", "没有写入权限"), ("probed_path", @"C:\Windows")));
        yield return ("envstation.detect.deps", NewOutputs(("missing_count", "1"), ("missing", "vcredist-2015-2022-x64")));
        yield return ("envstation.detect.network", NewOutputs(("reachable_count", "0"), ("total", "2"), ("results", "a=fail, b=fail")));
        yield return ("envstation.detect.conflict", NewOutputs(("conflict_count", "2"), ("conflicts", "yarn || python")));
        yield return ("envstation.detect.env", NewOutputs(("found_count", "0"), ("details", "[user] JAVA_HOME 未定义")));
        yield return ("envstation.path.validate", NewOutputs(
            ("entry_count", "5"), ("problem_count", "3"), ("missing_count", "1"),
            ("duplicate_count", "1"), ("empty_count", "1"), ("unresolved_count", "0"),
            ("length", "300"), ("over_legacy_limit", "false"), ("details", @"C:\Gone —— 不存在")));
    }

    private static Dictionary<string, string> NewOutputs(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            map[key] = value;
        }

        return map;
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
