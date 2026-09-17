using System.Collections.Immutable;
using System.Text;

namespace EnvStation.Abstractions.Diagnostics;

/// <summary>
/// 发现（Finding）等级。统一了"规则引擎"与"包校验"两处的严重级别语义，
/// 使 UI、CLI 与报告只需处理一种结构（需求 24.2 的 block / warn / info）。
/// </summary>
public enum FindingLevel
{
    /// <summary>提示：不影响执行，仅告知。</summary>
    Info = 0,

    /// <summary>警告：允许继续，但必须在能力授权与报告中醒目标注。</summary>
    Warn = 1,

    /// <summary>阻断：禁止继续。导入流程任一步失败即中止（需求 IMP-1）。</summary>
    Block = 2,
}

/// <summary>
/// 一条校验发现。字段设计目标是"用户看完就知道发生了什么、该怎么办"（需求 U-4）。
/// </summary>
/// <param name="RuleId">规则或错误码标识，如 <c>S-01</c>、<c>R1</c>、<c>E_PACKAGE_PARSE_FAILED</c>。</param>
/// <param name="Level">严重级别。</param>
/// <param name="Title">一句话标题。</param>
/// <param name="Message">现象说明（中文）。</param>
/// <param name="Suggestion">建议动作（中文）；可为空。</param>
/// <param name="Location">定位信息，如 <c>workflow.toml:42</c>；可为空。</param>
/// <param name="Subject">被检查的对象，如动作 ID 或路径；可为空。</param>
public sealed record Finding(
    string RuleId,
    FindingLevel Level,
    string Title,
    string Message,
    string? Suggestion = null,
    string? Location = null,
    string? Subject = null)
{
    /// <summary>是否为阻断级。</summary>
    public bool IsBlocker => Level == FindingLevel.Block;

    /// <summary>单行渲染，供 CLI 与日志使用。</summary>
    public string ToLine()
    {
        var sb = new StringBuilder();
        sb.Append(Level switch
        {
            FindingLevel.Block => "[阻断]",
            FindingLevel.Warn => "[警告]",
            _ => "[提示]",
        });
        sb.Append(' ').Append(RuleId);
        if (Location is { Length: > 0 })
        {
            sb.Append(" (").Append(Location).Append(')');
        }

        sb.Append('：').Append(Title);
        sb.Append(" — ").Append(Message);
        if (Suggestion is { Length: > 0 })
        {
            sb.Append(" 建议：").Append(Suggestion);
        }

        return sb.ToString();
    }
}

/// <summary>
/// 发现收集器。校验过程刻意"尽量跑完再汇报"——一次性告知用户所有问题，
/// 而不是修一个报一个（对比需求 U-4：不让用户在错误之间来回试错）。
/// </summary>
public sealed class FindingBag
{
    private readonly List<Finding> _items = [];

    /// <summary>已收集的发现。</summary>
    public IReadOnlyList<Finding> Items => _items;

    /// <summary>是否存在阻断级发现。</summary>
    public bool HasBlockers => _items.Any(static f => f.IsBlocker);

    /// <summary>阻断级数量。</summary>
    public int BlockCount => _items.Count(static f => f.Level == FindingLevel.Block);

    /// <summary>警告级数量。</summary>
    public int WarnCount => _items.Count(static f => f.Level == FindingLevel.Warn);

    /// <summary>添加一条发现。</summary>
    public void Add(Finding finding) => _items.Add(finding);

    /// <summary>添加一条阻断级发现。</summary>
    public void Block(string ruleId, string title, string message, string? suggestion = null, string? location = null, string? subject = null) =>
        _items.Add(new Finding(ruleId, FindingLevel.Block, title, message, suggestion, location, subject));

    /// <summary>添加一条警告级发现。</summary>
    public void Warn(string ruleId, string title, string message, string? suggestion = null, string? location = null, string? subject = null) =>
        _items.Add(new Finding(ruleId, FindingLevel.Warn, title, message, suggestion, location, subject));

    /// <summary>添加一条提示级发现。</summary>
    public void Info(string ruleId, string title, string message, string? suggestion = null, string? location = null, string? subject = null) =>
        _items.Add(new Finding(ruleId, FindingLevel.Info, title, message, suggestion, location, subject));

    /// <summary>合并另一批发现。</summary>
    public void AddRange(IEnumerable<Finding> findings) => _items.AddRange(findings);

    /// <summary>导出为只读报告。</summary>
    public ValidationReport ToReport() => new([.. _items]);

    /// <summary>只保留阻断级发现（用于"短报告"模式）。</summary>
    public ImmutableArray<Finding> Blockers() => [.. _items.Where(static f => f.IsBlocker)];
}

/// <summary>
/// 校验报告。V1~V4 与规则引擎的统一输出契约。
/// </summary>
/// <param name="Findings">全部发现，按"阻断 → 警告 → 提示"排序输出。</param>
public sealed record ValidationReport(ImmutableArray<Finding> Findings)
{
    /// <summary>空报告（无任何发现）。</summary>
    public static ValidationReport Empty { get; } = new(ImmutableArray<Finding>.Empty);

    /// <summary>是否存在阻断级发现。</summary>
    public bool HasBlockers => Findings.Any(static f => f.IsBlocker);

    /// <summary>阻断级数量。</summary>
    public int BlockCount => Findings.Count(static f => f.Level == FindingLevel.Block);

    /// <summary>警告级数量。</summary>
    public int WarnCount => Findings.Count(static f => f.Level == FindingLevel.Warn);

    /// <summary>按严重级别降序排序后的发现。</summary>
    public IEnumerable<Finding> Ordered() =>
        Findings.OrderByDescending(static f => (int)f.Level).ThenBy(static f => f.RuleId, StringComparer.Ordinal);

    /// <summary>渲染为多行文本报告。</summary>
    public string ToText()
    {
        if (Findings.Length == 0)
        {
            return "校验通过：未发现问题。";
        }

        var sb = new StringBuilder();
        sb.Append("校验结果：阻断 ").Append(BlockCount)
          .Append(" 项，警告 ").Append(WarnCount)
          .Append(" 项，合计 ").Append(Findings.Length).Append(" 项。\n");
        foreach (var f in Ordered())
        {
            sb.Append("  ").Append(f.ToLine()).Append('\n');
        }

        return sb.ToString();
    }
}
