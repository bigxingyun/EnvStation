using EnvStation.Abstractions;
using EnvStation.Core.Environment;
using EnvScope = EnvStation.Abstractions.Environment.EnvScope;
using EnvValueKind = EnvStation.Abstractions.Environment.EnvValueKind;
using EnvVariable = EnvStation.Abstractions.Environment.EnvVariable;
using EnvironmentSnapshot = EnvStation.Abstractions.Environment.EnvironmentSnapshot;
using RiskLevel = EnvStation.Abstractions.Transactions.RiskLevel;
using SnapshotIndexEntry = EnvStation.Abstractions.Serialization.SnapshotIndexEntry;

namespace EnvStation.TestKit;

/// <summary>
/// 内存沙箱环境操作实现（测试共享件，供多套用例复用）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么测试必须用沙箱而不是真注册表</b>：A05/A06 这一组动作的职责就是"修改环境变量"。
/// 如果测试直接把它们接到真实的 <c>HKCU\Environment</c>，一次断言写错就会污染开发机的
/// PATH —— 而 PATH 被写坏会让整台机器的命令行工具集体失效（这正是本产品要解决的问题本身）。
/// </para>
/// <para>
/// 因此本套用例<b>物理上不可能</b>碰真机环境：动作经由真实的 <c>ActionExecutor</c> 与真实的
/// 参数绑定器执行，唯一被替换掉的是"值落到哪张表"。这样既保住了对动作逻辑的真实覆盖，
/// 又让"跑测试"这件事在任何机器上都是安全的。
/// </para>
/// </remarks>
public sealed class SandboxEnvironment : IEnvironmentOperations
{
    private readonly Dictionary<EnvScope, Dictionary<string, EnvVariable>> _store = new()
    {
        [EnvScope.User] = new Dictionary<string, EnvVariable>(StringComparer.OrdinalIgnoreCase),
        [EnvScope.Machine] = new Dictionary<string, EnvVariable>(StringComparer.OrdinalIgnoreCase),
    };

    private readonly Dictionary<string, EnvironmentSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly List<SnapshotIndexEntry> _index = [];
    private int _sequence;

    /// <summary>注入初始内容（模拟"用户机器上本来就有这些变量"）。</summary>
    public void Seed(EnvScope scope, string name, string value, EnvValueKind kind = EnvValueKind.ExpandString) =>
        _store[scope][name] = new EnvVariable { Name = name, RawValue = value, Kind = kind };

    /// <summary>读取沙箱内当前值（仅测试断言使用）。</summary>
    public string? Peek(EnvScope scope, string name) =>
        _store[scope].TryGetValue(name, out var v) ? v.RawValue : null;

    /// <summary>读取沙箱内当前值类型。</summary>
    public EnvValueKind? PeekKind(EnvScope scope, string name) =>
        _store[scope].TryGetValue(name, out var v) ? v.Kind : null;

    /// <summary>沙箱内该作用域的变量总数。</summary>
    public int Count(EnvScope scope) => _store[scope].Count;

    /// <summary>已创建的快照数量。</summary>
    public int SnapshotCount => _snapshots.Count;

    /// <inheritdoc />
    public Result<EnvVariable?> Read(EnvScope scope, string name) =>
        Result<EnvVariable?>.Ok(_store[scope].TryGetValue(name, out var v) ? v : null);

    /// <inheritdoc />
    public Result<IReadOnlyList<EnvVariable>> ReadAll(EnvScope scope) =>
        Result<IReadOnlyList<EnvVariable>>.Ok([.. _store[scope].Values]);

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> CaptureSnapshot(string trigger, string? note = null, bool isManual = false, bool isBaseline = false)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{++_sequence:D4}-{Sanitize(trigger)}";
        var snapshot = new EnvironmentSnapshot
        {
            SchemaVersion = "1.0",
            SnapshotId = id,
            CreatedAt = DateTimeOffset.Now,
            Trigger = trigger,
            Note = note,
            UserVariables = [.. _store[EnvScope.User].Values],
            MachineVariables = [.. _store[EnvScope.Machine].Values],
            MachineScopeReadFailed = false,
        };

        _snapshots[id] = snapshot;
        _index.Insert(0, new SnapshotIndexEntry
        {
            SnapshotId = id,
            CreatedAt = snapshot.CreatedAt,
            Trigger = trigger,
            Note = note,
            IsManual = isManual,
            IsBaseline = isBaseline,
            SizeBytes = 0,
            ContentHash = "sandbox",
        });

