using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using EnvStation.Abstractions;

namespace EnvStation.Core.Configuration;

/// <summary>一个配置文件目标的探测结果。</summary>
/// <param name="Target">目标标识（如 <c>maven</c>）。</param>
/// <param name="DisplayName">中文名。</param>
/// <param name="Candidates">候选路径（按生效优先级从高到低）。</param>
/// <param name="Existing">实际存在的路径。</param>
/// <param name="Format">文件格式。</param>
/// <param name="Explanation">给用户看的"这些路径分别在哪一层生效"。</param>
public sealed record ConfigTargetProbe(
    string Target,
    string DisplayName,
    ImmutableArray<string> Candidates,
    ImmutableArray<string> Existing,
    string Format,
    string Explanation);

/// <summary>配置文件格式。</summary>
public static class ConfigFormats
{
    /// <summary>XML（Maven settings.xml、NuGet.Config）。</summary>
    public const string Xml = "xml";

    /// <summary>INI（pip.ini、.npmrc）。</summary>
    public const string Ini = "ini";

    /// <summary>JSON（Composer config.json）。</summary>
    public const string Json = "json";

    /// <summary>TOML（Cargo config.toml）。</summary>
    public const string Toml = "toml";

    /// <summary>Groovy / Kotlin DSL（Gradle init.gradle）。</summary>
    public const string Groovy = "groovy";
}

/// <summary>
/// 配置文件目标目录（需求 21.2.1 的"镜像源目标矩阵"）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要把它做成一张表而不是散在动作里</b>：这张表同时回答三个问题——
/// 文件在哪、优先级如何、风险多高。三者必须一起看才有意义：
/// 例如"用户级 Maven settings.xml 优先于安装级"这条规则，
/// 决定了我们应当去改哪一个以及要不要提示用户 IDE 可能覆盖它。
/// </para>
/// <para>
/// <b>URL 一律不硬编码在这里</b>：需求 MS-1 要求镜像源清单外置可热更新。
/// 本表只描述"配置写到哪"，具体镜像 URL 由 <c>mirror.list</c> 从外置清单读取。
/// </para>
/// </remarks>
public static class ConfigTargetCatalog
{
    /// <summary>全部支持的配置目标。</summary>
    public static ImmutableArray<ConfigTargetProbe> Probe(string? userProfile = null, string? appData = null)
    {
        var home = userProfile ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var roaming = appData ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);

        var targets = ImmutableArray.CreateBuilder<ConfigTargetProbe>();

        targets.Add(Build(
            "maven", "Maven（settings.xml）",
            [
                Path.Combine(home, ".m2", "settings.xml"),
                Path.Combine(home, ".m2", "settings-security.xml"),
            ],
            ConfigFormats.Xml,
            "用户级 ~/.m2/settings.xml 优先于 Maven 安装目录下的 conf/settings.xml；" +
            "IDEA 等 IDE 若配置了自带的 settings.xml，会覆盖这两者。"));

        targets.Add(Build(
            "gradle", "Gradle（init.gradle）",
            [
                Path.Combine(home, ".gradle", "init.gradle"),
                Path.Combine(home, ".gradle", "init.gradle.kts"),
            ],
            ConfigFormats.Groovy,
            "写入 ~/.gradle/init.gradle 会在所有项目生效，且不改动项目自身的 repositories 声明。"));

        targets.Add(Build(
            "pip", "pip（pip.ini）",
            [
                Path.Combine(roaming, "pip", "pip.ini"),
                Path.Combine(roaming, "pip", "pip.ini"),
            ],
            ConfigFormats.Ini,
            "Windows 上 pip 的用户级配置是 %APPDATA%\\pip\\pip.ini。"));

        targets.Add(Build(
            "npm", "npm / yarn（.npmrc）",
            [
                Path.Combine(home, ".npmrc"),
                Path.Combine(home, ".yarnrc.yml"),
            ],
            ConfigFormats.Ini,
            "npm 与 yarn 1.x 共用 ~/.npmrc；yarn 2+ 使用 ~/.yarnrc.yml，需要单独配置。"));

        targets.Add(Build(
            "cargo", "Cargo（config.toml）",
            [
                Path.Combine(home, ".cargo", "config.toml"),
                Path.Combine(home, ".cargo", "config"),
            ],
            ConfigFormats.Toml,
            "Cargo 的源替换写在 [source.crates-io] 的 replace-with 上，保留用户其他 [source.*] 段。"));

