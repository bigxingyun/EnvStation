using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Environment;

namespace EnvStation.Core.Actions.Builtin;

// ══════════════════════════════════════════════════════════════════════════════
//  A09 验证与断言（9 个动作）
//
//  这一组回答的问题是："装完了，怎么知道真的成了？"
//
//  它与 A01 探测组的根本区别：A01 只读静态证据（文件在不在、注册表怎么写），
//  A09 要**真的运行程序**或**真的发一次请求**，因此需要更高的能力授权。
//
//  安全上的三条硬约束：
//    1. 能运行的可执行文件必须落在白名单内且目录可信（AI-1）；
//    2. 参数以数组传递，永不拼接命令行（S6）；
//    3. 验证类动作**只读不写**——除了"启动子进程"这一件事，不产生任何副作用。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary><c>envstation.verify.version_output</c>：运行受控命令并解析版本号。</summary>
internal sealed class VerifyVersionOutputAction : ActionBase
{
    /// <summary>默认的版本号提取模式（覆盖绝大多数运行时的 <c>--version</c> 输出）。</summary>
    private const string DefaultVersionPattern = @"(?<v>\d+\.\d+(?:\.\d+)?(?:[-._][0-9A-Za-z]+)*)";

    /// <summary>单次验证最多尝试的候选参数个数（避免把"探测"变成"跑一堆命令"）。</summary>
    private const int MaxAttempts = 3;

    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.verify.version_output",
        "1.0.0",
        CapabilityIds.ProcessLaunch,
        "运行命令并解析版本号",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["process.launch"],
        Parameters:
        [
            Str("command", true,
                "要运行的命令：绝对路径，或已登记运行时的命令名（如 python）。不接受任意程序。",
                maxLength: 512),
            new ParameterSpec("pattern", ParameterType.String, false,
                "自定义版本号提取正则（必须含命名组 v）。省略则使用内置模式", MaxLength: 200),
            new ParameterSpec("expect", ParameterType.VersionRange, false,
                "期望的版本约束（如 >=3.10）。给出后会直接判定是否满足", MaxLength: 128),
            new ParameterSpec("timeout_ms", ParameterType.Integer, false,
                "单次运行的超时（毫秒）", Minimum: 1000, Maximum: 60000, DefaultValue: "15000"),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var command = arguments.GetString("command")!;
        var pattern = arguments.GetString("pattern");
        var expect = arguments.GetString("expect");
        var timeoutMs = arguments.GetInt64("timeout_ms", 15000);

        var verdict = ExecutableAllowList.Check(command, context.TrustedDirectories);
        if (!verdict.Allowed)
        {
            return FailResult(EnvStationErrorCodes.CapabilityDenied, verdict.Reason);
        }

        Regex versionRegex;
        try
        {
            versionRegex = new Regex(
                pattern ?? DefaultVersionPattern,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException ex)
        {
            return FailResult(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"pattern 不是合法的正则表达式：{ex.Message}");
        }

        if (!versionRegex.GetGroupNames().Contains("v", StringComparer.Ordinal))
        {
            return FailResult(
                EnvStationErrorCodes.ActionArgumentInvalid,
                "pattern 必须包含名为 v 的命名组，例如 (?<v>\\d+\\.\\d+)。");
        }

        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        var attempts = new List<string>(MaxAttempts);

        foreach (var probeArguments in ExecutableAllowList.VersionProbeArguments.Take(MaxAttempts))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var run = await context.ProcessRunner
                .RunAsync(verdict.ResolvedPath, probeArguments, timeout, cancellationToken)
                .ConfigureAwait(false);

            var invocation = ExecutableAllowList.DescribeInvocation(verdict.ResolvedPath, probeArguments);
            context.Audit("verify.version_output", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["invocation"] = invocation,
                ["started"] = invocation.Length > 0 && run.Started ? "true" : "false",
                ["exit_code"] = run.ExitCode.ToString(CultureInfo.InvariantCulture),
                ["elapsed_ms"] = ((long)run.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            });

            if (!run.Started)
            {
                attempts.Add($"{invocation} → 未能运行（{run.FailureReason}）");
                continue;
            }

            var combined = (run.StandardOutput + "\n" + run.StandardError).Trim();
            var match = SafeMatch(versionRegex, combined);
            if (match is null)
            {
                attempts.Add($"{invocation} → 输出中没有找到版本号（退出码 {run.ExitCode}）");
                continue;
            }

            var versionText = match;
            var version = SemanticVersion.ParseOrNull(versionText);
            var satisfied = expect is null || (version is { } parsed && parsed.Satisfies(expect));

            var outputs = Outputs(
                ("found", "true"),
                ("version", versionText),
                ("version_normalized", version?.ToString() ?? versionText),
                ("satisfied", Bool(satisfied)),
                ("expect", expect ?? string.Empty),
                ("invocation", invocation),
                ("exit_code", run.ExitCode.ToString(CultureInfo.InvariantCulture)),
                ("raw_output", Truncate(combined, 500)),
                ("attempts", string.Join(" || ", attempts)));

            if (!satisfied)
            {
                return FailResult(
                    EnvStationErrorCodes.AssertFailed,
                    $"{verdict.ResolvedPath} 的版本是 {versionText}，不满足要求 {expect}。" +
                    $" 完整输出：{Truncate(combined, 200)}");
            }

            return OkResult(
                expect is null
                    ? $"{Path.GetFileName(verdict.ResolvedPath)} 版本 {versionText}。"
                    : $"{Path.GetFileName(verdict.ResolvedPath)} 版本 {versionText}，满足要求 {expect}。",
                outputs,
                touched: [verdict.ResolvedPath]);
        }

        return FailResult(
            EnvStationErrorCodes.AssertFailed,
            $"无法从 {verdict.ResolvedPath} 的输出中解析出任何版本号。已尝试：{string.Join("、", attempts)}");
    }

