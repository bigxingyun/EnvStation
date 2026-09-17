using System.Collections.Immutable;
using System.Text;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Environment;
using EnvStation.Core.Scripting;

namespace EnvStation.Core.Actions;

/// <summary>
/// 变量作用域实现（需求 20.7）。
/// </summary>
/// <remarks>
/// <para>
/// 五个作用域：<c>pkg.*</c>（包私有，本次运行内可写）、<c>user.*</c>（安装向导输入）、
/// <c>sys.*</c>（只读探测结果）、<c>env.*</c>（目标环境变量，只读）、<c>secret.*</c>（敏感输入）。
/// </para>
/// <para>
/// <b>隔离纪律</b>：包只能写 <c>pkg.*</c>。宿主写入其余作用域。这样"包读不到别的包的数据"
/// 与"包改不了探测结果"这两件事是由接口形状保证的，而不是靠约定（需求 TH-5）。
/// </para>
/// <para>
/// <b>secret.* 的三条硬性要求</b>（需求 20.7）：不落盘、日志中替换为 <c>***</c>、
/// 导出报告时剔除。本类通过 <see cref="Redact"/> 统一提供脱敏，任何写日志的路径都必须经过它。
/// </para>
/// </remarks>
public sealed class VariableTable : IVariableScope, IVariableResolver
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<string> _secretValues = [];

    /// <summary>已被登记的敏感字面量数量（用于自检：不应为 0 却出现 secret 读取）。</summary>
    public int SecretCount => _secretValues.Count;

    /// <summary>写入一个变量（宿主使用）。同名覆盖。</summary>
    public void Set(string qualifiedName, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(qualifiedName);
        _values[qualifiedName] = value;

        if (qualifiedName.StartsWith("secret.", StringComparison.Ordinal) && value.Length > 0)
        {
            _secretValues.Add(value);
        }
    }

    /// <summary>写入 <c>pkg.*</c> 变量（包与动作使用）。</summary>
    public void SetPackageVariable(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _values[$"pkg.{name}"] = value;
    }

    /// <summary>按限定名读取；不存在返回 null。</summary>
    public string? Get(string qualifiedName) =>
        _values.TryGetValue(qualifiedName, out var v) ? v : null;

    /// <summary>是否存在该变量。</summary>
    public bool Exists(string qualifiedName) => _values.ContainsKey(qualifiedName);

    /// <summary>列出某个作用域下的全部变量（<c>secret.*</c> 一律以 <c>***</c> 返回）。</summary>
    public IEnumerable<KeyValuePair<string, string>> ListScope(string scope)
    {
        var prefix = scope + ".";
        foreach (var (key, value) in _values)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new KeyValuePair<string, string>(
                key,
                key.StartsWith("secret.", StringComparison.Ordinal) ? "***" : value);
        }
    }

    /// <summary>枚举全部变量名（不返回值，供报告列出"本次使用了哪些变量"）。</summary>
    public IEnumerable<string> Names => _values.Keys;

    /// <summary>
    /// 脱敏：把出现过的敏感字面量替换为 <c>***</c>。
    /// <b>任何进入日志、审计、报告、异常消息的文本都必须先过这里。</b>
    /// </summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text) || _secretValues.Count == 0)
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var secret in _secretValues)
        {
            if (secret.Length >= 3)
            {
                result = result.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return result;
    }

    /// <inheritdoc />
    ExprValue IVariableResolver.Resolve(string qualifiedName) =>
        _values.TryGetValue(qualifiedName, out var v)
            ? InferredExprValue(v)
            : ExprValue.Missing;

    /// <summary>
    /// 变量一律以字符串存储，但 <c>true</c>/<c>false</c> 与<b>规范写法</b>的数字会被推断为对应类型，
    /// 这样 <c>${py.found} == false</c> 这类直观写法才能工作。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 推断规则刻意保守——只有严格等于 <c>true</c>/<c>false</c>（小写）才当布尔；
    /// 数字还必须是<b>自己的规范形式</b>（往返格式化后与原文本完全相同）。其余一律是字符串。
    /// </para>
    /// <para>
    /// <b>为什么要做规范形式检查</b>：<c>0012</c> 这样的值可以被解析成数字 12，
    /// 但那样会丢掉前导零。版本号、区号、构建号、零填充编号都是真实存在的场景，
    /// 而"猜类型"正是配置语言最容易产生"看起来对但行为诡异"的地方。
    /// 只有 <c>12</c> 与 <c>3.12</c> 这类无歧义写法才被当作数字。
    /// </para>
    /// </remarks>
    private static ExprValue InferredExprValue(string raw)
    {
        // 布尔判定刻意大小写不敏感：动作输出的规范写法是小写 true/false，
        // 但第三方实现或历史数据可能给出 True/False。这两种写法都没有歧义、也没有信息损失，
        // 因此一并接受——这与"数字必须自身规范形式"的严格性并不矛盾：
        // 那里拒绝的是 0012 这类**会丢信息**的推断，这里不存在这个问题。
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return ExprValue.FromBoolean(true);
        }

        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return ExprValue.FromBoolean(false);
        }

        if (IsCanonicalNumber(raw))
        {
            return ExprValue.FromNumber(double.Parse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture));
        }

        return ExprValue.FromString(raw);
    }

    private static bool IsCanonicalNumber(string raw)
    {
        if (raw.Length is 0 or > 18 || raw.Trim() != raw)
        {
            return false;
        }

        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var canonical = number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? ((long)number).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        return string.Equals(canonical, raw, StringComparison.Ordinal);
    }
}

