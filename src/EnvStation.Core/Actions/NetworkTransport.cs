using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using EnvStation.Abstractions;

namespace EnvStation.Core.Actions;

/// <summary>一次 HTTP 探测的结果。</summary>
/// <param name="StatusCode">HTTP 状态码。</param>
/// <param name="FinalUrl">跟随重定向后的最终 URL。</param>
/// <param name="ContentLength">内容长度；未知时为 -1。</param>
/// <param name="ETag">ETag（可能为空）。</param>
/// <param name="LastModified">最后修改时间（可能为空）。</param>
/// <param name="Redirects">经过的重定向跳数。</param>
public sealed record HttpProbeResult(
    int StatusCode,
    string FinalUrl,
    long ContentLength,
    string? ETag,
    DateTimeOffset? LastModified,
    int Redirects);

/// <summary>一次下载的结果。</summary>
/// <param name="BytesWritten">实际写入的字节数。</param>
/// <param name="ActualSha256">落盘文件的 SHA-256（裸十六进制小写）。</param>
/// <param name="FinalUrl">跟随重定向后的最终 URL。</param>
/// <param name="Redirects">经过的重定向跳数。</param>
public sealed record DownloadResult(
    long BytesWritten,
    string ActualSha256,
    string FinalUrl,
    int Redirects);

/// <summary>
/// 网络传输通道。
/// </summary>
/// <remarks>
/// <para>
/// <b>它是"能不能出网"的唯一开关。</b>与 <c>IEnvironmentOperations</c> 同样的设计思路：
/// 预演、静态检查与单元测试不注入实现，因此不会产生任何真实网络请求。
/// </para>
/// <para>
/// <b>域名白名单在执行器内部逐跳校验。</b>这不是"多做一层保险"，而是必须的：
/// 只校验初始 URL 的白名单可以被一次 302 跳转绕过——攻击者用一个白名单内的域名
/// 重定向到任意主机即可。因此重定向必须由我们自己跟随，并在<b>每一跳</b>上重新校验。
/// </para>
/// </remarks>
public interface INetworkTransport
{
    /// <summary>探测远端资源（不落盘）。</summary>
    ValueTask<Result<HttpProbeResult>> ProbeAsync(
        Uri url,
        ImmutableArray<string> allowedHosts,
        CancellationToken cancellationToken);

    /// <summary>获取小段文本（不落盘，长度受限）。</summary>
    ValueTask<Result<string>> FetchTextAsync(
        Uri url,
        ImmutableArray<string> allowedHosts,
        int maxBytes,
        CancellationToken cancellationToken);

    /// <summary>
    /// 下载到指定路径。调用方负责提供<b>已通过路径守卫</b>的目标路径。
    /// </summary>
    /// <param name="url">初始 URL。</param>
    /// <param name="allowedHosts">域名白名单（逐跳校验）。</param>
    /// <param name="destinationPath">目标文件路径。</param>
    /// <param name="maxBytes">允许的最大字节数；超出即中止并删除半成品。</param>
    /// <param name="progress">进度回调（已接收字节数）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<Result<DownloadResult>> DownloadAsync(
        Uri url,
        ImmutableArray<string> allowedHosts,
        string destinationPath,
        long maxBytes,
        IProgress<long>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// 基于 <see cref="HttpClient"/> 的网络传输实现。
/// </summary>
/// <remarks>
/// <para><b>硬性约束</b>：</para>
/// <list type="bullet">
///   <item>逐跳校验域名白名单（见 <see cref="INetworkTransport"/> 的说明）；</item>
///   <item>重定向跳数上限 5，避免重定向环；</item>
///   <item>下载边写边累加，<b>一旦超过上限立即中止并删除半成品</b>——
///         不能等下载完再判断，否则"用超大文件撑爆磁盘"依然成立；</item>
///   <item>只接受 http / https，其它协议（file、ftp、UNC）一律拒绝：
///         <c>file://</c> 会让"下载"变成"读本地任意文件"。</item>
/// </list>
/// <para><b>为什么不用 <c>HttpClient</c> 的自动重定向</b>：它的 <c>AllowAutoRedirect</c>
/// 会在内部跟随跳转，我们拿不到中间每一跳的 URL，也就无法逐跳校验白名单。</para>
/// </remarks>
public sealed class HttpNetworkTransport : INetworkTransport, IDisposable
{
    /// <summary>最大重定向跳数。</summary>
    public const int MaxRedirects = 5;

    /// <summary>单次请求的整体超时。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    /// <summary>用默认配置构造（禁自动重定向、禁 Cookie、固定 UA）。</summary>
    public HttpNetworkTransport()
    {
        var handler = new HttpClientHandler
        {
            // 关键：自己跟随重定向，才能逐跳校验白名单。
            AllowAutoRedirect = false,
            // 不用 Cookie 容器：包与包之间不应共享任何会话状态。
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        };

        _client = new HttpClient(handler) { Timeout = RequestTimeout };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("EnvStation/0.1 (+https://example.invalid/envstation)");
        _ownsClient = true;
    }

