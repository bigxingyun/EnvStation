using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Core.Actions;

namespace EnvStation.Core.Packages;

/// <summary>打包结果。</summary>
/// <param name="OutputPath">输出文件路径。</param>
/// <param name="SizeBytes">包体积。</param>
/// <param name="StampedActions">自动补全的 <c>pin.hash</c> 条数。</param>
/// <param name="FileCount">包内文件数。</param>
/// <param name="Signed">是否已签名。</param>
/// <param name="KeyFingerprint">签名密钥指纹（未签名时为空）。</param>
public sealed record PackResult(
    string OutputPath,
    long SizeBytes,
    int StampedActions,
    int FileCount,
    bool Signed,
    string KeyFingerprint);

/// <summary>
/// <c>.envstation</c> 打包器。
/// </summary>
/// <remarks>
/// <para>
/// <b>它替包作者做三件手工做不了或极易做错的事</b>（需求 M13-1 / M13-3）：
/// </para>
/// <list type="number">
///   <item><b>自动补全 <c>pin.hash</c></b>。需求 S3 要求每个动作引用都带契约哈希，
///         但让作者手工去查几十个 64 位哈希并不现实——上一轮写测试时我自己就体会到了这一点（TD-13）。
///         这里从<b>当前动作注册表</b>取值写入，因此包与它编写时的动作契约被绑定在一起。</item>
///   <item><b>生成 <c>manifest.sha256</c></b>：覆盖包内每个文件的内容哈希。
///         签名签的就是它，因此"改一个字节即验签失败"成立。</item>
///   <item><b>确定性输出</b>：固定时间戳 + 按路径排序 + 固定换行符。
///         同一个源目录打出来的包在任何机器上字节完全相同，签名因此可被独立复核。</item>
/// </list>
/// <para>
/// <b>打包时就要拒绝的东西</b>：如果工作流里引用了注册表里不存在的动作，打包应当失败而不是
/// 生成一个装不上的包。这与"宁可报错也不猜测"是同一条纪律。
/// </para>
/// </remarks>
public static class PackagePacker
{
    /// <summary>匹配工作流里的 <c>uses = "动作ID@版本"</c> 行。</summary>
    private static readonly Regex UsesLinePattern = new(
        @"^(?<indent>\s*)uses\s*=\s*""(?<id>[^""]+)""\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>匹配已有的 <c>pin = { hash = "..." }</c> 行。</summary>
    private static readonly Regex PinLinePattern = new(
        @"^\s*pin\s*=\s*\{[^}]*\}\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// 为工作流文本中的每个动作引用补全或校验 <c>pin.hash</c>。
    /// </summary>
    /// <param name="workflowText">工作流原文。</param>
    /// <param name="registry">动作注册表。</param>
    /// <param name="findings">发现收集器。</param>
    /// <returns>补齐后的文本，以及补全的条数。</returns>
    public static Result<(string Text, int Stamped)> StampPins(
        string workflowText,
        ActionRegistry registry,
        Abstractions.Diagnostics.FindingBag findings)
    {
        ArgumentNullException.ThrowIfNull(workflowText);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(findings);

        var lines = workflowText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(lines.Length + 16);
        var stamped = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = UsesLinePattern.Match(line);

            if (!match.Success)
            {
                output.Add(line);
                continue;
            }

            output.Add(line);

            var reference = match.Groups["id"].Value;
            var at = reference.LastIndexOf('@');
            if (at <= 0)
            {
                findings.Block(
                    "PACK-01", "动作引用缺少版本",
                    $"{reference} 没有指定版本。",
                    "格式必须是 <动作ID>@<语义化版本>。",
                    $"workflow.toml:{i + 1}", reference);
                continue;
            }

            var actionId = reference[..at];
            var version = reference[(at + 1)..];

            var hash = registry.GetContractHash(actionId, version);
            if (hash is null)
            {
                findings.Block(
                    "PACK-02", "引用了不存在的动作",
                    $"动作 {actionId}@{version} 不在当前动作库中。",
                    registry.TryGet(actionId, out var known)
                        ? $"该动作存在，提供的是版本 {known.Descriptor.Version}。把引用的版本改为 {known.Descriptor.Version}。"
                        : "检查动作 ID 是否拼写正确，用 envstation actions list 查看全部可用动作。",
                    $"workflow.toml:{i + 1}", actionId);
                continue;
            }

            // 若下一行已经是 pin，就地替换；否则插入一行。
            if (i + 1 < lines.Length && PinLinePattern.IsMatch(lines[i + 1]))
            {
                // 已存在 pin：只有当哈希不一致时才覆盖（并说明原因）。
                if (!lines[i + 1].Contains(hash, StringComparison.Ordinal))
                {
                    output.Add($"{match.Groups["indent"].Value}pin = {{ hash = \"{hash}\" }}");
                    stamped++;
                    i++; // 跳过原 pin 行
                }

                continue;
            }

            output.Add($"{match.Groups["indent"].Value}pin = {{ hash = \"{hash}\" }}");
            stamped++;
        }

        return Result<(string, int)>.Ok((string.Join('\n', output), stamped));
    }

    /// <summary>
    /// 从源目录打包。
    /// </summary>
    /// <param name="sourceDirectory">包含 <c>envstation.toml</c> 与 <c>workflow.toml</c> 的目录。</param>
    /// <param name="outputPath">输出的 <c>.envstation</c> 路径。</param>
    /// <param name="privateKeyPem">签名私钥；为 null 表示不签名（仅本地包）。</param>
    /// <param name="overwrite">是否覆盖已存在的输出文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<Result<PackResult>> PackAsync(
        string sourceDirectory,
        string outputPath,
        string? privateKeyPem = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDirectory);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!Directory.Exists(sourceDirectory))
        {
            return Result<PackResult>.Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"源目录不存在：{sourceDirectory}。");
        }

        var findings = new Abstractions.Diagnostics.FindingBag();

        var registryResult = ActionRegistry.CreateDefault(findings);
        if (registryResult.IsFailure)
        {
            return Result<PackResult>.Fail(
                EnvStationErrorCodes.ActionNotFound,
                "内置动作库加载失败，无法打包。");
        }

        var registry = registryResult.Value;

        // ① 扫描源目录
        var files = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(static f => !f.EndsWith(".envstation", StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();

        var manifestPath = Path.Combine(sourceDirectory, PackageLayout.ManifestEntry);
        var workflowPath = Path.Combine(sourceDirectory, PackageLayout.WorkflowEntry);

        if (!File.Exists(manifestPath))
        {
            return Result<PackResult>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                $"源目录缺少 {PackageLayout.ManifestEntry}。",
                $"在源目录放置 {PackageLayout.ManifestEntry} 后重新打包。");
        }

        if (!File.Exists(workflowPath))
        {
            return Result<PackResult>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                $"源目录缺少 {PackageLayout.WorkflowEntry}。");
        }

        // ② 补全 pin.hash
        var workflowText = await File.ReadAllTextAsync(workflowPath, cancellationToken).ConfigureAwait(false);
        var stampedResult = StampPins(workflowText, registry, findings);

        if (stampedResult.IsFailure || findings.HasBlockers)
        {
            return Result<PackResult>.Fail(
                EnvStationErrorCodes.PackageStaticCheckFailed,
                "打包前检查未通过：动作引用存在问题，详见发现列表。",
                findings.ToReport().ToText());
        }

        var (stampedText, stampedCount) = stampedResult.Value;

        // ③ 组装包内条目
        var text = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var binary = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');

            // 不把上次打包残留的清单与签名带进新包。
            if (relative is PackageLayout.HashManifestEntry or PackageLayout.SignatureEntry)
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);

            if (relative == PackageLayout.WorkflowEntry)
            {
                text[relative] = stampedText;
                continue;
            }

            if (LooksBinary(bytes))
            {
                binary[relative] = bytes;
            }
            else
            {
                // 统一换行符 + 剥掉 BOM：让同一个源在 Windows 与 Linux 上打出完全相同的包。
                // BOM 必须剥掉——否则同一个文件在两台机器上（一台的工具写了 BOM、一台没写）
                // 会算出不同的哈希，签名随之不同，"可复现"就不成立了。
                text[relative] = EnvStation.Core.Toml.TomlReader
                    .StripBom(new UTF8Encoding(false).GetString(bytes))
                    .Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        // ④ 生成哈希清单（对"最终会写进包里的内容"取哈希）
        var hashes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in text)
        {
            hashes[path] = PackageSigner.ComputeHash(content);
        }

        foreach (var (path, content) in binary)
        {
            hashes[path] = PackageSigner.ComputeHash(content);
        }

        var manifestText = PackageSigner.BuildManifest(hashes.ToImmutable());
        text[PackageLayout.HashManifestEntry] = manifestText;

        var keyFingerprint = string.Empty;
        var signed = false;

        // ⑤ 签名（签的是哈希清单本身）
        if (privateKeyPem is { Length: > 0 })
        {
            var signature = PackageSigner.Sign(manifestText, privateKeyPem);
            if (signature.IsFailure)
            {
                return Result<PackResult>.Fail(signature.Error);
            }

            text[PackageLayout.SignatureEntry] = signature.Value;
            signed = true;

            var publicKey = TryDerivePublicKey(privateKeyPem);
            if (publicKey is not null)
            {
                var fingerprint = PackageSigner.ComputeKeyFingerprint(publicKey);
                keyFingerprint = fingerprint.IsSuccess ? fingerprint.Value : string.Empty;
            }
        }

        // ⑥ 写盘
        var contents = new PackageContents
        {
            TextEntries = text.ToImmutable(),
            BinaryEntries = binary.ToImmutable(),
        };

        var write = await PackageArchive.WriteAsync(outputPath, contents, overwrite, cancellationToken)
            .ConfigureAwait(false);

        if (write.IsFailure)
        {
            return Result<PackResult>.Fail(write.Error);
        }

        return Result<PackResult>.Ok(new PackResult(
            outputPath,
            write.Value,
            stampedCount,
            contents.AllPaths.Length,
            signed,
            keyFingerprint));
    }

    /// <summary>从私钥推导公钥（用于展示指纹）。失败返回 null。</summary>
    private static string? TryDerivePublicKey(string privateKeyPem)
    {
        try
        {
            using var ecdsa = System.Security.Cryptography.ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyPem);
            return ecdsa.ExportSubjectPublicKeyInfoPem();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static bool LooksBinary(byte[] bytes)
    {
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
