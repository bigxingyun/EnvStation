using System.Collections.Immutable;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;

namespace EnvStation.Core.Actions.Builtin;

// ══════════════════════════════════════════════════════════════════════════════
//  A03 解压与封装（3 个动作 · CAP.ARCHIVE）
//
//  解压是"把外部数据变成自己磁盘上的文件"这一步，因此是路径穿越（Zip Slip）
//  与压缩炸弹两类攻击的正面战场。四条硬性纪律：
//
//    1. **逐条目校验落点**：解压后的绝对路径必须仍在目标目录内。
//       只检查条目名里有没有 ".." 是不够的——绝对路径、盘符、UNC 前缀都要挡。
//    2. **拒绝符号链接与硬链接**：链接可以把"写入目标目录"变成"写入任意位置"，
//       而且它绕过所有基于路径字符串的检查。
//    3. **体积比与总量双限**：单个条目、总产出、压缩比三者都要查。
//       只查总量挡不住"先写 10 GB 再删"这类中途撑爆磁盘的构造。
//    4. **边解边记配额**：与下载同理，事后检查等于没有防护。
//
//  支持的格式刻意只用 BCL 能力：**zip / tar / tar.gz**。
//  这三种正好覆盖 JDK、Python、Node、Go、Maven 的实际发行包；7z 不在 BCL 中，
//  引入第三方库会给解压这条攻击面最大的路径增加依赖（见 TD-16）。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>归档格式。</summary>
internal enum ArchiveFormat
{
    /// <summary>zip。</summary>
    Zip,

    /// <summary>tar（未压缩）。</summary>
    Tar,

    /// <summary>tar.gz / tgz。</summary>
    TarGz,
}

/// <summary>归档中的一个条目（用于预检与列表）。</summary>
/// <param name="Key">条目路径（相对）。</param>
/// <param name="Size">声明的原始大小（可能为 -1，表示未知）。</param>
/// <param name="CompressedSize">压缩后大小（可能为 -1）。</param>
/// <param name="IsDirectory">是否为目录。</param>
/// <param name="IsLink">是否为符号链接或硬链接。</param>
internal sealed record ArchiveEntryInfo(string Key, long Size, long CompressedSize, bool IsDirectory, bool IsLink);

/// <summary>归档读取与安全检查。</summary>
internal static class ArchiveInspector
{
    /// <summary>压缩比上限：解压后 / 压缩后 超过该倍数即视为压缩炸弹。</summary>
    internal const int MaxCompressionRatio = 200;