/// <summary>审计事件。逐条写入 JSONL（需求 8.2），凭据脱敏，参数摘要化。</summary>
/// <param name="Timestamp">发生时间。</param>
/// <param name="RunId">运行 ID。</param>
/// <param name="PackageId">包 ID。</param>
/// <param name="EventName">事件名，如 <c>action.begin</c>。</param>
/// <param name="Fields">字段集合（值必须已脱敏）。</param>
public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string RunId,
    string PackageId,
    string EventName,
    ImmutableDictionary<string, string> Fields);

/// <summary>审计落盘接口。测试与预演使用内存实现。</summary>
public interface IAuditSink
{
    /// <summary>记录一条事件。</summary>
    void Write(AuditEvent auditEvent);
}

/// <summary>内存审计实现（测试 / 预演 / 报告生成使用）。</summary>
public sealed class MemoryAuditSink : IAuditSink
{
    private readonly List<AuditEvent> _events = [];

    /// <summary>已记录事件。</summary>
    public IReadOnlyList<AuditEvent> Events => _events;

    /// <inheritdoc />
    public void Write(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        _events.Add(auditEvent);
    }

    /// <summary>按事件名筛选。</summary>
    public IEnumerable<AuditEvent> Where(string eventName) =>
        _events.Where(e => string.Equals(e.EventName, eventName, StringComparison.Ordinal));
}

/// <summary>进度上报接口。UI 与 CLI 各自实现。</summary>
public interface IProgressSink
{
    /// <summary>上报进度。</summary>
    void Report(int percent, string? message);
}

/// <summary>把进度丢弃的实现（无人值守）。</summary>
public sealed class NullProgressSink : IProgressSink
{
    /// <summary>单例。</summary>
    public static NullProgressSink Instance { get; } = new();

    /// <inheritdoc />
    public void Report(int percent, string? message)
    {
        // 刻意留空。
    }
}

/// <summary>
/// 资源配额（需求 22.3）。每次包运行独立计量，超限即拒绝。
/// </summary>
/// <param name="MaxDownloadBytes">累计下载字节上限。</param>
/// <param name="MaxExtractedBytes">累计解压产出字节上限。</param>
/// <param name="MaxFilesWritten">累计写入文件数上限。</param>
/// <param name="MaxWallClock">整包运行时长上限。</param>
public sealed record ResourceQuota(
    long MaxDownloadBytes,
    long MaxExtractedBytes,
    int MaxFilesWritten,
    TimeSpan MaxWallClock)
{
    /// <summary>默认配额（需求 22.3）。</summary>
    public static ResourceQuota Default { get; } = new(
        MaxDownloadBytes: 4L * 1024 * 1024 * 1024,
        MaxExtractedBytes: 12L * 1024 * 1024 * 1024,
        MaxFilesWritten: 200_000,
        MaxWallClock: TimeSpan.FromMinutes(30));
}

/// <summary>配额计量器。动作在消耗资源前必须 <see cref="TryConsume"/>。</summary>
public sealed class QuotaMeter(ResourceQuota quota)
{
    private readonly ResourceQuota _quota = quota ?? throw new ArgumentNullException(nameof(quota));
    private long _downloaded;
    private long _extracted;
    private int _filesWritten;

    /// <summary>本次运行的配额上限。</summary>
    public ResourceQuota Limits => _quota;

