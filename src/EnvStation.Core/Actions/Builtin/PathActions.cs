using System.Collections.Immutable;
using System.Globalization;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Transactions;
using EnvStation.Core.Environment;

namespace EnvStation.Core.Actions.Builtin;

// ══════════════════════════════════════════════════════════════════════════════
//  A06 PATH 专项（7 个动作）
//
//  PATH 是环境站唯一"写错就会让用户机器上系统命令集体失效"的地方
//  （《需求分析.md》1.2 节 P2 记录的正是这类事故）。因此这一组的共同纪律是：
//
//    · 永不拼接字符串 —— 一律"解析成条目 → 用 PathEditor 做列表操作 → 按原始写法拼回"
//      （用户的 %JAVA_HOME%\bin 必须原样保留，不能被展开成绝对路径，PE-4）；
//    · 永不截断 —— 超长一律拒绝并给出可操作的处置建议；
//    · 幂等 —— 已经满足条件时不做任何修改，也不产生无意义的快照噪音；
//    · 默认追加而不是插到最前 —— 插入最前会抢走同名命令的优先级（D-5 的教训）。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>PATH 动作的公共基类：读取、写回、变更描述都收在一处。</summary>
internal abstract class PathActionBase : EnvActionBase
{
    /// <summary>读取 PATH，并做必要的前置检查。</summary>
    protected static Result<PathSnapshot> ReadPath(ActionExecutionContext context, EnvScope scope)
    {
        var environment = RequireEnvironment(context);
        return environment.IsFailure
            ? Result<PathSnapshot>.Fail(environment.Error)
            : environment.Value.ReadPath(scope);
    }

    /// <summary>把编辑结果写回；无变化时直接返回"未修改"。</summary>
    protected static Result<ActionResult> ApplyPlan(
        ActionExecutionContext context,
        EnvScope scope,
        PathEditPlan plan,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.HasChanges)
        {
            return Result<ActionResult>.Ok(ActionResult.Ok(
                plan.Summarize(),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["changed"] = "false",
                    ["scope"] = EnvironmentPathResolver.ToScopeName(scope),
                    ["summary"] = plan.Summarize(),
                    ["entry_count"] = plan.After.Count.ToString(CultureInfo.InvariantCulture),
                }));
        }

        var written = context.Environment!.WritePath(scope, plan.After, RiskOf(scope), operation);
        if (written.IsFailure)
        {
            return Result<ActionResult>.Fail(written.Error);
        }

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["changed"] = "true",
            ["scope"] = EnvironmentPathResolver.ToScopeName(scope),
            ["summary"] = plan.Summarize(),
            ["change_count"] = plan.Changes.Count.ToString(CultureInfo.InvariantCulture),
            ["entry_count_before"] = plan.Before.Count.ToString(CultureInfo.InvariantCulture),
            ["entry_count_after"] = plan.After.Count.ToString(CultureInfo.InvariantCulture),
            ["changes"] = string.Join(" || ", plan.Changes.Select(static c => $"{c.Kind}:{c.Value}（{c.Reason}）")),
            ["snapshot_id"] = written.Value.SnapshotId,
        };

        var message = plan.Summarize();
        if (written.Value.Note is { Length: > 0 } note)
        {
            message += " " + note;
        }

        return Result<ActionResult>.Ok(ActionResult.Ok(
            message, outputs, reversibleToken: written.Value.SnapshotId));
    }

    /// <summary>供 <c>path.validate</c> 与报告使用的问题描述。</summary>
    protected static string DescribeIssues(PathEntryIssue issues)
    {
        if (issues == PathEntryIssue.None)
        {
            return "正常";
        }

        var parts = new List<string>(4);
        if (issues.HasFlag(PathEntryIssue.Empty))
        {
            parts.Add("空条目（会让系统在当前目录查找程序，存在安全风险）");
        }

        if (issues.HasFlag(PathEntryIssue.Missing))
        {
            parts.Add("目录不存在（多为软件卸载残留）");
        }

        if (issues.HasFlag(PathEntryIssue.Duplicate))
        {
            parts.Add("与其他条目重复");
        }

        if (issues.HasFlag(PathEntryIssue.Relative))
        {
            parts.Add("相对路径（其含义取决于当前目录，不可靠）");
        }

        if (issues.HasFlag(PathEntryIssue.UnresolvedVariable))
        {
            parts.Add("引用的环境变量未定义或 % 未配对");
        }

        if (issues.HasFlag(PathEntryIssue.InvalidCharacters))
        {
            parts.Add("含非法字符");
        }

        if (issues.HasFlag(PathEntryIssue.TrailingSeparator))
        {
            parts.Add("以分隔符结尾（多数工具可容忍，个别工具会因此判定失败）");
        }

        return string.Join("、", parts);
    }
}