    /// <summary>单个条目的最大解压体积（1 GB）。</summary>
    internal const long MaxSingleEntryBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>按文件名推断格式；无法判断时返回 null。</summary>
    internal static ArchiveFormat? DetectFormat(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();

        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal))
        {
            return ArchiveFormat.TarGz;
        }

        if (name.EndsWith(".tar", StringComparison.Ordinal))
        {
            return ArchiveFormat.Tar;
        }

        if (name.EndsWith(".zip", StringComparison.Ordinal)
            || name.EndsWith(".jar", StringComparison.Ordinal)
            || name.EndsWith(".whl", StringComparison.Ordinal))
        {
            return ArchiveFormat.Zip;
        }

        return null;
    }

    /// <summary>枚举条目（zip 与 tar 家族统一）。</summary>
    internal static Result<ImmutableArray<ArchiveEntryInfo>> List(string archivePath, ArchiveFormat format)
    {
        try
        {
            switch (format)
            {
                case ArchiveFormat.Zip:
                    using (var zip = ZipFile.OpenRead(archivePath))
                    {
                        return Result<ImmutableArray<ArchiveEntryInfo>>.Ok(
                        [
                            .. zip.Entries.Select(static e => new ArchiveEntryInfo(
                                e.FullName,
                                e.Length,
                                e.CompressedLength,
                                e.FullName.EndsWith('/') || e.Name.Length == 0,
                                // zip 的"符号链接"通过外部属性位标记；BCL 不直接暴露，
                                // 因此这里保守地按"名字以 / 结尾"判断目录，链接在解压时按普通文件处理。
                                false)),
                        ]);
                    }

                case ArchiveFormat.Tar:
                case ArchiveFormat.TarGz:
                    {
                        var entries = ImmutableArray.CreateBuilder<ArchiveEntryInfo>();
                        foreach (var entry in ReadTarEntries(archivePath, format))
                        {
                            entries.Add(entry);
                        }

                        return Result<ImmutableArray<ArchiveEntryInfo>>.Ok(entries.ToImmutable());
                    }

                default:
                    return Result<ImmutableArray<ArchiveEntryInfo>>.Fail(
                        EnvStationErrorCodes.ActionArgumentInvalid,
                        $"不支持的归档格式：{format}");
            }
        }
        catch (InvalidDataException ex)
        {
            return Result<ImmutableArray<ArchiveEntryInfo>>.Fail(
                EnvStationErrorCodes.ActionFailed,
                $"归档已损坏或格式不符：{ex.Message}",
                "重新下载该文件，再用 envstation.net.verify_hash 校验哈希。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<ImmutableArray<ArchiveEntryInfo>>.Fail(
                EnvStationErrorCodes.PathNotWritable, $"无法读取归档：{ex.Message}");
        }
    }

    private static IEnumerable<ArchiveEntryInfo> ReadTarEntries(string archivePath, ArchiveFormat format)
    {
        using var file = File.OpenRead(archivePath);
        Stream source = format == ArchiveFormat.TarGz ? new GZipStream(file, CompressionMode.Decompress) : file;
        try
        {
            using var reader = new TarReader(source, leaveOpen: true);
            while (reader.GetNextEntry() is { } entry)
            {
                var isDirectory = entry.EntryType == TarEntryType.Directory;
                var isLink = entry.EntryType is TarEntryType.SymbolicLink
                    or TarEntryType.HardLink
                    or TarEntryType.GlobalExtendedAttributes;

                yield return new ArchiveEntryInfo(
                    entry.Name,
                    entry.Length,
                    -1,
                    isDirectory,
                    isLink);
            }
        }
        finally
        {
            if (format == ArchiveFormat.TarGz)
            {
                source.Dispose();
            }
        }
    }

    /// <summary>
    /// 校验条目名是否会逃出目标目录。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能只查 <c>".."</c></b>：逃逸方式至少有四种，只挡一种等于没挡。
    /// <list type="bullet">
    ///   <item><c>../../etc/passwd</c> —— 相对上跳（最经典的一种）；</item>
    ///   <item><c>/etc/passwd</c> 或 <c>\Windows\x</c> —— 绝对路径，<c>Path.Combine</c> 会直接丢弃前缀；</item>
    ///   <item><c>C:\Windows\x</c> —— 带盘符；</item>
    ///   <item><c>\\server\share\x</c> —— UNC 路径，会写到网络共享上。</item>
    /// </list>
    /// 因此判定方式是"先规范化、再比较前缀"，而不是字符串匹配。
    /// </remarks>
    internal static Result<string> ResolveSafeTarget(string destinationRoot, string entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey))
        {
            return Result<string>.Fail(EnvStationErrorCodes.ArchivePathTraversal, "归档中存在空条目名。");
        }

        // ⚠ 检查顺序至关重要：必须先判断"绝对 / UNC / 带盘符"，再做清洗。
        //
        // 反例（这是一次真实缺陷）：如果先 TrimStart('/') 再去查 "//" 前缀，
        // 那么 \\attacker\share\x 会被清洗成 attacker/share/x —— 一个完全正常的相对路径，
        // 于是 UNC 逃逸就这么溜过去了。
        if (entryKey.StartsWith(@"\\", StringComparison.Ordinal)
            || entryKey.StartsWith("//", StringComparison.Ordinal))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ArchivePathTraversal,
                $"归档条目 {entryKey} 是网络路径（UNC），已拒绝解压。",
                "UNC 条目会写到网络共享，而不是目标目录。已中止解压，未写入任何文件。");
        }

        if (entryKey.StartsWith('/') || entryKey.StartsWith('\\'))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ArchivePathTraversal,
                $"归档条目 {entryKey} 是绝对路径，已拒绝解压。",
                "绝对路径条目会忽略目标目录。已中止解压，未写入任何文件。");
        }

        // 盘符（C:）与 NTFS 备用数据流（file.txt:evil）都在这里被挡掉。
        if (entryKey.Contains(':', StringComparison.Ordinal))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ArchivePathTraversal,
                $"归档条目 {entryKey} 含冒号（盘符或数据流），已拒绝解压。",
                "正常打包不会产生这种条目。已中止解压，未写入任何文件。");
        }

        var normalizedKey = entryKey.Replace('\\', '/');

        if (normalizedKey.Length == 0)
        {
            return Result<string>.Fail(EnvStationErrorCodes.ArchivePathTraversal, "归档中存在空条目路径。");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(destinationRoot, normalizedKey));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ArchivePathTraversal,
                $"归档条目 {entryKey} 的路径无法解析：{ex.Message}");
        }

        var root = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ArchivePathTraversal,
                $"归档条目 {entryKey} 会解压到目标目录之外（{full}），已拒绝。",
                "这是典型的 Zip Slip 构造。已中止解压，未写入任何文件。");
        }

        return Result<string>.Ok(full);
    }
}

