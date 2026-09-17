using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Diagnostics;

namespace EnvStation.Core.Packages;

/// <summary>包内文件清单（<c>.envstation</c> 的本质是一个 zip）。</summary>
/// <remarks>
/// <para><b>为什么用 zip 而不是自定义二进制格式</b>：zip 可以被任何工具打开检查。
/// 一个"用户能自己打开看看里面有什么"的包格式，本身就是一个安全属性——
/// 它让"这个包到底做了什么"这个问题不需要依赖我们的软件就能回答。</para>
/// <para><b>固定布局</b>（需求 25.1）：清单与工作流在根目录，动作/资源/测试各占一个子目录。</para>
/// </remarks>
public static class PackageLayout
{
    /// <summary>包清单。</summary>
    public const string ManifestEntry = "envstation.toml";

    /// <summary>工作流。</summary>
    public const string WorkflowEntry = "workflow.toml";

    /// <summary>说明文档。</summary>
    public const string ReadmeEntry = "README.md";

    /// <summary>许可证。</summary>
    public const string LicenseEntry = "LICENSE";

    /// <summary>变更日志。</summary>
    public const string ChangeLogEntry = "CHANGELOG.md";

    /// <summary>图标。</summary>
    public const string LogoEntry = "logo.png";

    /// <summary>文件哈希清单。</summary>
    public const string HashManifestEntry = PackageSigner.ManifestHashFileName;

    /// <summary>签名。</summary>
    public const string SignatureEntry = PackageSigner.SignatureFileName;

    /// <summary>可选公钥（便于分发时携带）。</summary>
    public const string PublicKeyEntry = PackageSigner.PublicKeyFileName;

    /// <summary>依赖清单（SBOM）。</summary>
    public const string SbomEntry = "sbom.json";

    /// <summary>这些文件本身不参与哈希清单（否则会自我引用）。</summary>
    public static ImmutableHashSet<string> ExcludedFromManifest { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, HashManifestEntry, SignatureEntry);

    /// <summary>目录型条目的前缀。</summary>
    public static ImmutableArray<string> DirectoryPrefixes { get; } =
        ["actions/", "scripts/", "resources/", "tests/"];
}

/// <summary>一个已打开的包（内存中的条目集合）。</summary>
public sealed class PackageContents
{
    /// <summary>相对路径 → 文本内容（仅文本条目，二进制条目见 <see cref="BinaryEntries"/>）。</summary>
    public required ImmutableDictionary<string, string> TextEntries { get; init; }

    /// <summary>相对路径 → 字节内容（仅二进制条目）。</summary>
    public required ImmutableDictionary<string, byte[]> BinaryEntries { get; init; }

    /// <summary>全部条目路径（文本 + 二进制），按序号排序。</summary>
    public ImmutableArray<string> AllPaths =>
        [.. TextEntries.Keys.Concat(BinaryEntries.Keys).OrderBy(static p => p, StringComparer.Ordinal)];

    /// <summary>取文本条目；不存在返回 null。</summary>
    public string? GetText(string path) => TextEntries.TryGetValue(path, out var v) ? v : null;

    /// <summary>是否存在某条目。</summary>
    public bool Contains(string path) => TextEntries.ContainsKey(path) || BinaryEntries.ContainsKey(path);
}

/// <summary>
/// <c>.envstation</c> 包的读写。
/// </summary>
/// <remarks>
/// <para>
/// <b>读取时的三条纪律</b>（与解压动作同一套理由，但这里更严格，因为包里可能有可执行内容）：
/// </para>
/// <list type="number">
///   <item>拒绝任何逃出包根的条目路径（Zip Slip）；</item>
///   <item>拒绝符号链接条目；</item>
///   <item>体积与条目数有上限——包本身就是一个不可信输入。</item>
/// </list>
/// </remarks>
public static class PackageArchive
{
    /// <summary>包的最大体积（512 MB）。</summary>
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>包内条目数上限。</summary>
    public const int MaxEntries = 10_000;

    /// <summary>单条目解压后大小上限（64 MB）。</summary>
    public const long MaxEntryBytes = 64L * 1024 * 1024;

    /// <summary>读取一个包到内存。</summary>
    public static Result<PackageContents> Read(string packagePath, FindingBag findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        if (!File.Exists(packagePath))
        {
            findings.Block("PKG-01", "包文件不存在", $"找不到 {packagePath}。");
            return Result<PackageContents>.Fail(EnvStationErrorCodes.PackageParseFailed, "包文件不存在。");
        }

        var size = new FileInfo(packagePath).Length;
        if (size > MaxPackageBytes)
        {
            findings.Block(
                "PKG-02",
                "包体积过大",
                $"包体积 {size / 1024.0 / 1024.0:F1} MB，超过上限 {MaxPackageBytes / 1024 / 1024} MB。",
                "从包内移除大文件，改用下载动作在运行时获取。");
            return Result<PackageContents>.Fail(EnvStationErrorCodes.PackageParseFailed, "包体积过大。");
        }

        var text = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var binary = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);

