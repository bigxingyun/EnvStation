using System.Collections.Immutable;
using EnvStation.Core.Actions.Builtin;
using EnvStation.Core.Environment;
using EnvStation.Core.Installation;

namespace EnvStation.Core.Diagnostics;

/// <summary>
/// <see cref="IPathProbe"/> 的真实系统实现：<b>只读</b>本机的文件系统、注册表 PATH 与运行时清单。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么规则引擎的事实来源必须有个生产实现</b>：引擎本身只认 <see cref="IPathProbe"/>，
/// 在 M2-5 的第一段里只有测试用的假实现。假实现能证明"判定逻辑对"，但证明不了
/// "界面拿到的结论对"——两者之间隔着的正是这个类。缺了它，路径校验在真机上根本跑不起来。
/// </para>
/// <para>
/// <b>本类不做任何写入</b>。V6 要求的"真实写入-删除测试"是另一件事（<see cref="TargetWriteProbe"/>），
/// 因为它有副作用，必须单独出现在前置检查里、单独进审计，不能藏在"读事实"的探针里。
/// </para>
/// <para>
/// <b>清单与 PATH 只读一次并缓存</b>：V1 要求键入即校验（防抖 200ms），
/// 每敲一个字符都去读注册表与清单文件是不必要的开销。事实在"一次安装流程"内足够稳定；
/// 流程结束后由调用方新建一个探针即可（<c>Core</c> 不持有长生命周期状态）。
/// </para>
/// </remarks>
public sealed class SystemPathProbe : IPathProbe
{
    private readonly Lazy<ImmutableArray<RuntimeRegistration>> _registrations;
    private readonly Lazy<ImmutableArray<string>> _pathEntries;

    /// <summary>用默认清单位置（<c>%LOCALAPPDATA%\EnvStation\runtimes.json</c>）构造。</summary>
    public SystemPathProbe()
        : this(RuntimeManifestStore.CreateDefault())
    {
    }

    /// <summary>用指定清单存储构造。</summary>
    /// <remarks>
    /// 之所以允许换掉：R13（与已登记组件重叠）与 R15（目录是否由环境站创建）的判定
    /// 完全取决于这份清单。测试要造出"某个目录已经被 Python 3.12 占用"这种局面，
    /// 只能给一份自己写的清单——而它必须与生产走**同一段**判定代码。
    /// </remarks>
    public SystemPathProbe(RuntimeManifestStore manifestStore)
    {
        ArgumentNullException.ThrowIfNull(manifestStore);

        _registrations = new Lazy<ImmutableArray<RuntimeRegistration>>(() =>
        {
            var loaded = manifestStore.Load();
            return loaded is { IsSuccess: true } ? loaded.Value : [];
        });

        _pathEntries = new Lazy<ImmutableArray<string>>(() =>
        {
            // 合并顺序（系统级在前、用户级在后）由 EnvironmentPathResolver 统一负责；
            // 这里绝不自己拼 PATH——那段语义已经因为"二选一 vs 拼接"出过一次真实缺陷。
            var raw = EnvironmentPathResolver.Read("merged");
            return [.. PathParser.Parse(raw, probeFileSystem: false).Select(static e => e.Normalized)];
        });
    }

    /// <inheritdoc />
    public bool ContainsVariableReference(string path) => path.Contains('%');