    private static string? SafeMatch(Regex regex, string input)
    {
        try
        {
            var match = regex.Match(input.Length > 8192 ? input[..8192] : input);
            return match.Success && match.Groups["v"].Success ? match.Groups["v"].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}

/// <summary><c>envstation.verify.command_resolves</c>：校验命令最终解析到期望路径。</summary>
internal sealed class VerifyCommandResolvesAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.verify.command_resolves",
        "1.0.0",
        CapabilityIds.Inspect,
        "校验命令解析路径",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "registry.read"],
        Parameters:
        [
            Str("command", true, "命令名（如 python）", maxLength: 260),
            Path_("expect_path", true, "期望解析到的可执行文件完整路径"),
            new ParameterSpec("scope", ParameterType.Enum, false, "在哪个作用域的 PATH 中解析",
                AllowedValues: ["machine", "user", "process", "merged"]),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var command = arguments.GetString("command")!;
        var expectPath = arguments.GetString("expect_path")!;
        var scope = arguments.GetString("scope") ?? "merged";

        var pathValue = EnvironmentPathResolver.Read(scope);
        if (pathValue is null)
        {
            return Fail(EnvStationErrorCodes.NetUnreachable, $"无法读取{CommandResolver.ScopeLabel(scope)} PATH。");
        }

        var resolved = CommandResolver.Resolve(command, pathValue);

        var expectedFull = Path.GetFullPath(expectPath);
        var matches = resolved is not null
            && string.Equals(Path.GetFullPath(resolved), expectedFull, StringComparison.OrdinalIgnoreCase);

        var outputs = Outputs(
            ("command", command),
            ("scope", scope),
            ("resolved", resolved ?? string.Empty),
            ("expected", expectedFull),
            ("matches", Bool(matches)));

        if (resolved is null)
        {
            return Fail(
                EnvStationErrorCodes.AssertFailed,
                $"命令 {command} 在{CommandResolver.ScopeLabel(scope)} PATH 中未找到，期望解析到 {expectedFull}。");
        }

        if (!matches)
        {
            return Fail(
                EnvStationErrorCodes.AssertFailed,
                $"命令 {command} 解析到 {resolved}，与期望的 {expectedFull} 不一致。" +
                " 这通常意味着 PATH 中存在更靠前的同名命令（例如另一个工具自带的版本），" +
                "可使用 envstation.detect.conflict 查看全部来源。");
        }

        return Ok($"{command} 正确解析到 {resolved}。", outputs, touched: [resolved]);
    }
}