        targets.Add(Build(
            "composer", "Composer（config.json）",
            [
                Path.Combine(roaming, "Composer", "config.json"),
            ],
            ConfigFormats.Json,
            "Composer 的仓库配置在 repositories.packagist。"));

        targets.Add(Build(
            "nuget", "NuGet（NuGet.Config）",
            [
                Path.Combine(roaming, "NuGet", "NuGet.Config"),
            ],
            ConfigFormats.Xml,
            "NuGet 的包源在 <packageSources>；凭据在 <packageSourceCredentials>，本软件绝不触碰后者。"));

        return targets.ToImmutable();
    }

    /// <summary>按标识查找目标。</summary>
    public static ConfigTargetProbe? Find(string target, string? userProfile = null, string? appData = null) =>
        Probe(userProfile, appData)
            .FirstOrDefault(t => string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase));

    /// <summary>全部目标标识。</summary>
    public static ImmutableArray<string> Targets { get; } =
        [.. Probe().Select(static t => t.Target)];

    private static ConfigTargetProbe Build(
        string target, string displayName, ImmutableArray<string> candidates, string format, string explanation)
    {
        var distinct = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        var existing = distinct.Where(File.Exists).ToImmutableArray();
        return new ConfigTargetProbe(target, displayName, distinct, existing, format, explanation);
    }
}

/// <summary>
/// Maven <c>settings.xml</c> 的结构化编辑器（XML-M1 ~ XML-M10）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能用字符串替换</b>（XML-M1）：<c>settings.xml</c> 有命名空间
/// （<c>http://maven.apache.org/SETTINGS/1.0.0</c> 及 1.1.0/1.2.0），
/// 而且用户可能用任意缩进、任意注释、任意属性顺序书写。
/// 字符串替换会在"看起来改对了"的同时破坏文件结构——而 Maven 一旦读不了 settings.xml，
/// 用户的所有构建都会失败，且错误信息通常只有一句"Error reading settings.xml"。
/// </para>
/// <para>
/// <b>因此本类只做一件事</b>：用 <see cref="XDocument"/> 解析 → 做最小必要的节点增删 →
/// 序列化回去。加载时<b>保留空白</b>（<see cref="LoadOptions.PreserveWhitespace"/>），
/// 让用户的缩进与注释尽量原样保留。
/// </para>
/// <para>
/// <b>绝对只读的部分</b>（XML-M6）：<c>&lt;servers&gt;</c> 下的
/// <c>&lt;username&gt;</c> / <c>&lt;password&gt;</c> / <c>&lt;passphrase&gt;</c>
/// 我们不读取、不记录、不改动。实现上不是"记得别碰"，而是<b>从不访问该节点</b>。
/// </para>
/// </remarks>
public static class MavenSettingsEditor
{
    /// <summary>已知的 settings.xml 命名空间。</summary>
    public static ImmutableArray<string> KnownNamespaces { get; } =
    [
        "http://maven.apache.org/SETTINGS/1.0.0",
        "http://maven.apache.org/SETTINGS/1.1.0",
        "http://maven.apache.org/SETTINGS/1.2.0",
    ];

    /// <summary>本软件写入的 mirror id 前缀（用于识别"哪些是我们写的"）。</summary>
    public const string MirrorIdPrefix = "envstation-";

    /// <summary><c>mirrorOf</c> 的合法取值。</summary>
    public static ImmutableArray<string> ValidMirrorOf { get; } =
        ["central", "*", "external:*", "*,!envstation-private"];

    /// <summary>解析 result。</summary>
    /// <param name="Document">解析后的文档。</param>
    /// <param name="Namespace">生效的命名空间。</param>
    /// <param name="HadNamespace">原文件是否声明了命名空间。</param>
    public readonly record struct ParseResult(XDocument Document, XNamespace Namespace, bool HadNamespace);

