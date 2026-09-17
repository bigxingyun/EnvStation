using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Core.Configuration;
using EnvStation.Core.Installation;
using AbsPkg = EnvStation.Abstractions.Packages;

namespace EnvStation.Core.Actions.Builtin;

// ══════════════════════════════════════════════════════════════════════════════
//  A04 包管理器与安装（6 个动作） · A07 运行时版本管理（5 个动作）
//
//  这一组是"真的往用户机器上装东西"，因此三条纪律比别处更硬：
//
//    1. **落盘必须走影子目录 + 原子切换**（local.install / runtime.install）：
//       直接往目标目录解压，中途失败会留下一个半残的运行时——而半残的运行时比没有更糟，
//       因为它看起来是"装好了"的。
//    2. **未托管的运行时只能登记、不能删**：用户自己装的 JDK 不属于我们，
//       runtime.remove 对 "registered" 来源一律拒绝（只从清单里摘掉记录）。
//    3. **包管理器的命令与参数全部白名单化**（需求 AI-1）：
//       winget / scoop / choco 的调用参数由我们自己拼固定模板，包只能提供**包名**，
//       且包名要过字符集校验。这是"防注入"在这条路径上的唯一可行做法。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>安装类动作的公共基类。</summary>
internal abstract class InstallActionBase : EnvActionBase
{
    /// <summary>受支持的包管理器。</summary>
    protected static ImmutableArray<string> Managers { get; } = ["winget", "scoop", "choco"];

    /// <summary>解析包管理器可执行文件路径。</summary>
    protected static string? ResolveManagerExecutable(string manager)
    {
        var candidates = manager.ToLowerInvariant() switch
        {
            "winget" => new[] { Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe") },
            "scoop" => new[]
            {
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "scoop", "shims", "scoop.cmd"),
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "scoop", "shims", "scoop.exe"),
            },
            "choco" => new[] { @"C:\ProgramData\chocolatey\bin\choco.exe" },
            _ => [],
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 回退：在 PATH 中查找（不依赖硬编码路径）。
        var path = EnvironmentPathResolver.Read("merged");
        if (path is null)
        {
            return null;
        }

        var isWinget = string.Equals(manager, "winget", StringComparison.OrdinalIgnoreCase);
        var extension = isWinget ? ".exe" : ".cmd";
        return CommandResolver.Resolve(manager, path)
            ?? CommandResolver.Resolve(manager + extension, path);
    }

    /// <summary>
    /// 校验包名。
    /// </summary>
    /// <remarks>
    /// 包名会被拼进命令行，因此必须严格限制字符集。
    /// 允许的字符覆盖真实世界的包名：<c>Python.Python.3.12</c>、<c>openjdk@17</c>、
    /// <c>nodejs-lts</c>、<c>git</c>、<c>microsoft-visualstudio-2022</c>。
    /// 明确禁止空格、引号、<c>&amp;</c>、<c>|</c>、<c>&gt;</c>、<c>;</c> 等一切元字符。
    /// </remarks>
    protected static Result<string> ValidatePackageName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"包名非法：{name}");
        }

        foreach (var ch in name)
        {
            var ok = char.IsAsciiLetterOrDigit(ch)
                || ch is '.' or '-' or '_' or '+' or '@' or '/' or ':';

            if (!ok)
            {
                return Result<string>.Fail(
                    EnvStationErrorCodes.ActionArgumentInvalid,
                    $"包名 {name} 含不允许的字符 '{ch}'。",
                    "包名只允许字母、数字与 . - _ + @ / : 这些字符。");
            }
        }

        return Result<string>.Ok(name);
    }

    /// <summary>版本号同样要过字符集校验（它也会进命令行）。</summary>
    protected static Result<string> ValidateVersion(string version)
    {
        if (version.Length > 64)
        {
            return Result<string>.Fail(EnvStationErrorCodes.ActionArgumentInvalid, "版本号过长。");
        }

        foreach (var ch in version)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '-' or '_' or '+'))
            {
                return Result<string>.Fail(
                    EnvStationErrorCodes.ActionArgumentInvalid,
                    $"版本号 {version} 含不允许的字符 '{ch}'。");
            }
        }

        return Result<string>.Ok(version);
    }

    /// <summary>执行一次白名单化的包管理器命令。</summary>
    protected static async ValueTask<(bool Ok, string Output, string Invocation)> RunManagerAsync(
        ActionExecutionContext context,
        string executable,
        ImmutableArray<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var invocation = ExecutableAllowList.DescribeInvocation(executable, arguments);

        context.Audit("pkg.exec", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["invocation"] = invocation,
        });

        var run = await context.ProcessRunner
            .RunAsync(executable, arguments, timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!run.Started)
        {
            return (false, run.FailureReason ?? "未能启动", invocation);
        }

        var output = (run.StandardOutput + "\n" + run.StandardError).Trim();
        return (run.ExitCode == 0, output.Length <= 4000 ? output : output[..4000] + "…", invocation);
    }
}

