using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Environment;

namespace EnvStation.Core.Actions.Builtin;

// ══════════════════════════════════════════════════════════════════════════════
//  A01 探测与系统信息（9 个动作 · 只读 · CAP.INSPECT）
//
//  这一组的共同纪律：**绝不修改任何东西，也绝不运行任何程序**。
//  探测结论要么来自文件系统与注册表这类"静态证据"，要么来自操作系统 API。
//  需要运行程序才能得到的结论（如 python --version）属于 A09 验证组，需要更高的能力授权。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary><c>envstation.detect.runtime</c>：探测指定运行时是否安装、版本、路径、来源。</summary>
internal sealed class DetectRuntimeAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.detect.runtime",
        "1.0.0",
        CapabilityIds.Inspect,
        "探测运行时",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["filesystem.read", "registry.read"],
        Parameters:
        [
            new ParameterSpec("kind", ParameterType.Enum, true, "运行时种类",
                AllowedValues: RuntimeCatalog.Kinds),
            new ParameterSpec("requirement", ParameterType.VersionRange, false,
                "版本约束，如 >=3.10。留空表示任意版本均可。", MaxLength: 128),
            StrArray("search_paths", false, "额外的搜索目录（会在内置搜索位置之前检查）"),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken)
    {
        var kind = arguments.GetString("kind")!;
        if (!RuntimeCatalog.TryGet(kind, out var definition))
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"未知的运行时种类 {kind}。已知种类：{string.Join("、", RuntimeCatalog.Kinds)}。");
        }

        var requirement = arguments.GetString("requirement");
        var extraPaths = arguments.GetStringArray("search_paths");

        var candidates = RuntimeDetector.Enumerate(definition, extraPaths, cancellationToken);

        RuntimeCandidate? best = null;
        foreach (var candidate in candidates)
        {
            if (requirement is null
                || (candidate.Version is { } candidateVersion && candidateVersion.Satisfies(requirement))
                || candidate.Version is null)
            {
                best = candidate;
                break;
            }
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        builder["kind"] = kind;
        builder["found"] = best is null ? "false" : "true";
        builder["version"] = best?.Version?.ToString() ?? string.Empty;
        builder["path"] = best?.Path ?? string.Empty;
        builder["source"] = best?.Source ?? string.Empty;
        builder["candidate_count"] = candidates.Length.ToString(CultureInfo.InvariantCulture);
        builder["all_candidates"] = string.Join(" | ", candidates.Select(static c => c.Describe()));

        if (best is null)
        {
            var message = candidates.Length == 0
                ? $"未检测到 {definition.DisplayName}。"
                : $"检测到 {definition.DisplayName}，但没有满足版本要求 {requirement} 的版本。已找到：{string.Join("；", candidates.Select(static c => c.Describe()))}";

            context.ReportProgress(100, message);
            return Ok(message, builder.ToImmutable(), candidates.Select(static c => c.Path));
        }

        // 版本号可能探测不到（比如只有可执行文件、读不出版本）。直接插值会留下双空格，
        // 变成"检测到 Python 解释器 （PATH）"这种看着像坏了的样子——所以位置要按有无值来拼。
        var versionText = best.Version is null ? string.Empty : $" {best.Version}";
        var ok = $"检测到 {definition.DisplayName}{versionText}（{best.Source}）：{best.Path}";
        context.ReportProgress(100, ok);
        return Ok(ok, builder.ToImmutable(), [best.Path]);
    }
}

/// <summary><c>envstation.detect.arch</c>：系统架构、进程架构、CPU 指令集、模拟运行状态。</summary>
internal sealed class DetectArchAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.detect.arch",
        "1.0.0",
        CapabilityIds.Inspect,
        "探测 CPU 架构",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 10,
        TouchedResources: ImmutableArray<string>.Empty,
        Parameters: ImmutableArray<ParameterSpec>.Empty);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken)
    {
        var processArch = RuntimeInformation.ProcessArchitecture;
        var osArch = RuntimeInformation.OSArchitecture;

        // 进程架构与系统架构不一致 = 正在以模拟方式运行（ARM64 上跑 x64 进程）。
        // 这是本项目必须能识别的情况：需求明确要求"用户电脑指令集与预计不一致"时要及时制止。
        var emulated = processArch != osArch;

        var outputs = Outputs(
            ("process_arch", Map(processArch)),
            ("os_arch", Map(osArch)),
            ("emulated", emulated ? "true" : "false"),
            ("os_description", RuntimeInformation.OSDescription),
            ("framework", RuntimeInformation.FrameworkDescription),
            ("processor_count", System.Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)),
            ("is_64bit_process", System.Environment.Is64BitProcess ? "true" : "false"));

        var message = emulated
            ? $"当前进程以 {Map(processArch)} 运行，而系统架构是 {Map(osArch)}：正处于模拟运行状态。安装原生组件前请改用原生版本的环境站。"
            : $"CPU 架构：{Map(osArch)}（进程 {Map(processArch)}）。";

        context.ReportProgress(100, message);
        return Ok(message, outputs);
    }

    private static string Map(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => architecture.ToString().ToLowerInvariant(),
    };
}

