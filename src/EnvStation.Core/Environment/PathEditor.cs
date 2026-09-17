using EnvStation.Abstractions;

namespace EnvStation.Core.Environment;

/// <summary>一次 PATH 编辑产生的变更类型，用于生成 diff 预览（需求 M1-5 要求逐条列出原因）。</summary>
public enum PathChangeKind
{
    Added,
    Removed,
    Moved,
    Kept,
}

/// <summary>单条变更记录，直接对应 UI 上的 diff 行。</summary>
public sealed record PathChange
{
    public required PathChangeKind Kind { get; init; }

    /// <summary>变更的值（原始写法）。</summary>
    public required string Value { get; init; }

    /// <summary>变更原因，面向用户的中文短语（如"重复，保留首次出现"）。</summary>
    public required string Reason { get; init; }

    /// <summary>变更前的下标（新增时为 <c>null</c>）。</summary>
    public int? FromIndex { get; init; }

    /// <summary>变更后的下标（删除时为 <c>null</c>）。</summary>
    public int? ToIndex { get; init; }
}

/// <summary>
/// PATH 编辑计划：纯数据结果，**不接触注册表**。
/// <para>
/// 拆成纯函数的好处：
/// <list type="bullet">
///   <item>可以让调用方先把它渲染成 diff 交给用户确认（需求 M1-5 / CF-2），确认后再落盘；</item>
///   <item>可以脱离文件系统与注册表做完整单元测试；</item>
///   <item>同一个计划对象既能生成 diff，也能交给事务层执行，避免"预览"与"实际执行"走两套逻辑。</item>
/// </list>
/// </para>
/// </summary>
public sealed record PathEditPlan
{
    /// <summary>编辑前的条目（用于对比）。</summary>
    public required IReadOnlyList<PathEntry> Before { get; init; }

    /// <summary>编辑后的条目。</summary>
    public required IReadOnlyList<PathEntry> After { get; init; }

    /// <summary>逐条变更明细。</summary>
    public required IReadOnlyList<PathChange> Changes { get; init; }

    /// <summary>
    /// 无变化时的说明。用于让动作层在审计日志里写清"为何什么都没做"——
    /// 这对用户排查"我点了整理但 PATH 没变"很关键。
    /// </summary>
    public string? SummaryReason { get; init; }

    /// <summary>是否有实际变化。为 false 时调用方不应执行任何写入（避免无意义快照与变更记录）。</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>把编辑后的条目拼回 PATH 字符串。</summary>
    public string ToPathString() => PathParser.Join(After);

    /// <summary>变更摘要，形如 "+1 添加 · -2 移除 · 0 移动"；无变化时给出原因。</summary>
    public string Summarize()
    {
        if (!HasChanges)
        {
            return SummaryReason ?? "无变化";
        }

        var add = Changes.Count(c => c.Kind == PathChangeKind.Added);
        var del = Changes.Count(c => c.Kind == PathChangeKind.Removed);
        var mov = Changes.Count(c => c.Kind == PathChangeKind.Moved);
        return $"+{add} 添加 · -{del} 移除 · {mov} 移动";
    }
}

/// <summary>
/// PATH 编辑操作（纯函数，不接触注册表与事务）。
///
/// <para><b>设计原则：</b>所有操作都返回 <see cref="PathEditPlan"/>，由调用方决定是否执行。
/// 这样"预览 diff"和"实际写入"共用同一份计算，不可能出现两者不一致的情况。</para>
///
/// <para><b>幂等性（动作契约 AC-2）：</b>所有方法重复执行都不产生额外变化。
/// 例如对已存在的条目调用 <see cref="Ensure"/>，返回的计划的 <see cref="PathEditPlan.HasChanges"/> 为 false。</para>
/// </summary>
public static class PathEditor
{
    /// <summary>
    /// 确保某目录存在于 PATH 中。已存在时（忽略大小写与短名差异）不重复添加。
    /// </summary>
    /// <param name="entries">当前条目。</param>
    /// <param name="directory">要确保存在的目录。</param>
    /// <param name="position">
    /// 插入位置。**默认追加到末尾（<see cref="PathPosition.Append"/>）**。
    ///
    /// <para><b>为什么默认不是前置（这是一个安全取舍）：</b>
    /// 前置会改变同名命令的优先级，从而可能悄悄劫持用户已有的工具链
    /// （例如把另一个 Python 的目录插到最前，会让 <c>python</c> 指向意料之外的版本）。
    /// 追加对现有环境的影响最小，符合设计原则 G1「安全优先于便捷」。
    /// 需要前置时必须由调用方显式指定 —— 那种场景应属于"消解命令冲突"，
    /// 且必须走 diff 预览 + 用户确认。</para>
    /// </param>
    public static PathEditPlan Ensure(
        IReadOnlyList<PathEntry> entries, string directory, PathPosition position = PathPosition.Append)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (string.IsNullOrWhiteSpace(directory))
        {
            return NoChange(entries, "目录为空，未做任何修改");
        }

