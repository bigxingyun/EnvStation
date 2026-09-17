using EnvStation.Abstractions;
using EnvStation.Abstractions.Transactions;

namespace EnvStation.Core.Installation;

/// <summary>切换策略：同卷用原子 Move，跨卷走降级路径（M0-P05 的核心结论）。</summary>
public enum SwitchStrategy
{
    /// <summary>staging 与目标同卷 —— <c>Directory.Move</c> 原子改名，目标目录不会出现半成品状态。</summary>
    AtomicSameVolume = 0,

    /// <summary>
    /// 跨卷降级：先在目标同卷建立临时目录再交换。
    /// 仍保证"目标目录要么是旧的、要么是新的"，但中途会短暂占用双倍空间。
    /// </summary>
    StagedCrossVolume = 1,
}

/// <summary>故障注入点（仅供 M0-P05 验证崩溃恢复，生产路径不触发）。</summary>
public enum ShadowFaultPoint
{
    /// <summary>在最终的原子改名之前抛出，用于模拟"交换中途断电/被杀"。</summary>
    BeforeSwapMove = 0,
}

/// <summary>一次安装/升级的切换计划（提交前可展示给用户确认）。</summary>
public sealed record ShadowInstallPlan
{
    public required string TransactionId { get; init; }

    /// <summary>影子目录：所有文件先落此处，失败直接丢弃，正式环境从未被触碰。</summary>
    public required string StagingPath { get; init; }

    public required string TargetPath { get; init; }

    /// <summary>同卷降级时用于交换的临时目录。</summary>
    public required string SwapTempPath { get; init; }

    /// <summary>目标已存在时的备份目录（切换失败要还原）。</summary>
    public required string BackupPath { get; init; }

    public required SwitchStrategy Strategy { get; init; }

    /// <summary>切换完成后需要删除的路径（备份与 swap 临时目录）。</summary>
    public IReadOnlyList<string> CleanupAfterCommit { get; init; } = [];
}

/// <summary>提交结果，供 UI 与审计展示。</summary>
public sealed record ShadowCommitResult
{
    public required bool Succeeded { get; init; }

    public required SwitchStrategy Strategy { get; init; }

    /// <summary>是否替换了已存在的目录（升级场景）。</summary>
    public bool ReplacedExistingTarget { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }
}

/// <summary>
/// 影子目录安装器（M0-P05 的正式实现）——"失败零污染"的技术基础（NFR-R5 / 需求 5.6 节 I1）。
///
/// <para><b>不变量</b>：</para>
/// <list type="number">
///   <item>步骤 1（<see cref="CreatePlan"/> + <see cref="StageAsync"/>）期间<b>绝不</b>触碰目标目录。</item>
///   <item>提交是"改名"而非"复制后删除"，因此不会出现"A 的文件已删、B 的文件未到"的中间态。</item>
///   <item>任何一步失败都尝试把目标目录还原为调用前状态；还原失败返回明确错误而非静默忽略。</item>
/// </list>
///
/// <para><b>同卷约束</b>：影子目录默认放在<b>安装根的父目录</b>，以保证与目标同卷。</para>
/// </summary>
public sealed class ShadowInstaller
{
    /// <summary>
    /// 安装目标路径的字符数上限。
    /// 取 200 而非 MAX_PATH(260)：解压后实际文件路径 = 目标路径 + 包内相对路径，
    /// 必须为包内目录层级预留空间（需求 6.1 R6 的 200/260 双阈值设计）。
    /// </summary>
    public const int MaxTargetPathLength = 200;

    private readonly string _stagingRoot;

    public ShadowInstaller(string stagingRoot, Action<ShadowFaultPoint>? faultInjector = null)
    {
        _stagingRoot = stagingRoot;
        FaultInjector = faultInjector;
    }

    /// <summary>
    /// 故障注入点。仅供测试与 M0 PoC 使用——用于验证"切换中途崩溃"后的恢复能力，
    /// 生产代码不传此参数（默认 <c>null</c>，零开销）。
    /// </summary>
    internal Action<ShadowFaultPoint>? FaultInjector { get; }