/// <summary><c>envstation.archive.extract</c>：解压 zip / tar / tar.gz，含穿越与炸弹防护。</summary>
internal sealed class ArchiveExtractAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.archive.extract",
        "1.0.0",
        CapabilityIds.Archive,
        "解压归档",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 900,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("file", true, "要解压的归档路径"),
            Path_("dest", true, "解压目标目录（必须落在授权目录内）"),
            new ParameterSpec("strip_components", ParameterType.Integer, false,
                "去掉最前面的几层目录，与 tar --strip-components 同义；" +
                "Node 的 tar.gz 用 1 即可把内容提到根目录", Minimum: 0, Maximum: 8, DefaultValue: "0"),
            StrArray("include", false, "只解压匹配这些前缀的条目；留空表示全部"),
            Bool("overwrite", "目标文件已存在时是否覆盖", true),
            Bool("dry_run", "只做安全检查与统计，不写入任何文件", false),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var fileGuard = context.GuardPath(arguments.GetString("file")!, "file");
        if (fileGuard.IsFailure)
        {
            return Fail(fileGuard.Error.Code, fileGuard.Error.Message, fileGuard.Error.Remediation);
        }

        var destGuard = context.GuardPath(arguments.GetString("dest")!, "dest");
        if (destGuard.IsFailure)
        {
            return Fail(destGuard.Error.Code, destGuard.Error.Message, destGuard.Error.Remediation);
        }

        var archivePath = fileGuard.Value;
        var destination = destGuard.Value;

        if (!File.Exists(archivePath))
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"归档文件不存在：{archivePath}");
        }

        var format = ArchiveInspector.DetectFormat(archivePath);
        if (format is null)
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"无法从文件名判断归档格式：{Path.GetFileName(archivePath)}",
                "支持 .zip / .jar / .whl / .tar / .tar.gz / .tgz。" +
                "7z 与 rar 需先用其他工具解压，再对解压结果执行 envstation.local.install。");
        }

        var strip = (int)arguments.GetInt64("strip_components", 0);
        var include = arguments.GetStringArray("include");
        var overwrite = arguments.GetBoolean("overwrite", true);
        var dryRun = arguments.GetBoolean("dry_run", false);

        // ① 预检：先读目录，检查全部条目，再决定是否动磁盘。
        //    顺序很重要——"先解压一半再发现穿越"和"先发现再拒绝"是完全不同的后果。
        var listed = ArchiveInspector.List(archivePath, format.Value);
        if (listed.IsFailure)
        {
            return Fail(listed.Error.Code, listed.Error.Message, listed.Error.Remediation);
        }

        var entries = listed.Value;
        var compressedSize = new FileInfo(archivePath).Length;

        long declaredTotal = 0;
        var plan = ImmutableArray.CreateBuilder<(ArchiveEntryInfo Entry, string Target)>();
        var skippedByFilter = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                continue;
            }

            if (entry.IsLink)
            {
                return Fail(
                    EnvStationErrorCodes.ArchivePathTraversal,
                    $"归档中包含链接条目：{entry.Key}",
                    "链接条目会把文件写到目标目录之外，并绕过按路径的检查。" +
                    "含链接的归档一律拒绝解压。");
            }

            if (!MatchesFilter(entry.Key, include, strip, out var strippedKey))
            {
                skippedByFilter++;
                continue;
            }

            if (strippedKey.Length == 0)
            {
                skippedByFilter++;
                continue;
            }

            var safe = ArchiveInspector.ResolveSafeTarget(destination, strippedKey);
            if (safe.IsFailure)
            {
                return Fail(safe.Error.Code, safe.Error.Message, safe.Error.Remediation);
            }

            // 同时校验**原始条目名**。
            //
            // 为什么两道都要查（这是一次真实缺陷）：strip_components 与 include 过滤都会对条目名
            // 做规范化（替换分隔符、去掉前导斜杠），而规范化恰好会把
            // \\attacker\share\x 变成 attacker/share/x —— 一个看起来完全正常的相对路径。
            // 只校验剥离后的结果，UNC 逃逸就会从这道规范化里溜过去。
            var rawSafe = ArchiveInspector.ResolveSafeTarget(destination, entry.Key);
            if (rawSafe.IsFailure)
            {
                return Fail(rawSafe.Error.Code, rawSafe.Error.Message, rawSafe.Error.Remediation);
            }

            if (entry.Size > ArchiveInspector.MaxSingleEntryBytes)
            {
                return Fail(
                    EnvStationErrorCodes.NetSizeLimit,
                    $"归档条目 {entry.Key} 声明的解压大小 {entry.Size} 字节超过单条上限。",
                    "这很可能是压缩炸弹。");
            }

            declaredTotal += Math.Max(0, entry.Size);
            plan.Add((entry, safe.Value));
        }

        // ② 体积比检查：声明总量 / 压缩包大小。
        if (declaredTotal > 0 && compressedSize > 0)
        {
            var ratio = declaredTotal / (double)compressedSize;
            if (ratio > ArchiveInspector.MaxCompressionRatio)
            {
                return Fail(
                    EnvStationErrorCodes.NetSizeLimit,
                    $"解压后的声明体积是归档的 {ratio:F0} 倍（{Format(declaredTotal)} / {Format(compressedSize)}），" +
                    $"超过 {ArchiveInspector.MaxCompressionRatio} 倍上限。",
                    "疑似压缩炸弹，解压会耗尽磁盘与内存。已拒绝解压。");
            }
        }

        // ③ 配额预检：按声明总量先做一次，能在写盘前就发现"装不下"。
        var quotaCheck = context.Quota.TryConsume(QuotaKind.Extract, declaredTotal);
        if (quotaCheck.IsFailure)
        {
            return Fail(quotaCheck.Error.Code, quotaCheck.Error.Message, quotaCheck.Error.Remediation);
        }

        var fileQuota = context.Quota.TryConsume(QuotaKind.FileWrite, plan.Count);
        if (fileQuota.IsFailure)
        {
            return Fail(fileQuota.Error.Code, fileQuota.Error.Message, fileQuota.Error.Remediation);
        }

        if (dryRun)
        {
            return Ok(
                $"预演：归档包含 {entries.Length} 个条目，将解压 {plan.Count} 个文件（声明 {Format(declaredTotal)}）到 {destination}。" +
                " 已通过路径穿越与体积比检查，未写入任何文件。",
                Outputs(
                    ("dry_run", "true"),
                    ("format", format.Value.ToString()),
                    ("entry_count", entries.Length.ToString(CultureInfo.InvariantCulture)),
                    ("extract_count", plan.Count.ToString(CultureInfo.InvariantCulture)),
                    ("skipped_by_filter", skippedByFilter.ToString(CultureInfo.InvariantCulture)),
                    ("declared_bytes", declaredTotal.ToString(CultureInfo.InvariantCulture))));
        }

        // ④ 真正解压。zip 用流式读取（不信任声明大小），tar 同理。
        Directory.CreateDirectory(destination);
        long written = 0;
        var writtenFiles = 0;

        try
        {
            switch (format.Value)
            {
                case ArchiveFormat.Zip:
                    using (var zip = ZipFile.OpenRead(archivePath))
                    {
                        var byKey = zip.Entries.ToDictionary(static e => e.FullName, StringComparer.Ordinal);

                        foreach (var (entry, target) in plan)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!byKey.TryGetValue(entry.Key, out var zipEntry))
                            {
                                continue;
                            }

                            var dir = Path.GetDirectoryName(target);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }

                            using var source = zipEntry.Open();
                            written += WriteBounded(context, source, target, overwrite, declaredTotal, ref writtenFiles);
                        }
                    }

                    break;

                case ArchiveFormat.Tar:
                case ArchiveFormat.TarGz:
                    {
                        var targets = plan.ToDictionary(static p => p.Entry.Key, static p => p.Target, StringComparer.Ordinal);

                        using var file = File.OpenRead(archivePath);
                        Stream source = format.Value == ArchiveFormat.TarGz
                            ? new GZipStream(file, CompressionMode.Decompress)
                            : file;

                        try
                        {
                            using var reader = new TarReader(source, leaveOpen: true);
                            while (reader.GetNextEntry() is { } tarEntry)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                if (tarEntry.EntryType != TarEntryType.RegularFile
                                    || !targets.TryGetValue(tarEntry.Name, out var target))
                                {
                                    continue;
                                }

                                var dir = Path.GetDirectoryName(target);
                                if (!string.IsNullOrEmpty(dir))
                                {
                                    Directory.CreateDirectory(dir);
                                }

                                var stream = tarEntry.DataStream;
                                written += stream is null
                                    ? 0
                                    : WriteBounded(context, stream, target, overwrite, declaredTotal, ref writtenFiles);
                            }
                        }
                        finally
                        {
                            if (format.Value == ArchiveFormat.TarGz)
                            {
                                source.Dispose();
                            }
                        }

                        break;
                    }

                default:
                    return Fail(EnvStationErrorCodes.ActionArgumentInvalid, $"不支持的格式 {format.Value}");
            }
        }
        catch (InvalidDataException ex)
        {
            return Fail(
                EnvStationErrorCodes.ActionFailed,
                $"解压过程中数据无效：{ex.Message}",
                "归档可能已损坏。重新下载后用 envstation.net.verify_hash 校验哈希。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"解压失败：{ex.Message}");
        }

        return Ok(
            $"已解压 {writtenFiles} 个文件（{Format(written)}）到 {destination}。",
            Outputs(
                ("dry_run", "false"),
                ("format", format.Value.ToString()),
                ("extract_count", writtenFiles.ToString(CultureInfo.InvariantCulture)),
                ("bytes", written.ToString(CultureInfo.InvariantCulture)),
                ("dest", destination),
                ("skipped_by_filter", skippedByFilter.ToString(CultureInfo.InvariantCulture))),
            touched: [destination]);
    }

    /// <summary>写单个条目，并在此过程中继续累加配额（防止声明大小造假）。</summary>
    private static long WriteBounded(
        ActionExecutionContext context,
        Stream source,
        string target,
        bool overwrite,
        long declaredTotal,
        ref int writtenFiles)
    {
        if (!overwrite && File.Exists(target))
        {
            return 0;
        }

        long entryBytes = 0;
        var buffer = new byte[81920];

        using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                entryBytes += read;

                // 单个条目超过声明总量的 2 倍即判定声明造假，立即中止。
                if (declaredTotal > 0 && entryBytes > declaredTotal * 2)
                {
                    throw new InvalidDataException(
                        $"条目 {Path.GetFileName(target)} 的实际大小远超声明值，疑似压缩炸弹。");
                }

                destination.Write(buffer, 0, read);
            }
        }

        writtenFiles++;

        // 实际写入量超过预扣量时补扣；不足的部分不退还（保守）——
        // 退还逻辑会让"伪造小体积声明"变成一种配额套利手段。
        var extra = entryBytes - (declaredTotal / Math.Max(1, writtenFiles));
        if (extra > 0)
        {
            _ = context.Quota.TryConsume(QuotaKind.Extract, extra);
        }

        return entryBytes;
    }

    /// <summary>
    /// 计算条目在目标目录中的相对路径，并判断是否被 include 过滤掉。
    /// </summary>
    /// <remarks>
    /// <b>关于 strip_components</b>：Node、Go 这类运行时的发行包外面都套了一层版本目录
    /// （<c>node-v20.11.0-win-x64/</c>），去掉一层才能得到干净的安装目录。
    /// 层级不足的条目（例如包根目录下的 LICENSE）会被整条跳过——
    /// 这与 <c>tar --strip-components</c> 的语义一致。
    /// </remarks>
    private static bool MatchesFilter(
        string key, ImmutableArray<string> include, int stripComponents, out string strippedKey)
    {
        var normalized = key.Replace('\\', '/').TrimStart('/');
        strippedKey = normalized;

        if (stripComponents > 0)
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= stripComponents)
            {
                strippedKey = string.Empty;
                return false;
            }

            strippedKey = string.Join('/', parts.Skip(stripComponents));
        }

        if (include.Length > 0)
        {
            var matched = include.Any(prefix =>
                normalized.StartsWith(prefix.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase));

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1} MB"
        : $"{bytes / (double)(1L << 10):F1} KB";
}