/// <summary>命令解析器（<c>verify.command_resolves</c> 与 <c>verify.conflict_clear</c> 共用）。</summary>
internal static class CommandResolver
{
    /// <summary>把 <c>scope</c> 的取值渲染为消息里的中文标签（取值本身仍原样出现在输出字段中）。</summary>
    internal static string ScopeLabel(string scope) => scope switch
    {
        "user" => "用户级",
        "machine" => "系统级",
        "process" => "进程级",
        _ => "合并",
    };

    /// <summary>在给定的 PATH 值中解析命令；找不到返回 null。</summary>
    internal static string? Resolve(string command, string pathValue)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var extensions = command.Contains('.', StringComparison.Ordinal)
            ? new[] { string.Empty }
            : new[] { ".exe", ".cmd", ".bat", ".com" };

        foreach (var entry in PathParser.Parse(pathValue, probeFileSystem: true))
        {
            if (entry.Issues != PathEntryIssue.None)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(entry.Normalized, command + extension);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    continue;
                }
            }
        }

        return null;
    }
}

/// <summary><c>envstation.verify.env_effective</c>：以新子进程读取环境变量，验证真实生效状态。</summary>
internal sealed class VerifyEnvEffectiveAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.verify.env_effective",
        "1.0.0",
        CapabilityIds.ProcessLaunch,
        "验证环境变量真实生效",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["process.launch", "registry.read"],
        Parameters:
        [
            Str("name", true, "变量名（只允许字母、数字与下划线）", maxLength: 255),
            Str("expect", false, "期望值，给出后会比对（末端反斜杠的差异会被忽略，这是 Windows 路径处理的已知行为）", maxLength: 32000),
            new ParameterSpec("scope", ParameterType.Enum, false,
                "只验证该作用域，留空表示验证新进程实际看到的值",
                AllowedValues: ["user", "machine", "process"]),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var name = arguments.GetString("name")!;
        var expect = arguments.GetString("expect");
        var scope = arguments.GetString("scope");

        // 变量名会被塞进 cmd 的命令行，因此必须严格限制字符集。
        // 这不是"防御性编程"，而是这套设计成立的前提：只有变量名是我们自己拼的，
        // 才能保证命令行里没有用户数据（AI-1 / S6）。
        if (!IsSafeVariableName(name))
        {
            return FailResult(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"变量名 {name} 含不允许的字符，只允许字母、数字与下划线。");
        }

        string? observed = null;
        string source;

        if (scope is "user" or "machine")
        {
            // 明确指定作用域时读的是"配置里到底写了什么"。
            //
            // 必须经由 context.Environment 而不是直接 new RegistryEnvStore：
            // 那条路径会绕过注入机制，导致两件重要的事同时失效——
            //   · 预演与测试无法隔离（会直接读到真实注册表）；
            //   · "未注入环境操作就一律失败"这条真机安全总闸被绕过。
            if (context.Environment is null)
            {
                return FailResult(
                    EnvStationErrorCodes.ActionFailed,
                    "本次运行没有启用环境变量读取能力（未注入环境操作实现）。");
            }

            var envScope = scope == "machine" ? EnvScope.Machine : EnvScope.User;
            var read = context.Environment.Read(envScope, name);
            if (read.IsFailure)
            {
                return FailResult(read.Error.Code, read.Error.Message + " " + read.Error.Remediation);
            }

            observed = read.Value?.RawValue;
            source = $"{CommandResolver.ScopeLabel(scope)}配置";
        }
        else
        {
            // 未指定作用域：启动一个**新进程**去读，验证"新开的程序能不能看到"。
            // 这是唯一能证明配置真正生效的方式——读取注册表只能证明"写进去了"。
            var cmd = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
                "cmd.exe");

            if (!File.Exists(cmd))
            {
                return FailResult(EnvStationErrorCodes.ActionFailed, "找不到 cmd.exe，无法启动验证子进程。");
            }

            // /d 跳过 AutoRun，/c 执行后退出。变量名已通过字符集校验，命令中不含包数据。
            ImmutableArray<string> cmdArguments = ["/d", "/c", $"echo %{name}%"];

            var run = await context.ProcessRunner
                .RunAsync(cmd, cmdArguments, TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);

            context.Audit("verify.env_effective", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["started"] = Bool(run.Started),
                ["exit_code"] = run.ExitCode.ToString(CultureInfo.InvariantCulture),
            });

            if (!run.Started)
            {
                return FailResult(
                    EnvStationErrorCodes.AssertFailed,
                    $"无法启动验证子进程：{run.FailureReason}");
            }

            var output = run.StandardOutput.Trim();
            // cmd 在变量未定义时会原样输出 %NAME%，这正是我们要识别的信号。
            observed = output.Contains($"%{name}%", StringComparison.OrdinalIgnoreCase) ? null : output;
            source = "新启动的子进程";
        }

        var outputs = Outputs(
            ("name", name),
            ("source", source),
            ("defined", Bool(observed is not null)),
            ("observed", observed ?? string.Empty),
            ("expected", expect ?? string.Empty));

        if (observed is null)
        {
            return FailResult(
                EnvStationErrorCodes.AssertFailed,
                $"{name} 在{source}中未定义。若刚刚写入过该变量，确认作用域是否正确，以及是否被其他工具删除。");
        }

        if (expect is not null && !ValueMatches(observed, expect))
        {
            return FailResult(
                EnvStationErrorCodes.AssertFailed,
                $"{name} 的实际值是 {Truncate(observed, 200)}，与期望值不一致。" +
                " 若差异仅在末端的反斜杠，属 Windows 路径处理的已知行为。");
        }

        return OkResult($"{name} 已生效（来源：{source}）。", outputs);
    }

    /// <summary>
    /// 比对实际值与期望值。
    /// </summary>
    /// <remarks>
    /// <b>末尾反斜杠为什么单独处理</b>：Windows 在把环境变量交给子进程时会对以反斜杠结尾的路径做处理，
    /// 而且 PATH 类变量的末尾分号也常被工具规范化。如果直接做严格相等，
    /// 用户会看到"明明设置对了却验证失败"这种极难理解的结论。
    /// 因此这里在严格相等之外，额外接受"仅末尾分隔符不同"的情况，并在消息中说明。
    /// </remarks>
    private static bool ValueMatches(string observed, string expected) =>
        string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            observed.TrimEnd('\\', '/', ';'),
            expected.TrimEnd('\\', '/', ';'),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeVariableName(string name)
    {
        if (name.Length is 0 or > 255)
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary><c>envstation.verify.file_exists</c>：校验文件/目录存在（含非空检查）。</summary>
internal sealed class VerifyFileExistsAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.verify.file_exists",
        "1.0.0",
        CapabilityIds.Inspect,
        "校验文件或目录存在",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read"],
        Parameters:
        [
            Path_("path", true, "要检查的路径"),
            new ParameterSpec("kind", ParameterType.Enum, false, "期望的类型",
                AllowedValues: ["any", "file", "directory"]),
            Bool("non_empty", "目录/文件是否必须非空", false),
            Int("min_bytes", "文件的最小字节数", 0, long.MaxValue),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var raw = arguments.GetString("path")!;
        var kind = arguments.GetString("kind") ?? "any";
        var nonEmpty = arguments.GetBoolean("non_empty", false);
        var minBytes = arguments.GetInt64("min_bytes", 0);

        // 允许检查授权根之外的路径吗？不允许——否则包可以用它当"文件系统探测器"，
        // 枚举出用户磁盘上有什么（信息泄露，需求 ISO-2）。
        var guard = context.GuardPath(raw, "path");
        if (guard.IsFailure)
        {
            return Fail(guard.Error.Code, guard.Error.Message + " " + guard.Error.Remediation);
        }

        var path = guard.Value;
        var isFile = File.Exists(path);
        var isDirectory = Directory.Exists(path);

        var outputs = Outputs(
            ("path", path),
            ("exists", Bool(isFile || isDirectory)),
            ("is_file", Bool(isFile)),
            ("is_directory", Bool(isDirectory)));

        if (!isFile && !isDirectory)
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"路径不存在：{path}");
        }

        if (kind == "file" && !isFile)
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"{path} 是目录，但期望是文件。");
        }

        if (kind == "directory" && !isDirectory)
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"{path} 是文件，但期望是目录。");
        }

        if (isFile && minBytes > 0)
        {
            var size = new FileInfo(path).Length;
            if (size < minBytes)
            {
                return Fail(
                    EnvStationErrorCodes.AssertFailed,
                    $"{path} 只有 {size} 字节，小于要求的 {minBytes} 字节（可能是下载被中断产生的残缺文件）。");
            }
        }

        if (nonEmpty)
        {
            if (isDirectory)
            {
                var any = Directory.EnumerateFileSystemEntries(path).Any();
                if (!any)
                {
                    return Fail(EnvStationErrorCodes.AssertFailed, $"目录 {path} 是空的。");
                }
            }
            else if (new FileInfo(path).Length == 0)
            {
                return Fail(EnvStationErrorCodes.AssertFailed, $"文件 {path} 长度为 0。");
            }
        }

        return Ok($"{(isDirectory ? "目录" : "文件")}存在：{path}", outputs, touched: [path]);
    }
}

