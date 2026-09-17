using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Serialization;
using EnvStation.Core.Environment;

namespace EnvStation.Core.Transactions;

/// <summary>快照存储的抽象，便于测试注入与未来的远端存储扩展。</summary>
public interface ISnapshotStore
{
    /// <summary>快照根目录。</summary>
    string RootPath { get; }

    /// <summary>
    /// 采集当前环境并落盘为快照。
    /// <paramref name="trigger"/> 用于审计与时间线展示（如 <c>env.set PATH</c>）。
    /// </summary>
    Result<EnvironmentSnapshot> Capture(string trigger, string? note = null, bool isManual = false, bool isBaseline = false);

    /// <summary>读取并校验指定快照；校验失败返回 <see cref="EnvStationErrorCodes.TxSnapshotInvalid"/>。</summary>
    Result<EnvironmentSnapshot> Load(string snapshotId);

    /// <summary>列出全部快照的轻量索引（时间倒序）。</summary>
    Result<IReadOnlyList<SnapshotIndexEntry>> List();

    /// <summary>删除快照（同时清理哈希文件与索引项）。</summary>
    Result<Unit> Delete(string snapshotId);

    /// <summary>校验快照完整性（NFR-R4：损坏即禁止后续写操作）。</summary>
    Result<Unit> Verify(string snapshotId);

    /// <summary>
    /// 按保留策略裁剪自动快照。
    /// 返回"本应被清理但因超限而提示"的条目——设计上<b>不静默删除</b>（需求 9.4）。
    /// </summary>
    Result<IReadOnlyList<SnapshotIndexEntry>> PruneAutoSnapshots(int keep);

    // ── 未完成事务标记（需求 6.4 F2 / NFR-R2 的实现载体）──

    /// <summary>写入"未完成事务"标记（事务开始前）。</summary>
    Result<Unit> WritePendingMarker(PendingTransactionMarker marker);

    /// <summary>读取"未完成事务"标记；不存在返回 <c>null</c> 值的成功结果。</summary>
    Result<PendingTransactionMarker?> ReadPendingMarker();

    /// <summary>清除"未完成事务"标记（事务成功结束或已回滚后）。</summary>
    Result<Unit> ClearPendingMarker();
}

/// <summary>
/// 基于文件系统的快照存储（M0-P06 的正式实现）。
///
/// <para><b>存储布局</b>（见《详细设计文档》8.1 节）：</para>
/// <code>
/// &lt;root&gt;/
///   index.json                     快照索引（轻量，列表用）
///   &lt;snapshotId&gt;.json             快照正文
///   &lt;snapshotId&gt;.sha256           内容哈希
///   pending-transaction.json       未完成事务标记
/// </code>
///
/// <para><b>完整性（NFR-R4）</b>：写入后立即重读并校验哈希；任何损坏都会让上层拒绝后续写操作。</para>
/// <para><b>原子写</b>：先写临时文件再替换，避免断电产生半截 JSON（需求 6.4 F2）。</para>
/// </summary>
public sealed class SnapshotStore : ISnapshotStore
{
    public const string SchemaVersion = "1.0";

    private const string IndexFileName = "index.json";

    private const string PendingMarkerFileName = "pending-transaction.json";

    private readonly IEnvironmentStore _user;
    private readonly IEnvironmentStore _machine;

    public SnapshotStore(string rootPath, IEnvironmentStore user, IEnvironmentStore machine)
    {
        RootPath = rootPath;
        _user = user;
        _machine = machine;
    }

    public string RootPath { get; }

    /// <summary>索引文件路径。</summary>
    public string IndexPath => Path.Combine(RootPath, IndexFileName);

    /// <summary>未完成事务标记路径。</summary>
    public string PendingMarkerPath => Path.Combine(RootPath, PendingMarkerFileName);

