using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnvStation.Abstractions.Actions;

namespace EnvStation.Core.Actions;

/// <summary>
/// 审计日志的 JSONL 落盘实现（需求 8.2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须是"每行一个 JSON 对象"而不是一个 JSON 数组</b>：审计是<b>追加写</b>的。
/// 数组要求结尾有 <c>]</c>，那么每次追加都要改动文件尾部，进程被强杀时文件必然残缺、
/// 整个文件再也解析不出来。JSONL 没有这个问题——崩溃时最多丢掉最后一行，
/// 前面的记录全部完好。审计日志的价值恰恰在"出事之后"，所以格式必须为这个场景选。
/// </para>
/// <para>
/// <b>为什么不保持文件句柄常开</b>：Windows 上句柄常开会阻止文件被改名/删除，
/// 于是轮转与清理都会失败。审计量级是"每个动作两条"，按次开关文件的代价可以忽略，
/// 换来的是轮转、清理、外部工具读取都不打架。
/// </para>
/// <para>
/// <b>轮转与保留</b>：单文件超过 <see cref="MaxFileBytes"/> 就换下一个序号文件；
/// 保留最近 <see cref="RetentionFiles"/> 个文件，更早的删除。
/// 保留策略只删自己命名的文件（<c>audit-*.jsonl*</c>），绝不触碰目录里的其他内容。
/// </para>
/// <para>
/// <b>线程安全</b>：动作可能在后台线程写审计，因此写盘路径整体加锁。
/// 审计宁可慢一点，也不能出现两行交错拼接——那种文件无法逐行解析。
/// </para>
/// </remarks>
public sealed class JsonlAuditSink : IAuditSink, IDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private bool _disposed;
    private string? _currentPath;

    /// <summary>构造一个 JSONL 审计汇。</summary>
    /// <param name="directory">审计目录（不存在会自动创建）。</param>
    /// <param name="maxFileBytes">单个文件的大小上限，超过即轮转。</param>
    /// <param name="retentionFiles">保留的文件个数（含当前文件）。</param>
    public JsonlAuditSink(string directory, long maxFileBytes = 8L * 1024 * 1024, int retentionFiles = 12)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (maxFileBytes < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "单文件上限不得小于 4096 字节。");
        }

        if (retentionFiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionFiles), "保留文件数不得小于 1。");
        }

        _directory = directory;
        MaxFileBytes = maxFileBytes;
        RetentionFiles = retentionFiles;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>单文件大小上限。</summary>
    public long MaxFileBytes { get; }

    /// <summary>保留的文件个数。</summary>
    public int RetentionFiles { get; }

    /// <summary>当前写入的文件路径（还没写过任何事件时是"即将写入"的那个）。</summary>
    public string CurrentPath
    {
        get
        {
            lock (_gate)
            {
                return _currentPath ?? PathFor(Today(), 0);
            }
        }
    }

    /// <inheritdoc />
    public void Write(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var line = JsonSerializer.Serialize(auditEvent, AuditJsonContext.Default.AuditEvent);

        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = NextFilePath();
            Append(path, line);
            Prune();
        }
    }

    /// <summary>列出当前保留的审计文件（新的在前），供界面与 CLI 展示。</summary>
    public IReadOnlyList<string> ListFiles()
    {
        lock (_gate)
        {
            return [.. EnumerateEntries().OrderByDescending(static e => e.Date, StringComparer.Ordinal)
                                         .ThenByDescending(static e => e.Index)
                                         .Select(static e => e.Path)];
        }
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;

    /// <summary>
    /// 挑一个还没写满的文件；写满了就换下一个序号。
    /// </summary>
    /// <remarks>
    /// <b>只能往后开新文件，绝不能回到小序号</b>：清理会删掉旧文件，于是"从 0 开始找第一个
    /// 没满的文件"会重新命中刚刚被删掉的 0 号——结果是每次写都新建 0 号、紧接着又被清理删掉，
    /// 事件静默消失（AC-49 抓到的就是这个）。所以序号取"当天最大序号"，满了才 +1。
    /// </remarks>
    private string NextFilePath()
    {
        var date = Today();
        var indices = EnumerateEntries()
            .Where(e => string.Equals(e.Date, date, StringComparison.Ordinal))
            .Select(static e => e.Index)
            .ToList();

        if (indices.Count == 0)
        {
            _currentPath = PathFor(date, 0);
            return _currentPath;
        }

        var highest = indices.Max();
        var candidate = PathFor(date, highest);

        // 只有"当前最高序号的那个文件还没写满"时才继续写它；否则一律开新序号。
        _currentPath = new FileInfo(candidate).Length < MaxFileBytes ? candidate : PathFor(date, highest + 1);
        return _currentPath;
    }

    private static string Today() => DateTimeOffset.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private string PathFor(string date, int index) =>
        index == 0
            ? Path.Combine(_directory, $"audit-{date}.jsonl")
            : Path.Combine(_directory, $"audit-{date}.{index}.jsonl");

    private void Append(string path, string line)
    {
        try
        {
            // 用 FileStream 而不是 AppendAllText：需要精确控制"一行一次写、写完即落盘"，
            // 并显式指定 UTF-8 无 BOM（带 BOM 会让第一个 JSON 对象前面多出三个字节，
            // 逐行解析的工具会在第一行就失败）。
            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 审计写不进去不能把动作搞崩：写不进去是运维问题，动作崩掉是产品问题。
            // 但也不能静默——调用方可以通过 ReadingFailed 事件上报（见 AuditWriteFailed）。
            AuditWriteFailed?.Invoke(this, ex.Message);
        }
    }

    /// <summary>写盘失败时触发（磁盘满、权限不足等）。宿主应把它显示给用户，而不是当作没发生。</summary>
    public event EventHandler<string>? AuditWriteFailed;

    /// <summary>
    /// 删除超出保留数量的旧文件。
    /// </summary>
    /// <remarks>
    /// <b>必须按 (日期, 序号) 排序，不能按文件名字符串排序</b>：文件名形如
    /// <c>audit-20260916.jsonl</c>（序号 0）与 <c>audit-20260916.1.jsonl</c>（序号 1），
    /// 而字符串比较里 <c>"audit-20260916.jsonl" &gt; "audit-20260916.1.jsonl"</c>（'j' &gt; '1'），
    /// 于是"按名字降序"会把<b>最旧的</b>那个当成最新的——清理时删掉的恰恰是最近的记录。
    /// 这个错误由 AC-49 用例抓到。
    /// </remarks>
    private void Prune()
    {
        var files = EnumerateEntries()
            .OrderByDescending(static e => e.Date, StringComparer.Ordinal)
            .ThenByDescending(static e => e.Index)
            .Select(static e => e.Path)
            .ToList();

        foreach (var stale in files.Skip(RetentionFiles))
        {
            // 正在写的那个文件永远不删——即使排序因某种意外排错了，也不能把当前文件清掉。
            if (_currentPath is { } active && string.Equals(stale, active, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(stale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉就留着，下次再试——绝不为了清理而抛异常。
            }
        }
    }

    /// <summary>枚举本汇自己产生的文件，并解析出排序键（绝不匹配目录里的其他内容）。</summary>
    private IEnumerable<(string Path, string Date, int Index)> EnumerateEntries()
    {
        if (!Directory.Exists(_directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "audit-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("audit-", StringComparison.Ordinal)
                || !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // audit-<8 位日期>[.<序号>].jsonl
            var core = name["audit-".Length..^".jsonl".Length];
            var parts = core.Split('.');

            if (parts.Length == 1 && parts[0].Length == 8)
            {
                yield return (path, parts[0], 0);
            }
            else if (parts.Length == 2 && parts[0].Length == 8
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                yield return (path, parts[0], index);
            }
        }
    }
}

/// <summary>
/// 审计日志的源生成 JSON 上下文。
/// </summary>
/// <remarks>
/// <b>为什么单独一个上下文</b>：<see cref="AuditEvent"/> 定义在 Core 里，
/// 而 Abstractions 的上下文不能反向引用 Core。同时这也让审计的序列化契约独立出来——
/// 审计格式一旦发布就不能随意改（用户的审计文件会被外部工具消费）。
/// <c>WriteIndented = false</c> 是硬要求：审计必须是每行一个对象的 JSONL。
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AuditEvent))]
public sealed partial class AuditJsonContext : JsonSerializerContext;