    /// <summary>用外部提供的 <see cref="HttpClient"/> 构造（测试可注入假实现）。</summary>
    public HttpNetworkTransport(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownsClient = false;
    }

    /// <inheritdoc />
    public async ValueTask<Result<HttpProbeResult>> ProbeAsync(
        Uri url,
        ImmutableArray<string> allowedHosts,
        CancellationToken cancellationToken)
    {
        var hop = await FollowAsync(url, allowedHosts, HttpMethod.Head, cancellationToken).ConfigureAwait(false);
        if (hop.IsFailure)
        {
            return hop.Propagate<HttpProbeResult>();
        }

        using var response = hop.Value.Response;
        var contentLength = response.Content.Headers.ContentLength ?? -1;
        var etag = response.Headers.ETag?.Tag;
        var lastModified = response.Content.Headers.LastModified;

        return Result<HttpProbeResult>.Ok(new HttpProbeResult(
            (int)response.StatusCode,
            hop.Value.FinalUrl.ToString(),
            contentLength,
            etag,
            lastModified,
            hop.Value.Redirects));
    }

    /// <inheritdoc />
    public async ValueTask<Result<string>> FetchTextAsync(
        Uri url,
        ImmutableArray<string> allowedHosts,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var hop = await FollowAsync(url, allowedHosts, HttpMethod.Get, cancellationToken).ConfigureAwait(false);
        if (hop.IsFailure)
        {
            return hop.Propagate<string>();
        }

        using var response = hop.Value.Response;

        var declared = response.Content.Headers.ContentLength;
        if (declared is > 0 && declared > maxBytes)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.NetSizeLimit,
                $"远端文件声明大小 {declared} 字节，超过允许的 {maxBytes} 字节。",
                "该动作只用于获取小段文本；下载大文件改用 envstation.net.download。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var buffer = new char[maxBytes];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, maxBytes), cancellationToken).ConfigureAwait(false);

        // 再读一个字符：如果还能读到，说明实际内容比 maxBytes 长。
        var extra = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (extra > 0)
        {
            return Result<string>.Fail(
                EnvStationErrorCodes.NetSizeLimit,
                $"远端内容超过允许的 {maxBytes} 字节。",
                "该动作只用于获取小段文本。");
        }

        return Result<string>.Ok(new string(buffer, 0, read));
    }

    /// <inheritdoc />
    public async ValueTask<Result<DownloadResult>> DownloadAsync(
        Uri url,
        ImmutableArray<string> allowedHosts,
        string destinationPath,
        long maxBytes,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        var hop = await FollowAsync(url, allowedHosts, HttpMethod.Get, cancellationToken).ConfigureAwait(false);
        if (hop.IsFailure)
        {
            return hop.Propagate<DownloadResult>();
        }

        using var response = hop.Value.Response;

        var declared = response.Content.Headers.ContentLength;
        if (declared is > 0 && declared > maxBytes)
        {
            return Result<DownloadResult>.Fail(
                EnvStationErrorCodes.NetSizeLimit,
                $"远端文件声明大小 {Format(declared.Value)}，超过允许的 {Format(maxBytes)}。");
        }

        // 先写临时文件，成功后再改名。
        // 这样"下载到一半失败"不会留下一个看似完整、实则残缺的文件被后续步骤当成成品使用。
        var tempPath = destinationPath + ".partial";

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            long total = 0;
            string hash;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;

                    // 边下边判：不能等下载完再检查上限。
                    if (total > maxBytes)
                    {
                        await destination.DisposeAsync().ConfigureAwait(false);
                        TryDelete(tempPath);
                        return Result<DownloadResult>.Fail(
                            EnvStationErrorCodes.NetSizeLimit,
                            $"下载已超过允许的 {Format(maxBytes)}（已接收 {Format(total)}），已中止并清理临时文件。");
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(total);
                }

                sha.TransformFinalBlock([], 0, 0);
                hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }

            if (declared is > 0 && total != declared)
            {
                TryDelete(tempPath);
                return Result<DownloadResult>.Fail(
                    EnvStationErrorCodes.NetIncomplete,
                    $"下载不完整：声明 {declared} 字节，实际收到 {total} 字节。",
                    "这通常是网络中断导致的。已清理临时文件，可重新下载。");
            }

            File.Move(tempPath, destinationPath, overwrite: true);

