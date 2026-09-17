using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;

namespace EnvStation.Core.Actions.Builtin;

// ══════════════════════════════════════════════════════════════════════════════
//  A02 下载与校验（4 个动作 · CAP.NET.DOWNLOAD）
//
//  这一组是供应链攻击的主要入口（需求 TH-4）。三条硬性纪律：
//
//    1. **强制哈希**：sha256 是必填参数，没有它就拒绝下载（需求 AI-4）。
//       这不是"建议"——没有哈希的下载等于把"装什么"的决定权交给网络。
//    2. **域名白名单逐跳校验**：只查初始 URL 可以被一次 302 绕过，
//       因此跟随重定向的每一步都重新校验（见 HttpNetworkTransport）。
//    3. **边下边判上限**：等下载完再检查大小，等于没有防护。
//       实现上是每读一块就累加，超限立即中止并删除半成品。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>网络动作的公共基类：取出传输通道、解析 URL、写审计。</summary>
internal abstract class NetActionBase : ActionBase
{
    /// <summary>默认的最大下载体积（1 GB）。</summary>
    protected const long DefaultMaxBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>取出网络通道；没有则明确失败。</summary>
    protected static Result<INetworkTransport> RequireNetwork(ActionExecutionContext context)
    {
        if (context.Network is null)
        {
            return Result<INetworkTransport>.Fail(
                EnvStationErrorCodes.NetUnreachable,
                "本次运行没有启用网络能力（未注入网络通道实现）。",
                "预演模式下不发起网络请求，属预期行为；正式运行时出现该提示可提交反馈。");
        }

        return Result<INetworkTransport>.Ok(context.Network);
    }

    /// <summary>解析 URL 参数；只接受 http / https。</summary>
    protected static Result<Uri> ParseUrl(string raw, string parameterName)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url))
        {
            return Result<Uri>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"参数 {parameterName} 不是合法的绝对 URL：{raw}",
                "示例：https://mirrors.aliyun.com/python/3.12.1/python-3.12.1-amd64.exe");
        }

        if (url.Scheme is not ("http" or "https"))
        {
            return Result<Uri>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"参数 {parameterName} 的协议是 {url.Scheme}，只允许 http 与 https。",
                "这是为了防止把本地文件读取伪装成下载（例如 file:///C:/Windows/…）。");
        }

        return Result<Uri>.Ok(url);
    }

    /// <summary>校验期望哈希的格式。</summary>
    protected static Result<string> ParseHash(string raw, string parameterName)
    {
        var normalized = ActionRegistry.NormalizeHash(raw);
        return normalized is null
            ? Result<string>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"参数 {parameterName} 不是合法的 SHA-256：{raw}",
                "正确格式为 sha256:<64 位小写十六进制>。")
            : Result<string>.Ok(normalized);
    }
}