/// <summary><c>envstation.detect.os</c>：系统版本、构建号、SKU、语言、区域。</summary>
internal sealed class DetectOsAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.detect.os",
        "1.0.0",
        CapabilityIds.Inspect,
        "探测操作系统信息",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 10,
        TouchedResources: ["registry.read"],
        Parameters: ImmutableArray<ParameterSpec>.Empty);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken)
    {
        var version = System.Environment.OSVersion.Version;
        var (productName, displayVersion, ubr) = OsInfo.ReadFromRegistry();
        var displayName = OsInfo.NormalizeProductName(productName, version.Build);

        var build = ubr is null ? version.Build : int.Parse(
            $"{version.Build}{ubr.Value.ToString("D4", CultureInfo.InvariantCulture)}",
            CultureInfo.InvariantCulture);

        // 环境站的最低要求是 Windows 10 1809（build 17763）。
        var supported = version.Major > 10 || (version.Major == 10 && version.Build >= 17763);

        var outputs = Outputs(
            ("os_version", version.ToString()),
            ("build", version.Build.ToString(CultureInfo.InvariantCulture)),
            ("revision", ubr?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            ("build_with_revision", build.ToString(CultureInfo.InvariantCulture)),
            ("product_name", displayName ?? "未知"),
            ("display_version", displayVersion ?? string.Empty),
            ("architecture", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()),
            ("culture", CultureInfo.CurrentUICulture.Name),
            ("system_culture", CultureInfo.CurrentCulture.Name),
            ("supported", supported ? "true" : "false"));

        var message = $"{displayName ?? "Windows"} · 构建 {version.Build}.{ubr?.ToString(CultureInfo.InvariantCulture) ?? "?"} · {CultureInfo.CurrentUICulture.DisplayName}";
        if (!supported)
        {
            message += " —— 系统版本低于环境站的最低要求（Windows 10 1809 / 构建 17763）。";
        }

        context.ReportProgress(100, message);
        return Ok(message, outputs);
    }
}

/// <summary>从注册表读取 Windows 的展示名称与显示版本（这些信息无法从 API 可靠获得）。</summary>
internal static class OsInfo
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>
    /// 修正 Windows 11 的 <c>ProductName</c> 问题。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows 11 的注册表里 <c>ProductName</c> <b>至今仍写着 Windows 10</b>——
    /// 微软为了让按字符串判断版本的旧程序不误判而刻意保留。直接展示它，
    /// 在一台 Windows 11 上会显示成 Windows 10（本机 GUI 首次运行时就是这样，
    /// 见 实施进展与M0结论.md 的产品缺陷一节）。
    /// </para>
    /// <para>
    /// 判定 Windows 11 的唯一可靠依据是<b>构建号</b>：22000 起为 Windows 11。
    /// 这里只在构建号确认的前提下替换名称，不做任何猜测。
    /// </para>
    /// </remarks>
    internal static string? NormalizeProductName(string? productName, int buildNumber) =>
        productName is not null && buildNumber >= 22000
            ? productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase)
            : productName;

    internal static (string? ProductName, string? DisplayVersion, int? Ubr) ReadFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            if (key is null)
            {
                return (null, null, null);
            }

            var product = key.GetValue("ProductName") as string;
            var display = (key.GetValue("DisplayVersion") as string) ?? (key.GetValue("ReleaseId") as string);
            int? ubr = key.GetValue("UBR") is int value ? value : null;
            return (product, display, ubr);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 读不到注册表不是致命问题：降级为 API 信息即可，不阻断探测。
            return (null, null, null);
        }
    }
}

