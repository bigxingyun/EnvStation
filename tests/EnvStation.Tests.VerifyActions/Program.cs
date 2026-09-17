using System.Collections.Immutable;
using EnvStation.TestKit;

namespace EnvStation.Tests.VerifyActions;

/// <summary>
/// A09 验证与断言 / A10 交互与报告 / A11 清理与生命周期 的动作测试。
///
/// <para><b>本套用例的关键设计：不启动任何真实进程，也不真的弹窗。</b>
/// 进程执行被替换成一个记录调用的假实现，交互被替换成一个脚本化的假实现。
/// 这样测试既能断言"参数以数组传递、没有任何拼接"，又不依赖本机装了什么软件，
/// 更不会在测试过程中弹出窗口或运行用户的程序。</para>
///
/// <para><b>重点覆盖的拒绝路径</b>：可执行文件白名单、路径越界、盘根保护、
/// 跨包删除、无人值守提问——这些一旦漏判，后果都是"用户机器被动了不该动的东西"。</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("验证 / 交互 / 清理动作测试（假进程执行器 + 假交互，不运行任何真实程序）");
        Console.WriteLine();

        var h = new TestHarness("验证与清理动作");

        VerifyCases(h);
        InteractionCases(h);
        CleanupCases(h);
        AllowListCases(h);

        var code = h.Summarize();

        var jsonPath = TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return code;
    }

    // ══════════════════════════ 测试替身 ══════════════════════════

    /// <summary>记录调用并返回脚本化输出的假进程执行器。</summary>
    private sealed class FakeProcessRunner : CoreActions.IProcessRunner
    {
        private readonly Func<string, ImmutableArray<string>, ProcessScript> _script;

        internal FakeProcessRunner(Func<string, ImmutableArray<string>, ProcessScript> script) => _script = script;

        internal List<string> Invocations { get; } = [];

        /// <summary>记录每一次"文件名 + 参数数组"的调用（用于断言没有字符串拼接）。</summary>
        internal List<(string File, ImmutableArray<string> Args)> Calls { get; } = [];

        public ValueTask<CoreActions.ProcessRunResult> RunAsync(
            string executablePath,
            ImmutableArray<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Invocations.Add(CoreActions.ExecutableAllowList.DescribeInvocation(executablePath, arguments));
            Calls.Add((executablePath, arguments));

            var script = _script(executablePath, arguments);
            return ValueTask.FromResult(script.Started
                ? new CoreActions.ProcessRunResult(true, script.ExitCode, script.StdOut, script.StdErr, null, TimeSpan.FromMilliseconds(12))
                : CoreActions.ProcessRunResult.NotStarted(script.FailureReason ?? "脚本要求不启动", TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed record ProcessScript(bool Started, int ExitCode, string StdOut, string StdErr, string? FailureReason = null)
    {
        internal static ProcessScript Output(string stdout, int exitCode = 0) => new(true, exitCode, stdout, string.Empty);

        internal static ProcessScript Failure(string reason) => new(false, -1, string.Empty, string.Empty, reason);
    }

    /// <summary>脚本化的假交互：按预设序列回答问题并记录通知。</summary>
    private sealed class FakeInteraction : CoreActions.IInteractionSink
    {
        private readonly Queue<CoreActions.PromptAnswer> _answers;

        internal FakeInteraction(params CoreActions.PromptAnswer[] answers) => _answers = new Queue<CoreActions.PromptAnswer>(answers);

        internal List<(CoreActions.NotifyLevel Level, string Message)> Notifications { get; } = [];

        internal List<CoreActions.PromptRequest> Prompts { get; } = [];

        public void Notify(CoreActions.NotifyLevel level, string message) => Notifications.Add((level, message));

        public ValueTask<CoreActions.PromptAnswer> PromptAsync(CoreActions.PromptRequest request, CancellationToken cancellationToken)
        {
            Prompts.Add(request);
            return ValueTask.FromResult(_answers.Count > 0 ? _answers.Dequeue() : CoreActions.PromptAnswer.Cancelled);
        }
    }

    // ══════════════════════════ 脚手架 ══════════════════════════

    private static readonly AbsActions.CapabilitySet AllCapabilities = new(
    [
        AbsActions.CapabilityIds.Inspect,
        AbsActions.CapabilityIds.EnvironmentUser,
        AbsActions.CapabilityIds.EnvironmentMachine,
        AbsActions.CapabilityIds.PathModify,
        AbsActions.CapabilityIds.FileSystemInstall,
        AbsActions.CapabilityIds.ProcessLaunch,
        AbsActions.CapabilityIds.UserInteraction,
        AbsActions.CapabilityIds.Cleanup,
        AbsActions.CapabilityIds.Archive,
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
        Dictionary<string, AbsPkg.ScriptValue>? arguments = null,
        IEnumerable<string>? authorizedRoots = null,
        SandboxEnvironment? sandbox = null,
        CoreActions.IProcessRunner? processRunner = null,
        CoreActions.IInteractionSink? interaction = null,
        bool unattended = false,
        CoreActions.VariableTable? variables = null,
        IEnumerable<string>? capabilities = null,
        CoreActions.RunLedger? ledger = null)
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
            "x-test.verify-actions",
            "run-va",
            new AbsActions.CapabilitySet(capabilities ?? AllCapabilities.Ids),
            authorizedRoots ?? [Path.GetTempPath()],
            variables ?? new CoreActions.VariableTable(),
            new CoreActions.QuotaMeter(CoreActions.ResourceQuota.Default),
            new CoreActions.MemoryAuditSink(),
            unattended: unattended,
            environment: sandbox,
            processRunner: processRunner,
            interaction: interaction,
            ledger: ledger);

        return CoreActions.ActionExecutor
            .ExecuteAsync(resolved.Value, context, bound.Value)
            .AsTask().GetAwaiter().GetResult();
    }

    // ══════════════════════════ A09 验证与断言 ══════════════════════════

    private static void VerifyCases(TestHarness h)
    {
        var work = Path.Combine(Path.GetTempPath(), "envstation-va-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            h.Case("VA-01", "★verify.version_output：命令不在白名单时拒绝执行", () =>
            {
                var runner = new FakeProcessRunner(static (_, _) => ProcessScript.Output("should not run"));
                var result = Run("envstation.verify.version_output",
                    Args(("command", S(@"C:\Windows\System32\curl.exe"))),
                    processRunner: runner);

                Assert.False(result.Success, "白名单外的程序不应被执行");
                Assert.Equal(0, runner.Calls.Count, "假执行器一次都不应被调用——拒绝必须发生在启动之前");
                Assert.Contains("白名单", result.Message, "应说明白名单规则");
            });

            h.Case("VA-02", "★verify.version_output：参数以数组传递，不做字符串拼接", () =>
            {
                var python = Path.Combine(work, "python.exe");
                File.WriteAllText(python, "stub");

                var runner = new FakeProcessRunner(static (_, args) =>
                    args.Contains("--version") ? ProcessScript.Output("Python 3.12.1") : ProcessScript.Output("nope", 1));

                // 用 AuthorizedRoots 让 work 目录成为可信目录，从而 python.exe 通过目录校验。
                var result = Run("envstation.verify.version_output",
                    Args(("command", S(python)), ("expect", S(">=3.10"))),
                    authorizedRoots: [work],
                    processRunner: runner);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("3.12.1", result.Outputs["version"], "应解析出版本号");
                Assert.Equal("true", result.Outputs["satisfied"], "应满足 >=3.10");
                Assert.Equal(1, runner.Calls.Count, "第一次尝试就应成功");
                Assert.Equal(1, runner.Calls[0].Args.Length, "参数应作为独立数组元素传递");
                Assert.Equal("--version", runner.Calls[0].Args[0], "参数内容应精确");
            });

            h.Case("VA-03", "verify.version_output：版本不满足要求时给出结构化失败", () =>
            {
                var python = Path.Combine(work, "python.exe");
                File.WriteAllText(python, "stub");

                var runner = new FakeProcessRunner(static (_, _) => ProcessScript.Output("Python 3.8.10"));
                var result = Run("envstation.verify.version_output",
                    Args(("command", S(python)), ("expect", S(">=3.10"))),
                    authorizedRoots: [work],
                    processRunner: runner);

                Assert.False(result.Success, "版本不满足应失败");
                Assert.Equal(Abs.EnvStationErrorCodes.AssertFailed, result.ErrorCode, "应返回断言失败码");
                Assert.Contains("3.8.10", result.Message, "应报告实际版本");
            });

            h.Case("VA-04", "verify.version_output：自定义正则必须含命名组 v", () =>
            {
                var python = Path.Combine(work, "python.exe");
                File.WriteAllText(python, "stub");

                var result = Run("envstation.verify.version_output",
                    Args(("command", S(python)), ("pattern", S(@"(\d+\.\d+)"))),
                    authorizedRoots: [work],
                    processRunner: new FakeProcessRunner(static (_, _) => ProcessScript.Output("x")));

                Assert.False(result.Success, "缺少命名组应被拒绝");
                Assert.Contains("名为 v 的命名组", result.Message, "应说明如何修正");
            });

            h.Case("VA-05", "verify.command_resolves：能解析到真实存在的命令", () =>
            {
                // cmd.exe 在 System32，必然在机器 PATH 中；这里只验证"解析"逻辑。
                var result = Run("envstation.verify.command_resolves",
                    Args(("command", S("cmd")), ("expect_path", S(Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "cmd.exe")))));

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("true", result.Outputs["matches"], "应判为一致");
            });

            h.Case("VA-06", "verify.command_resolves：解析结果与期望不符时说明原因", () =>
            {
                var result = Run("envstation.verify.command_resolves",
                    Args(("command", S("cmd")), ("expect_path", S(@"D:\Not\Where\It\Is\cmd.exe"))));

                Assert.False(result.Success, "路径不符应失败");
                Assert.Contains("更靠前的同名命令", result.Message, "应指出冲突这一最常见原因");
            });

            h.Case("VA-07", "★verify.env_effective：变量名含非法字符时拒绝（防命令注入）", () =>
            {
                var runner = new FakeProcessRunner(static (_, _) => ProcessScript.Output("x"));
                var result = Run("envstation.verify.env_effective",
                    Args(("name", S("PATH & calc.exe"))),
                    processRunner: runner);

                Assert.False(result.Success, "含元字符的变量名应被拒绝");
                Assert.Equal(0, runner.Calls.Count, "拒绝必须发生在启动子进程之前");
                Assert.Contains("只允许字母、数字与下划线", result.Message, "应说明字符集限制");
            });

            h.Case("VA-08", "verify.env_effective：指定作用域时直接读注册表（沙箱）", () =>
            {
                var sandbox = new SandboxEnvironment();
                sandbox.Seed(AbsEnv.EnvScope.User, "VA_LAYER", "expected-value");

                var result = Run("envstation.verify.env_effective",
                    Args(("name", S("VA_LAYER")), ("scope", S("user")), ("expect", S("expected-value"))),
                    sandbox: sandbox);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("true", result.Outputs["defined"], "应判为已定义");
                Assert.Contains("用户级配置", result.Outputs["source"], "应说明证据来源");
            });

            h.Case("VA-09", "★verify.env_effective：末尾分隔符差异不判为失败（Windows 已知行为）", () =>
            {
                var sandbox = new SandboxEnvironment();
                sandbox.Seed(AbsEnv.EnvScope.User, "VA_TRAILING", @"C:\Tools\");

                var result = Run("envstation.verify.env_effective",
                    Args(("name", S("VA_TRAILING")), ("scope", S("user")), ("expect", S(@"C:\Tools"))),
                    sandbox: sandbox);

                Assert.True(result.Success,
                    $"仅末尾反斜杠不同不应判失败（否则用户会看到'明明设对了却验证失败'）：{result.Message}");
            });

            h.Case("VA-10", "verify.env_effective：未定义时明确失败", () =>
            {
                var sandbox = new SandboxEnvironment();
                var result = Run("envstation.verify.env_effective",
                    Args(("name", S("VA_MISSING")), ("scope", S("user"))),
                    sandbox: sandbox);

                Assert.False(result.Success, "未定义应失败");
                Assert.Contains("未定义", result.Message, "应说明现象");
            });

            h.Case("VA-11", "★verify.file_exists：授权根之外的路径被拒绝（防信息泄露）", () =>
            {
                var result = Run("envstation.verify.file_exists",
                    Args(("path", S(@"C:\Windows\System32\drivers\etc\hosts"))),
                    authorizedRoots: [work]);

                Assert.False(result.Success, "包不应能探测授权范围之外的文件");
                Assert.Equal(Abs.EnvStationErrorCodes.PathOutsideAuthorizedRoot, result.ErrorCode, "应返回越界码");
            });

            h.Case("VA-12", "verify.file_exists：目录非空与最小字节数检查", () =>
            {
                var emptyDir = Path.Combine(work, "empty");
                Directory.CreateDirectory(emptyDir);

                var nonEmpty = Run("envstation.verify.file_exists",
                    Args(("path", S(emptyDir)), ("kind", S("directory")), ("non_empty", B(true))),
                    authorizedRoots: [work]);
                Assert.False(nonEmpty.Success, "空目录应判失败");

                var smallFile = Path.Combine(work, "small.bin");
                File.WriteAllText(smallFile, "abc");

                var tooSmall = Run("envstation.verify.file_exists",
                    Args(("path", S(smallFile)), ("min_bytes", I(1000))),
                    authorizedRoots: [work]);
                Assert.False(tooSmall.Success, "文件过小应判失败");
                Assert.Contains("残缺文件", tooSmall.Message, "应指出最常见的原因");
            });

            h.Case("VA-13", "verify.conflict_clear：命令不存在时给出明确失败", () =>
            {
                var result = Run("envstation.verify.conflict_clear",
                    Args(("command", S("va_definitely_not_a_real_command_xyz"))));

                Assert.False(result.Success, "不存在的命令应失败");
                Assert.Contains("未找到", result.Message, "应说明现象");
            });

            h.Case("VA-14", "★assert：条件为真通过，为假失败并给出用户文案", () =>
            {
                var variables = new CoreActions.VariableTable();
                variables.SetPackageVariable("found", "true");
                variables.SetPackageVariable("version", "3.12.1");

                var ok = Run("envstation.verify.assert",
                    Args(("condition", S("${pkg.found} == true and semver_gt(${pkg.version}, \"3.10.0\")")),
                         ("message", S("应当已检测到 Python"))),
                    variables: variables);
                Assert.True(ok.Success, $"应通过：{ok.Message}");

                var fail = Run("envstation.verify.assert",
                    Args(("condition", S("${pkg.found} == false")), ("message", S("没有找到 Python，请先安装"))),
                    variables: variables);
                Assert.False(fail.Success, "条件为假应失败");
                Assert.Equal(Abs.EnvStationErrorCodes.AssertFailed, fail.ErrorCode, "应返回断言失败码");
                Assert.Contains("没有找到 Python", fail.Message, "必须原样展示作者写的用户文案");
            });

            h.Case("VA-15", "★assert：表达式写错与断言不成立必须区分", () =>
            {
                var variables = new CoreActions.VariableTable();
                variables.SetPackageVariable("x", "1");

                var broken = Run("envstation.verify.assert",
                    Args(("condition", S("${pkg.x} === 1")), ("message", S("m"))),
                    variables: variables);

                Assert.False(broken.Success, "表达式非法应失败");
                Assert.Equal(Abs.EnvStationErrorCodes.WorkflowExpressionFailed, broken.ErrorCode,
                    "表达式本身写错应返回表达式错误码，而不是断言失败码——两者的处置方式完全不同");
            });

            h.Case("VA-16", "verify.config_restored：哈希一致通过、不一致失败", () =>
            {
                var file = Path.Combine(work, "settings.xml");
                File.WriteAllText(file, "<settings/>");

                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(file))
                {
                    hash = "sha256:" + Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                }

                var ok = Run("envstation.verify.config_restored",
                    Args(("path", S(file)), ("expect_sha256", S(hash))),
                    authorizedRoots: [work]);
                Assert.True(ok.Success, $"应通过：{ok.Message}");

                File.WriteAllText(file, "<settings>changed</settings>");
                var fail = Run("envstation.verify.config_restored",
                    Args(("path", S(file)), ("expect_sha256", S(hash))),
                    authorizedRoots: [work]);
                Assert.False(fail.Success, "内容变化应失败");
                Assert.Contains("又被其他程序改动过", fail.Message, "应给出最可能的原因");
            });
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }

    // ══════════════════════════ A10 交互与报告 ══════════════════════════

    private static void InteractionCases(TestHarness h)
    {
        var work = Path.Combine(Path.GetTempPath(), "envstation-va-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            h.Case("VA-17", "ui.notify：把提示送进交互通道并写审计", () =>
            {
                var interaction = new FakeInteraction();
                var result = Run("envstation.ui.notify",
                    Args(("level", S("warning")), ("message", S("即将修改系统级 PATH"))),
                    interaction: interaction);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal(1, interaction.Notifications.Count, "应产生一条通知");
                Assert.Equal(CoreActions.NotifyLevel.Warning, interaction.Notifications[0].Level, "级别应正确映射");
            });

            h.Case("VA-18", "★ui.prompt：无人值守且有默认值时使用默认值并留痕", () =>
            {
                var interaction = new FakeInteraction();
                var result = Run("envstation.ui.prompt",
                    Args(("question", S("是否继续？")), ("type", S("confirm")), ("default", S("yes"))),
                    unattended: true,
                    interaction: interaction);

                Assert.True(result.Success, "有默认值时无人值守应成功");
                Assert.Equal("true", result.Outputs["from_default"], "应明确标记来自默认值");
                Assert.Equal(0, interaction.Prompts.Count, "无人值守时不应真的弹窗");
            });

            h.Case("VA-19", "★ui.prompt：无人值守且无默认值时明确失败（不静默猜测）", () =>
            {
                var result = Run("envstation.ui.prompt",
                    Args(("question", S("请输入代理地址"))),
                    unattended: true);

                Assert.False(result.Success, "无人值守且无默认值应失败");
                Assert.Equal(Abs.EnvStationErrorCodes.UnattendedInteraction, result.ErrorCode, "应返回无人值守码");
                Assert.Contains("提供 default", result.Message, "应给出可操作的处置");
            });

            h.Case("VA-20", "ui.prompt：敏感输入不回显、且登记进 secret 作用域以便脱敏", () =>
            {
                var interaction = new FakeInteraction(new CoreActions.PromptAnswer(true, "hunter2", false));
                var variables = new CoreActions.VariableTable();

                var result = Run("envstation.ui.prompt",
                    Args(("question", S("代理密码")), ("secret", B(true))),
                    interaction: interaction,
                    variables: variables);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("***", result.Outputs["value"], "输出中必须掩码");
                Assert.Equal(1, variables.SecretCount, "敏感值必须登记进 secret 作用域，从而在所有日志中被替换");
                Assert.NotContains("hunter2", result.Message, "消息中不得出现明文");
            });

            h.Case("VA-21", "ui.prompt：用户取消时按中止处理", () =>
            {
                var interaction = new FakeInteraction(CoreActions.PromptAnswer.Cancelled);
                var result = Run("envstation.ui.prompt",
                    Args(("question", S("继续吗？"))),
                    interaction: interaction);

                Assert.False(result.Success, "用户取消应失败");
                Assert.Equal(Abs.EnvStationErrorCodes.WorkflowAborted, result.ErrorCode, "应返回中止码");
            });

            h.Case("VA-22", "ui.prompt：choice 类型必须给出可选项", () =>
            {
                var result = Run("envstation.ui.prompt",
                    Args(("question", S("选择镜像")), ("type", S("choice"))));

                Assert.False(result.Success, "缺少 choices 应被拒绝");
                Assert.Contains("必须提供 choices", result.Message, "应说明缺什么");
            });

            h.Case("VA-23", "ui.progress：上报表进度", () =>
            {
                var result = Run("envstation.ui.progress",
                    Args(("percent", I(42)), ("message", S("正在解压"))));

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("42", result.Outputs["percent"], "应回显百分比");
            });

            h.Case("VA-24", "report.generate：Markdown 报告包含步骤、触碰路径与可还原点", () =>
            {
                var sandbox = new SandboxEnvironment();
                var variables = new CoreActions.VariableTable();

                // 账本是"本次运行"的账本，因此两次调用必须共用同一个实例。
                var ledger = new CoreActions.RunLedger
                {
                    RunId = "run-va-report",
                    PackageId = "x-test.verify-actions",
                    StartedAt = DateTimeOffset.Now,
                };

                // 先做一次真实修改，让账本里有点东西。
                _ = Run("envstation.env.set", Args(("scope", S("user")), ("name", S("VA_REPORT")), ("value", S("x"))),
                    sandbox: sandbox, variables: variables, ledger: ledger);

                var result = Run("envstation.report.generate",
                    Args(("format", S("markdown"))),
                    sandbox: sandbox,
                    variables: variables,
                    ledger: ledger);

                Assert.True(result.Success, $"应成功：{result.Message}");
                var content = result.Outputs["content"];
                Assert.Contains("环境站执行报告", content, "应有标题");
                Assert.Contains("envstation.env.set", content, "应含步骤明细");
            });

            h.Case("VA-25", "report.generate：JSON 报告结构完整", () =>
            {
                var result = Run("envstation.report.generate", Args(("format", S("json"))));
                Assert.True(result.Success, $"应成功：{result.Message}");

                var content = result.Outputs["content"];
                Assert.Contains("\"packageId\"", content, "应含 packageId");
                Assert.Contains("\"steps\"", content, "应含 steps");
                Assert.Contains("\"reversibleTokens\"", content, "应含可还原点");
            });

            h.Case("VA-26", "★report.generate：报告落盘路径必须落在授权范围内", () =>
            {
                var result = Run("envstation.report.generate",
                    Args(("format", S("markdown")), ("out", S(@"C:\Windows\Temp\report.md"))),
                    authorizedRoots: [work]);

                Assert.False(result.Success, "越界路径应被拒绝");
                Assert.Equal(Abs.EnvStationErrorCodes.PathOutsideAuthorizedRoot, result.ErrorCode, "应返回越界码");
            });

            h.Case("VA-27", "★report.export_diagnostics：诊断包已脱敏且不含环境变量值", () =>
            {
                var sandbox = new SandboxEnvironment();
                sandbox.Seed(AbsEnv.EnvScope.User, "VA_SECRET_TOKEN", "super-secret-value");

                var outPath = Path.Combine(work, "diag.zip");
                var result = Run("envstation.report.export_diagnostics",
                    Args(("out", S(outPath)), ("redact", B(true))),
                    authorizedRoots: [work],
                    sandbox: sandbox);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.True(File.Exists(outPath), "诊断包应已生成");

                using var archive = System.IO.Compression.ZipFile.OpenRead(outPath);
                Assert.Equal(4, archive.Entries.Count, "应含 4 个文件");

                var summaryEntry = archive.GetEntry("env-summary.txt");
                Assert.NotNull(summaryEntry, "应含环境摘要");
                using var reader = new StreamReader(summaryEntry!.Open());
                var summary = reader.ReadToEnd();

                Assert.Contains("VA_SECRET_TOKEN", summary, "摘要应列出变量名（这有助于定位问题）");
                Assert.NotContains("super-secret-value", summary, "摘要绝不能含变量值——诊断包常被发到公开渠道");
            });

            h.Case("VA-28", "report.export_diagnostics：脱敏会替换用户名与家目录", () =>
            {
                var user = System.Environment.UserName;
                var redacted = CoreActions.Builtin.DiagnosticsCollector.RedactPersonalData($"C:\\Users\\{user}\\x 由 {user} 使用");

                Assert.NotContains(user, redacted, "用户名应被替换");
                Assert.Contains("<用户>", redacted, "应使用占位符");
            });
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }

    // ══════════════════════════ A11 清理与生命周期 ══════════════════════════

    private static void CleanupCases(TestHarness h)
    {
        var work = Path.Combine(Path.GetTempPath(), "envstation-va-clean-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            h.Case("VA-29", "cleanup.temp：清理临时目录并统计体积", () =>
            {
                var tempDir = Path.Combine(work, "pkg-temp");
                Directory.CreateDirectory(Path.Combine(tempDir, "nested"));
                File.WriteAllText(Path.Combine(tempDir, "a.txt"), "aaaa");
                File.WriteAllText(Path.Combine(tempDir, "nested", "b.txt"), "bbbb");

                var result = Run("envstation.cleanup.temp",
                    Args(("directory", S(tempDir))),
                    authorizedRoots: [work]);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("2", result.Outputs["removed_files"], "应删除 2 个文件");
                Assert.False(File.Exists(Path.Combine(tempDir, "a.txt")), "文件应已被删除");
            });

            h.Case("VA-30", "cleanup.temp：dry_run 不删除任何文件", () =>
            {
                var tempDir = Path.Combine(work, "dry");
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "keep.txt"), "keep");

                var result = Run("envstation.cleanup.temp",
                    Args(("directory", S(tempDir)), ("dry_run", B(true))),
                    authorizedRoots: [work]);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("true", result.Outputs["dry_run"], "应标记为干跑");
                Assert.True(File.Exists(Path.Combine(tempDir, "keep.txt")), "干跑绝不能删除文件");
            });

            h.Case("VA-31", "★cleanup.temp：拒绝清理磁盘根与系统目录", () =>
            {
                foreach (var dangerous in new[] { @"C:\", System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows) })
                {
                    var result = Run("envstation.cleanup.temp",
                        Args(("directory", S(dangerous))),
                        authorizedRoots: [dangerous, work]);

                    Assert.False(result.Success, $"{dangerous} 必须被拒绝");
                    Assert.Contains("系统关键目录", result.Message, "应说明拒绝原因");
                }
            });

            h.Case("VA-32", "★cleanup.remove_package：只能删除自己的包目录", () =>
            {
                var other = Path.Combine(work, "other-package");
                Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(other, "data.txt"), "important");

                var result = Run("envstation.cleanup.remove_package",
                    Args(("package_id", S("x-someone-else.package")), ("directory", S(other))),
                    authorizedRoots: [work]);

                Assert.False(result.Success, "删除别的包的目录应被拒绝（隔离模型的核心）");
                Assert.Contains("只能清理自己的私有目录", result.Message, "应说明隔离规则");
                Assert.True(File.Exists(Path.Combine(other, "data.txt")), "他人数据必须完好");
            });

            h.Case("VA-33", "cleanup.remove_package：自己的目录可删除（含 dry_run）", () =>
            {
                var mine = Path.Combine(work, "mine");
                Directory.CreateDirectory(mine);
                File.WriteAllText(Path.Combine(mine, "cache.bin"), "cache");

                var dry = Run("envstation.cleanup.remove_package",
                    Args(("package_id", S("x-test.verify-actions")), ("directory", S(mine)), ("dry_run", B(true))),
                    authorizedRoots: [work]);

                Assert.True(dry.Success, $"干跑应成功：{dry.Message}");
                Assert.True(Directory.Exists(mine), "干跑不应删除目录");

                var real = Run("envstation.cleanup.remove_package",
                    Args(("package_id", S("x-test.verify-actions")), ("directory", S(mine))),
                    authorizedRoots: [work]);

                Assert.True(real.Success, $"应成功：{real.Message}");
                Assert.False(Directory.Exists(mine), "目录应已被删除");
            });

            h.Case("VA-34", "fs.copy：复制目录并统计文件数", () =>
            {
                var src = Path.Combine(work, "src");
                Directory.CreateDirectory(Path.Combine(src, "sub"));
                File.WriteAllText(Path.Combine(src, "one.txt"), "1");
                File.WriteAllText(Path.Combine(src, "sub", "two.txt"), "2");

                var dest = Path.Combine(work, "dest");
                var result = Run("envstation.fs.copy",
                    Args(("src", S(src)), ("dest", S(dest))),
                    authorizedRoots: [work]);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("2", result.Outputs["file_count"], "应复制 2 个文件");
                Assert.True(File.Exists(Path.Combine(dest, "sub", "two.txt")), "嵌套文件应复制");
            });

            h.Case("VA-35", "★fs.copy：目标已存在且未允许覆盖时拒绝", () =>
            {
                var src = Path.Combine(work, "src2");
                var dest = Path.Combine(work, "dest2");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dest);
                File.WriteAllText(Path.Combine(src, "f.txt"), "new");
                File.WriteAllText(Path.Combine(dest, "existing.txt"), "keep");

                var result = Run("envstation.fs.copy",
                    Args(("src", S(src)), ("dest", S(dest))),
                    authorizedRoots: [work]);

                Assert.False(result.Success, "目标已存在时应拒绝");
                Assert.True(File.Exists(Path.Combine(dest, "existing.txt")), "已有数据必须完好");
            });

            h.Case("VA-36", "fs.copy：dry_run 只统计不写入", () =>
            {
                var src = Path.Combine(work, "src3");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "a.bin"), new string('x', 2048));

                var dest = Path.Combine(work, "dest3");
                var result = Run("envstation.fs.copy",
                    Args(("src", S(src)), ("dest", S(dest)), ("dry_run", B(true))),
                    authorizedRoots: [work]);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.False(Directory.Exists(dest), "干跑不应创建目标目录");
            });

            h.Case("VA-37", "fs.move：同卷移动并清理源", () =>
            {
                var src = Path.Combine(work, "move-src");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "f.txt"), "data");

                var dest = Path.Combine(work, "move-dest");
                var result = Run("envstation.fs.move",
                    Args(("src", S(src)), ("dest", S(dest))),
                    authorizedRoots: [work]);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.False(Directory.Exists(src), "源目录应已移除");
                Assert.True(File.Exists(Path.Combine(dest, "f.txt")), "目标应包含文件");
            });

            h.Case("VA-38", "★fs.move：目标已存在时拒绝（移动不覆盖）", () =>
            {
                var src = Path.Combine(work, "move-src2");
                var dest = Path.Combine(work, "move-dest2");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dest);
                File.WriteAllText(Path.Combine(src, "f.txt"), "src");
                File.WriteAllText(Path.Combine(dest, "g.txt"), "dest");

                var result = Run("envstation.fs.move",
                    Args(("src", S(src)), ("dest", S(dest))),
                    authorizedRoots: [work]);

                Assert.False(result.Success, "目标已存在时应拒绝");
                Assert.True(File.Exists(Path.Combine(src, "f.txt")), "源文件必须保留");
                Assert.True(File.Exists(Path.Combine(dest, "g.txt")), "目标文件必须保留");
            });

            h.Case("VA-39", "fs.move：干跑会点明是否同卷（风险提示）", () =>
            {
                var src = Path.Combine(work, "move-src3");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "f.txt"), "d");

                var result = Run("envstation.fs.move",
                    Args(("src", S(src)), ("dest", S(Path.Combine(work, "move-dest3"))), ("dry_run", B(true))),
                    authorizedRoots: [work]);

                Assert.True(result.Success, $"应成功：{result.Message}");
                Assert.Equal("true", result.Outputs["same_volume"], "同卷应被识别");
                Assert.Contains("原子改名", result.Message, "应说明同卷的优势");
            });
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }

    // ══════════════════════════ 可执行文件白名单 ══════════════════════════

    private static void AllowListCases(TestHarness h)
    {
        h.Case("VA-40", "★白名单：通用工具一律拒绝（需求 AI-7）", () =>
        {
            string[] forbidden =
            [
                "cmd.exe", "powershell.exe", "pwsh.exe", "curl.exe", "wget.exe",
                "msiexec.exe", "reg.exe", "regedit.exe", "schtasks.exe", "sc.exe",
                "net.exe", "netsh.exe", "rundll32.exe", "wscript.exe", "cscript.exe",
            ];

            foreach (var tool in forbidden)
            {
                Assert.False(
                    CoreActions.ExecutableAllowList.IsKnownRuntimeExecutable(tool),
                    $"{tool} 绝不能被允许运行——它是持久化、提权或任意代码执行的入口");
            }
        });

        h.Case("VA-41", "白名单：已登记运行时的可执行文件被允许", () =>
        {
            foreach (var tool in new[] { "python.exe", "java.exe", "node.exe", "dotnet.exe", "gcc.exe", "mvn.cmd" })
            {
                Assert.True(
                    CoreActions.ExecutableAllowList.IsKnownRuntimeExecutable(tool),
                    $"{tool} 是验证安装结果所必需的");
            }
        });

        h.Case("VA-42", "★白名单：含 shell 元字符的命令被拒绝", () =>
        {
            foreach (var bad in new[] { "python.exe & calc.exe", "a|b", "x>y", "q\"w", "line\nbreak" })
            {
                var verdict = CoreActions.ExecutableAllowList.Check(bad, [Path.GetTempPath()]);
                Assert.False(verdict.Allowed, $"含元字符的命令必须被拒绝：{bad}");
            }
        });

        h.Case("VA-43", "★白名单：前缀相似的兄弟目录不被当作可信目录", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "trusted-root");
            var sibling = root + "-evil";

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sibling);

            try
            {
                // 在兄弟目录放一个白名单内的文件名
                var fake = Path.Combine(sibling, "python.exe");
                File.WriteAllText(fake, "stub");

                var verdict = CoreActions.ExecutableAllowList.Check(fake, [root]);
                Assert.False(verdict.Allowed, "同前缀的兄弟目录必须被判为不可信");
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                    Directory.Delete(sibling, recursive: true);
                }
                catch (IOException)
                {
                    // 忽略。
                }
            }
        });

        h.Case("VA-44", "白名单：可信目录的子目录被允许", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "trusted-root-" + Guid.NewGuid().ToString("N")[..8]);
            var binDir = Path.Combine(root, "bin");
            Directory.CreateDirectory(binDir);

            try
            {
                var exe = Path.Combine(binDir, "python.exe");
                File.WriteAllText(exe, "stub");

                var verdict = CoreActions.ExecutableAllowList.Check(exe, [root]);
                Assert.True(verdict.Allowed, "运行时主目录的 bin 子目录必须被允许（JAVA_HOME\\bin 就是这种结构）");
                Assert.Equal(exe, verdict.ResolvedPath, "应返回解析后的完整路径");
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                    // 忽略。
                }
            }
        });
    }
}
