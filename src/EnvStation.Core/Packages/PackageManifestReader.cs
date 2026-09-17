using System.Collections.Immutable;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Toml;

namespace EnvStation.Core.Packages;

/// <summary>TOML 节点到脚本值的桥接。保持 Abstractions 不依赖任何具体序列化格式。</summary>
internal static class TomlScriptValues
{
    internal static Result<ScriptValue> Convert(TomlValue node)
    {
        switch (node)
        {
            case TomlString s:
                return Result<ScriptValue>.Ok(new ScriptString(s.Value, s.Line));
            case TomlInteger i:
                return Result<ScriptValue>.Ok(new ScriptInteger(i.Value, i.Line));
            case TomlFloat f:
                return Result<ScriptValue>.Ok(new ScriptFloat(f.Value, f.Line));
            case TomlBoolean b:
                return Result<ScriptValue>.Ok(new ScriptBoolean(b.Value, b.Line));
            case TomlArray a:
                {
                    var items = ImmutableArray.CreateBuilder<ScriptValue>(a.Count);
                    foreach (var item in a)
                    {
                        var converted = Convert(item);
                        if (converted.IsFailure)
                        {
                            return converted.Propagate<ScriptValue>();
                        }

                        items.Add(converted.Value);
                    }

                    return Result<ScriptValue>.Ok(new ScriptArray(items.ToImmutable(), a.Line));
                }

            case TomlTable t:
                return Result<ScriptValue>.Fail(
                    EnvStationErrorCodes.PackageParseFailed,
                    $"第 {t.Line} 行：工作流参数不支持嵌套表。改用扁平参数，或把复杂数据放进 resources/ 目录。");

            case TomlDateTime d:
                return Result<ScriptValue>.Ok(new ScriptString(d.Raw, d.Line));

            default:
                return Result<ScriptValue>.Fail(
                    EnvStationErrorCodes.PackageParseFailed,
                    $"第 {node.Line} 行：无法识别的值类型 {node.TypeName}。");
        }
    }
}

/// <summary>
/// 包清单读取器：把 <c>envstation.toml</c> 解析为 <see cref="PackageManifest"/>，
/// 并同步完成结构与语义校验（需求 24.1 的 V1 层的一部分）。
/// </summary>
/// <remarks>
/// 设计纪律：读取器<b>不抛异常</b>，所有问题以 <see cref="Finding"/> 形式进入 <see cref="FindingBag"/>，
/// 且尽量一次报全部问题——用户改一次就能过，而不是逐个试错。
/// </remarks>
public static class PackageManifestReader
{
    /// <summary>
    /// 允许的能力档位。
    /// </summary>
    /// <remarks>
    /// 需求 20.3 的"脚本变体矩阵"把 T0 细分为 a~e 五个子档（说明型 / 探测型 / 配置型 / 安装型 / 全流程型），
    /// 而 25.2 的清单示例写的是 <c>tier = "T0"</c>。两者都要接受：
    /// 子档直接决定导入时的授权体验（T0-b 无需逐项授权，T0-d 要展示下载域名），
    /// 因此既有必要保留细粒度，也不能拒绝只写大档的包。
    /// </remarks>
    private static readonly ImmutableArray<string> ValidTiers =
        ["T0", "T0-a", "T0-b", "T0-c", "T0-d", "T0-e", "T1", "T2", "T3"];
    private static readonly ImmutableArray<string> ValidArchs = ["x64", "arm64", "x86"];

    /// <summary>
    /// 解析并校验包清单。
    /// </summary>
    /// <param name="tomlText">清单文件内容。</param>
    /// <param name="findings">发现收集器。</param>
    public static Result<PackageManifest> Read(string tomlText, FindingBag findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var parsed = TomlReader.Parse(tomlText);
        if (parsed.IsFailure)
        {
            findings.Block(
                EnvStationErrorCodes.PackageParseFailed,
                "清单无法解析",
                parsed.Error.Message,
                parsed.Error.Remediation,
                "envstation.toml");
            return parsed.Propagate<PackageManifest>();
        }

        var root = parsed.Value;
        var specVersion = RequireString(root, "spec_version", findings, "envstation.toml");
        var id = RequireString(root, "id", findings, "envstation.toml");
        var version = RequireString(root, "version", findings, "envstation.toml");
        var name = RequireString(root, "name", findings, "envstation.toml");
        var tier = RequireString(root, "tier", findings, "envstation.toml");

        if (specVersion is null || id is null || version is null || name is null || tier is null)
        {
            return Result<PackageManifest>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                "清单缺少必需字段，详见校验发现列表。");
        }

        CheckSpecVersion(specVersion, findings);
        CheckIdNamespace(id, findings);
        CheckSemanticVersion(version, "version", findings);
        CheckTier(tier, findings);