    public Result<EnvironmentSnapshot> Capture(
        string trigger, string? note = null, bool isManual = false, bool isBaseline = false)
    {
        var ensure = EnsureRoot();
        if (ensure.IsFailure)
        {
            return ensure.Propagate<EnvironmentSnapshot>();
        }

        var userRead = _user.ReadAll();
        if (userRead.IsFailure)
        {
            return userRead.Propagate<EnvironmentSnapshot>();
        }

        // 系统级读取失败不阻断用户级快照，但要显式记录，避免还原时误判（P02-7）
        var machineRead = _machine.ReadAll();
        var machineVars = machineRead.IsSuccess
            ? machineRead.Value
            : (IReadOnlyList<EnvVariable>)[];

        var snapshotId = BuildSnapshotId(trigger, isManual, isBaseline);
        var createdAt = DateTimeOffset.Now;
        var previous = isBaseline ? null : TryLoadLatestForDiff();

        var changes = ComputeChanges(previous, userRead.Value, machineVars);

        var draft = new EnvironmentSnapshot
        {
            SchemaVersion = SchemaVersion,
            SnapshotId = snapshotId,
            CreatedAt = createdAt,
            Trigger = trigger,
            Note = note,
            UserVariables = userRead.Value,
            MachineVariables = machineVars,
            MachineScopeReadFailed = machineRead.IsFailure,
            HasPendingTransaction = File.Exists(PendingMarkerPath),
            Changes = changes,
        };

        // 先算哈希（不含 ContentHash 字段本身），再写入完整对象
        var hash = ComputeHash(draft);
        var snapshot = draft with { ContentHash = hash };

        var writeResult = WriteSnapshot(snapshot);
        if (writeResult.IsFailure)
        {
            return writeResult.Propagate<EnvironmentSnapshot>();
        }

        // 写后立即校验：磁盘损坏必须在本次调用内暴露，而不是等到还原时（NFR-R4）
        var verify = Verify(snapshotId);
        if (verify.IsFailure)
        {
            return verify.Propagate<EnvironmentSnapshot>();
        }

        var indexUpdate = AppendIndex(snapshot, isManual, isBaseline);
        if (indexUpdate.IsFailure)
        {
            return indexUpdate.Propagate<EnvironmentSnapshot>();
        }

        return Result<EnvironmentSnapshot>.Ok(snapshot);
    }

    public Result<EnvironmentSnapshot> Load(string snapshotId)
    {
        var path = SnapshotPath(snapshotId);
        if (!File.Exists(path))
        {
            return Result<EnvironmentSnapshot>.Fail(
                EnvStationErrorCodes.TxSnapshotInvalid,
                $"快照 {snapshotId} 不存在。",
                "在「快照与历史」中刷新列表，确认该快照是否已被清理。");
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var snapshot = JsonSerializer.Deserialize(json, EnvStationJsonContext.Default.EnvironmentSnapshot);
            if (snapshot is null)
            {
                return Result<EnvironmentSnapshot>.Fail(
                    EnvStationErrorCodes.TxSnapshotInvalid, $"快照 {snapshotId} 内容为空或无法解析。");
            }

            return Result<EnvironmentSnapshot>.Ok(snapshot);
        }
        catch (JsonException ex)
        {
            return Result<EnvironmentSnapshot>.Fail(
                EnvStationErrorCodes.TxSnapshotInvalid,
                $"快照 {snapshotId} 解析失败：{ex.Message}",
                "快照文件可能已损坏。此状态下环境站拒绝执行写操作。",
                ex);
        }
    }

    public Result<IReadOnlyList<SnapshotIndexEntry>> List()
    {
        var index = ReadIndex();
        return index.IsFailure
            ? index.Propagate<IReadOnlyList<SnapshotIndexEntry>>()
            : Result<IReadOnlyList<SnapshotIndexEntry>>.Ok(index.Value.Entries);
    }