/// <summary><c>envstation.detect.command</c>：命令解析追踪（增强版 where，含跳过原因）。</summary>
internal sealed class DetectCommandAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.detect.command",
        "1.0.0",
        CapabilityIds.Inspect,
        "追踪命令解析",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 20,
        TouchedResources: ["filesystem.read", "environment.read"],
        Parameters:
        [
            Str("command", true, "要追踪的命令名，如 python 或 python.exe", maxLength: 260),
            new ParameterSpec("scope", ParameterType.Enum, false, "在哪个作用域的 PATH 中查找",
                AllowedValues: EnvironmentPathResolver.Scopes),
            StrArray("extensions", false, "要尝试的扩展名，默认使用 PATHEXT"),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken)
    {
        var command = arguments.GetString("command")!;
        var scope = arguments.GetString("scope") ?? "merged";

        if (command.Contains('\\', StringComparison.Ordinal) || command.Contains('/', StringComparison.Ordinal))
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                "command 参数只接受命令名（如 python），不接受路径。若要检查某个具体文件，请使用 envstation.verify.file_exists。");
        }

        var pathValue = EnvironmentPathResolver.Read(scope);
        if (pathValue is null)
        {
            return Fail(EnvStationErrorCodes.NetUnreachable, $"无法读取{EnvironmentPathResolver.ToScopeLabel(scope)} PATH。");
        }

        var extensions = arguments.GetStringArray("extensions");
        if (extensions.Length == 0)
        {
            var pathExt = System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            extensions = [.. pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        var entries = PathParser.Parse(pathValue, probeFileSystem: true);
        var trace = ImmutableArray.CreateBuilder<string>();
        string? resolved = null;
        string? resolvedEntry = null;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Issues != PathEntryIssue.None)
            {
                trace.Add($"跳过 {entry.Raw} —— {DescribeIssue(entry.Issues)}");
                continue;
            }

            if (Directory.Exists(entry.Normalized) is false)
            {
                trace.Add($"跳过 {entry.Normalized} —— 目录不存在");
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(entry.Normalized, command + extension);
                if (File.Exists(candidate))
                {
                    trace.Add($"命中 {candidate}");
                    resolved ??= candidate;
                    resolvedEntry ??= entry.Normalized;
                    break;
                }
            }

            if (resolved is not null)
            {
                // 继续扫描以生成完整追踪（用户需要看到"后面还有几个同名命令"），但不改变命中结果。
                continue;
            }

            trace.Add($"未命中 {entry.Normalized} —— 目录中不存在 {command}");
        }

        var outputs = Outputs(
            ("command", command),
            ("scope", scope),
            ("found", resolved is null ? "false" : "true"),
            ("resolved_path", resolved ?? string.Empty),
            ("resolved_directory", resolvedEntry ?? string.Empty),
            ("path_entry_count", entries.Count.ToString(CultureInfo.InvariantCulture)),
            ("trace", string.Join(" | ", trace)));

        var message = resolved is null
            ? $"命令 {command} 在{EnvironmentPathResolver.ToScopeLabel(scope)} PATH 中未找到，共 {entries.Count} 项，详情见 trace。"
            : $"命令 {command} 解析到 {resolved}。";

        context.ReportProgress(100, message);
        return Ok(message, outputs, resolved is null ? null : [resolved]);
    }

    private static string DescribeIssue(PathEntryIssue issue) => issue switch
    {
        PathEntryIssue.Empty => "空项",
        PathEntryIssue.UnresolvedVariable => "包含未定义的环境变量",
        PathEntryIssue.Relative => "相对路径（不可靠）",
        PathEntryIssue.InvalidCharacters => "包含非法字符",
        _ => issue.ToString(),
    };
}

