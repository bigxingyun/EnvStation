using System.Collections.Immutable;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Environment;

namespace EnvStation.Core.Actions.Builtin;

/// <summary>一个被探测到的运行时安装。</summary>
/// <param name="Kind">运行时种类。</param>
/// <param name="Version">解析出的版本；无法从目录名解析时为 null。</param>
/// <param name="Path">主目录（<c>JAVA_HOME</c> 语义）或可执行文件所在目录。</param>
/// <param name="Source">证据来源，如 <c>环境变量 JAVA_HOME</c>、<c>常见安装位置</c>、<c>PATH</c>。</param>
/// <param name="Confidence">置信度：高=主目录明确；中=仅从 PATH 命中。</param>
internal sealed record RuntimeCandidate(
    string Kind,
    SemanticVersion? Version,
    string Path,
    string Source,
    string Confidence)
{
    internal string Describe() =>
        $"{Path}（{(Version is null ? "版本未知" : Version.ToString())}，来源：{Source}）";
}

/// <summary>
/// 运行时探测器。
/// </summary>
/// <remarks>
/// <para>
/// 探测顺序刻意是"越具体的证据越优先"：
/// <list type="number">
///   <item>包显式给出的搜索目录（用户/包作者最清楚东西装在哪）；</item>
///   <item>主目录环境变量（<c>JAVA_HOME</c> 等）——这是最可靠的"用户意图"信号；</item>
///   <item>常见安装位置下有版本号的目录（按版本从高到低）；</item>
///   <item>PATH 中的命中（只能证明"能运行"，不能证明安装位置）。</item>
/// </list>
/// </para>
/// <para>
/// <b>不运行任何程序</b>：版本来自目录名或主目录名中的版本号。这条约束让探测保持只读能力
/// （<c>CAP.INSPECT</c>），而真正"跑一下看看"由 <c>envstation.verify.version_output</c> 承担。
/// </para>
/// </remarks>
internal static class RuntimeDetector
{
    /// <summary>枚举某运行时的全部安装候选，按证据强度排序。</summary>
    internal static ImmutableArray<RuntimeCandidate> Enumerate(
        RuntimeDefinition definition,
        ImmutableArray<string> extraPaths,
        CancellationToken cancellationToken)
    {
        var results = new List<RuntimeCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string home, string source, string confidence)
        {
            var normalized = NormalizeDirectory(home);
            if (normalized is null || !seen.Add(normalized))
            {
                return;
            }

            results.Add(new RuntimeCandidate(
                definition.Kind,
                ResolveVersion(definition, normalized),
                normalized,
                source,
                confidence));
        }