        return Result<EnvironmentSnapshot>.Ok(snapshot);
    }

    /// <inheritdoc />
    public Result<IReadOnlyList<SnapshotIndexEntry>> ListSnapshots() =>
        Result<IReadOnlyList<SnapshotIndexEntry>>.Ok([.. _index]);

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> LoadSnapshot(string snapshotId) =>
        _snapshots.TryGetValue(snapshotId, out var snapshot)
            ? Result<EnvironmentSnapshot>.Ok(snapshot)
            : Result<EnvironmentSnapshot>.Fail(
                EnvStationErrorCodes.TxSnapshotInvalid, $"沙箱中不存在快照 {snapshotId}。");

    /// <inheritdoc />
    public Result<Unit> VerifySnapshot(string snapshotId) =>
        _snapshots.ContainsKey(snapshotId)
            ? Results.Ok()
            : Result<Unit>.Fail(EnvStationErrorCodes.TxSnapshotInvalid, $"沙箱中不存在快照 {snapshotId}。");

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> SetVariable(
        EnvScope scope, string name, string rawValue, EnvValueKind? kind, RiskLevel risk, string operation)
    {
        var captured = CaptureSnapshot(operation);
        if (captured.IsFailure)
        {
            return captured;
        }

        var effectiveKind = kind
            ?? (_store[scope].TryGetValue(name, out var existing) ? existing.Kind : EnvValueKind.ExpandString);

        _store[scope][name] = new EnvVariable
        {
            Name = name,
            RawValue = rawValue,
            Kind = effectiveKind,
            ContainsVariableReference = rawValue.Contains('%', StringComparison.Ordinal),
        };

        _ = risk;
        return captured;
    }

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> RemoveVariable(EnvScope scope, string name, RiskLevel risk, string operation)
    {
        var captured = CaptureSnapshot(operation);
        if (captured.IsFailure)
        {
            return captured;
        }

        _store[scope].Remove(name);
        _ = risk;
        return captured;
    }

    /// <inheritdoc />
    public Result<Unit> Restore(EnvironmentSnapshot snapshot, IReadOnlyList<string>? names)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var wanted = names is null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        foreach (var scope in new[] { EnvScope.User, EnvScope.Machine })
        {
            var target = scope == EnvScope.User ? snapshot.UserVariables : snapshot.MachineVariables;
            var targetByName = target.ToDictionary(static v => v.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var variable in target)
            {
                if (wanted is not null && !wanted.Contains(variable.Name))
                {
                    continue;
                }

                _store[scope][variable.Name] = variable;
            }

            if (wanted is not null)
            {
                continue;
            }

            foreach (var name in _store[scope].Keys.ToArray())
            {
                if (!targetByName.ContainsKey(name))
                {
                    _store[scope].Remove(name);
                }
            }
        }

        return Results.Ok();
    }

    /// <inheritdoc />
    public Result<PathSnapshot> ReadPath(EnvScope scope)
    {
        var exists = _store[scope].TryGetValue("PATH", out var variable);
        var raw = exists ? variable!.RawValue : string.Empty;
        var kind = exists ? variable!.Kind : EnvValueKind.ExpandString;

        // probeFileSystem: false —— 沙箱用例不应依赖真实磁盘内容，否则用例结论会随机器而变。
        // 需要验证"不存在目录"的用例请显式使用一个几乎不可能存在的路径。
        return Result<PathSnapshot>.Ok(new PathSnapshot(scope, raw, kind, PathParser.Parse(raw, probeFileSystem: true)));
    }

    /// <inheritdoc />
    public Result<EnvironmentSnapshot> WritePath(
        EnvScope scope, IReadOnlyList<PathEntry> entries, RiskLevel risk, string operation)
    {
        var value = PathParser.Join(entries);
        var kind = _store[scope].TryGetValue("PATH", out var p) ? p.Kind : EnvValueKind.ExpandString;
        return SetVariable(scope, "PATH", value, kind, risk, operation);
    }

    private static string Sanitize(string text)
    {
        var chars = text.Where(char.IsAsciiLetterOrDigit).Take(12).ToArray();
        return chars.Length == 0 ? "evt" : new string(chars);
    }
}