/// <summary><c>envstation.detect.conflict</c>：检测同名命令 / 变量冲突（需求 7.8）。</summary>
internal sealed class DetectConflictAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.detect.conflict",
        "1.0.0",
        CapabilityIds.Inspect,
        "检测命令冲突",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "environment.read"],
        Parameters:
        [
            StrArray("commands", false, "只检查这些命令；留空则按内置冲突知识库检查"),
            Bool("include_kb", "是否启用内置冲突知识库（如 hadoop 的 yarn 与 node 的 yarn）", true),
        ]);

    /// <summary>内置冲突知识库：这些同名命令是真实世界里最常见的"装完就坏"来源。</summary>
    private static readonly ImmutableArray<(string Command, string OwnerA, string OwnerB, string Note)> KnownConflicts =
    [
        ("yarn", "hadoop", "node", "Hadoop 的 yarn.cmd 与 Node 的 yarn 同名。PATH 中谁在前谁生效，装完 Hadoop 后 yarn 常常变成 Hadoop 的。"),
        ("npm", "node", "其他工具", "部分工具会自带 npm 包装脚本，覆盖 Node 官方 npm。"),
        ("python", "python", "Windows Store 别名", "Windows 自带的 python.exe 应用执行别名会在未安装 Python 时抢先命中，打开应用商店。"),
        ("pip", "python", "其他 Python 发行版", "多个 Python 并存时，pip 与 python 可能来自不同发行版。"),
        ("java", "jdk", "jre", "同时存在 JDK 与独立 JRE 时，java 可能解析到 JRE。"),
        ("mvn", "maven", "IDE 内置 Maven", "IDE 内置的 Maven 会覆盖 PATH 中的 mvn。"),
        ("dotnet", "dotnet sdk", "Visual Studio 私有副本", "VS 自带的 dotnet 可能与系统安装的版本不同。"),
        ("git", "git", "其他工具自带 git", "部分工具（如便携式 IDE）会自带 git。"),
        ("cmake", "cmake", "Visual Studio 内置 CMake", "VS 内置 CMake 可能抢先命中。"),
        ("mysql", "mysql", "其他数据库客户端", "MariaDB 等兼容客户端会提供同名命令。"),
    ];

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken)
    {
        var requested = arguments.GetStringArray("commands");
        var includeKb = arguments.GetBoolean("include_kb", true);

        var pathValue = EnvironmentPathResolver.Read("merged");
        if (pathValue is null)
        {
            return Fail(EnvStationErrorCodes.NetUnreachable, "无法读取 PATH。");
        }

        var entries = PathParser.Parse(pathValue, probeFileSystem: true)
            .Where(static e => e.Issues == PathEntryIssue.None)
            .ToImmutableArray();

        var findings = ImmutableArray.CreateBuilder<string>();
        var conflictCount = 0;

        // ① 知识库冲突：只在这些命令确实存在于 PATH 中多个不同目录时才报告，
        //    避免"什么都没装也报一堆冲突"这种噪音。
        if (includeKb)
        {
            foreach (var (command, ownerA, ownerB, note) in KnownConflicts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (requested.Length > 0 && !requested.Contains(command, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hits = FindCommand(entries, command);
                if (hits.Count > 1)
                {
                    conflictCount++;
                    findings.Add($"[知识库] {command} 有 {hits.Count} 个来源（{ownerA} / {ownerB}）：{string.Join(" → ", hits)}。{note}");
                }
            }
        }

        // ② 显式请求的命令：逐个列出全部命中位置（不论是否在知识库中）。
        foreach (var command in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hits = FindCommand(entries, command);
            if (hits.Count > 1)
            {
                conflictCount++;
                findings.Add($"{command} 有 {hits.Count} 个来源，按 PATH 顺序：{string.Join(" → ", hits)}。生效的是第一个。");
            }
            else if (hits.Count == 0)
            {
                findings.Add($"{command} 在 PATH 中未找到。");
            }
        }

        var outputs = Outputs(
            ("conflict_count", conflictCount.ToString(CultureInfo.InvariantCulture)),
            ("conflicts", string.Join(" || ", findings)),
            ("path_entry_count", entries.Length.ToString(CultureInfo.InvariantCulture)));

        var message = conflictCount == 0
            ? "未发现同名命令冲突。"
            : $"发现 {conflictCount} 处同名命令冲突：{string.Join("；", findings)}";

        context.ReportProgress(100, message);
        return Ok(message, outputs);
    }

    private static List<string> FindCommand(ImmutableArray<PathEntry> entries, string command)
    {
        var extensions = new[] { ".exe", ".cmd", ".bat", ".com", ".ps1" };
        var hits = new List<string>();
        foreach (var entry in entries)
        {
            if (!Directory.Exists(entry.Normalized))
            {
                continue;
            }

            if (File.Exists(Path.Combine(entry.Normalized, command)))
            {
                hits.Add(Path.Combine(entry.Normalized, command));
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(entry.Normalized, command + extension);
                if (File.Exists(candidate))
                {
                    hits.Add(candidate);
                    break;
                }
            }
        }

        return hits;
    }
}

/// <summary><c>envstation.detect.disk</c>：磁盘可用空间、文件系统类型、可写性。</summary>
internal sealed class DetectDiskAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.detect.disk",
        "1.0.0",
        CapabilityIds.Inspect,
        "探测磁盘空间",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 20,
        TouchedResources: ["filesystem.read"],
        Parameters:
        [
            Path_("path", true, "要检查的目录"),
            new ParameterSpec("required_bytes", ParameterType.Integer, false,
                "所需的可用字节数；给出后会直接判断是否足够", Minimum: 0),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken)
    {
        var raw = arguments.GetString("path")!;

        // 探测磁盘时目录可能尚不存在（安装目标），因此先向上找到存在的祖先目录再探测。
        var probePath = raw;
        while (!string.IsNullOrEmpty(probePath) && !Directory.Exists(probePath) && !File.Exists(probePath))
        {
            var parent = Path.GetDirectoryName(probePath);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, probePath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            probePath = parent;
        }

        if (string.IsNullOrEmpty(probePath))
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"无法为 {raw} 找到已存在的祖先目录以探测磁盘。");
        }

        var full = Path.GetFullPath(probePath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"无法确定 {full} 所在的驱动器。");
        }

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"无法读取驱动器 {root} 的信息：{ex.Message}");
        }

        if (!drive.IsReady)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"驱动器 {root} 未就绪（可能是未插入的可移动介质或未挂载的网络盘）。");
        }

        var available = drive.AvailableFreeSpace;
        var required = arguments.GetInt64("required_bytes", 0);
        var enough = required <= 0 || available >= required;

        // 可写性探测：在目标目录（存在时）创建一个极小的临时文件后立即删除。
        // 刻意不做"创建目录再删除"——那会留下空目录，属于本工具的副作用，而探测动作不应有副作用。
        var writable = ProbeWritable(Directory.Exists(full) ? full : probePath);

        var outputs = Outputs(
            ("drive", root),
            ("drive_format", drive.DriveFormat),
            ("drive_type", drive.DriveType.ToString()),
            ("total_bytes", drive.TotalSize.ToString(CultureInfo.InvariantCulture)),
            ("available_bytes", available.ToString(CultureInfo.InvariantCulture)),
            ("required_bytes", required.ToString(CultureInfo.InvariantCulture)),
            ("enough", enough ? "true" : "false"),
            ("writable", writable ? "true" : "false"),
            ("probed_path", probePath));

        if (!enough)
        {
            var message = $"磁盘空间不足：{root} 可用 {Mb(available)}，需要 {Mb(required)}。";
            context.ReportProgress(100, message);
            return Fail(EnvStationErrorCodes.PreflightDiskShort, message);
        }

        var ok = $"磁盘 {root}（{drive.DriveFormat}）可用 {Mb(available)}，可写：{(writable ? "是" : "否")}。";
        context.ReportProgress(100, ok);
        return Ok(ok, outputs);
    }

    private static bool ProbeWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".envstation-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";
}

