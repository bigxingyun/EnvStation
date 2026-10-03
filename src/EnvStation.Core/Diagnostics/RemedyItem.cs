using System.Collections.Immutable;
using EnvStation.Abstractions.Transactions;

namespace EnvStation.Core.Diagnostics;

/// <summary>待办项严重度（界面上的分级与排序依据）。</summary>
public enum RemedySeverity
{
    /// <summary>建议：不影响可用性，做了更好。</summary>
    Advice = 0,

    /// <summary>警告：可能出问题或已经不好用。</summary>
    Warning = 1,

    /// <summary>严重：已经坏了或即将坏。</summary>
    Critical = 2,
}

/// <summary>修复步骤里的一个参数值。</summary>
/// <param name="Name">参数名（必须是被引动作声明的参数）。</param>
/// <param name="Text">字符串值。</param>
/// <param name="Flag">布尔值；非空时以布尔传参。</param>
/// <param name="Number">整数值；非空时以整数传参。</param>
public sealed record RemedyArgument(string Name, string? Text = null, bool? Flag = null, long? Number = null)
{
    /// <summary>字符串参数。</summary>
    public static RemedyArgument Of(string name, string value) => new(name, Text: value);

    /// <summary>布尔参数。</summary>
    public static RemedyArgument Of(string name, bool value) => new(name, Flag: value);

    /// <summary>整数参数。</summary>
    public static RemedyArgument Of(string name, long value) => new(name, Number: value);
}

/// <summary>修复计划里的一步：调用一个已登记的动作。</summary>
/// <param name="ActionId">动作 ID；必须是动作注册表里存在的预制动作。</param>
/// <param name="Label">界面上这一步的说法（人话，不是动作 ID）。</param>
/// <param name="Arguments">参数。</param>
public sealed record RemedyStep(string ActionId, string Label, ImmutableArray<RemedyArgument> Arguments)
{
    /// <summary>构造一个无参数步骤。</summary>
    public static RemedyStep Of(string actionId, string label) => new(actionId, label, []);

    /// <summary>构造一个带参数步骤。</summary>
    public static RemedyStep Of(string actionId, string label, params RemedyArgument[] arguments) =>
        new(actionId, label, [.. arguments]);
}

/// <summary>
/// 修复计划。刻意只用"已登记动作 + 参数"表达，不引入任何可执行脚本——
/// 铁律 A1（第三方包永远不能执行任意命令）在界面这一层同样成立。
/// </summary>
/// <param name="Steps">按顺序执行的步骤；空表示这条待办无法自动修复。</param>
/// <param name="Summary">对用户展示的"将要做什么"（一句话）。</param>
public sealed record RemediationPlan(ImmutableArray<RemedyStep> Steps, string Summary)
{
    /// <summary>只提示、不可自动修复。</summary>
    public static RemediationPlan NotFixable { get; } = new([], string.Empty);

    /// <summary>构造一个单步计划。</summary>
    public static RemediationPlan Single(RemedyStep step, string summary) => new([step], summary);

    /// <summary>是否有可执行的修复步骤。</summary>
    public bool HasSteps => !Steps.IsDefaultOrEmpty;

    /// <summary>涉及的动作 ID（供能力授权与审计使用）。</summary>
    public IEnumerable<string> ActionIds => Steps.Select(static s => s.ActionId);
}

/// <summary>
/// 一条「发现问题 → 可以怎么办」的待办项。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是本次重构补上的那个缺失的领域模型。</b>在此之前，检测结果只是一串 <c>ActionResult</c>，
/// 界面对它的全部处置能力就是渲染一行文字——「检测出来之后呢」在结构上无处安放。
/// </para>
/// <para>
/// 四段式（现象 / 原因 / 影响 / 怎么办）来自《UI设计规范.md》4.7 的 DP-6：
/// 让非专业用户看得懂"会发生什么"。缺任何一段都不算合格的一条。
/// </para>
/// </remarks>
public sealed record RemedyItem
{
    /// <summary>构造一条待办项。</summary>
    /// <param name="id">稳定标识（用于"忽略此项"与去重）。</param>
    /// <param name="severity">严重度。</param>
    /// <param name="title">标题（一句话，疑问句或陈述句均可）。</param>
    /// <param name="symptom">现象：用户能观察到什么。</param>
    /// <param name="cause">原因：为什么会这样（通俗语言）。</param>
    /// <param name="impact">影响：不管它会怎样。</param>
    /// <param name="plan">修复计划；<see cref="RemediationPlan.NotFixable"/> 表示只提示。</param>
    /// <param name="risk">风险级（决定确认方式）。</param>
    /// <param name="ruleId">来源规则或检测项标识。</param>
    public RemedyItem(
        string id,
        RemedySeverity severity,
        string title,
        string symptom,
        string cause,
        string impact,
        RemediationPlan plan,
        RiskLevel risk,
        string ruleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);

