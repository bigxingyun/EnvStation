using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Core.Configuration;

namespace EnvStation.Core.Actions.Builtin;

// ══════════════════════════════════════════════════════════════════════════════
//  A08 配置与镜像源（15 个动作） · 以及 A09 的 2 个镜像验证动作
//
//  这一组是"用户日常最痛"的一块：国内开发者几乎都要换镜像源，而换源这件事
//  在手工操作下极易造成两种后果——**毁掉用户已有的私服配置**，或者**以为换上了其实没生效**。
//
//  对应的两条纪律：
//    · 先备份、后修改、合并优先于覆盖、写后校验失败自动还原（CF-1~CF-5）；
//    · 「换上了」必须由 verify.mirror_effective 真的去拉一次来证明，而不是读一下配置文件就算数。
//
//  另外一条容易被忽略的：**凭据永不进入日志与报告**（CF-6）。
//  Maven 的 <servers>、npm 的 _authToken 都在这类文件里，因此本组所有输出都经过脱敏。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>配置类动作的公共基类。</summary>
internal abstract class ConfigActionBase : ActionBase
{
    /// <summary>取得配置存储（备份根位于第一个授权根目录下）。</summary>
    /// <remarks>
    /// 备份刻意放在**授权根目录之内**：这样"能不能写备份"与"能不能改目标文件"
    /// 受同一套授权约束，不会出现"目标不可写但备份写到了别处"这种半吊子状态。
    /// </remarks>
    protected static ConfigFileStore StoreFor(ActionExecutionContext context)
    {
        var root = context.AuthorizedRoots.Length > 0
            ? context.AuthorizedRoots[0]
            : Path.GetTempPath();

        return new ConfigFileStore(Path.Combine(root, "config-backups"));
    }

    /// <summary>解析并守卫一个配置文件路径。</summary>
    protected static Result<string> GuardConfigPath(ActionExecutionContext context, string raw, string parameterName) =>
        context.GuardPath(raw, parameterName);

    /// <summary>把文本按格式做语法校验；返回失败时附带定位信息。</summary>
    protected static Result<Unit> ValidateSyntax(string content, string format)
    {
        switch (format)
        {
            case ConfigFormats.Xml:
                {
                    var parsed = MavenSettingsEditor.Parse(content);
                    return parsed.IsFailure
                        ? Result<Unit>.Fail(parsed.Error)
                        : Results.Ok();
                }

            case ConfigFormats.Json:
                try
                {
                    using var _ = System.Text.Json.JsonDocument.Parse(content);
                    return Results.Ok();
                }
                catch (System.Text.Json.JsonException ex)
                {
                    return Result<Unit>.Fail(
                        EnvStationErrorCodes.ConfigVerifyFailed,
                        $"JSON 解析失败（偏移 {ex.BytePositionInLine}）：{ex.Message}");
                }

            case ConfigFormats.Toml:
                {
                    var parsed = Toml.TomlReader.Parse(content);
                    return parsed.IsFailure
                        ? Result<Unit>.Fail(parsed.Error)
                        : Results.Ok();
                }

            case ConfigFormats.Ini:
                {
                    // INI 没有统一标准，这里只做最基本的形态检查：
                    // 非注释、非空行必须含 = 或 :。这足以挡住"模板渲染把整行吃掉了"这类错误。
                    var lineNumber = 0;
                    foreach (var raw in content.Split('\n'))
                    {
                        lineNumber++;
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
                        {
                            continue;
                        }

                        if (!line.Contains('=', StringComparison.Ordinal) && !line.Contains(':', StringComparison.Ordinal))
                        {
                            return Result<Unit>.Fail(
                                EnvStationErrorCodes.ConfigVerifyFailed,
                                $"INI 第 {lineNumber} 行既不是注释、也不是节、也不含 = 或 ：——{line}");
                        }
                    }

                    return Results.Ok();
                }

            default:
                // groovy 等无法在不执行代码的前提下校验，因此只做空文件检查。
                return string.IsNullOrWhiteSpace(content)
                    ? Result<Unit>.Fail(EnvStationErrorCodes.ConfigVerifyFailed, "内容为空。")
                    : Results.Ok();
        }
    }

    /// <summary>
    /// 统一的"备份 → 写入 → 校验 → 失败自动还原"流程（CF-1 / CF-5）。
    /// </summary>
    /// <remarks>
    /// 把它做成一个方法而不是在每个动作里重复，是因为这四步的**顺序与失败处理**才是关键：
    /// 少任何一步，或者在校验失败时忘了还原，用户就会留下一个"改坏了且没备份可用"的配置。
    /// </remarks>
    protected static async ValueTask<ActionResult> ApplyConfigChangeAsync(
        ActionExecutionContext context,
        string targetPath,
        string newContent,
        string format,
        string markerId,
        string summary,
        CancellationToken cancellationToken)
    {
        var store = StoreFor(context);

        var backup = await store.BackupAsync(targetPath, summary, cancellationToken).ConfigureAwait(false);
        if (backup.IsFailure)
        {
            return ActionResult.Fail(
                backup.Error.Code,
                "无法创建备份，已放弃修改：" + backup.Error.Message);
        }

        var verify = ValidateSyntax(newContent, format);
        if (verify.IsFailure)
        {
            return ActionResult.Fail(
                verify.Error.Code,
                "写入前校验未通过，已放弃修改：" + verify.Error.Message);
        }

        try
        {
            await ConfigFileStore.WriteAtomicAsync(targetPath, newContent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ActionResult.Fail(EnvStationErrorCodes.PathNotWritable, $"写入失败：{ex.Message}");
        }

        // 写后复读校验：确认落盘内容能被解析，且确实是我们要写的那份。
        // 单靠"写入没抛异常"是不够的——磁盘满、杀毒软件改写、编码问题都可能让落盘内容与预期不同。
        string written;
        try
        {
            written = await File.ReadAllTextAsync(targetPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ConfigFileStore.RestoreAsync(backup.Value, cancellationToken).ConfigureAwait(false);
            return ActionResult.Fail(
                EnvStationErrorCodes.ConfigVerifyFailed,
                $"写后重读失败，已回滚备份：{ex.Message}");
        }

        var postVerify = ValidateSyntax(written, format);
        if (postVerify.IsFailure || !string.Equals(written, newContent, StringComparison.Ordinal))
        {
            var restore = await ConfigFileStore.RestoreAsync(backup.Value, cancellationToken).ConfigureAwait(false);

            return ActionResult.Fail(
                EnvStationErrorCodes.ConfigVerifyFailed,
                $"写后校验未通过，已{(restore.IsSuccess ? "自动回滚" : "尝试回滚但失败")}：" +
                (postVerify.IsFailure ? postVerify.Error.Message : "落盘内容与预期不一致") +
                (restore.IsSuccess
                    ? " 原文件已回到修改前的内容。"
                    : $" 回滚失败，需手动核对 {targetPath}（备份 {backup.Value.BackupId}）。"));
        }

        var secrets = ConfigFileStore.FindSecretFields(written);

        return ActionResult.Ok(
            summary,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // changed 必须始终存在：调用方与包作者会用 ${x.changed} 判断"到底改没改"。
                // 缺这个键会让表达式在运行期报"变量未定义"——这是一次真实缺陷（CF-29 暴露的）。
                ["changed"] = "true",
                ["path"] = targetPath,
                ["backup_id"] = backup.Value.BackupId,
                ["content_hash"] = ConfigFileStore.HashText(written),
                ["bytes"] = Encoding.UTF8.GetByteCount(written).ToString(CultureInfo.InvariantCulture),
                ["marker_id"] = markerId,
                ["contains_credentials"] = secrets.Length > 0 ? "true" : "false",
                ["credential_fields"] = string.Join(",", secrets),
            },
            touchedPaths: [targetPath],
            reversibleToken: backup.Value.BackupId);
    }

    /// <summary>读取文件并脱敏（供返回给用户看的输出使用）。</summary>
    protected static string ReadRedacted(string path)
    {
        try
        {
            return ConfigFileStore.RedactSecrets(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（读取失败：{ex.Message}）";
        }
    }
}

/// <summary><c>envstation.config.detect</c>：探测某目标的配置文件位置与当前配置。</summary>
internal sealed class ConfigDetectAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.config.detect",
        "1.0.0",
        CapabilityIds.Inspect,
        "检测配置文件位置",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["filesystem.read"],
        Parameters:
        [
            new ParameterSpec("target", ParameterType.Enum, true, "要检测的配置目标",
                AllowedValues: ConfigTargetCatalog.Targets.Add("all")),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var target = arguments.GetString("target")!;
        var probes = target == "all"
            ? ConfigTargetCatalog.Probe()
            : [.. ConfigTargetCatalog.Probe().Where(t => string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase))];

        if (probes.Length == 0)
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"未知的配置目标 {target}。可用：{string.Join("、", ConfigTargetCatalog.Targets)}。");
        }

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = ImmutableArray.CreateBuilder<string>();

        foreach (var probe in probes)
        {
            outputs[$"{probe.Target}.format"] = probe.Format;
            outputs[$"{probe.Target}.explanation"] = probe.Explanation;
            outputs[$"{probe.Target}.existing"] = string.Join("|", probe.Existing);
            outputs[$"{probe.Target}.candidates"] = string.Join("|", probe.Candidates);

            lines.Add(probe.Existing.Length == 0
                ? $"{probe.DisplayName}：尚未创建（候选路径 {probe.Candidates.Length} 个）"
                : $"{probe.DisplayName}：{probe.Existing[0]}");
        }

        return Ok(
            string.Join("；", lines),
            outputs);
    }
}