/// <summary><c>envstation.detect.deps</c>：前置依赖检测（VC++ 运行库 / .NET / WebView2 等）。</summary>
internal sealed class DetectDepsAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.detect.deps",
        "1.0.0",
        CapabilityIds.Inspect,
        "检测前置依赖",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["filesystem.read", "registry.read"],
        Parameters:
        [
            StrArray("items", false, "要检测的依赖项；留空则检测全部内置项"),
        ]);

    /// <summary>内置前置依赖清单。</summary>
    private static readonly ImmutableArray<(string Id, string Display, string Note)> KnownDependencies =
    [
        ("vcredist-2015-2022-x64", "Microsoft Visual C++ 2015-2022 可再发行组件（x64）",
            "绝大多数用 MSVC 编译的程序（含 Python 官方安装包、MySQL、Node 原生模块）都依赖它。"),
        ("vcredist-2013-x64", "Microsoft Visual C++ 2013 可再发行组件（x64）",
            "较老的 Python 扩展与部分国产软件依赖该版本。"),
        ("vcredist-2010-x64", "Microsoft Visual C++ 2010 可再发行组件（x64）",
            "少量老工具依赖。"),
        ("dotnet-desktop-8", ".NET 8 桌面运行时",
            "部分 .NET 工具的运行前提。"),
        ("webview2", "Microsoft Edge WebView2 运行时",
            "Windows 11 与较新的 Windows 10 已内置。"),
        ("powershell-7", "PowerShell 7",
            "可选；部分自动化脚本使用 pwsh 而非 Windows PowerShell。"),
    ];

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken)
    {
        var requested = arguments.GetStringArray("items");
        var items = requested.Length == 0
            ? KnownDependencies
            : KnownDependencies.Where(d => requested.Contains(d.Id, StringComparer.OrdinalIgnoreCase)).ToImmutableArray();

        var missing = ImmutableArray.CreateBuilder<string>();
        var present = ImmutableArray.CreateBuilder<string>();
        var lines = ImmutableArray.CreateBuilder<string>();

        foreach (var (id, display, note) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installed = DependencyProbe.IsInstalled(id);
            if (installed)
            {
                present.Add(id);
                lines.Add($"✓ {display}");
            }
            else
            {
                missing.Add(id);
                lines.Add($"✗ {display} —— {note}");
            }
        }

        var outputs = Outputs(
            ("missing_count", missing.Count.ToString(CultureInfo.InvariantCulture)),
            ("missing", string.Join(",", missing)),
            ("present", string.Join(",", present)),
            ("details", string.Join(" || ", lines)));

        var message = missing.Count == 0
            ? $"全部 {items.Length} 项前置依赖均已就绪。"
            : $"缺少 {missing.Count} 项前置依赖：{string.Join("、", missing)}";

        context.ReportProgress(100, message);
        return Ok(message, outputs);
    }
}

