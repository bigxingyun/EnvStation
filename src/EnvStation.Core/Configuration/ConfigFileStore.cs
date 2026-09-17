using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EnvStation.Abstractions;

namespace EnvStation.Core.Configuration;

/// <summary>一次配置文件备份。</summary>
/// <param name="BackupId">备份 ID。</param>
/// <param name="OriginalPath">原文件路径。</param>
/// <param name="ContentHash">原内容的 SHA-256（裸十六进制小写）。</param>
/// <param name="Existed">备份时文件是否存在。为 false 时"还原"的正确动作是删除文件。</param>
/// <param name="BackupPath">备份文件路径。</param>
/// <param name="CreatedAt">备份时间。</param>
/// <param name="Note">备注。</param>
public sealed record ConfigBackup(
    string BackupId,
    string OriginalPath,
    string ContentHash,
    bool Existed,
    string BackupPath,
    DateTimeOffset CreatedAt,
    string? Note);

/// <summary>
/// 配置文件存储：备份、标记区块、原子写入、写后校验。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类的存在意义是让"改配置文件"这件事变得可逆且不破坏用户既有配置。</b>
/// 需求 21.2.1 的七条硬性要求全部落在这里，因为它们有一个共同前提：
/// <b>必须先有一份可靠的原始内容</b>。散在各个动作里实现，就等于把"用户配置会不会被毁掉"
/// 这件事交给十几个动作各自的自觉。
/// </para>
/// <para><b>四条实现纪律</b>：</para>
/// <list type="number">
///   <item><b>备份必然在修改之前</b>（CF-1）：<see cref="BackupAsync"/> 返回失败时，
///         调用方必须放弃写入。这是结构性的，不是建议。</item>
///   <item><b>原子写入</b>：先写 <c>.tmp</c> 再替换。断电/崩溃不会留下半截文件——
///         半截的 <c>settings.xml</c> 会让 Maven 完全无法启动。</item>
///   <item><b>标记区块</b>（CF-4）：本软件写入的内容都包在
///         <c># >>> EnvStation >>></c> … <c># &lt;&lt;&lt; EnvStation &lt;&lt;&lt;</c> 之间，
///         因此"一键移除"不需要理解文件语义。</item>
///   <item><b>凭据永不落盘到日志</b>（CF-6）：备份文件保留原样是必要的（还原需要它），
///         但任何进入日志、报告、输出的内容都必须经过 <see cref="RedactSecrets"/>。</item>
/// </list>
/// </remarks>
public sealed class ConfigFileStore
{
    /// <summary>标记区块的起始行。</summary>
    public const string MarkerBegin = "# >>> EnvStation >>>";

    /// <summary>标记区块的结束行。</summary>
    public const string MarkerEnd = "# <<< EnvStation <<<";

    /// <summary>XML 注释形式的标记（用于 Maven settings.xml）。</summary>
    public const string XmlMarkerBegin = "<!-- >>> EnvStation >>> 由环境站写入，可通过软件一键移除 -->";

    /// <summary>XML 注释形式的结束标记。</summary>
    public const string XmlMarkerEnd = "<!-- <<< EnvStation <<< -->";

