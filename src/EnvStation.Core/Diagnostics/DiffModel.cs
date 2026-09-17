using System.Collections.Immutable;
using System.Text;

namespace EnvStation.Core.Diagnostics;

/// <summary>行级差异的种类。</summary>
public enum DiffKind
{
    /// <summary>未变化（可折叠展示，用于给出上下文）。</summary>
    Unchanged = 0,

    /// <summary>新增。</summary>
    Added = 1,

    /// <summary>删除。</summary>
    Removed = 2,

    /// <summary>修改（旧值 → 新值）。</summary>
    Changed = 3,

    /// <summary>保持但顺序变了（PATH 顺序调整属于此类）。</summary>
    Moved = 4,
}

/// <summary>一行差异。</summary>
/// <param name="Kind">差异种类。</param>
/// <param name="Key">被比较对象的名称（变量名、PATH 项等）。</param>
/// <param name="Before">变更前的值；新增时为 null。</param>
/// <param name="After">变更后的值；删除时为 null。</param>
/// <param name="Note">补充说明（可为空，例如「与第 3 项重复」）。</param>
public sealed record DiffRow(DiffKind Kind, string Key, string? Before, string? After, string? Note = null);

/// <summary>
/// 一次写操作的完整差量。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这是信任模型的核心</b>：本产品所有写操作都遵循「先看变更、再执行」。
/// 界面上必须先把即将发生的事摊开给用户看——这是需求 G5「不静默、不隐瞒」的落地界面。
/// </para>
/// <para>
/// <b>纪律（对应重构方案 X-3）</b>：预览与执行<b>必须用同一份数据</b>。
/// 不允许「预览算一遍、执行再算一遍」——那两者迟早会算得不一样，而用户是照着预览点的确认。
/// </para>
/// </remarks>
public sealed record DiffModel
{
    private DiffModel(ImmutableArray<DiffRow> rows, string title, string? target)
    {
        Rows = rows;
        Title = title;
        Target = target;
    }

    /// <summary>全部差异行（含 Unchanged，供折叠显示上下文）。</summary>
    public ImmutableArray<DiffRow> Rows { get; }

    /// <summary>这次变更的名字（如「用户级 PATH」）。</summary>
    public string Title { get; }

    /// <summary>变更的落点（如注册表键或文件路径）；可为空。</summary>
    public string? Target { get; }

    /// <summary>新增行数。</summary>
    public int AddedCount => Rows.Count(static r => r.Kind == DiffKind.Added);

    /// <summary>删除行数。</summary>
    public int RemovedCount => Rows.Count(static r => r.Kind == DiffKind.Removed);

    /// <summary>修改行数。</summary>
    public int ChangedCount => Rows.Count(static r => r.Kind == DiffKind.Changed);

    /// <summary>顺序变化行数。</summary>
    public int MovedCount => Rows.Count(static r => r.Kind == DiffKind.Moved);

    /// <summary>是否有实际变化。</summary>
    public bool HasChanges => Rows.Any(static r => r.Kind != DiffKind.Unchanged);

    /// <summary>只有 Unchanged 的那些行（渲染时通常折叠）。</summary>
    public IEnumerable<DiffRow> Context => Rows.Where(static r => r.Kind == DiffKind.Unchanged);

    /// <summary>实际发生变化的行。</summary>
    public IEnumerable<DiffRow> Changes => Rows.Where(static r => r.Kind != DiffKind.Unchanged);

    /// <summary>构造一个只有标题、没有变化的空差量（用于「无需变更」的结果）。</summary>
    public static DiffModel NoChange(string title, string? target = null) =>
        new([], title, target);

    /// <summary>直接由行集合构造。</summary>
    public static DiffModel From(string title, IEnumerable<DiffRow> rows, string? target = null) =>
        new([.. rows], title, target);

    /// <summary>
    /// 按"键"比较两个集合，得出差量。
    /// </summary>
    /// <param name="title">变更名称。</param>
    /// <param name="before">变更前的项（每个元素给出键与值）。</param>
    /// <param name="after">变更后的项（顺序有意义时按传入顺序判定 Moved）。</param>
    /// <param name="target">变更落点。</param>
    /// <param name="orderMatters">顺序是否算差异（PATH 为 true，环境变量集合为 false）。</param>
    public static DiffModel Compare(
        string title,
        IEnumerable<KeyValuePair<string, string>> before,
        IEnumerable<KeyValuePair<string, string>> after,
        string? target = null,
        bool orderMatters = false)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeList = before as IReadOnlyList<KeyValuePair<string, string>> ?? [.. before];
        var afterList = after as IReadOnlyList<KeyValuePair<string, string>> ?? [.. after];

