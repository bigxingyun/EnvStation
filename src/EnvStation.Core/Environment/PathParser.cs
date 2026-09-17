using System.Runtime.InteropServices;
using System.Text;

namespace EnvStation.Core.Environment;

/// <summary>
/// PATH 解析与规范化内核（《详细设计文档》7.1 节的正式实现）。
///
/// <para><b>这是整个项目最容易出错、也最不能出错的一段代码。</b>一旦写坏了 PATH，
/// 用户机器上的系统命令会集体失效（需求 1.2 节 P2 描述的正是这种事故）。
/// 因此本类的所有规则都有对应的边界用例测试，见 <c>EnvStation.Tests.PathKernel</c>。</para>
///
/// <para><b>七条硬性规则（PE-1~PE-7）：</b></para>
/// <list type="number">
///   <item>内部统一用规范化绝对路径比较，但**保留原始写法用于回写**。</item>
///   <item>比较忽略大小写；回写保留原大小写。</item>
///   <item>容忍 `;` 混用、项首尾空白、空项。</item>
///   <item>写回时**保持原值类型**（REG_SZ / REG_EXPAND_SZ）；REG_EXPAND_SZ 不回写展开结果。</item>
///   <item>顺序语义敏感，任何重排都视为 L1 变更，必须快照 + diff。</item>
///   <item>去重保留**首次出现**。</item>
///   <item>空项与纯空白项可清理，但必须在预览中列出。</item>
/// </list>
///
/// <para><b>陷阱警示：</b>本文件所在命名空间为 <c>EnvStation.Core.Environment</c>，
/// 因此 <c>Environment.XXX</c> 这类简单名会被解析为**命名空间**而非 <c>System.Environment</c>。
/// 本文件内一律使用 <c>System.Environment</c> 全限定名。</para>
/// </summary>
public static partial class PathParser
{
    /// <summary>PATH 使用的分隔符。</summary>
    public const char Separator = ';';

    /// <summary>
    /// 解析 PATH 字符串为条目数组。
    /// </summary>
    /// <param name="pathValue">PATH 的原始值（可为 null 或空）。</param>
    /// <param name="probeFileSystem">
    /// 是否探测目录是否存在。默认 true。
    /// 设为 false 可让纯逻辑测试不依赖真实文件系统，从而可复现。
    /// </param>
    /// <param name="directoryExists">
    /// 目录存在性判定函数。为 null 时使用 <see cref="Directory.Exists(string)"/>。
    /// 测试注入此参数即可精确控制"哪些目录存在"，不受本机实际磁盘影响。
    /// </param>
    public static IReadOnlyList<PathEntry> Parse(
        string? pathValue,
        bool probeFileSystem = true,
        Func<string, bool>? directoryExists = null)
    {
        if (string.IsNullOrEmpty(pathValue))
        {
            return [];
        }

        var exists = directoryExists ?? Directory.Exists;
        var rawParts = pathValue.Split(Separator);
        var entries = new List<PathEntry>(rawParts.Length);

        // 首次出现的比较键 -> 下标，用于标记重复并指向"首次出现"（PE-6）
        var firstSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rawParts.Length; i++)
        {
            var raw = rawParts[i];
            var trimmed = raw.Trim();
            var issues = PathEntryIssue.None;

            // ── 空项（PE-7）──
            if (trimmed.Length == 0)
            {
                entries.Add(new PathEntry
                {
                    Index = i,
                    Raw = string.Empty,
                    Normalized = string.Empty,
                    ComparisonKey = string.Empty,
                    Exists = false,
                    Issues = PathEntryIssue.Empty,
                });
                continue;
            }

            // ── 变量引用检查（不展开，只判断是否可解析）──
            if (HasUnresolvedVariable(trimmed))
            {
                issues |= PathEntryIssue.UnresolvedVariable;
            }

            // ── 尾随分隔符（根目录除外）──
            if (HasTrailingSeparator(trimmed))
            {
                issues |= PathEntryIssue.TrailingSeparator;
            }

            // ── 规范化（用于比较与展示）──
            var normalized = Normalize(trimmed);

            // ── 绝对路径判定 ──
            // 注意：Path.IsPathFullyQualified("C:foo") 返回 false —— 这是"盘符相对路径"，
            // 它相对于该盘的当前目录，属于必须在检测中提示的问题项。
            if (!Path.IsPathFullyQualified(normalized))
            {
                issues |= PathEntryIssue.Relative;
            }

            // ── 非法字符 ──
            if (HasInvalidPathCharacters(normalized))
            {
                issues |= PathEntryIssue.InvalidCharacters;
            }

            // ── 存在性 ──
            // 刻意**跳过**三类条目的存在性探测，避免产生误导性的"目录不存在"结论：
            //   · Relative            —— 相对路径的解析结果取决于进程当前目录，探测无意义
            //   · InvalidCharacters   —— 路径本身非法，探测只会抛异常
            //   · UnresolvedVariable  —— 变量未定义时无法得知真实路径，标 Missing 是假阳性
            // 这三类各自已有专门的标记，交由规则引擎分别给文案。
            var probeWorthy = (issues & (PathEntryIssue.Relative
                                         | PathEntryIssue.InvalidCharacters
                                         | PathEntryIssue.UnresolvedVariable)) == 0;

            var existsOnDisk = false;
            if (probeFileSystem && probeWorthy)
            {
                try
                {
                    existsOnDisk = exists(normalized);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    existsOnDisk = false;
                    issues |= PathEntryIssue.InvalidCharacters;
                }
            }

            if (probeFileSystem && probeWorthy && !existsOnDisk)
            {
                issues |= PathEntryIssue.Missing;
            }

            // ── 重复判定（PE-6：指向首次出现）──
            int? duplicateOf = null;
            if (firstSeen.TryGetValue(normalized, out var firstIndex))
            {
                issues |= PathEntryIssue.Duplicate;
                duplicateOf = firstIndex;
            }
            else
            {
                firstSeen[normalized] = i;
            }

            entries.Add(new PathEntry
            {
                Index = i,
                Raw = trimmed,
                Normalized = normalized,
                ComparisonKey = normalized.ToLowerInvariant(),
                Exists = existsOnDisk,
                Issues = issues,
                DuplicateOfIndex = duplicateOf,
            });
        }