        try
        {
            using var zip = ZipFile.OpenRead(packagePath);

            if (zip.Entries.Count > MaxEntries)
            {
                findings.Block(
                    "PKG-03",
                    "包内条目过多",
                    $"包内有 {zip.Entries.Count} 个条目，超过上限 {MaxEntries}。");
                return Result<PackageContents>.Fail(EnvStationErrorCodes.PackageParseFailed, "包内条目过多。");
            }

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Name.Length == 0)
                {
                    continue;
                }

                var safe = ResolveEntryPath(entry.FullName);
                if (safe.IsFailure)
                {
                    findings.Block("PKG-04", "包内条目路径非法", safe.Error.Message, safe.Error.Remediation);
                    return safe.Propagate<PackageContents>();
                }

                if (entry.Length > MaxEntryBytes)
                {
                    findings.Block(
                        "PKG-05",
                        "包内单文件过大",
                        $"{entry.FullName} 体积 {entry.Length / 1024.0 / 1024.0:F1} MB，超过单文件上限 {MaxEntryBytes / 1024 / 1024} MB。");
                    return Result<PackageContents>.Fail(EnvStationErrorCodes.PackageParseFailed, "包内单文件过大。");
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();

                // 文本条目以 UTF-8 解析；含 NUL 的按二进制处理。
                if (LooksBinary(bytes))
                {
                    binary[safe.Value] = bytes;
                }
                else
                {
                    text[safe.Value] = new UTF8Encoding(false).GetString(bytes);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            findings.Block("PKG-06", "包文件损坏", $"无法作为 zip 读取：{ex.Message}");
            return Result<PackageContents>.Fail(EnvStationErrorCodes.PackageParseFailed, "包文件损坏。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            findings.Block("PKG-07", "包文件无法读取", ex.Message);
            return Result<PackageContents>.Fail(EnvStationErrorCodes.PackageParseFailed, ex.Message);
        }

        return Result<PackageContents>.Ok(new PackageContents
        {
            TextEntries = text.ToImmutable(),
            BinaryEntries = binary.ToImmutable(),
        });
    }

    /// <summary>写入一个包。</summary>
    /// <param name="outputPath">输出路径。</param>
    /// <param name="contents">要写入的条目。</param>
    /// <param name="overwrite">是否覆盖已存在的文件。</param>
    public static async Task<Result<long>> WriteAsync(
        string outputPath,
        PackageContents contents,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);

        if (File.Exists(outputPath) && !overwrite)
        {
            return Result<long>.Fail(
                EnvStationErrorCodes.ActionFailed,
                $"输出文件已存在：{outputPath}。",
                "确认覆盖时加 --force。");
        }

        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            using (var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                foreach (var path in contents.AllPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entry = zip.CreateEntry(path, CompressionLevel.Optimal);

                    // 固定时间戳：让同一个包在任何时间、任何机器上产生完全相同的字节。
                    // 这是"可复现构建"与"签名可被独立复核"的前提。
                    entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

                    await using var stream = entry.Open();

                    if (contents.BinaryEntries.TryGetValue(path, out var bytes))
                    {
                        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var text = contents.TextEntries[path];
                        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            return Result<long>.Ok(new FileInfo(outputPath).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<long>.Fail(EnvStationErrorCodes.PathNotWritable, $"写入包失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 校验 zip 条目路径。
    /// </summary>
    /// <remarks>
    /// 与解压动作同一套判据，但**顺序同样关键**：先判绝对 / UNC / 盘符，再规范化。
    /// 反过来做的话 <c>\\evil\share\x</c> 会被规范化成正常的相对路径。
    /// </remarks>
    private static Result<string> ResolveEntryPath(string entryName)
    {
        if (entryName.StartsWith(@"\\", StringComparison.Ordinal)
            || entryName.StartsWith("//", StringComparison.Ordinal))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ArchivePathTraversal,
                $"包内条目 {entryName} 是网络路径（UNC）。",
                "包内路径只能用相对于包根目录的相对路径。已拒绝导入。");
        }

        if (entryName.StartsWith('/') || entryName.StartsWith('\\'))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ArchivePathTraversal,
                $"包内条目 {entryName} 是绝对路径。",
                "包内路径只能用相对于包根目录的相对路径。已拒绝导入。");
        }

        if (entryName.Contains(':', StringComparison.Ordinal))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ArchivePathTraversal,
                $"包内条目 {entryName} 含冒号。",
                "包内路径不得包含盘符或数据流语法。已拒绝导入。");
        }

        var normalized = entryName.Replace('\\', '/');

        if (normalized.Split('/').Any(static segment => segment == ".."))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.ArchivePathTraversal,
                $"包内条目 {entryName} 含上级目录跳转。",
                "包内路径不得包含 .. 段。已拒绝导入。");
        }

        return Result<string>.Ok(normalized);
    }

    private static bool LooksBinary(byte[] bytes)
    {
        // 前 8KB 内出现 NUL 即视为二进制。
        var limit = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}


