using System.Collections.Immutable;
using EnvStation.Abstractions.Diagnostics;

namespace EnvStation.Core.Diagnostics;

/// <summary>一次只读检测的原始结果。</summary>
/// <param name="ActionId">检测动作 ID。</param>
/// <param name="Label">界面上这一项的名字（如「用户 PATH」）。</param>
/// <param name="Succeeded">动作本身是否成功返回。</param>
/// <param name="Message">动作返回的一句话说明。</param>
/// <param name="Outputs">动作声明的输出键值。</param>
/// <param name="ErrorCode">失败时的错误码；成功时为空。</param>
/// <param name="ElapsedMilliseconds">这一项耗时（毫秒）。</param>
/// <param name="Scope">作用域（<c>user</c> / <c>machine</c>），非作用域检测为空。</param>
public sealed record DetectionRecord(
    string ActionId,
    string Label,
    bool Succeeded,
    string Message,
    IReadOnlyDictionary<string, string> Outputs,
    string? ErrorCode = null,
    long ElapsedMilliseconds = 0,
    string Scope = "");

/// <summary>
/// 一次体检的完整结论。
/// </summary>
/// <remarks>
/// <para>
/// <b>它解决的具体问题</b>：上一版的「环境检测」页把 5 个探测的结果渲染成 5 行
/// 「徽标 + 说明」，然后数一下有几个问题写进状态栏。界面对"结论是什么、该怎么办"没有任何模型，
/// 于是用户看到 5 行灰字，不知道先处理哪个。
/// </para>
/// <para>
/// 把结论收成一个类型之后，三件事同时变得可能：<b>排序</b>（严重的排前面）、
/// <b>分级</b>（严重/警告/建议各自计数）、<b>给出路</b>（每条挂修复计划）。
/// 界面只负责渲染，不再自己下结论——上一版「徽标说正常、正文说异常」（D-44）
/// 正是因为"下结论"这件事散在各处。
/// </para>
/// <para>
/// 本类型不可变，构造即完成全部推导。
/// </para>
/// </remarks>
public sealed record DiagnosticReport
{
    private DiagnosticReport(
        ImmutableArray<DetectionRecord> detections,
        ImmutableArray<RemedyItem> remedies)
    {
        Detections = detections;
        Remedies = remedies;
    }

    /// <summary>原始检测记录（按传入顺序）。</summary>
    public ImmutableArray<DetectionRecord> Detections { get; }

    /// <summary>全部待办项（已按「严重 → 风险 → 标识」排序）。</summary>
    public ImmutableArray<RemedyItem> Remedies { get; }

    /// <summary>执行失败的检测项（不是"检出了问题"，而是"这一项没跑成"）。</summary>
    /// <remarks>
    /// 这两种情况必须分开：前者是环境的毛病，后者是工具自身的毛病。
    /// 混在一起会让用户去修一个根本不存在的问题。
    /// </remarks>
    public ImmutableArray<DetectionRecord> FailedDetections =>
        [.. Detections.Where(static d => !d.Succeeded)];

    /// <summary>严重级数量。</summary>
    public int CriticalCount => Remedies.CriticalCount();

    /// <summary>警告级数量。</summary>
    public int WarningCount => Remedies.WarningCount();

    /// <summary>建议级数量。</summary>
    public int AdviceCount => Remedies.Count(static r => r.Severity == RemedySeverity.Advice);

    /// <summary>可一键修复的数量。</summary>
    public int FixableCount => Remedies.FixableCount();

    /// <summary>
    /// 是否一切正常。
    /// </summary>
    /// <remarks>
    /// 「尚未检测」<b>不算</b>正常：还没看过就宣称健康，既与界面上那句「尚未检测。」自相矛盾，
    /// 也会让顶部结论卡显示成绿色的"正常"——用户会以为已经查过了。
    /// 因此这里要求至少跑过一次检测。
    /// </remarks>
    public bool IsHealthy => !Detections.IsEmpty && Remedies.IsEmpty && FailedDetections.IsEmpty;

    /// <summary>总体语气。</summary>
    /// <remarks>
    /// 三种情况各有对应：有待办项看最严重的那条；没有待办项但检测失败则是异常
    /// （"我没能检查"不等同于"检查通过"——这正是 CP-3 与 MS-6 的口径）；都不是才算正常。
    /// </remarks>
    public ToneStyle Tone
    {
        get
        {
            if (!Remedies.IsEmpty)
            {
                return StatusTone.For(Remedies);
            }

            return FailedDetections.IsEmpty ? StatusTone.Success : StatusTone.Error;
        }
    }

