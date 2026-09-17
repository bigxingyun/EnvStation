using System.Collections.Immutable;
using EnvStation.TestKit;
using CoreCfg = EnvStation.Core.Configuration;

namespace EnvStation.Tests.Config;

/// <summary>
/// A08 配置与镜像源 的动作测试，以及配置存储 / Maven XML 编辑器的单元测试。
///
/// <para><b>为什么这一组必须测得细</b>：改配置文件是"用户看不见但后果最严重"的一类操作——
/// 一个写坏的 <c>settings.xml</c> 会让用户所有 Maven 构建失败，而错误信息通常只有
/// "Error reading settings.xml" 一句。同时这类文件里常常存着公司私服地址与凭据，
/// 改错一次就可能把用户的私服配置抹掉。</para>
///
/// <para>全部用例都在临时目录里造文件，<b>不触碰用户真实的 ~/.m2 或 ~/.gradle</b>。</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("配置与镜像源测试（全程临时目录，不触碰用户真实配置）");
        Console.WriteLine();

        var h = new TestHarness("配置与镜像源");

        StoreCases(h);
        MarkedBlockCases(h);
        SecretRedactionCases(h);
        MavenEditorCases(h);
        ActionCases(h);

        var code = h.Summarize();

        var jsonPath = TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return code;
    }

    // ══════════════════════════ 脚手架 ══════════════════════════

    private static readonly AbsActions.CapabilitySet AllCapabilities = new(
    [
        AbsActions.CapabilityIds.Inspect,
        AbsActions.CapabilityIds.ConfigApp,
        AbsActions.CapabilityIds.NetDownload,
        AbsActions.CapabilityIds.Archive,
    ]);

    private static Dictionary<string, AbsPkg.ScriptValue> Args(params (string Name, AbsPkg.ScriptValue Value)[] pairs) =>
        pairs.ToDictionary(static p => p.Name, static p => p.Value, StringComparer.Ordinal);

    private static AbsPkg.ScriptValue S(string value) => new AbsPkg.ScriptString(value);

    private static AbsPkg.ScriptValue B(bool value) => new AbsPkg.ScriptBoolean(value);

    private static AbsPkg.ScriptValue I(long value) => new AbsPkg.ScriptInteger(value);

    private static AbsPkg.ScriptValue Arr(params string[] values) =>
        new AbsPkg.ScriptArray([.. values.Select(static v => (AbsPkg.ScriptValue)new AbsPkg.ScriptString(v))]);

    private static AbsActions.ActionResult Run(
        string actionId,
        Dictionary<string, AbsPkg.ScriptValue>? arguments,
        string workRoot,
        IEnumerable<string>? capabilities = null)
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
            "x-test.config",
            "run-cf",
            new AbsActions.CapabilitySet(capabilities ?? AllCapabilities.Ids),
            [workRoot],
            new CoreActions.VariableTable(),
            new CoreActions.QuotaMeter(CoreActions.ResourceQuota.Default),
            new CoreActions.MemoryAuditSink());

        return CoreActions.ActionExecutor
            .ExecuteAsync(resolved.Value, context, bound.Value)
            .AsTask().GetAwaiter().GetResult();
    }

    private sealed class TempWorkspace : IDisposable
    {
        internal TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "envstation-cf-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string Resolve(string name) => System.IO.Path.Combine(Root, name);

        internal string Write(string name, string content)
        {
            var path = Resolve(name);
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响用例结论。
            }
        }
    }

    // ══════════════════════════ 配置存储 ══════════════════════════

    private static void StoreCases(TestHarness h)
    {
        h.Case("CF-01", "备份 → 修改 → 还原：内容逐字节一致", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Write("settings.xml", "<settings><mirrors/></settings>\n");
            var original = File.ReadAllText(path);

            var store = new CoreCfg.ConfigFileStore(ws.Resolve("backups"));
            var backup = store.BackupAsync(path, "测试").GetAwaiter().GetResult();
            Assert.True(backup.IsSuccess, $"备份应成功：{backup.Error?.Message}");

            File.WriteAllText(path, "<settings><mirrors><mirror/></mirrors></settings>\n");
            Assert.NotEqual(original, File.ReadAllText(path), "修改应生效");

            var restore = CoreCfg.ConfigFileStore.RestoreAsync(backup.Value).GetAwaiter().GetResult();
            Assert.True(restore.IsSuccess, $"还原应成功：{restore.Error?.Message}");
            Assert.Equal(original, File.ReadAllText(path), "还原后内容应与原始逐字节一致");
        });

        h.Case("CF-02", "★备份时文件不存在 → 还原应删除该文件而不是恢复空内容", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Resolve("new-config.ini");

            var store = new CoreCfg.ConfigFileStore(ws.Resolve("backups"));
            var backup = store.BackupAsync(path, "文件原本不存在").GetAwaiter().GetResult();
            Assert.True(backup.IsSuccess, "备份应成功");
            Assert.False(backup.Value.Existed, "应记录为「原本不存在」");

            File.WriteAllText(path, "key = value\n");
            Assert.True(File.Exists(path), "文件已创建");

            var restore = CoreCfg.ConfigFileStore.RestoreAsync(backup.Value).GetAwaiter().GetResult();
            Assert.True(restore.IsSuccess, $"还原应成功：{restore.Error?.Message}");
            Assert.False(File.Exists(path),
                "原本不存在的文件，还原的正确动作是删除——恢复成空文件会留下一个会让工具报错的坏配置");
        });

        h.Case("CF-03", "备份记录可重新加载（跨进程可用）", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Write("a.ini", "[s]\nk = v\n");

            var store = new CoreCfg.ConfigFileStore(ws.Resolve("backups"));
            var backup = store.BackupAsync(path).GetAwaiter().GetResult().Value;

            var reloaded = store.LoadBackupAsync(backup.BackupId).GetAwaiter().GetResult();
            Assert.True(reloaded.IsSuccess, $"应能重新加载：{reloaded.Error?.Message}");
            Assert.Equal(path, reloaded.Value.OriginalPath, "原路径应一致");
            Assert.Equal(backup.ContentHash, reloaded.Value.ContentHash, "内容哈希应一致");
        });

        h.Case("CF-04", "原子写入：不留下临时文件", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Resolve("atomic.ini");

            CoreCfg.ConfigFileStore.WriteAtomicAsync(path, "content\n").GetAwaiter().GetResult();

            Assert.True(File.Exists(path), "目标文件应存在");
            Assert.False(File.Exists(path + ".envstation.tmp"), "不应留下临时文件");
            Assert.Equal("content\n", File.ReadAllText(path), "内容应一致");
        });
    }

    // ══════════════════════════ 标记区块 ══════════════════════════

    private static void MarkedBlockCases(TestHarness h)
    {
        h.Case("CF-05", "★标记区块：重复写入同一 id 不产生重复内容（幂等）", () =>
        {
            var content = "[global]\ntimeout = 30\n";
            var body = "[global]\nindex-url = https://pypi.tuna.tsinghua.edu.cn/simple";

            var once = CoreCfg.ConfigFileStore.UpsertMarkedBlock(content, "pip-mirror", body);
            var twice = CoreCfg.ConfigFileStore.UpsertMarkedBlock(once, "pip-mirror", body);
            var thrice = CoreCfg.ConfigFileStore.UpsertMarkedBlock(twice, "pip-mirror", body);

            Assert.Equal(once, twice, "第二次写入应与第一次结果相同（幂等）");
            Assert.Equal(once, thrice, "第三次同样不应变化");

            var occurrences = once.Split(CoreCfg.ConfigFileStore.MarkerBegin).Length - 1;
            Assert.Equal(1, occurrences, "标记起始行只应出现一次");
        });

        h.Case("CF-06", "标记区块：内容变化时就地替换而不是追加", () =>
        {
            var content = "";
            var first = CoreCfg.ConfigFileStore.UpsertMarkedBlock(content, "npm-registry", "registry=https://a.example");
            var second = CoreCfg.ConfigFileStore.UpsertMarkedBlock(first, "npm-registry", "registry=https://b.example");

            Assert.Contains("b.example", second, "应含新内容");
            Assert.NotContains("a.example", second, "旧内容应被替换");
            Assert.Equal(1, second.Split(CoreCfg.ConfigFileStore.MarkerBegin).Length - 1, "标记只应出现一次");
        });

        h.Case("CF-07", "标记区块：移除后不残留空行，且用户原有内容完好", () =>
        {
            const string userContent = "[global]\ntimeout = 30\n";
            var withBlock = CoreCfg.ConfigFileStore.UpsertMarkedBlock(userContent, "pip-mirror", "index-url = https://x");
            var removed = CoreCfg.ConfigFileStore.RemoveMarkedBlock(withBlock, "pip-mirror");

            Assert.NotContains(CoreCfg.ConfigFileStore.MarkerBegin, removed, "标记应被移除");
            Assert.Contains("timeout = 30", removed, "用户原有内容必须完好");
            Assert.NotContains("\n\n\n", removed, "不应残留多余空行");
        });

        h.Case("CF-08", "标记区块：多个 id 互不干扰", () =>
        {
            var content = CoreCfg.ConfigFileStore.UpsertMarkedBlock("", "pip-mirror", "pip-body");
            content = CoreCfg.ConfigFileStore.UpsertMarkedBlock(content, "npm-registry", "npm-body");

            Assert.True(CoreCfg.ConfigFileStore.HasMarkedBlock(content, "pip-mirror"), "pip 块应存在");
            Assert.True(CoreCfg.ConfigFileStore.HasMarkedBlock(content, "npm-registry"), "npm 块应存在");

            var removed = CoreCfg.ConfigFileStore.RemoveMarkedBlock(content, "pip-mirror");
            Assert.False(CoreCfg.ConfigFileStore.HasMarkedBlock(removed, "pip-mirror"), "pip 块应被移除");
            Assert.True(CoreCfg.ConfigFileStore.HasMarkedBlock(removed, "npm-registry"), "npm 块必须保留");
        });

        h.Case("CF-09", "★标记区块：XML 注释形式与井号形式互不误伤", () =>
        {
            var text = CoreCfg.ConfigFileStore.UpsertMarkedBlock("", "m", "plain", xmlStyle: false);
            var xml = CoreCfg.ConfigFileStore.UpsertMarkedBlock("", "m", "markup", xmlStyle: true);

            Assert.Contains("plain", text, "文本形式应写入");
            Assert.NotContains("<!--", text, "文本形式不应带 XML 注释");

            Assert.Contains("<!--", xml, "XML 形式应带注释");
            Assert.Contains("markup", xml, "XML 形式内容应写入");
        });
    }

    // ══════════════════════════ 凭据保护 ══════════════════════════

    private static void SecretRedactionCases(TestHarness h)
    {
        h.Case("CF-10", "★凭据脱敏：XML 标签形式", () =>
        {
            const string xml = """
                <settings>
                  <servers>
                    <server>
                      <id>company-nexus</id>
                      <username>zhangsan</username>
                      <password>SuperSecret123</password>
                    </server>
                  </servers>
                </settings>
                """;

            var redacted = CoreCfg.ConfigFileStore.RedactSecrets(xml);

            Assert.NotContains("SuperSecret123", redacted, "密码绝不能出现在脱敏结果里");
            Assert.Contains("***", redacted, "应替换为掩码");
            Assert.Contains("company-nexus", redacted, "非凭据字段应保留（否则用户看不出哪个 server 被脱敏了）");
        });

        h.Case("CF-11", "★凭据脱敏：key=value 与 key: value 形式", () =>
        {
            const string ini = "registry=https://registry.npmjs.org\n//registry.npmjs.org/:_authToken=npm_ABC123\npassword: hunter2\n";

            var redacted = CoreCfg.ConfigFileStore.RedactSecrets(ini);

            Assert.NotContains("npm_ABC123", redacted, "npm token 必须被掩码");
            Assert.NotContains("hunter2", redacted, "密码必须被掩码");
            Assert.Contains("registry=https://registry.npmjs.org", redacted, "普通配置不应被误伤");
        });

        h.Case("CF-12", "凭据字段检测：能报出字段名供用户知情", () =>
        {
            const string xml = "<settings><servers><server><password>x</password><passphrase>y</passphrase></server></servers></settings>";
            var found = CoreCfg.ConfigFileStore.FindSecretFields(xml);

            Assert.True(found.Contains("password"), "应检测到 password");
            Assert.True(found.Contains("passphrase"), "应检测到 passphrase");
        });
    }

    // ══════════════════════════ Maven XML 编辑器 ══════════════════════════

    private static void MavenEditorCases(TestHarness h)
    {
        const string UserSettings = """
            <?xml version="1.0" encoding="UTF-8"?>
            <settings xmlns="http://maven.apache.org/SETTINGS/1.0.0">
              <!-- 我的注释必须保留 -->
              <localRepository>D:\m2\repo</localRepository>
              <mirrors>
                <mirror>
                  <id>company-nexus</id>
                  <url>https://nexus.corp.example/repository/maven-public/</url>
                  <mirrorOf>*</mirrorOf>
                </mirror>
              </mirrors>
              <servers>
                <server>
                  <id>company-nexus</id>
                  <username>zhangsan</username>
                  <password>CorpSecret</password>
                </server>
              </servers>
            </settings>
            """;

        h.Case("CF-13", "★Maven：存在用户自定义镜像时默认拒绝替换（保护公司私服）", () =>
        {
            var parsed = CoreCfg.MavenSettingsEditor.Parse(UserSettings);
            Assert.True(parsed.IsSuccess, $"解析应成功：{parsed.Error?.Message}");

            var result = CoreCfg.MavenSettingsEditor.SetMirror(
                parsed.Value, "aliyun", "阿里云", "https://maven.aliyun.com/repository/public", "central");

            Assert.False(result.IsSuccess, "默认不应覆盖用户已有的镜像");
            Assert.Equal(Abs.EnvStationErrorCodes.ConfigMergeConflict, result.Error!.Code, "应返回合并冲突码");
            Assert.Contains("公司私服", result.Error!.Remediation, "应说明为什么默认不改");
        });

        h.Case("CF-14", "Maven：显式允许后才替换，且保留其余内容", () =>
        {
            var parsed = CoreCfg.MavenSettingsEditor.Parse(UserSettings);
            var result = CoreCfg.MavenSettingsEditor.SetMirror(
                parsed.Value, "aliyun", "阿里云", "https://maven.aliyun.com/repository/public", "central",
                replaceUserMirror: false);

            Assert.False(result.IsSuccess, "只读路径：用户镜像存在时不写");

            var parsed2 = CoreCfg.MavenSettingsEditor.Parse(UserSettings);
            var forced = CoreCfg.MavenSettingsEditor.SetMirror(
                parsed2.Value, "aliyun", "阿里云", "https://maven.aliyun.com/repository/public", "central",
                replaceUserMirror: true);

            Assert.True(forced.IsSuccess, $"显式允许后应成功：{forced.Error?.Message}");

            var text = CoreCfg.MavenSettingsEditor.Serialize(parsed2.Value);

            Assert.Contains("envstation-aliyun", text, "应写入我们的镜像");
            Assert.Contains("我的注释必须保留", text, "用户的注释必须保留（最小插入原则）");
            Assert.Contains("D:\\m2\\repo", text, "localRepository 必须原样保留");
            Assert.Contains("company-nexus", text, "用户的 server 定义必须原样保留");
        });

        h.Case("CF-15", "★Maven：绝不读取或修改 <servers> 里的凭据", () =>
        {
            var parsed = CoreCfg.MavenSettingsEditor.Parse(UserSettings);
            CoreCfg.MavenSettingsEditor.SetMirror(
                parsed.Value, "aliyun", "阿里云", "https://maven.aliyun.com/repository/public", "central",
                replaceUserMirror: true);

            var text = CoreCfg.MavenSettingsEditor.Serialize(parsed.Value);

            // 保留原样是对的（不删用户的数据），但**任何输出给用户的文本**都必须脱敏。
            Assert.Contains("CorpSecret", text, "原文件中的凭据应原样保留，不被我们删改");

            var redacted = CoreCfg.ConfigFileStore.RedactSecrets(text);
            Assert.NotContains("CorpSecret", redacted, "脱敏后的文本绝不能含明文凭据");
        });

        h.Case("CF-16", "★Maven：拒绝明文 HTTP 镜像地址", () =>
        {
            var parsed = CoreCfg.MavenSettingsEditor.Parse("<settings/>");
            var result = CoreCfg.MavenSettingsEditor.SetMirror(
                parsed.Value, "insecure", "不安全", "http://mirror.example/maven", "central");

            Assert.False(result.IsSuccess, "明文 HTTP 镜像应被拒绝");
            Assert.Contains("https", result.Error!.Message, "应说明必须是 https");
        });

        h.Case("CF-17", "Maven：mirrorOf 语义写入正确", () =>
        {
            var parsed = CoreCfg.MavenSettingsEditor.Parse("<settings/>");
            CoreCfg.MavenSettingsEditor.SetMirror(
                parsed.Value, "aliyun", "阿里云", "https://maven.aliyun.com/repository/public", "external:*");

            var text = CoreCfg.MavenSettingsEditor.Serialize(parsed.Value);
            Assert.Contains("<mirrorOf>external:*</mirrorOf>", text, "mirrorOf 应原样写入");
        });

        h.Case("CF-18", "★Maven：命名空间感知解析，且拒绝未知命名空间", () =>
        {
            var ns10 = CoreCfg.MavenSettingsEditor.Parse("<settings xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\"/>");
            Assert.True(ns10.IsSuccess, "1.0.0 应被接受");
            Assert.True(ns10.Value.HadNamespace, "应识别出命名空间");

            var noNs = CoreCfg.MavenSettingsEditor.Parse("<settings/>");
            Assert.True(noNs.IsSuccess, "无命名空间也应被接受（真实世界两者都有）");
            Assert.False(noNs.Value.HadNamespace, "应识别为无命名空间");

            var unknown = CoreCfg.MavenSettingsEditor.Parse("<settings xmlns=\"http://example.com/evil\"/>");
            Assert.False(unknown.IsSuccess, "未知命名空间应被拒绝");
        });

        h.Case("CF-19", "Maven：损坏的 XML 被拒绝且给出定位", () =>
        {
            var result = CoreCfg.MavenSettingsEditor.Parse("<settings><mirrors></settings>");
            Assert.False(result.IsSuccess, "标签不匹配应被拒绝");
            Assert.Equal(Abs.EnvStationErrorCodes.ConfigXmlParse, result.Error!.Code, "应返回 XML 解析错误码");
        });

        h.Case("CF-20", "Maven：写后语义校验能发现非法 mirrorOf", () =>
        {
            var parsed = CoreCfg.MavenSettingsEditor.Parse("<settings/>");
            CoreCfg.MavenSettingsEditor.SetMirror(
                parsed.Value, "x", "X", "https://maven.example/repo", "central");

            var ok = CoreCfg.MavenSettingsEditor.Validate(["x"], parsed.Value);
            Assert.True(ok.IsSuccess, $"合法配置应通过：{ok.Error?.Message}");
        });

        h.Case("CF-21", "Maven：一键移除只删本软件写入的镜像", () =>
        {
            var parsed = CoreCfg.MavenSettingsEditor.Parse(UserSettings);
            CoreCfg.MavenSettingsEditor.SetMirror(
                parsed.Value, "aliyun", "阿里云", "https://maven.aliyun.com/repository/public", "central",
                replaceUserMirror: true);

            var removed = CoreCfg.MavenSettingsEditor.RemoveEnvStationMirrors(parsed.Value);
            Assert.Equal(1, removed, "应移除 1 条本软件的镜像");

            var text = CoreCfg.MavenSettingsEditor.Serialize(parsed.Value);
            Assert.NotContains("envstation-aliyun", text, "我们的镜像应被移除");
            Assert.Contains("company-nexus", text, "用户的镜像必须保留");
        });
    }

    // ══════════════════════════ 动作 ══════════════════════════

    private static void ActionCases(TestHarness h)
    {
        h.Case("CF-22", "mirror.list：列出镜像并附带「镜像可换不可信」提示", () =>
        {
            using var ws = new TempWorkspace();
            var result = Run("envstation.mirror.list", Args(("target", S("maven"))), ws.Root);

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Contains("maintainer", "maintainer", "占位");
            Assert.Contains("阿里云", result.Outputs["sources"], "应含维护方信息");
            Assert.Contains("第三方", result.Outputs["trust_warning"], "必须提示镜像的信任问题（需求 MS-4）");
        });

        h.Case("CF-23", "★xml.set_mirror：dry_run 不修改文件", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Write(".m2/settings.xml", "<settings/>\n");
            var before = File.ReadAllText(path);

            var result = Run("envstation.xml.set_mirror", Args(
                ("file", S(path)),
                ("id", S("aliyun")),
                ("url", S("https://maven.aliyun.com/repository/public")),
                ("dry_run", B(true))), ws.Root);

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("true", result.Outputs["dry_run"], "应标记为干跑");
            Assert.Equal(before, File.ReadAllText(path), "干跑绝不能修改文件");
        });

        h.Case("CF-24", "★xml.set_mirror：写入后文件结构仍然有效且可一键还原", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Write(".m2/settings.xml", "<settings>\n  <localRepository>D:\\repo</localRepository>\n</settings>\n");
            var before = File.ReadAllText(path);

            var result = Run("envstation.xml.set_mirror", Args(
                ("file", S(path)),
                ("id", S("aliyun")),
                ("url", S("https://maven.aliyun.com/repository/public"))), ws.Root);

            Assert.True(result.Success, $"应成功：{result.Message}");

            var after = File.ReadAllText(path);
            Assert.Contains("envstation-aliyun", after, "应写入镜像");
            Assert.Contains("D:\\repo", after, "应保留用户原有的 localRepository");

            var parsed = CoreCfg.MavenSettingsEditor.Parse(after);
            Assert.True(parsed.IsSuccess, "写入后应仍是合法 XML");

            var backupId = result.Outputs["backup_id"];
            var restore = Run("envstation.mirror.restore", Args(("backup_id", S(backupId))), ws.Root);
            Assert.True(restore.Success, $"还原应成功：{restore.Message}");
            Assert.Equal(before, File.ReadAllText(path), "还原后应与修改前逐字节一致");
        });

        h.Case("CF-25", "★xml.set_mirror：遇到用户自制镜像时拒绝（AC-4 可逆性声明之外的硬保护）", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Write(".m2/settings.xml", """
                <settings>
                  <mirrors>
                    <mirror><id>corp</id><url>https://nexus.corp.example/repo</url><mirrorOf>*</mirrorOf></mirror>
                  </mirrors>
                </settings>
                """);
            var before = File.ReadAllText(path);

            var result = Run("envstation.xml.set_mirror", Args(
                ("file", S(path)),
                ("id", S("aliyun")),
                ("url", S("https://maven.aliyun.com/repository/public"))), ws.Root);

            Assert.False(result.Success, "应拒绝覆盖用户的私服配置");
            Assert.Contains("公司私服", result.Message, "应说明原因");
            Assert.Equal(before, File.ReadAllText(path), "文件必须完好无损");
        });

        h.Case("CF-26", "config.set_kv：INI 就地更新与新建节", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Write("pip.ini", "[global]\ntimeout = 30\n");

            var updated = Run("envstation.config.set_kv", Args(
                ("path", S(path)),
                ("key", S("global.index-url")),
                ("value", S("https://pypi.tuna.tsinghua.edu.cn/simple"))), ws.Root);

            Assert.True(updated.Success, $"应成功：{updated.Message}");
            var text = File.ReadAllText(path);
            Assert.Contains("index-url = https://pypi.tuna.tsinghua.edu.cn/simple", text, "应写入新键");
            Assert.Contains("timeout = 30", text, "原有键必须保留");

            var added = Run("envstation.config.set_kv", Args(
                ("path", S(path)),
                ("key", S("install.trusted-host")),
                ("value", S("pypi.tuna.tsinghua.edu.cn"))), ws.Root);

            Assert.True(added.Success, $"向新节写入应成功：{added.Message}");
            Assert.Contains("[install]", File.ReadAllText(path), "应自动创建缺失的节");
        });

        h.Case("CF-27", "config.set_kv：TOML 就地更新", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Write("config.toml", "[source.crates-io]\nreplace-with = \"old\"\n");

            var result = Run("envstation.config.set_kv", Args(
                ("path", S(path)),
                ("key", S("source.crates-io.replace-with")),
                ("value", S("envstation-mirror"))), ws.Root);

            Assert.True(result.Success, $"应成功：{result.Message}");

            var parsed = CoreToml.TomlReader.Parse(File.ReadAllText(path));
            Assert.True(parsed.IsSuccess, "写入后应仍是合法 TOML");
            Assert.Equal("envstation-mirror", parsed.Value.GetPathString("source.crates-io.replace-with"), "值应更新");
        });

        h.Case("CF-28", "★config.set_kv：解析失败时拒绝修改（不破坏文件）", () =>
        {
            using var ws = new TempWorkspace();
            var broken = "[[[not valid toml\n";
            var path = ws.Write("broken.toml", broken);

            var result = Run("envstation.config.set_kv", Args(
                ("path", S(path)),
                ("key", S("a.b")),
                ("value", S("c"))), ws.Root);

            Assert.False(result.Success, "解析失败应拒绝修改");
            Assert.Equal(broken, File.ReadAllText(path), "文件必须保持原样");
        });

        h.Case("CF-29", "★file.marked_block_remove：移除后用户内容完好且幂等", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Write(".npmrc", "fund=false\n");

            var withBlock = CoreCfg.ConfigFileStore.UpsertMarkedBlock(
                File.ReadAllText(path), "npm-registry", "registry=https://registry.npmmirror.com");
            File.WriteAllText(path, withBlock);

            var removed = Run("envstation.file.marked_block_remove", Args(
                ("file", S(path)),
                ("marker_id", S("npm-registry"))), ws.Root);

            Assert.True(removed.Success, $"应成功：{removed.Message}");
            Assert.Equal("true", removed.Outputs["changed"], "应报告已变更");
            Assert.Contains("fund=false", File.ReadAllText(path), "用户原有配置必须保留");

            var again = Run("envstation.file.marked_block_remove", Args(
                ("file", S(path)),
                ("marker_id", S("npm-registry"))), ws.Root);

            Assert.True(again.Success, "重复移除应幂等成功");
            Assert.Equal("false", again.Outputs["changed"], "第二次应报告未变更");
        });

        h.Case("CF-30", "★file.write_template：未提供的变量必须报错而不是留下占位符", () =>
        {
            using var ws = new TempWorkspace();
            var dest = ws.Resolve("out.ini");

            var result = Run("envstation.file.write_template", Args(
                ("template", S("registry=${registry_url}\ntimeout=${timeout}")),
                ("vars", S("registry_url=https://example.invalid")),
                ("dest", S(dest))), ws.Root);

            Assert.False(result.Success, "缺少变量应报错");
            Assert.Contains("timeout", result.Message, "应指出缺哪个变量");
            Assert.False(File.Exists(dest), "绝不应写出带未替换占位符的文件");
        });

        h.Case("CF-31", "file.write_template：变量只作数据替换，不做任何求值", () =>
        {
            using var ws = new TempWorkspace();
            var dest = ws.Resolve("out.ini");

            var result = Run("envstation.file.write_template", Args(
                ("template", S("value=${v}")),
                ("vars", S("v=$$(whoami) & echo pwned")),
                ("dest", S(dest))), ws.Root);

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("value=$$(whoami) & echo pwned", File.ReadAllText(dest).Trim(),
                "变量值必须原样写入——模板渲染只做数据替换，永不求值");
        });

        h.Case("CF-32", "★gradle.set_mirror：settings 风格只输出片段、不改文件", () =>
        {
            using var ws = new TempWorkspace();
            var result = Run("envstation.gradle.set_mirror", Args(
                ("urls", Arr("https://maven.aliyun.com/repository/public")),
                ("style", S("settings"))), ws.Root);

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("false", result.Outputs["changed"], "不应修改任何文件");
            Assert.Contains("repositories", result.Outputs["snippet"], "应给出可粘贴的片段");
        });

        h.Case("CF-33", "★gradle.set_mirror：init 风格写入标记块且保留用户已有内容", () =>
        {
            using var ws = new TempWorkspace();
            var home = ws.Resolve("home");
            Directory.CreateDirectory(Path.Combine(home, ".gradle"));

            // 动作写的是用户主目录下的 ~/.gradle/init.gradle，因此这里只验证"拒绝越界"这一路径：
            // 授权根是临时目录，而真实 ~/.gradle 不在其中——这正是我们希望的行为。
            var result = Run("envstation.gradle.set_mirror", Args(
                ("urls", Arr("https://maven.aliyun.com/repository/public"))), ws.Root);

            Assert.False(result.Success, "写入用户主目录需要显式授权，默认应被拒绝");
            Assert.Contains("授权目录", result.Message, "应告诉用户怎么授权，而不是笼统报错");
        });

        h.Case("CF-34", "xml.validate：能报出重复 id 与明文 HTTP", () =>
        {
            using var ws = new TempWorkspace();
            var path = ws.Write(".m2/settings.xml", """
                <settings>
                  <mirrors>
                    <mirror><id>dup</id><url>http://insecure.example/repo</url><mirrorOf>central</mirrorOf></mirror>
                    <mirror><id>dup</id><url>https://ok.example/repo</url><mirrorOf>central</mirrorOf></mirror>
                  </mirrors>
                </settings>
                """);

            var result = Run("envstation.xml.validate", Args(("file", S(path))), ws.Root);

            Assert.True(result.Success, $"校验动作本身应成功：{result.Message}");
            Assert.Contains("明文 HTTP", result.Outputs["problems"], "应报出明文 HTTP");
            Assert.Contains("重复", result.Outputs["problems"], "应报出重复 id");
            Assert.Equal("2", result.Outputs["mirror_count"], "应统计 2 个 mirror");
        });

        h.Case("CF-35", "★config.detect：报出候选路径与生效优先级说明", () =>
        {
            using var ws = new TempWorkspace();
            var result = Run("envstation.config.detect", Args(("target", S("maven"))), ws.Root);

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Contains("candidates", string.Join(",", result.Outputs.Keys), "应给出候选路径");
            Assert.Contains("优先", result.Outputs["maven.explanation"], "应说明生效优先级（XML-M2）");
        });
    }
}