    /// <summary>已下载字节。</summary>
    public long DownloadedBytes => _downloaded;

    /// <summary>已解压字节。</summary>
    public long ExtractedBytes => _extracted;

    /// <summary>已写入文件数。</summary>
    public int FilesWritten => _filesWritten;

    /// <summary>尝试消耗配额。超限返回失败（不部分扣减）。</summary>
    public Result<Unit> TryConsume(QuotaKind kind, long amount)
    {
        if (amount < 0)
        {
            return Result<Unit>.Fail(EnvStationErrorCodes.ActionFailed, "配额消耗量不能为负。");
        }

        switch (kind)
        {
            case QuotaKind.Download:
                if (_downloaded + amount > _quota.MaxDownloadBytes)
                {
                    return Exceeded($"下载量将超过上限 {Format(_quota.MaxDownloadBytes)}（已用 {Format(_downloaded)}，本次再需 {Format(amount)}）");
                }

                _downloaded += amount;
                break;

            case QuotaKind.Extract:
                if (_extracted + amount > _quota.MaxExtractedBytes)
                {
                    return Exceeded($"解压产出将超过上限 {Format(_quota.MaxExtractedBytes)}（已用 {Format(_extracted)}，本次再需 {Format(amount)}）");
                }

                _extracted += amount;
                break;

            case QuotaKind.FileWrite:
                if ((long)_filesWritten + amount > _quota.MaxFilesWritten)
                {
                    return Exceeded($"写入文件数将超过上限 {_quota.MaxFilesWritten}（已用 {_filesWritten}）");
                }

                _filesWritten += (int)amount;
                break;

            default:
                return Result<Unit>.Fail(EnvStationErrorCodes.ActionFailed, $"未知的配额种类 {kind}。");
        }

        return Results.Ok();
    }

    private static Result<Unit> Exceeded(string detail) =>
        Result<Unit>.Fail(
            EnvStationErrorCodes.NetSizeLimit,
            "本次操作将超出资源配额：" + detail + "。",
            "在设置中提高配额后重试。");

    private static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1} MB"
        : $"{bytes / (double)(1L << 10):F1} KB";
}

/// <summary>配额种类。</summary>
public enum QuotaKind
{
    /// <summary>下载字节。</summary>
    Download = 0,

    /// <summary>解压产出字节。</summary>
    Extract = 1,

    /// <summary>写入文件数。</summary>
    FileWrite = 2,
}

/// <summary>
/// 动作执行上下文实现：动作与外部世界之间的唯一通道。
/// </summary>
/// <remarks>
/// 刻意让本类<b>只暴露受控能力</b>：动作拿不到注册表句柄、拿不到 <c>Process.Start</c>、
/// 拿不到任意文件路径——想写文件必须给出落在授权根内的路径并经过 <see cref="GuardPath"/>。
/// </remarks>
public sealed class ActionExecutionContext : IActionContext, IDisposable
{
    private readonly IProgressSink _progress;
    private readonly IAuditSink _audit;

