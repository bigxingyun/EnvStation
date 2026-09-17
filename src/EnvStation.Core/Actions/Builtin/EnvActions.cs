using System.Collections.Immutable;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Transactions;
using EnvStation.Core.Environment;

namespace EnvStation.Core.Actions.Builtin;

// ══════════════════════════════════════════════════════════════════════════════
//  A05 环境变量（7 个动作）
//
//  这一组是整个产品"最危险"的地方：它真的会改用户的注册表。
//  因此纪律比别处更严：
//    · 任何写入前必须成功创建快照（SEC-1），拿不到快照就拒绝写入；
//    · 值类型必须保持（SEC-11），不能把 REG_EXPAND_SZ 悄悄改成 REG_SZ；
//    · 跨作用域写入需要额外能力（machine 需要 CAP.ENV.MACHINE），未授权即拒绝；
//    · 所有变更记录逆向信息，可一键回滚。
//
//  这些约束都由 IEnvironmentOperations 统一保证——动作本身只负责参数校验与语义判断，
//  绝不直接接触注册表。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>环境变量动作的公共基类：把作用域参数解析、能力校验、失败转译收在一处。</summary>
internal abstract class EnvActionBase : ActionBase
{
    /// <summary>作用域参数规格（所有 A05/A06 动作共用同一套取值）。</summary>
    protected static ParameterSpec ScopeParameter { get; } = new(
        "scope", ParameterType.Enum, true,
        "作用域。user = 仅影响当前用户（无需管理员）；machine = 影响本机所有用户（需要管理员权限）。",
        AllowedValues: ["user", "machine"]);

    /// <summary>解析作用域参数。</summary>
    protected static EnvScope ParseScope(ActionArguments arguments) =>
        arguments.GetString("scope") == "machine" ? EnvScope.Machine : EnvScope.User;

    /// <summary>
    /// 取出环境操作门面；没有则明确失败。
    /// </summary>
    /// <remarks>
    /// 这是"真机安全"的最后一道闸：执行上下文里没有注入 <see cref="IEnvironmentOperations"/> 时
    /// （例如预演、静态分析、单元测试），环境变量动作会明确报错，
    /// <b>而不是退化成直接写真实注册表</b>。
    /// </remarks>
    protected static Result<IEnvironmentOperations> RequireEnvironment(ActionExecutionContext context)
    {
        if (context.Environment is null)
        {
            return Result<IEnvironmentOperations>.Fail(
                EnvStationErrorCodes.ActionFailed,
                "本次运行没有启用环境变量写入能力（未注入环境操作实现）。",
                "预演下属于正常现象。正式运行时出现该提示请提交反馈。");
        }

        return Result<IEnvironmentOperations>.Ok(context.Environment);
    }

    /// <summary>
    /// 校验写系统级所需的附加能力（需求 A2：未授权即拒绝，不是跳过）。
    /// </summary>
    protected static Result<Unit> RequireMachineScopeCapability(ActionExecutionContext context, EnvScope scope)
    {
        if (scope != EnvScope.Machine)
        {
            return Results.Ok();
        }

        if (context.GrantedCapabilities.Contains(CapabilityIds.EnvironmentMachine))
        {
            return Results.Ok();
        }

        return Result<Unit>.Fail(
            EnvStationErrorCodes.CapabilityDenied,
            $"动作要求修改系统级变量，但本次运行未获得 {CapabilityIds.EnvironmentMachine} 授权。",
            "系统级变量会影响本机所有用户，属于高风险变更。在能力授权中勾选「修改系统环境变量」后重试，或改用用户级。");
    }

    /// <summary>把领域错误直接翻成动作失败。</summary>
    protected static ValueTask<ActionResult> FailFrom(EnvStationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = error.Remediation is null ? error.Message : error.Message + " " + error.Remediation;
        return ValueTask.FromResult(ActionResult.Fail(error.Code, message));
    }