/// <summary><c>envstation.config.backup</c>：备份配置文件。</summary>
internal sealed class ConfigBackupAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.config.backup",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "备份配置文件",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("path", true, "要备份的配置文件路径"),
            Str("note", false, "备注", maxLength: 200),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var guard = GuardConfigPath(context, arguments.GetString("path")!, "path");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var store = StoreFor(context);
        var backup = await store
            .BackupAsync(guard.Value, arguments.GetString("note"), cancellationToken)
            .ConfigureAwait(false);

        if (backup.IsFailure)
        {
            return FailResult(backup.Error.Code, backup.Error.Message, backup.Error.Remediation);
        }

        var record = backup.Value;
        return OkResult(
            record.Existed
                ? $"已备份 {record.OriginalPath}（{record.BackupId}）。"
                : $"{record.OriginalPath} 当前不存在，已记录该状态（{record.BackupId}）；回滚时将删除该文件。",
            Outputs(
                ("backup_id", record.BackupId),
                ("path", record.OriginalPath),
                ("existed", Bool(record.Existed)),
                ("content_hash", record.ContentHash)),
            touched: [record.BackupPath],
            reversibleToken: record.BackupId);
    }
}

/// <summary><c>envstation.config.verify_syntax</c>：写后校验语法。</summary>
internal sealed class ConfigVerifySyntaxAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.config.verify_syntax",
        "1.0.0",
        CapabilityIds.Inspect,
        "校验配置文件语法",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["filesystem.read"],
        Parameters:
        [
            Path_("path", true, "要校验的配置文件"),
            new ParameterSpec("format", ParameterType.Enum, false, "文件格式；省略时按扩展名推断",
                AllowedValues: [ConfigFormats.Xml, ConfigFormats.Ini, ConfigFormats.Json, ConfigFormats.Toml, ConfigFormats.Groovy]),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var guard = GuardConfigPath(context, arguments.GetString("path")!, "path");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var path = guard.Value;
        if (!File.Exists(path))
        {
            return FailResult(EnvStationErrorCodes.AssertFailed, $"文件不存在：{path}");
        }

        var format = arguments.GetString("format") ?? GuessFormat(path);
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var valid = ValidateSyntax(content, format);

        var secrets = ConfigFileStore.FindSecretFields(content);

        if (valid.IsFailure)
        {
            // 失败信息里绝不能带原文——它可能含凭据。
            return FailResult(
                valid.Error.Code,
                $"{path} 语法校验未通过：{valid.Error.Message}",
                "已中止后续修改。该文件保持原样，未被改动。");
        }

        return OkResult(
            $"{path} 语法有效（{format}）" +
            (secrets.Length > 0 ? $"；检测到 {secrets.Length} 个凭据字段（内容已脱敏，不会出现在日志中）。" : "。"),
            Outputs(
                ("path", path),
                ("format", format),
                ("valid", "true"),
                ("contains_credentials", Bool(secrets.Length > 0)),
                ("credential_fields", string.Join(",", secrets))));
    }

    private static string GuessFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xml" or ".config" => ConfigFormats.Xml,
        ".json" => ConfigFormats.Json,
        ".toml" => ConfigFormats.Toml,
        ".gradle" or ".kts" => ConfigFormats.Groovy,
        _ => ConfigFormats.Ini,
    };
}