    /// <summary>默认备份根目录。</summary>
    public static string DefaultBackupRoot => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "EnvStation",
        "config-backups");

    private readonly string _backupRoot;

    /// <summary>用指定备份根目录构造（测试必须显式传入临时目录）。</summary>
    public ConfigFileStore(string backupRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupRoot);
        _backupRoot = backupRoot;
    }

    /// <summary>用默认位置构造。</summary>
    public static ConfigFileStore CreateDefault() => new(DefaultBackupRoot);

    /// <summary>
    /// 备份一个配置文件（CF-1）。
    /// </summary>
    /// <param name="path">要备份的文件。文件不存在也算成功——备份记录里记下"原本不存在"，
    /// 这样还原时才知道应该删除文件而不是恢复内容。</param>
    /// <param name="note">备注。</param>
    public async Task<Result<ConfigBackup>> BackupAsync(
        string path, string? note = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var full = Path.GetFullPath(path);
        var exists = File.Exists(full);

        Directory.CreateDirectory(_backupRoot);

        var backupId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var backupPath = Path.Combine(_backupRoot, backupId + ".bak");

        string hash;
        if (exists)
        {
            var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
            hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await File.WriteAllBytesAsync(backupPath, bytes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            hash = string.Empty;

            // 文件不存在时不写备份体，但**仍然创建一条记录**：
            // "这个文件原本不存在"本身就是要还原的信息。
            await File.WriteAllTextAsync(backupPath, string.Empty, cancellationToken).ConfigureAwait(false);
        }

        var record = new ConfigBackup(
            backupId,
            full,
            hash,
            exists,
            backupPath,
            DateTimeOffset.Now,
            note);

        var indexPath = Path.Combine(_backupRoot, backupId + ".json");
        var json = Serialize(record);
        await File.WriteAllTextAsync(indexPath, json, cancellationToken).ConfigureAwait(false);

        return Result<ConfigBackup>.Ok(record);
    }

    /// <summary>读取一条备份记录。</summary>
    public async Task<Result<ConfigBackup>> LoadBackupAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var indexPath = Path.Combine(_backupRoot, backupId + ".json");
        if (!File.Exists(indexPath))
        {
            return Result<ConfigBackup>.Fail(
                EnvStationErrorCodes.ConfigVerifyFailed,
                $"找不到备份 {backupId}。",
                "备份可能已被清理。检查设置中的备份保留策略。");
        }

        var json = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);
        var parsed = Deserialize(json);

        return parsed is null
            ? Result<ConfigBackup>.Fail(EnvStationErrorCodes.ConfigVerifyFailed, $"备份记录 {backupId} 已损坏。")
            : Result<ConfigBackup>.Ok(parsed);
    }

    /// <summary>
    /// 从备份还原（CF-5 的兜底路径）。
    /// </summary>
    public static async Task<Result<Unit>> RestoreAsync(ConfigBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);

        if (!backup.Existed)
        {
            // 原本不存在 → 还原 = 删除本软件新建的文件。
            if (File.Exists(backup.OriginalPath))
            {
                File.Delete(backup.OriginalPath);
            }

            return Results.Ok();
        }

        if (!File.Exists(backup.BackupPath))
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.ConfigVerifyFailed,
                $"备份体丢失：{backup.BackupPath}",
                "无法回滚。手动核对该配置文件。");
        }

        var bytes = await File.ReadAllBytesAsync(backup.BackupPath, cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(backup.OriginalPath, bytes, cancellationToken).ConfigureAwait(false);
        return Results.Ok();
    }

    /// <summary>
    /// 原子写入文本（CF-5）。
    /// </summary>
    /// <remarks>
    /// 先写同目录下的 <c>.tmp</c>，再用 <see cref="File.Move(string, string, bool)"/> 替换。
    /// 关键是 <b>.tmp 必须与目标同目录</b>：跨卷的"替换"不是原子的，
    /// 而配置文件损坏的后果（Maven 起不来、npm 全部失败）远比多留一个临时文件严重。
    /// </remarks>
    public static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".envstation.tmp";
        await File.WriteAllBytesAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>原子写入文本（UTF-8 无 BOM，LF 换行）。</summary>
    public static Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken = default) =>
        WriteAtomicAsync(path, new UTF8Encoding(false).GetBytes(content), cancellationToken);

    /// <summary>计算文件内容的 SHA-256；文件不存在返回空串。</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>计算一段文本的 SHA-256。</summary>
    public static string HashText(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    // ────────────────────────────── 标记区块（CF-3 / CF-4）──────────────────────────────

    /// <summary>
    /// 在文本中插入或替换标记区块。
    /// </summary>
    /// <param name="original">原文件内容。</param>
    /// <param name="blockId">区块标识（同一文件可以有多个区块，例如 maven 与 gradle 各一个）。</param>
    /// <param name="body">区块内容（不含标记行）。</param>
    /// <param name="xmlStyle">是否使用 XML 注释形式的标记。</param>
    /// <remarks>
    /// <b>幂等的关键在"按 id 定位"而不是"按内容匹配"</b>：
    /// 如果靠"看起来像上次写的那段"来识别，用户手工改过一个字符就会导致重复插入。
    /// 因此每个区块的标记行里都带 id，替换时精确匹配它。
    /// </remarks>
    public static string UpsertMarkedBlock(string original, string blockId, string body, bool xmlStyle = false)
    {
        ArgumentNullException.ThrowIfNull(original);

        var (begin, end) = Markers(blockId, xmlStyle);
        var trimmedBody = body.TrimEnd('\r', '\n');

        var startIndex = original.IndexOf(begin, StringComparison.Ordinal);

        if (startIndex >= 0)
        {
            var endIndex = original.IndexOf(end, startIndex, StringComparison.Ordinal);
            if (endIndex >= 0)
            {
                var afterEnd = endIndex + end.Length;
                // 连同紧随其后的一个换行一起替换，避免反复插入留下空行。
                while (afterEnd < original.Length && (original[afterEnd] == '\r' || original[afterEnd] == '\n'))
                {
                    afterEnd++;
                }

                var replacement = begin + "\n" + trimmedBody + "\n" + end + "\n";
                return original[..startIndex] + replacement + original[afterEnd..];
            }
        }

        var sb = new StringBuilder(original);
        if (sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }

        sb.Append(begin).Append('\n').Append(trimmedBody).Append('\n').Append(end).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// 移除标记区块（CF-4：一键卸载）。
    /// </summary>
    public static string RemoveMarkedBlock(string original, string blockId, bool xmlStyle = false)
    {
        ArgumentNullException.ThrowIfNull(original);

        var (begin, end) = Markers(blockId, xmlStyle);
        var startIndex = original.IndexOf(begin, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return original;
        }

        var endIndex = original.IndexOf(end, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            return original;
        }

        var afterEnd = endIndex + end.Length;
        while (afterEnd < original.Length && (original[afterEnd] == '\r' || original[afterEnd] == '\n'))
        {
            afterEnd++;
        }

        // 连同标记前面的一个多余空行一起删掉，避免反复"写入-移除"累积空行。
        var cutStart = startIndex;
        if (cutStart > 0 && original[cutStart - 1] == '\n')
        {
            cutStart--;
            if (cutStart > 0 && original[cutStart - 1] == '\r')
            {
                cutStart--;
            }
        }

        return original[..cutStart] + original[afterEnd..];
    }

    /// <summary>判断文本中是否存在指定区块。</summary>
    public static bool HasMarkedBlock(string original, string blockId, bool xmlStyle = false)
    {
        var (begin, _) = Markers(blockId, xmlStyle);
        return original.Contains(begin, StringComparison.Ordinal);
    }

    private static (string Begin, string End) Markers(string blockId, bool xmlStyle) =>
        xmlStyle
            ? ($"<!-- >>> EnvStation >>> id={blockId} -->", $"<!-- <<< EnvStation <<< id={blockId} -->")
            : ($"{MarkerBegin} id={blockId}", $"{MarkerEnd} id={blockId}");

    // ────────────────────────────── 凭据保护（CF-6）──────────────────────────────

    /// <summary>
    /// 需要脱敏的字段名（出现即掩码）。
    /// </summary>
    /// <remarks>
    /// 覆盖 Maven settings.xml 的 <c>&lt;password&gt;</c> / <c>&lt;passphrase&gt;</c>、
    /// npm 的 <c>_authToken</c>、pip 的 <c>extra-index-url</c> 里的 token 等。
    /// 名单刻意偏保守：多掩一个的代价是"日志少看到一点信息"，
    /// 漏掩一个的代价是用户的私服密码进了工单系统。
    /// </remarks>
    public static ImmutableArray<string> SecretFieldNames { get; } =
    [
        "password", "passwd", "passphrase", "secret", "token", "_authToken", "_auth",
        "apikey", "api-key", "accesskey", "access-key", "privatekey", "private-key",
        "credentials", "authorization",
    ];

    /// <summary>
    /// 对文本做凭据脱敏（CF-6 / XML-M6）。
    /// </summary>
    /// <remarks>
    /// 同时处理两种常见形态：<c>&lt;password&gt;xxx&lt;/password&gt;</c> 与 <c>password = xxx</c>。
    /// 目标不是"完美解析"，而是"绝不把明文凭据带进日志"。
    /// </remarks>
    public static string RedactSecrets(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        var result = content;

        foreach (var field in SecretFieldNames)
        {
            // XML / INI 成对标签形态
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                $"(<{System.Text.RegularExpressions.Regex.Escape(field)}[^>]*>)([^<]+)(</{System.Text.RegularExpressions.Regex.Escape(field)}>)",
                "$1***$3",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));

            // key = value / key: value 形态
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                $"(^|[\\s:=/])({System.Text.RegularExpressions.Regex.Escape(field)})(\\s*[=:]\\s*)(\\S+)",
                "$1$2$3***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.Multiline
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }

        return result;
    }

    /// <summary>检测文本中是否含疑似明文凭据（用于 XML 校验的告警）。</summary>
    public static ImmutableArray<string> FindSecretFields(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var found = ImmutableArray.CreateBuilder<string>();
        foreach (var field in SecretFieldNames)
        {
            if (content.Contains($"<{field}", StringComparison.OrdinalIgnoreCase)
                || content.Contains(field + "=", StringComparison.OrdinalIgnoreCase)
                || content.Contains(field + ":", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(field);
            }
        }

        return found.ToImmutable();
    }

    // ────────────────────────────── 备份记录序列化 ──────────────────────────────

    private static string Serialize(ConfigBackup backup)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"backupId\": ").Append(Json(backup.BackupId)).Append(",\n");
        sb.Append("  \"originalPath\": ").Append(Json(backup.OriginalPath)).Append(",\n");
        sb.Append("  \"contentHash\": ").Append(Json(backup.ContentHash)).Append(",\n");
        sb.Append("  \"existed\": ").Append(backup.Existed ? "true" : "false").Append(",\n");
        sb.Append("  \"backupPath\": ").Append(Json(backup.BackupPath)).Append(",\n");
        sb.Append("  \"createdAt\": ").Append(Json(backup.CreatedAt.ToString("O", CultureInfo.InvariantCulture))).Append(",\n");
        sb.Append("  \"note\": ").Append(backup.Note is null ? "null" : Json(backup.Note)).Append('\n');
        sb.Append("}\n");
        return sb.ToString();
    }

    private static ConfigBackup? Deserialize(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;

            return new ConfigBackup(
                root.GetProperty("backupId").GetString() ?? string.Empty,
                root.GetProperty("originalPath").GetString() ?? string.Empty,
                root.GetProperty("contentHash").GetString() ?? string.Empty,
                root.GetProperty("existed").GetBoolean(),
                root.GetProperty("backupPath").GetString() ?? string.Empty,
                root.GetProperty("createdAt").GetDateTimeOffset(),
                root.TryGetProperty("note", out var note) && note.ValueKind == System.Text.Json.JsonValueKind.String
                    ? note.GetString()
                    : null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Json(string? text)
    {
        if (text is null)
        {
            return "null";
        }

        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ')
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