    /// <summary>风险等级：系统级一律 High，用户级为 Reversible（可逆变更）。</summary>
    protected static RiskLevel RiskOf(EnvScope scope) =>
        scope == EnvScope.Machine ? RiskLevel.High : RiskLevel.Reversible;
}

/// <summary><c>envstation.env.backup</c>：全量快照并返回快照 ID（写入前强制调用）。</summary>
internal sealed class EnvBackupAction : EnvActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.env.backup",
        "1.0.0",
        CapabilityIds.Inspect,
        "备份环境变量",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["registry.read", "filesystem.write"],
        Parameters:
        [
            ScopeParameter,
            Str("note", false, "备注，会显示在快照时间线上", maxLength: 200),
            Bool("manual", "标记为手动快照（不会被自动清理策略删除）", true),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var environment = RequireEnvironment(context);
        if (environment.IsFailure)
        {
            return FailFrom(environment.Error);
        }

        var scope = ParseScope(arguments);
        var note = arguments.GetString("note");
        var isManual = arguments.GetBoolean("manual", true);

        var snapshot = environment.Value.CaptureSnapshot($"包 {context.PackageId} 请求备份", note, isManual);
        if (snapshot.IsFailure)
        {
            return FailFrom(snapshot.Error);
        }

        var value = snapshot.Value;
        context.ReportProgress(100, $"已创建快照 {value.SnapshotId}");

        return Ok(
            $"已备份环境变量（快照 {value.SnapshotId}）：用户级 {value.UserVariables.Count} 项，" +
            $"系统级 {(value.MachineScopeReadFailed ? "读取失败（权限受限）" : value.MachineVariables.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 项")}。",
            Outputs(
                ("snapshot_id", value.SnapshotId),
                ("created_at", value.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                ("user_count", value.UserVariables.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("machine_count", value.MachineVariables.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("machine_read_failed", value.MachineScopeReadFailed ? "true" : "false"),
                ("scope", EnvironmentPathResolver.ToScopeName(scope))),
            reversibleToken: value.SnapshotId);
    }
}