    /// <summary>整包时长的单调计时器（见 <see cref="RemainingWallClock"/>）。</summary>
    private readonly System.Diagnostics.Stopwatch _runClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>构造执行上下文。</summary>
    public ActionExecutionContext(
        string packageId,
        string runId,
        CapabilitySet grantedCapabilities,
        IEnumerable<string> authorizedRoots,
        VariableTable variables,
        QuotaMeter quota,
        IAuditSink audit,
        IProgressSink? progress = null,
        bool unattended = false,
        IEnvironmentOperations? environment = null,
        IProcessRunner? processRunner = null,
        IInteractionSink? interaction = null,
        RunLedger? ledger = null,
        IEnumerable<string>? allowedHosts = null,
        INetworkTransport? network = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentNullException.ThrowIfNull(grantedCapabilities);
        ArgumentNullException.ThrowIfNull(authorizedRoots);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(audit);

        PackageId = packageId;
        RunId = runId;
        GrantedCapabilities = grantedCapabilities;
        Variables = variables;
        Quota = quota;
        Unattended = unattended;
        Environment = environment;
        ProcessRunner = processRunner ?? ControlledProcessRunner.Instance;
        Interaction = interaction ?? NullInteractionSink.Instance;
        Network = network;

        // 域名白名单：**只有包清单里声明过的域名才允许访问**（需求 AI-4）。
        // 大小写不敏感、去重、去空白；空白名单表示"任何域名都不允许"，
        // 这样"忘了声明"的后果是下载失败，而不是悄悄放行。
        AllowedHosts = [.. (allowedHosts ?? [])
            .Where(static h => !string.IsNullOrWhiteSpace(h))
            .Select(static h => h.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)];

        Ledger = ledger ?? new RunLedger
        {
            RunId = runId,
            PackageId = packageId,
            StartedAt = DateTimeOffset.Now,
        };
        _audit = audit;
        _progress = progress ?? NullProgressSink.Instance;

        // 授权根目录必须预先规范化，否则 "\foo\..\bar" 这类写法会绕过前缀比较。
        AuthorizedRoots =
        [
            .. authorizedRoots
                .Where(static r => !string.IsNullOrWhiteSpace(r))
                .Select(NormalizeRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <inheritdoc />
    public string PackageId { get; }

    /// <inheritdoc />
    public string RunId { get; }

    /// <inheritdoc />
    public CapabilitySet GrantedCapabilities { get; }

    /// <inheritdoc />
    public ImmutableArray<string> AuthorizedRoots { get; }

    /// <inheritdoc />
    public IVariableScope Variables { get; }

    /// <summary>变量表的具体类型（动作需要按作用域枚举时使用）。</summary>
    public VariableTable VariableTable => (VariableTable)Variables;

    /// <summary>配额计量器。</summary>
    public QuotaMeter Quota { get; }

    /// <summary>
    /// 本此运行还剩多少整包时长（需求 22.3 的 <c>MaxWallClock</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用<b>单调时钟</b>（<see cref="System.Diagnostics.Stopwatch"/>）而不是墙上时间：
    /// 用户改系统时间、夏令时切换、NTP 校时都会让墙上时间跳变，
    /// 而"这个包还能跑多久"必须是稳定可预期的——否则一次对时就能让配额凭空多出或少了半小时。
    /// </para>
    /// <para>
    /// 返回负值表示已超时；调用方据此拒绝继续执行，而不是把负的超时当成"立即超时"传下去
    /// （那样得到的错误信息会变成"动作超时 0 秒"，完全指不到真正的原因）。
    /// </para>
    /// </remarks>
    public TimeSpan RemainingWallClock => Quota.Limits.MaxWallClock - _runClock.Elapsed;

    /// <summary>整包运行已耗时（单调时钟）。</summary>
    public TimeSpan ElapsedWallClock => _runClock.Elapsed;

    /// <summary>是否处于无人值守模式（交互动作应被拒绝）。</summary>
    public bool Unattended { get; }

    /// <summary>
    /// 环境变量领域操作门面；为 <c>null</c> 时所有 <c>env.*</c> / <c>path.*</c> 动作都会明确失败。
    /// </summary>
    /// <remarks>
    /// <b>这是"真机安全"的总闸。</b>只有宿主（CLI / 图形界面）显式注入实现，包才可能改到真实环境变量；
    /// 预演、静态检查与单元测试都不注入，因此物理上不可能写坏用户环境。
    /// </remarks>
    public IEnvironmentOperations? Environment { get; }

    /// <summary>受控进程执行器。默认实现直接创建进程；测试可注入假实现而完全不启动真实进程。</summary>
    public IProcessRunner ProcessRunner { get; }

    /// <summary>交互通道（提示与提问）。无人值守时为不做任何事的空实现。</summary>
    public IInteractionSink Interaction { get; }

    /// <summary>本次运行的账本，用于生成报告。</summary>
    public RunLedger Ledger { get; }

    /// <summary>
    /// 允许访问的域名白名单（来自包清单的 <c>[network].allow</c>）。
    /// </summary>
    /// <remarks>
    /// <b>空白名单的含义是"一个域名都不允许"，而不是"不限制"。</b>
    /// 这个方向很关键：如果空白等于放行，那么"忘了声明域名"这个最常见的作者疏忽
    /// 就会变成一条无声的任意出网通道（需求 TH-4 供应链投毒）。
    /// </remarks>
    public ImmutableArray<string> AllowedHosts { get; }

    /// <summary>
    /// 网络传输通道。为 <c>null</c> 时所有下载类动作都会明确失败。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Environment"/> 同样的思路：这是"能不能出网"的唯一开关。
    /// 预演、静态检查与单元测试不注入实现，因此不会产生任何真实网络请求。
    /// </remarks>
    public INetworkTransport? Network { get; }

    /// <summary>
    /// 可执行文件的可信目录集合：授权根目录 + PATH 中的目录。
    /// </summary>
    /// <remarks>
    /// 这是 <see cref="ExecutableAllowList"/> 的第二个判据来源。
    /// 刻意<b>包含授权根目录</b>：包安装到自己的目录后，需要能运行其中的解释器来验证安装结果。
    /// 同时刻意<b>不包含</b>包的临时目录与缓存目录——那些地方不应出现可执行文件。
    /// </remarks>
    public ImmutableArray<string> TrustedDirectories
    {
        get
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            builder.AddRange(AuthorizedRoots);

            var path = Builtin.EnvironmentPathResolver.Read("merged");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var entry in PathParser.Parse(path, probeFileSystem: true))
                {
                    if (entry.Issues == PathEntryIssue.None)
                    {
                        builder.Add(entry.Normalized);
                    }
                }
            }

            return [.. builder.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>审计落盘接口。</summary>
    public IAuditSink AuditSink => _audit;

    /// <inheritdoc />
    public void ReportProgress(int percent, string? message) =>
        _progress.Report(Math.Clamp(percent, 0, 100), VariableTable.Redact(message));

    /// <inheritdoc />
    public void Audit(string eventName, IReadOnlyDictionary<string, string> fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(fields);

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            builder[key] = VariableTable.Redact(value);
        }

        _audit.Write(new AuditEvent(DateTimeOffset.Now, RunId, PackageId, eventName, builder.ToImmutable()));
    }

    /// <inheritdoc />
    public Result<string> GuardPath(string rawPath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"参数 {parameterName} 的路径为空。");
        }

        if (rawPath.Contains('\0', StringComparison.Ordinal))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"参数 {parameterName} 的路径包含空字符。",
                "这通常意味着包的路径拼接有误。");
        }

        if (rawPath.Contains("${", StringComparison.Ordinal))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"参数 {parameterName} 的路径中仍有未解析的变量：{rawPath}。",
                "检查工作流中该变量的赋值步骤是否已执行。");
        }

        string full;
        try
        {
            full = Path.GetFullPath(rawPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"参数 {parameterName} 的路径无法解析：{rawPath}。",
                "检查路径是否含非法字符或长度超限。");
        }

        if (AuthorizedRoots.Length == 0)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"本次运行没有授权任何目录，无法访问 {rawPath}。",
                "这是客户端配置问题，提交反馈。");
        }

        if (!IsUnderAuthorizedRoot(full))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"路径 {full} 不在本次运行的授权根目录内。",
                $"授权范围：{string.Join("、", AuthorizedRoots)}。包只能操作自己声明的安装目录与私有目录。");
        }

        return Result<string>.Ok(full);
    }

    /// <summary>判断一个已规范化的绝对路径是否落在授权根内。</summary>
    public bool IsUnderAuthorizedRoot(string fullPath)
    {
        foreach (var root in AuthorizedRoots)
        {
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fullPath.Length > root.Length
                && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (root.EndsWith(Path.DirectorySeparatorChar)
                    || fullPath[root.Length] == Path.DirectorySeparatorChar
                    || fullPath[root.Length] == Path.AltDirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 保留盘根写法（C:\），否则 C: 会被当成"驱动器相对路径"，前缀比较将失去意义。
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

    /// <summary>把参数渲染为可入审计日志的摘要文本。</summary>
    public string DescribeArguments(ActionArguments arguments, ImmutableHashSet<string> secretNames)    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(secretNames);

        var sb = new StringBuilder();
        var first = true;
        foreach (var (name, value) in arguments.Pairs.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            sb.Append(name).Append('=');

            if (secretNames.Contains(name))
            {
                sb.Append("***");
                continue;
            }

            var text = value switch
            {
                null => "null",
                ImmutableArray<string> array => "[" + string.Join(",", array) + "]",
                _ => value.ToString() ?? string.Empty,
            };

            sb.Append(VariableTable.Redact(text.Length <= 120 ? text : text[..120] + "…"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 释放上下文持有的资源（目前只有网络通道）。
    /// </summary>
    /// <remarks>
    /// 让上下文可释放是有实际意义的：网络通道持有 <c>HttpClient</c> 与底层连接池，
    /// 一个长时间运行的宿主（GUI 或 CLI 批处理）如果每个包运行都新建一个而不释放，
    /// 会持续累积连接与句柄。因此"用完就释放"应当是调用方的默认动作，而不是可选项。
    /// </remarks>
    public void Dispose()
    {
        if (Network is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