        // ① 包显式指定的搜索目录
        foreach (var extra in extraPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryMatchHome(definition, extra) is { } home)
            {
                Add(home, "包指定目录", "高");
            }
        }

        // ② 主目录环境变量
        foreach (var variable in definition.HomeEnvironmentVariables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = System.Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (TryMatchHome(definition, value) is { } home)
            {
                Add(home, $"环境变量 {variable}", "高");
            }
        }

        // ③ 常见安装位置（按版本从高到低，让"最新版"排在前面）
        var fromWellKnown = new List<RuntimeCandidate>();
        foreach (var pattern in definition.WellKnownDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var directory in ExpandPattern(pattern))
            {
                if (TryMatchHome(definition, directory) is { } home)
                {
                    var normalized = NormalizeDirectory(home);
                    if (normalized is null || seen.Contains(normalized))
                    {
                        continue;
                    }

                    fromWellKnown.Add(new RuntimeCandidate(
                        definition.Kind,
                        ResolveVersion(definition, normalized),
                        normalized,
                        "常见安装位置",
                        "中"));
                }
            }
        }

        foreach (var candidate in fromWellKnown
            .OrderByDescending(static c => c.Version ?? default)
            .ThenBy(static c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            Add(candidate.Path, candidate.Source, candidate.Confidence);
        }

        // ④ PATH 中的命中
        foreach (var entry in ReadPathEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContainsExecutable(definition, entry))
            {
                Add(entry, "PATH", "低");
            }
        }

        return [.. results];
    }

    /// <summary>
    /// 判断一个目录是否是该运行时的"主目录"。主目录的两种形态：
    /// 可执行文件直接在目录下（如 <c>C:\Go\bin</c> 的上一级），或在 <c>bin</c> 子目录下（如 <c>JAVA_HOME\bin\java.exe</c>）。
    /// 返回值是**主目录**（而非 bin 目录），以便直接用于写 <c>JAVA_HOME</c>。
    /// </summary>
    private static string? TryMatchHome(RuntimeDefinition definition, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string expanded;
        try
        {
            expanded = System.Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (expanded.Contains('%', StringComparison.Ordinal) || !Directory.Exists(expanded))
        {
            return null;
        }

        if (ContainsExecutable(definition, expanded))
        {
            return expanded;
        }

        var bin = Path.Combine(expanded, "bin");
        if (Directory.Exists(bin) && ContainsExecutable(definition, bin))
        {
            return expanded;
        }

        return null;
    }

    private static bool ContainsExecutable(RuntimeDefinition definition, string directory)
    {
        foreach (var executable in definition.Executables)
        {
            try
            {
                if (File.Exists(Path.Combine(directory, executable)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }
        }

        return false;
    }

    /// <summary>解析版本：先看目录名，再看上一级目录名（如 <c>...\Python\3.12.1\</c>）。</summary>
    private static SemanticVersion? ResolveVersion(RuntimeDefinition definition, string home)
    {
        var name = Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var fromSelf = RuntimeCatalog.ExtractVersionFromDirectoryName(definition, name);
        if (fromSelf is not null && SemanticVersion.TryParse(fromSelf, out var selfVersion))
        {
            return selfVersion;
        }

        // bin 目录形态：<home>\bin 的 home 名不含版本，但再上一级可能含（…\nodejs\node-v20.11.0\bin）。
        if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
            {
                var parentName = Path.GetFileName(parent);
                var fromParent = RuntimeCatalog.ExtractVersionFromDirectoryName(definition, parentName);
                if (fromParent is not null && SemanticVersion.TryParse(fromParent, out var parentVersion))
                {
                    return parentVersion;
                }
            }
        }

        return null;
    }

    /// <summary>展开 <c>%VAR%</c> 与末尾的 <c>*</c> 通配（只支持"前缀 + 星号"这一种形式）。</summary>
    private static IEnumerable<string> ExpandPattern(string pattern)
    {
        string expanded;
        try
        {
            expanded = System.Environment.ExpandEnvironmentVariables(pattern);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        if (expanded.Contains('%', StringComparison.Ordinal))
        {
            // 环境变量未定义（如 32 位系统上的 %ProgramFiles(x86)%）——直接跳过，不报错。
            yield break;
        }

        if (!expanded.Contains('*', StringComparison.Ordinal))
        {
            yield return expanded;
            yield break;
        }

        if (!expanded.EndsWith('*'))
        {
            // 本项目只支持"末尾通配"，中间通配会让匹配规则难以解释；不支持就跳过而不是猜。
            yield break;
        }

        var prefix = expanded[..^1];
        var parent = Path.GetDirectoryName(prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var leafPrefix = Path.GetFileName(prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            yield break;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(parent);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (Path.GetFileName(child).StartsWith(leafPrefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<string> ReadPathEntries()
    {
        var raw = EnvironmentPathResolver.Read("merged");
        if (string.IsNullOrEmpty(raw))
        {
            yield break;
        }

        foreach (var entry in PathParser.Parse(raw, probeFileSystem: true))
        {
            if (entry.Issues == PathEntryIssue.None && Directory.Exists(entry.Normalized))
            {
                yield return entry.Normalized;
            }
        }
    }

    private static string? NormalizeDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: 2 } trimmed
                   && trimmed[1] == ':'
                ? trimmed + Path.DirectorySeparatorChar
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