/// <summary><c>envstation.path.ensure</c>：确保某目录存在于 PATH。</summary>
internal sealed class PathEnsureAction : PathActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.path.ensure",
        "1.0.0",
        CapabilityIds.PathModify,
        "确保 PATH 包含目录",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["registry.write", "filesystem.write"],
        Parameters:
        [
            ScopeParameter,
            Str("entry", true, "要确保存在的目录。可写 %VAR% 形式（会原样保留）", maxLength: 512),
            new ParameterSpec("position", ParameterType.Enum, false,
                "插入位置。append = 追加到末尾（默认，不改变现有优先级）；prepend = 插到最前（会抢走同名命令的优先级）",
                AllowedValues: ["append", "prepend"]),
        ],
        ConditionalCapabilities: [CapabilityIds.EnvironmentMachine]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = ParseScope(arguments);
        var capability = RequireMachineScopeCapability(context, scope);
        if (capability.IsFailure)
        {
            return FailFrom(capability.Error);
        }

        var entry = arguments.GetString("entry")!;
        var positionText = arguments.GetString("position") ?? "append";

        var path = ReadPath(context, scope);
        if (path.IsFailure)
        {
            return FailFrom(path.Error);
        }

        // 相对路径与非法字符必须在校验期拒绝：写进 PATH 之后排查起来非常困难。
        if (!Path.IsPathFullyQualified(System.Environment.ExpandEnvironmentVariables(entry))
            && !entry.Contains('%', StringComparison.Ordinal))
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"PATH 条目 {entry} 不是绝对路径。",
                "改用完整路径（如 D:\\Dev\\Python\\Scripts），或用 %VAR% 形式引用环境变量。相对路径的含义取决于当前目录，会产生难以排查的问题。");
        }

        var position = positionText == "prepend" ? PathPosition.Prepend : PathPosition.Append;
        var plan = PathEditor.Ensure(path.Value.Entries, entry, position);

        var applied = ApplyPlan(
            context, scope, plan, $"path.ensure {EnvironmentPathResolver.ToScopeName(scope)} {entry}");
        if (applied.IsFailure)
        {
            return FailFrom(applied.Error);
        }

        return ValueTask.FromResult(applied.Value);
    }
}

/// <summary><c>envstation.path.remove</c>：移除 PATH 中某项。</summary>
internal sealed class PathRemoveAction : PathActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.path.remove",
        "1.0.0",
        CapabilityIds.PathModify,
        "移除 PATH 条目",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["registry.write", "filesystem.write"],
        Parameters:
        [
            ScopeParameter,
            Str("entry", true, "要移除的目录（大小写与首尾分隔符容错）", maxLength: 512),
        ],
        ConditionalCapabilities: [CapabilityIds.EnvironmentMachine]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = ParseScope(arguments);
        var capability = RequireMachineScopeCapability(context, scope);
        if (capability.IsFailure)
        {
            return FailFrom(capability.Error);
        }

        var entry = arguments.GetString("entry")!;

        var path = ReadPath(context, scope);
        if (path.IsFailure)
        {
            return FailFrom(path.Error);
        }

        var plan = PathEditor.Remove(path.Value.Entries, entry);
        var applied = ApplyPlan(
            context, scope, plan, $"path.remove {EnvironmentPathResolver.ToScopeName(scope)} {entry}");
        if (applied.IsFailure)
        {
            return FailFrom(applied.Error);
        }

        return ValueTask.FromResult(applied.Value);
    }
}

/// <summary><c>envstation.path.dedupe</c>：去重（保留首次出现）。</summary>
internal sealed class PathDedupeAction : PathActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.path.dedupe",
        "1.0.0",
        CapabilityIds.PathModify,
        "PATH 去重",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["registry.write", "filesystem.write"],
        Parameters: [ScopeParameter],
        ConditionalCapabilities: [CapabilityIds.EnvironmentMachine]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = ParseScope(arguments);
        var capability = RequireMachineScopeCapability(context, scope);
        if (capability.IsFailure)
        {
            return FailFrom(capability.Error);
        }

        var path = ReadPath(context, scope);
        if (path.IsFailure)
        {
            return FailFrom(path.Error);
        }

        var plan = PathEditor.Dedupe(path.Value.Entries);
        var applied = ApplyPlan(context, scope, plan, $"path.dedupe {EnvironmentPathResolver.ToScopeName(scope)}");
        if (applied.IsFailure)
        {
            return FailFrom(applied.Error);
        }

        return ValueTask.FromResult(applied.Value);
    }
}