/// <summary><c>envstation.verify.conflict_clear</c>：校验指定冲突已消解。</summary>
internal sealed class VerifyConflictClearAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.verify.conflict_clear",
        "1.0.0",
        CapabilityIds.Inspect,
        "校验同名命令冲突已消解",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "registry.read"],
        Parameters:
        [
            Str("command", true, "命令名（如 yarn）", maxLength: 260),
            Path_("expect_path", false, "期望解析到的路径，省略时只要求该命令没有多个来源"),
            new ParameterSpec("scope", ParameterType.Enum, false, "在哪个作用域的 PATH 中解析",
                AllowedValues: ["machine", "user", "process", "merged"]),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var command = arguments.GetString("command")!;
        var expectPath = arguments.GetString("expect_path");
        var scope = arguments.GetString("scope") ?? "merged";

        var pathValue = EnvironmentPathResolver.Read(scope);
        if (pathValue is null)
        {
            return Fail(EnvStationErrorCodes.NetUnreachable, $"无法读取{CommandResolver.ScopeLabel(scope)} PATH。");
        }

        var sources = new List<string>();
        var extensions = new[] { ".exe", ".cmd", ".bat", ".com" };

        foreach (var entry in PathParser.Parse(pathValue, probeFileSystem: true))
        {
            if (entry.Issues != PathEntryIssue.None || !Directory.Exists(entry.Normalized))
            {
                continue;
            }

            foreach (var candidate in new[] { command }.Concat(extensions.Select(e => command + e)))
            {
                try
                {
                    var full = Path.Combine(entry.Normalized, candidate);
                    if (File.Exists(full))
                    {
                        sources.Add(Path.GetFullPath(full));
                        break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    continue;
                }
            }
        }

        var outputs = Outputs(
            ("command", command),
            ("source_count", sources.Count.ToString(CultureInfo.InvariantCulture)),
            ("sources", string.Join(" || ", sources)),
            ("resolved", sources.Count > 0 ? sources[0] : string.Empty));

        if (sources.Count == 0)
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"命令 {command} 在{CommandResolver.ScopeLabel(scope)} PATH 中未找到。");
        }

        if (expectPath is not null)
        {
            var expected = Path.GetFullPath(expectPath);
            if (!string.Equals(sources[0], expected, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    EnvStationErrorCodes.AssertFailed,
                    $"{command} 当前解析到 {sources[0]}，而不是期望的 {expected}。" +
                    $" PATH 中共有 {sources.Count} 个来源：{string.Join(" → ", sources)}。" +
                    " 可使用 envstation.path.prioritize 把期望的目录提到前面。");
            }
        }
        else if (sources.Count > 1)
        {
            return Fail(
                EnvStationErrorCodes.AssertFailed,
                $"{command} 仍有 {sources.Count} 个来源，冲突未消解：{string.Join(" → ", sources)}。" +
                " 生效的是第一个（排在最前面的那个）。");
        }

        return Ok(
            $"{command} 冲突已消解，唯一来源：{sources[0]}。",
            outputs,
            touched: sources);
    }
}

