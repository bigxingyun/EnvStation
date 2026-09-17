using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace EnvStation.Core.Diagnostics;

/// <summary>规则级别。</summary>
public enum PathRuleLevel
{
    /// <summary>警告：可以继续，但要在最终确认页再次汇总。</summary>
    Warn = 0,

    /// <summary>阻断：禁止继续。</summary>
    Block = 1,
}

/// <summary>一键修正策略。</summary>
public enum PathFixKind
{
    /// <summary>没有自动修正，只能手工改。</summary>
    None = 0,

    /// <summary>去掉每段结尾的空格与点（R5）。</summary>
    StripTrailing = 1,

    /// <summary>把非 ASCII 的目录段换成英文建议名（R1）。</summary>
    ToAsciiName = 2,
}

/// <summary>判定类型（规则文件里的 <c>check</c> 字段）。</summary>
/// <remarks>
/// 规则文件只声明"这一条用哪种判定"，判定逻辑仍在代码里——
/// 把判定也做成数据会变成一门需要解释器的小语言，而这条路的尽头就是铁律 A1 禁止的东西。
/// 外置的是**规则的存在、级别与文案**，不是行为能力。
/// </remarks>
public enum PathCheckKind
{
    /// <summary>含非 ASCII 字符（R1）。</summary>
    NonAscii = 0,

    /// <summary>含空格（R2）。</summary>
    ContainsSpace = 1,

    /// <summary>含脚本敏感字符（R3）。</summary>
    SpecialCharacters = 2,

    /// <summary>段名是保留设备名（R4）。</summary>
    ReservedDeviceName = 3,

    /// <summary>段名以空格或点结尾（R5）。</summary>
    TrailingSpaceOrDot = 4,

    /// <summary>路径较长（R6 警告档）。</summary>
    LongPath = 5,

    /// <summary>路径过长（R6 阻断档）。</summary>
    VeryLongPath = 6,

    /// <summary>含 Windows 不允许的字符（R7）。</summary>
    InvalidPathCharacters = 7,

    /// <summary>需要管理员权限的位置（R8）。</summary>
    NeedsElevation = 8,

    /// <summary>云同步目录（R9）。</summary>
    CloudSyncedDirectory = 9,

    /// <summary>网络路径或可移动盘（R10）。</summary>
    NetworkOrRemovableDrive = 10,

    /// <summary>符号链接或目录联接（R11）。</summary>
    ReparsePoint = 11,

    /// <summary>含未展开的变量引用（R12）。</summary>
    UnresolvedVariable = 12,

    /// <summary>与已登记组件目录重叠（R13）。</summary>
    OverlapsRegisteredRuntime = 13,

    /// <summary>磁盘不是 NTFS（R14）。</summary>
    NotNtfs = 14,

    /// <summary>已存在且非空目录（R15）。</summary>
    ExistingNonEmptyDirectory = 15,

    /// <summary>磁盘剩余空间不足（R16）。</summary>
    InsufficientDiskSpace = 16,

    /// <summary>已在 PATH 中（R17）。</summary>
    AlreadyInPath = 17,
}

/// <summary>一条规则的声明（来自 <c>data/rules/path-rules.toml</c>）。</summary>
/// <param name="Id">规则编号（R1~R17）。</param>
/// <param name="Level">级别。</param>
/// <param name="Check">判定类型。</param>
/// <param name="Title">一句话结论。</param>
/// <param name="Message">现象说明。</param>
/// <param name="Fix">一键修正策略。</param>
public sealed record PathRule(
    string Id,
    PathRuleLevel Level,
    PathCheckKind Check,
    string Title,
    string Message,
    PathFixKind Fix)
{
    /// <summary>是否阻断级。</summary>
    public bool IsBlocking => Level == PathRuleLevel.Block;
}

/// <summary>校验结论里的一条。</summary>
/// <param name="Rule">触发的规则。</param>
/// <param name="Detail">具体到本次路径的补充说明（如"第 2 段是 CON"）。</param>
/// <param name="Suggestion">一键修正后的建议路径；不可修正时为空。</param>
public sealed record PathFinding(PathRule Rule, string Detail, string? Suggestion)
{
    /// <summary>级别。</summary>
    public PathRuleLevel Level => Rule.Level;

    /// <summary>是否阻断级。</summary>
    public bool IsBlocking => Rule.IsBlocking;