        var key = PathParser.ComparisonKey(directory);
        var existing = entries.FirstOrDefault(e => string.Equals(e.ComparisonKey, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // 幂等：已存在即不做任何事。
            // 注意这里**不**因为"重复项"而顺手清理 —— 清理是 Clean/Dedupe 的职责，
            // 混在一起会让用户无法预期 Ensure 的行为。
            return NoChange(entries, $"PATH 中已存在该目录（第 {existing.Index + 1} 位），未重复添加");
        }

        var newEntry = new PathEntry
        {
            Index = -1, // 占位，稍后重排时统一赋值
            Raw = directory.Trim(),
            Normalized = PathParser.Normalize(directory),
            ComparisonKey = key,
            Exists = SafeDirectoryExists(directory),
            Issues = PathIssueOf(directory),
        };

        var list = entries.ToList();
        if (position == PathPosition.Prepend)
        {
            list.Insert(0, newEntry);
        }
        else
        {
            list.Add(newEntry);
        }

        var after = Reindex(list);
        var toIndex = position == PathPosition.Prepend ? 0 : after.Count - 1;

        return new PathEditPlan
        {
            Before = entries,
            After = after,
            Changes =
            [
                new PathChange
                {
                    Kind = PathChangeKind.Added,
                    Value = newEntry.Raw,
                    Reason = position == PathPosition.Prepend
                        ? "添加到最前，优先于其他同名命令"
                        : "追加到末尾，不改变现有优先级",
                    ToIndex = toIndex,
                },
            ],
        };
    }

    /// <summary>
    /// 从 PATH 中移除指定目录。容忍大小写、短名、尾随分隔符差异。
    /// 目录不存在时视为已达成目标（幂等）。
    /// </summary>
    public static PathEditPlan Remove(IReadOnlyList<PathEntry> entries, string directory)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (string.IsNullOrWhiteSpace(directory))
        {
            return NoChange(entries, "目录为空，未做任何修改");
        }

        var key = PathParser.ComparisonKey(directory);
        var removed = entries.Where(e => string.Equals(e.ComparisonKey, key, StringComparison.OrdinalIgnoreCase)).ToList();

        if (removed.Count == 0)
        {
            return NoChange(entries, "PATH 中不存在该目录，无需移除");
        }

        var after = Reindex([.. entries.Where(e => !string.Equals(e.ComparisonKey, key, StringComparison.OrdinalIgnoreCase))]);