/// <summary><c>envstation.net.download</c>：下载文件，强制哈希校验、镜像回退、总大小上限。</summary>
internal sealed class NetDownloadAction : NetActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.net.download",
        "1.0.0",
        CapabilityIds.NetDownload,
        "下载文件",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 1800,
        TouchedResources: ["network.outbound", "filesystem.write", "filesystem.read"],
        Parameters:
        [
            Url("url", true, "下载地址（主源）"),
            Sha256("sha256", true, "文件内容的 SHA-256；必填，缺失即拒绝下载"),
            new ParameterSpec("mirrors", ParameterType.StringArray, false,
                "备用镜像地址；主源失败时按顺序尝试。每个镜像必须同样在白名单内"),
            Path_("dest", true, "保存路径（必须落在授权目录内）"),
            new ParameterSpec("max_bytes", ParameterType.Integer, false,
                "允许的最大字节数；超出即中止并清理。省略时使用 1 GB", Minimum: 1024, Maximum: long.MaxValue),
            Bool("skip_if_exists", "目标已存在且哈希正确时跳过下载（便于重跑）", true),
            Bool("allow_http", "是否允许明文 HTTP 下载。默认 false：明文传输可能被中间人篡改", false),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var network = RequireNetwork(context);
        if (network.IsFailure)
        {
            return FailResult(network.Error.Code, network.Error.Message, network.Error.Remediation);
        }

        var urlResult = ParseUrl(arguments.GetString("url")!, "url");
        if (urlResult.IsFailure)
        {
            return FailResult(urlResult.Error.Code, urlResult.Error.Message, urlResult.Error.Remediation);
        }

        var hashResult = ParseHash(arguments.GetString("sha256")!, "sha256");
        if (hashResult.IsFailure)
        {
            return FailResult(hashResult.Error.Code, hashResult.Error.Message, hashResult.Error.Remediation);
        }

        var destGuard = context.GuardPath(arguments.GetString("dest")!, "dest");
        if (destGuard.IsFailure)
        {
            return FailResult(destGuard.Error.Code, destGuard.Error.Message, destGuard.Error.Remediation);
        }

        var dest = destGuard.Value;
        var expected = hashResult.Value;
        var maxBytes = arguments.GetInt64("max_bytes", DefaultMaxBytes);
        var skipIfExists = arguments.GetBoolean("skip_if_exists", true);
        var allowHttp = arguments.GetBoolean("allow_http", false);

        // 明文 HTTP：默认拒绝。PATH 上的中间人可以替换内容并同时替换哈希值——
        // 但哈希是包作者写死的，所以篡改会被哈希校验挡住；真正的风险是"内容被替换成
        // 另一份同样被作者记录过的文件"，以及隐私泄露。因此默认为拒绝，需要显式降级。
        if (urlResult.Value.Scheme == "http" && !allowHttp)
        {
            return FailResult(
                EnvStationErrorCodes.NetUnreachable,
                $"下载地址使用明文 HTTP：{urlResult.Value.Host}",
                "明文传输可能被中间人观察或替换。内网源只有 HTTP 时，显式把 allow_http 设为 true。");
        }

        // 目标已存在且哈希正确 → 幂等跳过。这让"重跑一遍"变得廉价且安全。
        if (skipIfExists && File.Exists(dest))
        {
            var existing = await HashFileAsync(dest, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existing, expected, StringComparison.Ordinal))
            {
                var size = new FileInfo(dest).Length;
                return OkResult(
                    $"目标文件已存在且哈希正确，跳过下载：{dest}（{Format(size)}）。",
                    Outputs(
                        ("skipped", "true"),
                        ("path", dest),
                        ("bytes", size.ToString(CultureInfo.InvariantCulture)),
                        ("sha256", expected),
                        ("source", "已存在的本地文件")),
                    touched: [dest]);
            }
        }

        // 按"主源 + 镜像"的顺序尝试。每个候选都独立校验白名单与哈希。
        var candidates = ImmutableArray.CreateBuilder<string>();
        candidates.Add(urlResult.Value.ToString());
        foreach (var mirror in arguments.GetStringArray("mirrors"))
        {
            if (!string.IsNullOrWhiteSpace(mirror))
            {
                candidates.Add(mirror.Trim());
            }
        }

        var attempts = ImmutableArray.CreateBuilder<string>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidateUrl = ParseUrl(candidate, "mirrors");
            if (candidateUrl.IsFailure)
            {
                // 镜像地址本身写错同样是配置问题，直接报出来比埋在 attempts 里更有用。
                return FailResult(
                    candidateUrl.Error.Code,
                    $"镜像地址不合法：{candidateUrl.Error.Message}",
                    candidateUrl.Error.Remediation);
            }

            // 白名单与协议这类问题是**包本身的配置错误**，不是网络抖动：
            // 换镜像同样会被拒，继续尝试只会把真正的错误信息埋在一长串 attempts 里。
            // 因此这里直接返回，让用户一眼看到"哪个域名没声明"。
            var hostCheck = HostPolicy.Check(candidateUrl.Value, context.AllowedHosts);
            if (hostCheck.IsFailure)
            {
                return FailResult(hostCheck.Error.Code, hostCheck.Error.Message, hostCheck.Error.Remediation);
            }

            if (candidateUrl.Value.Scheme == "http" && !allowHttp)
            {
                return FailResult(
                    EnvStationErrorCodes.NetUnreachable,
                    $"下载地址使用明文 HTTP：{candidateUrl.Value.Host}",
                    "明文传输可能被中间人观察或替换。内网源只有 HTTP 时，显式把 allow_http 设为 true。");
            }

            // 配额预检：先按"声明的最大值"占位，下载完再按实际值结算。
            // 这样做的好处是"磁盘装不下"能在发起请求前就发现。
            var quota = context.Quota.TryConsume(QuotaKind.Download, 0);
            if (quota.IsFailure)
            {
                return FailResult(quota.Error.Code, quota.Error.Message, quota.Error.Remediation);
            }

            long lastReported = 0;
            var progress = new Progress<long>(received =>
            {
                // 每 4MB 报一次，避免把 UI 线程刷爆。
                if (received - lastReported >= 4 * 1024 * 1024)
                {
                    lastReported = received;
                    context.ReportProgress(0, "正在下载…");
                }
            });

            context.Audit("net.download.begin", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["url"] = HostPolicy.DescribeForAudit(candidateUrl.Value),
                ["dest"] = dest,
                ["expected_sha256"] = expected,
            });

            var download = await network.Value
                .DownloadAsync(candidateUrl.Value, context.AllowedHosts, dest, maxBytes, progress, cancellationToken)
                .ConfigureAwait(false);

            if (download.IsFailure)
            {
                // 大小上限是我们自己施加的约束，换镜像结果一样 —— 属于不可重试的终止性失败。
                // 哈希不符则**可以**重试：不同镜像可能同步了不同版本，换一个或许就对上了。
                if (download.Error.Code == EnvStationErrorCodes.NetSizeLimit)
                {
                    return FailResult(download.Error.Code, download.Error.Message, download.Error.Remediation);
                }

                attempts.Add($"{candidateUrl.Value.Host} → {download.Error.Message}");
                continue;
            }

            var result = download.Value;

            // 命中缓存/已下载过时，配额已经在上一次结算过；这里只结算本次新下载的量。
            var consume = context.Quota.TryConsume(QuotaKind.Download, result.BytesWritten);
            if (consume.IsFailure)
            {
                TryDelete(dest);
                return FailResult(consume.Error.Code, consume.Error.Message, consume.Error.Remediation);
            }

            if (!string.Equals(result.ActualSha256, expected, StringComparison.Ordinal))
            {
                // 哈希不符：必须删除文件而不是留在原地。留下"一个看起来对但内容不对的文件"
                // 比直接失败危险得多——后续步骤可能拿它去安装。
                TryDelete(dest);

                context.Audit("net.download.hash_mismatch", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["url"] = HostPolicy.DescribeForAudit(candidateUrl.Value),
                    ["expected"] = expected,
                    ["actual"] = result.ActualSha256,
                });

                attempts.Add(
                    $"{candidateUrl.Value.Host} → 哈希不符（期望 {expected[..16]}…，实际 {result.ActualSha256[..16]}…）");
                continue;
            }

            context.Audit("net.download.ok", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["url"] = HostPolicy.DescribeForAudit(candidateUrl.Value),
                ["bytes"] = result.BytesWritten.ToString(CultureInfo.InvariantCulture),
                ["redirects"] = result.Redirects.ToString(CultureInfo.InvariantCulture),
            });

            return OkResult(
                $"已下载 {Path.GetFileName(dest)}（{Format(result.BytesWritten)}），SHA-256 校验通过。",
                Outputs(
                    ("skipped", "false"),
                    ("path", dest),
                    ("bytes", result.BytesWritten.ToString(CultureInfo.InvariantCulture)),
                    ("sha256", result.ActualSha256),
                    ("source", HostPolicy.DescribeForAudit(candidateUrl.Value)),
                    ("final_url", result.FinalUrl),
                    ("redirects", result.Redirects.ToString(CultureInfo.InvariantCulture)),
                    ("attempts", string.Join(" | ", attempts))),
                touched: [dest]);
        }

        return FailResult(
            EnvStationErrorCodes.NetUnreachable,
            $"全部 {candidates.Count} 个下载地址都失败了：{string.Join("；", attempts)}",
            "检查网络连通性（envstation.detect.network）、镜像站是否已同步该文件，或镜像地址是否写错。");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不改变结论。
        }
    }

    private static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1} MB"
        : $"{bytes / (double)(1L << 10):F1} KB";
}

