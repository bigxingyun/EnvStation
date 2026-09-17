using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Packages;

namespace EnvStation.Core.Installation;

/// <summary>一个已登记的运行时安装。</summary>
/// <param name="Kind">运行时种类（java / python / node …）。</param>
/// <param name="Version">版本。</param>
/// <param name="HomePath">主目录（JAVA_HOME 语义）。</param>
/// <param name="Source">来源：managed（环境站安装）/ registered（登记已有）。</param>
/// <param name="RegisteredAt">登记时间。</param>
/// <param name="Components">已安装的组件（文档、调试符号等）。</param>
public sealed record RuntimeRegistration(
    string Kind,
    string Version,
    string HomePath,
    string Source,
    DateTimeOffset RegisteredAt,
    ImmutableArray<string> Components)
{
    /// <summary>该安装是否由环境站托管（托管才允许环境站删除）。</summary>
    public bool IsManaged => string.Equals(Source, "managed", StringComparison.Ordinal);
}

/// <summary>
/// 运行时清单：记录本机装了哪些运行时、装在哪里。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要一份自己的清单</b>：系统里"装了什么"只能靠探测猜（PATH、环境变量、常见目录），
/// 而环境站需要知道更确定的事——<b>哪些是我们装的、装到了哪个路径、当前默认版本是哪个</b>。
/// 没有这份清单，"切换版本"就只能靠改 PATH 顺序去猜，而那是不可靠的。
/// </para>
/// <para>
/// <b>清单不是真相的唯一来源</b>：用户可能手工删掉目录。因此读取时一律校验路径是否仍存在，
/// 不存在的条目标记为失效而不是直接信任清单。
/// </para>
/// </remarks>
public sealed class RuntimeManifestStore
{
    private readonly string _manifestPath;