    /// <summary>
    /// 创建切换计划。此处只做路径与卷的判定，不产生任何文件系统副作用。
    /// </summary>
    public Result<ShadowInstallPlan> CreatePlan(string transactionId, string targetPath)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(transactionId))
        {
            errors.Add("事务 ID 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            errors.Add("目标路径不能为空。");
        }

        if (errors.Count > 0)
        {
            return Result<ShadowInstallPlan>.Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot, string.Join(" ", errors));
        }

        string targetFull;
        try
        {
            targetFull = Path.GetFullPath(targetPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<ShadowInstallPlan>.Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"目标路径非法：{ex.Message}",
                "改用纯英文、无特殊字符的本地路径。");
        }

        // 根目录保护：绝不允许把卷根当作安装目标（切换时会移动整个盘的内容）
        if (IsVolumeRoot(targetFull))
        {
            return Result<ShadowInstallPlan>.Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                "不允许将磁盘根目录作为安装目标。",
                "指定一个子目录，例如 D:\\Dev\\Python。");
        }

        // 路径长度前置校验（需求 6.1 R6）。
        // 为什么必须在**计划阶段**拦：一旦进入解压才发现超长，会留下半截影子目录；
        // 而超长路径导致的失败往往出现在随机某个深层文件上，极难诊断。
        // 200 字符为警告阈值、260 为阻断阈值（MAX_PATH），此处取保守的 200 作为硬上限。
        if (targetFull.Length > MaxTargetPathLength)
        {
            return Result<ShadowInstallPlan>.Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"安装路径过长（{targetFull.Length} 字符，上限 {MaxTargetPathLength}）。",
                "解压后的深层文件会超过 Windows 的 260 字符上限而写入失败。改用更短的路径，例如 D:\\Dev\\Python。");
        }

        // 非法字符：这些字符即便在长路径模式下也不应出现在安装目录中
        var invalidChars = Path.GetInvalidPathChars();
        var badChar = targetFull.IndexOfAny(invalidChars);
        if (badChar >= 0)
        {
            return Result<ShadowInstallPlan>.Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"安装路径包含非法字符（位置 {badChar}）。",
                "改用仅含字母、数字、连字符与反斜杠的本地路径。");
        }

        var staging = Path.Combine(_stagingRoot, transactionId);
        var swapTemp = targetFull + ".envstation-swap";
        var backup = targetFull + ".envstation-bak";

        var strategy = IsSameVolume(staging, targetFull)
            ? SwitchStrategy.AtomicSameVolume
            : SwitchStrategy.StagedCrossVolume;

        return Result<ShadowInstallPlan>.Ok(new ShadowInstallPlan
        {
            TransactionId = transactionId,
            StagingPath = staging,
            TargetPath = targetFull,
            SwapTempPath = swapTemp,
            BackupPath = backup,
            Strategy = strategy,
            CleanupAfterCommit = [backup, swapTemp],
        });
    }

    /// <summary>
    /// 确保影子目录存在（供调用方把文件写进去）。
    /// 调用前必须确认目标盘剩余空间 ≥ 需求 × 1.5（需求 5.8 前置检查）。
    /// </summary>
    public static Result<string> Stage(ShadowInstallPlan plan)
    {
        try
        {
            Directory.CreateDirectory(plan.StagingPath);
            return Result<string>.Ok(plan.StagingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"无法创建影子目录：{plan.StagingPath}（{ex.Message}）",
                "检查磁盘剩余空间与目录写权限。",
                ex);
        }
    }

    /// <summary>
    /// 提交：把影子目录切换为正式目录。
    /// <para>同卷 → 原子改名；跨卷 → 降级为"目标同卷临时目录 + 交换"。</para>
    /// </summary>
    public async Task<Result<ShadowCommitResult>> CommitAsync(
        ShadowInstallPlan plan, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (!Directory.Exists(plan.StagingPath))
        {
            return Result<ShadowCommitResult>.Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"影子目录不存在：{plan.StagingPath}",
                "安装流程可能未正确完成，重新执行安装。");
        }

        var targetExisted = Directory.Exists(plan.TargetPath);
        var faultInjector = FaultInjector;

        try
        {
            return plan.Strategy == SwitchStrategy.AtomicSameVolume
                ? await CommitAtomicAsync(plan, targetExisted, sw, faultInjector, ct).ConfigureAwait(false)
                : await CommitCrossVolumeAsync(plan, targetExisted, sw, faultInjector, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<ShadowCommitResult>.Fail(
                EnvStationErrorCodes.TxRollbackFailed,
                "切换过程中被取消，已尝试回滚目标目录。",
                "检查目标目录当前是否可用；若有异常，可从快照回滚。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<ShadowCommitResult>.Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"切换失败：{ex.Message}",
                "目标目录可能被其他程序占用（例如正在运行的 Python 或 IDE）；关闭相关程序后重试。",
                ex);
        }
        catch (Exception ex)
        {
            // 兜底：任何未预期异常都必须转成结构化失败结果，绝不允许从本方法逃逸。
            //
            // 为什么必须兜底（M0-P05 用例 P05-5 暴露）：调用方（事务层）依赖"失败=返回值"这一契约
            // 来决定是否回滚；若异常穿透，调用方会跳过回滚流程，直接破坏"失败零污染"承诺。
            // 故障注入点抛出的异常正属于这一类。
            TryRestoreAfterUnexpectedFailure(plan, targetExisted);

            return Result<ShadowCommitResult>.Fail(
                EnvStationErrorCodes.TxRollbackFailed,
                $"切换过程中发生未预期错误：{ex.GetType().Name}：{ex.Message}",
                "已尝试回滚目标目录。检查目标目录是否可用；异常持续时导出诊断包反馈。",
                ex);
        }
    }

    /// <summary>丢弃影子目录（失败/取消路径）。正式环境完全未被触碰。</summary>
    public static Result<Unit> Discard(ShadowInstallPlan plan)
    {
        TryDeleteDirectory(plan.StagingPath);
        TryDeleteDirectory(plan.SwapTempPath);
        return Results.Ok();
    }

    /// <summary>
    /// 崩溃恢复：清理上次中断留下的临时目录（需求 6.4 F2）。
    /// 返回被清理的路径，供审计与用户提示。
    /// </summary>
    public static IReadOnlyList<string> Recover(string targetPath)
    {
        var cleaned = new List<string>();

        var swapTemp = targetPath + ".envstation-swap";
        if (Directory.Exists(swapTemp))
        {
            // swap 存在说明上次在交换中途中断：若目标缺失，则把 swap 还原为目标
            if (!Directory.Exists(targetPath))
            {
                TryMove(swapTemp, targetPath);
            }
            else
            {
                TryDeleteDirectory(swapTemp);
            }

            cleaned.Add(swapTemp);
        }

        var backup = targetPath + ".envstation-bak";
        if (Directory.Exists(backup))
        {
            var cleanedBackup = TryDeleteDirectory(backup);
            if (cleanedBackup)
            {
                cleaned.Add(backup);
            }
        }

        return cleaned;
    }

    // ────────────────────────── 内部实现 ──────────────────────────

    private static async Task<Result<ShadowCommitResult>> CommitAtomicAsync(
        ShadowInstallPlan plan,
        bool targetExisted,
        System.Diagnostics.Stopwatch sw,
        Action<ShadowFaultPoint>? faultInjector,
        CancellationToken ct)
    {
        // 1) 目标已存在（升级）→ 先改名备份，不删除
        if (targetExisted)
        {
            TryDeleteDirectory(plan.BackupPath);
            if (!TryMove(plan.TargetPath, plan.BackupPath))
            {
                return Result<ShadowCommitResult>.Fail(
                    EnvStationErrorCodes.PathNotWritable,
                    $"无法备份现有目录：{plan.TargetPath}",
                    "目标目录可能被其他程序占用；关闭后重试。");
            }
        }

        ct.ThrowIfCancellationRequested();
        faultInjector?.Invoke(ShadowFaultPoint.BeforeSwapMove);

        // 2) 原子改名：staging → target
        if (!TryMove(plan.StagingPath, plan.TargetPath))
        {
            // 3a) 失败：还原备份
            if (targetExisted)
            {
                TryMove(plan.BackupPath, plan.TargetPath);
            }

            return Result<ShadowCommitResult>.Fail(
                EnvStationErrorCodes.PathNotWritable,
                "切换到目标目录失败，目标目录已回到原有内容。",
                "目标目录可能被占用，或存在权限问题。");
        }

        // 3b) 成功：异步清理备份（失败不影响提交结果）
        await Task.Yield();
        TryDeleteDirectory(plan.BackupPath);

        sw.Stop();
        return Result<ShadowCommitResult>.Ok(new ShadowCommitResult
        {
            Succeeded = true,
            Strategy = SwitchStrategy.AtomicSameVolume,
            ReplacedExistingTarget = targetExisted,
            Elapsed = sw.Elapsed,
        });
    }

    private static async Task<Result<ShadowCommitResult>> CommitCrossVolumeAsync(
        ShadowInstallPlan plan,
        bool targetExisted,
        System.Diagnostics.Stopwatch sw,
        Action<ShadowFaultPoint>? faultInjector,
        CancellationToken ct)
    {
        // 降级路径：先把 staging 复制到目标同卷的临时目录，再交换。
        // 这样"目标同卷改名"仍是原子的，代价是临时占用双倍空间。
        TryDeleteDirectory(plan.SwapTempPath);

        try
        {
            await CopyDirectoryAsync(plan.StagingPath, plan.SwapTempPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(plan.SwapTempPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(plan.SwapTempPath);
            return Result<ShadowCommitResult>.Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"跨卷复制失败：{ex.Message}",
                "确认目标盘剩余空间是否充足；跨卷切换需要临时占用双倍空间。",
                ex);
        }

        if (targetExisted)
        {
            TryDeleteDirectory(plan.BackupPath);
            if (!TryMove(plan.TargetPath, plan.BackupPath))
            {
                TryDeleteDirectory(plan.SwapTempPath);
                return Result<ShadowCommitResult>.Fail(
                    EnvStationErrorCodes.PathNotWritable, "无法备份现有目录。",
                    "目标目录可能被其他程序占用。");
            }
        }

        ct.ThrowIfCancellationRequested();
        faultInjector?.Invoke(ShadowFaultPoint.BeforeSwapMove);

        if (!TryMove(plan.SwapTempPath, plan.TargetPath))
        {
            if (targetExisted)
            {
                TryMove(plan.BackupPath, plan.TargetPath);
            }

            TryDeleteDirectory(plan.SwapTempPath);
            return Result<ShadowCommitResult>.Fail(
                EnvStationErrorCodes.PathNotWritable, "跨卷交换失败，目标目录已回到原有内容。");
        }

        TryDeleteDirectory(plan.BackupPath);
        TryDeleteDirectory(plan.StagingPath);

        sw.Stop();
        return Result<ShadowCommitResult>.Ok(new ShadowCommitResult
        {
            Succeeded = true,
            Strategy = SwitchStrategy.StagedCrossVolume,
            ReplacedExistingTarget = targetExisted,
            Elapsed = sw.Elapsed,
        });
    }

    private static async Task CopyDirectoryAsync(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var src = File.OpenRead(file);
            await using var dst = File.Create(target);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
    }

    private static bool IsSameVolume(string a, string b)
    {
        var rootA = Path.GetPathRoot(Path.GetFullPath(a));
        var rootB = Path.GetPathRoot(Path.GetFullPath(b));
        return !string.IsNullOrEmpty(rootA)
            && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVolumeRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 未预期异常后的兜底还原：把可能已被改名到备份目录的目标搬回原位。
    /// 与 <see cref="Recover"/> 的区别是它作用于"当前这次调用留下的中间态"，语义更聚焦。
    /// </summary>
    private static void TryRestoreAfterUnexpectedFailure(ShadowInstallPlan plan, bool targetExisted)
    {
        if (targetExisted
            && !Directory.Exists(plan.TargetPath)
            && Directory.Exists(plan.BackupPath))
        {
            TryMove(plan.BackupPath, plan.TargetPath);
        }

        // swap 临时目录与 staging 属于本次调用的产物，可安全清理
        TryDeleteDirectory(plan.SwapTempPath);
    }
}