    /// <summary>渲染成一行（供 CLI 与日志使用）。</summary>
    public string ToLine()
    {
        var head = Rule.IsBlocking ? "[阻断]" : "[警告]";
        var text = $"{head} {Rule.Id}：{Rule.Title}";
        if (Detail.Length > 0)
        {
            text += $"（{Detail}）";
        }

        return text;
    }
}

/// <summary>一次目录校验的完整结论。</summary>
/// <param name="Path">被校验的路径。</param>
/// <param name="Findings">发现项（阻断在前）。</param>
public sealed record PathValidationResult(string Path, ImmutableArray<PathFinding> Findings)
{
    /// <summary>是否通过（无阻断级发现）。</summary>
    public bool CanProceed => !Findings.Any(static f => f.IsBlocking);

    /// <summary>阻断级数量。</summary>
    public int BlockCount => Findings.Count(static f => f.IsBlocking);

    /// <summary>警告级数量。</summary>
    public int WarnCount => Findings.Count(static f => !f.IsBlocking);

    /// <summary>是否一切正常。</summary>
    public bool IsClean => Findings.IsEmpty;

    /// <summary>最好的一条修正建议（取第一个有建议的发现项）。</summary>
    public string? BestSuggestion => Findings.FirstOrDefault(static f => f.Suggestion is not null)?.Suggestion;

    /// <summary>一句话结论。</summary>
    public string Summarize() => Findings.IsEmpty
        ? "路径校验通过。"
        : BlockCount > 0
            ? $"路径有 {BlockCount} 项阻断级问题、{WarnCount} 项警告。"
            : $"路径有 {WarnCount} 项警告，可以继续。";
}

/// <summary>
/// 规则引擎需要的外部事实。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么把事实抽成接口</b>：引擎要判的东西里，一大半需要读文件系统与注册表
/// （目录在不在、是不是联接点、盘是什么格式、剩多少空间、已登记哪些组件）。
/// 若引擎自己去读，17 条规则就只能靠"在真机上凑一个刚好触发的目录"来测——
/// 而 R4（保留设备名）、R10（网络盘）、R14（非 NTFS）这类**在开发机上根本造不出来**。
/// </para>
/// <para>
/// 抽成接口之后，17 条规则每条都可以用构造好的事实精确触发。生产实现读真实系统，
/// 测试实现给假数据，两者跑同一套判定逻辑。
/// </para>
/// </remarks>
public interface IPathProbe
{
    /// <summary>路径是否已展开（不应再含 <c>%VAR%</c>）。</summary>
    bool ContainsVariableReference(string path);

    /// <summary>是否为 UNC 网络路径（<c>\\server\share</c>）。</summary>
    bool IsNetworkPath(string path);

    /// <summary>盘符根目录（如 <c>C:\</c>）；无法识别时为空。</summary>
    string DriveRoot(string path);

    /// <summary>驱动器类型名（<c>fixed</c> / <c>removable</c> / <c>network</c> / <c>unknown</c>）。</summary>
    string DriveType(string path);

    /// <summary>文件系统名（<c>NTFS</c> / <c>FAT32</c> / 空表示未知）。</summary>
    string FileSystem(string path);

    /// <summary>可用空间（字节）；无法取到时为 null。</summary>
    long? FreeSpaceBytes(string path);

    /// <summary>目录是否存在。</summary>
    bool DirectoryExists(string path);

    /// <summary>
    /// 目录内的顶层条目数；目录不存在时为 0，<b>读不到内容时为 null</b>。
    /// </summary>
    /// <remarks>
    /// "空目录"与"读不进去"必须分开：前者是安全的（R15 不报），后者意味着
    /// 我们根本不知道里面有什么，按空目录处理会让"继续安装可能覆盖别人的文件"
    /// 这条警告在最需要它的时候消失。返回 <c>null</c> 时引擎给的是"无法确认"的措辞。
    /// </remarks>
    int? EnumerateEntryCount(string path);

    /// <summary>是否由环境站创建（用于 R15 区分"我建的"与"别人的"）。</summary>
    bool IsOwnedByEnvStation(string path);

    /// <summary>是否符号链接或目录联接。</summary>
    bool IsReparsePoint(string path);

    /// <summary>联接点/符号链接的实际目标；不是链接时为空。</summary>
    string? ReparseTarget(string path);

    /// <summary>已登记的组件目录（kind → 目录）。</summary>
    IReadOnlyDictionary<string, string> RegisteredRuntimeDirectories { get; }