/// <summary><c>envstation.verify.assert</c>：通用断言，失败按 on_error 处理。</summary>
internal sealed class AssertAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.verify.assert",
        "1.0.0",
        CapabilityIds.Inspect,
        "通用断言",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 10,
        TouchedResources: [],
        Parameters:
        [
            new ParameterSpec("condition", ParameterType.String, true,
                "条件表达式，使用受限表达式语言（可用变量、比较、逻辑与内置纯函数）", MaxLength: 1024),
            Str("message", true, "断言失败时向用户展示的说明", maxLength: 512),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var condition = arguments.GetString("condition")!;
        var message = arguments.GetString("message")!;

        var evaluated = Scripting.ExpressionEvaluator.EvaluateCondition(condition, context.VariableTable);

        if (evaluated.IsFailure)
        {
            // 表达式本身写错 ≠ 断言不成立。两者必须区分：
            // 前者是包的缺陷（要修包），后者是环境不满足（用户要处理）。
            return Fail(
                evaluated.Error.Code,
                $"断言表达式无法求值：{evaluated.Error.Message} {evaluated.Error.Remediation}");
        }

        var outputs = Outputs(("condition", condition), ("result", Bool(evaluated.Value)));

        if (evaluated.Value)
        {
            return Ok($"断言通过：{condition}", outputs);
        }

        return Fail(EnvStationErrorCodes.AssertFailed, $"断言失败：{message}（条件：{condition}）");
    }
}