    /// <summary>用清单文件路径构造。</summary>
    public RuntimeManifestStore(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);
        _manifestPath = manifestPath;
    }

    /// <summary>用默认位置构造（<c>%LOCALAPPDATA%\EnvStation\runtimes.json</c>）。</summary>
    public static RuntimeManifestStore CreateDefault() => new(Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "EnvStation",
        "runtimes.json"));

    /// <summary>读取全部登记项。</summary>
    public Result<ImmutableArray<RuntimeRegistration>> Load()
    {
        if (!File.Exists(_manifestPath))
        {
            return Result<ImmutableArray<RuntimeRegistration>>.Ok([]);
        }

        try
        {
            var json = File.ReadAllText(_manifestPath);
            return Result<ImmutableArray<RuntimeRegistration>>.Ok(Parse(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<ImmutableArray<RuntimeRegistration>>.Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"无法读取运行时清单：{ex.Message}");
        }
    }

    /// <summary>新增或更新一条登记。</summary>
    public Result<Unit> Upsert(RuntimeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var loaded = Load();
        if (loaded.IsFailure)
        {
            return loaded.Propagate<Unit>();
        }

        var list = loaded.Value
            .Where(r => !(string.Equals(r.Kind, registration.Kind, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(r.Version, registration.Version, StringComparison.Ordinal)))
            .Append(registration)
            .OrderBy(static r => r.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static r => r.Version, StringComparer.Ordinal)
            .ToImmutableArray();

        return Save(list);
    }

    /// <summary>移除一条登记。</summary>
    public Result<Unit> Remove(string kind, string version)
    {
        var loaded = Load();
        if (loaded.IsFailure)
        {
            return loaded.Propagate<Unit>();
        }

        var list = loaded.Value
            .Where(r => !(string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(r.Version, version, StringComparison.Ordinal)))
            .ToImmutableArray();

        return Save(list);
    }

    /// <summary>查找某个运行时种类下的全部登记（含已失效项，由调用方判断）。</summary>
    public ImmutableArray<(RuntimeRegistration Registration, bool Exists)> Find(string kind)
    {
        var loaded = Load();
        if (loaded.IsFailure)
        {
            return [];
        }

        return
        [
            .. loaded.Value
                .Where(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .Select(static r => (r, Directory.Exists(r.HomePath))),
        ];
    }

    private Result<Unit> Save(ImmutableArray<RuntimeRegistration> items)
    {
        try
        {
            var directory = Path.GetDirectoryName(_manifestPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Configuration.ConfigFileStore
                .WriteAtomicAsync(_manifestPath, Serialize(items))
                .GetAwaiter().GetResult();

            return Results.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"无法保存运行时清单：{ex.Message}");
        }
    }

    private static string Serialize(ImmutableArray<RuntimeRegistration> items)
    {
        var sb = new StringBuilder();
        sb.Append("[\n");
        for (var i = 0; i < items.Length; i++)
        {
            var r = items[i];
            sb.Append("  {")
              .Append("\"kind\": ").Append(Json(r.Kind)).Append(", ")
              .Append("\"version\": ").Append(Json(r.Version)).Append(", ")
              .Append("\"homePath\": ").Append(Json(r.HomePath)).Append(", ")
              .Append("\"source\": ").Append(Json(r.Source)).Append(", ")
              .Append("\"registeredAt\": ").Append(Json(r.RegisteredAt.ToString("O", CultureInfo.InvariantCulture))).Append(", ")
              .Append("\"components\": [")
              .Append(string.Join(", ", r.Components.Select(Json)))
              .Append("]}")
              .Append(i == items.Length - 1 ? "\n" : ",\n");
        }

        sb.Append("]\n");
        return sb.ToString();
    }

    private static ImmutableArray<RuntimeRegistration> Parse(string json)
    {
        var builder = ImmutableArray.CreateBuilder<RuntimeRegistration>();

        using var document = System.Text.Json.JsonDocument.Parse(json);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var components = element.TryGetProperty("components", out var c)
                ? c.EnumerateArray().Select(static x => x.GetString() ?? string.Empty).ToImmutableArray()
                : ImmutableArray<string>.Empty;

            builder.Add(new RuntimeRegistration(
                element.GetProperty("kind").GetString() ?? string.Empty,
                element.GetProperty("version").GetString() ?? string.Empty,
                element.GetProperty("homePath").GetString() ?? string.Empty,
                element.GetProperty("source").GetString() ?? "managed",
                element.TryGetProperty("registeredAt", out var at) && at.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue,
                components));
        }

        return builder.ToImmutable();
    }

    private static string Json(string? text)
    {
        if (text is null)
        {
            return "null";
        }

        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ')
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>受支持的运行时组件。</summary>
/// <param name="Id">组件标识。</param>
/// <param name="DisplayName">中文名。</param>
/// <param name="ApproxBytes">大致体积（用于提前提示磁盘需求）。</param>
/// <param name="Note">说明。</param>
public sealed record RuntimeComponent(string Id, string DisplayName, long ApproxBytes, string Note);

/// <summary>运行时可补装组件的目录。</summary>
public static class RuntimeComponentCatalog
{
    /// <summary>内置组件清单。</summary>
    public static ImmutableArray<RuntimeComponent> All { get; } =
    [
        new("docs", "文档与手册", 40L * 1024 * 1024, "离线 API 文档，装与不装都不影响运行"),
        new("sources", "源码包", 120L * 1024 * 1024, "调试时能跳进标准库源码"),
        new("debug-symbols", "调试符号", 300L * 1024 * 1024, "分析崩溃转储时需要"),
        new("jre", "Java 运行时（仅 JDK 有意义）", 180L * 1024 * 1024, "单独部署一份运行时，便于对比"),
        new("test-suite", "语言自带测试套件", 90L * 1024 * 1024, "一般不需要"),
    ];

    /// <summary>按标识查找组件。</summary>
    public static RuntimeComponent? Find(string id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>全部组件标识。</summary>
    public static ImmutableArray<string> Ids { get; } = [.. All.Select(static c => c.Id)];
}