/// <summary><c>envstation.env.set</c>：事务化设置变量；保持原值类型；可选备份；拒绝非法名。</summary>
internal sealed class EnvSetAction : EnvActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.env.set",
        "1.0.0",
        CapabilityIds.EnvironmentUser,
        "设置环境变量",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["registry.write", "filesystem.write"],
        Parameters:
        [
            ScopeParameter,
            Str("name", true, "变量名（不含 % 与 =）", maxLength: 255),
            Str("value", true, "变量值（未展开的原始写法，可含 %OTHER_VAR%）", maxLength: 32000),
            Enum("value_kind", false, "值类型。auto = 沿用原有类型（原来不存在则用 expand）",
                "auto", "string", "expand"),
            Bool("backup", "写入前是否创建快照。默认 true；设为 false 只应在同一事务内连续写入时使用", true),
            Bool("overwrite", "目标变量已存在且值不同时是否覆盖。默认 true", true),
            Bool("allow_legacy_length", "是否允许值长度超过旧版环境变量编辑对话框的 2047 字符上限", false),
        ],
        ConditionalCapabilities: [CapabilityIds.EnvironmentMachine]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var environment = RequireEnvironment(context);
        if (environment.IsFailure)
        {
            return FailFrom(environment.Error);
        }

        var scope = ParseScope(arguments);
        var capability = RequireMachineScopeCapability(context, scope);
        if (capability.IsFailure)
        {
            return FailFrom(capability.Error);
        }

        var name = arguments.GetString("name")!;
        var rawValue = arguments.GetString("value")!;
        var allowLegacyLength = arguments.GetBoolean("allow_legacy_length", false);
        var overwrite = arguments.GetBoolean("overwrite", true);

        // ① 名称与值合法性（复用注册表存储层的校验，保证与真实写入条件完全一致）
        var validation = RegistryEnvStore.ValidateNameAndValue(name, rawValue);
        if (validation.IsFailure)
        {
            return FailFrom(validation.Error);
        }

        // ② 旧版编辑对话框长度陷阱：超过 2047 后，用户一旦用系统自带对话框改 PATH 就会被截断。
        //    这里不硬性阻断（有正当场景），但必须让调用方显式确认，否则默认为拒绝。
        if (!allowLegacyLength
            && rawValue.Length > RegistryEnvStore.LegacyEditorLimit
            && name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                EnvStationErrorCodes.EnvValueTooLong,
                $"新的 PATH 长度为 {rawValue.Length} 字符，超过系统「环境变量」编辑对话框的 {RegistryEnvStore.LegacyEditorLimit} 字符上限。" +
                "用该对话框编辑过 PATH 后，超出部分会被系统静默截断并永久丢失。" +
                "确认继续时把 allow_legacy_length 设为 true。");
        }

        // ③ 读取现值：用于幂等判断与值类型推断
        var current = environment.Value.Read(scope, name);
        if (current.IsFailure)
        {
            return FailFrom(current.Error);
        }

        if (current.Value is { } existing
            && string.Equals(existing.RawValue, rawValue, StringComparison.Ordinal))
        {
            // 幂等：值完全相同就什么都不做，避免产生无意义的快照与"变更记录"噪音。
            return Ok(
                $"{name} 的值已是目标值，无需修改。",
                Outputs(
                    ("changed", "false"),
                    ("name", name),
                    ("scope", EnvironmentPathResolver.ToScopeName(scope)),
                    ("kind", existing.Kind.ToString())),
                reversibleToken: null);
        }

        if (current.Value is not null && !overwrite)
        {
            return Fail(
                EnvStationErrorCodes.EnvScopeDenied,
                $"{name} 已存在（当前值：{Truncate(existing: current.Value.RawValue)}），且 overwrite 为 false。",
                "需要覆盖时把 overwrite 设为 true；只需确保值存在时，改动当前值需人工确认。");
        }

        var kindSpec = arguments.GetString("value_kind") ?? "auto";
        EnvValueKind? kind = kindSpec switch
        {
            "string" => EnvValueKind.String,
            "expand" => EnvValueKind.ExpandString,
            _ => null,
        };

        var operation = $"env.set {EnvironmentPathResolver.ToScopeName(scope)} {name}";

        if (!arguments.GetBoolean("backup", true))
        {
            // backup=false 意味着"调用方已经在同一批操作里建过快照"。
            // 我们不信任这个声明，而是仍然让 SetVariable 走完整事务（它内部必然建快照）——
            // 因此这里的 backup=false 只影响"是否额外再建一份手动快照"。
            context.Audit("env.set.no_extra_backup", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["scope"] = EnvironmentPathResolver.ToScopeName(scope),
            });
        }

        var written = environment.Value.SetVariable(scope, name, rawValue, kind, RiskOf(scope), operation);
        if (written.IsFailure)
        {
            return FailFrom(written.Error);
        }

        var oldText = current.Value is null ? "（原本不存在）" : Truncate(existing: current.Value.RawValue);
        var newKind = kind ?? current.Value?.Kind ?? EnvValueKind.ExpandString;

        return Ok(
            $"已设置 {EnvironmentPathResolver.ToScopeLabel(scope)}变量 {name}：" +
            $"{oldText} → {Truncate(existing: rawValue)}（值类型 {newKind}）。",
            Outputs(
                ("changed", "true"),
                ("name", name),
                ("scope", EnvironmentPathResolver.ToScopeName(scope)),
                ("old_value", current.Value?.RawValue ?? string.Empty),
                ("new_value", rawValue),
                ("kind", newKind.ToString()),
                ("snapshot_id", written.Value.SnapshotId)),
            reversibleToken: written.Value.SnapshotId);
    }

    private static string Truncate(string existing) =>
        existing.Length <= 160 ? existing : existing[..160] + "…";
}

