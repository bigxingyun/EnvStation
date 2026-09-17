using System.Text.Json.Serialization;

namespace EnvStation.Abstractions.Environment;

/// <summary>
/// 环境变量值类型。
/// <para>
/// <b>为什么不用 <c>Microsoft.Win32.RegistryValueKind</c>：</b>该类型来自 Windows 专属程序集，
/// 放在 Abstractions 会污染可移植性并影响 AOT 源生成。此处只保留环境变量真正会用到的两种，
/// 由 <c>RegistryEnvStore</c> 负责与注册表类型互转（SEC-11：禁止隐式改写类型）。
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EnvValueKind>))]
public enum EnvValueKind
{
    /// <summary>REG_SZ：字面量，<c>%VAR%</c> 不展开。</summary>
    String = 0,

    /// <summary>REG_EXPAND_SZ：可展开，<c>%VAR%</c> 由读取方展开。</summary>
    ExpandString = 1,
}

/// <summary>环境变量作用域。用户级不需要管理员权限；系统级影响所有用户（需求 2 章）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EnvScope>))]
public enum EnvScope
{
    User = 0,
    Machine = 1,
}

/// <summary>
/// 单个环境变量条目。
/// <para>
/// <see cref="RawValue"/> 是<b>未展开</b>的原始值（例如 <c>%JAVA_HOME%\bin</c>）；
/// <see cref="ExpandedValue"/> 仅在需要比较或展示时计算，<b>绝不用于回写</b>（PE-4）。
/// </para>
/// </summary>
public sealed record EnvVariable
{
    /// <summary>变量名。Windows 环境变量名大小写不敏感，但保留原始大小写用于回写（PE-2）。</summary>
    public required string Name { get; init; }

    /// <summary>未展开的原始值。</summary>
    public required string RawValue { get; init; }

    /// <summary>值类型。回写时必须保持原类型（SEC-11 / PE-4）。</summary>
    public required EnvValueKind Kind { get; init; }

    /// <summary>该值是否含 <c>%VAR%</c> 形式的引用（仅作提示，不改变行为）。</summary>
    public bool ContainsVariableReference { get; init; }
}

/// <summary>
/// 一次环境变量全量快照。
/// <para>
/// 存储格式见《详细设计文档》8.1 节；完整性校验见 NFR-R4（损坏即禁止后续写操作）。
/// 快照同时记录用户级与系统级，即使本次只改了用户级——这样还原时不会误伤另一侧。
/// </para>
/// </summary>
public sealed record EnvironmentSnapshot
{
    public required string SchemaVersion { get; init; }

    public required string SnapshotId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>触发来源，如 <c>env.set PATH</c>、<c>install python</c>、<c>manual</c>。</summary>
    public required string Trigger { get; init; }

    /// <summary>用户可读备注（手动快照时填写）。</summary>
    public string? Note { get; init; }

    public required IReadOnlyList<EnvVariable> UserVariables { get; init; }

    /// <summary>
    /// 系统级变量。读取 <c>HKLM</c> 通常不需要管理员（M0-P02 用例 P02-7 验证）；
    /// 若读取失败，此列表为空并置 <see cref="MachineScopeReadFailed"/>。
    /// </summary>
    public required IReadOnlyList<EnvVariable> MachineVariables { get; init; }

    /// <summary>系统级读取是否失败（权限受限时）。失败时还原操作必须拒绝执行系统级部分。</summary>
    public bool MachineScopeReadFailed { get; init; }

    /// <summary>内容哈希（SHA-256，十六进制小写），用于完整性校验。</summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// 采样时本机是否处于"未完成事务"状态。启动检测依赖此标记（需求 6.4 F2 / M0-P06 用例 8）。
    /// </summary>
    public bool HasPendingTransaction { get; init; }

    /// <summary>本次快照涉及的变量变化摘要（由调用方填充，便于时间线展示）。</summary>
    public IReadOnlyList<SnapshotChangeSummary> Changes { get; init; } = [];
}

/// <summary>快照与上一次状态之间的单条变化摘要（用于时间线与 diff 列表）。</summary>
public sealed record SnapshotChangeSummary
{
    public required EnvScope Scope { get; init; }
    public required string Name { get; init; }
    public required ChangeKind Kind { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ChangeKind>))]
public enum ChangeKind
{
    Added = 0,
    Removed = 1,
    Modified = 2,
    Unchanged = 3,
}