/// <summary><c>envstation.pkg.install</c>：通过包管理器安装。</summary>
internal sealed class PkgInstallAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.pkg.install",
        "1.0.0",
        CapabilityIds.PackageManager,
        "通过包管理器安装",
        IsIdempotent: false,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 1800,
        TouchedResources: ["process.launch", "filesystem.write", "network.outbound"],
        Parameters:
        [
            new ParameterSpec("manager", ParameterType.Enum, true, "使用哪个包管理器",
                AllowedValues: Managers),
            Str("package", true, "包名（只允许字母、数字与 . - _ + @ / :）", maxLength: 128),
            Str("version", false, "指定版本；省略则装最新", maxLength: 64),
            Bool("silent", "静默安装（不弹交互界面）", true),
            Bool("accept_agreements", "自动接受包管理器的许可协议", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var manager = arguments.GetString("manager")!;
        var nameResult = ValidatePackageName(arguments.GetString("package")!);
        if (nameResult.IsFailure)
        {
            return FailResult(nameResult.Error.Code, nameResult.Error.Message, nameResult.Error.Remediation);
        }

        var name = nameResult.Value;
        var version = arguments.GetString("version");
        var silent = arguments.GetBoolean("silent", true);
        var acceptAgreements = arguments.GetBoolean("accept_agreements", false);

        if (version is not null)
        {
            var versionResult = ValidateVersion(version);
            if (versionResult.IsFailure)
            {
                return FailResult(versionResult.Error.Code, versionResult.Error.Message, versionResult.Error.Remediation);
            }
        }

        var executable = ResolveManagerExecutable(manager);
        if (executable is null)
        {
            return FailResult(
                EnvStationErrorCodes.ActionFailed,
                $"找不到包管理器 {manager}。",
                manager switch
                {
                    "winget" => "winget 随「应用安装程序」提供，先在 Microsoft Store 安装它。",
                    "scoop" => "scoop 需要先安装：https://scoop.sh",
                    _ => "choco 需要先安装 Chocolatey。",
                });
        }

        // 参数由我们自己拼固定模板 —— 包只能提供**包名**，不能提供任何参数形式的东西。
        // 这是需求 AI-1「所有执行外部程序的能力集中且参数白名单化」在这条路径上的落地。
        var commandArgs = manager.ToLowerInvariant() switch
        {
            "winget" => BuildWingetArgs(name, version, silent, acceptAgreements),
            "scoop" => BuildScoopArgs(name, version),
            _ => BuildChocoArgs(name, version, silent),
        };

        var (ok, output, invocation) = await RunManagerAsync(
            context, executable, commandArgs, TimeSpan.FromMinutes(25), cancellationToken).ConfigureAwait(false);

        var outputs = Outputs(
            ("manager", manager),
            ("package", name),
            ("version", version ?? "latest"),
            ("invocation", invocation),
            ("succeeded", Bool(ok)),
            ("output", output));

        if (!ok)
        {
            return FailResult(
                EnvStationErrorCodes.ActionFailed,
                $"{manager} 安装 {name} 失败。命令：{invocation}。输出：{output}",
                $"{manager} 的常见失败原因是包名写错、需要管理员权限，或网络不可达。" +
                " 可用 envstation.pkg.list 查看该管理器里实际可用的包名。");
        }

        return OkResult(
            $"{manager} 已安装 {name}{(version is null ? string.Empty : " " + version)}。",
            outputs);
    }

    private static ImmutableArray<string> BuildWingetArgs(string name, string? version, bool silent, bool acceptAgreements)
    {
        // winget 的参数顺序与取值固定，不接受包提供任何额外开关。
        var args = ImmutableArray.CreateBuilder<string>();
        args.Add("install");
        args.Add("--id");
        args.Add(name);
        args.Add("--exact");

        if (version is not null)
        {
            args.Add("--version");
            args.Add(version);
        }

        if (silent)
        {
            args.Add("--silent");
        }

        if (acceptAgreements)
        {
            args.Add("--accept-package-agreements");
            args.Add("--accept-source-agreements");
        }

        // 禁用交互提示：否则无人值守场景会一直挂在那里等输入。
        args.Add("--disable-interactivity");
        return args.ToImmutable();
    }

    private static ImmutableArray<string> BuildScoopArgs(string name, string? version)
    {
        var args = ImmutableArray.CreateBuilder<string>();
        args.Add("install");
        args.Add(version is null ? name : $"{name}@{version}");
        return args.ToImmutable();
    }

    private static ImmutableArray<string> BuildChocoArgs(string name, string? version, bool silent)
    {
        var args = ImmutableArray.CreateBuilder<string>();
        args.Add("install");
        args.Add(name);
        if (version is not null)
        {
            args.Add("--version=" + version);
        }

        if (silent)
        {
            args.Add("--yes");
            args.Add("--no-progress");
        }

        return args.ToImmutable();
    }
}

/// <summary><c>envstation.pkg.uninstall</c>：通过包管理器卸载。</summary>
internal sealed class PkgUninstallAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.pkg.uninstall",
        "1.0.0",
        CapabilityIds.PackageManager,
        "通过包管理器卸载",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 1200,
        TouchedResources: ["process.launch", "filesystem.write"],
        Parameters:
        [
            new ParameterSpec("manager", ParameterType.Enum, true, "包管理器", AllowedValues: Managers),
            Str("package", true, "包名", maxLength: 128),
            Bool("purge", "同时删除用户数据（scoop 的 --purge）", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var manager = arguments.GetString("manager")!;
        var nameResult = ValidatePackageName(arguments.GetString("package")!);
        if (nameResult.IsFailure)
        {
            return FailResult(nameResult.Error.Code, nameResult.Error.Message, nameResult.Error.Remediation);
        }

        var packageName = nameResult.Value;
        var purge = arguments.GetBoolean("purge", false);
        var executable = ResolveManagerExecutable(manager);
        if (executable is null)
        {
            return FailResult(EnvStationErrorCodes.ActionFailed, $"找不到包管理器 {manager}。");
        }

        ImmutableArray<string> commandArgs = manager.ToLowerInvariant() switch
        {
            "winget" => ["uninstall", "--id", packageName, "--exact", "--silent", "--disable-interactivity"],
            "scoop" => purge ? ["uninstall", packageName, "--purge"] : ["uninstall", packageName],
            _ => ["uninstall", packageName, "--yes", "--no-progress"],
        };

        var (ok, output, invocation) = await RunManagerAsync(
            context, executable, commandArgs, TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);

        if (!ok && output.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            // 幂等：本来就没装，视作已经达成目标。
            return OkResult(
                $"{packageName} 本来就没有通过 {manager} 安装，无需卸载。",
                Outputs(("changed", "false"), ("manager", manager), ("package", nameResult.Value)));
        }

        return ok
            ? OkResult(
                $"{manager} 已卸载 {packageName}。",
                Outputs(("changed", "true"), ("manager", manager), ("package", nameResult.Value), ("output", output)))
            : FailResult(
                EnvStationErrorCodes.ActionFailed,
                $"{manager} 卸载 {packageName} 失败。命令：{invocation}。输出：{output}",
                "可能是包名不正确，或需要管理员权限。");
    }
}

