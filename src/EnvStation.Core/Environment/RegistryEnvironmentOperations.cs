using EnvStation.Abstractions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Serialization;
using EnvStation.Abstractions.Transactions;
using EnvStation.Core.Transactions;

namespace EnvStation.Core.Environment;

/// <summary>
/// <see cref="IEnvironmentOperations"/> 的生产实现：真实注册表 + 文件快照 + 事务。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个类是"能碰到真机环境"的唯一入口。</b>凡是它出现的地方，都必须能回答两个问题：
/// 快照存到哪、失败怎么回滚。因此它刻意不做任何"顺手优化"——
/// 每一次写入都走完整的 <c>快照 → 加锁 → 读旧值 → 写新值 → 记录逆向信息</c> 流程。
/// </para>
/// <para>
/// <b>PATH 的特殊处理</b>：PATH 是一个"值里再分成多个条目"的复合值。
/// 本项目<b>绝不</b>用字符串替换去改它，而是解析成条目列表、编辑、再按原始写法拼回
/// （<see cref="PathEditor"/> 与 <see cref="PathParser"/> 负责这件事）。
/// 同时写回时必须保持原来的 <see cref="EnvValueKind"/>：
/// 把 <c>REG_EXPAND_SZ</c> 写成 <c>REG_SZ</c> 会让用户的 <c>%JAVA_HOME%\bin</c> 永久变成字面量（PE-4）。
/// </para>
/// </remarks>
public sealed class RegistryEnvironmentOperations : IEnvironmentOperations
{
    private readonly ISnapshotStore _snapshots;
    private readonly Func<EnvScope, IEnvironmentStore> _storeFactory;
    private readonly EnvironmentTransactionManager _transactions;

    /// <summary>用显式的快照存储与存储工厂构造（便于测试与沙箱）。</summary>
    public RegistryEnvironmentOperations(
        ISnapshotStore snapshots,
        Func<EnvScope, IEnvironmentStore> storeFactory)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(storeFactory);

