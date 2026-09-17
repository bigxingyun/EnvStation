using System.Collections.Immutable;
using EnvStation.TestKit;

namespace EnvStation.Tests.Scripting;

/// <summary>
/// ActionScript 标准层测试：TOML 解析器 / 包清单 / 工作流 / 受限表达式语言 / 语义化版本。
///
/// <para><b>为什么这批用例是"引入第三方包"这件事的安全底线：</b>
/// 需求 A1 承诺"第三方包永远无法执行任意命令"。这个承诺不是靠运行时拦截实现的，
/// 而是靠<b>标准层根本不存在表达任意命令的语法</b>。因此标准层的每一个放宽
/// （允许嵌套参数、允许动态动作 ID、允许变量文本参与表达式求值）都会直接变成
/// 一条新的攻击路径。本套用例逐条钉住这些"不允许"。</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("ActionScript 标准层测试");
        Console.WriteLine("（TOML 解析 · 包清单 · 工作流 · 受限表达式 · 语义化版本）");
        Console.WriteLine();

        var h = new TestHarness("ActionScript 标准层");

        TomlCases(h);
        ManifestCases(h);
        TrialCases(h);
        WorkflowCases(h);
        ExpressionCases(h);
        SemVerCases(h);

        var code = h.Summarize();

        var jsonPath = TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return code;
    }

    // ══════════════════════════════════ TOML ══════════════════════════════════

    /// <summary>取出失败结果的错误信息；若结果竟是成功则立刻判为用例失败。</summary>
    private static string ErrorOf<T>(Result<T> result)
    {
        Assert.True(result.IsFailure, "期望得到失败结果，但实际成功了。");
        return result.Error!.Message;
    }

    private static void TomlCases(TestHarness h)
    {
        h.Case("SC-01", "TOML 基本键值与注释", () =>
        {
            const string text = """
                # 这是注释
                spec_version = "1.0"   # 行尾注释
                count = 42
                ratio = 1.5
                enabled = true
                """;

            var r = CoreToml.TomlReader.Parse(text);
            Assert.True(r.IsSuccess, $"应解析成功，实际 {r.Error}");
            Assert.Equal("1.0", r.Value.GetString("spec_version"), "字符串值");
            Assert.Equal(42L, r.Value.GetInteger("count"), "整数值");
            Assert.Equal(true, r.Value.GetBoolean("enabled"), "布尔值");
        });

        h.Case("SC-02", "TOML 表头与点号键", () =>
        {
            const string text = """
                dotted.key.here = 7

                [requirements]
                os = ">=10.0.17763"
                disk_bytes = 1_500_000_000

                [a.b.c]
                deep = "yes"
                """;

            var r = CoreToml.TomlReader.Parse(text);
            Assert.True(r.IsSuccess, $"应解析成功，实际 {r.Error}");
            Assert.Equal(">=10.0.17763", r.Value.GetPathString("requirements.os"), "子表取值");
            Assert.Equal(1_500_000_000L, r.Value.GetTable("requirements")!.GetInteger("disk_bytes"), "下划线数字");
            Assert.Equal("yes", r.Value.GetPathString("a.b.c.deep"), "三层表");
            Assert.Equal(7L, (r.Value.GetPath("dotted.key.here") as CoreToml.TomlInteger)?.Value, "点号键");
        });

        h.Case("SC-03", "TOML 数组表 [[steps]]", () =>
        {
            const string text = """
                [[steps]]
                id = "first"
                uses = "envstation.detect.os@1.0.0"

                [steps.with]
                verbose = true

                [[steps]]
                id = "second"
                uses = "envstation.env.set@1.0.0"
                """;

            var r = CoreToml.TomlReader.Parse(text);
            Assert.True(r.IsSuccess, $"应解析成功，实际 {r.Error}");

            var steps = r.Value.GetArray("steps");
            Assert.NotNull(steps, "steps 应为数组");
            Assert.Equal(2, steps!.Count, "应有 2 个步骤");

            var first = (CoreToml.TomlTable)steps[0];
            Assert.Equal("first", first.GetString("id"), "第一个步骤 id");

            // [steps.with] 必须落在**最后一个** [[steps]] 元素上（TOML 规范行为）
            var with = first.GetTable("with");
            Assert.NotNull(with, "[steps.with] 应挂到第一个数组元素上");
            Assert.Equal(true, with!.GetBoolean("verbose"), "with 内容");

            var second = (CoreToml.TomlTable)steps[1];
            Assert.Equal("second", second.GetString("id"), "第二个步骤 id");
            Assert.Null(second.Get("with"), "第二个步骤不应继承 with");
        });

        h.Case("SC-04", "TOML 内联表与嵌套内联表", () =>
        {
            const string text = """
                author = { name = "张三", id = "zhangsan" }
                pin = { hash = "sha256:abc", extra = { level = 2 } }
                """;

            var r = CoreToml.TomlReader.Parse(text);
            Assert.True(r.IsSuccess, $"应解析成功，实际 {r.Error}");
            var author = r.Value.GetTable("author");
            Assert.NotNull(author, "author 应为表");
            Assert.Equal("张三", author!.GetString("name"), "内联表取值");
            Assert.Equal(2L, r.Value.GetTable("pin")!.GetTable("extra")!.GetInteger("level"), "嵌套内联表");
        });

        h.Case("SC-05", "TOML 字符串家族与转义", () =>
        {
            const string text = """"
                basic = "hello\nworld"
                literal = 'C:\Dev\Python'
                multi = """
                line1
                line2"""
                multiLiteral = '''
                raw\no\escape'''
                unicode = "\u4e2d\u6587"
                """";

            var r = CoreToml.TomlReader.Parse(text);
            Assert.True(r.IsSuccess, $"应解析成功，实际 {r.Error}");
            Assert.Equal("hello\nworld", r.Value.GetString("basic"), "基本字符串转义");
            Assert.Equal(@"C:\Dev\Python", r.Value.GetString("literal"), "字面量字符串不做转义");
            Assert.Equal("line1\nline2", r.Value.GetString("multi"), "多行字符串首行换行被剥离");
            Assert.Equal(@"raw\no\escape", r.Value.GetString("multiLiteral"), "多行字面量字符串");
            Assert.Equal("中文", r.Value.GetString("unicode"), "Unicode 转义");
        });

        // 上面那条用例的题面是 C# 原始字符串字面量，题面里的换行取决于源文件的行尾。
        // 这里显式喂 CRLF 与单独的 CR，把"换行必须归一为 \n"这条规范钉死——
        // 否则同一份测试在不同检出设置下结果不同（本题就是因此在 CRLF 工作区挂过）。
        h.Case("SC-05b", "TOML 多行字符串：CRLF 与 CR 的换行必须归一为 \\n", () =>
        {
            const string crlf = "multi = \"\"\"\r\nline1\r\nline2\"\"\"\r\nliteral = '''\r\nraw1\r\nraw2'''\r\n";
            const string cr = "multi = \"\"\"\rline1\rline2\"\"\"\rliteral = '''\rraw1\rraw2'''\r";

            foreach (var (text, label) in new[] { (crlf, "CRLF"), (cr, "CR") })
            {
                var r = CoreToml.TomlReader.Parse(text);
                Assert.True(r.IsSuccess, $"{label}：应解析成功，实际 {r.Error}");
                Assert.Equal("line1\nline2", r.Value.GetString("multi"), $"{label}：多行基本字符串换行归一");
                Assert.Equal("raw1\nraw2", r.Value.GetString("literal"), $"{label}：多行字面量字符串换行归一");
            }
        });

        h.Case("SC-06", "TOML 数值家族", () =>
        {
            const string text = """
                dec = 1_000
                hex = 0xFF
                oct = 0o17
                bin = 0b1010
                neg = -42
                f1 = 3.14
                f2 = 1e3
                inf = inf
                """;

            var r = CoreToml.TomlReader.Parse(text);
            Assert.True(r.IsSuccess, $"应解析成功，实际 {r.Error}");
            Assert.Equal(1000L, r.Value.GetInteger("dec"), "下划线分隔");
            Assert.Equal(255L, r.Value.GetInteger("hex"), "十六进制");
            Assert.Equal(15L, r.Value.GetInteger("oct"), "八进制");
            Assert.Equal(10L, r.Value.GetInteger("bin"), "二进制");
            Assert.Equal(-42L, r.Value.GetInteger("neg"), "负数");
            Assert.Equal(3.14, (r.Value.Get("f1") as CoreToml.TomlFloat)?.Value, "浮点数");
            Assert.Equal(1000.0, (r.Value.Get("f2") as CoreToml.TomlFloat)?.Value, "科学计数法");
            Assert.Equal(double.PositiveInfinity, (r.Value.Get("inf") as CoreToml.TomlFloat)?.Value, "无穷大");
        });

        h.Case("SC-07", "TOML 数组：跨行、尾逗号、异构", () =>
        {
            const string text = """
                hosts = [
                  "a.com",
                  "b.com",   # 允许尾逗号
                ]
                mixed = [1, "two", true]
                nested = [[1, 2], [3]]
                """;

            var r = CoreToml.TomlReader.Parse(text);
            Assert.True(r.IsSuccess, $"应解析成功，实际 {r.Error}");
            Assert.Equal(2, r.Value.GetArray("hosts")!.Count, "跨行数组");
            Assert.Equal(3, r.Value.GetArray("mixed")!.Count, "TOML 1.0 允许异构数组");
            Assert.Equal(2, r.Value.GetArray("nested")!.Count, "嵌套数组");
        });

        h.Case("SC-08", "TOML 日期时间按不透明字符串保留", () =>
        {
            const string text = """
                offset = 1979-05-27T07:32:00Z
                local = 1979-05-27 07:32:00
                """;

            var r = CoreToml.TomlReader.Parse(text);
            Assert.True(r.IsSuccess, $"应解析成功，实际 {r.Error}");
            Assert.Equal("1979-05-27T07:32:00Z", (r.Value.Get("offset") as CoreToml.TomlDateTime)?.Raw, "带时区");
            Assert.Equal("1979-05-27 07:32:00", (r.Value.Get("local") as CoreToml.TomlDateTime)?.Raw, "本地时间");
        });

        h.Case("SC-09", "TOML 错误：键重复必须报错", () =>
        {
            var r = CoreToml.TomlReader.Parse("a = 1\na = 2\n");
            Assert.True(r.IsFailure, "键重复应判失败");
            Assert.Contains("重复定义", ErrorOf(r), "错误信息应说明原因");
            Assert.Contains("第 2 行", ErrorOf(r), "错误信息应带行号");
        });

        h.Case("SC-10", "TOML 错误：表重复定义", () =>
        {
            var r = CoreToml.TomlReader.Parse("[t]\na = 1\n[t]\nb = 2\n");
            Assert.True(r.IsFailure, "表重复定义应判失败");
            Assert.Contains("重复定义", ErrorOf(r), "错误信息应说明原因");
        });

        h.Case("SC-11", "TOML 错误：字符串未闭合", () =>
        {
            var r = CoreToml.TomlReader.Parse("a = \"abc\n");
            Assert.True(r.IsFailure, "未闭合字符串应判失败");
            Assert.Contains("不能跨行", ErrorOf(r), "应提示基本字符串不能跨行");
        });

        h.Case("SC-12", "TOML 错误：数组未闭合", () =>
        {
            var r = CoreToml.TomlReader.Parse("a = [1, 2\n");
            Assert.True(r.IsFailure, "未闭合数组应判失败");
        });

        h.Case("SC-13", "TOML 错误：非法裸键", () =>
        {
            var r = CoreToml.TomlReader.Parse("!bad = 1\n");
            Assert.True(r.IsFailure, "非法裸键应判失败");
            Assert.Contains("键名包含非法字符", ErrorOf(r), "应给出明确原因");
        });

        h.Case("SC-14", "TOML 错误：无法识别的值", () =>
        {
            var r = CoreToml.TomlReader.Parse("a = @nonsense\n");
            Assert.True(r.IsFailure, "非法值应判失败");
            Assert.Contains("无法识别的值", ErrorOf(r), "应给出明确原因");
        });

        h.Case("SC-15", "TOML 错误：不支持的转义序列", () =>
        {
            var r = CoreToml.TomlReader.Parse("a = \"\\q\"\n");
            Assert.True(r.IsFailure, "非法转义应判失败");
            Assert.Contains("不支持的转义序列", ErrorOf(r), "应列出允许的转义");
        });
    }

    // ══════════════════════════════════ 包清单 ══════════════════════════════════

    private const string ValidManifest = """
        spec_version = "1.0"
        id           = "x-zhangsan.python-env"
        version      = "1.2.0"
        name         = "Python 3.12 环境一键配置"
        description  = "安装 Python 3.12、配置国内镜像源"
        author       = { name = "张三", id = "zhangsan" }
        license      = "MIT"
        tier         = "T0"

        [requirements]
        os          = ">=10.0.17763"
        arch        = ["x64", "arm64"]
        disk_bytes  = 1_500_000_000

        [runtime]
        kind        = "python"
        version     = ">=3.12"

        [permissions]
        "CAP.INSPECT"      = "检测已安装的 Python 与系统信息"
        "CAP.NET.DOWNLOAD" = "从 python.org 与 mirrors.aliyun.com 下载安装包"

        [network]
        allow = ["www.python.org", "mirrors.aliyun.com"]

        [side_effects]
        writes_env      = true
        writes_files    = true
        modifies_config = ["pip.ini"]
        irreversible    = false
        reversible_by   = "内置回滚（快照 + 文件备份）"

        [quality]
        has_tests     = true
        has_uninstall = true
        readme        = "README.md"
        """;

    // ══════════════════════════════════ V4 沙箱试运行 ══════════════════════════════════

    /// <summary>只读探测的工作流（不触碰任何东西）。</summary>
    private const string InspectWorkflow = """
        spec_version = "1.0"

        [[steps]]
        id   = "os"
        uses = "envstation.detect.os@1.0.0"
        register = "os"
        """;

    /// <summary>会写环境变量的工作流——用于验证"副作用声明是否覆盖实际行为"。</summary>
    private const string WriteEnvWorkflow = """
        spec_version = "1.0"

        [[steps]]
        id   = "set"
        uses = "envstation.env.set@1.0.0"
        [steps.with]
        scope = "user"
        name  = "ENVSTATION_V4_PROBE"
        value = "1"
        """;

    private static void TrialCases(TestHarness h)
    {
        h.Case("SC-58", "V4：声明与工作流一致时通过，且能力清单来自真实账本", () =>
        {
            var result = RunTrial(ValidManifest, InspectWorkflow);

            Assert.False(result.Report.HasBlockers,
                $"声明与工作流一致时不应有阻断项，实际：{result.Report.ToText()}");
            Assert.True(result.ExercisedCapabilities.Contains("CAP.INSPECT"),
                $"只读探测动作实际用到了 CAP.INSPECT，账本必须记下来；试运行结果：{result.Outcome.Message}（执行 {result.Outcome.ExecutedSteps} 步，跳过 {result.Outcome.SkippedSteps} 步）");
            Assert.Equal(0, result.UndeclaredCapabilities.Length, "不应有未声明的能力");
            TryClean(result.SandboxRoot);
        });

        h.Case("SC-59", "★V4：工作流用到未声明的能力 → 阻断（声明不完整）", () =>
        {
            // 清单只声明 CAP.INSPECT，工作流却去写环境变量——这正是"作者改了工作流忘了改清单"
            // 的真实形态：权限墙按清单渲染，用户看不到也不需要同意那项能力。
            var manifest = ValidManifest.Replace(
                "        \"CAP.NET.DOWNLOAD\" = \"从 python.org 与 mirrors.aliyun.com 下载安装包\"\n",
                string.Empty,
                StringComparison.Ordinal);

            var result = RunTrial(manifest, WriteEnvWorkflow);

            Assert.True(result.Report.HasBlockers,
                $"用了未声明的能力必须阻断；实际用到：{string.Join("、", result.ExercisedCapabilities)}；试运行：{result.Outcome.Message}；发现：{result.Report.ToText()}");
            Assert.True(result.UndeclaredCapabilities.Contains("CAP.ENV.USER"),
                $"应识别出 CAP.ENV.USER 未声明，实际：{string.Join("、", result.UndeclaredCapabilities)}");
            Assert.Contains("V4-01", result.Report.ToText(), "应给出 V4-01 编号");
            TryClean(result.SandboxRoot);
        });

        h.Case("SC-60", "V4：声明了但没走到 → 警告而非阻断（多授权比少授权安全）", () =>
        {
            // ValidManifest 声明了 CAP.NET.DOWNLOAD，但这个工作流只做只读探测。
            var result = RunTrial(ValidManifest, InspectWorkflow);

            Assert.False(result.Report.HasBlockers, "多声明不应阻断导入");
            Assert.True(result.UnusedCapabilities.Contains("CAP.NET.DOWNLOAD"),
                $"应指出 CAP.NET.DOWNLOAD 本次未用到，实际：{string.Join("、", result.UnusedCapabilities)}");
            Assert.Contains("V4-02", result.Report.ToText(), "应给出 V4-02 编号");
            TryClean(result.SandboxRoot);
        });

        h.Case("SC-61", "★V4：副作用声明与实际不符 → 阻断（用户是照着声明做的决定）", () =>
        {
            // 声明"不写环境变量"，但工作流调用了会写环境变量的动作。
            // 这里必须把 CAP.ENV.USER 声明出来，否则动作会因为未授权而被拒绝，
            // 测到的就变成 V4-01 而不是本条要验的副作用声明偏差。
            var manifest = ValidManifest
                .Replace(
                    "        \"CAP.NET.DOWNLOAD\" = \"从 python.org 与 mirrors.aliyun.com 下载安装包\"\n",
                    "        \"CAP.ENV.USER\" = \"写入用户级环境变量\"\n",
                    StringComparison.Ordinal)
                .Replace("writes_env      = true", "writes_env      = false", StringComparison.Ordinal);

            var result = RunTrial(manifest, WriteEnvWorkflow);

            Assert.True(result.Report.HasBlockers,
                $"副作用声明与实际不符必须阻断；实际用到：{string.Join("、", result.ExercisedCapabilities)}；试运行：{result.Outcome.Message}；发现：{result.Report.ToText()}");
            Assert.Contains("V4-04", result.Report.ToText(), "应给出 V4-04 编号");
            TryClean(result.SandboxRoot);
        });

        h.Case("SC-62", "V4：试运行不注入网络、不注入可写环境（结构上没有副作用通道）", () =>
        {
            // 这条守的是 V4 自身的安全性：跑别人分享的包做试运行，绝不能真的改到本机。
            var result = RunTrial(ValidManifest, InspectWorkflow);

            Assert.True(Directory.Exists(result.SandboxRoot), "应建立独立的沙箱目录");
            Assert.Equal(0, result.TouchedPaths.Length, "只读探测不应触碰任何路径");
            TryClean(result.SandboxRoot);
        });
    }

    /// <summary>
    /// 给工作流里的每个 <c>uses</c> 补上 <c>pin.hash</c>。
    /// </summary>
    /// <remarks>
    /// 需求 S3 要求每个动作引用都携带契约哈希；真实工具链里这件事由 <c>envstation pack</c> 完成
    /// （需求 M13-1/M13-3），测试里等价地由注册表补。刻意不绕过 pin 校验——
    /// 如果为了测试方便就放宽解析，那这条安全约束在测试里就形同不存在。
    /// </remarks>
    private static string StampPins(string toml)
    {
        var registry = CoreActions.ActionRegistry.CreateDefault(new AbsDiag.FindingBag()).Value;
        var lines = toml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(lines.Length + 8);

        foreach (var line in lines)
        {
            output.Add(line);

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("uses = ", StringComparison.Ordinal))
            {
                continue;
            }

            var indent = line[..(line.Length - trimmed.Length)];
            var value = trimmed["uses = ".Length..].Trim().Trim('"');
            var at = value.LastIndexOf('@');
            if (at <= 0)
            {
                continue;
            }

            var hash = registry.GetContractHash(value[..at], value[(at + 1)..]);
            Assert.NotNull(hash, $"动作 {value} 应已登记，否则测试数据本身有问题");
            output.Add($"{indent}pin = {{ hash = \"{hash}\" }}");
        }

        return string.Join('\n', output);
    }

    /// <summary>跑一次 V4 试运行（用真实的动作注册表）。</summary>
    private static CorePkg.SandboxTrialResult RunTrial(string manifestToml, string workflowToml)
    {
        var bag = new AbsDiag.FindingBag();

        var manifest = CorePkg.PackageManifestReader.Read(manifestToml, bag);
        Assert.True(manifest.IsSuccess, $"清单应能解析：{bag.ToReport().ToText()}");

        var workflow = CorePkg.WorkflowReader.Read(StampPins(workflowToml), bag);
        Assert.True(workflow.IsSuccess, $"工作流应能解析：{bag.ToReport().ToText()}");

        var registry = CoreActions.ActionRegistry.CreateDefault(bag);
        Assert.True(registry.IsSuccess, $"动作注册表应能建立：{bag.ToReport().ToText()}");

        var sandbox = Path.Combine(Path.GetTempPath(), "envstation-sc-v4-" + Guid.NewGuid().ToString("N")[..8]);

        // 沙箱目录刻意不在返回前删掉：SC-62 要断言它真的被建出来了。
        // 由用到它的用例自行清理（TryClean）。
        return CorePkg.SandboxTrial
            .RunAsync(manifest.Value, workflow.Value, registry.Value, sandbox)
            .GetAwaiter().GetResult();
    }

    /// <summary>清理沙箱目录（失败不影响结论）。</summary>
    private static void TryClean(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响用例结论。
        }
    }

    private static void ManifestCases(TestHarness h)
    {
        h.Case("SC-16", "合法清单完整解析", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(ValidManifest, bag);
            Assert.True(r.IsSuccess, $"应解析成功，实际发现：{bag.ToReport().ToText()}");

            var m = r.Value;
            Assert.Equal("x-zhangsan.python-env", m.Id, "包 ID");
            Assert.Equal("T0", m.Tier, "档位");
            Assert.Equal("张三", m.Author!.Name, "作者");
            Assert.Equal(2, m.AllowedHosts.Length, "域名白名单数量");
            Assert.Equal(1_500_000_000L, m.Requirements!.DiskBytes, "磁盘需求");
            Assert.Equal(2, m.DeclaredCapabilities().Count, "声明的能力数量");
            Assert.False(bag.HasBlockers, "不应有阻断问题");
        });

        h.Case("SC-17", "清单缺少必需字段 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read("id = \"x-a.b\"\n", bag);
            Assert.True(r.IsFailure, "缺字段应判失败");
            Assert.True(bag.BlockCount >= 4, $"应报出多个缺失字段（一次报全），实际 {bag.BlockCount}");
        });

        h.Case("SC-18", "声明未知能力 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("\"CAP.INSPECT\"      = \"检测已安装的 Python 与系统信息\"",
                    "\"CAP.REGISTRY.WRITE\" = \"随意写注册表\"", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "未知能力应判失败");
            Assert.Contains("不是官方定义的能力", bag.ToReport().ToText(), "应说明能力表由官方封闭维护");
            Assert.Contains(Abs.EnvStationErrorCodes.PackageUnknownCapability, bag.ToReport().ToText(), "应使用专用错误码");
        });

        h.Case("SC-19", "包 ID 未使用合法命名空间 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("x-zhangsan.python-env", "someone.python-env", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "命名空间违规应判失败");
            Assert.Contains("未使用合法前缀", bag.ToReport().ToText(), "应给出命名规范提示");
        });

        h.Case("SC-20", "声明下载能力但无域名白名单 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("allow = [\"www.python.org\", \"mirrors.aliyun.com\"]", "allow = []", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "无白名单的下载能力应判失败");
            Assert.Contains("域名白名单", bag.ToReport().ToText(), "应说明必须声明域名");
        });

        h.Case("SC-21", "域名白名单含通配符 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("\"mirrors.aliyun.com\"", "\"*.aliyun.com\"", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "通配符域名应判失败");
            Assert.Contains("不允许通配符", bag.ToReport().ToText(), "应说明通配符使白名单失效");
        });

        h.Case("SC-22", "域名位置写成 URL → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("\"www.python.org\"", "\"https://www.python.org/downloads\"", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "URL 形式的白名单应判失败");
            Assert.Contains("看起来是 URL", bag.ToReport().ToText(), "应提示只写主机名");
        });

        h.Case("SC-23", "spec_version 高于客户端 → 警告而非阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("spec_version = \"1.0\"", "spec_version = \"9.0\"", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsSuccess, $"新标准版本应允许导入（仅警告），实际：{bag.ToReport().ToText()}");
            Assert.True(bag.WarnCount >= 1, "应给出警告");
            Assert.False(bag.HasBlockers, "不应阻断");
        });

        h.Case("SC-24", "未知 CPU 架构 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("[\"x64\", \"arm64\"]", "[\"x64\", \"mips\"]", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "未知架构应判失败");
            Assert.Contains("未知的 CPU 架构", bag.ToReport().ToText(), "应说明支持列表");
        });

        h.Case("SC-25", "未知能力档位 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("tier         = \"T0\"", "tier         = \"T9\"", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "未知档位应判失败");
            Assert.Contains("未知的能力档位", bag.ToReport().ToText(), "应说明支持的档位");
        });

        h.Case("SC-26", "能力缺少自然语言说明 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("\"CAP.INSPECT\"      = \"检测已安装的 Python 与系统信息\"", "\"CAP.INSPECT\"      = \"\"", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "空说明应判失败");
            Assert.Contains("能力说明缺失", bag.ToReport().ToText(), "应引用需求 IMP-2");
        });

        h.Case("SC-27", "readme 路径越界 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("readme        = \"README.md\"", "readme        = \"../../secret.md\"", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "越界 readme 应判失败");
            Assert.Contains("越界", bag.ToReport().ToText(), "应说明必须是包内相对路径");
        });

        h.Case("SC-28", "包版本不是语义化版本 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.PackageManifestReader.Read(
                ValidManifest.Replace("version      = \"1.2.0\"", "version      = \"latest\"", StringComparison.Ordinal),
                bag);
            Assert.True(r.IsFailure, "非语义化版本应判失败");
            Assert.Contains("语义化版本", bag.ToReport().ToText(), "应说明格式要求");
        });
    }

    // ══════════════════════════════════ 工作流 ══════════════════════════════════

    private static void WorkflowCases(TestHarness h)
    {
        h.Case("SC-29", "合法工作流解析与展开", () =>
        {
            const string text = """
                spec_version = "1.0"
                default_on_error = "rollback"

                [[steps]]
                id = "probe"
                uses = "envstation.detect.runtime@1.0.0"
                register = "py"
                [steps.with]
                kind = "python"

                [[steps]]
                id = "branch"
                if = "${py.found} == false"
                [[steps.then]]
                uses = "envstation.pkg.install@1.0.0"
                on_error = "continue"
                [steps.then.with]
                manager = "winget"
                package = "Python.Python.3.12"
                [[steps.else]]
                uses = "envstation.env.get@1.0.0"

                [[steps]]
                id = "loop"
                foreach = "${profile.mirrors}"
                as = "m"
                max = 5
                [[steps.do]]
                uses = "envstation.mirror.set@1.0.0"
                """;

            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read(text, bag);
            Assert.True(r.IsSuccess, $"应解析成功，实际：{bag.ToReport().ToText()}");

            var doc = r.Value;
            Assert.Equal(3, doc.Steps.Length, "顶层步骤数");
            Assert.Equal(AbsPkg.ErrorPolicy.Rollback, doc.DefaultErrorPolicy, "默认失败策略");

            // 展开后应包含所有嵌套步骤：3 顶层 + 1 then + 1 else + 1 do = 6
            Assert.Equal(6, doc.Flatten().Count(), "展开后的步骤总数");

            var actions = doc.ReferencedActions();
            Assert.Equal(4, actions.Length, $"去重后的动作数量，实际 {string.Join(",", actions.Select(a => a.ActionId))}");
            Assert.Equal("envstation.detect.runtime", actions[0].ActionId, "动作按 ID 排序");
            Assert.False(bag.HasBlockers, "不应有阻断问题");
        });

        h.Case("SC-30", "动作引用缺少版本 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read("[[steps]]\nuses = \"envstation.env.set\"\n", bag);
            Assert.True(r.IsFailure, "缺版本应判失败");
            Assert.Contains("缺少版本", bag.ToReport().ToText(), "应说明格式");
        });

        h.Case("SC-31", "动作引用使用浮动版本 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read("[[steps]]\nuses = \"envstation.env.set@latest\"\n", bag);
            Assert.True(r.IsFailure, "浮动版本应判失败");
            Assert.Contains("浮动版本", bag.ToReport().ToText(), "应引用需求 S2");
        });

        h.Case("SC-32", "动作 ID 中动态构造 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read("[[steps]]\nuses = \"envstation.env.${op}@1.0.0\"\n", bag);
            Assert.True(r.IsFailure, "动态动作 ID 应判失败");
            Assert.Contains("变量插值", bag.ToReport().ToText(), "应引用需求 S4");
        });

        h.Case("SC-33", "动作 ID 未使用合法命名空间 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read("[[steps]]\nuses = \"evil.env.set@1.0.0\"\n", bag);
            Assert.True(r.IsFailure, "非法命名空间应判失败");
            Assert.Contains("未使用合法命名空间", bag.ToReport().ToText(), "应说明前缀规则");
        });

        h.Case("SC-34", "官方动作 ID 段数不足 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read("[[steps]]\nuses = \"envstation.set@1.0.0\"\n", bag);
            Assert.True(r.IsFailure, "非三段式官方 ID 应判失败");
            Assert.Contains("三段式", bag.ToReport().ToText(), "应说明三段式规则");
        });

        h.Case("SC-35", "步骤未声明任何动作 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read("[[steps]]\nid = \"empty\"\n", bag);
            Assert.True(r.IsFailure, "空步骤应判失败");
            Assert.Contains("没有声明任何动作", bag.ToReport().ToText(), "应说明合法动作关键字");
        });

        h.Case("SC-36", "步骤混用多种控制结构 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read(
                "[[steps]]\nuses = \"envstation.detect.os@1.0.0\"\nif = \"true\"\n[[steps.then]]\nuses = \"envstation.detect.os@1.0.0\"\n",
                bag);
            Assert.True(r.IsFailure, "混用控制结构应判失败");
            Assert.Contains("混用了多种控制结构", bag.ToReport().ToText(), "应说明一步只做一件事");
        });

        h.Case("SC-37", "foreach 上限超过硬上限 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read(
                "[[steps]]\nforeach = \"${xs}\"\nas = \"x\"\nmax = 5000\n[[steps.do]]\nuses = \"envstation.detect.os@1.0.0\"\n",
                bag);
            Assert.True(r.IsFailure, "超限循环应判失败");
            Assert.Contains("必须在 1 到 1000 之间", bag.ToReport().ToText(), "应说明硬上限 1000");
        });

        h.Case("SC-38", "动作参数中出现嵌套表 → 阻断", () =>
        {
            const string text = """
                [[steps]]
                uses = "envstation.env.set@1.0.0"
                [steps.with]
                scope = "user"
                [steps.with.options]
                deep = true
                """;

            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read(text, bag);
            Assert.True(r.IsFailure, "嵌套参数应判失败");
            Assert.Contains("嵌套表", bag.ToReport().ToText(), "应说明参数必须扁平");
        });

        h.Case("SC-39", "pin 声明但缺少 hash → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read(
                "[[steps]]\nuses = \"envstation.env.set@1.0.0\"\npin = { }\n",
                bag);
            Assert.True(r.IsFailure, "缺 hash 的 pin 应判失败");
            Assert.Contains("pin 缺少 hash", bag.ToReport().ToText(), "应说明 pin.hash 格式");
        });

        h.Case("SC-40", "on_error 取值非法 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read(
                "[[steps]]\nuses = \"envstation.detect.os@1.0.0\"\non_error = \"explode\"\n",
                bag);
            Assert.True(r.IsFailure, "非法失败策略应判失败");
            Assert.Contains("on_error 取值非法", bag.ToReport().ToText(), "应列出合法取值");
        });

        h.Case("SC-41", "timeout 超出范围 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read(
                "[[steps]]\nuses = \"envstation.detect.os@1.0.0\"\ntimeout = 99999\n",
                bag);
            Assert.True(r.IsFailure, "超范围超时应判失败");
            Assert.Contains("timeout 超出范围", bag.ToReport().ToText(), "应说明上限");
        });

        h.Case("SC-42", "未知字段给出警告而非失败", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read(
                "[[steps]]\nuses = \"envstation.detect.os@1.0.0\"\nfuture_field = 1\n",
                bag);
            Assert.True(r.IsSuccess, $"未知字段应仅警告（需求 STD-3），实际：{bag.ToReport().ToText()}");
            Assert.True(bag.WarnCount >= 1, "应给出警告");
        });

        h.Case("SC-43", "工作流缺少步骤 → 阻断", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CorePkg.WorkflowReader.Read("spec_version = \"1.0\"\n", bag);
            Assert.True(r.IsFailure, "无步骤应判失败");
            Assert.Contains("没有任何步骤", bag.ToReport().ToText(), "应给出可操作建议");
        });
    }

    // ══════════════════════════════════ 表达式 ══════════════════════════════════

    /// <summary>测试用变量表。</summary>
    private sealed class Vars(Dictionary<string, CoreScript.ExprValue> map) : CoreScript.IVariableResolver
    {
        public CoreScript.ExprValue Resolve(string qualifiedName) =>
            map.TryGetValue(qualifiedName, out var v) ? v : CoreScript.ExprValue.Missing;
    }

    private static Vars MakeVars() => new(new Dictionary<string, CoreScript.ExprValue>(StringComparer.Ordinal)
    {
        ["py.found"] = CoreScript.ExprValue.FromBoolean(false),
        ["py.version"] = CoreScript.ExprValue.FromString("3.12.1"),
        ["py.count"] = CoreScript.ExprValue.FromNumber(3),
        ["user.name"] = CoreScript.ExprValue.FromString("张三"),
        ["evil.text"] = CoreScript.ExprValue.FromString("x\" or true or \""),
        ["path.install"] = CoreScript.ExprValue.FromString(@"D:\Dev\Python"),
    });

    private static bool Eval(string expr) => Eval(expr, MakeVars());

    private static bool Eval(string expr, CoreScript.IVariableResolver vars)
    {
        var r = CoreScript.ExpressionEvaluator.EvaluateCondition(expr, vars);
        Assert.True(r.IsSuccess, $"表达式 {expr} 应求值成功，实际：{r.Error}");
        return r.Value;
    }

    private static void ExpressionCases(TestHarness h)
    {
        h.Case("SC-44", "表达式：比较与逻辑", () =>
        {
            Assert.True(Eval("${py.found} == false"), "布尔相等（false == false 应为真）");
            Assert.True(Eval("${py.found} != true"), "布尔不等");
            Assert.True(Eval("${py.count} > 2 and ${py.count} < 5"), "数值区间");
            Assert.True(Eval("not ${py.found}"), "逻辑非");
            Assert.True(Eval("${py.found} or ${py.count} == 3"), "逻辑或");
            Assert.True(Eval("(${py.count} == 4) or ${py.count} == 3"), "括号优先级");
            Assert.False(Eval("${py.count} >= 5"), "数值比较否定");
        });

        h.Case("SC-45", "表达式：字符串比较与内置函数", () =>
        {
            Assert.True(Eval("${user.name} == \"张三\""), "字符串相等");
            Assert.True(Eval("contains(${py.version}, \"3.12\")"), "contains");
            Assert.False(Eval("contains(${py.version}, \"3.11\")"), "contains 否定");
            Assert.True(Eval("len(${py.version}) == 6"), "len");
            Assert.True(Eval("matches(${py.version}, \"^3\\\\.1[0-9]\")"), "matches");
            Assert.True(Eval("semver_gt(${py.version}, \"3.10.0\")"), "semver_gt");
            Assert.True(Eval("semver_satisfies(${py.version}, \">=3.10,<4\")"), "semver_satisfies");
            Assert.True(Eval("starts_with(lower(${user.name}), \"张\")"), "嵌套函数");
            Assert.True(Eval("exists(${py.version})"), "exists 为真");
            Assert.False(Eval("exists(${nope.missing})"), "exists 为假");
        });

        h.Case("SC-46", "表达式：路径拼接", () =>
        {
            Assert.True(
                Eval("path_join(${path.install}, \"Scripts\") == \"D:\\\\Dev\\\\Python\\\\Scripts\""),
                "path_join 应使用平台分隔符拼接");
        });

        h.Case("SC-47", "★安全：变量内容无法注入表达式代码", () =>
        {
            // 关键用例：如果把变量做"文本替换再解析"，evil.text 的值
            //   x" or true or "
            // 会把表达式变成 `"x" or true or "" == "x" or true or ""` —— 恒为真，条件被绕过。
            // 本实现把 ${...} 作为语法节点，变量值只能是"值"，无法成为"代码"。
            var vars = MakeVars();
            var r = CoreScript.ExpressionEvaluator.EvaluateCondition("${evil.text} == \"never\"", vars);
            Assert.True(r.IsSuccess, $"应求值成功，实际：{r.Error}");
            Assert.False(r.Value, "注入文本必须只被当作普通字符串比较，不能改变表达式结构");
        });

        h.Case("SC-48", "★安全：裸标识符必须报错（防变量名被当关键字）", () =>
        {
            var r = CoreScript.ExpressionEvaluator.Compile("py.found == false");
            Assert.True(r.IsFailure, "裸标识符应判失败");
            Assert.Contains("变量必须写成", ErrorOf(r), "应提示正确写法");
        });

        h.Case("SC-49", "★安全：单等号赋值必须被拒绝", () =>
        {
            var r = CoreScript.ExpressionEvaluator.Compile("${py.count} = 3");
            Assert.True(r.IsFailure, "单等号应判失败");
            Assert.Contains("没有赋值能力", ErrorOf(r), "应说明表达式语言无赋值");
        });

        h.Case("SC-50", "★安全：未登记函数必须被拒绝", () =>
        {
            var r = CoreScript.ExpressionEvaluator.Compile("exec(\"calc\")");
            Assert.True(r.IsFailure, "未知函数应判失败");
            Assert.Contains("只允许这些内置纯函数", ErrorOf(r), "应列出白名单");
        });

        h.Case("SC-51", "★安全：字符串不能直接作为条件", () =>
        {
            var r = CoreScript.ExpressionEvaluator.EvaluateCondition("${user.name}", MakeVars());
            Assert.True(r.IsFailure, "字符串直接作条件应判失败");
            Assert.Contains("不能直接作为条件", ErrorOf(r), "应说明必须显式比较");
        });

        h.Case("SC-52", "缺失变量参与比较 → 失败而非静默 false", () =>
        {
            var r = CoreScript.ExpressionEvaluator.EvaluateCondition("${nope.missing} == \"x\"", MakeVars());
            Assert.True(r.IsFailure, "缺失变量应判失败");
            Assert.Contains("未定义的变量", ErrorOf(r), "应指出变量未定义");
        });

        h.Case("SC-53", "表达式：超长与非法模式被安全拒绝", () =>
        {
            var longExpr = new string('a', CoreScript.ExpressionEvaluator.MaxExpressionLength + 1);
            Assert.True(CoreScript.ExpressionEvaluator.Compile(longExpr).IsFailure, "超长表达式应判失败");

            // 非法正则 → 结果为"未知" → 整体判失败，而不是静默地当成"不匹配"
            var r = CoreScript.ExpressionEvaluator.EvaluateCondition("matches(${py.version}, \"[unclosed\")", MakeVars());
            Assert.True(r.IsFailure, "非法正则模式应判失败而不是返回 false");
        });

        h.Case("SC-54", "表达式：短路求值", () =>
        {
            // 左侧已为假时不应触碰右侧（右侧引用缺失变量；若求值会失败）
            Assert.True(Eval("${py.found} == false or ${nope.missing} == \"x\""), "or 左侧为真应短路");
        });
    }

    // ══════════════════════════════════ 语义化版本 ══════════════════════════════════

    private static void SemVerCases(TestHarness h)
    {
        h.Case("SC-55", "语义化版本解析与比较", () =>
        {
            Assert.True(AbsPkg.SemanticVersion.TryParse("1.2.3", out var v), "应解析 1.2.3");
            Assert.Equal(1, v.Major, "主版本");
            Assert.Equal(2, v.Minor, "次版本");
            Assert.Equal(3, v.Patch, "修订号");

            Assert.True(AbsPkg.SemanticVersion.TryParse("3.12", out var short3), "缺少修订号应视为 0");
            Assert.Equal(0, short3.Patch, "补齐修订号");

            Assert.True(AbsPkg.SemanticVersion.TryParse("1.2.3+build.5", out var withBuild), "应忽略构建元数据");
            Assert.True(withBuild == v, "构建元数据不参与比较");

            Assert.False(AbsPkg.SemanticVersion.TryParse("1.2.3.4", out _), "四段应判非法");
            Assert.False(AbsPkg.SemanticVersion.TryParse("", out _), "空串应判非法");
            Assert.False(AbsPkg.SemanticVersion.TryParse("latest", out _), "latest 应判非法");

            Assert.True(AbsPkg.SemanticVersion.TryParse("3.10.0", out var older), "解析");
            Assert.True(older < short3, "3.10 < 3.12");
        });

        h.Case("SC-56", "语义化版本预发布排序", () =>
        {
            Assert.True(AbsPkg.SemanticVersion.TryParse("1.0.0-alpha", out var alpha), "解析 alpha");
            Assert.True(AbsPkg.SemanticVersion.TryParse("1.0.0-alpha.1", out var alpha1), "解析 alpha.1");
            Assert.True(AbsPkg.SemanticVersion.TryParse("1.0.0-beta", out var beta), "解析 beta");
            Assert.True(AbsPkg.SemanticVersion.TryParse("1.0.0", out var release), "解析正式版");

            Assert.True(alpha < alpha1, "alpha < alpha.1");
            Assert.True(alpha1 < beta, "alpha.1 < beta");
            Assert.True(beta < release, "预发布 < 正式版（SemVer 规范）");
        });

        h.Case("SC-57", "版本约束求值", () =>
        {
            Assert.True(AbsPkg.SemanticVersion.TryParse("3.12.1", out var v), "解析");

            Assert.True(v.Satisfies(">=3.10"), ">=3.10");
            Assert.True(v.Satisfies(">=3.10,<4"), "区间");
            Assert.True(v.Satisfies("*"), "通配");
            Assert.True(v.Satisfies(null), "无约束视为满足");
            Assert.False(v.Satisfies(">=3.13"), "不满足");
            Assert.False(v.Satisfies(">=3.10,<3.12"), "区间右界不满足");

            // 约束写法错误时必须按"不满足"处理（安全侧：宁可让用户改包，也不静默放行）
            Assert.False(v.Satisfies(">=abc"), "非法约束应按不满足处理");
        });
    }
}