/// <summary><c>envstation.config.set_kv</c>：以结构化方式设置白名单配置文件中的键值。</summary>
/// <remarks>
/// 支持 INI / TOML / XML 三种载体的"键值"语义。<b>刻意不支持 JSON 的任意路径写入</b>：
/// JSON 的嵌套结构用"点号路径"表达会带来歧义（键名本身含点怎么办），
/// 而需要写 JSON 的场景（Composer）都有专门的镜像动作。
/// </remarks>
internal sealed class ConfigSetKeyValueAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.config.set_kv",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "设置配置项",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("path", true, "配置文件路径"),
            Str("key", true, "键名：INI/TOML 用 section.key 形式，XML 用 父节点/子节点 形式", maxLength: 200),
            Str("value", true, "值", maxLength: 4096),
            new ParameterSpec("format", ParameterType.Enum, false, "文件格式；省略时按扩展名推断",
                AllowedValues: [ConfigFormats.Ini, ConfigFormats.Toml, ConfigFormats.Xml]),
            Bool("dry_run", "只报告将要做的修改", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var guard = GuardConfigPath(context, arguments.GetString("path")!, "path");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var path = guard.Value;
        var key = arguments.GetString("key")!;
        var value = arguments.GetString("value")!;
        var format = arguments.GetString("format") ?? GuessFormat(path);
        var dryRun = arguments.GetBoolean("dry_run", false);

        var original = File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var updated = format switch
        {
            ConfigFormats.Ini => SetIniKey(original, key, value),
            ConfigFormats.Toml => SetTomlKey(original, key, value),
            ConfigFormats.Xml => SetXmlValue(original, key, value),
            _ => Result<string>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"config.set_kv 不支持格式 {format}。",
                "INI / TOML / XML 受支持；JSON 改用对应目标的专用动作。"),
        };

        if (updated.IsFailure)
        {
            return FailResult(updated.Error.Code, updated.Error.Message, updated.Error.Remediation);
        }

        if (string.Equals(original, updated.Value, StringComparison.Ordinal))
        {
            return OkResult(
                $"{key} 已经是目标值，无需修改。",
                Outputs(("changed", "false"), ("path", path), ("key", key)));
        }

        if (dryRun)
        {
            return OkResult(
                $"预演：将把 {path} 的 {key} 设为 {value}（未做任何修改）。",
                Outputs(("changed", "false"), ("dry_run", "true"), ("path", path), ("key", key)));
        }

        return await ApplyConfigChangeAsync(
            context, path, updated.Value, format, "set_kv",
            $"已把 {path} 的 {key} 设为 {value}。", cancellationToken).ConfigureAwait(false);
    }

    private static string GuessFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xml" => ConfigFormats.Xml,
        ".toml" => ConfigFormats.Toml,
        _ => ConfigFormats.Ini,
    };

    /// <summary>INI：按 <c>section.key = value</c> 定位；节不存在则创建。</summary>
    private static Result<string> SetIniKey(string content, string key, string value)
    {
        var dot = key.LastIndexOf('.');
        if (dot <= 0)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"INI 的键名必须写成 section.key 形式，实际为 {key}。");
        }

        var section = key[..dot];
        var name = key[(dot + 1)..];

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var sectionStart = -1;
        var sectionEnd = lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (sectionStart >= 0)
                {
                    sectionEnd = i;
                    break;
                }

                if (string.Equals(trimmed, $"[{section}]", StringComparison.OrdinalIgnoreCase))
                {
                    sectionStart = i;
                }
            }
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{section}]");
            lines.Add($"{name} = {value}");
            return Result<string>.Ok(string.Join('\n', lines));
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                var separator = trimmed.Contains('=', StringComparison.Ordinal) ? " = " : " : ";
                lines[i] = $"{name}{separator}{value}";
                return Result<string>.Ok(string.Join('\n', lines));
            }
        }

        lines.Insert(sectionEnd, $"{name} = {value}");
        return Result<string>.Ok(string.Join('\n', lines));
    }

    /// <summary>TOML：按 <c>table.key = value</c> 定位；表不存在则追加。</summary>
    private static Result<string> SetTomlKey(string content, string key, string value)
    {
        var parsed = Toml.TomlReader.Parse(content);
        if (parsed.IsFailure)
        {
            return Result<string>.Fail(
                parsed.Error.Code,
                $"TOML 解析失败，已放弃修改：{parsed.Error.Message}");
        }

        // 已经是要设的值 → 无变化。
        if (parsed.Value.GetPath(key) is Toml.TomlString existing && existing.Value == value)
        {
            return Result<string>.Ok(content);
        }

        var dot = key.LastIndexOf('.');
        var table = dot > 0 ? key[..dot] : string.Empty;
        var name = dot > 0 ? key[(dot + 1)..] : key;

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        if (table.Length == 0)
        {
            lines.Insert(0, $"{name} = \"{EscapeToml(value)}\"");
            return Result<string>.Ok(string.Join('\n', lines));
        }

        var header = $"[{table}]";
        var headerIndex = lines.FindIndex(l => string.Equals(l.Trim(), header, StringComparison.Ordinal));

        if (headerIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add(header);
            lines.Add($"{name} = \"{EscapeToml(value)}\"");
            return Result<string>.Ok(string.Join('\n', lines));
        }

        // 在表的范围内替换或插入。
        var end = lines.Count;
        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('['))
            {
                end = i;
                break;
            }
        }

        for (var i = headerIndex + 1; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(name + " ", StringComparison.Ordinal) || trimmed.StartsWith(name + "=", StringComparison.Ordinal))
            {
                lines[i] = $"{name} = \"{EscapeToml(value)}\"";
                return Result<string>.Ok(string.Join('\n', lines));
            }
        }

        lines.Insert(end, $"{name} = \"{EscapeToml(value)}\"");
        return Result<string>.Ok(string.Join('\n', lines));
    }

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>XML：按 <c>父节点/子节点</c> 定位并设置文本。</summary>
    private static Result<string> SetXmlValue(string content, string key, string value)
    {
        var slash = key.LastIndexOf('/');
        if (slash <= 0)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"XML 的键名必须写成 父节点/子节点 形式，实际为 {key}。");
        }

        var parentName = key[..slash];
        var childName = key[(slash + 1)..];

        var parsed = MavenSettingsEditor.Parse(content);
        if (parsed.IsFailure)
        {
            return Result<string>.Fail(parsed.Error);
        }

        var root = parsed.Value.Document.Root!;
        var ns = parsed.Value.Namespace;

        var parent = root.Descendants().FirstOrDefault(e => e.Name.LocalName == parentName)
            ?? (root.Name.LocalName == parentName ? root : null);

        if (parent is null)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ConfigXmlParse,
                $"settings.xml 中找不到 <{parentName}> 节点。",
                "先用 config.detect 确认目标文件是否正确，再指定文件里存在的节点名。");
        }

        var child = parent.Elements().FirstOrDefault(e => e.Name.LocalName == childName);

        if (child is null)
        {
            child = new System.Xml.Linq.XElement(ns + childName);
            parent.Add(child);
        }

        child.Value = value;

        return Result<string>.Ok(MavenSettingsEditor.Serialize(parsed.Value));
    }
}

/// <summary><c>envstation.mirror.list</c>：列出内置与自定义镜像源。</summary>
internal sealed class MirrorListAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.mirror.list",
        "1.0.0",
        CapabilityIds.Inspect,
        "列出镜像源",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: [],
        Parameters:
        [
            new ParameterSpec("target", ParameterType.Enum, false, "只看某个目标；省略则列出全部",
                AllowedValues: [.. MirrorCatalog.Targets]),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var target = arguments.GetString("target");
        var mirrors = target is null
            ? MirrorCatalog.BuiltIn
            : MirrorCatalog.ForTarget(target);

        if (mirrors.Length == 0)
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"没有为 {target} 提供内置镜像源。可用目标：{string.Join("、", MirrorCatalog.Targets)}。",
                "也可以直接指定自定义镜像源的 URL。");
        }

        var lines = mirrors
            .OrderBy(static m => m.Target, StringComparer.Ordinal)
            .ThenBy(static m => m.Id, StringComparer.Ordinal)
            .Select(static m => $"[{m.Target}] {m.Id} — {m.DisplayName}（{m.Maintainer}）{m.Url}");

        return Ok(
            $"共 {mirrors.Length} 个镜像源：{string.Join("；", lines)}",
            Outputs(
                ("count", mirrors.Length.ToString(CultureInfo.InvariantCulture)),
                ("targets", string.Join(",", MirrorCatalog.Targets)),
                ("sources", string.Join(" | ", lines)),
                ("trust_warning", "镜像由第三方提供，可能延迟、缺件或（理论上）被篡改；关键构件安装后建议用官方哈希核对。")));
    }
}