/// <summary><c>envstation.pkg.list</c>：列出已装包及版本。</summary>
internal sealed class PkgListAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.pkg.list",
        "1.0.0",
        CapabilityIds.Inspect,
        "列出已安装的包",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 180,
        TouchedResources: ["process.launch"],
        Parameters:
        [
            new ParameterSpec("manager", ParameterType.Enum, true, "包管理器", AllowedValues: Managers),
            Str("filter", false, "只列出包名含该子串的项", maxLength: 64),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var manager = arguments.GetString("manager")!;
        var filter = arguments.GetString("filter");

        // 注意：这道动作申请的是 CAP.INSPECT，但它需要运行包管理器。
        // 这是刻意的取舍——"列出已装包"在用户心智里是只读操作。
        // 因此我们只允许**固定的列表子命令**，不接受任何来自包的参数（filter 只做本地过滤）。
        var executable = ResolveManagerExecutable(manager);
        if (executable is null)
        {
            return FailResult(EnvStationErrorCodes.ActionFailed, $"找不到包管理器 {manager}。");
        }

        ImmutableArray<string> commandArgs = manager.ToLowerInvariant() switch
        {
            "winget" => ["list", "--disable-interactivity"],
            "scoop" => ["list"],
            _ => ["list", "--no-progress"],
        };

        var (ok, output, _) = await RunManagerAsync(
            context, executable, commandArgs, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            return FailResult(
                EnvStationErrorCodes.ActionFailed,
                $"{manager} list 失败：{output}");
        }

        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => filter is null || l.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return OkResult(
            $"{manager} 共列出 {lines.Length} 行" + (filter is null ? string.Empty : $"（过滤条件：{filter}）") + "。",
            Outputs(
                ("manager", manager),
                ("line_count", lines.Length.ToString(CultureInfo.InvariantCulture)),
                ("content", string.Join('\n', lines.Take(400)))));
    }
}

/// <summary><c>envstation.local.install</c>：把已下载/本地内容装到目标目录（影子目录 + 原子切换）。</summary>
internal sealed class LocalInstallAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.local.install",
        "1.0.0",
        CapabilityIds.FileSystemInstall,
        "安装到目标目录",
        IsIdempotent: false,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 900,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("source", true, "已下载并校验过的目录，或已解压的目录"),
            Path_("dest", true, "最终安装目录"),
            Bool("replace_existing", "目标已存在时是否替换（旧目录会先备份）", false),
            Bool("dry_run", "只做预检与体积统计", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var srcGuard = context.GuardPath(arguments.GetString("source")!, "source");
        if (srcGuard.IsFailure)
        {
            return FailResult(srcGuard.Error.Code, srcGuard.Error.Message, srcGuard.Error.Remediation);
        }

        var destGuard = context.GuardPath(arguments.GetString("dest")!, "dest");
        if (destGuard.IsFailure)
        {
            return FailResult(destGuard.Error.Code, destGuard.Error.Message, destGuard.Error.Remediation);
        }

        var source = srcGuard.Value;
        var dest = destGuard.Value;
        var replaceExisting = arguments.GetBoolean("replace_existing", false);
        var dryRun = arguments.GetBoolean("dry_run", false);

        if (!Directory.Exists(source))
        {
            return FailResult(EnvStationErrorCodes.AssertFailed, $"源目录不存在：{source}");
        }

        var (fileCount, totalBytes) = FileSystemMeter.Measure(source, isDirectory: true);

        if (Directory.Exists(dest) && !replaceExisting)
        {
            return FailResult(
                EnvStationErrorCodes.ActionFailed,
                $"目标目录已存在：{dest}",
                "默认不覆盖已有安装。确认替换时把 replace_existing 设为 true；旧目录会先备份，失败时可以回滚。");
        }

        if (dryRun)
        {
            return OkResult(
                $"预演：将把 {fileCount} 个文件（{totalBytes / 1024.0 / 1024.0:F1} MB）从 {source} 安装到 {dest}。" +
                "安装先写入影子目录，失败时目标目录保持不变。",
                Outputs(
                    ("dry_run", "true"),
                    ("file_count", fileCount.ToString(CultureInfo.InvariantCulture)),
                    ("bytes", totalBytes.ToString(CultureInfo.InvariantCulture))));
        }

        var stagingRoot = Path.Combine(context.AuthorizedRoots.Length > 0 ? context.AuthorizedRoots[0] : Path.GetTempPath(), "staging");
        var installer = new ShadowInstaller(stagingRoot);

        var transactionId = $"{context.RunId}-{Guid.NewGuid().ToString("N")[..6]}";
        var plan = installer.CreatePlan(transactionId, dest);
        if (plan.IsFailure)
        {
            return FailResult(plan.Error.Code, plan.Error.Message, plan.Error.Remediation);
        }

        var quota = context.Quota.TryConsume(QuotaKind.FileWrite, fileCount);
        if (quota.IsFailure)
        {
            return FailResult(quota.Error.Code, quota.Error.Message, quota.Error.Remediation);
        }

        // ① 先在影子目录里把内容放好。
        var staged = ShadowInstaller.Stage(plan.Value);
        if (staged.IsFailure)
        {
            return FailResult(staged.Error.Code, staged.Error.Message, staged.Error.Remediation);
        }

        try
        {
            FileSystemCopier.CopyDirectory(source, plan.Value.StagingPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ShadowInstaller.Discard(plan.Value);
            return FailResult(EnvStationErrorCodes.PathNotWritable, $"写入影子目录失败：{ex.Message}");
        }

        // ② 原子切换。失败时 ShadowInstaller 会自己还原目标。
        var commit = await installer.CommitAsync(plan.Value, cancellationToken).ConfigureAwait(false);
        if (commit.IsFailure)
        {
            return FailResult(commit.Error.Code, commit.Error.Message, commit.Error.Remediation);
        }

        return OkResult(
            $"已把 {fileCount} 个文件（{totalBytes / 1024.0 / 1024.0:F1} MB）安装到 {dest}（{commit.Value.Strategy}）。",
            Outputs(
                ("dest", dest),
                ("file_count", fileCount.ToString(CultureInfo.InvariantCulture)),
                ("bytes", totalBytes.ToString(CultureInfo.InvariantCulture)),
                ("strategy", commit.Value.Strategy.ToString()),
                ("strategy_detail", commit.Value.Strategy.ToString()),
                ("elapsed_ms", ((long)commit.Value.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture))),
            touched: [dest]);
    }
}

