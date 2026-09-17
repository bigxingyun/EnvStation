using System.Text.Json;
using System.Text.Json.Serialization;
using EnvStation.Abstractions.Environment;

namespace EnvStation.Abstractions.Serialization;

/// <summary>
/// 源生成的 JSON 序列化上下文。
/// <para>
/// <b>AOT 约束（AOT-1 / IM-3）：</b>禁止运行期反射式序列化。所有需要持久化的类型
/// 必须在此登记，否则在 Native AOT 下会在运行时报错或被打裁掉。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(EnvironmentSnapshot))]
[JsonSerializable(typeof(EnvVariable))]
[JsonSerializable(typeof(EnvVariable[]))]
[JsonSerializable(typeof(SnapshotChangeSummary))]
[JsonSerializable(typeof(SnapshotChangeSummary[]))]
[JsonSerializable(typeof(SnapshotIndex))]
[JsonSerializable(typeof(SnapshotIndexEntry))]
[JsonSerializable(typeof(SnapshotIndexEntry[]))]
[JsonSerializable(typeof(PendingTransactionMarker))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class EnvStationJsonContext : JsonSerializerContext;

/// <summary>快照目录索引，避免每次列出快照都要反序列化全部快照内容。</summary>
public sealed record SnapshotIndex
{
    public required string SchemaVersion { get; init; }
    public required IReadOnlyList<SnapshotIndexEntry> Entries { get; init; }
}

/// <summary>索引中的单条记录（轻量，仅用于列表与时间线）。</summary>
public sealed record SnapshotIndexEntry
{
    public required string SnapshotId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Trigger { get; init; }
    public string? Note { get; init; }
    public required bool IsManual { get; init; }
    public required bool IsBaseline { get; init; }
    public required long SizeBytes { get; init; }
    public required string ContentHash { get; init; }
}

/// <summary>
/// "未完成事务"标记。
/// <para>
/// 事务开始前写入、成功后删除。启动时若发现残留，说明上次执行中断（崩溃 / 断电 / 强杀），
/// 必须引导用户还原（需求 6.4 F2、NFR-R2、M0-P06 用例 8）。
/// </para>
/// </summary>
public sealed record PendingTransactionMarker
{
    public required string TransactionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string Operation { get; init; }

    /// <summary>可用于还原的快照 ID（若事务已创建快照）。</summary>
    public string? SnapshotId { get; init; }

    /// <summary>进程 ID，仅用于诊断，不作为判定依据（PID 会复用）。</summary>
    public int ProcessId { get; init; }
}