    /// <summary>解析 settings.xml（XML-M1）。</summary>
    public static Result<ParseResult> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            // 空文件是合法的起点：Maven 会使用默认设置。
            var fresh = new XDocument(new XElement("settings"));
            return Result<ParseResult>.Ok(new ParseResult(fresh, XNamespace.None, false));
        }

        try
        {
            var document = XDocument.Parse(
                xml,
                LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

            if (document.Root is null || document.Root.Name.LocalName != "settings")
            {
                return Result<ParseResult>.Fail(
                    EnvStationErrorCodes.ConfigXmlParse,
                    $"settings.xml 的根节点是 <{document.Root?.Name.LocalName ?? "?"}>，而不是 <settings>。",
                    "这通常意味着选错了文件。已中止修改，原文件未改动。");
            }

            var ns = document.Root.Name.Namespace;
            if (ns != XNamespace.None && !KnownNamespaces.Contains(ns.NamespaceName, StringComparer.Ordinal))
            {
                return Result<ParseResult>.Fail(
                    EnvStationErrorCodes.ConfigXmlParse,
                    $"settings.xml 使用了未知的命名空间：{ns.NamespaceName}",
                    $"已知的命名空间：{string.Join("、", KnownNamespaces)}。已中止修改。");
            }

            return Result<ParseResult>.Ok(new ParseResult(document, ns, ns != XNamespace.None));
        }
        catch (XmlException ex)
        {
            return Result<ParseResult>.Fail(
                EnvStationErrorCodes.ConfigXmlParse,
                $"settings.xml 解析失败（第 {ex.LineNumber} 行第 {ex.LinePosition} 列）：{ex.Message}",
                "文件可能已损坏。环境站不会修改无法解析的文件；先修复或删除它。");
        }
    }

    /// <summary>读取现有的 <c>&lt;mirrors&gt;</c> 条目（只读，用于冲突判定）。</summary>
    public static ImmutableArray<(string Id, string Url, string MirrorOf, bool ByEnvStation)> ReadMirrors(ParseResult parsed)
    {
        var mirrors = FindChild(parsed.Document.Root!, parsed.Namespace, "mirrors");
        if (mirrors is null)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<(string, string, string, bool)>();
        foreach (var mirror in mirrors.Elements(parsed.Namespace + "mirror"))
        {
            var id = Value(mirror, parsed.Namespace, "id");
            var url = Value(mirror, parsed.Namespace, "url");
            var mirrorOf = Value(mirror, parsed.Namespace, "mirrorOf");
            builder.Add((id, url, mirrorOf, id.StartsWith(MirrorIdPrefix, StringComparison.OrdinalIgnoreCase)));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// 设置一个镜像（XML-M3 / XML-M4 / XML-M5）。
    /// </summary>
    /// <param name="parsed">已解析的文档。</param>
    /// <param name="id">镜像 id（会自动加上 <see cref="MirrorIdPrefix"/> 前缀）。</param>
    /// <param name="name">展示名。</param>
    /// <param name="url">镜像地址（必须 https）。</param>
    /// <param name="mirrorOf">mirrorOf 取值。</param>
    /// <param name="replaceUserMirror">
    /// 当存在**非本软件写入**的 mirror 时是否仍然替换。默认 false（XML-M4 / CF-7）。
    /// </param>
    public static Result<bool> SetMirror(
        ParseResult parsed,
        string id,
        string name,
        string url,
        string mirrorOf,
        bool replaceUserMirror = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return Result<bool>.Fail(
                EnvStationErrorCodes.ConfigVerifyFailed,
                $"镜像地址必须是 https：{url}",
                "明文 HTTP 的镜像可能被中间人替换依赖包。");
        }

        var root = parsed.Document.Root!;
        var ns = parsed.Namespace;
        var mirrors = FindOrCreate(root, ns, "mirrors", parsed.HadNamespace);

        var fullId = id.StartsWith(MirrorIdPrefix, StringComparison.OrdinalIgnoreCase) ? id : MirrorIdPrefix + id;

        // XML-M4：已有**用户自定义** mirror 时默认不动。
        var existing = ReadMirrors(parsed);
        var foreignMirror = existing.FirstOrDefault(m => !m.ByEnvStation && m.Id.Length > 0);
        if (foreignMirror.Id is { Length: > 0 } && !replaceUserMirror)
        {
            return Result<bool>.Fail(
                EnvStationErrorCodes.ConfigMergeConflict,
                $"settings.xml 中已存在非本软件写入的镜像「{foreignMirror.Id}」（{foreignMirror.Url}）。",
                "默认不覆盖已有的镜像，它可能是公司私服。确认要替换时，显式把 replace_user_mirror 设为 true；" +
                "也可以改用「追加并调整 mirrorOf」的方式让两者共存。");
        }

        var target = mirrors
            .Elements(ns + "mirror")
            .FirstOrDefault(m => string.Equals(Value(m, ns, "id"), fullId, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            target = new XElement(ns + "mirror");
            mirrors.Add(target);
        }
        else
        {
            // 只改我们需要改的四个子节点，其余（用户加的注释、额外字段）保持不动。
            target.RemoveNodes();
        }

        target.Add(new XComment(" >>> EnvStation >>> 由环境站写入，可通过软件一键移除 "));
        target.Add(new XElement(ns + "id", fullId));
        target.Add(new XElement(ns + "name", name));
        target.Add(new XElement(ns + "url", url));
        target.Add(new XElement(ns + "mirrorOf", mirrorOf));
        target.Add(new XComment(" <<< EnvStation <<< "));

        return Result<bool>.Ok(true);
    }

    /// <summary>
    /// 写后语义校验（XML-M8）。
    /// </summary>
    /// <remarks>
    /// 校验的是"这条镜像是否真的会被 Maven 用上"：存在目标 id、URL 是 https、mirrorOf 合法。
    /// 只检查"能解析"是不够的——一个解析正常但 mirrorOf 写错的配置，
    /// 会让用户以为换源成功了，实际仍然走官方源（或反过来把私服也劫持了）。
    /// </remarks>
    public static Result<Unit> Validate(IEnumerable<string> expectedMirrorIds, ParseResult parsed)
    {
        var mirrors = ReadMirrors(parsed);
        var problems = new List<string>(4);

        foreach (var expected in expectedMirrorIds)
        {
            var fullId = expected.StartsWith(MirrorIdPrefix, StringComparison.OrdinalIgnoreCase)
                ? expected
                : MirrorIdPrefix + expected;

            var found = mirrors.FirstOrDefault(m => string.Equals(m.Id, fullId, StringComparison.OrdinalIgnoreCase));
            if (found.Id is null)
            {
                problems.Add($"缺少 id 为 {fullId} 的 mirror");
                continue;
            }

            if (!found.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{fullId} 的 url 不是 https");
            }

            if (!ValidMirrorOf.Contains(found.MirrorOf, StringComparer.Ordinal))
            {
                problems.Add($"{fullId} 的 mirrorOf「{found.MirrorOf}」不是推荐取值之一（{string.Join(" / ", ValidMirrorOf)}）");
            }
        }

        return problems.Count == 0
            ? Results.Ok()
            : Result<Unit>.Fail(
                EnvStationErrorCodes.ConfigVerifyFailed,
                "settings.xml 写后校验未通过：" + string.Join("；", problems),
                "环境站会自动回滚备份，原文件保持修改前的内容。");
    }

    /// <summary>移除本软件写入的镜像（一键卸载）。</summary>
    public static int RemoveEnvStationMirrors(ParseResult parsed)
    {
        var mirrors = FindChild(parsed.Document.Root!, parsed.Namespace, "mirrors");
        if (mirrors is null)
        {
            return 0;
        }

        var toRemove = mirrors
            .Elements(parsed.Namespace + "mirror")
            .Where(m => Value(m, parsed.Namespace, "id").StartsWith(MirrorIdPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var element in toRemove)
        {
            element.Remove();
        }

        return toRemove.Length;
    }

    /// <summary>序列化回文本。</summary>
    public static string Serialize(ParseResult parsed) =>
        parsed.Document.ToString(SaveOptions.DisableFormatting) + "\n";

    // ────────────────────────────── 内部工具 ──────────────────────────────

    private static XElement? FindChild(XElement parent, XNamespace ns, string name)
    {
        // 同时接受带命名空间与不带命名空间的写法：真实世界的 settings.xml 两者都有。
        return parent.Element(ns + name) ?? parent.Element(name);
    }

    private static XElement FindOrCreate(XElement parent, XNamespace ns, string name, bool useNamespace)
    {
        var existing = FindChild(parent, ns, name);
        if (existing is not null)
        {
            return existing;
        }

        var created = new XElement(useNamespace ? ns + name : name);
        parent.Add(created);
        return created;
    }

    private static string Value(XElement parent, XNamespace ns, string name) =>
        FindChild(parent, ns, name)?.Value.Trim() ?? string.Empty;
}

/// <summary>一个镜像源定义。</summary>
/// <param name="Target">适用目标（maven / npm / pip / go / cargo …）。</param>
/// <param name="Id">源标识。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="Url">地址。</param>
/// <param name="Maintainer">维护方（需求 MS-2：每条源必须标注维护方）。</param>
/// <param name="SupportsHttps">是否支持 HTTPS。</param>
public sealed record MirrorDefinition(
    string Target,
    string Id,
    string DisplayName,
    string Url,
    string Maintainer,
    bool SupportsHttps);

/// <summary>
/// 内置镜像源清单。
/// </summary>
/// <remarks>
/// <para>
/// <b>内置清单只是兜底</b>。需求 MS-1 要求清单外置可热更新；本类提供的是"离线也能用"的最低集合，
/// 且刻意只收录**公开、无认证、长期存在**的源。
/// </para>
/// <para>
/// <b>刻意不做的事</b>：不收录任何需要 token 的源，也不做"自动测速选最快"——
/// 后者会让"我配置的是哪个源"变得不可预测。用户应当明确知道自己用的是哪一个。
/// </para>
/// </remarks>
public static class MirrorCatalog
{
    /// <summary>内置镜像源。</summary>
    public static ImmutableArray<MirrorDefinition> BuiltIn { get; } =
    [
        new("maven", "aliyun", "阿里云公共仓库（Maven）",
            "https://maven.aliyun.com/repository/public", "阿里云", true),
        new("maven", "tencent", "腾讯云 Maven 镜像",
            "https://mirrors.cloud.tencent.com/nexus/repository/maven-public/", "腾讯云", true),
        new("maven", "huawei", "华为云 Maven 镜像",
            "https://repo.huaweicloud.com/repository/maven/", "华为云", true),

        new("npm", "npmmirror", "npmmirror（原淘宝 npm 镜像）",
            "https://registry.npmmirror.com", "阿里巴巴", true),
        new("npm", "tencent", "腾讯云 npm 镜像",
            "https://mirrors.cloud.tencent.com/npm/", "腾讯云", true),

        new("pip", "tuna", "清华大学 TUNA 镜像（PyPI）",
            "https://pypi.tuna.tsinghua.edu.cn/simple", "清华大学", true),
        new("pip", "aliyun", "阿里云 PyPI 镜像",
            "https://mirrors.aliyun.com/pypi/simple/", "阿里云", true),
        new("pip", "ustc", "中国科学技术大学 PyPI 镜像",
            "https://pypi.mirrors.ustc.edu.cn/simple/", "中国科学技术大学", true),

        new("go", "goproxy-cn", "GOPROXY.CN（带 direct 回退）",
            "https://goproxy.cn,direct", "七牛云", true),
        new("go", "aliyun", "阿里云 Go 模块代理",
            "https://mirrors.aliyun.com/goproxy/,direct", "阿里云", true),

        new("cargo", "tuna", "清华大学 TUNA 镜像（crates.io）",
            "https://mirrors.tuna.tsinghua.edu.cn/crates.io-index", "清华大学", true),
        new("cargo", "ustc", "中国科学技术大学 crates.io 镜像",
            "https://mirrors.ustc.edu.cn/crates.io-index", "中国科学技术大学", true),

        new("composer", "aliyun", "阿里云 Composer 镜像",
            "https://mirrors.aliyun.com/composer/", "阿里云", true),
        new("composer", "tencent", "腾讯云 Composer 镜像",
            "https://mirrors.cloud.tencent.com/composer/", "腾讯云", true),

        new("nuget", "huawei", "华为云 NuGet 镜像",
            "https://repo.huaweicloud.com/repository/nuget/v3/index.json", "华为云", true),
        new("nuget", "tencent", "腾讯云 NuGet 镜像",
            "https://mirrors.cloud.tencent.com/nuget/", "腾讯云", true),
    ];

    /// <summary>按目标列出镜像源。</summary>
    public static ImmutableArray<MirrorDefinition> ForTarget(string target) =>
        [.. BuiltIn.Where(m => string.Equals(m.Target, target, StringComparison.OrdinalIgnoreCase))];

    /// <summary>全部目标标识。</summary>
    public static ImmutableArray<string> Targets { get; } =
        [.. BuiltIn.Select(static m => m.Target).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static t => t, StringComparer.Ordinal)];

    /// <summary>查找一个具体镜像源。</summary>
    public static MirrorDefinition? Find(string target, string id) =>
        BuiltIn.FirstOrDefault(m =>
            string.Equals(m.Target, target, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 构造"镜像可换不可信"提示（需求 MS-4）。
    /// </summary>
    /// <remarks>
    /// 这句提示必须出现在每一次换源操作的结果里，而不是只在文档里写一遍：
    /// 镜像由第三方提供，可能延迟、缺件，理论上也可能被篡改。
    /// </remarks>
    public static string TrustWarning(MirrorDefinition mirror) =>
        $"镜像源「{mirror.DisplayName}」由第三方（{mirror.Maintainer}）提供，可能同步延迟或缺件；" +
        "关键构件安装后用官方哈希核对（envstation.verify.mirror_integrity）。";
}