/// <summary><c>envstation.archive.list</c>：列出压缩包内容与总体积（解压前预检）。</summary>
internal sealed class ArchiveListAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.archive.list",
        "1.0.0",
        CapabilityIds.Inspect,
        "列出归档内容",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 300,
        TouchedResources: ["filesystem.read"],
        Parameters:
        [
            Path_("file", true, "归档路径"),
            new ParameterSpec("max_entries", ParameterType.Integer, false,
                "最多返回多少条明细，用于控制输出体积", Minimum: 1, Maximum: 100000, DefaultValue: "200"),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var guard = context.GuardPath(arguments.GetString("file")!, "file");
        if (guard.IsFailure)
        {
            return Fail(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var path = guard.Value;
        if (!File.Exists(path))
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"归档不存在：{path}");
        }

        var format = ArchiveInspector.DetectFormat(path);
        if (format is null)
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"无法识别归档格式：{Path.GetFileName(path)}");
        }

        var listed = ArchiveInspector.List(path, format.Value);
        if (listed.IsFailure)
        {
            return Fail(listed.Error.Code, listed.Error.Message, listed.Error.Remediation);
        }

        var entries = listed.Value;
        var maxEntries = (int)arguments.GetInt64("max_entries", 200);

        long totalSize = 0;
        var files = 0;
        var directories = 0;
        var links = 0;
        var risks = ImmutableArray.CreateBuilder<string>();

        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                directories++;
                continue;
            }

            files++;

            if (entry.IsLink)
            {
                links++;
                risks.Add($"含链接条目：{entry.Key}");
            }

            if (entry.Size > 0)
            {
                totalSize += entry.Size;
            }

            // 顺带做一次穿越预检：列出内容时就把危险条目报出来，
            // 让"先 list 再 extract"这条推荐流程真正有用。
            if (ArchiveInspector.ResolveSafeTarget(Path.GetTempPath(), entry.Key).IsFailure)
            {
                risks.Add($"可疑路径：{entry.Key}");
            }
        }

        var compressed = new FileInfo(path).Length;
        var ratio = compressed > 0 && totalSize > 0 ? totalSize / (double)compressed : 0;

        var details = entries
            .Where(static e => !e.IsDirectory)
            .Take(maxEntries)
            .Select(static e => $"{e.Key} ({e.Size} B)");

        return Ok(
            $"归档 {Path.GetFileName(path)}（{format.Value}）：{files} 个文件、{directories} 个目录，" +
            $"解压后约 {Format(totalSize)}，压缩比 {ratio:F1}。" +
            (risks.Count > 0 ? $" 发现 {risks.Count} 项风险：{string.Join("；", risks.Take(5))}" : " 未发现风险条目。"),
            Outputs(
                ("format", format.Value.ToString()),
                ("file_count", files.ToString(CultureInfo.InvariantCulture)),
                ("directory_count", directories.ToString(CultureInfo.InvariantCulture)),
                ("link_count", links.ToString(CultureInfo.InvariantCulture)),
                ("uncompressed_bytes", totalSize.ToString(CultureInfo.InvariantCulture)),
                ("compressed_bytes", compressed.ToString(CultureInfo.InvariantCulture)),
                ("compression_ratio", ratio.ToString("F2", CultureInfo.InvariantCulture)),
                ("risk_count", risks.Count.ToString(CultureInfo.InvariantCulture)),
                ("risks", string.Join(" | ", risks)),
                ("entries", string.Join(" | ", details))));
    }

    private static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB" : $"{bytes / (double)(1L << 20):F1} MB";
}