    public Result<Unit> Verify(string snapshotId)
    {
        var loaded = Load(snapshotId);
        if (loaded.IsFailure)
        {
            return loaded.Discard();
        }

        var snapshot = loaded.Value;
        var hashPath = HashPath(snapshotId);

        // 期望哈希：优先取同目录的 .sha256 文件；缺失则退回快照内嵌哈希
        string? expected = File.Exists(hashPath)
            ? File.ReadAllText(hashPath, Encoding.UTF8).Trim()
            : snapshot.ContentHash;

        if (string.IsNullOrEmpty(expected))
        {
            return Results.Fail(
                EnvStationErrorCodes.TxSnapshotInvalid,
                $"快照 {snapshotId} 缺少完整性哈希。",
                "无法确认快照未被篡改，已拒绝后续写操作。");
        }

        var actual = ComputeHash(snapshot with { ContentHash = null });
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Fail(
                EnvStationErrorCodes.TxSnapshotInvalid,
                $"快照 {snapshotId} 完整性校验失败（内容与哈希不一致）。",
                "快照文件可能被外部修改或磁盘损坏。为保证可回滚性，环境站已拒绝后续写操作。");
        }

        return Results.Ok();
    }

    public Result<Unit> Delete(string snapshotId)
    {
        try
        {
            var path = SnapshotPath(snapshotId);
            var hashPath = HashPath(snapshotId);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(hashPath))
            {
                File.Delete(hashPath);
            }

            return RemoveFromIndex(snapshotId);
        }
        catch (IOException ex)
        {
            return Results.Fail(
                EnvStationErrorCodes.PathNotWritable, $"删除快照 {snapshotId} 失败：{ex.Message}");
        }
    }

    public Result<IReadOnlyList<SnapshotIndexEntry>> PruneAutoSnapshots(int keep)
    {
        var index = ReadIndex();
        if (index.IsFailure)
        {
            return index.Propagate<IReadOnlyList<SnapshotIndexEntry>>();
        }

        var auto = index.Value.Entries
            .Where(e => !e.IsManual && !e.IsBaseline)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

        // 多余项不静默删除——交由上层提示用户确认（需求 9.4）
        var excess = auto.Skip(Math.Max(0, keep)).ToList();
        return Result<IReadOnlyList<SnapshotIndexEntry>>.Ok(excess);
    }

    // ────────────────────────── 未完成事务标记 ──────────────────────────

    /// <summary>写入"未完成事务"标记（事务开始前）。</summary>
    public Result<Unit> WritePendingMarker(PendingTransactionMarker marker)
    {
        var ensure = EnsureRoot();
        if (ensure.IsFailure)
        {
            return ensure;
        }

        return WriteJsonAtomic(PendingMarkerPath, marker, EnvStationJsonContext.Default.PendingTransactionMarker);
    }

    /// <summary>读取"未完成事务"标记；不存在返回 <c>null</c>。</summary>
    public Result<PendingTransactionMarker?> ReadPendingMarker()
    {
        if (!File.Exists(PendingMarkerPath))
        {
            return Result<PendingTransactionMarker?>.Ok(null);
        }

        try
        {
            var json = File.ReadAllText(PendingMarkerPath, Encoding.UTF8);
            var marker = JsonSerializer.Deserialize(json, EnvStationJsonContext.Default.PendingTransactionMarker);
            return Result<PendingTransactionMarker?>.Ok(marker);
        }
        catch (JsonException ex)
        {
            return Result<PendingTransactionMarker?>.Fail(
                EnvStationErrorCodes.TxSnapshotInvalid,
                $"未完成事务标记无法解析：{ex.Message}",
                "上次执行可能被中断，可从最近的快照回滚。");
        }
    }

    /// <summary>清除"未完成事务"标记（事务成功结束或已回滚后）。</summary>
    public Result<Unit> ClearPendingMarker()
    {
        try
        {
            if (File.Exists(PendingMarkerPath))
            {
                File.Delete(PendingMarkerPath);
            }

            return Results.Ok();
        }
        catch (IOException ex)
        {
            return Results.Fail(
                EnvStationErrorCodes.PathNotWritable, $"清除未完成事务标记失败：{ex.Message}");
        }
    }

    // ────────────────────────── 内部实现 ──────────────────────────

    /// <summary>计算内容哈希。<paramref name="snapshot"/> 的 ContentHash 字段会被忽略。</summary>
    internal static string ComputeHash(EnvironmentSnapshot snapshot)
    {
        var canonical = snapshot with { ContentHash = null };
        var json = JsonSerializer.Serialize(canonical, EnvStationJsonContext.Default.EnvironmentSnapshot);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));

        // 注：Convert.ToHexStringLower 是 .NET 9 API；本项目基线为 .NET 8，使用 ToHexString + ToLowerInvariant
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string SnapshotPath(string snapshotId) => Path.Combine(RootPath, snapshotId + ".json");

    private string HashPath(string snapshotId) => Path.Combine(RootPath, snapshotId + ".sha256");

    private Result<Unit> EnsureRoot()
    {
        try
        {
            Directory.CreateDirectory(RootPath);
            return Results.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"无法创建快照目录：{RootPath}",
                "检查路径是否存在、是否有写权限；也可以在设置中改到其他磁盘。",
                ex);
        }
    }

    private static string BuildSnapshotId(string trigger, bool isManual, bool isBaseline)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..4];
        var kind = isBaseline ? "baseline" : isManual ? "manual" : "auto";
        var slug = Slugify(trigger);
        return $"{stamp}-{kind}-{slug}-{suffix}";
    }

    private static string Slugify(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (ch is ' ' or '.' or '_' or '-')
            {
                sb.Append('-');
            }
        }

        var s = sb.ToString().Trim('-');
        if (s.Length == 0)
        {
            s = "snapshot";
        }

        return s.Length > 32 ? s[..32] : s;
    }

    private Result<Unit> WriteSnapshot(EnvironmentSnapshot snapshot)
    {
        var path = SnapshotPath(snapshot.SnapshotId);
        var write = WriteJsonAtomic(path, snapshot, EnvStationJsonContext.Default.EnvironmentSnapshot);
        if (write.IsFailure)
        {
            return write;
        }

        try
        {
            File.WriteAllText(HashPath(snapshot.SnapshotId), snapshot.ContentHash ?? string.Empty, Encoding.UTF8);
            return Results.Ok();
        }
        catch (IOException ex)
        {
            return Results.Fail(
                EnvStationErrorCodes.PathNotWritable, $"写入快照哈希文件失败：{ex.Message}", detail: ex);
        }
    }

    /// <summary>原子写 JSON：先写 .tmp，再替换目标文件（防断电产生半截文件）。</summary>
    private static Result<Unit> WriteJsonAtomic<T>(
        string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var tmp = path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(value, typeInfo);
            File.WriteAllText(tmp, json, Encoding.UTF8);

            if (File.Exists(path))
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }

            return Results.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            return Results.Fail(
                EnvStationErrorCodes.PathNotWritable, $"写入文件失败：{path}（{ex.Message}）", detail: ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响主流程
        }
    }

    private Result<SnapshotIndex> ReadIndex()
    {
        if (!File.Exists(IndexPath))
        {
            return Result<SnapshotIndex>.Ok(new SnapshotIndex
            {
                SchemaVersion = SchemaVersion,
                Entries = [],
            });
        }

        try
        {
            var json = File.ReadAllText(IndexPath, Encoding.UTF8);
            var index = JsonSerializer.Deserialize(json, EnvStationJsonContext.Default.SnapshotIndex);
            return Result<SnapshotIndex>.Ok(index ?? new SnapshotIndex
            {
                SchemaVersion = SchemaVersion,
                Entries = [],
            });
        }
        catch (JsonException)
        {
            // 索引损坏不应阻断快照使用——降级为扫描目录重建
            return Result<SnapshotIndex>.Ok(RebuildIndexFromDisk());
        }
    }

    private SnapshotIndex RebuildIndexFromDisk()
    {
        var entries = new List<SnapshotIndexEntry>();
        foreach (var file in Directory.EnumerateFiles(RootPath, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(name, "index", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var loaded = Load(name);
            if (loaded.IsFailure)
            {
                continue;
            }

            var s = loaded.Value;
            entries.Add(new SnapshotIndexEntry
            {
                SnapshotId = s.SnapshotId,
                CreatedAt = s.CreatedAt,
                Trigger = s.Trigger,
                Note = s.Note,
                IsManual = s.SnapshotId.Contains("-manual-", StringComparison.Ordinal),
                IsBaseline = s.SnapshotId.Contains("-baseline-", StringComparison.Ordinal),
                SizeBytes = new FileInfo(file).Length,
                ContentHash = s.ContentHash ?? string.Empty,
            });
        }

        return new SnapshotIndex
        {
            SchemaVersion = SchemaVersion,
            Entries = [.. entries.OrderByDescending(e => e.CreatedAt)],
        };
    }

    private Result<Unit> AppendIndex(EnvironmentSnapshot snapshot, bool isManual, bool isBaseline)
    {
        var index = ReadIndex();
        if (index.IsFailure)
        {
            return index.Discard();
        }

        var entry = new SnapshotIndexEntry
        {
            SnapshotId = snapshot.SnapshotId,
            CreatedAt = snapshot.CreatedAt,
            Trigger = snapshot.Trigger,
            Note = snapshot.Note,
            IsManual = isManual,
            IsBaseline = isBaseline,
            SizeBytes = new FileInfo(SnapshotPath(snapshot.SnapshotId)).Length,
            ContentHash = snapshot.ContentHash ?? string.Empty,
        };

        var updated = new SnapshotIndex
        {
            SchemaVersion = SchemaVersion,
            Entries = [entry, .. index.Value.Entries],
        };

        return WriteJsonAtomic(IndexPath, updated, EnvStationJsonContext.Default.SnapshotIndex);
    }

    private Result<Unit> RemoveFromIndex(string snapshotId)
    {
        var index = ReadIndex();
        if (index.IsFailure)
        {
            return index.Discard();
        }

        var updated = new SnapshotIndex
        {
            SchemaVersion = SchemaVersion,
            Entries = [.. index.Value.Entries.Where(e => e.SnapshotId != snapshotId)],
        };

        return WriteJsonAtomic(IndexPath, updated, EnvStationJsonContext.Default.SnapshotIndex);
    }

    private EnvironmentSnapshot? TryLoadLatestForDiff()
    {
        var index = ReadIndex();
        if (index.IsFailure || index.Value.Entries.Count == 0)
        {
            return null;
        }

        var latest = index.Value.Entries.OrderByDescending(e => e.CreatedAt).First();
        var loaded = Load(latest.SnapshotId);
        return loaded.IsSuccess ? loaded.Value : null;
    }

    private static List<SnapshotChangeSummary> ComputeChanges(
        EnvironmentSnapshot? previous,
        IReadOnlyList<EnvVariable> user,
        IReadOnlyList<EnvVariable> machine)
    {
        if (previous is null)
        {
            return [];
        }

        var changes = new List<SnapshotChangeSummary>();
        DiffScope(EnvScope.User, previous.UserVariables, user, changes);
        DiffScope(EnvScope.Machine, previous.MachineVariables, machine, changes);
        return changes;
    }

    private static void DiffScope(
        EnvScope scope,
        IReadOnlyList<EnvVariable> oldVars,
        IReadOnlyList<EnvVariable> newVars,
        List<SnapshotChangeSummary> sink)
    {
        // Windows 环境变量名大小写不敏感（PE-2）
        var oldMap = oldVars.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        var newMap = newVars.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, newVar) in newMap)
        {
            if (!oldMap.TryGetValue(name, out var oldVar))
            {
                sink.Add(new SnapshotChangeSummary
                {
                    Scope = scope, Name = newVar.Name, Kind = ChangeKind.Added, NewValue = newVar.RawValue,
                });
            }
            else if (!string.Equals(oldVar.RawValue, newVar.RawValue, StringComparison.Ordinal))
            {
                sink.Add(new SnapshotChangeSummary
                {
                    Scope = scope, Name = newVar.Name, Kind = ChangeKind.Modified,
                    OldValue = oldVar.RawValue, NewValue = newVar.RawValue,
                });
            }
        }

        foreach (var (name, oldVar) in oldMap)
        {
            if (!newMap.ContainsKey(name))
            {
                sink.Add(new SnapshotChangeSummary
                {
                    Scope = scope, Name = oldVar.Name, Kind = ChangeKind.Removed, OldValue = oldVar.RawValue,
                });
            }
        }
    }
}