/// <summary><c>envstation.local.register</c>：把已存在的运行时登记进清单（不移动文件）。</summary>
internal sealed class LocalRegisterAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.local.register",
        "1.0.0",
        CapabilityIds.RuntimeManage,
        "登记已有运行时",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            new ParameterSpec("kind", ParameterType.Enum, true, "运行时种类",
                AllowedValues: RuntimeCatalog.Kinds),
            Path_("path", true, "已存在的运行时主目录"),
            Str("version", true, "版本号", maxLength: 64),
            Bool("verify", "登记前先用 envstation.verify.version_output 的方式确认可运行（此处只做静态检查）", true),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var kind = arguments.GetString("kind")!;
        var pathGuard = context.GuardPath(arguments.GetString("path")!, "path");
        if (pathGuard.IsFailure)
        {
            return Fail(pathGuard.Error.Code, pathGuard.Error.Message, pathGuard.Error.Remediation);
        }

        var version = arguments.GetString("version")!;
        var path = pathGuard.Value;

        if (!Directory.Exists(path))
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"目录不存在：{path}");
        }

        if (!RuntimeCatalog.TryGet(kind, out var definition))
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"未知的运行时种类 {kind}。已知：{string.Join("、", RuntimeCatalog.Kinds)}。");
        }

        // 静态证据检查：目录里至少要能看到该运行时的可执行文件。
        // 这不等于"它一定能跑"，但能挡住"登记了一个不相干的目录"这种最常见的误用。
        var hasExecutable = definition.Executables.Any(exe =>
            File.Exists(Path.Combine(path, exe)) || File.Exists(Path.Combine(path, "bin", exe)));

        if (!hasExecutable)
        {
            return Fail(
                EnvStationErrorCodes.AssertFailed,
                $"{path} 中找不到 {definition.DisplayName} 的可执行文件（期望其中之一：{string.Join("、", definition.Executables)}）。",
                "登记不会移动或修改任何文件，但目录必须确实是该运行时的主目录（JAVA_HOME 语义）。");
        }

        var store = new RuntimeManifestStore(ManifestPath(context));

        // 关键：来源记为 "registered" —— 用户自己装的东西，我们只记下来，绝不删。
        var registration = new RuntimeRegistration(
            kind,
            version,
            path,
            "registered",
            DateTimeOffset.Now,
            []);

        var upsert = store.Upsert(registration);
        if (upsert.IsFailure)
        {
            return Fail(upsert.Error.Code, upsert.Error.Message, upsert.Error.Remediation);
        }

        return Ok(
            $"已登记 {definition.DisplayName} {version} → {path}（来源：用户已有，环境站不会删除它）。",
            Outputs(
                ("kind", kind),
                ("version", version),
                ("path", path),
                ("source", "registered")));
    }

    /// <summary>清单落在第一个授权根目录下，与备份同一个原则：受同一套授权约束。</summary>
    internal static string ManifestPath(ActionExecutionContext context) =>
        Path.Combine(
            context.AuthorizedRoots.Length > 0 ? context.AuthorizedRoots[0] : Path.GetTempPath(),
            "runtimes.json");
}