    /// <inheritdoc />
    public bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    /// <inheritdoc />
    public string DriveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetPathRoot(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            // 含非法字符的路径取不到根——交给 R7 去报，不在这里抛。
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public string DriveType(string path)
    {
        if (IsNetworkPath(path))
        {
            return "network";
        }

        var root = DriveRoot(path);
        if (root.Length == 0)
        {
            return "unknown";
        }

        try
        {
            return new DriveInfo(root).DriveType switch
            {
                System.IO.DriveType.Fixed => "fixed",
                System.IO.DriveType.Removable => "removable",
                System.IO.DriveType.Network => "network",
                System.IO.DriveType.CDRom => "cdrom",
                System.IO.DriveType.Ram => "ram",
                _ => "unknown",
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // 盘符不存在（`Z:\...`）或读不到设备信息：一律"未知"。
            // 报"未知"而不是抛异常，是因为用户在输入框里敲半个路径是常态。
            return "unknown";
        }
    }

    /// <inheritdoc />
    public string FileSystem(string path)
    {
        if (IsNetworkPath(path))
        {
            return string.Empty;
        }

        var root = DriveRoot(path);
        if (root.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return new DriveInfo(root).DriveFormat;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public long? FreeSpaceBytes(string path)
    {
        if (IsNetworkPath(path))
        {
            return null;
        }

        var root = DriveRoot(path);
        if (root.Length == 0)
        {
            return null;
        }

        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path) => path.Length > 0 && Directory.Exists(path);

    /// <inheritdoc />
    public int? EnumerateEntryCount(string path)
    {
        if (!DirectoryExists(path))
        {
            return 0;
        }

        try
        {
            // 只数顶层：R15 问的是"会不会覆盖别人的东西"，递归计数既慢又与问题无关。
            return Directory.EnumerateFileSystemEntries(path).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 返回 null 而不是 0："读不进去"与"空目录"是两件不同的事。
            // 谎报 0 会让 R15 在最需要它的场合（别人的加密目录、权限受限目录）保持沉默。
            return null;
        }
    }

    /// <summary>
    /// 该目录是否完全由环境站创建（R15 用它区分"我建的"与"别人的"）。
    /// </summary>
    /// <remarks>
    /// 三种情况算"是我们的"：
    /// <list type="number">
    /// <item>就是某个托管安装的主目录；</item>
    /// <item>在某个托管安装的主目录之内（如 <c>...\python\3.12.4\Lib</c>）；</item>
    /// <item>是若干托管安装主目录的上级，且<b>它的顶层条目全部落在托管目录里</b>——<br/>
    /// 典型例子是 <c>D:\EnvStation</c> 下面只有我们建的 <c>python</c> / <c>jdk</c>。
    /// 这一条不能省：用户装第二个版本时目标常填这个上级目录，若判成"别人的目录"，
    /// 界面会警告"可能覆盖其中部分文件"，而里面其实全是我们自己的东西。</item>
    /// </list>
    /// <para>
    /// <b>只看 <c>managed</c> 登记</b>：<c>registered</c>（如 winget 装的）目录里的文件不是我们写的，
    /// 删掉或覆盖的后果与别人的目录没有区别。
    /// </para>
    /// </remarks>
    public bool IsOwnedByEnvStation(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        var managed = _registrations.Value
            .Where(static r => r.IsManaged && r.HomePath.Length > 0)
            .Select(static r => Normalize(r.HomePath))
            .ToImmutableArray();

        if (managed.IsEmpty)
        {
            return false;
        }

        var candidate = Normalize(path);

        if (managed.Any(h => IsSameOrUnder(candidate, h)))
        {
            return true;
        }

        // 上级目录：仅当它的每一个顶层条目都落在某个托管目录里才算我们的。
        var ancestors = managed.Where(h => IsSameOrUnder(h, candidate)).ToArray();
        if (ancestors.Length == 0 || !Directory.Exists(path))
        {
            return false;
        }

        string[] entries;
        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(path).Select(Normalize)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return entries.Length > 0
            && entries.All(e => ancestors.Any(h => IsSameOrUnder(e, h)));
    }

    /// <inheritdoc />
    public bool IsReparsePoint(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 路径还不存在（用户正在输入）时不是链接。R11 只对已存在的链接有意义。
            return false;
        }
    }

    /// <inheritdoc />
    public string? ReparseTarget(string path)
    {
        if (!IsReparsePoint(path))
        {
            return null;
        }

        try
        {
            var info = new DirectoryInfo(path);

            // 先按目录解，失败再按文件解：联接点几乎都是目录，但符号链接也可能是文件。
            // returnFinalTarget: true —— 多重链接时给出最终位置，否则用户会看到
            // "实际位置为 链接"这种等于没说的结论（R11 的文案要求写出实际位置）。
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            return resolved?.FullName ?? info.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            try
            {
                var file = new FileInfo(path);
                return file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.LinkTarget;
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 是链接但解不出目标：R11 会退回到"是符号链接或联接点"的措辞（引擎已经做了这个分支）。
                return null;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> RegisteredRuntimeDirectories =>
        _registrations.Value
            .GroupBy(static r => r.Kind, StringComparer.OrdinalIgnoreCase)
            // 同一种类装了多个版本时保留版本号最大的那个：R13 的消息里要写出"被谁占用"，
            // 说"Python"没有说"Python 3.12.4"有用。仍然一份 kind 一个条目，
            // 因为接口的契约是"kind → 目录"（多版本场景由安装编排在 M2-7 展开）。
            .ToDictionary(
                static g => g.Key,
                static g => g.OrderByDescending(r => r.Version, StringComparer.Ordinal).First().HomePath,
                StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ImmutableArray<string> PathEntries => _pathEntries.Value;

    /// <summary>统一为"无反斜杠结尾、正斜杠转反斜杠"的形式，便于比较。</summary>
    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');

    /// <summary><paramref name="inner"/> 是否等于 <paramref name="outer"/> 或在其之内。</summary>
    private static bool IsSameOrUnder(string inner, string outer) =>
        string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase) ||
        inner.StartsWith(outer + "\\", StringComparison.OrdinalIgnoreCase);
}
