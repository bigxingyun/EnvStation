using EnvStation.Abstractions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Serialization;
using EnvStation.Abstractions.Transactions;

namespace EnvStation.Core.Environment;

/// <summary>
/// 环境变量的领域操作门面：动作层修改环境变量的<b>唯一</b>通道。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这一层（而不是让动作直接用注册表或事务管理器）</b>，三个理由：
/// </para>
/// <list type="number">
///   <item><b>真机安全的第一道闸。</b>本接口是"能不能碰到真实环境变量"的唯一开关。
///         执行上下文里没有实现时，所有 <c>env.*</c> / <c>path.*</c> 动作会明确失败，
///         而不是悄悄去改真注册表。测试与预演注入沙箱实现，物理上不可能写坏用户环境。</item>
///   <item><b>把四条硬性要求收在一处。</b>需求 SEC-1（无快照不写入）、SEC-11（保持值类型）、
///         SEC-3/SEC-5（作用域权限）、以及"变更必须记录逆向信息"——这些如果散落在十几个动作里，
///         只要有一个动作忘了，整个安全承诺就出现缺口。</item>
///   <item><b>让"读"与"写"用同一条代码路径。</b>回滚与正向写入共用 <see cref="IEnvironmentStore"/>，
///         避免两者行为不一致（这是回滚类功能最危险的隐患）。</item>
/// </list>
/// </remarks>
public interface IEnvironmentOperations
{
    /// <summary>读取单个变量；不存在时返回 <c>null</c> 值的成功结果。</summary>
    Result<EnvVariable?> Read(EnvScope scope, string name);

    /// <summary>读取某个作用域的全部变量。</summary>
    Result<IReadOnlyList<EnvVariable>> ReadAll(EnvScope scope);

    /// <summary>
    /// 采集快照（需求 SEC-1 的载体）。任何写入之前必须先成功调用一次。
    /// </summary>
    Result<EnvironmentSnapshot> CaptureSnapshot(string trigger, string? note = null, bool isManual = false, bool isBaseline = false);

    /// <summary>列出快照索引（时间倒序）。</summary>
    Result<IReadOnlyList<SnapshotIndexEntry>> ListSnapshots();

    /// <summary>读取并校验快照。</summary>
    Result<EnvironmentSnapshot> LoadSnapshot(string snapshotId);

    /// <summary>校验快照完整性（NFR-R4）。</summary>
    Result<Unit> VerifySnapshot(string snapshotId);

    /// <summary>
    /// 事务化写入一个变量：内部会创建快照、写入、记录逆向信息，失败自动回滚。
    /// </summary>
    /// <param name="scope">作用域。</param>
    /// <param name="name">变量名。</param>
    /// <param name="rawValue">未展开的原始值。</param>
    /// <param name="kind">值类型；为 null 表示"沿用原类型，原来不存在则用 REG_EXPAND_SZ"。</param>
    /// <param name="risk">风险等级（决定 UI 确认强度与是否双份快照）。</param>
    /// <param name="operation">操作描述，写入快照的 trigger 字段与审计日志。</param>
    Result<EnvironmentSnapshot> SetVariable(
        EnvScope scope, string name, string rawValue, EnvValueKind? kind, RiskLevel risk, string operation);

    /// <summary>事务化删除一个变量。</summary>
    Result<EnvironmentSnapshot> RemoveVariable(EnvScope scope, string name, RiskLevel risk, string operation);

    /// <summary>
    /// 从一个快照还原。<paramref name="names"/> 为 null 表示全量还原；否则只还原列出的变量。
    /// </summary>
    /// <param name="snapshot">已加载的快照。</param>
    /// <param name="names">要还原的变量名；null 表示全部。</param>
    Result<Unit> Restore(EnvironmentSnapshot snapshot, IReadOnlyList<string>? names);

    /// <summary>获取某个作用域 PATH 的原始值与当前条目列表（PATH 专项动作共用）。</summary>
    Result<PathSnapshot> ReadPath(EnvScope scope);

    /// <summary>把编辑后的 PATH 条目写回（保持原值类型；超长则拒绝而不是截断）。</summary>
    Result<EnvironmentSnapshot> WritePath(EnvScope scope, IReadOnlyList<PathEntry> entries, RiskLevel risk, string operation);
}

/// <summary>某个作用域 PATH 的当前状态。</summary>
/// <param name="Scope">作用域。</param>
/// <param name="RawValue">注册表中的原始值（未展开）。</param>
/// <param name="Kind">值类型。写回时必须保持。</param>
/// <param name="Entries">解析后的条目。</param>
public sealed record PathSnapshot(EnvScope Scope, string RawValue, EnvValueKind Kind, IReadOnlyList<PathEntry> Entries)
{
    /// <summary>注册表中 PATH 是否存在（不存在时 <see cref="RawValue"/> 为空串）。</summary>
    public bool Existed => RawValue.Length > 0;
}