            return Result<DownloadResult>.Ok(new DownloadResult(
                total,
                hash,
                hop.Value.FinalUrl.ToString(),
                hop.Value.Redirects));
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            return Result<DownloadResult>.Fail(
                ex is UnauthorizedAccessException ? EnvStationErrorCodes.PathNotWritable : EnvStationErrorCodes.NetUnreachable,
                $"下载失败：{ex.Message}");
        }
    }

    /// <summary>跟随重定向，并在每一跳校验域名白名单。</summary>
    private async ValueTask<Result<(HttpResponseMessage Response, Uri FinalUrl, int Redirects)>> FollowAsync(
        Uri url,
        ImmutableArray<string> allowedHosts,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        var current = url;
        var redirects = 0;

        while (true)
        {
            var check = HostPolicy.Check(current, allowedHosts);
            if (check.IsFailure)
            {
                return check.Propagate<(HttpResponseMessage, Uri, int)>();
            }

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, current);
                response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result<(HttpResponseMessage, Uri, int)>.Fail(
                    EnvStationErrorCodes.NetUnreachable,
                    $"请求 {current.Host} 超时（{RequestTimeout.TotalMinutes:F0} 分钟）。");
            }
            catch (HttpRequestException ex)
            {
                return Result<(HttpResponseMessage, Uri, int)>.Fail(
                    EnvStationErrorCodes.NetUnreachable,
                    $"无法连接 {current.Host}：{ex.Message}",
                    "检查网络与代理设置，或用 envstation.detect.network 确认该域名是否可达。");
            }

            var status = (int)response.StatusCode;

            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();

                if (++redirects > MaxRedirects)
                {
                    return Result<(HttpResponseMessage, Uri, int)>.Fail(
                        EnvStationErrorCodes.NetUnreachable,
                        $"重定向次数超过上限 {MaxRedirects}，已中止。",
                        "可能是镜像站配置错误导致的重定向环。");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return Result<(HttpResponseMessage, Uri, int)>.Fail(
                    EnvStationErrorCodes.NetUnreachable,
                    $"服务器返回 {status} {response.ReasonPhrase}（{current}）。",
                    status == 404
                        ? "文件不存在——常见原因是版本号写错，或该镜像尚未同步此文件。"
                        : "稍后重试，或改用其他镜像。");
            }

            return Result<(HttpResponseMessage, Uri, int)>.Ok((response, current, redirects));
        }
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
            // 清理失败不影响主流程结论文案。
        }
    }

    private static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1} MB"
        : $"{bytes / (double)(1L << 10):F1} KB";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}

/// <summary>
/// 域名白名单策略。
/// </summary>
/// <remarks>
/// <para>
/// 匹配规则刻意<b>不支持通配符</b>，且要求<b>完全相等</b>（忽略大小写）：
/// </para>
/// <list type="bullet">
///   <item><c>*.example.com</c> 这类通配会让白名单形同虚设（清单校验阶段就已拒绝通配符）；</item>
///   <item>后缀匹配 <c>endsWith(".example.com")</c> 会被 <c>evil-example.com</c> 绕过——
///         这是白名单实现里最经典的一个漏洞。</item>
/// </list>
/// <para>因此这里只做精确匹配。子域名必须逐个显式列出，这是刻意选择的"多写一行、少一个洞"。</para>
/// </remarks>
public static class HostPolicy
{
    /// <summary>判断 URL 的主机是否在白名单内。</summary>
    public static Result<Unit> Check(Uri url, ImmutableArray<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Scheme is not ("http" or "https"))
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.NetUnreachable,
                $"不允许的协议 {url.Scheme}：只支持 http 与 https。",
                "file:// 之类的协议会让下载变成读取本地任意文件，因此被明确禁止。");
        }

        if (allowedHosts.OrEmpty().Length == 0)
        {
            return Result<Unit>.Fail(
                EnvStationErrorCodes.NetUnreachable,
                "该包没有声明任何允许访问的域名，因此拒绝一切网络请求。",
                "在包清单的 [network].allow 中显式列出需要的域名。");
        }

        var host = url.Host.ToLowerInvariant();
        foreach (var allowed in allowedHosts)
        {
            if (string.Equals(host, allowed, StringComparison.Ordinal))
            {
                return Results.Ok();
            }
        }

        return Result<Unit>.Fail(
            EnvStationErrorCodes.NetUnreachable,
            $"域名 {host} 不在该包声明的白名单内。",
            $"该包声明的域名：{string.Join("、", allowedHosts)}。" +
            " 这是镜像站重定向到的新域名时，需要包作者把它加入白名单。");
    }

    /// <summary>把 URL 渲染为可入审计日志的文本（去掉查询串，可能含凭据）。</summary>
    public static string DescribeForAudit(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return $"{url.Scheme}://{url.Host}{url.AbsolutePath}";
    }

    /// <summary>把字节数渲染为人类可读文本。</summary>
    public static string FormatBytes(long bytes) =>
        bytes.ToString(CultureInfo.InvariantCulture) + " B";
}
