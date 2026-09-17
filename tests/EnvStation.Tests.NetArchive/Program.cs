using System.Collections.Immutable;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using EnvStation.TestKit;

namespace EnvStation.Tests.NetArchive;

/// <summary>
/// A02 下载与校验 + A03 解压与封装 的动作测试。
///
/// <para><b>本套用例的两个重点</b>：</para>
/// <list type="number">
///   <item><b>下载</b>：域名白名单（含逐跳）、强制哈希、边下边判上限、明文 HTTP 默认拒绝。
///         全部通过假传输通道验证，<b>不发出任何真实网络请求</b>。</item>
///   <item><b>解压</b>：路径穿越的四种形态、符号链接、压缩炸弹。
///         这些构造全部在测试里就地生成，因此用例在任何机器上结论一致。</item>
/// </list>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("下载与解压动作测试（假网络通道，不发出真实请求）");
        Console.WriteLine();

        var h = new TestHarness("下载与解压动作");

        DownloadCases(h);
        HashAndProbeCases(h);
        ExtractionSafetyCases(h);
        ExtractionFunctionCases(h);

        var code = h.Summarize();

        var jsonPath = TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return code;
    }

    // ══════════════════════════ 测试替身 ══════════════════════════

    /// <summary>假网络通道：按 URL 返回脚本化的内容，并像真实实现一样做白名单校验。</summary>
    private sealed class FakeNetworkTransport : CoreActions.INetworkTransport
    {
        private readonly Dictionary<string, byte[]> _content = new(StringComparer.OrdinalIgnoreCase);

        internal List<string> RequestedUrls { get; } = [];

        internal List<string> RejectedHosts { get; } = [];

        internal byte[]? LastWritten { get; private set; }

        /// <summary>注册一个可下载的资源。</summary>
        internal void Add(string url, byte[] content) => _content[url] = content;

        /// <summary>注册一个文本资源。</summary>
        internal void AddText(string url, string content) => _content[url] = System.Text.Encoding.UTF8.GetBytes(content);

        public ValueTask<Result<CoreActions.HttpProbeResult>> ProbeAsync(
            Uri url, ImmutableArray<string> allowedHosts, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url.ToString());

            var check = CoreActions.HostPolicy.Check(url, allowedHosts);
            if (check.IsFailure)
            {
                RejectedHosts.Add(url.Host);
                return ValueTask.FromResult(check.Propagate<CoreActions.HttpProbeResult>());
            }

            if (!_content.TryGetValue(url.ToString(), out var content))
            {
                return ValueTask.FromResult(Result<CoreActions.HttpProbeResult>.Fail(
                    Abs.EnvStationErrorCodes.NetUnreachable, $"找不到 {url}"));
            }

            return ValueTask.FromResult(Result<CoreActions.HttpProbeResult>.Ok(
                new CoreActions.HttpProbeResult(200, url.ToString(), content.Length, "etag-x", null, 0)));
        }

        public ValueTask<Result<string>> FetchTextAsync(
            Uri url, ImmutableArray<string> allowedHosts, int maxBytes, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url.ToString());

            var check = CoreActions.HostPolicy.Check(url, allowedHosts);
            if (check.IsFailure)
            {
                RejectedHosts.Add(url.Host);
                return ValueTask.FromResult(check.Propagate<string>());
            }

            if (!_content.TryGetValue(url.ToString(), out var content))
            {
                return ValueTask.FromResult(Result<string>.Fail(
                    Abs.EnvStationErrorCodes.NetUnreachable, $"找不到 {url}"));
            }

            if (content.Length > maxBytes)
            {
                return ValueTask.FromResult(Result<string>.Fail(
                    Abs.EnvStationErrorCodes.NetSizeLimit, $"内容 {content.Length} 字节超过上限 {maxBytes}"));
            }

            return ValueTask.FromResult(Result<string>.Ok(System.Text.Encoding.UTF8.GetString(content)));
        }

        public ValueTask<Result<CoreActions.DownloadResult>> DownloadAsync(
            Uri url,
            ImmutableArray<string> allowedHosts,
            string destinationPath,
            long maxBytes,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url.ToString());

            // 与真实实现一致：逐跳校验在此处发生（假实现只有一跳）。
            var check = CoreActions.HostPolicy.Check(url, allowedHosts);
            if (check.IsFailure)
            {
                RejectedHosts.Add(url.Host);
                return ValueTask.FromResult(check.Propagate<CoreActions.DownloadResult>());
            }

            if (!_content.TryGetValue(url.ToString(), out var content))
            {
                return ValueTask.FromResult(Result<CoreActions.DownloadResult>.Fail(
                    Abs.EnvStationErrorCodes.NetUnreachable, $"找不到 {url}"));
            }

            if (content.Length > maxBytes)
            {
                return ValueTask.FromResult(Result<CoreActions.DownloadResult>.Fail(
                    Abs.EnvStationErrorCodes.NetSizeLimit,
                    $"下载已超过允许的上限（已接收 {content.Length} 字节），已中止并清理临时文件。"));
            }

            LastWritten = content;
            progress?.Report(content.Length);

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(destinationPath, content);

            var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();


            return ValueTask.FromResult(Result<CoreActions.DownloadResult>.Ok(
                new CoreActions.DownloadResult(content.Length, hash, url.ToString(), 0)));
        }
    }

    // ══════════════════════════ 脚手架 ══════════════════════════

    private static readonly AbsActions.CapabilitySet AllCapabilities = new(
    [
        AbsActions.CapabilityIds.Inspect,
        AbsActions.CapabilityIds.NetDownload,
        AbsActions.CapabilityIds.Archive,
        AbsActions.CapabilityIds.FileSystemInstall,
        AbsActions.CapabilityIds.Cleanup,
        AbsActions.CapabilityIds.ProcessLaunch,
    ]);

    private static Dictionary<string, AbsPkg.ScriptValue> Args(params (string Name, AbsPkg.ScriptValue Value)[] pairs) =>
        pairs.ToDictionary(static p => p.Name, static p => p.Value, StringComparer.Ordinal);

    private static AbsPkg.ScriptValue S(string value) => new AbsPkg.ScriptString(value);

    private static AbsPkg.ScriptValue I(long value) => new AbsPkg.ScriptInteger(value);

    private static AbsPkg.ScriptValue B(bool value) => new AbsPkg.ScriptBoolean(value);

    private static AbsPkg.ScriptValue Arr(params string[] values) =>
        new AbsPkg.ScriptArray([.. values.Select(static v => (AbsPkg.ScriptValue)new AbsPkg.ScriptString(v))]);

    private static AbsActions.ActionResult Run(
        string actionId,
        Dictionary<string, AbsPkg.ScriptValue>? arguments,
        string workRoot,
        FakeNetworkTransport? network = null,
        IEnumerable<string>? hosts = null,
        IEnumerable<string>? capabilities = null,
        CoreActions.ResourceQuota? quota = null)
    {
        var bag = new AbsDiag.FindingBag();
        var registry = CoreActions.ActionRegistry.CreateDefault(bag);
        Assert.True(registry.IsSuccess, $"注册表应可用：{bag.ToReport().ToText()}");

        var descriptor = registry.Value.Descriptors.SingleOrDefault(d => d.ActionId == actionId);
        Assert.NotNull(descriptor, $"动作 {actionId} 应已登记");

        var hash = registry.Value.GetContractHash(actionId, descriptor!.Version);
        var resolved = registry.Value.Resolve(new AbsPkg.ActionReference(actionId, descriptor.Version, hash));
        Assert.True(resolved.IsSuccess, $"应能解析 {actionId}：{resolved.Error?.Message}");

        var bindBag = new AbsDiag.FindingBag();
        var bound = CoreActions.ActionArgumentsBinder.Bind(
            resolved.Value.Descriptor,
            arguments ?? new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal),
            bindBag);

        if (bound.IsFailure)
        {
            return AbsActions.ActionResult.Fail(bound.Error.Code, "参数绑定失败：" + bindBag.ToReport().ToText());
        }

        var context = new CoreActions.ActionExecutionContext(
            "x-test.net-archive",
            "run-na",
            new AbsActions.CapabilitySet(capabilities ?? AllCapabilities.Ids),
            [workRoot],
            new CoreActions.VariableTable(),
            new CoreActions.QuotaMeter(quota ?? CoreActions.ResourceQuota.Default),
            new CoreActions.MemoryAuditSink(),
            allowedHosts: hosts,
            network: network);

        return CoreActions.ActionExecutor
            .ExecuteAsync(resolved.Value, context, bound.Value)
            .AsTask().GetAwaiter().GetResult();
    }

    private static string Sha256Of(byte[] content)
    {
        return "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    }

    // ══════════════════════════ A02 下载 ══════════════════════════

    private static void DownloadCases(TestHarness h)
    {
        var work = Path.Combine(Path.GetTempPath(), "envstation-na-dl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("this-is-the-real-payload");

            h.Case("NA-01", "★net.download：域名不在白名单时拒绝，且不发出请求", () =>
            {
                var network = new FakeNetworkTransport();
                network.Add("https://evil.example.com/x.bin", payload);

                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://evil.example.com/x.bin")),
                        ("sha256", S(Sha256Of(payload))),
                        ("dest", S(Path.Combine(work, "out.bin")))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.False(result.Success, "白名单外的域名应被拒绝");
                Assert.Equal(0, network.RequestedUrls.Count, "拒绝必须发生在发起请求之前");
                Assert.Contains("不在该包声明的白名单内", result.Message, "应说明原因");
            });

            h.Case("NA-02", "★net.download：未声明任何域名时拒绝一切网络请求", () =>
            {
                var network = new FakeNetworkTransport();
                network.Add("https://mirrors.aliyun.com/x.bin", payload);

                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://mirrors.aliyun.com/x.bin")),
                        ("sha256", S(Sha256Of(payload))),
                        ("dest", S(Path.Combine(work, "out.bin")))),
                    work,
                    network,
                    hosts: []);

                Assert.False(result.Success, "空白名单必须等于什么都不允许");
                Assert.Contains("没有声明任何允许访问的域名", result.Message, "应给出可操作提示");
            });

            h.Case("NA-03", "net.download：白名单内下载成功并通过哈希校验", () =>
            {
                var network = new FakeNetworkTransport();
                network.Add("https://mirrors.aliyun.com/py.exe", payload);

                var dest = Path.Combine(work, "py.exe");
                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://mirrors.aliyun.com/py.exe")),
                        ("sha256", S(Sha256Of(payload))),
                        ("dest", S(dest))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.True(File.Exists(dest), "文件应已落盘");
                Assert.Equal("false", result.Outputs["skipped"], "首次下载不应标记为跳过");
            });

            h.Case("NA-04", "★net.download：哈希不符时必须删除文件", () =>
            {
                var network = new FakeNetworkTransport();
                network.Add("https://mirrors.aliyun.com/bad.exe", payload);

                var dest = Path.Combine(work, "bad.exe");
                var wrongHash = "sha256:" + new string('0', 64);

                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://mirrors.aliyun.com/bad.exe")),
                        ("sha256", S(wrongHash)),
                        ("dest", S(dest))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.False(result.Success, "哈希不符应失败");
                Assert.False(File.Exists(dest),
                    "内容不对的文件绝不能留在磁盘上——后续步骤可能拿它去安装");
                Assert.Contains("哈希不符", result.Message, "应说明是哈希问题");
            });

            h.Case("NA-05", "net.download：哈希不符时回退到镜像", () =>
            {
                var network = new FakeNetworkTransport();
                network.Add("https://mirrors.aliyun.com/x.bin", System.Text.Encoding.UTF8.GetBytes("wrong-content"));
                network.Add("https://mirrors.tuna.tsinghua.edu.cn/x.bin", payload);

                var dest = Path.Combine(work, "mirror.bin");
                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://mirrors.aliyun.com/x.bin")),
                        ("sha256", S(Sha256Of(payload))),
                        ("dest", S(dest)),
                        ("mirrors", Arr("https://mirrors.tuna.tsinghua.edu.cn/x.bin"))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com", "mirrors.tuna.tsinghua.edu.cn"]);

                Assert.True(result.Success, $"镜像应被尝试并成功：{result.Message}");
                Assert.Equal(2, network.RequestedUrls.Count, "应依次尝试主源与镜像");
                Assert.True(File.Exists(dest), "文件应已落盘");
            });

            h.Case("NA-06", "★net.download：超过大小上限时中止（边下边判）", () =>
            {
                var network = new FakeNetworkTransport();
                var big = new byte[2 * 1024 * 1024];
                network.Add("https://mirrors.aliyun.com/big.bin", big);

                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://mirrors.aliyun.com/big.bin")),
                        ("sha256", S(Sha256Of(big))),
                        ("dest", S(Path.Combine(work, "big.bin"))),
                        ("max_bytes", I(1024 * 1024))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.False(result.Success, "超过上限应失败");
                Assert.Equal(Abs.EnvStationErrorCodes.NetSizeLimit, result.ErrorCode, "应返回大小限制码");
            });

            h.Case("NA-07", "★net.download：明文 HTTP 默认拒绝，显式允许后才放行", () =>
            {
                var network = new FakeNetworkTransport();
                network.Add("http://intranet.corp.local/x.bin", payload);

                var dest = Path.Combine(work, "http.bin");
                var rejected = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("http://intranet.corp.local/x.bin")),
                        ("sha256", S(Sha256Of(payload))),
                        ("dest", S(dest))),
                    work,
                    network,
                    hosts: ["intranet.corp.local"]);

                Assert.False(rejected.Success, "明文 HTTP 默认应被拒绝");
                Assert.Contains("allow_http", rejected.Message, "应告诉用户怎么显式降级");
                Assert.Equal(0, network.RequestedUrls.Count, "拒绝应发生在请求之前");

                var allowed = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("http://intranet.corp.local/x.bin")),
                        ("sha256", S(Sha256Of(payload))),
                        ("dest", S(dest)),
                        ("allow_http", B(true))),
                    work,
                    network,
                    hosts: ["intranet.corp.local"]);

                Assert.True(allowed.Success, $"显式允许后应成功：{allowed.Message}");
            });

            h.Case("NA-08", "★net.download：拒绝非 http(s) 协议", () =>
            {
                var network = new FakeNetworkTransport();
                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S(@"file:///C:/Windows/System32/drivers/etc/hosts")),
                        ("sha256", S("sha256:" + new string('a', 64))),
                        ("dest", S(Path.Combine(work, "x.bin")))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.False(result.Success, "file:// 协议应被拒绝");
                Assert.Contains("http 或 https", result.Message, "应说明协议限制（在参数校验期就被拦下）");
            });

            h.Case("NA-09", "net.download：目标已存在且哈希正确时跳过（幂等）", () =>
            {
                var network = new FakeNetworkTransport();
                network.Add("https://mirrors.aliyun.com/cached.bin", payload);

                var dest = Path.Combine(work, "cached.bin");
                File.WriteAllBytes(dest, payload);

                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://mirrors.aliyun.com/cached.bin")),
                        ("sha256", S(Sha256Of(payload))),
                        ("dest", S(dest))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("true", result.Outputs["skipped"], "应标记为跳过");
                Assert.Equal(0, network.RequestedUrls.Count, "跳过时不应发起任何请求");
            });

            h.Case("NA-10", "★net.download：没有网络通道时明确失败（不静默）", () =>
            {
                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://mirrors.aliyun.com/x.bin")),
                        ("sha256", S(Sha256Of(payload))),
                        ("dest", S(Path.Combine(work, "x.bin")))),
                    work,
                    network: null,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.False(result.Success, "无网络通道应失败");
                Assert.Contains("没有启用网络能力", result.Message, "应说明是环境未启用，而不是网络故障");
            });

            h.Case("NA-11", "★net.download：目标路径越界被拒绝", () =>
            {
                var network = new FakeNetworkTransport();
                network.Add("https://mirrors.aliyun.com/x.bin", payload);

                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://mirrors.aliyun.com/x.bin")),
                        ("sha256", S(Sha256Of(payload))),
                        ("dest", S(@"C:\Windows\Temp\evil.bin"))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.False(result.Success, "越界目标路径应被拒绝");
                Assert.Equal(Abs.EnvStationErrorCodes.PathOutsideAuthorizedRoot, result.ErrorCode, "应返回越界码");
            });

            h.Case("NA-12", "net.download：sha256 格式非法时拒绝", () =>
            {
                var network = new FakeNetworkTransport();
                var result = Run(
                    "envstation.net.download",
                    Args(
                        ("url", S("https://mirrors.aliyun.com/x.bin")),
                        ("sha256", S("md5:abc")),
                        ("dest", S(Path.Combine(work, "x.bin")))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.False(result.Success, "非法哈希应被拒绝");
                Assert.Equal(0, network.RequestedUrls.Count, "校验期就应拒绝，不发起请求");
            });
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    // ══════════════════════════ 哈希与探测 ══════════════════════════

    private static void HashAndProbeCases(TestHarness h)
    {
        var work = Path.Combine(Path.GetTempPath(), "envstation-na-hp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("hash-me");

            h.Case("NA-13", "net.verify_hash：一致通过、不一致失败", () =>
            {
                var file = Path.Combine(work, "h.bin");
                File.WriteAllBytes(file, payload);

                var ok = Run(
                    "envstation.net.verify_hash",
                    Args(("path", S(file)), ("sha256", S(Sha256Of(payload)))),
                    work);
                Assert.True(ok.Success, $"应通过：{ok.Message}");

                var bad = Run(
                    "envstation.net.verify_hash",
                    Args(("path", S(file)), ("sha256", S("sha256:" + new string('b', 64)))),
                    work);
                Assert.False(bad.Success, "哈希不符应失败");
                Assert.Equal(Abs.EnvStationErrorCodes.NetHashMismatch, bad.ErrorCode, "应返回哈希不符码");
            });

            h.Case("NA-14", "net.verify_hash：超过 max_bytes 时拒绝读取", () =>
            {
                var file = Path.Combine(work, "big.bin");
                File.WriteAllBytes(file, new byte[64 * 1024]);

                var result = Run(
                    "envstation.net.verify_hash",
                    Args(("path", S(file)), ("sha256", S(Sha256Of(new byte[64 * 1024]))), ("max_bytes", I(1024))),
                    work);

                Assert.False(result.Success, "超过上限应拒绝读取");
                Assert.Contains("拒绝读取", result.Message, "应说明是为了避免误对超大文件做哈希");
            });

            h.Case("NA-15", "net.head：返回大小与 ETag", () =>
            {
                var network = new FakeNetworkTransport();
                network.Add("https://mirrors.aliyun.com/probe.bin", payload);

                var result = Run(
                    "envstation.net.head",
                    Args(("url", S("https://mirrors.aliyun.com/probe.bin"))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal(payload.Length.ToString(CultureInfo.InvariantCulture), result.Outputs["content_length"], "应返回真实大小");
            });

            h.Case("NA-16", "net.fetch_text：返回文本并支持 expect_contains 断言", () =>
            {
                var network = new FakeNetworkTransport();
                network.AddText("https://mirrors.aliyun.com/version.txt", "latest=3.12.1\nchannel=stable");

                var ok = Run(
                    "envstation.net.fetch_text",
                    Args(("url", S("https://mirrors.aliyun.com/version.txt")), ("expect_contains", S("3.12.1"))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);
                Assert.True(ok.Success, $"应成功：{ok.Message}");
                Assert.Contains("3.12.1", ok.Outputs["text"], "应返回内容");

                var bad = Run(
                    "envstation.net.fetch_text",
                    Args(("url", S("https://mirrors.aliyun.com/version.txt")), ("expect_contains", S("9.9.9"))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);
                Assert.False(bad.Success, "内容不含期望子串应失败");
                Assert.Contains("镜像返回了错误页", bad.Message, "应指出最可能的原因");
            });

            h.Case("NA-17", "net.fetch_text：超过大小上限时拒绝", () =>
            {
                var network = new FakeNetworkTransport();
                network.AddText("https://mirrors.aliyun.com/big.txt", new string('x', 5000));

                var result = Run(
                    "envstation.net.fetch_text",
                    Args(("url", S("https://mirrors.aliyun.com/big.txt")), ("max_bytes", I(100))),
                    work,
                    network,
                    hosts: ["mirrors.aliyun.com"]);

                Assert.False(result.Success, "超过上限应失败");
                Assert.Equal(Abs.EnvStationErrorCodes.NetSizeLimit, result.ErrorCode, "应返回大小限制码");
            });
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    // ══════════════════════════ A03 解压：安全 ══════════════════════════

    private static void ExtractionSafetyCases(TestHarness h)
    {
        var work = Path.Combine(Path.GetTempPath(), "envstation-na-zip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            h.Case("NA-18", "★archive.extract：拒绝相对上跳（Zip Slip 经典形态）", () =>
            {
                var archive = Path.Combine(work, "traversal.zip");
                CreateZip(archive, ("../../escaped.txt", "pwned"), ("bin/ok.txt", "fine"));

                var dest = Path.Combine(work, "out1");
                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(dest))),
                    work);

                Assert.False(result.Success, "含上跳条目的归档应被拒绝");
                Assert.Equal(Abs.EnvStationErrorCodes.ArchivePathTraversal, result.ErrorCode, "应返回穿越码");
                Assert.False(Directory.Exists(dest), "预检失败时不应创建目标目录");
            });

            h.Case("NA-19", "★archive.extract：拒绝绝对路径条目", () =>
            {
                var archive = Path.Combine(work, "absolute.zip");
                CreateZip(archive, (@"C:\Windows\Temp\pwned.txt", "pwned"));

                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(Path.Combine(work, "out2")))),
                    work);

                Assert.False(result.Success, "绝对路径条目应被拒绝");
                Assert.Contains("已拒绝解压", result.Message, "应说明拒绝原因");
            });

            h.Case("NA-20", "★archive.extract：拒绝 UNC 网络路径条目", () =>
            {
                var archive = Path.Combine(work, "unc.zip");
                CreateZip(archive, (@"\\attacker\share\pwned.txt", "pwned"));

                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(Path.Combine(work, "out3")))),
                    work);

                Assert.False(result.Success, "UNC 条目应被拒绝");
            });

            h.Case("NA-21", "★archive.extract：拒绝符号链接条目（tar）", () =>
            {
                var archive = Path.Combine(work, "link.tar");
                using (var file = File.Create(archive))
                using (var writer = new TarWriter(file, leaveOpen: false))
                {
                    var entry = new PaxTarEntry(TarEntryType.SymbolicLink, "evil-link")
                    {
                        LinkName = @"C:\Windows\System32",
                    };
                    writer.WriteEntry(entry);
                }

                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(Path.Combine(work, "out4")))),
                    work);

                Assert.False(result.Success, "含符号链接的归档应被拒绝");
                Assert.Contains("链接条目", result.Message, "应说明拒绝原因");
            });

            h.Case("NA-22", "★archive.extract：压缩比异常时判为压缩炸弹", () =>
            {
                var archive = Path.Combine(work, "bomb.zip");
                // 20 MB 全零：压缩后极小，压缩比远超 200
                var zeros = new byte[20 * 1024 * 1024];
                CreateZip(archive, ("zeros.bin", zeros));

                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(Path.Combine(work, "out5")))),
                    work);

                Assert.False(result.Success, "压缩比异常应被拒绝");
                Assert.Contains("压缩炸弹", result.Message, "应点明威胁类型");
            });

            h.Case("NA-23", "★archive.extract：解压总量超过配额时拒绝", () =>
            {
                var archive = Path.Combine(work, "medium.zip");
                CreateZip(archive, ("data.bin", new byte[512 * 1024]));

                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(Path.Combine(work, "out6")))),
                    work,
                    quota: new CoreActions.ResourceQuota(1024, 64 * 1024, 100, TimeSpan.FromMinutes(1)));

                Assert.False(result.Success, "超过解压配额应被拒绝");
                Assert.False(Directory.Exists(Path.Combine(work, "out6")), "配额不足时不应创建目标目录");
            });

            h.Case("NA-24", "★archive.extract：拒绝不支持的格式（含 7z 的说明）", () =>
            {
                var file = Path.Combine(work, "archive.7z");
                File.WriteAllBytes(file, [0x37, 0x7A, 0xBC, 0xAF]);

                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(file)), ("dest", S(Path.Combine(work, "out7")))),
                    work);

                Assert.False(result.Success, "7z 应被明确拒绝");
                Assert.Contains("7z", result.Message, "应说明不支持的原因");
            });
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    // ══════════════════════════ A03 解压：功能 ══════════════════════════

    private static void ExtractionFunctionCases(TestHarness h)
    {
        var work = Path.Combine(Path.GetTempPath(), "envstation-na-fn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            h.Case("NA-25", "archive.extract：正常解压 zip 并保留目录结构", () =>
            {
                var archive = Path.Combine(work, "ok.zip");
                CreateZip(archive, ("jdk-17/bin/java.exe", "fake-java"), ("jdk-17/README.txt", "hello"));

                var dest = Path.Combine(work, "extracted");
                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(dest))),
                    work);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.True(File.Exists(Path.Combine(dest, "jdk-17", "bin", "java.exe")), "嵌套文件应解压到位");
                Assert.Equal("2", result.Outputs["extract_count"], "应解压 2 个文件");
            });

            h.Case("NA-26", "archive.extract：strip_components 去掉最外层目录", () =>
            {
                var archive = Path.Combine(work, "strip.zip");
                CreateZip(archive, ("node-v20/bin/node.exe", "fake-node"), ("node-v20/package.json", "{}"));

                var dest = Path.Combine(work, "stripped");
                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(dest)), ("strip_components", I(1))),
                    work);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.True(File.Exists(Path.Combine(dest, "bin", "node.exe")),
                    "去掉一层后内容应提到根目录（Node 的 tar.gz 就是这个结构）");
            });

            h.Case("NA-27", "archive.extract：include 过滤只解压匹配前缀的条目", () =>
            {
                var archive = Path.Combine(work, "filter.zip");
                CreateZip(archive, ("bin/a.exe", "a"), ("docs/readme.md", "r"), ("lib/b.dll", "b"));

                var dest = Path.Combine(work, "filtered");
                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(dest)), ("include", Arr("bin/", "lib/"))),
                    work);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.True(File.Exists(Path.Combine(dest, "bin", "a.exe")), "匹配项应解压");
                Assert.False(File.Exists(Path.Combine(dest, "docs", "readme.md")), "未匹配项不应解压");
                Assert.Equal("1", result.Outputs["skipped_by_filter"], "应统计被过滤的条目数");
            });

            h.Case("NA-28", "archive.extract：dry_run 做完全部安全检查但不写文件", () =>
            {
                var archive = Path.Combine(work, "dry.zip");
                CreateZip(archive, ("a/b/c.txt", "data"));

                var dest = Path.Combine(work, "dry-out");
                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(dest)), ("dry_run", B(true))),
                    work);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("true", result.Outputs["dry_run"], "应标记为干跑");
                Assert.False(Directory.Exists(dest), "干跑不应创建目录");
            });

            h.Case("NA-29", "archive.extract：tar.gz 解压可用（Node / Go 的发行格式）", () =>
            {
                var archive = Path.Combine(work, "pkg.tar.gz");
                using (var file = File.Create(archive))
                using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                using (var writer = new TarWriter(gzip, leaveOpen: false))
                {
                    var tempFile = Path.Combine(work, "payload.txt");
                    File.WriteAllText(tempFile, "tar-content");
                    writer.WriteEntry(tempFile, "go/bin/go.exe");
                }

                var dest = Path.Combine(work, "tar-out");
                var result = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(dest))),
                    work);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.True(File.Exists(Path.Combine(dest, "go", "bin", "go.exe")), "tar.gz 内容应解压到位");
            });

            h.Case("NA-30", "archive.list：在解压前就能报出危险条目", () =>
            {
                var archive = Path.Combine(work, "risky.zip");
                CreateZip(archive, ("../../escape.txt", "evil"), ("ok.txt", "fine"));

                var result = Run("envstation.archive.list", Args(("file", S(archive))), work);

                Assert.True(result.Success, $"列目录本身应成功：{result.Message}");
                Assert.True(
                    int.Parse(result.Outputs["risk_count"], CultureInfo.InvariantCulture) >= 1,
                    "应报出可疑路径，让先 list 再 extract 这条推荐流程真正有用");
                Assert.Contains("可疑路径", result.Outputs["risks"], "应指明风险类型");
            });

            h.Case("NA-31", "archive.list：返回条目数与压缩比", () =>
            {
                var archive = Path.Combine(work, "list.zip");
                CreateZip(archive, ("a.txt", "1"), ("b.txt", "2"), ("c/d.txt", "3"));

                var result = Run("envstation.archive.list", Args(("file", S(archive))), work);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("3", result.Outputs["file_count"], "应统计 3 个文件");
                Assert.True(result.Outputs.ContainsKey("compression_ratio"), "应返回压缩比");
            });

            h.Case("NA-32", "archive.create：打包目录并可用 extract 还原", () =>
            {
                var source = Path.Combine(work, "to-pack");
                Directory.CreateDirectory(Path.Combine(source, "sub"));
                File.WriteAllText(Path.Combine(source, "one.txt"), "content-one");
                File.WriteAllText(Path.Combine(source, "sub", "two.txt"), "content-two");

                var archive = Path.Combine(work, "packed.zip");
                var created = Run(
                    "envstation.archive.create",
                    Args(("dir", S(source)), ("out", S(archive))),
                    work);

                Assert.True(created.Success, $"打包应成功：{created.Message}");
                Assert.Equal("2", created.Outputs["file_count"], "应打包 2 个文件");

                var restored = Path.Combine(work, "restored");
                var extracted = Run(
                    "envstation.archive.extract",
                    Args(("file", S(archive)), ("dest", S(restored))),
                    work);

                Assert.True(extracted.Success, $"还原应成功：{extracted.Message}");
                Assert.Equal("content-one", File.ReadAllText(Path.Combine(restored, "one.txt")), "内容应一致");
                Assert.Equal("content-two", File.ReadAllText(Path.Combine(restored, "sub", "two.txt")), "嵌套内容应一致");
            });

            h.Case("NA-33", "★archive.create：拒绝把输出写进源目录内部", () =>
            {
                var source = Path.Combine(work, "self-pack");
                Directory.CreateDirectory(source);

                var result = Run(
                    "envstation.archive.create",
                    Args(("dir", S(source)), ("out", S(Path.Combine(source, "self.zip")))),
                    work);

                Assert.False(result.Success, "输出位于源目录内应被拒绝");
                Assert.Contains("把自己也打进去", result.Message, "应说明无限递归的原因");
            });

            h.Case("NA-34", "archive.create：目标已存在时默认不覆盖", () =>
            {
                var source = Path.Combine(work, "src-nc");
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "f.txt"), "x");

                var archive = Path.Combine(work, "exists.zip");
                File.WriteAllText(archive, "existing");

                var result = Run(
                    "envstation.archive.create",
                    Args(("dir", S(source)), ("out", S(archive))),
                    work);

                Assert.False(result.Success, "目标已存在时应拒绝");
                Assert.Equal("existing", File.ReadAllText(archive), "已有文件必须完好");
            });
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    // ══════════════════════════ 工具 ══════════════════════════

    private static void CreateZip(string path, params (string Name, string Content)[] entries) =>
        CreateZip(path, [.. entries.Select(static e => (e.Name, System.Text.Encoding.UTF8.GetBytes(e.Content)))]);

    private static void CreateZip(string path, params (string Name, byte[] Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响用例结论。
        }
    }
}