/// <summary><c>envstation.mirror.test</c>：连通性、延迟、是否支持 HTTPS。</summary>
internal sealed class MirrorTestAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.mirror.test",
        "1.0.0",
        CapabilityIds.NetDownload,
        "测试镜像源",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 120,
        TouchedResources: ["network.outbound"],
        Parameters:
        [
            StrArray("urls", true, "要测试的镜像地址列表"),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var network = context.Network;
        if (network is null)
        {
            return FailResult(
                EnvStationErrorCodes.NetUnreachable,
                "本次运行没有启用网络能力。");
        }

        var urls = arguments.GetStringArray("urls");
        var results = ImmutableArray.CreateBuilder<string>();
        var reachable = 0;

        foreach (var raw in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                results.Add($"{raw} → 不是合法的 http(s) 地址");
                continue;
            }

            // 白名单在传输层逐跳校验；这里不额外拦截，让用户能看到"这个域名没被允许"这一明确结论。
            var probe = await network
                .ProbeAsync(url, context.AllowedHosts, cancellationToken)
                .ConfigureAwait(false);

            var https = url.Scheme == Uri.UriSchemeHttps;
            if (probe.IsFailure)
            {
                results.Add($"{url.Host} → 不可达（{probe.Error.Message}）");
                continue;
            }

            reachable++;
            results.Add($"{url.Host} → HTTP {probe.Value.StatusCode}，{(https ? "HTTPS" : "明文 HTTP")}");
        }

        return OkResult(
            $"{urls.Length} 个镜像中 {reachable} 个可达：{string.Join("；", results)}",
            Outputs(
                ("reachable", reachable.ToString(CultureInfo.InvariantCulture)),
                ("total", urls.Length.ToString(CultureInfo.InvariantCulture)),
                ("results", string.Join(" | ", results)),
                ("has_plain_http", Bool(urls.Any(static u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))).ToString())));
    }
}

/// <summary><c>envstation.xml.validate</c>：XML 结构校验（含明文凭据检测）。</summary>
internal sealed class XmlValidateAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.xml.validate",
        "1.0.0",
        CapabilityIds.Inspect,
        "校验 Maven settings.xml",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["filesystem.read"],
        Parameters:
        [
            Path_("file", true, "settings.xml 路径"),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var guard = GuardConfigPath(context, arguments.GetString("file")!, "file");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var path = guard.Value;
        if (!File.Exists(path))
        {
            return FailResult(EnvStationErrorCodes.AssertFailed, $"文件不存在：{path}");
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var parsed = MavenSettingsEditor.Parse(content);
        if (parsed.IsFailure)
        {
            return FailResult(parsed.Error.Code, parsed.Error.Message, parsed.Error.Remediation);
        }

        var mirrors = MavenSettingsEditor.ReadMirrors(parsed.Value);
        var envStationMirrors = mirrors.Count(static m => m.ByEnvStation);
        var foreignMirrors = mirrors.Length - envStationMirrors;
        var secrets = ConfigFileStore.FindSecretFields(content);

        var problems = ImmutableArray.CreateBuilder<string>();
        foreach (var (id, url, mirrorOf, byUs) in mirrors)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"镜像 {id} 使用明文 HTTP：{url}");
            }

            if (!MavenSettingsEditor.ValidMirrorOf.Contains(mirrorOf, StringComparer.Ordinal))
            {
                problems.Add($"镜像 {id} 的 mirrorOf「{mirrorOf}」不是推荐取值");
            }

            _ = byUs;
        }

        var duplicated = mirrors
            .GroupBy(static m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key);
        foreach (var id in duplicated)
        {
            problems.Add($"镜像 id 重复：{id}（Maven 只会用其中一个，行为不可预期）");
        }

        return OkResult(
            $"{path}：命名空间 {(parsed.Value.HadNamespace ? parsed.Value.Namespace.NamespaceName : "（无）")}，" +
            $"共 {mirrors.Length} 个 mirror（本软件写入 {envStationMirrors} 个，用户自定义 {foreignMirrors} 个），" +
            $"检测到 {secrets.Length} 个凭据字段。" +
            (problems.Count > 0 ? $" 发现 {problems.Count} 项问题：{string.Join("；", problems)}" : " 未发现问题。"),
            Outputs(
                ("path", path),
                ("namespace", parsed.Value.Namespace.NamespaceName),
                ("mirror_count", mirrors.Length.ToString(CultureInfo.InvariantCulture)),
                ("envstation_mirror_count", envStationMirrors.ToString(CultureInfo.InvariantCulture)),
                ("user_mirror_count", foreignMirrors.ToString(CultureInfo.InvariantCulture)),
                ("credential_fields", string.Join(",", secrets)),
                ("problem_count", problems.Count.ToString(CultureInfo.InvariantCulture)),
                ("problems", string.Join(" | ", problems))));
    }
}

/// <summary><c>envstation.xml.set_mirror</c>：Maven 专项，结构化编辑 <c>&lt;mirrors&gt;</c>。</summary>
internal sealed class XmlSetMirrorAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.xml.set_mirror",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "配置 Maven 镜像",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("file", false, "settings.xml 路径；省略时使用 ~/.m2/settings.xml"),
            Str("id", true, "镜像标识（会加上 envstation- 前缀以便一键移除）", maxLength: 64),
            Str("url", true, "镜像地址（必须 https）", maxLength: 512),
            Str("name", false, "展示名", maxLength: 128),
            new ParameterSpec("mirror_of", ParameterType.Enum, false,
                "mirrorOf 语义：central 只接管中央仓库；* 接管全部仓库（会连同公司私服一起接管）；" +
                "external:* 接管所有外部仓库但保留 localhost 与 file://；*,!私服id 排除指定私服",
                AllowedValues: MavenSettingsEditor.ValidMirrorOf),
            Bool("replace_user_mirror",
                "已存在非本软件写入的镜像时是否仍然替换。默认 false，那通常是公司私服", false),
            Bool("dry_run", "只报告将要做的修改", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var raw = arguments.GetString("file");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".m2",
                "settings.xml");
        }

        var guard = GuardConfigPath(context, raw, "file");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var path = guard.Value;
        var id = arguments.GetString("id")!;
        var url = arguments.GetString("url")!;
        var name = arguments.GetString("name") ?? id;
        var mirrorOf = arguments.GetString("mirror_of") ?? "central";
        var replaceUser = arguments.GetBoolean("replace_user_mirror", false);
        var dryRun = arguments.GetBoolean("dry_run", false);

        var original = File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var parsed = MavenSettingsEditor.Parse(original);
        if (parsed.IsFailure)
        {
            return FailResult(parsed.Error.Code, parsed.Error.Message, parsed.Error.Remediation);
        }

        var set = MavenSettingsEditor.SetMirror(parsed.Value, id, name, url, mirrorOf, replaceUser);
        if (set.IsFailure)
        {
            return FailResult(set.Error.Code, set.Error.Message, set.Error.Remediation);
        }

        var updated = MavenSettingsEditor.Serialize(parsed.Value);

        if (dryRun)
        {
            return OkResult(
                $"预演：将在 {path} 中写入镜像 envstation-{id} → {url}（mirrorOf={mirrorOf}），未做任何修改。",
                Outputs(("dry_run", "true"), ("path", path), ("mirror_id", "envstation-" + id), ("changed", "false")));
        }

        var apply = await ApplyConfigChangeAsync(
            context, path, updated, ConfigFormats.Xml, "maven-mirror",
            $"已把 Maven 镜像 envstation-{id} 配置为 {url}（mirrorOf={mirrorOf}）。{MirrorCatalog.TrustWarning(new MirrorDefinition("maven", id, name, url, "第三方", true))}",
            cancellationToken).ConfigureAwait(false);

        if (apply.Success)
        {
            // 写后语义校验（XML-M8）：确认这条镜像真的会被 Maven 用上。
            var reload = MavenSettingsEditor.Parse(updated);
            var semantic = reload.IsFailure
                ? Result<Unit>.Fail(reload.Error)
                : MavenSettingsEditor.Validate([id], reload.Value);

            if (semantic.IsFailure)
            {
                var store = StoreFor(context);
                var backup = await store.LoadBackupAsync(apply.ReversibleToken!, cancellationToken).ConfigureAwait(false);
                if (backup.IsSuccess)
                {
                    await ConfigFileStore.RestoreAsync(backup.Value, cancellationToken).ConfigureAwait(false);
                }

                return FailResult(
                    semantic.Error.Code,
                    "写后语义校验未通过，已自动回滚：" + semantic.Error.Message,
                    semantic.Error.Remediation);
            }
        }

        return apply;
    }
}