/// <summary><c>envstation.fs.link</c>：创建符号链接/目录联接，失败自动降级为复制。</summary>
internal sealed class FileSystemLinkAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.fs.link",
        "1.0.0",
        CapabilityIds.Link,
        "创建链接",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 300,
        TouchedResources: ["filesystem.write"],
        Parameters:
        [
            Path_("target", true, "链接指向的真实目录"),
            Path_("link", true, "要创建的链接路径"),
            new ParameterSpec("mode", ParameterType.Enum, false,
                "junction = 目录联接（不需要管理员权限，最常用）；symbolic = 符号链接（可能需要开发者模式）；" +
                "copy = 直接复制（最兼容，但没有链接的即时性）",
                AllowedValues: ["junction", "symbolic", "copy"]),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var targetGuard = context.GuardPath(arguments.GetString("target")!, "target");
        if (targetGuard.IsFailure)
        {
            return Fail(targetGuard.Error.Code, targetGuard.Error.Message, targetGuard.Error.Remediation);
        }

        var linkGuard = context.GuardPath(arguments.GetString("link")!, "link");
        if (linkGuard.IsFailure)
        {
            return Fail(linkGuard.Error.Code, linkGuard.Error.Message, linkGuard.Error.Remediation);
        }

        var target = targetGuard.Value;
        var link = linkGuard.Value;
        var mode = arguments.GetString("mode") ?? "junction";

        if (!Directory.Exists(target))
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"目标目录不存在：{target}");
        }

        if (Directory.Exists(link) || File.Exists(link))
        {
            return Ok(
                $"链接路径已存在：{link}（未做任何修改）。",
                Outputs(("created", "false"), ("link", link), ("target", target)));
        }

        var parent = Path.GetDirectoryName(link);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var failureReason = string.Empty;
        if (mode != "copy" && TryCreateLink(target, link, mode, out failureReason))
        {
            return Ok(
                $"已创建{(mode == "junction" ? "目录联接" : "符号链接")}：{link} → {target}。",
                Outputs(
                    ("created", "true"),
                    ("mode", mode),
                    ("link", link),
                    ("target", target)),
                touched: [link]);
        }

        // 降级为复制。这必须**明确告知**用户：复制出来的目录不会随源目录更新，
        // 静默降级会让用户以为"切换版本"生效了，实际改的是另一份副本。
        var (fileCount, totalBytes) = FileSystemMeter.Measure(target, isDirectory: true);
        var quota = context.Quota.TryConsume(QuotaKind.FileWrite, fileCount);
        if (quota.IsFailure)
        {
            return Fail(quota.Error.Code, quota.Error.Message, quota.Error.Remediation);
        }

        try
        {
            FileSystemCopier.CopyDirectory(target, link, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"复制降级失败：{ex.Message}");
        }

        return Ok(
            $"无法创建{(mode == "junction" ? "目录联接" : "符号链接")}（{failureReason}），已降级为复制 {fileCount} 个文件到 {link}。" +
            "副本不会随源目录变化而更新。",
            Outputs(
                ("created", "true"),
                ("mode", "copy"),
                ("fallback_reason", failureReason),
                ("file_count", fileCount.ToString(CultureInfo.InvariantCulture)),
                ("bytes", totalBytes.ToString(CultureInfo.InvariantCulture)),
                ("link", link),
                ("target", target)),
            touched: [link]);
    }

    private static bool TryCreateLink(string target, string link, string mode, out string failureReason)
    {
        failureReason = string.Empty;

        try
        {
            if (mode == "junction")
            {
                // 目录联接在任何 Windows 上都不需要特权，因此是默认选择。
                if (CreateSymbolicLink(link, target, SymbolicLinkFlags.Directory) != 0)
                {
                    return true;
                }
            }
            else
            {
                if (CreateSymbolicLink(link, target, SymbolicLinkFlags.Directory | SymbolicLinkFlags.AllowUnprivilegedCreate) != 0)
                {
                    return true;
                }
            }

            failureReason = $"Win32 错误 {Marshal.GetLastWin32Error()}";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    [Flags]
    private enum SymbolicLinkFlags : uint
    {
        File = 0x0,
        Directory = 0x1,
        AllowUnprivilegedCreate = 0x2,
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern byte CreateSymbolicLink(string linkPath, string targetPath, SymbolicLinkFlags flags);
}

// ══════════════════════════════════════════════════════════════════════════════
//  A07 运行时版本管理
// ══════════════════════════════════════════════════════════════════════════════

/// <summary><c>envstation.runtime.install</c>：下载 → 校验 → 解压 → 影子目录 → 原子切换 → 登记。</summary>
internal sealed class RuntimeInstallAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.runtime.install",
        "1.0.0",
        CapabilityIds.RuntimeManage,
        "安装运行时版本",
        IsIdempotent: false,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 3600,
        TouchedResources: ["network.outbound", "filesystem.write", "process.launch"],
        Parameters:
        [
            new ParameterSpec("kind", ParameterType.Enum, true, "运行时种类", AllowedValues: RuntimeCatalog.Kinds),
            Str("version", true, "版本号", maxLength: 64),
            Path_("dest", true, "安装到哪个目录（该目录将成为该版本的主目录）"),
            Url("source", false, "离线归档地址；与 source_path 二者必填其一"),
            Path_("source_path", false, "离线归档在本地磁盘上的路径（已下载并校验过）"),
            Sha256("sha256", false, "离线归档的 SHA-256；通过 source 下载时必须提供"),
            new ParameterSpec("strip_components", ParameterType.Integer, false,
                "解压时去掉最前面的几层目录（Node 的 tar.gz 通常需要 1）", Minimum: 0, Maximum: 8, DefaultValue: "0"),
            Bool("dry_run", "只报告将要做什么", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var kind = arguments.GetString("kind")!;
        var version = arguments.GetString("version")!;
        var destGuard = context.GuardPath(arguments.GetString("dest")!, "dest");
        if (destGuard.IsFailure)
        {
            return FailResult(destGuard.Error.Code, destGuard.Error.Message, destGuard.Error.Remediation);
        }

        var dest = destGuard.Value;
        var sourcePath = arguments.GetString("source_path");
        var sourceUrl = arguments.GetString("source");
        var sha256 = arguments.GetString("sha256");
        var strip = (int)arguments.GetInt64("strip_components", 0);
        var dryRun = arguments.GetBoolean("dry_run", false);

        if (!RuntimeCatalog.TryGet(kind, out var definition))
        {
            return FailResult(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"未知的运行时种类 {kind}。已知：{string.Join("、", RuntimeCatalog.Kinds)}。");
        }

        if (Directory.Exists(dest))
        {
            return FailResult(
                EnvStationErrorCodes.ActionFailed,
                $"目标目录已存在：{dest}",
                "每个版本应装到独立目录（例如 D:\\Dev\\Python\\3.12.1），这样多版本可以共存、切换只改链接。");
        }

        if (dryRun)
        {
            var plan = sourcePath is not null
                ? $"从本地 {sourcePath} 解压安装"
                : sourceUrl is not null ? $"从 {sourceUrl} 下载后安装" : "通过包管理器安装";
            return OkResult(
                $"预演：将把 {definition.DisplayName} {version} {plan} 到 {dest}（strip_components={strip}），未做任何修改。",
                Outputs(("dry_run", "true"), ("kind", kind), ("version", version), ("dest", dest)));
        }

        var workspace = Path.Combine(
            context.AuthorizedRoots.Length > 0 ? context.AuthorizedRoots[0] : Path.GetTempPath(),
            "downloads");
        Directory.CreateDirectory(workspace);

        string? archivePath = sourcePath is not null ? context.GuardPath(sourcePath, "source_path").Value : null;

        // ① 需要时先下载（强制哈希）。
        if (archivePath is null && sourceUrl is not null)
        {
            if (string.IsNullOrWhiteSpace(sha256))
            {
                return FailResult(
                    EnvStationErrorCodes.ActionArgumentInvalid,
                    "通过 URL 安装时必须提供 sha256。",
                    "没有哈希的下载等于把「装什么」的决定权交给网络。");
            }

            if (context.Network is null)
            {
                return FailResult(EnvStationErrorCodes.NetUnreachable, "本次运行没有启用网络能力。");
            }

            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var url))
            {
                return FailResult(EnvStationErrorCodes.ActionArgumentInvalid, $"source 不是合法 URL：{sourceUrl}");
            }

            var fileName = Path.GetFileName(url.LocalPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = $"{kind}-{version}.archive";
            }

            archivePath = Path.Combine(workspace, fileName);

            var expected = ActionRegistry.NormalizeHash(sha256);
            var download = await context.Network
                .DownloadAsync(url, context.AllowedHosts, archivePath, 4L * 1024 * 1024 * 1024, null, cancellationToken)
                .ConfigureAwait(false);

            if (download.IsFailure)
            {
                return FailResult(download.Error.Code, download.Error.Message, download.Error.Remediation);
            }

            if (!string.Equals(download.Value.ActualSha256, expected, StringComparison.Ordinal))
            {
                try
                {
                    File.Delete(archivePath);
                }
                catch (IOException)
                {
                    // 清理失败不改变结论。
                }

                return FailResult(
                    EnvStationErrorCodes.NetHashMismatch,
                    $"下载内容的哈希不符：{url} 实际 {download.Value.ActualSha256[..16]}…，期望 {expected?[..16]}…",
                    "已删除下载的文件。检查镜像是否同步了正确的版本。");
            }
        }

        // ② 有压缩包就解压，没有就走包管理器。
        if (archivePath is not null)
        {
            if (!File.Exists(archivePath))
            {
                return FailResult(EnvStationErrorCodes.AssertFailed, $"离线归档不存在：{archivePath}");
            }

            var stagingRoot = Path.Combine(
                context.AuthorizedRoots.Length > 0 ? context.AuthorizedRoots[0] : Path.GetTempPath(),
                "staging");
            var installer = new ShadowInstaller(stagingRoot);
            var plan = installer.CreatePlan($"{context.RunId}-{Guid.NewGuid().ToString("N")[..6]}", dest);
            if (plan.IsFailure)
            {
                return FailResult(plan.Error.Code, plan.Error.Message, plan.Error.Remediation);
            }

            var staged = ShadowInstaller.Stage(plan.Value);
            if (staged.IsFailure)
            {
                return FailResult(staged.Error.Code, staged.Error.Message, staged.Error.Remediation);
            }

            // 复用 archive.extract 的实现思路：这里直接调用同一个检查器，保证穿越与炸弹防护一致。
            var extract = await ExtractIntoAsync(context, archivePath, plan.Value.StagingPath, strip, cancellationToken)
                .ConfigureAwait(false);

            if (extract.IsFailure)
            {
                _ = ShadowInstaller.Discard(plan.Value);
                return FailResult(extract.Error.Code, extract.Error.Message, extract.Error.Remediation);
            }

            var commit = await installer.CommitAsync(plan.Value, cancellationToken).ConfigureAwait(false);
            if (commit.IsFailure)
            {
                return FailResult(commit.Error.Code, commit.Error.Message, commit.Error.Remediation);
            }
        }
        else
        {
            return FailResult(
                EnvStationErrorCodes.ActionArgumentInvalid,
                "必须提供 source_path 或 source 之一。",
                "可用 net.download + archive.extract + local.install 三步组合，或提供离线归档路径。");
        }

        // ③ 登记（来源 managed —— 允许环境站后续删除）。
        var store = new RuntimeManifestStore(LocalRegisterAction.ManifestPath(context));
        var registration = new RuntimeRegistration(kind, version, dest, "managed", DateTimeOffset.Now, []);
        var upsert = store.Upsert(registration);
        if (upsert.IsFailure)
        {
            return FailResult(upsert.Error.Code, upsert.Error.Message, upsert.Error.Remediation);
        }

        return OkResult(
            $"已安装并登记 {definition.DisplayName} {version} → {dest}。",
            Outputs(
                ("kind", kind),
                ("version", version),
                ("dest", dest),
                ("source", "managed")),
            touched: [dest]);
    }

    /// <summary>把归档解压到影子目录，复用与 archive.extract 相同的安全检查。</summary>
    private static async Task<Result<Unit>> ExtractIntoAsync(
        ActionExecutionContext context,
        string archivePath,
        string destination,
        int stripComponents,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var format = ArchiveInspector.DetectFormat(archivePath);
        if (format is null)
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"无法识别归档格式：{Path.GetFileName(archivePath)}",
                "支持 .zip / .jar / .tar / .tar.gz / .tgz。");
        }

        var listed = ArchiveInspector.List(archivePath, format.Value);
        if (listed.IsFailure)
        {
            return Result<Unit>.Fail(listed.Error);
        }

        long declaredTotal = 0;
        foreach (var entry in listed.Value)
        {
            if (entry.IsLink)
            {
                return Result<Unit>.Fail(
                    EnvStationErrorCodes.ArchivePathTraversal,
                    $"归档中含链接条目：{entry.Key}",
                    "环境站一律拒绝含链接的归档。");
            }

            if (entry.IsDirectory)
            {
                continue;
            }

            var key = StripComponents(entry.Key, stripComponents);
            if (key.Length == 0)
            {
                continue;
            }

            // 与解压动作一致：先判形状，再规范化。
            var safe = ArchiveInspector.ResolveSafeTarget(destination, key);
            if (safe.IsFailure)
            {
                return Result<Unit>.Fail(safe.Error);
            }

            var rawSafe = ArchiveInspector.ResolveSafeTarget(destination, entry.Key);
            if (rawSafe.IsFailure)
            {
                return Result<Unit>.Fail(rawSafe.Error);
            }

            declaredTotal += Math.Max(0, entry.Size);
        }

        var quota = context.Quota.TryConsume(QuotaKind.Extract, declaredTotal);
        if (quota.IsFailure)
        {
            return Result<Unit>.Fail(quota.Error);
        }

        try
        {
            Directory.CreateDirectory(destination);

            if (format.Value == ArchiveFormat.Zip)
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.Name.Length == 0)
                    {
                        continue;
                    }

                    var key = StripComponents(entry.FullName, stripComponents);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    var safe = ArchiveInspector.ResolveSafeTarget(destination, key);
                    if (safe.IsFailure)
                    {
                        return Result<Unit>.Fail(safe.Error);
                    }

                    var dir = Path.GetDirectoryName(safe.Value);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using (var source = entry.Open())
                    using (var target = File.Create(safe.Value))
                    {
                        source.CopyTo(target);
                    }
                }
            }
            else
            {
                using var file = File.OpenRead(archivePath);
                Stream source = format.Value == ArchiveFormat.TarGz
                    ? new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress)
                    : file;

                try
                {
                    using var reader = new System.Formats.Tar.TarReader(source, leaveOpen: true);
                    while (reader.GetNextEntry() is { } tarEntry)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (tarEntry.EntryType != System.Formats.Tar.TarEntryType.RegularFile)
                        {
                            continue;
                        }

                        var key = StripComponents(tarEntry.Name, stripComponents);
                        if (key.Length == 0)
                        {
                            continue;
                        }

                        var safe = ArchiveInspector.ResolveSafeTarget(destination, key);
                        if (safe.IsFailure)
                        {
                            return Result<Unit>.Fail(safe.Error);
                        }

                        var dir = Path.GetDirectoryName(safe.Value);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        using var target = File.Create(safe.Value);
                        tarEntry.DataStream?.CopyTo(target);
                    }
                }
                finally
                {
                    if (format.Value == ArchiveFormat.TarGz)
                    {
                        source.Dispose();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.ActionFailed,
                $"解压失败：{ex.Message}");
        }

        return Results.Ok();
    }

    private static string StripComponents(string key, int strip)
    {
        if (strip <= 0)
        {
            return key.Replace('\\', '/').TrimStart('/');
        }

        var parts = key.Replace('\\', '/').TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= strip ? string.Empty : string.Join('/', parts.Skip(strip));
    }
}

