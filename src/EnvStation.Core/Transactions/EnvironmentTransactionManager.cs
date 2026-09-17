using EnvStation.Abstractions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Serialization;
using EnvStation.Abstractions.Transactions;
using EnvStation.Core.Environment;

namespace EnvStation.Core.Transactions;

/// <summary>
/// 环境变量事务：先快照、再写入、失败即回滚（需求第 9 章"四层防护"的第二、三层）。
///
/// <para><b>硬性约束（SEC-1）</b>：事务开始时必须成功创建快照，否则<b>拒绝任何写入</b>。
/// 这不是可配置项——它是"一切可回滚"承诺的技术保证。</para>
///
/// <para><b>门面设计</b>：事务不自己实现注册表操作，而是复用 <see cref="IEnvironmentStore"/>；
/// 这样"回滚"与"正向写入"走同一条代码路径，避免两者行为不一致（最危险的隐患）。</para>
/// </summary>
public sealed class EnvironmentTransactionManager
{
    private readonly ISnapshotStore _snapshots;
    private readonly Func<EnvScope, IEnvironmentStore> _storeFactory;

    public EnvironmentTransactionManager(
        ISnapshotStore snapshots,
        Func<EnvScope, IEnvironmentStore> storeFactory)
    {
        _snapshots = snapshots;
        _storeFactory = storeFactory;
    }

    /// <summary>
    /// 开启事务：创建快照 + 写入"未完成事务"标记。
    /// 返回的事务对象上可以继续调用 <see cref="SetVariable"/> / <see cref="RemoveVariable"/>。
    /// </summary>
    public Result<EnvironmentTransaction> Begin(string operation, bool isManualSnapshot = false)
    {
        var txId = $"tx-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        var snapshot = _snapshots.Capture(operation, note: null, isManual: isManualSnapshot);
        if (snapshot.IsFailure)
        {
            // SEC-1：拿不到快照就不允许写入
            return Result<EnvironmentTransaction>.Fail(
                EnvStationErrorCodes.TxSnapshotRequired,
                $"无法为「{operation}」创建快照，已拒绝写入。",
                snapshot.Error.Remediation ?? "检查快照目录是否可写，或在设置中更换位置。");
        }

        // 未完成事务标记：崩溃/断电后据此提示还原（需求 6.4 F2）
        var marker = new PendingTransactionMarker
        {
            TransactionId = txId,
            StartedAt = DateTimeOffset.Now,
            Operation = operation,
            SnapshotId = snapshot.Value.SnapshotId,
            ProcessId = System.Environment.ProcessId,
        };

        var write = _snapshots.WritePendingMarker(marker);
        if (write.IsFailure)
        {
            return write.Propagate<EnvironmentTransaction>();
        }

        var tx = new EnvironmentTransaction
        {
            TransactionId = txId,
            Operation = operation,
            StartedAt = DateTimeOffset.Now,
            State = TransactionState.Prepared,
            SnapshotId = snapshot.Value.SnapshotId,
            Changes = [],
        };

        return Result<EnvironmentTransaction>.Ok(tx);
    }

    /// <summary>
    /// 在事务内设置变量。每次调用<b>立即生效</b>并记录逆向信息，
    /// 因此任意一步失败都能按逆序回滚到事务开始前状态。
    /// </summary>
    public Result<EnvironmentTransaction> SetVariable(
        EnvironmentTransaction tx, EnvScope scope, string name, string rawValue, EnvValueKind kind, RiskLevel risk)
    {
        if (tx.State is not (TransactionState.Prepared or TransactionState.Applying))
        {
            return Result<EnvironmentTransaction>.Fail(
                EnvStationErrorCodes.TxRollbackFailed,
                $"事务 {tx.TransactionId} 当前状态为 {tx.State}，不接受新变更。");
        }

        var store = _storeFactory(scope);

        // ── 跨进程互斥（M0-P02 用例 P02-10 实测结论）──
        // 注册表写入是"读-改-写"三步，没有原子性；实测中并发写入会相互覆盖。
        // 因此把"读旧值 → 写新值"整段纳入同一把按变量粒度的锁，
        // 保证不会有另一个进程在本进程读到旧值与写入新值之间插入修改。
        //
        // 粒度选择：按 (作用域, 变量名) 加锁而不是全局一把大锁 ——
        // "改 PATH"与"改 JAVA_HOME"不应互相阻塞，但两个进程同时改 PATH 必须串行。
        //
        // 注意 Acquire 返回的是 Result<EnvironmentMutex>，不能直接 using
        // （Result 是值类型结果，不是 IDisposable）。必须先判成败再取 Value。
        var guardResult = EnvironmentMutex.Acquire(scope, name);
        if (guardResult.IsFailure)
        {
            return guardResult.Propagate<EnvironmentTransaction>();
        }

        using var guard = guardResult.Value;

        // 先读旧值：回滚时需要它（含"原本不存在"这一情形）
        var before = store.Read(name);
        if (before.IsFailure)
        {
            return before.Propagate<EnvironmentTransaction>();
        }

        var write = store.Write(name, rawValue, kind);
        if (write.IsFailure)
        {
            // 写入失败：立即回滚已完成的变更（需求 6.4 F4）
            var rolledBack = RollbackCore(tx, failedOperation: $"写入 {name} 失败");
            return Result<EnvironmentTransaction>.Fail(
                write.Error.Code,
                write.Error.Message,
                $"{write.Error.Remediation} 已自动回滚本次事务的 {tx.Changes.Count} 项变更。".Trim(),
                tx with { State = rolledBack.State });
        }

        var record = new ChangeRecord
        {
            Type = ChangeType.EnvVarSet,
            Target = $"{ScopePath(scope)}\\{name}",
            Risk = risk,
            OldValue = before.Value?.RawValue,
            NewValue = rawValue,
            ExistedBefore = before.Value is not null,
            OldKind = before.Value?.Kind.ToString(),
        };

        return Result<EnvironmentTransaction>.Ok(tx with
        {
            State = TransactionState.Applying,
            Changes = [.. tx.Changes, record],
        });
    }