        return new PathEditPlan
        {
            Before = entries,
            After = after,
            Changes =
            [
                .. removed.Select(r => new PathChange
                {
                    Kind = PathChangeKind.Removed,
                    Value = r.Raw,
                    Reason = "已按操作移除",
                    FromIndex = r.Index,
                }),
            ],
        };
    }

    /// <summary>
    /// 去重：保留**首次出现**，移除后续重复项（PE-6）。
    /// </summary>
    public static PathEditPlan Dedupe(IReadOnlyList<PathEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var kept = new List<PathEntry>(entries.Count);
        var changes = new List<PathChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            // 空项不参与去重（由 Clean 负责移除），否则多个空项会被折叠成一个，掩盖问题
            if ((e.Issues & PathEntryIssue.Empty) != 0)
            {
                kept.Add(e);
                continue;
            }

            if (seen.Add(e.ComparisonKey))
            {
                kept.Add(e);
            }
            else
            {
                var first = kept.First(k => string.Equals(k.ComparisonKey, e.ComparisonKey, StringComparison.OrdinalIgnoreCase));
                changes.Add(new PathChange
                {
                    Kind = PathChangeKind.Removed,
                    Value = e.Raw,
                    Reason = $"与其他条目重复，保留第 {first.Index + 1} 位",
                    FromIndex = e.Index,
                });
            }
        }

        if (changes.Count == 0)
        {
            return NoChange(entries, "没有重复条目");
        }

        return new PathEditPlan
        {
            Before = entries,
            After = Reindex(kept),
            Changes = changes,
        };
    }

    /// <summary>
    /// 清理：移除空项与指向不存在目录的条目。
    /// <para>
    /// 刻意**不**移除"变量未解析"与"相对路径"项 —— 它们可能是用户有意为之
    /// （如 <c>%MY_TOOL_HOME%\bin</c> 在特定会话中才有效），必须交由用户判断。
    /// </para>
    /// </summary>
    /// <param name="probeFileSystem">
    /// 为 false 时不做存在性清理（避免在无法可靠探测的场景下误删）。
    /// </param>
    public static PathEditPlan Clean(IReadOnlyList<PathEntry> entries, bool probeFileSystem = true)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var kept = new List<PathEntry>(entries.Count);
        var changes = new List<PathChange>();

        foreach (var e in entries)
        {
            var reason = e.Issues switch
            {
                var f when (f & PathEntryIssue.Empty) != 0 => "空条目（由多余分号产生）",
                var f when probeFileSystem && (f & PathEntryIssue.Missing) != 0 => "目录不存在（疑似软件卸载残留）",
                var f when (f & PathEntryIssue.Duplicate) != 0 => "与其他条目重复",
                _ => null,
            };

            if (reason is null)
            {
                kept.Add(e);
            }
            else
            {
                changes.Add(new PathChange
                {
                    Kind = PathChangeKind.Removed,
                    Value = e.Raw,
                    Reason = reason,
                    FromIndex = e.Index,
                });
            }
        }

        if (changes.Count == 0)
        {
            return NoChange(entries, "没有可清理的条目");
        }

        return new PathEditPlan
        {
            Before = entries,
            After = Reindex(kept),
            Changes = changes,
        };
    }

    /// <summary>
    /// 把指定条目移动到指定位置，用于消解命令冲突（需求 7.3 节策略 S4）。
    /// </summary>
    public static PathEditPlan Prioritize(IReadOnlyList<PathEntry> entries, string directory, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var key = PathParser.ComparisonKey(directory);
        var current = entries.FirstOrDefault(e => string.Equals(e.ComparisonKey, key, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return NoChange(entries, "PATH 中不存在该目录，无法调整顺序");
        }

        var clamped = Math.Clamp(targetIndex, 0, entries.Count - 1);
        if (current.Index == clamped)
        {
            return NoChange(entries, $"该目录已在第 {clamped + 1} 位，无需移动");
        }

        var list = entries.ToList();
        list.RemoveAt(current.Index);
        list.Insert(clamped, current);
        var after = Reindex(list);

        return new PathEditPlan
        {
            Before = entries,
            After = after,
            Changes =
            [
                new PathChange
                {
                    Kind = PathChangeKind.Moved,
                    Value = current.Raw,
                    Reason = $"从第 {current.Index + 1} 位移到第 {clamped + 1} 位（改变同名命令的优先级）",
                    FromIndex = current.Index,
                    ToIndex = clamped,
                },
            ],
        };
    }

    /// <summary>
    /// 一次性生成"整理"方案：去重 + 清理，然后按需求 1.2 节的风险提示重新检查。
    /// 这是首页"整理 PATH"按钮背后的完整逻辑。
    /// </summary>
    public static PathEditPlan Tidy(IReadOnlyList<PathEntry> entries, bool probeFileSystem = true)
    {
        var deduped = Dedupe(entries);
        var cleaned = Clean(deduped.After, probeFileSystem);

        if (!deduped.HasChanges && !cleaned.HasChanges)
        {
            return NoChange(entries, "PATH 中已无失效条目");
        }

        return new PathEditPlan
        {
            Before = entries,
            After = cleaned.After,
            Changes = [.. deduped.Changes, .. cleaned.Changes],
        };
    }

    /// <summary>
    /// 生成"收敛到托管目录"的方案：把多个指向同一产品的条目折叠为单条托管 shims 目录。
    /// <para>
    /// 对应需求 M1-7 的"收敛到 shim 目录"建议，以及风险 T2（PATH 接近 2047 编辑上限）。
    /// </para>
    /// </summary>
    /// <param name="entries">当前条目。</param>
    /// <param name="shimDirectory">托管 shims 目录（将被前置）。</param>
    /// <param name="pathsToAbsorb">要被 shims 取代的目录（通常是各语言的 bin）。</param>
    public static PathEditPlan CollapseToShims(
        IReadOnlyList<PathEntry> entries, string shimDirectory, IEnumerable<string> pathsToAbsorb)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(pathsToAbsorb);

        if (string.IsNullOrWhiteSpace(shimDirectory))
        {
            return NoChange(entries, "托管目录为空，未做任何修改");
        }

        var absorbKeys = new HashSet<string>(
            pathsToAbsorb.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathParser.ComparisonKey),
            StringComparer.OrdinalIgnoreCase);

        var removed = entries.Where(e => absorbKeys.Contains(e.ComparisonKey)).ToList();
        if (removed.Count == 0)
        {
            return NoChange(entries, "没有可被托管目录取代的条目");
        }

        var kept = entries.Where(e => !absorbKeys.Contains(e.ComparisonKey)).ToList();
        var plan = Ensure(Reindex(kept), shimDirectory, PathPosition.Prepend);

        // 去重后 Ensure 可能没变化（托管目录本来就在），但仍需报告被吸收的条目。
        //
        // ★ 注意：C# 的集合表达式不支持嵌套推断 —— 外层 List<PathChange> 里再写
        //   ".. removed.Select(x => new PathChange { .. })" 时，编译器无法推断内层目标类型，
        //   会把它误判为 System.Index 而报 CS0029。因此这里改为先投影再拼接。
        var removedChanges = removed.Select(r => new PathChange
        {
            Kind = PathChangeKind.Removed,
            Value = r.Raw,
            Reason = "已被托管目录取代，命令仍可通过 shim 调用",
            FromIndex = r.Index,
        });

        var changes = new List<PathChange>(removedChanges);
        changes.AddRange(plan.Changes.Where(c => c.Kind == PathChangeKind.Added));

        return new PathEditPlan
        {
            Before = entries,
            After = plan.After,
            Changes = changes,
        };
    }

    // ────────────────────────── 内部辅助 ──────────────────────────

    /// <summary>重排下标，保证 <see cref="PathEntry.Index"/> 与数组位置一致。</summary>
    private static IReadOnlyList<PathEntry> Reindex(IEnumerable<PathEntry> entries)
    {
        var list = new List<PathEntry>();
        var i = 0;
        foreach (var e in entries)
        {
            list.Add(e with { Index = i++ });
        }

        return list;
    }

    private static PathEditPlan NoChange(IReadOnlyList<PathEntry> entries, string reason)
        => new()
        {
            Before = entries,
            After = entries,
            Changes = [],
            // 说明：即使无变化也把原因保留在摘要里，便于动作层写审计日志时说明"为何什么都没做"
            SummaryReason = reason,
        };

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(PathParser.Normalize(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static PathEntryIssue PathIssueOf(string directory)
    {
        var issues = PathEntryIssue.None;
        var normalized = PathParser.Normalize(directory);

        if (!Path.IsPathFullyQualified(normalized))
        {
            issues |= PathEntryIssue.Relative;
        }

        if (PathParser.HasTrailingSeparator(directory))
        {
            issues |= PathEntryIssue.TrailingSeparator;
        }

        return issues;
    }
}

/// <summary>插入位置。前置会让该目录里的命令优先被命中（用于消解命令冲突）。</summary>
public enum PathPosition
{
    Prepend = 0,
    Append = 1,
}