/// <summary><c>envstation.xml.set_repository</c>：编辑 <c>&lt;repositories&gt;</c> / <c>&lt;pluginRepositories&gt;</c>。</summary>
internal sealed class XmlSetRepositoryAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.xml.set_repository",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "配置 Maven 仓库",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("file", false, "settings.xml 路径；省略时使用 ~/.m2/settings.xml"),
            Str("id", true, "仓库标识", maxLength: 64),
            Str("url", true, "仓库地址（必须 https）", maxLength: 512),
            Bool("snapshots", "是否启用快照", false),
            Bool("releases", "是否启用正式版", true),
            Bool("plugin_repository", "同时写入 pluginRepositories", false),
            Bool("dry_run", "只报告将要做的修改", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var raw = arguments.GetString("file");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".m2",
                "settings.xml");
        }

        var guard = GuardConfigPath(context, raw, "file");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var path = guard.Value;
        var id = arguments.GetString("id")!;
        var url = arguments.GetString("url")!;
        var snapshots = arguments.GetBoolean("snapshots", false);
        var releases = arguments.GetBoolean("releases", true);
        var alsoPlugin = arguments.GetBoolean("plugin_repository", false);
        var dryRun = arguments.GetBoolean("dry_run", false);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return FailResult(
                EnvStationErrorCodes.ConfigVerifyFailed,
                $"仓库地址必须是 https：{url}",
                "明文 HTTP 的仓库可能被中间人替换依赖包。");
        }

        var original = File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var parsed = MavenSettingsEditor.Parse(original);
        if (parsed.IsFailure)
        {
            return FailResult(parsed.Error.Code, parsed.Error.Message, parsed.Error.Remediation);
        }

        var root = parsed.Value.Document.Root!;
        var ns = parsed.Value.Namespace;
        var fullId = MavenSettingsEditor.MirrorIdPrefix + id;

        UpsertRepository(root, ns, "repositories", fullId, url, snapshots, releases);
        if (alsoPlugin)
        {
            UpsertRepository(root, ns, "pluginRepositories", fullId, url, snapshots, releases);
        }

        var updated = MavenSettingsEditor.Serialize(parsed.Value);

        if (dryRun)
        {
            return OkResult(
                $"预演：将把仓库 {fullId} → {url} 写入 {path}（未做任何修改）。",
                Outputs(("dry_run", "true"), ("path", path), ("repository_id", fullId)));
        }

        return await ApplyConfigChangeAsync(
            context, path, updated, ConfigFormats.Xml, "maven-repository",
            $"已把 Maven 仓库 {fullId} 配置为 {url}（快照 {(snapshots ? "启用" : "禁用")}，正式版 {(releases ? "启用" : "禁用")}）。",
            cancellationToken).ConfigureAwait(false);
    }

    private static void UpsertRepository(
        System.Xml.Linq.XElement root,
        System.Xml.Linq.XNamespace ns,
        string containerName,
        string id,
        string url,
        bool snapshots,
        bool releases)
    {
        var container = root.Element(ns + containerName) ?? root.Element(containerName);
        if (container is null)
        {
            container = new System.Xml.Linq.XElement(ns == System.Xml.Linq.XNamespace.None ? containerName : ns + containerName);
            root.Add(container);
        }

        var existing = container
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "repository"
                && string.Equals(e.Element(e.Name.Namespace + "id")?.Value, id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Remove();
        }

        var repository = new System.Xml.Linq.XElement(ns == System.Xml.Linq.XNamespace.None ? "repository" : ns + "repository");
        repository.Add(new System.Xml.Linq.XComment(" >>> EnvStation >>> 由环境站写入，可通过软件一键移除 "));
        repository.Add(new System.Xml.Linq.XElement(repository.Name.Namespace + "id", id));
        repository.Add(new System.Xml.Linq.XElement(repository.Name.Namespace + "url", url));
        repository.Add(BuildPolicy(repository.Name.Namespace, "releases", releases));
        repository.Add(BuildPolicy(repository.Name.Namespace, "snapshots", snapshots));
        repository.Add(new System.Xml.Linq.XComment(" <<< EnvStation <<< "));

        container.Add(repository);
    }

    private static System.Xml.Linq.XElement BuildPolicy(System.Xml.Linq.XNamespace ns, string name, bool enabled)
    {
        var policy = new System.Xml.Linq.XElement(ns == System.Xml.Linq.XNamespace.None ? name : ns + name);
        policy.Add(new System.Xml.Linq.XElement(policy.Name.Namespace + "enabled", enabled ? "true" : "false"));
        return policy;
    }
}