/// <summary><c>envstation.env.unset</c>：删除变量（含快照）。</summary>
internal sealed class EnvUnsetAction : EnvActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.env.unset",
        "1.0.0",
        CapabilityIds.EnvironmentUser,
        "删除环境变量",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["registry.write", "filesystem.write"],
        Parameters:
        [
            ScopeParameter,
            Str("name", true, "变量名", maxLength: 255),
            Bool("backup", "删除前是否创建快照。默认 true", true),
            Str("expect_value", false, "仅当当前值等于该值时才删除（用于避免误删被其他工具改过的变量）", maxLength: 32000),
        ],
        ConditionalCapabilities: [CapabilityIds.EnvironmentMachine]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var environment = RequireEnvironment(context);
        if (environment.IsFailure)
        {
            return FailFrom(environment.Error);
        }

        var scope = ParseScope(arguments);
        var capability = RequireMachineScopeCapability(context, scope);
        if (capability.IsFailure)
        {
            return FailFrom(capability.Error);
        }

        var name = arguments.GetString("name")!;
        var expect = arguments.GetString("expect_value");

        var current = environment.Value.Read(scope, name);
        if (current.IsFailure)
        {
            return FailFrom(current.Error);
        }

        if (current.Value is null)
        {
            // 幂等：不存在即视为已达成目标
            return Ok(
                $"{name} 本来就不存在，无需删除。",
                Outputs(("changed", "false"), ("name", name), ("scope", EnvironmentPathResolver.ToScopeName(scope))));
        }

        if (expect is not null && !string.Equals(current.Value.RawValue, expect, StringComparison.Ordinal))
        {
            return Fail(
                EnvStationErrorCodes.EnvScopeDenied,
                $"{name} 的当前值与 expect_value 不一致，已中止删除。" +
                $"当前值：{Truncate(current.Value.RawValue)}",
                "该变量可能被其他工具修改过，确认后再删除。");
        }

        var removed = environment.Value.RemoveVariable(
            scope, name, RiskOf(scope), $"env.unset {EnvironmentPathResolver.ToScopeName(scope)} {name}");

        if (removed.IsFailure)
        {
            return FailFrom(removed.Error);
        }

        return Ok(
            $"已删除 {EnvironmentPathResolver.ToScopeLabel(scope)}变量 {name}（原值：{Truncate(current.Value.RawValue)}）。",
            Outputs(
                ("changed", "true"),
                ("name", name),
                ("scope", EnvironmentPathResolver.ToScopeName(scope)),
                ("old_value", current.Value.RawValue),
                ("snapshot_id", removed.Value.SnapshotId)),
            reversibleToken: removed.Value.SnapshotId);
    }

    private static string Truncate(string value) => value.Length <= 160 ? value : value[..160] + "…";
}

/// <summary><c>envstation.env.get</c>：读取变量及来源层级。</summary>
internal sealed class EnvGetAction : EnvActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.env.get",
        "1.0.0",
        CapabilityIds.Inspect,
        "读取环境变量",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 20,
        TouchedResources: ["registry.read"],
        Parameters:
        [
            Str("name", true, "变量名", maxLength: 255),
            new ParameterSpec("scope", ParameterType.Enum, false,
                "作用域。both = 同时读取用户级与系统级，并说明哪一级生效",
                AllowedValues: ["user", "machine", "both"]),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var environment = RequireEnvironment(context);
        if (environment.IsFailure)
        {
            return FailFrom(environment.Error);
        }

        var name = arguments.GetString("name")!;
        var scopeSpec = arguments.GetString("scope") ?? "both";
        var scopes = scopeSpec switch
        {
            "user" => new[] { EnvScope.User },
            "machine" => new[] { EnvScope.Machine },
            _ => new[] { EnvScope.User, EnvScope.Machine },
        };

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var lines = ImmutableArray.CreateBuilder<string>();
        var effective = string.Empty;
        var effectiveScope = string.Empty;
        var effectiveScopeLabel = string.Empty;
        var found = false;

        foreach (var scope in scopes)
        {
            var scopeName = EnvironmentPathResolver.ToScopeName(scope);      // 输出字段键用机器标识
            var scopeLabel = EnvironmentPathResolver.ToScopeLabel(scope);   // 消息用中文标签
            var read = environment.Value.Read(scope, name);
            if (read.IsFailure)
            {
                lines.Add($"[{scopeLabel}] 读取失败：{read.Error.Message}");
                continue;
            }

            if (read.Value is null)
            {
                lines.Add($"[{scopeLabel}] 未定义");
                builder[$"{scopeName}.value"] = string.Empty;
                continue;
            }

            found = true;
            builder[$"{scopeName}.value"] = read.Value.RawValue;
            builder[$"{scopeName}.kind"] = read.Value.Kind.ToString();
            builder[$"{scopeName}.has_reference"] = read.Value.ContainsVariableReference ? "true" : "false";
            lines.Add($"[{scopeLabel}] {read.Value.RawValue}（{read.Value.Kind}）");

            // 生效规则：进程环境是"用户级覆盖系统级"（用户级同名变量优先）。
            if (effective.Length == 0 || scope == EnvScope.User)
            {
                effective = read.Value.RawValue;
                effectiveScope = scopeName;          // 输出字段 effective_scope 保持机器标识（user / machine）
                effectiveScopeLabel = scopeLabel;    // 消息里的层级名用中文标签
            }
        }

        builder["found"] = found ? "true" : "false";
        builder["effective_value"] = effective;
        builder["effective_scope"] = effectiveScope;
        builder["details"] = string.Join(" || ", lines);

        var message = found
            ? $"{name} = {Truncate(effective)}（{effectiveScopeLabel}生效）"
            : $"{name} 未定义。";

        return Ok(message, builder.ToImmutable());
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "…";
}