    /// <summary>在事务内删除变量。</summary>
    public Result<EnvironmentTransaction> RemoveVariable(
        EnvironmentTransaction tx, EnvScope scope, string name, RiskLevel risk)
    {
        var store = _storeFactory(scope);

        // 同 SetVariable：删除也是"读-删"两步，需在同一把变量级锁内完成
        var guardResult = EnvironmentMutex.Acquire(scope, name);
        if (guardResult.IsFailure)
        {
            return guardResult.Propagate<EnvironmentTransaction>();
        }

        using var guard = guardResult.Value;

        var before = store.Read(name);
        if (before.IsFailure)
        {
            return before.Propagate<EnvironmentTransaction>();
        }

        var delete = store.Delete(name);
        if (delete.IsFailure)
        {
            var rolledBack = RollbackCore(tx, failedOperation: $"删除 {name} 失败");
            return Result<EnvironmentTransaction>.Fail(
                delete.Error.Code,
                delete.Error.Message,
                $"已自动回滚本次事务的 {tx.Changes.Count} 项变更。",
                tx with { State = rolledBack.State });
        }

        var record = new ChangeRecord
        {
            Type = ChangeType.EnvVarUnset,
            Target = $"{ScopePath(scope)}\\{name}",
            Risk = risk,
            OldValue = before.Value?.RawValue,
            NewValue = null,
            ExistedBefore = before.Value is not null,
            OldKind = before.Value?.Kind.ToString(),
        };

        return Result<EnvironmentTransaction>.Ok(tx with
        {
            State = TransactionState.Applying,
            Changes = [.. tx.Changes, record],
        });
    }

    /// <summary>提交事务：清除未完成标记。</summary>
    public Result<Unit> Commit(EnvironmentTransaction tx)
    {
        var clear = _snapshots.ClearPendingMarker();
        return clear.IsFailure ? clear : Results.Ok();
    }

    /// <summary>
    /// 回滚事务：按<b>逆序</b>恢复每一处变更。
    /// 任一步失败都会让事务进入 <see cref="TransactionState.RollbackFailed"/>，
    /// 并向上层提供详细日志（需求 6.4 F5）。
    /// </summary>
    public Result<EnvironmentTransaction> Rollback(EnvironmentTransaction tx, string? reason = null)
        => Result<EnvironmentTransaction>.Ok(RollbackCore(tx, reason));

    private EnvironmentTransaction RollbackCore(EnvironmentTransaction tx, string? failedOperation)
    {
        var updated = new List<ChangeRecord>(tx.Changes.Count);
        var anyFailure = false;

        // 逆序回滚：后发生的先撤销
        for (var i = tx.Changes.Count - 1; i >= 0; i--)
        {
            var change = tx.Changes[i];
            var outcome = RestoreOne(change);
            if (!outcome.IsSuccess)
            {
                anyFailure = true;
            }

            updated.Add(change with { RollbackOutcome = outcome.IsSuccess ? "ok" : outcome.Error.Code });
        }

        updated.Reverse();

        var finalState = anyFailure ? TransactionState.RollbackFailed : TransactionState.RolledBack;

        if (!anyFailure)
        {
            _snapshots.ClearPendingMarker();
        }

        return tx with
        {
            State = finalState,
            CompletedAt = DateTimeOffset.Now,
            Changes = updated,
            FailureReason = anyFailure
                ? $"回滚过程中有变更未能回到原值。原始失败：{failedOperation}"
                : failedOperation,
        };
    }

    private Result<Unit> RestoreOne(ChangeRecord change)
    {
        var (scope, name) = ParseTarget(change.Target);
        if (name is null)
        {
            return Results.Fail(
                EnvStationErrorCodes.TxRollbackFailed, $"无法解析变更目标：{change.Target}");
        }

        var store = _storeFactory(scope);

        if (!change.ExistedBefore)
        {
            // 变更前不存在 → 回滚 = 删除
            return store.Delete(name);
        }

        var kind = Enum.TryParse<EnvValueKind>(change.OldKind, out var k) ? k : EnvValueKind.String;
        return store.Write(name, change.OldValue ?? string.Empty, kind);
    }

    private static (EnvScope Scope, string? Name) ParseTarget(string target)
    {
        var idx = target.LastIndexOf('\\');
        if (idx < 0 || idx == target.Length - 1)
        {
            return (EnvScope.User, null);
        }

        var name = target[(idx + 1)..];
        var scope = target.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
            ? EnvScope.Machine
            : EnvScope.User;

        return (scope, name);
    }

    private static string ScopePath(EnvScope scope) => scope switch
    {
        EnvScope.Machine => @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
        _ => @"HKCU\Environment",
    };

    /// <summary>
    /// 启动时的未完成事务检测（需求 6.4 F2 / NFR-R2）。
    /// 返回 <c>null</c> 表示上次执行正常结束。
    /// </summary>
    public Result<PendingTransactionMarker?> DetectInterruptedTransaction()
        => _snapshots.ReadPendingMarker();
}