/// <summary><c>envstation.verify.config_restored</c>：校验配置文件已还原到备份状态。</summary>
internal sealed class VerifyConfigRestoredAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.verify.config_restored",
        "1.0.0",
        CapabilityIds.Inspect,
        "校验配置文件已回滚",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["filesystem.read"],
        Parameters:
        [
            Path_("path", true, "要校验的配置文件路径"),
            Sha256("expect_sha256", true, "期望的内容哈希（备份时记录的哈希）"),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var raw = arguments.GetString("path")!;
        var expect = ActionRegistry.NormalizeHash(arguments.GetString("expect_sha256")!);

        var guard = context.GuardPath(raw, "path");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message + " " + guard.Error.Remediation);
        }

        var path = guard.Value;
        if (!File.Exists(path))
        {
            return FailResult(EnvStationErrorCodes.AssertFailed, $"配置文件不存在：{path}");
        }

        string actual;
        using (var sha = SHA256.Create())
        {
            await using var stream = File.OpenRead(path);
            var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            actual = Convert.ToHexString(hash).ToLowerInvariant();
        }

        var matches = string.Equals(actual, expect, StringComparison.Ordinal);
        var outputs = Outputs(
            ("path", path),
            ("actual_sha256", actual),
            ("expected_sha256", expect ?? string.Empty),
            ("matches", Bool(matches)));

        return matches
            ? OkResult($"配置文件 {path} 的内容与备份一致。", outputs, touched: [path])
            : FailResult(
                EnvStationErrorCodes.AssertFailed,
                $"配置文件 {path} 的内容与备份不一致（当前 {actual[..12]}…，期望 {expect?[..12]}…）。" +
                " 这可能意味着回滚没有生效，或文件在回滚后又被其他程序改动过。");
    }
}
