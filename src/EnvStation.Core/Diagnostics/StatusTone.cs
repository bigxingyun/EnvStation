using EnvStation.Abstractions.Diagnostics;

namespace EnvStation.Core.Diagnostics;

/// <summary>状态语气（界面配色与图标选择的唯一依据）。</summary>
public enum ToneKind
{
    /// <summary>中性：只是信息。</summary>
    Neutral = 0,

    /// <summary>正常：检查通过。</summary>
    Success = 1,

    /// <summary>警告：需要关注但不致命。</summary>
    Warning = 2,

    /// <summary>异常：已经坏了或必须处理。</summary>
    Error = 3,
}

/// <summary>
/// 一个状态的三重编码：颜色角色 + 形状 + 文字。
/// </summary>
/// <param name="Kind">语气。</param>
/// <param name="Glyph">形状字形（Segoe Fluent Icons 码位）。</param>
/// <param name="Shape">形状的语义名，供自绘图形使用（色盲用户靠它分辨）。</param>
/// <param name="Label">最短文字（2~4 字，取自文案规范第 5 节的「界面标签」档）。</param>
public sealed record ToneStyle(ToneKind Kind, string Glyph, ToneShape Shape, string Label);

/// <summary>状态形状。颜色之外必须还有形状，否则色盲用户无法分辨。</summary>
public enum ToneShape
{
    /// <summary>圆环：中性。</summary>
    Circle = 0,

    /// <summary>对勾：正常。</summary>
    Check = 1,

    /// <summary>三角：警告。</summary>
    Triangle = 2,

    /// <summary>八边形（停止标志）：异常。</summary>
    Octagon = 3,
}

/// <summary>
/// 状态 → 三重编码的映射。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么集中在这里</b>：上一版界面的状态只有颜色（一个带底色的小方块 + 文字），
/// 而设计规范 A11Y-2 要求「颜色 + 图标 + 文字」三重编码。
/// 更要紧的是，状态语气此前在多处各判一次，于是出现过
/// 「徽标说正常、正文说缺少 1 项」这种自相矛盾的界面（缺陷 D-44）。
/// 把映射收成一个纯函数之后，界面只负责渲染，不再自己下结论。
/// </para>
/// </remarks>
public static class StatusTone
{
    /// <summary>中性。</summary>
    public static ToneStyle Neutral { get; } = new(ToneKind.Neutral, "\uE9CE", ToneShape.Circle, "信息");

    /// <summary>正常。</summary>
    public static ToneStyle Success { get; } = new(ToneKind.Success, "\uE73E", ToneShape.Check, "正常");

    /// <summary>警告。</summary>
    public static ToneStyle Warning { get; } = new(ToneKind.Warning, "\uE7BA", ToneShape.Triangle, "警告");

    /// <summary>异常。</summary>
    public static ToneStyle Error { get; } = new(ToneKind.Error, "\uEA39", ToneShape.Octagon, "异常");

    /// <summary>由待办项严重度取语气。</summary>
    public static ToneStyle For(RemedySeverity severity) => severity switch
    {
        RemedySeverity.Critical => Error,
        RemedySeverity.Warning => Warning,
        _ => Neutral,
    };

    /// <summary>由发现项等级取语气。</summary>
    public static ToneStyle For(FindingLevel level) => level switch
    {
        FindingLevel.Block => Error,
        FindingLevel.Warn => Warning,
        _ => Neutral,
    };

    /// <summary>由页面状态取语气。</summary>
    public static ToneStyle For(PageStateKind kind) => kind switch
    {
        PageStateKind.Error => Error,
        PageStateKind.Partial => Warning,
        PageStateKind.Ready => Success,
        PageStateKind.Empty => Neutral,
        _ => Neutral,
    };

    /// <summary>由计数取语气：0 为正常，其余为警告。</summary>
    public static ToneStyle ForCount(int problemCount) => problemCount <= 0 ? Success : Warning;

    /// <summary>
    /// 由一组待办项取总体语气。
    /// </summary>
    /// <remarks>
    /// 口径与文案规范 MS-6 一致：「动作执行成功」与「结果正常」是两件事。
    /// 这里只看待办项本身的严重度，不看任何动作是否成功返回。
    /// </remarks>
    public static ToneStyle For(IEnumerable<RemedyItem> items)
    {
        var worst = RemedySeverity.Advice;
        var any = false;
        foreach (var item in items)
        {
            any = true;
            if (item.Severity > worst)
            {
                worst = item.Severity;
            }
        }

        return any ? For(worst) : Success;
    }
}