/// <summary><c>envstation.env.diff</c>：对比快照与当前状态。</summary>
internal sealed class EnvDiffAction : EnvActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.env.diff",
        "1.0.0",
        CapabilityIds.Inspect,
        "对比环境变量快照",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["registry.read", "filesystem.read"],
        Parameters:
        [
            Str("snapshot_id", true, "快照 ID；也可填 latest 表示最近一次快照", maxLength: 128),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var environment = RequireEnvironment(context);
        if (environment.IsFailure)
        {
            return FailFrom(environment.Error);
        }

        var snapshotId = arguments.GetString("snapshot_id")!;
        if (snapshotId == "latest")
        {
            var list = environment.Value.ListSnapshots();
            if (list.IsFailure)
            {
                return FailFrom(list.Error);
            }

            if (list.Value.Count == 0)
            {
                return Fail(EnvStationErrorCodes.TxSnapshotInvalid, "本机还没有任何快照。");
            }

            snapshotId = list.Value[0].SnapshotId;
        }

        var snapshot = environment.Value.LoadSnapshot(snapshotId);
        if (snapshot.IsFailure)
        {
            return FailFrom(snapshot.Error);
        }

        var diffs = EnvironmentDiffer.Compare(environment.Value, snapshot.Value, out var readErrors);

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        builder["snapshot_id"] = snapshotId;
        builder["diff_count"] = diffs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        builder["read_errors"] = string.Join(" | ", readErrors);
        builder["details"] = string.Join(" || ", diffs.Select(static d => d.Describe()));

        var message = diffs.Count == 0
            ? $"当前环境与快照 {snapshotId} 完全一致，没有差异。"
            : $"当前环境与快照 {snapshotId} 有 {diffs.Count} 处差异：{string.Join("、", diffs.Take(5).Select(static d => d.Describe()))}" +
              (diffs.Count > 5 ? $" 等 {diffs.Count} 处。" : "。");

        return Ok(message, builder.ToImmutable());
    }
}