        return entries;
    }

    /// <summary>
    /// 规范化单个条目：展开变量（仅用于比较）→ 解析 8.3 短名为长名 → 去除尾随分隔符。
    /// </summary>
    public static string Normalize(string entry)
    {
        var s = entry.Trim().Trim('"');

        // 展开 %VAR%（仅用于比较；不用于回写）。
        // ★ 必须用 System.Environment 全限定名：本文件所在命名空间是 EnvStation.Core.Environment，
        //   简单名 "Environment" 会被解析为**命名空间**而非类型，直接导致 CS0234。
        //   本文件所有 Environment.* 调用都必须全限定，这是个很容易复发的坑。
        if (s.Contains('%', StringComparison.Ordinal))
        {
            s = System.Environment.ExpandEnvironmentVariables(s);
        }

        // 解析 8.3 短名 → 长名。
        // 这在重复检测中很关键：C:\PROGRA~1\Java 与 C:\Program Files\Java 必须判为同一目录。
        s = TryGetLongPath(s) ?? s;

        // 去除尾随分隔符（根目录 "D:\" 必须保留）
        s = TrimTrailingSeparator(s);

        return s;
    }

    /// <summary>生成用于比较的键：规范化 + 小写（PE-2）。</summary>
    public static string ComparisonKey(string entry) => Normalize(entry).ToLowerInvariant();

    /// <summary>
    /// 判断两个条目是否指向同一目录（忽略大小写、短名、尾随分隔符差异）。
    /// </summary>
    public static bool AreSameEntry(string a, string b)
        => string.Equals(ComparisonKey(a), ComparisonKey(b), StringComparison.Ordinal);

    /// <summary>
    /// 把条目数组拼回 PATH 字符串。
    /// <para>
    /// <b>回写纪律：</b>默认使用每个条目的 <see cref="PathEntry.Raw"/>，
    /// 从而保留用户原始写法与变量引用（PE-1 / PE-4）。
    /// </para>
    /// </summary>
    public static string Join(IEnumerable<PathEntry> entries)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var e in entries)
        {
            if (!first)
            {
                sb.Append(Separator);
            }

            sb.Append(e.Raw);
            first = false;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 计算 PATH 的"人类可读长度"判定所需的数值。
    /// 用于需求 6.2 节的两级阈值：2047（传统 GUI 编辑上限，警告）与 32767（硬上限，阻断）。
    /// </summary>
    public static int MeasureLength(string? pathValue) => pathValue?.Length ?? 0;

    // ────────────────────────── 内部辅助 ──────────────────────────

    /// <summary>
    /// 判定是否存在未解析的变量引用：<c>%</c> 未配对，或引用了未定义且原样保留的变量。
    /// <para>
    /// <c>Environment.ExpandEnvironmentVariables</c> 在变量不存在时会**原样保留** <c>%FOO%</c>，
    /// 因此"展开后仍含 %"即说明该引用无法解析。这个判定方式是可靠的。
    /// </para>
    /// </summary>
    internal static bool HasUnresolvedVariable(string entry)
    {
        if (!entry.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        var expanded = System.Environment.ExpandEnvironmentVariables(entry);
        return expanded.Contains('%', StringComparison.Ordinal);
    }

    /// <summary>判定是否以分隔符结尾，但排除根目录（如 <c>D:\</c>）。</summary>
    internal static bool HasTrailingSeparator(string entry)
    {
        if (entry.Length == 0)
        {
            return false;
        }

        var last = entry[^1];
        if (last is not ('\\' or '/'))
        {
            return false;
        }

        // 根目录保留尾随反斜杠是正确写法，不算问题
        var trimmed = entry.TrimEnd('\\', '/');
        return trimmed.Length > 0 && !trimmed.EndsWith(':');
    }

    /// <summary>
    /// 去除尾随分隔符，但保留根目录形态（<c>D:\</c>）。
    ///
    /// <para><b>为什么必须保留根目录的尾随反斜杠：</b>Windows 上 <c>D:\</c> 与 <c>D:</c>
    /// 是<b>语义不同</b>的两个路径 —— 前者是"D 盘根目录"，后者是"D 盘当前目录"
    /// （一个相对于进程状态的相对路径）。若把 <c>D:\</c> 规范化成 <c>D:</c>，
    /// 就会把绝对路径误判为相对路径，并可能在回写时改变语义。
    /// 这个 bug 由用例 PK-06 捕获。</para>
    /// </summary>
    internal static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1)
        {
            return path;
        }

        var trimmed = path.TrimEnd('\\', '/');

        // 根目录 "D:\" → trimmed 为 "D:"，必须把反斜杠补回来
        if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]))
        {
            return trimmed + "\\";
        }

        // UNC 根 "\\server\share\"：保留其形态
        if (trimmed.Length == 0)
        {
            return path;
        }

        return trimmed;
    }

    /// <summary>
    /// 判定是否含非法路径字符。
    /// 刻意排除 <c>:</c>（盘符）与反斜杠/正斜杠（分隔符）。
    /// </summary>
    internal static bool HasInvalidPathCharacters(string path)
    {
        // Path.GetInvalidPathChars() 在 Windows 上返回 " | \0 等；
        // 某些字符（如 < > * ?）不在其中但同样非法，需显式补充。
        foreach (var c in path)
        {
            if (c == ':' || c == '\\' || c == '/')
            {
                continue;
            }

            if (c < 32 || c is '<' or '>' or '|' or '"' or '*' or '?')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 尝试把 8.3 短名解析为长名。失败返回 <c>null</c>（调用方保留原值）。
    ///
    /// <para><b>为什么用 P/Invoke 而不是 <see cref="Path.GetFullPath(string)"/>：</b>
    /// 后者只做字符串处理，不会查询文件系统，因此无法把 <c>PROGRA~1</c> 展开成
    /// <c>Program Files</c>。而"短名与长名指向同一目录"正是重复检测必须处理的情形。</para>
    ///
    /// <para><b>为什么用 <see cref="LibraryImportAttribute"/> 而不是 <c>[DllImport]</c>：</b>
    /// 本项目采用 Native AOT 发布，<c>LibraryImport</c> 在编译期生成 marshalling 代码，
    /// 无运行期封送开销，且不依赖 AOT 下受限的 IL stub 机制。同时它天然规避了
    /// CA1838（禁止在 P/Invoke 上使用 StringBuilder）。</para>
    /// </summary>
    private static string? TryGetLongPath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.Length == 0)
        {
            return null;
        }

        // 优化：只有含 '~' 才可能是 8.3 短名，避免对每个 PATH 项都做系统调用
        if (!path.Contains('~', StringComparison.Ordinal))
        {
            return null;
        }

        // 先按常见长度申请缓冲区；不足时按返回值扩容重试（API 会返回所需长度）
        var buffer = new char[260];
        var length = GetLongPathName(path, buffer, buffer.Length);
        if (length == 0)
        {
            // 调用失败：路径可能不存在或不可访问。返回 null 让调用方保留原值（不抛异常）。
            return null;
        }

        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetLongPathName(path, buffer, buffer.Length);
            if (length == 0 || length > buffer.Length)
            {
                return null;
            }
        }

        // GetLongPathNameW 返回的是**不含结尾 NUL** 的字符数
        return length == 0 ? null : new string(buffer, 0, length);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetLongPathName(string shortPath, [Out] char[] longPath, int bufferLength);
}