/// <summary>依赖项探测实现（注册表卸载项 + 文件存在性）。</summary>
internal static class DependencyProbe
{
    private const string UninstallKeys =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    internal static bool IsInstalled(string dependencyId) => dependencyId switch
    {
        "vcredist-2015-2022-x64" => HasUninstallEntry("Visual C++ 2015-2022") || HasUninstallEntry("Visual C++ 2015-2019"),
        "vcredist-2013-x64" => HasUninstallEntry("Visual C++ 2013"),
        "vcredist-2010-x64" => HasUninstallEntry("Visual C++ 2010"),
        "dotnet-desktop-8" => Directory.Exists(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"),
        "webview2" => HasWebView2(),
        "powershell-7" => File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe"),
        _ => false,
    };

    /// <summary>在卸载注册表中按显示名子串匹配（同时检查 64 位与 32 位视图）。</summary>
    private static bool HasUninstallEntry(string displayNameFragment)
    {
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                using var uninstall = baseKey.OpenSubKey(UninstallKeys);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(subKeyName);
                    if (sub?.GetValue("DisplayName") is string name
                        && name.Contains(displayNameFragment, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }
        }

        return false;
    }

    private static bool HasWebView2()
    {
        string[] candidates =
        [
            @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application",
            @"C:\Program Files\Microsoft\EdgeWebView\Application",
        ];

        return candidates.Any(Directory.Exists);
    }
}

/// <summary><c>envstation.detect.network</c>：域名连通性、延迟、代理。</summary>
internal sealed class DetectNetworkAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.detect.network",
        "1.0.0",
        CapabilityIds.Inspect,
        "检测网络连通性",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["network.outbound"],
        Parameters:
        [
            StrArray("hosts", true, "要检测的主机名列表"),
            Int("timeout_ms", "单个主机的连接超时（毫秒）", 500, 30000, 3000),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken)
    {
        var hosts = arguments.GetStringArray("hosts");
        var timeoutMs = (int)arguments.GetInt64("timeout_ms", 3000);

        if (hosts.Length == 0)
        {
            return FailResult(
                EnvStationErrorCodes.ActionArgumentInvalid,
                "hosts 不能为空。");
        }

        var results = ImmutableArray.CreateBuilder<string>();
        var reachable = 0;

        for (var i = 0; i < hosts.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var host = hosts[i];

            context.ReportProgress((int)(i * 100.0 / hosts.Length), $"正在检测 {host}…");

            var (ok, detail, elapsed) = await NetworkProbe.ProbeAsync(host, timeoutMs, cancellationToken)
                .ConfigureAwait(false);

            if (ok)
            {
                reachable++;
            }

            results.Add($"{host}={(ok ? "ok" : "fail")}({elapsed}ms, {detail})");
        }

        var proxy = System.Net.WebRequest.DefaultWebProxy?.GetProxy(new Uri("https://example.com"))?.ToString();

        var outputs = Outputs(
            ("reachable_count", reachable.ToString(CultureInfo.InvariantCulture)),
            ("total", hosts.Length.ToString(CultureInfo.InvariantCulture)),
            ("results", string.Join(" | ", results)),
            ("system_proxy", string.IsNullOrEmpty(proxy) ? "未配置" : proxy));

        var message = reachable == hosts.Length
            ? $"全部 {hosts.Length} 个主机可达。"
            : $"{reachable}/{hosts.Length} 个主机可达：{string.Join("；", results)}";

        context.ReportProgress(100, message);
        return OkResult(message, outputs);
    }
}

