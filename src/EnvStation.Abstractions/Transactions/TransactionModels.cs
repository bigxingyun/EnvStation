using System.Text.Json.Serialization;

namespace EnvStation.Abstractions.Transactions;

/// <summary>风险等级（见《需求分析.md》9.1 节）。UI 必须按等级使用不同视觉重量与确认方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    /// <summary>只读或仅影响本软件自身目录，无需确认。</summary>
    Safe = 0,

    /// <summary>可逆变更（用户级变量、用户目录内文件）：diff + 单次确认 + 自动快照。</summary>
    Reversible = 1,

    /// <summary>高风险（系统级 PATH、HKLM）：diff + 风险说明 + 勾选 + UAC + 双份快照。</summary>
    High = 2,

    /// <summary>危险（重启 explorer、批量删除）：默认隐藏，需输入确认词。</summary>
    Dangerous = 3,
}

/// <summary>变更种类（对应《详细设计文档》6 章 <c>ChangeKind</c> 的落地版本）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangeType>))]
public enum ChangeType
{
    EnvVarSet = 0,
    EnvVarUnset = 1,
    PathEntryAdd = 2,
    PathEntryRemove = 3,
    PathReorder = 4,
    FileWrite = 5,
    FileDelete = 6,
    ConfigFileEdit = 7,
    ShimRedirect = 8,
    LinkCreate = 9,
}

/// <summary>事务状态机（见《详细设计文档》7.5 节）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TransactionState>))]
public enum TransactionState
{
    Idle = 0,
    Preparing = 1,
    Prepared = 2,
    Applying = 3,
    Applied = 4,
    Committed = 5,
    RollingBack = 6,
    RolledBack = 7,

    /// <summary>自动回滚失败——必须保留手动脚本并醒目告警（需求 6.4 F5）。</summary>
    RollbackFailed = 8,
}

/// <summary>
/// 单条变更记录。必须携带足够信息以支持逆向操作（否则无法回滚）。
/// </summary>
public sealed record ChangeRecord
{
    public required ChangeType Type { get; init; }

    /// <summary>变更目标，如 <c>HKCU\Environment\PATH</c> 或安装目录路径。</summary>
    public required string Target { get; init; }

    public required RiskLevel Risk { get; init; }

    /// <summary>变更前的原始值（未展开、保持原类型语义）。</summary>
    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    /// <summary>变更前该变量是否存在。为 false 时回滚动作是"删除"而非"写回"。</summary>
    public bool ExistedBefore { get; init; }

    /// <summary>变更前的值类型；回滚时必须复原（SEC-11）。</summary>
    public string? OldKind { get; init; }

    /// <summary>回滚结果。</summary>
    public string? RollbackOutcome { get; init; }
}

/// <summary>一次可回滚的变更事务。</summary>
public sealed record EnvironmentTransaction
{
    public required string TransactionId { get; init; }

    public required string Operation { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required TransactionState State { get; init; }

    /// <summary>事务开始时创建的快照 ID（SEC-1：无快照不得写入）。</summary>
    public string? SnapshotId { get; init; }

    public required IReadOnlyList<ChangeRecord> Changes { get; init; }

    /// <summary>事务级错误（失败原因 / 回滚失败原因）。</summary>
    public string? FailureReason { get; init; }
}