/// <summary><c>envstation.runtime.list</c>：列出已登记版本。</summary>
internal sealed class RuntimeListAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.runtime.list",
        "1.0.0",
        CapabilityIds.Inspect,
        "列出已登记运行时",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["filesystem.read"],
        Parameters:
        [
            new ParameterSpec("kind", ParameterType.Enum, false, "只列出某个种类；省略则全部",
                AllowedValues: [.. RuntimeCatalog.Kinds, "all"]),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var kind = arguments.GetString("kind") ?? "all";
        var store = new RuntimeManifestStore(LocalRegisterAction.ManifestPath(context));

        var loaded = store.Load();
        if (loaded.IsFailure)
        {
            return Fail(loaded.Error.Code, loaded.Error.Message, loaded.Error.Remediation);
        }

        var items = loaded.Value
            .Where(r => kind == "all" || string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var lines = items.Select(r =>
            $"[{r.Kind}] {r.Version} → {r.HomePath}（{(r.IsManaged ? "环境站托管" : "用户已有")}，{(Directory.Exists(r.HomePath) ? "存在" : "路径已失效")}）");

        return Ok(
            items.Length == 0
                ? "运行时清单为空。可用 local.register 登记已有运行时，或用 runtime.install 安装新版本。"
                : $"共 {items.Length} 条登记：{string.Join("；", lines)}",
            Outputs(
                ("count", items.Length.ToString(CultureInfo.InvariantCulture)),
                ("details", string.Join(" | ", lines)),
                ("missing_count", items.Count(static r => !Directory.Exists(r.HomePath))
                    .ToString(CultureInfo.InvariantCulture))));
    }
}