/// <summary>
/// 网络探测：TCP 连接 + <b>TLS 证书校验</b> + 延迟测量。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须做 TLS 校验，而不是「TCP 连上就算可达」</b>：
/// 这是一个被真实环境暴露出来的问题——某些网络（企业透明代理、运营商劫持、DNS 泛解析）
/// 会对<b>任意</b>主机名的 443 端口都成功建立 TCP 连接。此时「TCP 探测成功」是彻底的假阳性：
/// 用户看到「镜像可达」，然后下载到一份代理返回的错误页。
/// </para>
/// <para>
/// 因此判定「可达」的条件是：TCP 连通 <b>且</b> TLS 握手成功 <b>且</b> 服务器证书的主题/SAN
/// 与目标主机名匹配。三者缺一就说明「从这个域名拿不到你想要的东西」，并把具体原因告诉用户。
/// </para>
/// </remarks>
internal static class NetworkProbe
{
    internal static async Task<(bool Ok, string Detail, long ElapsedMs)> ProbeAsync(
        string host,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        System.Net.Sockets.TcpClient? client = null;

        try
        {
            client = new System.Net.Sockets.TcpClient();
            using var timeoutSource = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            await client.ConnectAsync(host, 443, linked.Token).ConfigureAwait(false);

            // TCP 通了 —— 但这可能是透明代理或 DNS 泛解析造成的假阳性，必须继续验 TLS。
            var tls = await VerifyTlsAsync(client, host, linked.Token).ConfigureAwait(false);
            clock.Stop();

            return tls.Ok
                ? (true, $"TCP 与 TLS 均正常（证书 {tls.Subject}）", (long)clock.Elapsed.TotalMilliseconds)
                : (false, tls.Reason, (long)clock.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            return (false, $"连接超时（{timeoutMs} 毫秒）", (long)clock.Elapsed.TotalMilliseconds);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            clock.Stop();
            return (false, $"网络错误：{ex.SocketErrorCode}", (long)clock.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            clock.Stop();
            return (false, $"主机名非法：{ex.Message}", (long)clock.Elapsed.TotalMilliseconds);
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>做一次 TLS 握手并校验证书是否覆盖目标主机。</summary>
    private static async Task<(bool Ok, string Subject, string Reason)> VerifyTlsAsync(
        System.Net.Sockets.TcpClient client, string host, CancellationToken cancellationToken)
    {
        try
        {
            // CA5359 是有意抑制的：回调返回 true 并不代表我们放弃了校验——
            // 紧接着的 CertificateMatchesHost 会自己核对 SAN/CN 与主机名，
            // 而且这样做能拿到证书主题，从而给出「证书是谁签的」这类可行动的提示。
            // 若直接用 SslStream 的默认校验，失败时只能得到一句 RemoteCertificateNameMismatch。
#pragma warning disable CA5359
            await using var ssl = new System.Net.Security.SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: true,
                // 回调里放行一切，改为在下面自己做主机名校验：
                // 这样我们可以拿到证书、给出「证书是谁签的」这类可行动信息，
                // 而不是只抛一句 "RemoteCertificateNameMismatch"。
                userCertificateValidationCallback: static (_, _, _, _) => true);
#pragma warning restore CA5359

            await ssl.AuthenticateAsClientAsync(
                new System.Net.Security.SslClientAuthenticationOptions { TargetHost = host },
                cancellationToken).ConfigureAwait(false);

            var certificate = ssl.RemoteCertificate;
            if (certificate is null)
            {
                return (false, string.Empty, "TLS 握手成功但没有拿到服务器证书。");
            }

            using var parsed = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                certificate.GetRawCertData());
            var subject = parsed.Subject;

            if (!CertificateMatchesHost(parsed, host))
            {
                return (false, subject,
                    $"TCP 可连接，但证书与主机名 {host} 不匹配（证书主题：{subject}）。" +
                    "这通常意味着网络里有透明代理、DNS 泛解析或劫持——" +
                    "此时「连接成功」是假象，从该域名取到的内容并不可信。");
            }

            return (true, subject, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty, "TLS 握手超时。");
        }
        catch (System.Security.Authentication.AuthenticationException ex)
        {
            return (false, string.Empty,
                $"TCP 可连接，但 TLS 握手失败（{ex.Message}）。" +
                "这通常意味着对端不是真正的目标服务，而是网络中的代理或拦截设备。");
        }
        catch (IOException ex)
        {
            return (false, string.Empty, $"TLS 握手期间连接中断：{ex.Message}");
        }
    }

    /// <summary>判断证书是否覆盖指定主机名（以 SAN 为准，回退到 CN）。</summary>
    private static bool CertificateMatchesHost(
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, string host)
    {
        var san = certificate.Extensions
            .OfType<System.Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension>()
            .FirstOrDefault();

        if (san is not null)
        {
            foreach (var name in san.EnumerateDnsNames())
            {
                if (MatchesPattern(name, host))
                {
                    return true;
                }
            }

            return false;
        }

        var cn = certificate.GetNameInfo(
            System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false);

        return MatchesPattern(cn, host);
    }

    /// <summary>支持通配符证书（<c>*.example.com</c>），但只允许匹配一层子域。</summary>
    private static bool MatchesPattern(string pattern, string host)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 只允许一层子域：*.example.com 不应匹配 a.b.example.com（RFC 6125）。
            var prefix = host[..^suffix.Length];
            return prefix.Length > 0 && !prefix.Contains('.', StringComparison.Ordinal);
        }

        return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary><c>envstation.detect.env</c>：读取环境变量（含来源层级、值类型）。</summary>
internal sealed class DetectEnvAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.detect.env",
        "1.0.0",
        CapabilityIds.Inspect,
        "读取环境变量",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 20,
        TouchedResources: ["registry.read"],
        Parameters:
        [
            StrArray("names", false, "要读取的变量名；留空则返回全部"),
            new ParameterSpec("scope", ParameterType.Enum, false, "作用域",
                AllowedValues: ["user", "machine", "both"]),
            Bool("include_values",
                "是否返回值内容。设为 false 时只返回名称与类型（用于导出报告时避免泄露）", true),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var names = arguments.GetStringArray("names");
        var scope = arguments.GetString("scope") ?? "both";
        var includeValues = arguments.GetBoolean("include_values", true);

        var wanted = names.Length == 0
            ? null
            : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var lines = ImmutableArray.CreateBuilder<string>();
        var found = 0;

        foreach (var envScope in EnumerateScopes(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var store = new RegistryEnvStore(envScope);

            // 输出键使用与参数一致的小写作用域名（user / machine），
            // 而不是直接 ToString() —— 否则参数写 "machine"、输出键却是 "Machine"，
            // 包作者引用 ${x.machine:PATH} 时会莫名其妙取不到值。
            var scopeName = EnvironmentPathResolver.ToScopeName(envScope);

            if (wanted is null)
            {
                var all = store.ReadAll();
                if (all.IsFailure)
                {
                    lines.Add($"[{scopeName}] 读取失败：{all.Error.Message}");
                    continue;
                }

                foreach (var variable in all.Value)
                {
                    found++;
                    builder[$"{scopeName}:{variable.Name}"] = includeValues
                        ? $"{variable.Kind}|{Truncate(variable.RawValue, 200)}"
                        : variable.Kind.ToString();
                }

                lines.Add($"[{scopeName}] 共 {all.Value.Count} 个变量");
                continue;
            }

            foreach (var name in names)
            {
                var read = store.Read(name);
                if (read.IsFailure)
                {
                    lines.Add($"[{scopeName}] {name} 读取失败：{read.Error.Message}");
                    continue;
                }

                if (read.Value is null)
                {
                    builder[$"{scopeName}:{name}"] = string.Empty;
                    lines.Add($"[{scopeName}] {name} 未定义");
                    continue;
                }

                found++;
                builder[$"{scopeName}:{name}"] = includeValues
                    ? $"{read.Value.Kind}|{Truncate(read.Value.RawValue, 200)}"
                    : read.Value.Kind.ToString();
                lines.Add($"[{scopeName}] {name} = {(includeValues ? Truncate(read.Value.RawValue, 200) : "（已隐藏）")}");
            }
        }

        var outputs = Outputs(
            ("found_count", found.ToString(CultureInfo.InvariantCulture)),
            ("scope", scope),
            ("details", string.Join(" || ", lines)));
        foreach (var (key, value) in builder)
        {
            outputs = outputs.SetItem(key, value);
        }

        var message = $"读取到 {found} 个环境变量（{EnvironmentPathResolver.ToScopeLabel(scope)}）。";
        context.ReportProgress(100, message);
        return Ok(message, outputs);
    }

    private static IEnumerable<EnvScope> EnumerateScopes(string scope) => scope switch
    {
        "user" => [EnvScope.User],
        "machine" => [EnvScope.Machine],
        _ => [EnvScope.User, EnvScope.Machine],
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}