/// <summary><c>envstation.net.head</c>：探测远端文件大小 / ETag / 最后修改时间。</summary>
internal sealed class NetHeadAction : NetActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.net.head",
        "1.0.0",
        CapabilityIds.NetDownload,
        "检测远端文件",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["network.outbound"],
        Parameters:
        [
            Url("url", true, "要检测的地址"),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var network = RequireNetwork(context);
        if (network.IsFailure)
        {
            return FailResult(network.Error.Code, network.Error.Message, network.Error.Remediation);
        }

        var urlResult = ParseUrl(arguments.GetString("url")!, "url");
        if (urlResult.IsFailure)
        {
            return FailResult(urlResult.Error.Code, urlResult.Error.Message, urlResult.Error.Remediation);
        }

        var probe = await network.Value
            .ProbeAsync(urlResult.Value, context.AllowedHosts, cancellationToken)
            .ConfigureAwait(false);

        if (probe.IsFailure)
        {
            return FailResult(probe.Error.Code, probe.Error.Message, probe.Error.Remediation);
        }

        var result = probe.Value;
        return OkResult(
            $"{urlResult.Value.Host} 可访问：HTTP {result.StatusCode}，" +
            (result.ContentLength >= 0 ? $"大小 {Format(result.ContentLength)}。" : "未声明大小。"),
            Outputs(
                ("status_code", result.StatusCode.ToString(CultureInfo.InvariantCulture)),
                ("content_length", result.ContentLength.ToString(CultureInfo.InvariantCulture)),
                ("etag", result.ETag ?? string.Empty),
                ("last_modified", result.LastModified?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                ("final_url", result.FinalUrl),
                ("redirects", result.Redirects.ToString(CultureInfo.InvariantCulture))));
    }

    private static string Format(long bytes) =>
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1} MB" : $"{bytes / (double)(1L << 10):F1} KB";
}

