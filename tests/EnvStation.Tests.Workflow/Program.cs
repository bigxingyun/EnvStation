using System.Collections.Immutable;
using EnvStation.TestKit;

namespace EnvStation.Tests.Workflow;

/// <summary>
/// 工作流解释器测试：把"标准层解析出来的工作流"真的跑起来。
///
/// <para><b>这一套用例为什么最关键</b>：前面几层各自证明了自己的性质——
/// 标准层证明了"非法的工作流写不出来"，动作层证明了"一个动作不会乱来"。
/// 而解释器是两者的接缝：控制流、变量作用域、失败策略、并行限制、干跑
/// 全都只在这一层才真正成立。接缝处出问题，前面两层的保证都会漏掉。</para>
///
/// <para><b>安全承诺</b>：所有用例都用内存沙箱环境、假进程执行器与假交互，
/// 因此可以在任何机器上反复运行而不改动真实环境。</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("工作流解释器测试（沙箱环境 + 假进程/交互）");
        Console.WriteLine();

        var h = new TestHarness("工作流解释器");

        FlowCases(h);
        ErrorPolicyCases(h);
        SafetyCases(h);

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
        AbsActions.CapabilityIds.EnvironmentUser,
        AbsActions.CapabilityIds.EnvironmentMachine,
        AbsActions.CapabilityIds.PathModify,
        AbsActions.CapabilityIds.FileSystemInstall,
        AbsActions.CapabilityIds.ProcessLaunch,
        AbsActions.CapabilityIds.UserInteraction,
        AbsActions.CapabilityIds.Cleanup,
    ]);

    private sealed class FakeInteraction(bool confirmAnswer) : CoreActions.IInteractionSink
    {
        internal List<CoreActions.PromptRequest> Prompts { get; } = [];

        public void Notify(CoreActions.NotifyLevel level, string message)
        {
            // 通知在测试中不需要断言内容，这里只保证不抛异常。
        }

        public ValueTask<CoreActions.PromptAnswer> PromptAsync(CoreActions.PromptRequest request, CancellationToken cancellationToken)
        {
            Prompts.Add(request);
            return ValueTask.FromResult(new CoreActions.PromptAnswer(true, confirmAnswer ? "yes" : "no", false));
        }
    }

    private sealed class FakeProcessRunner(string output) : CoreActions.IProcessRunner
    {
        internal List<string> Calls { get; } = [];

        public ValueTask<CoreActions.ProcessRunResult> RunAsync(
            string executablePath, ImmutableArray<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(CoreActions.ExecutableAllowList.DescribeInvocation(executablePath, arguments));
            return ValueTask.FromResult(new CoreActions.ProcessRunResult(
                true, 0, output, string.Empty, null, TimeSpan.FromMilliseconds(5)));
        }
    }

    /// <summary>
    /// 给工作流里的每个 uses 补上 pin.hash。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这不是"为了让测试通过"的权宜之计，而是真实工具链必须提供的能力。</b>
    /// 需求 S3 要求每个动作引用都携带实现哈希，但让包作者手工去查几十个哈希并不现实。
    /// 正式流程里这件事由 envstation pack 与脚手架自动完成（需求 M13-1、M13-3）；
    /// 本方法就是那个行为在测试里的等价物。
    /// </para>
    /// <para>顺带说明：pin.hash 缺失时解析会直接失败，
    /// 因此任何"忘记补哈希"的包都不会被静默放过。</para>
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

    /// <summary>解析 + 校验工作流 TOML，返回文档；stampPins 为 false 时保留原样的 pin。</summary>
    private static AbsPkg.WorkflowDocument Parse(string toml, bool stampPins = true)
    {
        var text = stampPins ? StampPins(toml) : toml;
        var bag = new AbsDiag.FindingBag();
        var parsed = CorePkg.WorkflowReader.Read(text, bag);
        Assert.True(parsed.IsSuccess, $"工作流应解析成功：{bag.ToReport().ToText()}");
        return parsed.Value;
    }

    /// <summary>准备一次运行所需的一切。</summary>
    private static (CoreWf.WorkflowInterpreter Interpreter, CoreActions.ActionExecutionContext Context, SandboxEnvironment Sandbox) Prepare(
        SandboxEnvironment? sandbox = null,
        IEnumerable<string>? capabilities = null,
        bool unattended = false,
        CoreActions.IProcessRunner? processRunner = null,
        CoreActions.IInteractionSink? interaction = null,
        IEnumerable<string>? authorizedRoots = null,
        CoreActions.RunLedger? ledger = null)
    {
        var bag = new AbsDiag.FindingBag();
        var registry = CoreActions.ActionRegistry.CreateDefault(bag);
        Assert.True(registry.IsSuccess, $"注册表应可用：{bag.ToReport().ToText()}");

        var env = sandbox ?? new SandboxEnvironment();
        var context = new CoreActions.ActionExecutionContext(
            "x-test.workflow",
            "run-wf",
            new AbsActions.CapabilitySet(capabilities ?? AllCapabilities.Ids),
            authorizedRoots ?? [Path.GetTempPath()],
            new CoreActions.VariableTable(),
            new CoreActions.QuotaMeter(CoreActions.ResourceQuota.Default),
            new CoreActions.MemoryAuditSink(),
            unattended: unattended,
            environment: env,
            processRunner: processRunner,
            interaction: interaction,
            ledger: ledger);

        return (new CoreWf.WorkflowInterpreter(registry.Value), context, env);
    }

    private static (CoreWf.WorkflowOutcome Outcome, CoreActions.ActionExecutionContext Context) Run(
        string toml,
        bool stampPins = true,
        SandboxEnvironment? sandbox = null,
        IEnumerable<string>? capabilities = null,
        bool unattended = false,
        CoreActions.IProcessRunner? processRunner = null,
        CoreActions.IInteractionSink? interaction = null,
        bool dryRun = false,
        IEnumerable<string>? authorizedRoots = null,
        CoreActions.RunLedger? ledger = null)
    {
        var document = Parse(toml, stampPins);
        var (interpreter, ctx, _) = Prepare(
            sandbox, capabilities, unattended, processRunner, interaction, authorizedRoots, ledger);

        var outcome = interpreter
            .RunAsync(document, ctx, new CoreWf.WorkflowOptions(DryRun: dryRun, Unattended: unattended))
            .AsTask().GetAwaiter().GetResult();

        return (outcome, ctx);
    }

    // ══════════════════════════ 控制流与变量 ══════════════════════════

    private static void FlowCases(TestHarness h)
    {
        h.Case("WF-01", "顺序执行：动作按声明顺序运行，register 绑定输出", () =>
        {
            const string toml = """
                [[steps]]
                id = "os"
                uses = "envstation.detect.os@1.0.0"
                register = "os"

                [[steps]]
                id = "arch"
                uses = "envstation.detect.arch@1.0.0"
                register = "arch"

                [[steps]]
                id = "check"
                uses = "envstation.verify.assert@1.0.0"
                [steps.with]
                condition = "${pkg.os.supported} == true"
                message = "系统版本必须满足最低要求"
                """;

            var (outcome, context) = Run(toml);

            Assert.True(outcome.Succeeded, $"应成功：{outcome.Message}");
            Assert.Equal(3, outcome.ExecutedSteps, "应执行 3 步");
            Assert.Equal(3, context.Ledger.Invocations.Count, "账本应记录 3 次调用");
            Assert.True(context.VariableTable.Exists("pkg.os.supported"), "register 应把输出绑定为变量");
        });

        h.Case("WF-02", "if / else：按条件选择分支", () =>
        {
            const string toml = """
                [[steps]]
                id = "arch"
                uses = "envstation.detect.arch@1.0.0"
                register = "a"

                [[steps]]
                if = "${pkg.a.emulated} == true"
                [[steps.then]]
                uses = "envstation.ui.notify@1.0.0"
                [steps.then.with]
                level = "warning"
                message = "正在模拟运行"
                [[steps.else]]
                uses = "envstation.ui.notify@1.0.0"
                [steps.else.with]
                level = "info"
                message = "原生运行"
                """;

            var (outcome, context) = Run(toml);

            Assert.True(outcome.Succeeded, $"应成功：{outcome.Message}");
            Assert.Equal(2, outcome.ExecutedSteps, "应执行 arch + 一个分支");

            var notifications = context.Ledger.Invocations
                .Where(static i => i.ActionId == "envstation.ui.notify")
                .ToArray();
            Assert.Equal(1, notifications.Length, "只应执行一个分支");
            Assert.Contains("原生运行", notifications[0].Message, "本机不是模拟运行，应走 else 分支");
        });

        h.Case("WF-03", "foreach：迭代列表并为每项绑定迭代变量", () =>
        {
            const string toml = """
                [[steps]]
                id = "seed"
                uses = "envstation.env.diff@1.0.0"
                [steps.with]
                snapshot_id = "latest"

                [[steps]]
                foreach = "${pkg.items}"
                as = "item"
                max = 10
                [[steps.do]]
                uses = "envstation.ui.progress@1.0.0"
                [steps.do.with]
                percent = 50
                message = "${pkg.item}"
                """;

            // 直接注入列表变量：pkg.items 用换行分隔（标准约定）
            var document = Parse(toml);
            var (interpreter, context, _) = Prepare();
            context.VariableTable.SetPackageVariable("items", "alpha\nbeta\ngamma");

            // 去掉第一步（它需要真实快照），只保留循环体部分来验证迭代语义
            var loopOnly = new AbsPkg.WorkflowDocument(
                document.SpecVersion,
                document.DefaultErrorPolicy,
                [document.Steps[1]]);

            var outcome = interpreter
                .RunAsync(loopOnly, context, new CoreWf.WorkflowOptions())
                .AsTask().GetAwaiter().GetResult();

            Assert.True(outcome.Succeeded, $"应成功：{outcome.Message}");
            Assert.Equal(3, outcome.ExecutedSteps, "应迭代 3 次");

            var messages = context.Ledger.Invocations.Select(static i => i.Message).ToArray();
            Assert.Contains("alpha", messages[0], "第一次迭代应对应第一项");
            Assert.Contains("gamma", messages[2], "第三次迭代应对应第三项");
        });

        h.Case("WF-04", "foreach：数据集超过声明上限时失败", () =>
        {
            const string toml = """
                [[steps]]
                foreach = "${pkg.items}"
                as = "x"
                max = 2
                [[steps.do]]
                uses = "envstation.ui.progress@1.0.0"
                [steps.do.with]
                percent = 1
                """;

            var document = Parse(toml);
            var (interpreter, context, _) = Prepare();
            context.VariableTable.SetPackageVariable("items", "a\nb\nc\nd");

            var outcome = interpreter
                .RunAsync(document, context, new CoreWf.WorkflowOptions())
                .AsTask().GetAwaiter().GetResult();

            Assert.False(outcome.Succeeded, "超过上限应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.WorkflowLoopLimitExceeded, outcome.ErrorCode, "应返回循环超限码");
            Assert.Contains("超过声明的上限", outcome.Message, "应说明原因与处置");
        });

        h.Case("WF-05", "when 守卫为假时跳过该步骤", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.env.diff@1.0.0"
                when = "${pkg.flag} == true"
                [steps.with]
                snapshot_id = "latest"

                [[steps]]
                uses = "envstation.detect.arch@1.0.0"
                """;

            var document = Parse(toml);
            var (interpreter, context, _) = Prepare();
            context.VariableTable.SetPackageVariable("flag", "false");

            var outcome = interpreter
                .RunAsync(document, context, new CoreWf.WorkflowOptions())
                .AsTask().GetAwaiter().GetResult();

            Assert.True(outcome.Succeeded, $"应成功：{outcome.Message}");
            Assert.Equal(1, outcome.SkippedSteps, "第一步应被跳过");
            Assert.Equal(1, outcome.ExecutedSteps, "只应执行第二步");
        });

        h.Case("WF-06", "★干跑：只读动作照常执行，有副作用的动作一个都不执行", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.detect.os@1.0.0"
                register = "os"

                [[steps]]
                uses = "envstation.env.set@1.0.0"
                [steps.with]
                scope = "user"
                name = "WF_DRYRUN"
                value = "should-not-be-written"
                """;

            var sandbox = new SandboxEnvironment();
            var (outcome, context) = Run(toml, sandbox: sandbox, dryRun: true);

            Assert.True(outcome.Succeeded, $"干跑应成功：{outcome.Message}");

            // 干跑必须真正执行**只读**动作：否则后续步骤拿不到真实值，
            // "预览将要发生什么"就无从谈起（这是端到端测试暴露出来的一条设计要点）。
            var readOnlyRan = context.Ledger.Invocations.Any(static i => i.ActionId == "envstation.detect.os");
            Assert.True(readOnlyRan, "只读动作（CAP.INSPECT）在干跑下应当真的执行，以便给出真实预览");

            // 但有副作用的一个都不能执行。
            var mutatingRan = context.Ledger.Invocations.Any(static i => i.ActionId == "envstation.env.set");
            Assert.False(mutatingRan, "有副作用的动作在干跑下绝不允许执行");
            Assert.Equal(0, sandbox.Count(AbsEnv.EnvScope.User), "干跑绝不能写入环境变量");
        });

        h.Case("WF-07", "confirm 步骤在有人值守时询问用户", () =>
        {
            const string toml = """
                [[steps]]
                confirm = "确认要把 PATH 加入托管目录吗？"
                default = false
                """;

            var interaction = new FakeInteraction(confirmAnswer: true);
            var outcome = Run(toml, interaction: interaction).Outcome;

            Assert.True(outcome.Succeeded, $"用户同意后应继续：{outcome.Message}");
            Assert.Equal(1, interaction.Prompts.Count, "应真的问了一次");
        });

        h.Case("WF-08", "confirm 步骤被用户拒绝时中止", () =>
        {
            const string toml = """
                [[steps]]
                confirm = "确认继续吗？"
                """;

            var interaction = new FakeInteraction(confirmAnswer: false);
            var outcome = Run(toml, interaction: interaction).Outcome;

            Assert.False(outcome.Succeeded, "用户拒绝应中止");
            Assert.Equal(Abs.EnvStationErrorCodes.WorkflowAborted, outcome.ErrorCode, "应返回中止码");
        });

        h.Case("WF-09", "confirm 步骤在无人值守时按默认值处理并留痕", () =>
        {
            const string toml = """
                [[steps]]
                confirm = "确认继续吗？"
                default = true
                """;

            var (outcome, context) = Run(toml, unattended: true);

            Assert.True(outcome.Succeeded, $"默认值为真时应继续：{outcome.Message}");
            Assert.Equal(1, outcome.ExecutedSteps, "确认步骤应被计入执行数");
            Assert.Equal(0, context.Ledger.Invocations.Count, "confirm 不是动作，不应产生动作调用记录");
        });

        h.Case("WF-10", "assert 步骤失败时按失败策略处理", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.detect.os@1.0.0"
                register = "os"

                [[steps]]
                assert = "${pkg.os.build} > 99999999"
                message = "构建号必须大于一个不可能的值"
                on_error = "continue"

                [[steps]]
                uses = "envstation.detect.arch@1.0.0"
                """;

            var outcome = Run(toml).Outcome;

            Assert.True(outcome.Succeeded, "on_error 为 continue 时应继续执行并最终成功");
            Assert.True(outcome.SkippedSteps >= 1, "失败的断言应被计为跳过");
        });
    }

    // ══════════════════════════ 失败策略 ══════════════════════════

    private static void ErrorPolicyCases(TestHarness h)
    {
        const string failingFirstStep = """
            [[steps]]
            uses = "envstation.env.unset@1.0.0"
            [steps.with]
            scope = "user"
            name = "WF_FAIL_UNSET"
            expect_value = "this-does-not-match"
            """;

        h.Case("WF-11", "on_error: abort —— 中止但不回滚", () =>
        {
            var toml = failingFirstStep + """

                [[steps]]
                uses = "envstation.detect.os@1.0.0"
                """;

            var document = Parse(toml);
            var (interpreter, context, _) = Prepare();
            context.VariableTable.SetPackageVariable("dummy", "1");

            // 让第一步真的失败：变量不存在时 env.unset 是幂等成功，
            // 因此改用一个必然失败的断言步骤来构造 abort 场景。
            var abortDoc = Parse("""
                [[steps]]
                assert = "false == true"
                message = "故意的失败"
                on_error = "abort"

                [[steps]]
                uses = "envstation.detect.os@1.0.0"
                """);

            var outcome = interpreter
                .RunAsync(abortDoc, context, new CoreWf.WorkflowOptions())
                .AsTask().GetAwaiter().GetResult();

            Assert.False(outcome.Succeeded, "应失败");
            Assert.False(outcome.RolledBack, "abort 策略不应回滚");
            Assert.Contains("未回滚", outcome.Message, "应明确告知用户修改仍在原处");
            Assert.Equal(0, context.Ledger.Invocations.Count, "后续步骤不应执行");

            _ = document;
        });

        h.Case("WF-12", "★on_error: rollback（默认）—— 自动回滚到修改前", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.env.set@1.0.0"
                [steps.with]
                scope = "user"
                name = "WF_ROLLBACK_ME"
                value = "written-by-workflow"

                [[steps]]
                assert = "false == true"
                message = "故意的失败，用于触发回滚"
                """;

            var sandbox = new SandboxEnvironment();
            var outcome = Run(toml, sandbox: sandbox).Outcome;

            Assert.False(outcome.Succeeded, "应失败");
            Assert.True(outcome.RolledBack, $"默认策略应回滚：{outcome.Message}");
            Assert.Null(sandbox.Peek(AbsEnv.EnvScope.User, "WF_ROLLBACK_ME"),
                "回滚后变量必须不存在——这正是失败零污染承诺的落点");
        });

        h.Case("WF-13", "on_error: continue —— 忽略失败并继续", () =>
        {
            const string toml = """
                [[steps]]
                assert = "false == true"
                message = "这一步失败但不要紧"
                on_error = "continue"

                [[steps]]
                uses = "envstation.detect.os@1.0.0"
                register = "os"
                """;

            var (outcome, context) = Run(toml);

            Assert.True(outcome.Succeeded, $"continue 策略下应最终成功：{outcome.Message}");
            Assert.Equal(1, context.Ledger.Invocations.Count, "后续步骤应被执行");
        });
    }

    // ══════════════════════════ 安全与限制 ══════════════════════════

    private static void SafetyCases(TestHarness h)
    {
        h.Case("WF-14", "★能力未授权：动作被拒绝而不是跳过", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.env.set@1.0.0"
                [steps.with]
                scope = "user"
                name = "WF_NO_CAP"
                value = "x"
                """;

            var sandbox = new SandboxEnvironment();
            var outcome = Run(
                toml,
                sandbox: sandbox,
                capabilities: [AbsActions.CapabilityIds.Inspect]).Outcome;

            Assert.False(outcome.Succeeded, "缺少能力应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.CapabilityDenied, outcome.ErrorCode, "应返回能力拒绝码");
            Assert.Equal(0, sandbox.Count(AbsEnv.EnvScope.User), "不应有任何写入");
        });

        h.Case("WF-15", "★parallel：拒绝包含不可并行动作的分支", () =>
        {
            const string toml = """
                [[steps]]
                [[steps.parallel]]
                uses = "envstation.detect.os@1.0.0"
                [[steps.parallel]]
                uses = "envstation.env.set@1.0.0"
                """;

            // env.set 的 IsParallelSafe 为 false，因此必须在执行前被拒绝。
            var outcome = Run(toml).Outcome;

            Assert.False(outcome.Succeeded, "含不可并行动作的 parallel 应被拒绝");
            Assert.Contains("未声明可并行", outcome.Message, "应说明是哪一个动作的问题");
        });

        h.Case("WF-16", "parallel：全部为可并行动作时正常执行", () =>
        {
            const string toml = """
                [[steps]]
                [[steps.parallel]]
                uses = "envstation.detect.os@1.0.0"
                [[steps.parallel]]
                uses = "envstation.detect.arch@1.0.0"
                """;

            var (outcome, context) = Run(toml);

            Assert.True(outcome.Succeeded, $"应成功：{outcome.Message}");
            Assert.Equal(2, context.Ledger.Invocations.Count, "两个分支都应执行");
        });

        h.Case("WF-17", "★变量未定义：插值失败并给出可操作提示", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.env.set@1.0.0"
                [steps.with]
                scope = "user"
                name = "WF_UNDEF"
                value = "${pkg.never_set}"
                """;

            var sandbox = new SandboxEnvironment();
            var outcome = Run(toml, sandbox: sandbox).Outcome;

            Assert.False(outcome.Succeeded, "未定义变量应导致失败");
            Assert.Contains("未定义", outcome.Message, "应说明现象");
            Assert.Equal(0, sandbox.Count(AbsEnv.EnvScope.User), "失败发生在写入之前，不应有副作用");
        });

        h.Case("WF-18", "★插值只产生数据：变量值里的命令元字符不会被解释", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.env.set@1.0.0"
                [steps.with]
                scope = "user"
                name = "WF_INJECT"
                value = "${pkg.evil}"
                """;

            var sandbox = new SandboxEnvironment();
            var document = Parse(toml);
            var (interpreter, context, _) = Prepare(sandbox: sandbox);
            context.VariableTable.SetPackageVariable("evil", "value & calc.exe | whoami > out.txt");

            var outcome = interpreter
                .RunAsync(document, context, new CoreWf.WorkflowOptions())
                .AsTask().GetAwaiter().GetResult();

            Assert.True(outcome.Succeeded, $"应成功：{outcome.Message}");
            Assert.Equal(
                "value & calc.exe | whoami > out.txt",
                sandbox.Peek(AbsEnv.EnvScope.User, "WF_INJECT"),
                "变量值必须原样作为数据处理——插值永远不进入命令行");
        });

        h.Case("WF-19", "★动作契约哈希不符：解析阶段就拒绝", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.detect.os@1.0.0"
                pin = { hash = "sha256:0000000000000000000000000000000000000000000000000000000000000000" }
                """;

            var outcome = Run(toml, stampPins: false).Outcome;

            Assert.False(outcome.Succeeded, "哈希不符应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.ActionPinMismatch, outcome.ErrorCode, "应返回 pin 不符码");
        });

        h.Case("WF-20", "★累计步骤数上限：防止用嵌套循环构造超长执行", () =>
        {
            const string toml = """
                [[steps]]
                foreach = "${pkg.items}"
                as = "a"
                max = 100
                [[steps.do]]
                foreach = "${pkg.items}"
                as = "b"
                max = 100
                [[steps.do.do]]
                uses = "envstation.ui.progress@1.0.0"
                [steps.do.do.with]
                percent = 1
                """;

            var document = Parse(toml);
            var (interpreter, context, _) = Prepare();
            context.VariableTable.SetPackageVariable("items", string.Join('\n', Enumerable.Range(0, 50).Select(i => $"i{i}")));

            var outcome = interpreter
                .RunAsync(document, context, new CoreWf.WorkflowOptions(MaxTotalSteps: 200))
                .AsTask().GetAwaiter().GetResult();

            Assert.False(outcome.Succeeded, "超过累计上限应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.WorkflowLoopLimitExceeded, outcome.ErrorCode, "应返回循环超限码");
            Assert.True(context.Ledger.Invocations.Count <= 200, "实际执行数不得超过上限");
        });

        h.Case("WF-21", "账本与报告：运行结束后可生成完整报告", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.detect.os@1.0.0"
                register = "os"

                [[steps]]
                uses = "envstation.env.set@1.0.0"
                [steps.with]
                scope = "user"
                name = "WF_REPORT"
                value = "v"

                [[steps]]
                uses = "envstation.report.generate@1.0.0"
                [steps.with]
                format = "markdown"
                """;

            var sandbox = new SandboxEnvironment();
            var ledger = new CoreActions.RunLedger
            {
                RunId = "run-wf-report",
                PackageId = "x-test.workflow",
                StartedAt = DateTimeOffset.Now,
            };

            var outcome = Run(toml, sandbox: sandbox, ledger: ledger).Outcome;

            Assert.True(outcome.Succeeded, $"应成功：{outcome.Message}");
            Assert.Equal(3, ledger.Invocations.Count, "账本应记录 3 次调用");

            var report = ledger.Invocations[^1].Outputs["content"];
            Assert.Contains("envstation.env.set", report, "报告应含环境变量修改步骤");
            Assert.Contains("envstation.detect.os", report, "报告应含探测步骤");
            Assert.Contains("WF_REPORT", report, "报告应含被触碰的变量名");
        });

        h.Case("WF-22", "★失败时账本仍完整：便于用户看清做到哪一步了", () =>
        {
            const string toml = """
                [[steps]]
                uses = "envstation.detect.os@1.0.0"

                [[steps]]
                assert = "false == true"
                message = "故意失败"
                """;

            var ledger = new CoreActions.RunLedger
            {
                RunId = "run-wf-fail",
                PackageId = "x-test.workflow",
                StartedAt = DateTimeOffset.Now,
            };

            var outcome = Run(toml, ledger: ledger).Outcome;

            Assert.False(outcome.Succeeded, "应失败");
            Assert.Equal(1, ledger.Invocations.Count, "失败前的成功步骤必须留在账本里");
            Assert.NotNull(ledger.Outcome, "账本应记录最终结论");
            Assert.Contains("失败", ledger.Outcome!, "结论应说明失败");
        });
    }
}