/// <summary><c>envstation.path.prioritize</c>：把指定项移动到指定位置（用于消解命令冲突）。</summary>
internal sealed class PathPrioritizeAction : PathActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.path.prioritize",
        "1.0.0",
        CapabilityIds.PathModify,
        "调整 PATH 优先级",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["registry.write", "filesystem.write"],
        Parameters:
        [
            ScopeParameter,
            Str("entry", true, "要移动的目录", maxLength: 512),
            new ParameterSpec("index", ParameterType.Integer, true,
                "目标位置（从 0 开始）。0 表示插到最前，使其优先于其他同名命令",
                Minimum: 0, Maximum: 10000),
        ],
        ConditionalCapabilities: [CapabilityIds.EnvironmentMachine]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = ParseScope(arguments);
        var capability = RequireMachineScopeCapability(context, scope);
        if (capability.IsFailure)
        {
            return FailFrom(capability.Error);
        }

        var entry = arguments.GetString("entry")!;
        var index = (int)arguments.GetInt64("index");

        var path = ReadPath(context, scope);
        if (path.IsFailure)
        {
            return FailFrom(path.Error);
        }

        if (index >= path.Value.Entries.Count)
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"目标位置 {index} 超出 PATH 条目数（共 {path.Value.Entries.Count} 项）。",
                $"合法范围是 0 到 {path.Value.Entries.Count - 1}。");
        }

        var plan = PathEditor.Prioritize(path.Value.Entries, entry, index);
        var applied = ApplyPlan(
            context, scope, plan, $"path.prioritize {EnvironmentPathResolver.ToScopeName(scope)} {entry} -> {index}");
        if (applied.IsFailure)
        {
            return FailFrom(applied.Error);
        }

        return ValueTask.FromResult(applied.Value);
    }
}

/// <summary><c>envstation.path.clean</c>：移除不存在目录、空项、多余分隔符。</summary>
internal sealed class PathCleanAction : PathActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.path.clean",
        "1.0.0",
        CapabilityIds.PathModify,
        "清理 PATH 失效项",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 120,
        TouchedResources: ["registry.write", "filesystem.write", "filesystem.read"],
        Parameters:
        [
            ScopeParameter,
            Bool("include_duplicates", "是否同时移除重复项", true),
            Bool("dry_run", "只报告将要移除的内容，不做修改", false),
        ],
        ConditionalCapabilities: [CapabilityIds.EnvironmentMachine]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = ParseScope(arguments);
        var capability = RequireMachineScopeCapability(context, scope);
        if (capability.IsFailure)
        {
            return FailFrom(capability.Error);
        }

        var includeDuplicates = arguments.GetBoolean("include_duplicates", true);
        var dryRun = arguments.GetBoolean("dry_run", false);

        var path = ReadPath(context, scope);
        if (path.IsFailure)
        {
            return FailFrom(path.Error);
        }

        var plan = includeDuplicates
            ? PathEditor.Tidy(path.Value.Entries)
            : PathEditor.Clean(path.Value.Entries);

        if (dryRun)
        {
            var wouldRemove = plan.Changes
                .Where(static c => c.Kind == PathChangeKind.Removed)
                .Select(static c => $"{c.Value}（{c.Reason}）")
                .ToArray();

            return Ok(
                wouldRemove.Length == 0
                    ? "预演结果：PATH 中没有需要清理的条目。"
                    : $"预演结果：将移除 {wouldRemove.Length} 项 —— {string.Join("、", wouldRemove)}（未做任何修改）",
                Outputs(
                    ("changed", "false"),
                    ("dry_run", "true"),
                    ("would_remove_count", wouldRemove.Length.ToString(CultureInfo.InvariantCulture)),
                    ("would_remove", string.Join(" || ", wouldRemove)),
                    ("summary", plan.Summarize())));
        }

        var applied = ApplyPlan(context, scope, plan, $"path.clean {EnvironmentPathResolver.ToScopeName(scope)}");
        if (applied.IsFailure)
        {
            return FailFrom(applied.Error);
        }

        return ValueTask.FromResult(applied.Value);
    }
}