        var author = ReadAuthor(root, findings);
        var requirements = ReadRequirements(root, findings);
        var runtime = ReadRuntime(root);
        var permissions = ReadPermissions(root, findings);
        var hosts = ReadAllowedHosts(root, findings);
        var sideEffects = ReadSideEffects(root, findings);
        var quality = ReadQuality(root, findings);

        CheckNetworkDeclaration(permissions, hosts, findings);

        var manifest = new PackageManifest
        {
            SpecVersion = specVersion,
            Id = id,
            Version = version,
            Name = name,
            Description = root.GetString("description"),
            Author = author,
            License = root.GetString("license"),
            Tier = tier,
            Requirements = requirements,
            Runtime = runtime,
            Permissions = permissions,
            AllowedHosts = hosts,
            SideEffects = sideEffects,
            Quality = quality,
            Line = root.Line,
        };

        CheckKnownFields(root, findings);

        if (findings.HasBlockers)
        {
            return Result<PackageManifest>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                $"清单存在 {findings.BlockCount} 项阻断问题，详见校验发现列表。");
        }

        return Result<PackageManifest>.Ok(manifest);
    }

    // ────────────────────────────── 各字段读取 ──────────────────────────────

    private static PackageAuthor? ReadAuthor(TomlTable root, FindingBag findings)
    {
        if (root.Get("author") is not TomlTable table)
        {
            if (root.ContainsKey("author"))
            {
                findings.Block(
                    "MAN-01",
                    "author 类型错误",
                    "author 必须是内联表，例如 author = { name = 「张三」, id = 「zhangsan」 }。",
                    "把 author 改为内联表写法。",
                    "envstation.toml");
            }

            return null;
        }

        var authorName = table.GetString("name");
        if (string.IsNullOrWhiteSpace(authorName))
        {
            findings.Block(
                "MAN-02",
                "缺少作者名",
                $"第 {table.Line} 行：author.name 为空。",
                "填写 author.name。",
                $"envstation.toml:{table.Line}");
        }

        return new PackageAuthor(
            authorName ?? string.Empty,
            table.GetString("id"),
            table.GetString("contact"));
    }

    private static PackageRequirements? ReadRequirements(TomlTable root, FindingBag findings)
    {
        if (root.Get("requirements") is not TomlTable table)
        {
            return null;
        }

        var os = table.GetString("os");
        if (os is not null && !os.StartsWith('>') && !os.StartsWith('<') && !os.StartsWith('=') && os != "*")
        {
            findings.Warn(
                "MAN-03",
                "系统版本约束写法可疑",
                $"第 {table.Line} 行：requirements.os 的值 {os} 不是比较表达式。",
                "改为比较表达式，例如 >=10.0.17763（Windows 10 1809）。",
                $"envstation.toml:{table.Line}");
        }

        ImmutableArray<string> arch = ImmutableArray<string>.Empty;
        if (table.Get("arch") is TomlArray archArray)
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            foreach (var item in archArray)
            {
                if (item is not TomlString s)
                {
                    findings.Block(
                        "MAN-04",
                        "arch 元素非字符串",
                        $"第 {item.Line} 行：arch 数组只能包含字符串。",
                        "示例：arch = [「x64」,「arm64」]。",
                        "envstation.toml");
                    continue;
                }

                if (!ValidArchs.Contains(s.Value, StringComparer.Ordinal))
                {
                    findings.Block(
                        "MAN-05",
                        "未知的 CPU 架构",
                        $"第 {s.Line} 行：arch 值 {s.Value} 不在支持列表内。",
                        $"支持的值：{string.Join(" / ", ValidArchs)}。",
                        "envstation.toml");
                    continue;
                }

                builder.Add(s.Value);
            }

            arch = builder.ToImmutable();
        }

        var disk = table.GetInteger("disk_bytes");
        if (disk is < 0)
        {
            findings.Block(
                "MAN-06",
                "disk_bytes 为负数",
                $"第 {table.Line} 行：所需磁盘空间是 {disk}，不能为负数。",
                "填写正整数，支持下划线分隔，例如 1_500_000_000。",
                $"envstation.toml:{table.Line}");
            disk = null;
        }

        return new PackageRequirements(os, arch, disk);
    }

    private static PackageRuntimeTarget? ReadRuntime(TomlTable root)
    {
        if (root.Get("runtime") is not TomlTable table)
        {
            return null;
        }

        return new PackageRuntimeTarget(table.GetString("kind"), table.GetString("version"));
    }

    private static ImmutableDictionary<string, string> ReadPermissions(TomlTable root, FindingBag findings)
    {
        if (root.Get("permissions") is not TomlTable table)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in table.Entries)
        {
            if (!Capabilities.IsKnown(key))
            {
                findings.Block(
                    EnvStationErrorCodes.PackageUnknownCapability,
                    "声明了未知能力",
                    $"第 {value.Line} 行：{key} 不是官方定义的能力。",
                    "删除该声明。可用能力 ID 见动作文档中的能力清单。",
                    $"envstation.toml:{value.Line}",
                    key);
                continue;
            }

            if (value is not TomlString s || string.IsNullOrWhiteSpace(s.Value))
            {
                findings.Block(
                    "MAN-07",
                    "能力说明缺失",
                    $"第 {value.Line} 行：{key} 必须附带一句自然语言说明。",
                    "[permissions] 中每个能力都要写一句说明，例如 CAP.ENV.USER 写「在用户确认后写入用户级环境变量，写入前自动创建快照」。",
                    $"envstation.toml:{value.Line}",
                    key);
                continue;
            }

            builder[key] = s.Value;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<string> ReadAllowedHosts(TomlTable root, FindingBag findings)
    {
        if (root.Get("network") is not TomlTable table)
        {
            return ImmutableArray<string>.Empty;
        }

        if (table.Get("allow") is not TomlArray allow)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var item in allow)
        {
            if (item is not TomlString s)
            {
                findings.Block(
                    "MAN-08",
                    "network.allow 元素非字符串",
                    $"第 {item.Line} 行：域名白名单只能包含字符串。",
                    "示例：allow = [「www.python.org」,「mirrors.aliyun.com」]。",
                    "envstation.toml");
                continue;
            }

            var host = s.Value.Trim();
            if (host.Length == 0)
            {
                continue;
            }

            if (host.Contains("://", StringComparison.Ordinal) || host.Contains('/', StringComparison.Ordinal))
            {
                findings.Block(
                    "MAN-09",
                    "域名白名单写法错误",
                    $"第 {s.Line} 行：{host} 看起来是 URL 而不是域名。",
                    "白名单只写主机名，例如 mirrors.aliyun.com。协议与路径由动作参数决定。",
                    "envstation.toml");
                continue;
            }

            if (host.Contains('*', StringComparison.Ordinal))
            {
                findings.Block(
                    "MAN-10",
                    "域名白名单不允许通配符",
                    $"第 {s.Line} 行：{host} 含通配符。",
                    "逐个列出会访问的域名，不用通配符。",
                    "envstation.toml");
                continue;
            }

            builder.Add(host);
        }

        return builder.ToImmutable();
    }

    private static PackageSideEffects? ReadSideEffects(TomlTable root, FindingBag findings)
    {
        if (root.Get("side_effects") is not TomlTable table)
        {
            return null;
        }

        ImmutableArray<string> configs = ImmutableArray<string>.Empty;
        if (table.Get("modifies_config") is TomlArray arr)
        {
            configs = [.. arr.OfType<TomlString>().Select(static s => s.Value)];
            if (configs.Length != arr.Count)
            {
                findings.Warn(
                    "MAN-11",
                    "modifies_config 含非字符串元素",
                    "已忽略其中的非字符串元素。",
                    "modifies_config 只接受字符串数组。",
                    $"envstation.toml:{arr.Line}");
            }
        }

        return new PackageSideEffects(
            table.GetBoolean("writes_env") ?? false,
            table.GetBoolean("writes_files") ?? false,
            configs,
            table.GetBoolean("irreversible") ?? false,
            table.GetString("reversible_by"));
    }

    private static PackageQuality? ReadQuality(TomlTable root, FindingBag findings)
    {
        if (root.Get("quality") is not TomlTable table)
        {
            return null;
        }

        var readme = table.GetString("readme");
        if (readme is not null && (readme.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(readme)))
        {
            findings.Block(
                "MAN-12",
                "readme 路径越界",
                $"第 {table.Line} 行：readme 必须是包内相对路径，不能包含上级目录跳转或使用绝对路径。",
                "示例：readme = 「README.md」。",
                "envstation.toml");
            readme = null;
        }

        return new PackageQuality(
            table.GetBoolean("has_tests") ?? false,
            table.GetBoolean("has_uninstall") ?? false,
            readme);
    }

    // ────────────────────────────── 语义检查 ──────────────────────────────

    private static void CheckSpecVersion(string specVersion, FindingBag findings)
    {
        if (!TryParseMajor(specVersion, out var major))
        {
            findings.Block(
                "STD-01",
                "spec_version 格式非法",
                $"spec_version 的值 {specVersion} 不是 <主版本>.<次版本> 形式。",
                $"把 spec_version 改为 {PackageManifest.CurrentSpecVersion}。",
                "envstation.toml");
            return;
        }

        // 当前客户端的标准版本由常量保证合法，因此这里刻意丢弃返回值（CA1806 要求显式表达该意图）。
        _ = TryParseMajor(PackageManifest.CurrentSpecVersion, out var currentMajor);
        if (major > currentMajor)
        {
            findings.Warn(
                "STD-02",
                "包使用了更新的标准版本",
                $"包声明的 spec_version 是 {specVersion}，高于当前客户端支持的 {PackageManifest.CurrentSpecVersion}。",
                "升级环境站客户端后重新校验。未升级时部分字段会被忽略。",
                "envstation.toml");
        }
        else if (currentMajor - major > PackageManifest.BackwardCompatibleMajorVersions)
        {
            findings.Block(
                EnvStationErrorCodes.PackageSpecIncompatible,
                "包标准版本过旧",
                $"包声明的 spec_version 是 {specVersion}，低于客户端支持范围。",
                $"客户端至少向前兼容 {PackageManifest.BackwardCompatibleMajorVersions} 个大版本。让包作者运行 envstation migrate 升级包定义。",
                "envstation.toml");
        }
    }

    private static bool TryParseMajor(string version, out int major)
    {
        major = 0;
        var dot = version.IndexOf('.', StringComparison.Ordinal);
        var head = dot < 0 ? version : version[..dot];
        return int.TryParse(head, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out major)
            && major >= 0;
    }

    private static void CheckIdNamespace(string id, FindingBag findings)
    {
        if (id.StartsWith(ActionReference.OfficialPrefix, StringComparison.Ordinal)
            || id.StartsWith(ActionReference.ThirdPartyPrefix, StringComparison.Ordinal))
        {
            return;
        }

        findings.Block(
            EnvStationErrorCodes.PackageNamespaceViolation,
            "包 ID 命名空间非法",
            $"包 ID {id} 未使用合法前缀。",
            "官方包用 envstation.<名称>，第三方包用 x-<作者ID>.<名称>。",
            "envstation.toml");
    }

    private static void CheckSemanticVersion(string version, string field, FindingBag findings)
    {
        var parts = version.Split('-')[0].Split('+')[0].Split('.');
        var ok = parts.Length == 3
            && parts.All(static p => p.Length > 0 && p.All(char.IsAsciiDigit));
        if (!ok)
        {
            findings.Block(
                "MAN-13",
                $"{field} 不是语义化版本",
                $"{field} 的值 {version} 不符合 <主>.<次>.<修订> 格式。",
                "改为 <主>.<次>.<修订> 形式，例如 1.2.0。不要使用 latest 或 *。",
                "envstation.toml");
        }
    }

    private static void CheckTier(string tier, FindingBag findings)
    {
        if (!ValidTiers.Contains(tier, StringComparer.Ordinal))
        {
            findings.Block(
                "MAN-14",
                "未知的能力档位",
                $"tier 的值 {tier} 不在支持列表内。",
                $"支持的值：{string.Join(" / ", ValidTiers)}。",
                "envstation.toml");
        }
    }

    private static void CheckNetworkDeclaration(
        ImmutableDictionary<string, string> permissions,
        ImmutableArray<string> hosts,
        FindingBag findings)
    {
        if (permissions.ContainsKey(CapabilityIds.NetDownload) && hosts.Length == 0)
        {
            findings.Block(
                "SEC-NET-01",
                "下载能力缺少域名白名单",
                "包声明了 CAP.NET.DOWNLOAD，但 [network].allow 为空。",
                "在 [network].allow 中列出全部会访问的域名，否则下载动作不会被执行。",
                "envstation.toml",
                CapabilityIds.NetDownload);
        }
    }

    private static void CheckKnownFields(TomlTable root, FindingBag findings)
    {
        string[] known =
        [
            "spec_version", "id", "version", "name", "description", "author", "license", "tier",
            "requirements", "runtime", "permissions", "network", "side_effects", "quality",
        ];

        foreach (var key in root.Keys)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                findings.Warn(
                    "STD-03",
                    "清单包含未知字段",
                    $"envstation.toml 中的字段 {key} 不被当前客户端识别。",
                    "可能是拼写错误，或来自更新的标准版本。该字段会被忽略。",
                    "envstation.toml",
                    key);
            }
        }
    }

    private static string? RequireString(TomlTable table, string key, FindingBag findings, string file)
    {
        var node = table.Get(key);
        if (node is null)
        {
            findings.Block(
                EnvStationErrorCodes.PackageManifestInvalid,
                $"缺少必需字段 {key}",
                $"{file} 中未找到 {key}。",
                $"补齐 {key} 后重新导入。",
                file,
                key);
            return null;
        }

        if (node is not TomlString s || string.IsNullOrWhiteSpace(s.Value))
        {
            findings.Block(
                EnvStationErrorCodes.PackageManifestInvalid,
                $"字段 {key} 不是非空字符串",
                $"第 {node.Line} 行：{key} 的类型是 {node.TypeName}。",
                "该字段必须是带引号的非空字符串。",
                $"{file}:{node.Line}",
                key);
            return null;
        }

        return s.Value;
    }
}