    /// <summary>合并后的 PATH 条目（用于 R17）。</summary>
    ImmutableArray<string> PathEntries { get; }
}

/// <summary>
/// 安装目录校验引擎：R1~R17。
/// </summary>
/// <remarks>
/// <para>
/// <b>它管的是"安装到哪"，不是"PATH 里有什么"</b>。后者由 <c>envstation.path.validate</c> 负责。
/// 两者都涉及路径，但问题完全不同：一个问"这个目录能不能安全地装进去"，
/// 另一个问"现有 PATH 有没有毛病"。混在一起会让两边的错误信息互相干扰。
/// </para>
/// <para>
/// <b>规则外置</b>（需求 V5）：规则的存在、级别与文案来自 <c>data/rules/path-rules.toml</c>；
/// 判定逻辑在代码里。读不到规则文件时<b>拒绝工作并说明缺哪个文件</b>——
/// 静默放行的后果是"校验全绿"，而用户以为已经检查过了。
/// </para>
/// </remarks>
public sealed class PathRuleEngine
{
    private readonly ImmutableArray<PathRule> _rules;
    private readonly IPathProbe _probe;

    private PathRuleEngine(ImmutableArray<PathRule> rules, IPathProbe probe, int warnLength, int blockLength)
    {
        _rules = rules;
        _probe = probe;
        WarnLength = warnLength;
        BlockLength = blockLength;
    }

    /// <summary>规则条数。</summary>
    public int RuleCount => _rules.Length;

    /// <summary>规则列表。</summary>
    public ImmutableArray<PathRule> Rules => _rules;

    /// <summary>外部事实来源。</summary>
    /// <remarks>
    /// 暴露出来是为了让调用方在"另建一个引擎"时复用同一份事实——
    /// 例如测试要验证"规则文件缺失时会不会静默放行"，得拿一个现成的 probe 去试着加载。
    /// 生产代码不会换它。
    /// </remarks>
    public IPathProbe Probe => _probe;

    /// <summary>超过此长度开始警告（配置项）。</summary>
    public int WarnLength { get; }

    /// <summary>超过此长度直接阻断（配置项）。</summary>
    public int BlockLength { get; }

    /// <summary>
    /// 从 TOML 文本建立引擎。
    /// </summary>
    /// <param name="toml">规则文件内容。</param>
    /// <param name="probe">外部事实来源。</param>
    /// <exception cref="InvalidOperationException">规则文件缺失或内容不合法时抛出。</exception>
    public static PathRuleEngine LoadFrom(string toml, IPathProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (string.IsNullOrWhiteSpace(toml))
        {
            throw new InvalidOperationException("路径校验规则文件为空，无法校验任何路径。");
        }

        var parsed = Toml.TomlReader.Parse(toml);
        if (parsed.IsFailure)
        {
            throw new InvalidOperationException(
                $"路径校验规则文件解析失败：{parsed.Error.Message}");
        }

        var root = parsed.Value;
        var meta = root.GetTable("meta");
        var warnLength = (int)(meta?.GetInteger("warn_length") ?? 200);
        var blockLength = (int)(meta?.GetInteger("block_length") ?? 260);

        var rules = ImmutableArray.CreateBuilder<PathRule>();
        var ruleArray = root.GetArray("rules");
        if (ruleArray is null)
        {
            throw new InvalidOperationException(
                "路径校验规则文件里没有 [[rules]] 段落，无法校验任何路径。");
        }

        foreach (var item in ruleArray)
        {
            if (item is not Toml.TomlTable table)
            {
                continue;
            }

            var id = table.GetString("id");
            var checkName = table.GetString("check");
            var title = table.GetString("title");
            var message = table.GetString("message");

            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(checkName) ||
                string.IsNullOrWhiteSpace(title))
            {
                throw new InvalidOperationException(
                    $"路径校验规则缺少必要字段（id / check / title 之一）：第 {table.Line} 行。");
            }

            if (!Enum.TryParse<PathCheckKind>(checkName, ignoreCase: true, out var check))
            {
                throw new InvalidOperationException(
                    $"路径校验规则 {id} 的 check 取值无法识别：{checkName}。" +
                    $"可选值见 {nameof(PathCheckKind)}。");
            }

            var levelName = table.GetString("level") ?? "warn";
            var level = string.Equals(levelName, "block", StringComparison.OrdinalIgnoreCase)
                ? PathRuleLevel.Block
                : PathRuleLevel.Warn;

            var fixName = table.GetString("fix") ?? "none";
            var fix = Enum.TryParse<PathFixKind>(fixName, ignoreCase: true, out var parsedFix)
                ? parsedFix
                : PathFixKind.None;

            rules.Add(new PathRule(id, level, check, title, message ?? title, fix));
        }

