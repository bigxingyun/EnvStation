using EnvStation.Abstractions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Serialization;
using EnvStation.Abstractions.Transactions;
using EnvStation.Core.Transactions;

namespace EnvStation.Core.Environment;

/// <summary>
/// 只读环境操作包装：读操作照常，写操作一律拒绝。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它（一次真实缺陷暴露出来的设计缺口）</b>：最初的预演实现干脆不注入任何环境操作，
/// 结果是连 <c>path.validate</c> 这种<b>纯读</b>动作都失败——而预演的全部价值恰恰在于
/// "把真实的当前状态读出来，告诉用户将要发生什么"。读不到现状，预览就无从谈起。
/// </para>
/// <para>
/// 但"为了预览而放开写"又是绝对不行的。因此这里把 <see cref="IEnvironmentOperations"/> 按
/// <b>读写分离</b>的方式切开：读委托给真实实现，写返回明确失败。
/// </para>
/// <para>
/// 与"不注入实现"相比，这个包装的价值在于：它让"预演"从"什么都做不了"变成"读得到真相、写不动任何东西"，
/// 而且拒写这件事是<b>结构性的</b>——不是靠调用方记得检查标志位。
/// </para>
/// </remarks>
public sealed class ReadOnlyEnvironmentOperations : IEnvironmentOperations
{
    private readonly IEnvironmentOperations _inner;

    /// <summary>包装一个真实实现。</summary>
    public ReadOnlyEnvironmentOperations(IEnvironmentOperations inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>用默认位置构造：读真实注册表，写一律拒绝。</summary>
    public static ReadOnlyEnvironmentOperations CreateDefault() =>
        new(RegistryEnvironmentOperations.CreateDefault());

    /// <inheritdoc />
    public Result<EnvVariable?> Read(EnvScope scope, string name) => _inner.Read(scope, name);

    /// <inheritdoc />
    public Result<IReadOnlyList<EnvVariable>> ReadAll(EnvScope scope) => _inner.ReadAll(scope);

    /// <inheritdoc />
    public Result<PathSnapshot> ReadPath(EnvScope scope) => _inner.ReadPath(scope);

    /// <inheritdoc />
    public Result<IReadOnlyList<SnapshotIndexEntry>> ListSnapshots() => _inner.ListSnapshots();

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> LoadSnapshot(string snapshotId) => _inner.LoadSnapshot(snapshotId);

    /// <inheritdoc />
    public Result<Unit> VerifySnapshot(string snapshotId) => _inner.VerifySnapshot(snapshotId);

    // ── 以下全部是写操作：预演模式下结构性拒绝 ──

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> CaptureSnapshot(
        string trigger, string? note = null, bool isManual = false, bool isBaseline = false)
    {
        _ = (trigger, note, isManual, isBaseline);

        // 建快照本身是"写盘"操作。预演不落盘，因此也不建快照——
        // 否则用户会看到一堆"什么都没改"的空快照，反而干扰对时间线的判断。
        return Result<EnvironmentSnapshot>.Fail(
            EnvStationErrorCodes.CapabilityDenied,
            "预演模式不会创建快照（因为没有做任何修改）。");
    }

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> SetVariable(
        EnvScope scope, string name, string rawValue, EnvValueKind? kind, RiskLevel risk, string operation)
    {
        _ = (scope, name, rawValue, kind, risk, operation);
        return WriteDenied($"设置环境变量 {name}");
    }

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> RemoveVariable(EnvScope scope, string name, RiskLevel risk, string operation)
    {
        _ = (scope, name, risk, operation);
        return WriteDenied($"删除环境变量 {name}");
    }

    /// <inheritdoc />
    public Result<Unit> Restore(EnvironmentSnapshot snapshot, IReadOnlyList<string>? names)
    {
        _ = (snapshot, names);
        return Result<Unit>.Fail(
            EnvStationErrorCodes.CapabilityDenied,
            "预演模式不允许回滚操作。",
            "回滚会真实修改环境变量。去掉 --dry-run（或加上 --apply）后重试。");
    }

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> WritePath(
        EnvScope scope, IReadOnlyList<PathEntry> entries, RiskLevel risk, string operation)
    {
        _ = (scope, entries, risk, operation);
        return WriteDenied("修改 PATH");
    }

    private static Result<EnvironmentSnapshot> WriteDenied(string what) =>
        Result<EnvironmentSnapshot>.Fail(
            EnvStationErrorCodes.CapabilityDenied,
            $"预演模式不允许「{what}」。",
            "预演只读取现状、不产生任何修改。确认预览无误后去掉预演标志即可真正执行。");
}