/// <summary><c>envstation.archive.create</c>：打包目录（用于迁移/备份）。</summary>
internal sealed class ArchiveCreateAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.archive.create",
        "1.0.0",
        CapabilityIds.Archive,
        "打包目录",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 900,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("dir", true, "要打包的目录"),
            Path_("out", true, "输出归档路径（扩展名决定格式：.zip / .tar.gz / .tar）"),
            StrArray("include", false, "只打包匹配这些前缀的相对路径；留空表示全部"),
            Bool("overwrite", "输出文件已存在时是否覆盖", false),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var dirGuard = context.GuardPath(arguments.GetString("dir")!, "dir");
        if (dirGuard.IsFailure)
        {
            return Fail(dirGuard.Error.Code, dirGuard.Error.Message, dirGuard.Error.Remediation);
        }

        var outGuard = context.GuardPath(arguments.GetString("out")!, "out");
        if (outGuard.IsFailure)
        {
            return Fail(outGuard.Error.Code, outGuard.Error.Message, outGuard.Error.Remediation);
        }

        var source = dirGuard.Value;
        var output = outGuard.Value;

        if (!Directory.Exists(source))
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"源目录不存在：{source}");
        }

        var format = ArchiveInspector.DetectFormat(output);
        if (format is null)
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"无法从输出文件名判断格式：{Path.GetFileName(output)}",
                "支持 .zip / .tar / .tar.gz / .tgz。");
        }

        var overwrite = arguments.GetBoolean("overwrite", false);
        if (File.Exists(output) && !overwrite)
        {
            return Fail(
                EnvStationErrorCodes.ActionFailed,
                $"输出文件已存在：{output}",
                "确认覆盖时把 overwrite 设为 true。");
        }

        var include = arguments.GetStringArray("include");

        // 打包自己所在的目录会导致无限递归，必须在动手前拦住。
        // 判定要用**输出文件本身的路径**与源目录比较，而不是用它的父目录。
        // 反例（这是一次真实缺陷）：源目录 D:\x、输出 D:\x\self.zip 时，
        // 输出的父目录恰好等于源目录，"父目录是否是源目录的子目录"判为否，检查就漏了。
        var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var outputFull = Path.GetFullPath(output);

        if (outputFull.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputFull, sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"输出文件位于源目录内部（{output}），打包过程中会把自己也打进去。",
                "把输出放到源目录之外。");
        }

        var files = EnumerateFiles(source, include).ToArray();
        var quota = context.Quota.TryConsume(QuotaKind.FileWrite, 1);
        if (quota.IsFailure)
        {
            return Fail(quota.Error.Code, quota.Error.Message, quota.Error.Remediation);
        }

        try
        {
            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            switch (format.Value)
            {
                case ArchiveFormat.Zip:
                    if (File.Exists(output))
                    {
                        File.Delete(output);
                    }

                    using (var zip = ZipFile.Open(output, ZipArchiveMode.Create))
                    {
                        foreach (var file in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            zip.CreateEntryFromFile(file, Path.GetRelativePath(source, file).Replace('\\', '/'));
                        }
                    }

                    break;

                case ArchiveFormat.Tar:
                case ArchiveFormat.TarGz:
                    {
                        using var outputStream = File.Create(output);
                        Stream target = format.Value == ArchiveFormat.TarGz
                            ? new GZipStream(outputStream, CompressionLevel.Optimal)
                            : outputStream;

                        try
                        {
                            using var writer = new TarWriter(target, leaveOpen: true);
                            foreach (var file in files)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                writer.WriteEntry(file, Path.GetRelativePath(source, file).Replace('\\', '/'));
                            }
                        }
                        finally
                        {
                            if (format.Value == ArchiveFormat.TarGz)
                            {
                                target.Dispose();
                            }
                        }

                        break;
                    }

                default:
                    return Fail(EnvStationErrorCodes.ActionArgumentInvalid, $"不支持的格式 {format.Value}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"打包失败：{ex.Message}");
        }

        var size = new FileInfo(output).Length;
        return Ok(
            $"已打包 {files.Length} 个文件到 {output}（{size / 1024.0 / 1024.0:F1} MB）。",
            Outputs(
                ("format", format.Value.ToString()),
                ("file_count", files.Length.ToString(CultureInfo.InvariantCulture)),
                ("bytes", size.ToString(CultureInfo.InvariantCulture)),
                ("out", output)),
            touched: [output]);
    }

    private static IEnumerable<string> EnumerateFiles(string root, ImmutableArray<string> include)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (include.Length == 0)
            {
                yield return file;
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (include.Any(prefix =>
                relative.StartsWith(prefix.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase)))
            {
                yield return file;
            }
        }
    }
}