        if (rules.Count == 0)
        {
            throw new InvalidOperationException(
                "路径校验规则文件里没有任何规则，无法校验任何路径。");
        }

        return new PathRuleEngine(rules.ToImmutable(), probe, warnLength, blockLength);
    }

    /// <summary>
    /// 从磁盘上的规则文件建立引擎。
    /// </summary>
    /// <param name="rulesPath">规则文件路径。</param>
    /// <param name="probe">外部事实来源。</param>
    /// <exception cref="FileNotFoundException">文件不存在时抛出——刻意不静默放行。</exception>
    public static PathRuleEngine Load(string rulesPath, IPathProbe probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesPath);

        if (!File.Exists(rulesPath))
        {
            throw new FileNotFoundException(
                $"路径校验规则文件不存在：{rulesPath}。缺少它就无法校验安装目录，因此拒绝继续。",
                rulesPath);
        }

        return LoadFrom(File.ReadAllText(rulesPath), probe);
    }

    /// <summary>默认规则文件路径（相对应用数据目录）。</summary>
    public static string DefaultRelativePath => Path.Combine("data", "rules", "path-rules.toml");

    /// <summary>
    /// 校验一个候选安装目录。
    /// </summary>
    /// <param name="path">候选目录。</param>
    /// <param name="requiredBytes">本次安装需要的空间；null 表示不检查磁盘空间。</param>
    /// <returns>按"阻断在前、同级按规则编号"排序的结论。</returns>
    public PathValidationResult Validate(string path, long? requiredBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var builder = ImmutableArray.CreateBuilder<PathFinding>();

        foreach (var rule in _rules)
        {
            var detail = Evaluate(rule.Check, path, requiredBytes);
            if (detail is null)
            {
                continue;
            }

            var suggestion = rule.Fix == PathFixKind.None ? null : TryFix(path, rule.Fix);
            builder.Add(new PathFinding(rule, detail, suggestion));
        }

        var ordered = builder
            .OrderByDescending(static f => (int)f.Level)
            .ThenBy(static f => f.Rule.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        return new PathValidationResult(path, ordered);
    }

    /// <summary>逐条判定：返回 null 表示这一条没触发，否则返回补充说明。</summary>
    private string? Evaluate(PathCheckKind check, string path, long? requiredBytes)
    {
        var segments = SplitSegments(path);

        switch (check)
        {
            case PathCheckKind.NonAscii:
                var bad = segments.Where(static s => s.Any(static c => c > 127)).ToArray();
                return bad.Length == 0 ? null : $"含非 ASCII 的段：{string.Join("、", bad)}";

            case PathCheckKind.ContainsSpace:
                return path.Contains(' ', StringComparison.Ordinal) ? "路径中有空格" : null;

            case PathCheckKind.SpecialCharacters:
                // `%` 单独由 R12 负责，避免同一条问题报两次。
                var specials = "&^!()[]{}';,=+`~@#$".ToCharArray();
                var hit = segments.FirstOrDefault(s => s.IndexOfAny(specials) >= 0);
                return hit is null ? null : $"段「{hit}」含脚本敏感字符";

            case PathCheckKind.ReservedDeviceName:
                var reserved = segments.Where(IsReservedDeviceName).ToArray();
                return reserved.Length == 0 ? null : $"含保留设备名：{string.Join("、", reserved)}";

            case PathCheckKind.TrailingSpaceOrDot:
                var trailing = segments
                    .Where(static s => s.Length > 0 && (s[^1] == ' ' || s[^1] == '.'))
                    .ToArray();
                return trailing.Length == 0 ? null : $"段「{string.Join("、", trailing)}」以空格或点结尾";

            case PathCheckKind.LongPath:
                return path.Length > WarnLength && path.Length <= BlockLength
                    ? $"当前 {path.Length} 字符，超过 {WarnLength}"
                    : null;

            case PathCheckKind.VeryLongPath:
                return path.Length > BlockLength
                    ? $"当前 {path.Length} 字符，超过 {BlockLength}"
                    : null;

            case PathCheckKind.InvalidPathCharacters:
                // 冒号只在盘符位置合法，因此去掉开头的 `X:` 之后再查。
                var tail = path.Length >= 2 && path[1] == ':' ? path[2..] : path;
                var invalid = tail.FirstOrDefault(static c => c is '<' or '>' or ':' or '"' or '|' or '?' or '*');
                return invalid == default ? null : $"含字符 `{invalid}`";

            case PathCheckKind.NeedsElevation:
                return IsElevatedLocation(path) ? "该位置需要管理员权限" : null;

            case PathCheckKind.CloudSyncedDirectory:
                var cloud = MatchCloudFolder(path);
                return cloud is null ? null : $"位于 {cloud} 同步目录内";

            case PathCheckKind.NetworkOrRemovableDrive:
                if (_probe.IsNetworkPath(path))
                {
                    return "是网络路径";
                }

                var type = _probe.DriveType(path);
                return type is "removable" or "network" ? $"驱动器类型为 {type}" : null;

            case PathCheckKind.ReparsePoint:
                if (!_probe.IsReparsePoint(path))
                {
                    return null;
                }

                var target = _probe.ReparseTarget(path);
                return target is { Length: > 0 } ? $"实际位置为 {target}" : "是符号链接或联接点";

            case PathCheckKind.UnresolvedVariable:
                return _probe.ContainsVariableReference(path) ? "含 %...% 形式的变量引用" : null;

            case PathCheckKind.OverlapsRegisteredRuntime:
                var owner = FindOverlappingRuntime(path);
                return owner is null ? null : $"已被 {owner} 使用";

            case PathCheckKind.NotNtfs:
                var fs = _probe.FileSystem(path);
                return fs.Length > 0 && !string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase)
                    ? $"文件系统为 {fs}"
                    : null;

            case PathCheckKind.ExistingNonEmptyDirectory:
                if (!_probe.DirectoryExists(path) || _probe.IsOwnedByEnvStation(path))
                {
                    return null;
                }

                var count = _probe.EnumerateEntryCount(path);
                if (count is null)
                {
                    // 读不到内容 ≠ 空目录。按空目录处理会让这条警告在最需要它的时候消失。
                    return "已存在，但读不到其中内容（权限不足）";
                }

                return count.Value > 0 ? $"已存在且包含 {count.Value} 个条目" : null;

            case PathCheckKind.InsufficientDiskSpace:
                if (requiredBytes is not { } need)
                {
                    return null;
                }

                var free = _probe.FreeSpaceBytes(path);
                if (free is not { } available)
                {
                    return null;
                }

                // 需求 R16：留 1.5 倍余量。装到一半失败会留下半成品环境。
                var required = (long)(need * 1.5);
                return available < required
                    ? $"需要 {ToGigabytes(required)}，可用 {ToGigabytes(available)}"
                    : null;

            case PathCheckKind.AlreadyInPath:
                var normalized = NormalizeForCompare(path);
                var exists = _probe.PathEntries.Any(e =>
                    string.Equals(NormalizeForCompare(e), normalized, StringComparison.OrdinalIgnoreCase));
                return exists ? "该目录已在 PATH 中" : null;

            default:
                return null;
        }
    }

    /// <summary>尝试一键修正。</summary>
    private static string? TryFix(string path, PathFixKind kind) => kind switch
    {
        PathFixKind.StripTrailing => PathAsciiFix.StripTrailing(path),
        PathFixKind.ToAsciiName => PathAsciiFix.ToAsciiPath(path),
        _ => null,
    };

    private string? FindOverlappingRuntime(string path)
    {
        var candidate = NormalizeForCompare(path);
        foreach (var (kind, directory) in _probe.RegisteredRuntimeDirectories)
        {
            if (directory.Length == 0)
            {
                continue;
            }

            var registered = NormalizeForCompare(directory);

            // 互为父子即算重叠：装在父目录里会与子目录混在一起，反之亦然。
            if (candidate.StartsWith(registered, StringComparison.OrdinalIgnoreCase) ||
                registered.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }

        // 保留设备名匹配时忽略扩展名：`CON.txt` 同样不可用（Windows 的既有行为）。
        var name = segment;
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            name = name[..dot];
        }

        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Length == 4 &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            name[3] is >= '1' and <= '9')
        {
            return true;
        }

        return false;
    }

    private static bool IsElevatedLocation(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        string[] roots =
        [
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            @"C:\Windows",
            @"C:\ProgramData",
        ];

        foreach (var root in roots)
        {
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 盘根目录（`D:\` 去掉尾反斜杠后长度为 2 且第二个字符是冒号）。
        return normalized.Length == 2 && normalized[1] == ':';
    }

    private static string? MatchCloudFolder(string path)
    {
        (string Marker, string Name)[] clients =
        [
            ("\\OneDrive", "OneDrive"),
            ("\\Dropbox", "Dropbox"),
            ("\\iCloudDrive", "iCloud"),
            ("\\Nutstore", "坚果云"),
            ("\\我的坚果云", "坚果云"),
            ("\\Google Drive", "Google Drive"),
            ("\\百度网盘", "百度网盘"),
        ];

        foreach (var (marker, name) in clients)
        {
            if (path.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    private static ImmutableArray<string> SplitSegments(string path) =>
        [.. path.Replace('/', '\\')
            .Split('\\', StringSplitOptions.None)
            .Where(static s => s.Length > 0)
            // 盘符（`D:`）不是目录名，不参与段名类规则。
            .Where(static s => !(s.Length == 2 && s[1] == ':'))];

    private static string NormalizeForCompare(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');

    private static string ToGigabytes(long bytes) =>
        (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " GB";
}

/// <summary>
/// 一键修正的实现（需求 V4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>中文段名换英文建议名</b>：维护一张常见开发目录名的对照表。
/// 表外的中文段按字转写——不追求拼音正确，只要**得到一个可用的纯 ASCII 名字**，
/// 并且让用户看得出它原本是什么。
/// </para>
/// <para>
/// 刻意不做的：不猜用户的意图、不静默替用户决定装到哪。修正结果只是**建议**，
/// 由用户确认（V2 要求"给出可点的修正"，不是"自动改掉"）。
/// </para>
/// </remarks>
public static class PathAsciiFix
{
    /// <summary>常见中文目录名的英文对照。</summary>
    private static readonly ImmutableDictionary<string, string> CommonNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["开发工具"] = "DevTools",
            ["开发环境"] = "DevEnv",
            ["工具"] = "Tools",
            ["软件"] = "Software",
            ["程序"] = "Programs",
            ["环境"] = "Env",
            ["运行时"] = "Runtimes",
            ["项目"] = "Projects",
            ["代码"] = "Code",
            ["源码"] = "Source",
            ["文档"] = "Documents",
            ["下载"] = "Downloads",
            ["桌面"] = "Desktop",
            ["用户"] = "Users",
            ["数据"] = "Data",
            ["缓存"] = "Cache",
            ["临时"] = "Temp",
            ["安装"] = "Install",
            ["测试"] = "Test",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>去掉每段结尾的空格与点（Windows 会丢弃它们）。</summary>
    public static string? StripTrailing(string path)
    {
        var segments = path.Split('\\');
        var changed = false;

        for (var i = 0; i < segments.Length; i++)
        {
            var trimmed = segments[i].TrimEnd(' ', '.');
            if (trimmed != segments[i])
            {
                segments[i] = trimmed;
                changed = true;
            }
        }

        return changed ? string.Join('\\', segments) : null;
    }

    /// <summary>把非 ASCII 的段换成英文建议名。</summary>
    public static string? ToAsciiPath(string path)
    {
        var segments = path.Split('\\');
        var changed = false;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.All(static c => c <= 127))
            {
                continue;
            }

            segments[i] = ToAsciiSegment(segment);
            changed = true;
        }

        return changed ? string.Join('\\', segments) : null;
    }

    private static string ToAsciiSegment(string segment)
    {
        // 整段命中对照表时直接用表里的名字——这比逐字转写好得多。
        if (CommonNames.TryGetValue(segment, out var mapped))
        {
            return mapped;
        }

        // 段里既有中文又有英文（如 `Python开发`）时，保留 ASCII 部分、削掉中文。
        var sb = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            if (ch <= 127)
            {
                sb.Append(ch);
            }
        }

        var kept = sb.ToString().Trim(' ', '-', '_');
        if (kept.Length > 0)
        {
            return kept;
        }

        // 整段都是非 ASCII：用首字的码位给一个可读且稳定的名字。
        // 刻意不用拼音——没有可靠的转换表，猜出来的拼音只会让用户困惑。
        var first = segment[0];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Dir{char.ConvertToUtf32(first.ToString(), 0):X4}");
    }
}