        Id = id;
        Severity = severity;
        Title = title;
        Symptom = symptom;
        Cause = cause;
        Impact = impact;
        Plan = plan;
        Risk = risk;
        RuleId = ruleId;
    }

    /// <summary>稳定标识。</summary>
    public string Id { get; }

    /// <summary>严重度。</summary>
    public RemedySeverity Severity { get; }

    /// <summary>标题。</summary>
    public string Title { get; }

    /// <summary>现象。</summary>
    public string Symptom { get; }

    /// <summary>原因。</summary>
    public string Cause { get; }

    /// <summary>影响。</summary>
    public string Impact { get; }

    /// <summary>修复计划。</summary>
    public RemediationPlan Plan { get; }

    /// <summary>风险级。</summary>
    public RiskLevel Risk { get; }

    /// <summary>来源规则或检测项标识。</summary>
    public string RuleId { get; }

    /// <summary>能否一键修复（有计划即有）。</summary>
    public bool CanAutoFix => Plan.HasSteps;

    /// <summary>
    /// 是否需要二次确认。
    /// </summary>
    /// <remarks>
    /// 门槛按需求 9.1 的风险分级：L0/L1 一步确认即可，L2 起必须二次确认并明示影响面，
    /// L3 只提示不执行（<see cref="ShouldOfferFix"/> 为假）。
    /// </remarks>
    public bool NeedsSecondConfirmation => Risk is RiskLevel.High or RiskLevel.Dangerous;

    /// <summary>是否应该给出"修复"入口（危险级不给，只给说明与手工指引）。</summary>
    public bool ShouldOfferFix => CanAutoFix && Risk != RiskLevel.Dangerous;

    /// <summary>排序键：先按严重度降序，再按风险<b>升序</b>，最后按标识稳定排序。</summary>
    /// <remarks>
    /// <para>
    /// 风险取<b>升序</b>是刻意的：同样严重的问题里，先给用户看容易修的那个。
    /// 一键可复原（L0/L1）的排在高风险（L2）前面——用户点两下就能消掉几条，
    /// 剩下的硬骨头再单独对付。反过来排会让列表第一屏全是需要管理员权限、
    /// 要二次确认的条目，用户会直接关掉不看。
    /// </para>
    /// <para>稳定的最终排序键是必要的——列表顺序随机变化会让用户以为界面在闪。</para>
    /// </remarks>
    public (int Severity, int Risk, string Id) SortKey =>
        (-(int)Severity, (int)Risk, Id);

    /// <summary>构造一条只提示、不可自动修复的待办项。</summary>
    public static RemedyItem Notice(
        string id,
        RemedySeverity severity,
        string title,
        string symptom,
        string cause,
        string impact,
        string ruleId,
        RiskLevel risk = RiskLevel.Safe) =>
        new(id, severity, title, symptom, cause, impact, RemediationPlan.NotFixable, risk, ruleId);
}

/// <summary>待办项集合的排序与汇总辅助。</summary>
public static class RemedyItems
{
    /// <summary>按「严重 → 风险 → 标识」稳定排序。</summary>
    public static IEnumerable<RemedyItem> Ordered(this IEnumerable<RemedyItem> items) =>
        items.OrderBy(static i => i.SortKey.Severity)
             .ThenBy(static i => i.SortKey.Risk)
             .ThenBy(static i => i.SortKey.Id, StringComparer.Ordinal);

    /// <summary>严重级数量。</summary>
    public static int CriticalCount(this IEnumerable<RemedyItem> items) =>
        items.Count(static i => i.Severity == RemedySeverity.Critical);

    /// <summary>警告级数量。</summary>
    public static int WarningCount(this IEnumerable<RemedyItem> items) =>
        items.Count(static i => i.Severity == RemedySeverity.Warning);

    /// <summary>可一键修复的数量。</summary>
    public static int FixableCount(this IEnumerable<RemedyItem> items) =>
        items.Count(static i => i.ShouldOfferFix);

    /// <summary>
    /// 生成一句结论（界面顶部那张汇总卡用它）。
    /// </summary>
    /// <remarks>
    /// 口径沿用文案规范 MS-6：「执行成功」与「结果正常」是两件事，
    /// 因此这里的措辞必须由待办项本身推出，不允许调用方自己拼。
    /// </remarks>
    public static string Summarize(this IEnumerable<RemedyItem> items)
    {
        var list = items as IReadOnlyCollection<RemedyItem> ?? [.. items];
        if (list.Count == 0)
        {
            return "没有发现问题。";
        }

        var critical = list.CriticalCount();
        var warning = list.WarningCount();
        var parts = new List<string>(2);
        if (critical > 0)
        {
            parts.Add($"严重 {critical}");
        }

        if (warning > 0)
        {
            parts.Add($"警告 {warning}");
        }

        var advice = list.Count - critical - warning;
        if (advice > 0)
        {
            parts.Add($"建议 {advice}");
        }

        var fixable = list.FixableCount();
        var tail = fixable > 0 ? $"，{fixable} 条能自动修。" : "。";
        return $"一共 {list.Count} 条：" + string.Join("、", parts) + tail;
    }
}
