using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using EnvStation.Abstractions;

namespace EnvStation.Core.Actions;

/// <summary>一次受控进程运行的结果。</summary>
/// <param name="Started">进程是否成功启动。</param>
/// <param name="ExitCode">退出码；未启动时为 -1。</param>
/// <param name="StandardOutput">标准输出（已按上限截断）。</param>
/// <param name="StandardError">标准错误（已按上限截断）。</param>
/// <param name="FailureReason">未启动或异常终止的原因。</param>
/// <param name="Elapsed">耗时。</param>
public sealed record ProcessRunResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? FailureReason,
    TimeSpan Elapsed)
{
    /// <summary>构造一个"未能启动"的结果。</summary>
    public static ProcessRunResult NotStarted(string reason, TimeSpan elapsed) =>
        new(false, -1, string.Empty, string.Empty, reason, elapsed);
}

/// <summary>
/// 受控进程执行器。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有这一层，而不是让动作直接 <c>Process.Start</c></b>：
/// 需求 AI-1 规定"所有执行外部程序的能力集中且参数白名单化"。
/// 只要有一个动作自己拼命令行，命令注入（TH-1）就回来了。
/// 因此本项目把"启动进程"这件事收敛成一个接口，并且：
/// </para>
/// <list type="bullet">
///   <item>参数以<b>数组</b>传递（<c>ArgumentList</c>），永不拼接成命令行字符串——
///         这从机制上消除了"用户数据被当成命令的一部分"的可能（S6）；</item>
///   <item><c>UseShellExecute = false</c>，因此不会经由 cmd.exe 解释，也没有 shell 元字符展开；</item>
///   <item>输出长度有硬上限，避免用海量输出耗尽内存；</item>
///   <item>实现是可替换的，测试可以注入假实现而完全不启动真实进程。</item>
/// </list>
/// </remarks>
public interface IProcessRunner
{
    /// <summary>运行一个可执行文件。调用方必须已完成白名单校验。</summary>
    ValueTask<ProcessRunResult> RunAsync(
        string executablePath,
        ImmutableArray<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// 默认的受控进程执行器：直接创建进程、重定向输出、按上限截断。
/// </summary>
public sealed class ControlledProcessRunner : IProcessRunner
{
    /// <summary>单次运行捕获的输出上限（字符）。</summary>
    public const int MaxCapturedCharacters = 64 * 1024;

    /// <summary>单例。</summary>
    public static ControlledProcessRunner Instance { get; } = new();

    /// <inheritdoc />
    public async ValueTask<ProcessRunResult> RunAsync(
        string executablePath,
        ImmutableArray<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Path.GetTempPath(),
        };

        // 关键：逐一加入 ArgumentList，而不是拼 Arguments 字符串。
        foreach (var argument in arguments.OrEmpty())
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                clock.Stop();
                return ProcessRunResult.NotStarted("进程未能启动。", clock.Elapsed);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            clock.Stop();
            return ProcessRunResult.NotStarted($"无法启动进程 {executablePath}：{ex.Message}", clock.Elapsed);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, linked.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            clock.Stop();
            var reason = cancellationToken.IsCancellationRequested
                ? "进程被取消。"
                : $"进程未在 {timeout.TotalSeconds:F0} 秒内结束，已强制终止。";
            return ProcessRunResult.NotStarted(reason, clock.Elapsed);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        clock.Stop();

        return new ProcessRunResult(true, process.ExitCode, stdout, stderr, null, clock.Elapsed);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();

        while (builder.Length < MaxCapturedCharacters)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
        }

        return builder.Length > MaxCapturedCharacters
            ? builder.ToString(0, MaxCapturedCharacters)
            : builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程已退出或不允许终止——两种情况都无需处理。
        }
    }
}

/// <summary>
/// 可执行文件白名单校验器。
/// </summary>
/// <remarks>
/// <para>
/// 需求 AI-1 的另一半：<b>只有已登记运行时的可执行文件才允许被运行</b>。
/// 判定分三步，缺一不可：
/// </para>
/// <list type="number">
///   <item><b>必须是绝对路径</b>——相对路径的含义取决于当前工作目录，等于把"运行什么"交给环境决定；</item>
///   <item><b>文件名必须在运行时目录的白名单内</b>（如 <c>python.exe</c> / <c>java.exe</c>），
///         包不能运行 <c>curl.exe</c>、<c>powershell.exe</c> 这类通用工具；</item>
///   <item><b>所在目录必须可信</b>——要么在本次运行的授权根目录内，要么在 PATH 中，
///         要么等于某个已登记运行时的主目录。</item>
/// </list>
/// <para>
/// 第三点尤其重要：它挡住了"把恶意 exe 放进包私有目录再让工作流运行它"这条路径。
/// </para>
/// </remarks>
public static class ExecutableAllowList
{
    /// <summary>白名单校验的结果。</summary>
    /// <param name="Allowed">是否允许。</param>
    /// <param name="ResolvedPath">解析后的绝对路径（允许时有效）。</param>
    /// <param name="Reason">拒绝原因（拒绝时有效）。</param>
    public readonly record struct Verdict(bool Allowed, string ResolvedPath, string Reason);

