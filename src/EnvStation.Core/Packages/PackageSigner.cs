using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EnvStation.Abstractions;

namespace EnvStation.Core.Packages;

/// <summary>
/// 包签名与验签（ECDSA P-256 + SHA-256）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是 P-256 而不是 Ed25519</b>：M0-P00-b 实测确认 <b>.NET 8 的 BCL 不提供 Ed25519</b>，
/// 而引入第三方密码学库会给"验证包来源"这条最关键的信任链增加一个不可审计的依赖。
/// ECDSA P-256 是 BCL 原生实现，且在 JIT 与 Native AOT 下均通过 keygen / sign / verify 全流程。
/// 需求 SIG-1 已同步修订。
/// </para>
/// <para>
/// <b>签什么</b>：签的是 <c>manifest.sha256</c>（包内每个文件的内容哈希清单）本身，
/// 而不是逐个文件签。这样只需验证一次签名，就能保证"清单里列出的每一个文件都没被改过"，
/// 同时也不会因为包里有几千个文件而变慢。
/// </para>
/// <para>
/// <b>签名覆盖清单的顺序敏感</b>：清单文本按路径排序后生成，因此同一个包在任何机器上
/// 都会得到完全相同的清单文本与相同的签名——这是"可复现"的前提，也是签名可被独立复核的前提。
/// </para>
/// </remarks>
public static class PackageSigner
{
    /// <summary>签名文件在包内的固定路径。</summary>
    public const string SignatureFileName = "signature.p256";

    /// <summary>哈希清单文件在包内的固定路径。</summary>
    public const string ManifestHashFileName = "manifest.sha256";

    /// <summary>公钥文件在包内的固定路径（可选；用于分发时携带公钥）。</summary>
    public const string PublicKeyFileName = "public.p256";

    /// <summary>签名算法标识，写入签名文件头。</summary>
    public const string Algorithm = "ecdsa-p256-sha256";