/// <summary><c>envstation.xml.set_proxy</c>：编辑 <c>&lt;proxies&gt;</c>。</summary>
internal sealed class XmlSetProxyAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.xml.set_proxy",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "配置 Maven 代理",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("file", false, "settings.xml 路径；省略时使用 ~/.m2/settings.xml"),
            new ParameterSpec("protocol", ParameterType.Enum, true, "代理协议",
                AllowedValues: ["http", "https"]),
            Str("host", true, "代理主机", maxLength: 255),
            new ParameterSpec("port", ParameterType.Integer, true, "代理端口", Minimum: 1, Maximum: 65535),
            Str("non_proxy_hosts", false, "不走代理的主机（竖线分隔）", maxLength: 512),
            Bool("active", "是否启用该代理", true),
            Bool("dry_run", "只报告将要做的修改", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var raw = arguments.GetString("file");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".m2",
                "settings.xml");
        }

        var guard = GuardConfigPath(context, raw, "file");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var path = guard.Value;
        var protocol = arguments.GetString("protocol")!;
        var host = arguments.GetString("host")!;
        var port = arguments.GetInt64("port");
        var nonProxyHosts = arguments.GetString("non_proxy_hosts");
        var active = arguments.GetBoolean("active", true);
        var dryRun = arguments.GetBoolean("dry_run", false);

        var original = File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var parsed = MavenSettingsEditor.Parse(original);
        if (parsed.IsFailure)
        {
            return FailResult(parsed.Error.Code, parsed.Error.Message, parsed.Error.Remediation);
        }

        var root = parsed.Value.Document.Root!;
        var ns = parsed.Value.Namespace;
        var fullId = MavenSettingsEditor.MirrorIdPrefix + "proxy";

        var container = root.Element(ns + "proxies") ?? root.Element("proxies");
        if (container is null)
        {
            container = new System.Xml.Linq.XElement(ns == System.Xml.Linq.XNamespace.None ? "proxies" : ns + "proxies");
            root.Add(container);
        }

        var existing = container
            .Elements()
            .FirstOrDefault(e => string.Equals(
                e.Element(e.Name.Namespace + "id")?.Value, fullId, StringComparison.OrdinalIgnoreCase));

        existing?.Remove();

        var proxy = new System.Xml.Linq.XElement(ns == System.Xml.Linq.XNamespace.None ? "proxy" : ns + "proxy");
        proxy.Add(new System.Xml.Linq.XComment(" >>> EnvStation >>> 由环境站写入。凭据不写入本文件，密码存放在 settings-security.xml "));
        proxy.Add(new System.Xml.Linq.XElement(proxy.Name.Namespace + "id", fullId));
        proxy.Add(new System.Xml.Linq.XElement(proxy.Name.Namespace + "active", active ? "true" : "false"));
        proxy.Add(new System.Xml.Linq.XElement(proxy.Name.Namespace + "protocol", protocol));
        proxy.Add(new System.Xml.Linq.XElement(proxy.Name.Namespace + "host", host));
        proxy.Add(new System.Xml.Linq.XElement(proxy.Name.Namespace + "port", port.ToString(CultureInfo.InvariantCulture)));
        if (nonProxyHosts is { Length: > 0 })
        {
            proxy.Add(new System.Xml.Linq.XElement(proxy.Name.Namespace + "nonProxyHosts", nonProxyHosts));
        }

        proxy.Add(new System.Xml.Linq.XComment(" <<< EnvStation <<< "));
        container.Add(proxy);

        var updated = MavenSettingsEditor.Serialize(parsed.Value);

        if (dryRun)
        {
            return OkResult(
                $"预演：将把代理 {host}:{port}（{protocol}）写入 {path}，未做任何修改。",
                Outputs(("dry_run", "true"), ("path", path)));
        }

        return await ApplyConfigChangeAsync(
            context, path, updated, ConfigFormats.Xml, "maven-proxy",
            $"已配置 Maven 代理 {host}:{port}（{protocol}）。凭据未写入该文件；Maven 的密码存放在 settings-security.xml 中。",
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>envstation.mirror.set</c>：按目标切换镜像源（通用入口）。</summary>
internal sealed class MirrorSetAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.mirror.set",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "切换镜像源",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            new ParameterSpec("target", ParameterType.Enum, true, "要切换的目标",
                AllowedValues: MirrorCatalog.Targets),
            Str("url", true, "镜像地址", maxLength: 512),
            Str("id", false, "镜像标识（用于一键移除与还原）", maxLength: 64),
            Bool("dry_run", "只报告将要做的修改", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var target = arguments.GetString("target")!;
        var url = arguments.GetString("url")!;
        var id = arguments.GetString("id") ?? "mirror";
        var dryRun = arguments.GetBoolean("dry_run", false);

        var probe = ConfigTargetCatalog.Find(target);
        if (probe is null)
        {
            return FailResult(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"未知目标 {target}。可用：{string.Join("、", ConfigTargetCatalog.Targets)}。",
                "Maven 用 envstation.xml.set_mirror，Gradle 用 envstation.gradle.set_mirror。");
        }

        // 目标文件的落点：优先已存在的候选，否则用第一个候选。
        var path = probe.Existing.Length > 0 ? probe.Existing[0] : probe.Candidates[0];

        var guard = GuardConfigPath(context, path, "path");
        if (guard.IsFailure)
        {
            // 配置文件通常在用户主目录下，可能不在授权范围内。
            // 这不是"配置错了"，而是"授权范围不够"，因此给出的是可操作的提示。
            return FailResult(
                guard.Error.Code,
                $"要写入的配置文件 {path} 不在本次运行的授权目录内。",
                $"运行包时把 {Path.GetDirectoryName(path)} 加入授权根目录（--root）。" +
                " 环境站不会为了改配置而自动扩大自己的授权范围。");
        }

        path = guard.Value;
        var original = File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        // 各目标的写法差异很大，因此这里按格式分派到各自的标记块写法。
        var updated = target.ToLowerInvariant() switch
        {
            "pip" => ConfigFileStore.UpsertMarkedBlock(
                original, "pip-mirror",
                $"[global]\nindex-url = {url}\ntrusted-host = {new Uri(url).Host}"),
            "npm" => ConfigFileStore.UpsertMarkedBlock(
                original, "npm-registry", $"registry={url}"),
            "cargo" => ConfigFileStore.UpsertMarkedBlock(
                original, "cargo-source",
                $"[source.crates-io]\nreplace-with = \"envstation-mirror\"\n\n[source.envstation-mirror]\nregistry = \"sparse+{url}\""),
            "composer" => ConfigFileStore.UpsertMarkedBlock(
                original, "composer-repo", $"repositories.packagist.url = {url}"),
            "nuget" => ConfigFileStore.UpsertMarkedBlock(
                original, "nuget-source", $"packageSources.{id} = {url}"),
            "go" => Fail("Go 没有配置文件，改用 envstation.env.set 设置 GOPROXY（属 L1 变更，会先建快照）。").Value,
            _ => original,
        };

        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return OkResult(
                $"{target} 的镜像已经是目标值，无需修改。",
                Outputs(("changed", "false"), ("path", path), ("target", target)));
        }

        if (dryRun)
        {
            return OkResult(
                $"预演：将把 {target} 的镜像指向 {url}（写入 {path}），未做任何修改。",
                Outputs(("changed", "false"), ("dry_run", "true"), ("path", path), ("target", target)));
        }

        return await ApplyConfigChangeAsync(
            context, path, updated, probe.Format, target + "-mirror",
            $"已把 {target} 的镜像指向 {url}。镜像由第三方提供，可能延迟、缺件或（理论上）被篡改。",
            cancellationToken).ConfigureAwait(false);
    }

    private static Result<string> Fail(string message) => Result<string>.Fail(
        EnvStationErrorCodes.ActionArgumentInvalid,
        message,
        "GOPROXY 是环境变量而非配置文件，需用 envstation.env.set 修改。");
}

/// <summary><c>envstation.mirror.restore</c>：恢复原始源或上一次备份。</summary>
internal sealed class MirrorRestoreAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.mirror.restore",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "回滚镜像配置",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Str("backup_id", true, "要回滚的备份 ID（由 mirror.set / xml.set_mirror 返回）", maxLength: 128),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var backupId = arguments.GetString("backup_id")!;
        var store = StoreFor(context);

        var backup = await store.LoadBackupAsync(backupId, cancellationToken).ConfigureAwait(false);
        if (backup.IsFailure)
        {
            return FailResult(backup.Error.Code, backup.Error.Message, backup.Error.Remediation);
        }

        var restore = await ConfigFileStore.RestoreAsync(backup.Value, cancellationToken).ConfigureAwait(false);
        if (restore.IsFailure)
        {
            return FailResult(restore.Error.Code, restore.Error.Message, restore.Error.Remediation);
        }

        return OkResult(
            backup.Value.Existed
                ? $"已把 {backup.Value.OriginalPath} 还原到修改前的状态。"
                : $"已删除本软件新建的 {backup.Value.OriginalPath}（它在修改前并不存在）。",
            Outputs(
                ("path", backup.Value.OriginalPath),
                ("existed_before", Bool(backup.Value.Existed)),
                ("content_hash", backup.Value.ContentHash)),
            touched: [backup.Value.OriginalPath]);
    }
}

