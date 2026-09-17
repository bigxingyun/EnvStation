namespace EnvStation.Core.Environment;

/// <summary>
/// PATH 中单个条目存在的问题标记。
/// <para>
/// 对应《需求分析.md》6.2 节与环境检测规则；每条都必须能给出**面向用户的解释**，
/// 而不只是一个位标志（UI 需要"现象/原因/影响/怎么办"四段式文案）。
/// </para>
/// </summary>
[Flags]
public enum PathEntryIssue
{
    None = 0,

    /// <summary>空项：由连续分号或首尾分号产生。</summary>
    Empty = 1 << 0,

    /// <summary>目录不存在。典型成因是软件卸载残留。</summary>
    Missing = 1 << 1,

    /// <summary>与另一个条目重复（规范化并忽略大小写后相同）。</summary>
    Duplicate = 1 << 2,

    /// <summary>非绝对路径（相对路径或盘符相对路径，如 <c>C:foo</c>、<c>foo</c>）。</summary>
    Relative = 1 << 3,

    /// <summary>引用的环境变量未定义，或 <c>%</c> 未配对。</summary>
    UnresolvedVariable = 1 << 4,

    /// <summary>含 Windows 不允许的路径字符。</summary>
    InvalidCharacters = 1 << 5,

    /// <summary>以分隔符结尾（根目录除外）。多数工具可容忍，但有工具会因此判定失败。</summary>
    TrailingSeparator = 1 << 6,
}

/// <summary>
/// PATH 中的一个条目。
/// <para>
/// <b>核心不变量（PE-1/PE-2）：</b><see cref="Raw"/> 永远保留用户原始写法（用于回写），
/// <see cref="Normalized"/> 只用于比较与展示。回写一律用 Raw，绝不写 Normalized ——
/// 否则会把用户的 <c>%JAVA_HOME%\bin</c> 展开成绝对路径，破坏可移植性（PE-4）。
/// </para>
/// </summary>
public sealed record PathEntry
{
    /// <summary>在原始 PATH 字符串中的序号（从 0 开始，用于 diff 定位与拖拽排序）。</summary>
    public required int Index { get; init; }

    /// <summary>原始写法，已去除首尾空白。回写时使用此值。</summary>
    public required string Raw { get; init; }

    /// <summary>
    /// 规范化后的形式：展开变量（仅用于比较）＋ 解析 8.3 短名 ＋ 去除尾随分隔符。
    /// <b>禁止用于回写。</b>
    /// </summary>
    public required string Normalized { get; init; }

    /// <summary>规范化后再转小写，作为重复判定的键（Windows 路径大小写不敏感，PE-2）。</summary>
    public required string ComparisonKey { get; init; }

    /// <summary>目录当前是否存在。空项恒为 false。</summary>
    public required bool Exists { get; init; }

    /// <summary>该条目是否是 CD 型条目（空、空白、或仅 <c>.</c>），这类条目会在 PATH 中开后门。</summary>
    public bool IsCurrentDirectory => string.IsNullOrWhiteSpace(Raw)
        || Raw.Trim() is "." or ".\\" or "./";

    public required PathEntryIssue Issues { get; init; }

    /// <summary>若本项与另一项重复，指向首次出现的下标；否则为 <c>null</c>。</summary>
    public int? DuplicateOfIndex { get; init; }

    /// <summary>正常项：存在、绝对、无重复、无其他问题。</summary>
    public bool IsHealthy => Issues == PathEntryIssue.None;

    /// <summary>是否应在"清理失效项"操作中被移除。</summary>
    public bool IsRemovableByClean =>
        (Issues & (PathEntryIssue.Empty | PathEntryIssue.Missing | PathEntryIssue.Duplicate)) != 0;

    public override string ToString() => Raw;

    /// <summary>供 UI 以"中间省略、保留盘符与末段"的方式显示长路径（UI 设计规范 4.4 节 PL-8）。</summary>
    public string ToDisplayString(int maxLength = 72)
    {
        if (Raw.Length <= maxLength)
        {
            return Raw;
        }

        var lastSep = Raw.LastIndexOfAny(['\\', '/']);
        if (lastSep <= 0)
        {
            return Raw[..maxLength] + "…";
        }

        var tail = Raw[lastSep..];
        var head = Raw[..Math.Max(3, maxLength - tail.Length - 1)];
        return head + "…" + tail;
    }
}