    /// <summary>生成一对新密钥。</summary>
    public static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>计算一段内容的 SHA-256（裸十六进制小写）。</summary>
    public static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>计算一段字节的 SHA-256（裸十六进制小写）。</summary>
    public static string ComputeHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>计算一个文件的 SHA-256（裸十六进制小写）。</summary>
    public static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>用私钥对内容签名，返回签名文件的完整文本。</summary>
    public static Result<string> Sign(string content, string privateKeyPem)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.VerifySignatureFailed,
                "私钥为空，无法签名。",
                "用 envstation pack --keygen 生成密钥对。");
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyPem);

            var contentBytes = Encoding.UTF8.GetBytes(content);
            var signature = ecdsa.SignData(contentBytes, HashAlgorithmName.SHA256);

            var sb = new StringBuilder();
            sb.Append("# EnvStation package signature\n");
            sb.Append("algorithm: ").Append(Algorithm).Append('\n');
            sb.Append("content-sha256: ").Append(ComputeHash(contentBytes)).Append('\n');
            sb.Append("signature-base64: ").Append(Convert.ToBase64String(signature)).Append('\n');
            return Result<string>.Ok(sb.ToString());
        }
        catch (CryptographicException ex)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.VerifySignatureFailed,
                $"签名失败：{ex.Message}",
                "确认私钥是合法的 PKCS#8 PEM，曲线为 P-256。");
        }
    }

    /// <summary>用公钥验证签名文件与内容是否匹配。</summary>
    public static Result<Unit> Verify(string content, string signatureFileText, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(signatureFileText))
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.VerifySignatureFailed,
                "包内没有签名文件。",
                "共享包必须携带签名。本地自有包用 envstation pack 签名后再分发。");
        }

        var header = ParseSignatureFile(signatureFileText);
        if (header.IsFailure)
        {
            return header.Propagate<Unit>();
        }

        var (algorithm, contentHash, signature) = header.Value;

        if (!string.Equals(algorithm, Algorithm, StringComparison.Ordinal))
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.VerifySignatureFailed,
                $"签名算法 {algorithm} 不受支持（当前只支持 {Algorithm}）。",
                "升级环境站客户端后重试。");
        }

        var actualHash = ComputeHash(Encoding.UTF8.GetBytes(content));
        if (!string.Equals(actualHash, contentHash, StringComparison.Ordinal))
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.VerifyHashMismatch,
                $"哈希清单的内容哈希不符：签名文件声明 {contentHash[..16]}…，实际 {actualHash[..16]}…。",
                "清单文件在签名之后被改动过，已拒绝导入。");
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);

            var ok = ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(content),
                signature,
                HashAlgorithmName.SHA256);

            return ok
                ? Results.Ok()
                : Result<Unit>.Fail(
                    EnvStationErrorCodes.VerifySignatureFailed,
                    "签名验证失败：内容与签名不匹配。",
                    "包内容被篡改，或签名来自另一对密钥。已拒绝导入，不降级放行。");
        }
        catch (CryptographicException ex)
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.VerifySignatureFailed,
                $"验签失败：{ex.Message}",
                "确认公钥是合法的 PEM，曲线为 P-256。");
        }
    }

    /// <summary>解析签名文件。</summary>
    public static Result<(string Algorithm, string ContentHash, byte[] Signature)> ParseSignatureFile(string text)
    {
        string? algorithm = null;
        string? contentHash = null;
        byte[]? signature = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "algorithm":
                    algorithm = value;
                    break;
                case "content-sha256":
                    contentHash = value;
                    break;
                case "signature-base64":
                    try
                    {
                        signature = Convert.FromBase64String(value);
                    }
                    catch (FormatException)
                    {
                        return Result<(string, string, byte[])>.Fail(
                            EnvStationErrorCodes.VerifySignatureFailed,
                            "签名文件中的 signature-base64 不是合法的 Base64。");
                    }

                    break;
                default:
                    break;
            }
        }

        if (algorithm is null || contentHash is null || signature is null)
        {
            return Result<(string, string, byte[])>.Fail(
                EnvStationErrorCodes.VerifySignatureFailed,
                "签名文件缺少必需字段（algorithm / content-sha256 / signature-base64）。");
        }

        return Result<(string, string, byte[])>.Ok((algorithm, contentHash, signature));
    }

    /// <summary>从公钥 PEM 计算指纹（用于让用户核对"这个包来自谁"）。</summary>
    /// <remarks>
    /// 指纹用 SHA-256 的前 16 字节按两位一组展示，形式与 SSH 指纹类似。
    /// 用户只需比对一次，就能确认后续所有包都来自同一个作者。
    /// </remarks>
    public static Result<string> ComputeKeyFingerprint(string publicKeyPem)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            var exported = ecdsa.ExportSubjectPublicKeyInfo();
            var hash = SHA256.HashData(exported);

            var hex = Convert.ToHexString(hash)[..32].ToLowerInvariant();
            var groups = Enumerable.Range(0, 8).Select(i => hex.Substring(i * 4, 4));
            return Result<string>.Ok(string.Join(':', groups));
        }
        catch (CryptographicException ex)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.VerifySignatureFailed,
                $"无法从公钥计算指纹：{ex.Message}");
        }
    }

    /// <summary>
    /// 生成 <c>manifest.sha256</c> 的内容（包内每个文件的哈希清单）。
    /// </summary>
    /// <param name="entries">相对路径 → 内容哈希（裸十六进制小写）。</param>
    /// <remarks>
    /// <b>顺序必须在生成时固定下来</b>：按路径序号排序，且用 <c>\n</c> 而非平台换行符。
    /// 否则同一个包在 Windows 与 Linux 上会生成不同的清单文本，签名随之不同——
    /// 那样"这个签名是不是我签的"就永远无法独立复核。
    /// </remarks>
    public static string BuildManifest(ImmutableDictionary<string, string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var sb = new StringBuilder();
        sb.Append("# EnvStation package manifest (sha256 of each file)\n");

        foreach (var (path, hash) in entries.OrderBy(static e => e.Key, StringComparer.Ordinal))
        {
            sb.Append(hash).Append("  ").Append(path).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>解析 <c>manifest.sha256</c>。</summary>
    public static Result<ImmutableDictionary<string, string>> ParseManifest(string text)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                return Result<ImmutableDictionary<string, string>>.Fail(
                    EnvStationErrorCodes.PackageParseFailed,
                    $"哈希清单格式非法：{line}。",
                    "正确格式为每行「<64 位十六进制>  <相对路径>」（两个空格分隔）。");
            }

            var hash = line[..separator].Trim();
            var path = line[(separator + 2)..].Trim();

            if (hash.Length != 64 || path.Length == 0)
            {
                return Result<ImmutableDictionary<string, string>>.Fail(
                    EnvStationErrorCodes.PackageParseFailed,
                    $"哈希清单条目非法：{line}。");
            }

            if (!builder.TryAdd(path, hash))
            {
                return Result<ImmutableDictionary<string, string>>.Fail(
                    EnvStationErrorCodes.PackageParseFailed,
                    $"哈希清单中 {path} 出现了多次。",
                    "重复条目说明包被手工改过，已拒绝。");
            }
        }

        return Result<ImmutableDictionary<string, string>>.Ok(builder.ToImmutable());
    }

    /// <summary>把密钥指纹渲染为用户可读的形式。</summary>
    public static string DescribeFingerprint(string fingerprint) =>
        string.Create(CultureInfo.InvariantCulture, $"ECDSA P-256 指纹 {fingerprint}");
}