        _snapshots = snapshots;
        _storeFactory = storeFactory;
        _transactions = new EnvironmentTransactionManager(snapshots, storeFactory);
    }

    /// <summary>
    /// 用默认位置构造：快照落在 <c>%LOCALAPPDATA%\EnvStation\snapshots</c>。
    /// </summary>
    /// <param name="snapshotRoot">
    /// 快照根目录；为 null 时使用默认位置。**测试必须显式传入临时目录**，
    /// 否则会把测试快照混进用户真实数据里。
    /// </param>
    public static RegistryEnvironmentOperations CreateDefault(string? snapshotRoot = null)
    {
        var root = snapshotRoot ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "EnvStation",
            "snapshots");

        return new RegistryEnvironmentOperations(
            new SnapshotStore(root, new RegistryEnvStore(EnvScope.User), new RegistryEnvStore(EnvScope.Machine)),
            static scope => new RegistryEnvStore(scope));
    }

    /// <inheritdoc />
    public Result<EnvVariable?> Read(EnvScope scope, string name) =>
        _storeFactory(scope).Read(name);

    /// <inheritdoc />
    public Result<IReadOnlyList<EnvVariable>> ReadAll(EnvScope scope) =>
        _storeFactory(scope).ReadAll();

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> CaptureSnapshot(
        string trigger, string? note = null, bool isManual = false, bool isBaseline = false) =>
        _snapshots.Capture(trigger, note, isManual, isBaseline);

    /// <inheritdoc />
    public Result<IReadOnlyList<SnapshotIndexEntry>> ListSnapshots() => _snapshots.List();

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> LoadSnapshot(string snapshotId) => _snapshots.Load(snapshotId);

    /// <inheritdoc />
    public Result<Unit> VerifySnapshot(string snapshotId) => _snapshots.Verify(snapshotId);

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> SetVariable(
        EnvScope scope,
        string name,
        string rawValue,
        EnvValueKind? kind,
        RiskLevel risk,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<EnvironmentSnapshot>.Fail(
                EnvStationErrorCodes.EnvNameInvalid,
                "环境变量名不能为空。");
        }

        // 值类型推断：沿用原类型 → 否则用 REG_EXPAND_SZ。
        //
        // 为什么默认是 REG_EXPAND_SZ 而不是 REG_SZ：环境变量里写 %OTHER_VAR% 是极常见的用法，
        // 用 REG_SZ 会让这些引用变成字面量（"看上去对了，实际指向一个不存在的路径"）。
        // 已经在事务里读过旧值，这里再读一次是为了在没有旧值时给出正确默认。
        var existing = _storeFactory(scope).Read(name);
        if (existing.IsFailure)
        {
            return existing.Propagate<EnvironmentSnapshot>();
        }

        var effectiveKind = kind ?? existing.Value?.Kind ?? EnvValueKind.ExpandString;

        var begin = _transactions.Begin(operation);
        if (begin.IsFailure)
        {
            return begin.Propagate<EnvironmentSnapshot>();
        }

        var written = _transactions.SetVariable(begin.Value, scope, name, rawValue, effectiveKind, risk);
        if (written.IsFailure)
        {
            // SetVariable 内部已在写入失败时回滚；这里补一次显式回滚以覆盖"前置校验失败"的路径，
            // 保证未完成事务标记一定被清掉（否则下次启动会误报"上次配置未完成"）。
            _ = _transactions.Rollback(begin.Value, written.Error.Message);
            return Result<EnvironmentSnapshot>.Fail(written.Error);
        }

        var commit = _transactions.Commit(written.Value);
        if (commit.IsFailure)
        {
            return commit.Propagate<EnvironmentSnapshot>();
        }

        // 返回事务开始时的快照——它正是"回到修改前"所需要的那个。
        var snapshot = _snapshots.Load(begin.Value.SnapshotId!);
        return snapshot.IsFailure
            ? Result<EnvironmentSnapshot>.Fail(snapshot.Error)
            : Result<EnvironmentSnapshot>.Ok(snapshot.Value);
    }

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> RemoveVariable(EnvScope scope, string name, RiskLevel risk, string operation)
    {
        var begin = _transactions.Begin(operation);
        if (begin.IsFailure)
        {
            return begin.Propagate<EnvironmentSnapshot>();
        }

        var removed = _transactions.RemoveVariable(begin.Value, scope, name, risk);
        if (removed.IsFailure)
        {
            _ = _transactions.Rollback(begin.Value, removed.Error.Message);
            return Result<EnvironmentSnapshot>.Fail(removed.Error);
        }

        var commit = _transactions.Commit(removed.Value);
        if (commit.IsFailure)
        {
            return commit.Propagate<EnvironmentSnapshot>();
        }

        var snapshot = _snapshots.Load(begin.Value.SnapshotId!);
        return snapshot.IsFailure
            ? Result<EnvironmentSnapshot>.Fail(snapshot.Error)
            : Result<EnvironmentSnapshot>.Ok(snapshot.Value);
    }

    /// <inheritdoc />
    public Result<Unit> Restore(EnvironmentSnapshot snapshot, IReadOnlyList<string>? names)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 还原前先给"当前状态"留一份快照：还原本身也是一次修改，
        // 万一是用户点错了，还能再退回来。这一点经常被忽略，但它决定了"还原"能否被信任。
        var backup = _snapshots.Capture("从快照回滚前的自动备份", note: $"回滚目标：{snapshot.SnapshotId}");
        if (backup.IsFailure)
        {
            return backup.Propagate<Unit>();
        }

        var begin = _transactions.Begin($"回滚快照 {snapshot.SnapshotId}");
        if (begin.IsFailure)
        {
            return begin.Propagate<Unit>();
        }

        var tx = begin.Value;
        var wanted = names is null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        // 用户级
        var userResult = RestoreScope(tx, snapshot, EnvScope.User, snapshot.UserVariables, wanted, snapshot.MachineScopeReadFailed ? false : true);
        if (userResult.IsFailure)
        {
            _ = _transactions.Rollback(tx, userResult.Error.Message);
            return Result<Unit>.Fail(userResult.Error);
        }

        tx = userResult.Value;

        // 系统级：快照当时读不到系统级时必须跳过，否则会把"读失败导致的空列表"当成"系统级应该没有变量"，
        // 从而删掉用户真实的系统变量——这是最危险的一种误还原。
        if (!snapshot.MachineScopeReadFailed)
        {
            var machineResult = RestoreScope(tx, snapshot, EnvScope.Machine, snapshot.MachineVariables, wanted, true);
            if (machineResult.IsFailure)
            {
                _ = _transactions.Rollback(tx, machineResult.Error.Message);
                return Result<Unit>.Fail(machineResult.Error);
            }

            tx = machineResult.Value;
        }

        return _transactions.Commit(tx);
    }

    /// <summary>还原单个作用域。<paramref name="allowDelete"/> 为 false 时只做"补回缺失项"，不删除多余项。</summary>
    private Result<EnvironmentTransaction> RestoreScope(
        EnvironmentTransaction tx,
        EnvironmentSnapshot snapshot,
        EnvScope scope,
        IReadOnlyList<EnvVariable> target,
        HashSet<string>? wanted,
        bool allowDelete)
    {
        var current = _storeFactory(scope).ReadAll();
        if (current.IsFailure)
        {
            return current.Propagate<EnvironmentTransaction>();
        }

        var currentByName = current.Value.ToDictionary(static v => v.Name, StringComparer.OrdinalIgnoreCase);
        var targetByName = target.ToDictionary(static v => v.Name, StringComparer.OrdinalIgnoreCase);

        var working = tx;

        // ① 补回 / 修正
        foreach (var variable in target)
        {
            if (wanted is not null && !wanted.Contains(variable.Name))
            {
                continue;
            }

            var existing = currentByName.GetValueOrDefault(variable.Name);
            if (existing is not null
                && string.Equals(existing.RawValue, variable.RawValue, StringComparison.Ordinal)
                && existing.Kind == variable.Kind)
            {
                continue;
            }

            var set = _transactions.SetVariable(
                working, scope, variable.Name, variable.RawValue, variable.Kind, RiskLevel.Reversible);
            if (set.IsFailure)
            {
                return Result<EnvironmentTransaction>.Fail(set.Error);
            }

            working = set.Value;
        }

        // ② 删除快照中不存在、当前却存在的变量（仅全量还原时才做）
        if (allowDelete && wanted is null)
        {
            foreach (var (name, _) in currentByName)
            {
                if (targetByName.ContainsKey(name))
                {
                    continue;
                }

                var removed = _transactions.RemoveVariable(working, scope, name, RiskLevel.Reversible);
                if (removed.IsFailure)
                {
                    return Result<EnvironmentTransaction>.Fail(removed.Error);
                }

                working = removed.Value;
            }
        }

        _ = snapshot;
        return Result<EnvironmentTransaction>.Ok(working);
    }

    /// <inheritdoc />
    public Result<PathSnapshot> ReadPath(EnvScope scope)
    {
        var store = _storeFactory(scope);
        var read = store.Read("PATH");
        if (read.IsFailure)
        {
            return read.Propagate<PathSnapshot>();
        }

        var raw = read.Value?.RawValue ?? string.Empty;
        var kind = read.Value?.Kind ?? EnvValueKind.ExpandString;

        // probeFileSystem: true —— PATH 动作需要知道哪些目录真的不存在（"清理失效项"依赖它）。
        var entries = PathParser.Parse(raw, probeFileSystem: true);
        return Result<PathSnapshot>.Ok(new PathSnapshot(scope, raw, kind, entries));
    }

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> WritePath(
        EnvScope scope, IReadOnlyList<PathEntry> entries, RiskLevel risk, string operation)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var current = ReadPath(scope);
        if (current.IsFailure)
        {
            return current.Propagate<EnvironmentSnapshot>();
        }

        var newValue = PathParser.Join(entries);

        // 长度检查必须在写之前做：Windows 的环境变量上限是 32767，
        // 但更现实的问题是 **超过 2047 后旧版"环境变量"编辑对话框会截断**，
        // 用户一旦用那个对话框改过就会永久损坏 PATH。
        // 因此这里明确拒绝而不是截断——截断造成的后果比拒绝严重得多。
        if (newValue.Length > RegistryEnvStore.MaxValueLength)
        {
            return Result<EnvironmentSnapshot>.Fail(
                EnvStationErrorCodes.EnvValueTooLong,
                $"修改后的 PATH 长度为 {newValue.Length} 字符，超过系统上限 {RegistryEnvStore.MaxValueLength}。",
                "先用「清理失效项」减少条目，或把多个目录合并到一层。已中止写入，PATH 保持原样。");
        }

        var shortenedByLength = newValue.Length > RegistryEnvStore.LegacyEditorLimit;

        // 值类型保持原样（PE-4）；PATH 几乎总是 REG_EXPAND_SZ。
        var result = SetVariable(scope, "PATH", newValue, current.Value.Kind, risk, operation);

        if (result.IsSuccess && shortenedByLength)
        {
            // 不阻断成功，但必须让用户知道：这是一个真实存在、且很容易踩的坑。
            return Result<EnvironmentSnapshot>.Ok(result.Value with
            {
                Note = $"PATH 长度 {newValue.Length} 字符，已超过旧版环境变量编辑对话框的 {RegistryEnvStore.LegacyEditorLimit} 字符上限。" +
                       "用该对话框编辑 PATH 会被静默截断。改用环境站编辑 PATH。",
            });
        }

        return result;
    }
}