        var beforeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in beforeList)
        {
            beforeMap[kv.Key] = kv.Value;
        }

        var afterMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in afterList)
        {
            afterMap[kv.Key] = kv.Value;
        }

        var beforeIndex = IndexOf(beforeList);
        var afterIndex = IndexOf(afterList);

        var rows = new List<DiffRow>(beforeList.Count + afterList.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先按"变更后"的顺序走一遍，这样 Added / Changed / Moved / Unchanged 的展示顺序与结果一致。
        for (var i = 0; i < afterList.Count; i++)
        {
            var key = afterList[i].Key;
            var value = afterList[i].Value;
            if (!emitted.Add(key))
            {
                continue;
            }

            if (!beforeMap.TryGetValue(key, out var oldValue))
            {
                rows.Add(new DiffRow(DiffKind.Added, key, null, value));
                continue;
            }

            if (!string.Equals(oldValue, value, StringComparison.Ordinal))
            {
                rows.Add(new DiffRow(DiffKind.Changed, key, oldValue, value));
                continue;
            }

            if (orderMatters && beforeIndex[key] != i)
            {
                rows.Add(new DiffRow(DiffKind.Moved, key, oldValue, value,
                    $"第 {beforeIndex[key] + 1} 位 → 第 {i + 1} 位"));
                continue;
            }

            rows.Add(new DiffRow(DiffKind.Unchanged, key, oldValue, value));
        }

        // 再补上被删掉的项。
        for (var i = 0; i < beforeList.Count; i++)
        {
            var key = beforeList[i].Key;
            if (afterMap.ContainsKey(key) || !emitted.Add(key))
            {
                continue;
            }

            rows.Add(new DiffRow(DiffKind.Removed, key, beforeList[i].Value, null));
        }

        return new DiffModel([.. rows], title, target);
    }

    /// <summary>
    /// 渲染成等宽文本（CLI 与「复制变更」按钮共用）。
    /// </summary>
    /// <remarks>
    /// 符号沿用通用的差异标记：<c>+</c> 新增、<c>-</c> 删除、<c>~</c> 修改、<c>↕</c> 顺序变化、
    /// 空格为未变化。只用 ASCII 与一个箭头字符，保证在等宽字体与纯文本环境里都不会错位。
    /// </remarks>
    public string ToText(bool includeContext = false)
    {
        var sb = new StringBuilder();
        sb.Append(Title);
        if (Target is { Length: > 0 })
        {
            sb.Append('（').Append(Target).Append('）');
        }

        sb.Append('\n');
        if (!HasChanges)
        {
            sb.Append("  无需变更。\n");
            return sb.ToString();
        }

        foreach (var row in Rows)
        {
            if (!includeContext && row.Kind == DiffKind.Unchanged)
            {
                continue;
            }

            sb.Append("  ").Append(Marker(row.Kind)).Append(' ').Append(row.Key);
            switch (row.Kind)
            {
                case DiffKind.Added:
                    sb.Append('：').Append(row.After);
                    break;
                case DiffKind.Removed:
                    sb.Append('：').Append(row.Before);
                    break;
                case DiffKind.Changed:
                    sb.Append('：').Append(row.Before).Append(" → ").Append(row.After);
                    break;
                case DiffKind.Moved:
                    if (row.Note is { Length: > 0 })
                    {
                        sb.Append('：').Append(row.Note);
                    }

                    break;
                default:
                    sb.Append('：').Append(row.After);
                    break;
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>一句话概括变更规模（徽标与状态栏用）。</summary>
    public string Summarize()
    {
        if (!HasChanges)
        {
            return "无变更";
        }

        var parts = new List<string>(4);
        if (AddedCount > 0)
        {
            parts.Add($"新增 {AddedCount}");
        }

        if (RemovedCount > 0)
        {
            parts.Add($"删除 {RemovedCount}");
        }

        if (ChangedCount > 0)
        {
            parts.Add($"修改 {ChangedCount}");
        }

        if (MovedCount > 0)
        {
            parts.Add($"顺序调整 {MovedCount}");
        }

        return string.Join("、", parts);
    }

    private static char Marker(DiffKind kind) => kind switch
    {
        DiffKind.Added => '+',
        DiffKind.Removed => '-',
        DiffKind.Changed => '~',
        DiffKind.Moved => '↕',
        _ => ' ',
    };

    private static Dictionary<string, int> IndexOf(IReadOnlyList<KeyValuePair<string, string>> items)
    {
        var map = new Dictionary<string, int>(items.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            map[items[i].Key] = i;
        }

        return map;
    }
}