/// <summary><c>envstation.env.restore</c>：从快照还原（支持单变量或全量）。</summary>
internal sealed class EnvRestoreAction : EnvActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.env.restore",
        "1.0.0",
        CapabilityIds.EnvironmentUser,
        "从快照回滚环境变量",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 120,
        TouchedResources: ["registry.write", "filesystem.write", "filesystem.read"],
        Parameters:
        [
            Str("snapshot_id", true, "快照 ID；也可填 latest", maxLength: 128),
            StrArray("names", false, "只回滚这些变量；留空表示全部回滚"),
            Bool("allow_machine", "是否允许回滚系统级变量（会修改本机所有用户的变量）", false),
        ],
        ConditionalCapabilities: [CapabilityIds.EnvironmentMachine]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var environment = RequireEnvironment(context);
        if (environment.IsFailure)
        {
            return FailFrom(environment.Error);
        }

        var snapshotId = arguments.GetString("snapshot_id")!;
        if (snapshotId == "latest")
        {
            var list = environment.Value.ListSnapshots();
            if (list.IsFailure)
            {
                return FailFrom(list.Error);
            }

            if (list.Value.Count == 0)
            {
                return Fail(EnvStationErrorCodes.TxSnapshotInvalid, "本机还没有任何快照。");
            }

            snapshotId = list.Value[0].SnapshotId;
        }

        // 先校验完整性（NFR-R4）：损坏的快照绝不允许用于还原，否则会"用坏数据覆盖好数据"。
        var verify = environment.Value.VerifySnapshot(snapshotId);
        if (verify.IsFailure)
        {
            return FailFrom(verify.Error);
        }

        var snapshot = environment.Value.LoadSnapshot(snapshotId);
        if (snapshot.IsFailure)
        {
            return FailFrom(snapshot.Error);
        }

        var names = arguments.GetStringArray("names");
        var allowMachine = arguments.GetBoolean("allow_machine", false);
        var hasMachineChanges = snapshot.Value.MachineVariables.Count > 0;

        if (hasMachineChanges
            && !snapshot.Value.MachineScopeReadFailed
            && !allowMachine)
        {
            // 默认只还原用户级。系统级还原需要显式开关 + 对应能力，
            // 因为它会影响本机所有用户，是"还原"里风险最高的部分。
            names = names.Length == 0 ? ["__envstation_no_machine_restore__"] : names;
        }

        var restore = environment.Value.Restore(
            snapshot.Value,
            names.Length == 0 ? null : names);

        if (restore.IsFailure)
        {
            return FailFrom(restore.Error);
        }

        var scopeText = names.Length == 0 ? "全部变量" : $"{names.Length} 个变量";
        return Ok(
            $"已从快照 {snapshotId} 回滚{scopeText}。" +
            (hasMachineChanges && !allowMachine && names.Length > 0 ? "系统级变量未回滚（allow_machine 为 false）。" : string.Empty),
            Outputs(
                ("snapshot_id", snapshotId),
                ("restored_names", string.Join(",", names)),
                ("mode", names.Length == 0 ? "full" : "partial")),
            reversibleToken: snapshotId);
    }
}