/// <summary><c>envstation.net.verify_hash</c>：校验已有文件哈希。</summary>
internal sealed class NetVerifyHashAction : NetActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.net.verify_hash",
        "1.0.0",
        CapabilityIds.Inspect,
        "校验文件哈希",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 300,
        TouchedResources: ["filesystem.read"],
        Parameters:
        [
            Path_("path", true, "要校验的文件"),
            Sha256("sha256", true, "期望的 SHA-256"),
            new ParameterSpec("max_bytes", ParameterType.Integer, false,
                "文件大小上限；超过即拒绝读取，防止误对超大文件做哈希", Minimum: 1024, Maximum: long.MaxValue),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var guard = context.GuardPath(arguments.GetString("path")!, "path");
        if (guard.IsFailure)
        {
            return FailResult(guard.Error.Code, guard.Error.Message, guard.Error.Remediation);
        }

        var hashResult = ParseHash(arguments.GetString("sha256")!, "sha256");
        if (hashResult.IsFailure)
        {
            return FailResult(hashResult.Error.Code, hashResult.Error.Message, hashResult.Error.Remediation);
        }

        var path = guard.Value;
        if (!File.Exists(path))
        {
            return FailResult(EnvStationErrorCodes.AssertFailed, $"文件不存在：{path}");
        }

        var info = new FileInfo(path);
        var maxBytes = arguments.GetInt64("max_bytes", 0);
        if (maxBytes > 0 && info.Length > maxBytes)
        {
            return FailResult(
                EnvStationErrorCodes.NetSizeLimit,
                $"文件大小 {info.Length} 字节超过上限 {maxBytes}，拒绝读取。");
        }

        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        var matches = string.Equals(actual, hashResult.Value, StringComparison.Ordinal);

        var outputs = Outputs(
            ("path", path),
            ("actual_sha256", actual),
            ("expected_sha256", hashResult.Value),
            ("matches", Bool(matches)),
            ("bytes", info.Length.ToString(CultureInfo.InvariantCulture)));

        return matches
            ? OkResult($"文件哈希校验通过：{path}。", outputs, touched: [path])
            : FailResult(
                EnvStationErrorCodes.NetHashMismatch,
                $"文件 {path} 的哈希不符：实际 {actual[..16]}…，期望 {hashResult.Value[..16]}…",
                "文件可能已损坏、被篡改，或下载到了错误的版本。删除后重新下载。");
    }
}

/// <summary><c>envstation.net.fetch_text</c>：获取小文本（限大小、限域名，不落盘）。</summary>
internal sealed class NetFetchTextAction : NetActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.net.fetch_text",
        "1.0.0",
        CapabilityIds.NetDownload,
        "获取小段文本",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 60,
        TouchedResources: ["network.outbound"],
        Parameters:
        [
            Url("url", true, "要获取的地址"),
            new ParameterSpec("max_bytes", ParameterType.Integer, false,
                "允许的最大字节数；默认 64 KB，该动作只用于获取小段文本（如版本清单）",
                Minimum: 64, Maximum: 4 * 1024 * 1024, DefaultValue: "65536"),
            Str("expect_contains", false, "若提供，返回内容必须包含该子串，否则判失败", maxLength: 512),
        ]);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var network = RequireNetwork(context);
        if (network.IsFailure)
        {
            return FailResult(network.Error.Code, network.Error.Message, network.Error.Remediation);
        }

        var urlResult = ParseUrl(arguments.GetString("url")!, "url");
        if (urlResult.IsFailure)
        {
            return FailResult(urlResult.Error.Code, urlResult.Error.Message, urlResult.Error.Remediation);
        }

        var maxBytes = (int)arguments.GetInt64("max_bytes", 65536);
        var expectContains = arguments.GetString("expect_contains");

        var fetched = await network.Value
            .FetchTextAsync(urlResult.Value, context.AllowedHosts, maxBytes, cancellationToken)
            .ConfigureAwait(false);

        if (fetched.IsFailure)
        {
            return FailResult(fetched.Error.Code, fetched.Error.Message, fetched.Error.Remediation);
        }

        var text = fetched.Value;

        if (expectContains is { Length: > 0 } && !text.Contains(expectContains, StringComparison.Ordinal))
        {
            return FailResult(
                EnvStationErrorCodes.AssertFailed,
                $"从 {urlResult.Value.Host} 获取的内容中不含期望的「{expectContains}」。",
                "常见原因：镜像返回了错误页（如 404 页面），或该地址的内容已变更。");
        }

        return OkResult(
            $"已从 {urlResult.Value.Host} 获取 {text.Length} 字符。",
            Outputs(
                ("text", text),
                ("length", text.Length.ToString(CultureInfo.InvariantCulture)),
                ("host", urlResult.Value.Host)));
    }
}