/// <summary><c>envstation.path.validate</c>：校验 PATH 项合法性（只读）。</summary>
internal sealed class PathValidateAction : PathActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.path.validate",
        "1.0.0",
        CapabilityIds.Inspect,
        "检测 PATH",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 120,
        TouchedResources: ["registry.read", "filesystem.read"],
        Parameters: [ScopeParameter]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = ParseScope(arguments);

        var path = ReadPath(context, scope);
        if (path.IsFailure)
        {
            return FailFrom(path.Error);
        }

        var entries = path.Value.Entries;
        var unhealthy = entries.Where(static e => !e.IsHealthy).ToArray();
        var missing = entries.Count(static e => e.Issues.HasFlag(PathEntryIssue.Missing));
        var duplicates = entries.Count(static e => e.Issues.HasFlag(PathEntryIssue.Duplicate));
        var empty = entries.Count(static e => e.Raw.Length == 0 || e.IsCurrentDirectory);
        var unresolved = entries.Count(static e => e.Issues.HasFlag(PathEntryIssue.UnresolvedVariable));

        var details = unhealthy
            .Take(50)
            .Select(e => $"{e.Raw} —— {DescribeIssues(e.Issues)}")
            .ToArray();

        var scopeName = EnvironmentPathResolver.ToScopeName(scope);
        var scopeLabel = EnvironmentPathResolver.ToScopeLabel(scope);
        var message = entries.Count == 0
            ? $"{scopeLabel} PATH 为空或不存在。"
            : $"{scopeLabel} PATH 共 {entries.Count} 项，其中 {unhealthy.Length} 项异常" +
              $"（不存在 {missing}、重复 {duplicates}、空条目 {empty}、变量未解析 {unresolved}）。";

        if (empty > 0)
        {
            message += " 警告：存在空条目，会让系统在当前工作目录查找可执行文件，属于安全风险，立即清理。";
        }

        return Ok(
            message,
            Outputs(
                ("scope", scopeName),
                ("entry_count", entries.Count.ToString(CultureInfo.InvariantCulture)),
                ("problem_count", unhealthy.Length.ToString(CultureInfo.InvariantCulture)),
                ("missing_count", missing.ToString(CultureInfo.InvariantCulture)),
                ("duplicate_count", duplicates.ToString(CultureInfo.InvariantCulture)),
                ("empty_count", empty.ToString(CultureInfo.InvariantCulture)),
                ("unresolved_count", unresolved.ToString(CultureInfo.InvariantCulture)),
                ("length", path.Value.RawValue.Length.ToString(CultureInfo.InvariantCulture)),
                ("over_legacy_limit", Bool(path.Value.RawValue.Length > RegistryEnvStore.LegacyEditorLimit)),
                ("details", string.Join(" || ", details))));
    }
}

/// <summary><c>envstation.path.ensure_shim</c>：确保托管 shim 目录在 PATH 中且位置正确。</summary>
internal sealed class PathEnsureShimAction : PathActionBase
{
    /// <summary>托管的 shim 目录名（位于用户的本地应用数据目录下）。</summary>
    internal const string ShimDirectoryName = "shims";

    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.path.ensure_shim",
        "1.0.0",
        CapabilityIds.PathModify,
        "确保托管目录在 PATH 中",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["registry.write", "filesystem.write"],
        Parameters:
        [
            ScopeParameter,
            Path_("shim_dir", false, "托管目录的完整路径；省略时使用环境站默认的 shims 目录"),
            new ParameterSpec("position", ParameterType.Enum, false,
                "插入位置。shim 目录用于接管版本切换，通常需要靠前",
                AllowedValues: ["append", "prepend"]),
        ],
        ConditionalCapabilities: [CapabilityIds.EnvironmentMachine]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = ParseScope(arguments);
        var capability = RequireMachineScopeCapability(context, scope);
        if (capability.IsFailure)
        {
            return FailFrom(capability.Error);
        }

        var shimDir = arguments.GetString("shim_dir");
        if (string.IsNullOrWhiteSpace(shimDir))
        {
            shimDir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "EnvStation",
                ShimDirectoryName);
        }

        if (!Path.IsPathFullyQualified(shimDir))
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"托管目录必须是绝对路径，实际为 {shimDir}。");
        }

        var path = ReadPath(context, scope);
        if (path.IsFailure)
        {
            return FailFrom(path.Error);
        }

        // 默认靠前：shim 目录存在的意义就是"接管同名命令"，追加到末尾会失去作用。
        // 这与 path.ensure 的默认相反，是刻意的——两者的语义不同：
        // ensure 是"让某个目录可用"，ensure_shim 是"让托管层优先接管"。
        var positionText = arguments.GetString("position") ?? "prepend";
        var position = positionText == "prepend" ? PathPosition.Prepend : PathPosition.Append;

        var plan = PathEditor.Ensure(path.Value.Entries, shimDir, position);

        // shim 目录可能尚不存在：这里只保证 PATH 里有一项，目录本身的创建由安装动作负责。
        var applied = ApplyPlan(
            context, scope, plan, $"path.ensure_shim {EnvironmentPathResolver.ToScopeName(scope)}");
        if (applied.IsFailure)
        {
            return FailFrom(applied.Error);
        }

        var result = applied.Value;
        return ValueTask.FromResult(result with
        {
            Outputs = result.Outputs
                .SetItem("shim_dir", shimDir)
                .SetItem("position", positionText)
                .SetItem("dir_exists", Directory.Exists(shimDir) ? "true" : "false"),
        });
    }
}