/// <summary><c>envstation.runtime.remove</c>：移除某版本（不删用户数据目录）。</summary>
internal sealed class RuntimeRemoveAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.runtime.remove",
        "1.0.0",
        CapabilityIds.RuntimeManage,
        "移除运行时版本",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 300,
        TouchedResources: ["filesystem.write"],
        Parameters:
        [
            new ParameterSpec("kind", ParameterType.Enum, true, "运行时种类", AllowedValues: RuntimeCatalog.Kinds),
            Str("version", true, "要移除的版本", maxLength: 64),
            Bool("dry_run", "只报告将要做什么", false),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var kind = arguments.GetString("kind")!;
        var version = arguments.GetString("version")!;
        var dryRun = arguments.GetBoolean("dry_run", false);

        var store = new RuntimeManifestStore(LocalRegisterAction.ManifestPath(context));
        var matches = store
            .Find(kind)
            .Where(m => string.Equals(m.Registration.Version, version, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            return Ok(
                $"清单中没有 {kind} {version}，无需移除。",
                Outputs(("changed", "false"), ("kind", kind), ("version", version)));
        }

        var registration = matches[0].Registration;

        // ⚠ 最关键的一条：**非托管的运行时绝不删除**。
        // 那是用户自己装的 JDK/Python，环境站只是"登记"了它。
        // 删掉它等于用户手工装的东西被一个配置工具清掉了——这是不可接受的。
        if (!registration.IsManaged)
        {
            var removedFromList = dryRun ? Results.Ok() : store.Remove(kind, version);
            if (removedFromList.IsFailure)
            {
                return Fail(removedFromList.Error.Code, removedFromList.Error.Message, removedFromList.Error.Remediation);
            }

            return Ok(
                $"{registration.HomePath} 是用户已有的安装，环境站只把它从清单中摘除，未删除任何文件。" +
                "如需卸载，使用该运行时自己的卸载程序。",
                Outputs(
                    ("changed", Bool(!dryRun)),
                    ("kind", kind),
                    ("version", version),
                    ("deleted_files", "false"),
                    ("path", registration.HomePath)));
        }

        if (dryRun)
        {
            return Ok(
                $"预演：将删除 {registration.HomePath} 并移除登记（未做任何修改）。" +
                "环境站只删除它自己安装的运行时目录，不触碰项目数据目录。",
                Outputs(("dry_run", "true"), ("path", registration.HomePath)));
        }

        if (Directory.Exists(registration.HomePath))
        {
            if (CleanupTempAction.IsDangerousDirectory(registration.HomePath))
            {
                return Fail(
                    EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                    $"拒绝删除 {registration.HomePath}：该路径是磁盘根或系统关键目录。");
            }

            try
            {
                Directory.Delete(registration.HomePath, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(
                    EnvStationErrorCodes.PathNotWritable,
                    $"删除 {registration.HomePath} 失败：{ex.Message}",
                    "可能有程序正在使用它。关闭相关程序后重试；清单记录已保留。");
            }
        }

        var removed = store.Remove(kind, version);
        if (removed.IsFailure)
        {
            return Fail(removed.Error.Code, removed.Error.Message, removed.Error.Remediation);
        }

        return Ok(
            $"已移除 {kind} {version}（{registration.HomePath}）。项目数据目录未被触碰。",
            Outputs(
                ("changed", "true"),
                ("kind", kind),
                ("version", version),
                ("path", registration.HomePath)),
            touched: [registration.HomePath]);
    }
}

/// <summary><c>envstation.runtime.switch</c>：切换默认版本（shim 重定向）。</summary>
internal sealed class RuntimeSwitchAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.runtime.switch",
        "1.0.0",
        CapabilityIds.RuntimeManage,
        "切换默认运行时版本",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 120,
        TouchedResources: ["filesystem.write"],
        Parameters:
        [
            new ParameterSpec("kind", ParameterType.Enum, true, "运行时种类", AllowedValues: RuntimeCatalog.Kinds),
            Str("version", true, "要切换到的版本（必须已登记）", maxLength: 64),
            Path_("shim_dir", false, "托管目录；省略时使用 <授权根>/shims"),
            Bool("dry_run", "只报告将要做什么", false),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var kind = arguments.GetString("kind")!;
        var version = arguments.GetString("version")!;
        var dryRun = arguments.GetBoolean("dry_run", false);

        var store = new RuntimeManifestStore(LocalRegisterAction.ManifestPath(context));
        var matches = store
            .Find(kind)
            .Where(m => string.Equals(m.Registration.Version, version, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            return Fail(
                EnvStationErrorCodes.AssertFailed,
                $"清单中没有 {kind} {version}。先安装或登记该版本。",
                "可用 envstation.runtime.list 查看已登记的版本。");
        }

        var (registration, exists) = matches[0];
        if (!exists)
        {
            return Fail(
                EnvStationErrorCodes.AssertFailed,
                $"{registration.HomePath} 已不存在（可能被手工删除）。",
                $"先用 runtime.install 重新安装，或把清单中的这条记录移除。");
        }

        var shimRoot = arguments.GetString("shim_dir")
            ?? Path.Combine(
                context.AuthorizedRoots.Length > 0 ? context.AuthorizedRoots[0] : Path.GetTempPath(),
                "shims");
        var shimPath = Path.Combine(shimRoot, kind);

        if (dryRun)
        {
            return Ok(
                $"预演：将把 {shimPath} 指向 {registration.HomePath}（未做任何修改）。",
                Outputs(("dry_run", "true"), ("shim", shimPath), ("target", registration.HomePath)));
        }

        Directory.CreateDirectory(shimRoot);

        // 切换 = 重建链接。先删旧的（如果存在），再建新的。
        try
        {
            if (Directory.Exists(shimPath))
            {
                Directory.Delete(shimPath, recursive: false);
            }
            else if (File.Exists(shimPath))
            {
                File.Delete(shimPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"无法清理旧的链接 {shimPath}：{ex.Message}");
        }

        var linkArguments = new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
        {
            ["target"] = new AbsPkg.ScriptString(registration.HomePath),
            ["link"] = new AbsPkg.ScriptString(shimPath),
            ["mode"] = new AbsPkg.ScriptString("junction"),
        };

        var findings = new Abstractions.Diagnostics.FindingBag();
        var registry = ActionRegistry.CreateDefault(findings);
        if (registry.IsFailure || !registry.Value.TryGet("envstation.fs.link", out var linkAction))
        {
            return Fail(EnvStationErrorCodes.ActionNotFound, "内部错误：找不到 fs.link 动作。");
        }

        var bound = ActionArgumentsBinder.Bind(linkAction.Descriptor, linkArguments, findings);
        if (bound.IsFailure)
        {
            return Fail(EnvStationErrorCodes.ActionArgumentInvalid, findings.ToReport().ToText());
        }

        var linkResult = ActionExecutor
            .ExecuteAsync(linkAction, context, bound.Value, timeoutSeconds: 120, cancellationToken)
            .AsTask().GetAwaiter().GetResult();

        if (!linkResult.Success)
        {
            return Fail(linkResult.ErrorCode, linkResult.Message);
        }

        return Ok(
            $"已把 {kind} 的默认版本切换为 {version}（{shimPath} → {registration.HomePath}）。" +
            "确认托管目录已在 PATH 中且位置靠前（envstation.path.ensure_shim）。",
            Outputs(
                ("kind", kind),
                ("version", version),
                ("shim", shimPath),
                ("target", registration.HomePath),
                ("mode", linkResult.Outputs.TryGetValue("mode", out var mode) ? mode : "junction")),
            touched: [shimPath]);
    }
}

/// <summary><c>envstation.runtime.components</c>：补装/移除组件。</summary>
internal sealed class RuntimeComponentsAction : InstallActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.runtime.components",
        "1.0.0",
        CapabilityIds.RuntimeManage,
        "管理运行时组件",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 600,
        TouchedResources: ["filesystem.write"],
        Parameters:
        [
            new ParameterSpec("kind", ParameterType.Enum, true, "运行时种类", AllowedValues: RuntimeCatalog.Kinds),
            Str("version", true, "目标版本", maxLength: 64),
            StrArray("components", true, "要启用或禁用的组件标识"),
            new ParameterSpec("action", ParameterType.Enum, false,
                "add = 补装（登记为已启用）；remove = 移除（仅从登记中摘除）",
                AllowedValues: ["add", "remove"]),
            Bool("dry_run", "只报告将要做什么", false),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var kind = arguments.GetString("kind")!;
        var version = arguments.GetString("version")!;
        var components = arguments.GetStringArray("components");
        var action = arguments.GetString("action") ?? "add";
        var dryRun = arguments.GetBoolean("dry_run", false);

        var unknown = components.Where(c => RuntimeComponentCatalog.Find(c) is null).ToArray();
        if (unknown.Length > 0)
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"未知的组件：{string.Join("、", unknown)}",
                $"可用组件：{string.Join("、", RuntimeComponentCatalog.Ids)}。");
        }

        var store = new RuntimeManifestStore(LocalRegisterAction.ManifestPath(context));
        var matches = store
            .Find(kind)
            .Where(m => string.Equals(m.Registration.Version, version, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            return Fail(
                EnvStationErrorCodes.AssertFailed,
                $"清单中没有 {kind} {version}。");
        }

        var registration = matches[0].Registration;
        var current = registration.Components.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = new List<string>(components.Length);
        foreach (var component in components)
        {
            var didChange = action == "add" ? current.Add(component) : current.Remove(component);
            if (didChange)
            {
                changed.Add(component);
            }
        }

        if (changed.Count == 0)
        {
            return Ok(
                $"{kind} {version} 的组件状态已是目标状态，无需修改。",
                Outputs(("changed", "false"), ("kind", kind), ("version", version)));
        }

        var bytes = changed
            .Select(c => RuntimeComponentCatalog.Find(c)?.ApproxBytes ?? 0)
            .Sum();

        if (dryRun)
        {
            return Ok(
                $"预演：将{(action == "add" ? "补装" : "移除")} {changed.Count} 个组件（约 {bytes / 1024.0 / 1024.0:F0} MB），未做任何修改。",
                Outputs(("dry_run", "true"), ("components", string.Join(",", changed))));
        }

        var updated = new RuntimeRegistration(
            registration.Kind,
            registration.Version,
            registration.HomePath,
            registration.Source,
            registration.RegisteredAt,
            [.. current.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase)]);

        var upsert = store.Upsert(updated);
        if (upsert.IsFailure)
        {
            return Fail(upsert.Error.Code, upsert.Error.Message, upsert.Error.Remediation);
        }

        return Ok(
            $"已更新 {kind} {version} 的组件登记：{(action == "add" ? "补装" : "移除")} {string.Join("、", changed)}。",
            Outputs(
                ("changed", "true"),
                ("kind", kind),
                ("version", version),
                ("components", string.Join(",", updated.Components)),
                ("approx_bytes", bytes.ToString(CultureInfo.InvariantCulture))));
    }
}