    /// <summary>判断一个可执行文件是否允许被运行。</summary>
    /// <param name="command">包给出的命令（路径或命令名）。</param>
    /// <param name="trustedDirectories">可信目录集合（授权根目录 + PATH 目录）。</param>
    public static Verdict Check(string command, IEnumerable<string> trustedDirectories)
    {
        ArgumentNullException.ThrowIfNull(trustedDirectories);

        if (string.IsNullOrWhiteSpace(command))
        {
            return new Verdict(false, string.Empty, "命令为空。");
        }

        // 命令名中不允许出现 shell 元字符。即便我们用 ArgumentList 传递参数，
        // 文件名本身若含 & | > ^ 等字符，在某些场景下仍可能被误解释；
        // 更重要的是：合法的运行时可执行文件名不可能含这些字符。
        const string forbidden = "&|<>^\"'\r\n\t";
        foreach (var ch in forbidden)
        {
            if (command.Contains(ch, StringComparison.Ordinal))
            {
                return new Verdict(false, string.Empty,
                    $"命令中含不允许的字符「{ch}」，可执行文件名不会包含这些字符。");
            }
        }

        string candidate;
        if (Path.IsPathFullyQualified(command))
        {
            candidate = command;
        }
        else
        {
            // 只给出命令名（如 python）时，在可信目录中解析。
            var found = ResolveOnPath(command, trustedDirectories);
            if (found is null)
            {
                return new Verdict(false, string.Empty,
                    $"在可信目录中没有找到命令 {command}。确认该运行时已安装并已加入 PATH。");
            }

            candidate = found;
        }

        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new Verdict(false, string.Empty, $"命令路径无法解析：{ex.Message}");
        }

        if (!File.Exists(full))
        {
            return new Verdict(false, string.Empty, $"文件不存在：{full}");
        }

        var fileName = Path.GetFileName(full);

        // ② 文件名白名单
        if (!IsKnownRuntimeExecutable(fileName))
        {
            return new Verdict(false, string.Empty,
                $"可执行文件 {fileName} 不在白名单内。允许运行的是已登记运行时的可执行文件" +
                "（python.exe、java.exe、dotnet.exe 等）。");
        }

        // ③ 目录可信性
        var directory = Path.GetDirectoryName(full) ?? string.Empty;
        if (!IsTrusted(directory, trustedDirectories))
        {
            return new Verdict(false, string.Empty,
                $"目录 {directory} 不在可信目录内。可信目录包括：已安装运行时的目录、PATH 中的目录、本次运行被授权的目录。");
        }

        return new Verdict(true, full, string.Empty);
    }

    private static string? ResolveOnPath(string command, IEnumerable<string> trustedDirectories)
    {
        var names = Path.HasExtension(command)
            ? new[] { command }
            : new[] { command + ".exe", command + ".cmd", command + ".bat" };

        foreach (var directory in trustedDirectories)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
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

    private static bool IsTrusted(string directory, IEnumerable<string> trustedDirectories)
    {
        var normalized = NormalizeDirectory(directory);

        foreach (var trusted in trustedDirectories)
        {
            if (string.IsNullOrWhiteSpace(trusted))
            {
                continue;
            }

            var trustedNormalized = NormalizeDirectory(trusted);
            if (string.Equals(normalized, trustedNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 允许"运行时主目录的子目录"（如 <JAVA_HOME>\bin），但不允许前缀相似的兄弟目录。
            if (normalized.Length > trustedNormalized.Length
                && normalized.StartsWith(trustedNormalized, StringComparison.OrdinalIgnoreCase)
                && (trustedNormalized.EndsWith(Path.DirectorySeparatorChar)
                    || normalized[trustedNormalized.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

    /// <summary>
    /// 判断文件名是否为已知运行时的可执行文件。
    /// </summary>
    /// <remarks>
    /// 名单刻意<b>只包含"用来验证安装结果"所必需的那些命令</b>，并把版本探测命令一并纳入。
    /// 明确排除：<c>cmd.exe</c> / <c>powershell.exe</c> / <c>curl.exe</c> / <c>wget.exe</c> /
    /// <c>msiexec.exe</c> / <c>reg.exe</c> / <c>schtasks.exe</c> 等——包不能运行它们（需求 AI-7）。
    /// </remarks>
    public static bool IsKnownRuntimeExecutable(string fileName) =>
        KnownExecutables.Contains(fileName);

    /// <summary>允许被运行的可执行文件名白名单。</summary>
    public static ImmutableHashSet<string> KnownExecutables { get; } =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "java.exe", "javac.exe", "jar.exe",
            "python.exe", "python3.exe", "pip.exe",
            "node.exe", "npm.cmd", "npx.cmd", "corepack.cmd",
            "go.exe", "gofmt.exe",
            "dotnet.exe",
            "mvn.cmd", "mvn.bat",
            "gradle.bat",
            "gcc.exe", "g++.exe", "clang.exe", "clang++.exe",
            "cmake.exe",
            "git.exe",
            "mysql.exe",
            "cargo.exe", "rustc.exe",
            "php.exe", "ruby.exe");

    /// <summary>把命令渲染为可入审计日志的文本（不含任何用户数据拼接风险）。</summary>
    public static string DescribeInvocation(string executablePath, ImmutableArray<string> arguments)
    {
        var sb = new StringBuilder();
        sb.Append(executablePath);
        foreach (var argument in arguments.OrEmpty())
        {
            sb.Append(' ').Append(argument);
        }

        var text = sb.ToString();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    /// <summary>把版本探测的候选参数集合列出（供文档与错误信息使用）。</summary>
    public static ImmutableArray<ImmutableArray<string>> VersionProbeArguments { get; } =
    [
        ["--version"],
        ["-version"],
        ["-V"],
        ["version"],
        ["-v"],
    ];
}