    /// <summary>当前状态对应的页面状态种类。</summary>
    public PageStateKind StateKind
    {
        get
        {
            if (Detections.IsEmpty)
            {
                return PageStateKind.Empty;
            }

            if (!Remedies.IsEmpty)
            {
                return PageStateKind.Partial;
            }

            return FailedDetections.IsEmpty ? PageStateKind.Ready : PageStateKind.Error;
        }
    }

    /// <summary>
    /// 生成顶部汇总卡的那句话。
    /// </summary>
    /// <remarks>
    /// 口径来自文案规范 MS-6：「执行成功」与「结果正常」是两件事。
    /// 因此这里的措辞必须由结论推出，且**检测失败必须单独说出来**，
    /// 不能因为"没有待办项"就报正常。
    /// </remarks>
    public string Summarize()
    {
        if (Detections.IsEmpty)
        {
            return "尚未检测。";
        }

        var failed = FailedDetections.Length;
        var tail = failed > 0 ? $"另有 {failed} 项检测未能完成。" : string.Empty;

        if (Remedies.IsEmpty)
        {
            return failed > 0
                ? $"{Detections.Length} 项检测中有 {failed} 项未完成，其余未发现问题。"
                : $"{Detections.Length} 项检测全部正常。";
        }

        return Remedies.Summarize() + tail;
    }

    /// <summary>
    /// 由检测记录构造报告。
    /// </summary>
    /// <param name="detections">检测记录。</param>
    /// <remarks>
    /// 对每一项都跑一遍 <see cref="RemedyCatalog"/>。已登记的检测项会产出待办项；
    /// <b>未登记的检测项不会报错，但也不会产出任何东西</b>——这正是 X-2 那条纪律要拦的情况，
    /// 由用例比对「动作注册表里的检测类动作」与 <see cref="RemedyCatalog.MappedDetections"/> 来兜底，
    /// 而不是在这里抛异常（运行期抛异常等于让整个体检失败，比漏一条更糟）。
    /// </remarks>
    public static DiagnosticReport From(IEnumerable<DetectionRecord> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);

        var list = detections as IReadOnlyList<DetectionRecord> ?? [.. detections];
        var remedies = ImmutableArray.CreateBuilder<RemedyItem>();

        foreach (var detection in list)
        {
            // 检测本身没跑成时，不去推导待办项：拿不到输出键就没有依据，
            // 硬推会造出"看起来像问题其实只是没测到"的假条目。
            if (!detection.Succeeded)
            {
                continue;
            }

            remedies.AddRange(RemedyCatalog.FromDetection(detection.ActionId, detection.Outputs, detection.Scope));
        }

        return new DiagnosticReport([.. list], [.. remedies.Ordered()]);
    }

    /// <summary>空报告（尚未检测）。</summary>
    public static DiagnosticReport None { get; } = new([], []);
}

/// <summary>
/// 把待办项分成界面上的三档。
/// </summary>
/// <param name="Critical">严重。</param>
/// <param name="Warning">警告。</param>
/// <param name="Advice">建议。</param>
/// <remarks>
/// 分档放在这里而不是界面里：分档规则与 <see cref="RemedySeverity"/> 是一对概念，
/// 散在界面里迟早会出现"这一页把 Advice 归到警告、那一页归到建议"。
/// </remarks>
public sealed record RemedyGroups(
    ImmutableArray<RemedyItem> Critical,
    ImmutableArray<RemedyItem> Warning,
    ImmutableArray<RemedyItem> Advice)
{
    /// <summary>总条数。</summary>
    public int Total => Critical.Length + Warning.Length + Advice.Length;

    /// <summary>是否全部为空。</summary>
    public bool IsEmpty => Total == 0;

    /// <summary>按严重度分组。</summary>
    public static RemedyGroups From(IEnumerable<RemedyItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var ordered = items.Ordered().ToArray();
        return new RemedyGroups(
            [.. ordered.Where(static i => i.Severity == RemedySeverity.Critical)],
            [.. ordered.Where(static i => i.Severity == RemedySeverity.Warning)],
            [.. ordered.Where(static i => i.Severity == RemedySeverity.Advice)]);
    }

    /// <summary>按「严重 → 警告 → 建议」顺序枚举非空分组，附带组标题与计数。</summary>
    public IEnumerable<(string Title, ImmutableArray<RemedyItem> Items)> NonEmpty()
    {
        if (Critical.Length > 0)
        {
            yield return ("严重", Critical);
        }

        if (Warning.Length > 0)
        {
            yield return ("警告", Warning);
        }

        if (Advice.Length > 0)
        {
            yield return ("建议", Advice);
        }
    }
}