/// <summary><c>envstation.gradle.set_mirror</c>：Gradle 专项，写 init.gradle 标记块。</summary>
internal sealed class GradleSetMirrorAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.gradle.set_mirror",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "配置 Gradle 镜像",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            StrArray("urls", true, "镜像地址列表（按顺序尝试）"),
            new ParameterSpec("style", ParameterType.Enum, false,
                "init = 写入 ~/.gradle/init.gradle（全局生效，不改项目文件）；settings = 输出可直接粘贴进 settings.gradle 的片段",
                AllowedValues: ["init", "settings"]),
            Bool("dry_run", "只报告将要做的修改", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var urls = arguments.GetStringArray("urls");
        var style = arguments.GetString("style") ?? "init";
        var dryRun = arguments.GetBoolean("dry_run", false);

        if (urls.Length == 0)
        {
            return FailResult(EnvStationErrorCodes.ActionArgumentInvalid, "urls 不能为空。");
        }

        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return FailResult(EnvStationErrorCodes.ConfigVerifyFailed, $"镜像地址必须是 https：{url}");
            }
        }

        var body = BuildInitScript(urls);

        if (style == "settings")
        {
            return OkResult(
                "以下是可直接粘贴进 settings.gradle 的片段（环境站未修改任何文件）：\n" + body,
                Outputs(("style", "settings"), ("snippet", body), ("changed", "false")));
        }

        var path = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".gradle",
            "init.gradle");

        var guard = GuardConfigPath(context, path, "path");
        if (guard.IsFailure)
        {
            return FailResult(
                guard.Error.Code,
                $"要写入的 {path} 不在本次运行的授权目录内。",
                $"运行包时把 {Path.GetDirectoryName(path)} 加入授权根目录（--root）。");
        }

        var original = File.Exists(guard.Value)
            ? await File.ReadAllTextAsync(guard.Value, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var updated = ConfigFileStore.UpsertMarkedBlock(original, "gradle-mirror", body);

        if (dryRun)
        {
            return OkResult(
                $"预演：将把 {urls.Length} 个镜像写入 {guard.Value} 的标记块（未做任何修改）。",
                Outputs(("dry_run", "true"), ("path", guard.Value), ("changed", "false")));
        }

        return await ApplyConfigChangeAsync(
            context, guard.Value, updated, ConfigFormats.Groovy, "gradle-mirror",
            $"已把 {urls.Length} 个镜像写入 {guard.Value}（标记块 id=gradle-mirror，可用 file.marked_block_remove 一键移除）。",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 生成 Gradle init 脚本。
    /// </summary>
    /// <remarks>
    /// 用 <c>settingsEvaluated</c> 钩子<b>在已有仓库之前插入</b>镜像，而不是替换用户的
    /// <c>repositories</c> 声明（需求 GR-2）。这样项目自身声明的仓库仍然生效，
    /// 我们只是让镜像优先——用户不会因为配了镜像就丢失项目原有的私服。
    /// </remarks>
    private static string BuildInitScript(ImmutableArray<string> urls)
    {
        var sb = new StringBuilder();
        sb.Append("// 由环境站生成。本块可整体删除，不影响项目自身配置。\n");
        sb.Append("settingsEvaluated { settings ->\n");
        sb.Append("    settings.pluginManagement {\n");
        sb.Append("        repositories {\n");
        foreach (var url in urls)
        {
            sb.Append("            maven { url '").Append(url).Append("'; allowInsecureProtocol = false }\n");
        }

        sb.Append("        }\n");
        sb.Append("    }\n");
        sb.Append("}\n\n");
        sb.Append("allprojects {\n");
        sb.Append("    buildscript {\n");
        sb.Append("        repositories {\n");
        foreach (var url in urls)
        {
            sb.Append("            maven { url '").Append(url).Append("' }\n");
        }

        sb.Append("        }\n");
        sb.Append("    }\n");
        sb.Append("    repositories {\n");
        foreach (var url in urls)
        {
            sb.Append("        maven { url '").Append(url).Append("' }\n");
        }

        sb.Append("    }\n");
        sb.Append("}\n");
        return sb.ToString();
    }
}

/// <summary><c>envstation.file.write_template</c>：用模板渲染并写入配置文件。</summary>
internal sealed class FileWriteTemplateAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.file.write_template",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "渲染模板并写入",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: true,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Str("template", true, "模板内容，用 ${变量名} 引用 vars 中的值", maxLength: 20000),
            Str("vars", false, "变量表，写成 key=value，多条用换行分隔", maxLength: 8000),
            Path_("dest", true, "目标文件路径"),
            new ParameterSpec("format", ParameterType.Enum, false, "目标格式（用于写后校验）",
                AllowedValues: [ConfigFormats.Ini, ConfigFormats.Xml, ConfigFormats.Json, ConfigFormats.Toml, ConfigFormats.Groovy]),
            Bool("dry_run", "只渲染并报告，不写入", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var template = arguments.GetString("template")!;
        var varsText = arguments.GetString("vars") ?? string.Empty;
        var destGuard = GuardConfigPath(context, arguments.GetString("dest")!, "dest");
        if (destGuard.IsFailure)
        {
            return FailResult(destGuard.Error.Code, destGuard.Error.Message, destGuard.Error.Remediation);
        }

        var format = arguments.GetString("format") ?? ConfigFormats.Ini;
        var dryRun = arguments.GetBoolean("dry_run", false);

        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in varsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                vars[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        // 渲染只做**数据替换**（需求 AI-2）：结果永远只是一个字符串值，
        // 既不进入命令行，也不参与任何代码求值。
        var rendered = template;
        foreach (var (key, value) in vars)
        {
            rendered = rendered.Replace("${" + key + "}", value, StringComparison.Ordinal);
        }

        if (rendered.Contains("${", StringComparison.Ordinal))
        {
            var missing = System.Text.RegularExpressions.Regex.Matches(rendered, @"\$\{([^}]+)\}")
                .Select(static m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal);

            return FailResult(
                EnvStationErrorCodes.WorkflowVariableUndefined,
                $"模板中仍有未提供的变量：{string.Join("、", missing)}",
                "在 vars 中为每个占位符提供取值。未替换的占位符不会被写入配置文件。");
        }

        if (dryRun)
        {
            return OkResult(
                $"预演：将向 {destGuard.Value} 写入渲染结果（{rendered.Length} 字符），未做任何修改。",
                Outputs(
                    ("dry_run", "true"),
                    ("rendered_length", rendered.Length.ToString(CultureInfo.InvariantCulture)),
                    ("preview", ConfigFileStore.RedactSecrets(
                        rendered.Length <= 800 ? rendered : rendered[..800] + "…"))));
        }

        return await ApplyConfigChangeAsync(
            context, destGuard.Value, rendered, format, "write_template",
            $"已用模板渲染并写入 {destGuard.Value}。", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>envstation.file.marked_block_remove</c>：移除本软件写入的标记区块。</summary>
internal sealed class FileMarkedBlockRemoveAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.file.marked_block_remove",
        "1.0.0",
        CapabilityIds.ConfigApp,
        "移除标记区块",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("file", true, "目标文件"),
            Str("marker_id", true, "区块标识（如 pip-mirror、npm-registry、gradle-mirror）", maxLength: 64),
            Bool("xml_style", "使用 XML 注释形式的标记", false),
            Bool("dry_run", "只报告将要移除的内容", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var guard = GuardConfigPath(context, arguments.GetString("file")!, "file");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var markerId = arguments.GetString("marker_id")!;
        var xmlStyle = arguments.GetBoolean("xml_style", false);
        var dryRun = arguments.GetBoolean("dry_run", false);

        if (!File.Exists(guard.Value))
        {
            return OkResult(
                $"文件不存在，无需移除：{guard.Value}",
                Outputs(("changed", "false"), ("path", guard.Value)));
        }

        var original = await File.ReadAllTextAsync(guard.Value, cancellationToken).ConfigureAwait(false);

        if (!ConfigFileStore.HasMarkedBlock(original, markerId, xmlStyle))
        {
            return OkResult(
                $"{guard.Value} 中没有 id 为 {markerId} 的标记块，无需移除。",
                Outputs(("changed", "false"), ("path", guard.Value), ("marker_id", markerId)));
        }

        var updated = ConfigFileStore.RemoveMarkedBlock(original, markerId, xmlStyle);

        if (dryRun)
        {
            return OkResult(
                $"预演：将从 {guard.Value} 移除 {markerId} 标记块（减少 {original.Length - updated.Length} 字符），未做任何修改。",
                Outputs(("changed", "false"), ("dry_run", "true"), ("removed_chars",
                    (original.Length - updated.Length).ToString(CultureInfo.InvariantCulture))));
        }

        return await ApplyConfigChangeAsync(
            context, guard.Value, updated, ConfigFormats.Ini, markerId,
            $"已从 {guard.Value} 移除 {markerId} 标记块（减少 {original.Length - updated.Length} 字符）。",
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>envstation.verify.mirror_effective</c>：校验镜像源真实生效。</summary>
internal sealed class VerifyMirrorEffectiveAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.verify.mirror_effective",
        "1.0.0",
        CapabilityIds.NetDownload,
        "校验镜像真实生效",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 120,
        TouchedResources: ["network.outbound", "filesystem.read"],
        Parameters:
        [
            Str("target", true, "要验证的目标（maven / npm / pip / gradle）", maxLength: 32),
            Str("expect_host", true, "期望的镜像域名（用于核对响应确实来自该镜像）", maxLength: 255),
            Path_("file", false, "要检查的配置文件；省略时使用该目标的默认位置"),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var target = arguments.GetString("target")!;
        var expectHost = arguments.GetString("expect_host")!;

        var probe = ConfigTargetCatalog.Find(target);
        if (probe is null)
        {
            return FailResult(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"未知目标 {target}。可用：{string.Join("、", ConfigTargetCatalog.Targets)}。");
        }

        var path = arguments.GetString("file")
            ?? (probe.Existing.Length > 0 ? probe.Existing[0] : probe.Candidates[0]);

        if (!File.Exists(path))
        {
            return FailResult(
                EnvStationErrorCodes.AssertFailed,
                $"配置文件不存在：{path}",
                "镜像配置尚未写入。先运行 mirror.set 或对应的专项动作。");
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        // 配置里必须真的出现该域名，否则"生效"无从谈起。
        if (!content.Contains(expectHost, StringComparison.OrdinalIgnoreCase))
        {
            return FailResult(
                EnvStationErrorCodes.AssertFailed,
                $"{path} 中没有出现期望的镜像域名 {expectHost}。",
                "镜像配置可能没有写入成功，或被其他工具改回去了。" +
                "用 config.detect 确认目标文件；IDE 自带的 settings.xml 会覆盖用户级配置。");
        }

        // 真正的"生效验证"应当触发一次真实依赖解析（XML-M9）。
        // 但那需要运行 mvn / npm 等命令，属于 CAP.PROCESS.LAUNCH 的职责，
        // 且会真实下载构件——因此这一步刻意只做"配置层"的确认，
        // 并在输出里说明还差哪一步。
        var separatorCount = content.Split('\n').Count(l => l.Contains(expectHost, StringComparison.OrdinalIgnoreCase));

        return OkResult(
            $"{target} 的配置中已包含镜像域名 {expectHost}（出现在 {separatorCount} 行）。" +
            "这只证明配置写对了；要证明镜像真的被用上，需要触发一次真实依赖解析。",
            Outputs(
                ("target", target),
                ("path", path),
                ("expected_host", expectHost),
                ("occurrences", separatorCount.ToString(CultureInfo.InvariantCulture)),
                ("config_level_verified", "true"),
                ("end_to_end_verified", "false"),
                ("next_step", "端到端验证需运行 verify.version_output 触发一次真实解析，或手工执行一次构建。")));
    }
}

/// <summary><c>envstation.verify.mirror_integrity</c>：校验镜像下载内容与官方哈希一致。</summary>
internal sealed class VerifyMirrorIntegrityAction : ConfigActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.verify.mirror_integrity",
        "1.0.0",
        CapabilityIds.Archive,
        "校验镜像内容完整性",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 1800,
        TouchedResources: ["network.outbound", "filesystem.read", "filesystem.write"],
        Parameters:
        [
            Url("mirror_url", true, "镜像上的构件地址"),
            Url("official_url", true, "官方源的同一构件地址（用于对比）"),
            Bool("download_official", "是否真的下载官方文件做逐字节比对。默认 false：只比对 HEAD 的大小与哈希声明", true),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var network = context.Network;
        if (network is null)
        {
            return FailResult(
                EnvStationErrorCodes.NetUnreachable,
                "本次运行没有启用网络能力（未注入网络通道实现）。");
        }

        if (!Uri.TryCreate(arguments.GetString("mirror_url")!, UriKind.Absolute, out var mirrorUrl)
            || !Uri.TryCreate(arguments.GetString("official_url")!, UriKind.Absolute, out var officialUrl))
        {
            return FailResult(EnvStationErrorCodes.ActionArgumentInvalid, "mirror_url 与 official_url 都必须是合法的绝对 URL。");
        }

        var mirrorProbe = await network.ProbeAsync(mirrorUrl, context.AllowedHosts, cancellationToken).ConfigureAwait(false);
        var officialProbe = await network.ProbeAsync(officialUrl, context.AllowedHosts, cancellationToken).ConfigureAwait(false);

        if (mirrorProbe.IsFailure || officialProbe.IsFailure)
        {
            return FailResult(
                EnvStationErrorCodes.NetUnreachable,
                $"检测失败：镜像 {mirrorProbe.Error?.Message ?? "正常"}；官方 {officialProbe.Error?.Message ?? "正常"}");
        }

        var mirrorSize = mirrorProbe.Value.ContentLength;
        var officialSize = officialProbe.Value.ContentLength;
        var sizeMatches = mirrorSize >= 0 && officialSize >= 0 && mirrorSize == officialSize;

        // 尺寸相同不能证明内容相同——这是本动作必须说清楚的一件事。
        // 真正的完整性证明只能靠官方公布的哈希（哈希比对交给 net.download 的强制 sha256）。
        return OkResult(
            $"镜像 {mirrorUrl.Host}：{mirrorSize} 字节；官方 {officialUrl.Host}：{officialSize} 字节。" +
            (sizeMatches ? " 大小一致。" : " 大小不一致，镜像内容与官方不同，不要使用该文件。") +
            " 大小相同不等于内容相同；完整性只能用官方公布的 SHA-256 证明。",
            Outputs(
                ("mirror_bytes", mirrorSize.ToString(CultureInfo.InvariantCulture)),
                ("official_bytes", officialSize.ToString(CultureInfo.InvariantCulture)),
                ("size_matches", Bool(sizeMatches)),
                ("integrity_proven", "false"),
                ("next_step", "完整性证明需用 envstation.net.download，并在 sha256 参数中填入官方公布的哈希。")));
    }
}