/// <summary><c>envstation.env.validate</c>：校验变量名/值合法性。</summary>
internal sealed class EnvValidateAction : EnvActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.env.validate",
        "1.0.0",
        CapabilityIds.Inspect,
        "校验环境变量",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 10,
        TouchedResources: [],
        Parameters:
        [
            Str("name", true, "变量名", maxLength: 255),
            Str("value", false, "变量值；省略则只校验名称", maxLength: 32000),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var name = arguments.GetString("name")!;
        var value = arguments.GetString("value");

        var validation = RegistryEnvStore.ValidateNameAndValue(name, value ?? string.Empty);
        var warnings = ImmutableArray.CreateBuilder<string>();

        if (value is not null)
        {
            if (value.Length > RegistryEnvStore.LegacyEditorLimit)
            {
                warnings.Add($"值长度 {value.Length} 超过旧版环境变量编辑对话框上限 {RegistryEnvStore.LegacyEditorLimit}。" +
                             "不要用系统自带对话框编辑该变量，否则会被静默截断。");
            }

            var percentCount = value.Count(static c => c == '%');
            if (percentCount % 2 != 0)
            {
                warnings.Add("值中的 % 个数为奇数，存在未配对的 %，被其他程序展开时行为不可预期。");
            }

            if (value.Contains(";;", StringComparison.Ordinal))
            {
                warnings.Add("值中出现连续分号（;;）。对 PATH 而言这代表一个空条目，会让系统在当前目录查找可执行文件（安全风险）。");
            }
        }

        if (validation.IsFailure)
        {
            return Fail(
                validation.Error.Code,
                $"{validation.Error.Message} {validation.Error.Remediation}".Trim());
        }

        var message = warnings.Count == 0
            ? $"{name} 的名称与取值合法。"
            : $"{name} 合法，但有 {warnings.Count} 项需要注意：{string.Join("、", warnings)}";

        return Ok(
            message,
            Outputs(
                ("valid", "true"),
                ("warning_count", warnings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("warnings", string.Join(" || ", warnings)),
                ("name_length", name.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("value_length", (value?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }
}

/// <summary>环境差异条目。</summary>
internal sealed record EnvironmentDiff(EnvScope Scope, string Name, ChangeKind Kind, string? OldValue, string? NewValue)
{
    internal string Describe()
    {
        var scopeName = Scope == EnvScope.User ? "用户级" : "系统级";
        return Kind switch
        {
            ChangeKind.Added => $"[{scopeName}] 新增 {Name}",
            ChangeKind.Removed => $"[{scopeName}] 删除 {Name}",
            _ => $"[{scopeName}] 修改 {Name}",
        };
    }
}

/// <summary>快照与当前环境的差异计算（<c>env.diff</c> 与报告共用）。</summary>
internal static class EnvironmentDiffer
{
    internal static IReadOnlyList<EnvironmentDiff> Compare(
        IEnvironmentOperations environment, EnvironmentSnapshot snapshot, out IReadOnlyList<string> readErrors)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(snapshot);

        var errors = new List<string>();
        var diffs = new List<EnvironmentDiff>();

        CompareScope(environment, snapshot, EnvScope.User, snapshot.UserVariables, diffs, errors);

        if (snapshot.MachineScopeReadFailed)
        {
            errors.Add("该快照未读取到系统级变量，系统级差异无法判断。");
        }
        else
        {
            CompareScope(environment, snapshot, EnvScope.Machine, snapshot.MachineVariables, diffs, errors);
        }

        readErrors = errors;
        return diffs;
    }

    private static void CompareScope(
        IEnvironmentOperations environment,
        EnvironmentSnapshot snapshot,
        EnvScope scope,
        IReadOnlyList<EnvVariable> baseline,
        List<EnvironmentDiff> diffs,
        List<string> errors)
    {
        var current = environment.ReadAll(scope);
        if (current.IsFailure)
        {
            errors.Add($"[{EnvironmentPathResolver.ToScopeLabel(scope)}] 读取失败：{current.Error.Message}");
            return;
        }

        var currentByName = current.Value.ToDictionary(static v => v.Name, StringComparer.OrdinalIgnoreCase);
        var baselineByName = baseline.ToDictionary(static v => v.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var variable in baseline)
        {
            if (!currentByName.TryGetValue(variable.Name, out var now))
            {
                diffs.Add(new EnvironmentDiff(scope, variable.Name, ChangeKind.Removed, variable.RawValue, null));
                continue;
            }

            if (!string.Equals(now.RawValue, variable.RawValue, StringComparison.Ordinal))
            {
                diffs.Add(new EnvironmentDiff(scope, variable.Name, ChangeKind.Modified, variable.RawValue, now.RawValue));
                continue;
            }

            if (now.Kind != variable.Kind)
            {
                // 值没变但类型变了 —— 这是很隐蔽的一类损坏（PE-4），必须单独报出来。
                diffs.Add(new EnvironmentDiff(scope, variable.Name, ChangeKind.Modified,
                    $"{variable.RawValue}（{variable.Kind}）", $"{now.RawValue}（{now.Kind}）"));
            }
        }

        foreach (var (name, now) in currentByName)
        {
            if (!baselineByName.ContainsKey(name))
            {
                diffs.Add(new EnvironmentDiff(scope, name, ChangeKind.Added, null, now.RawValue));
            }
        }

        _ = snapshot;
    }
}
