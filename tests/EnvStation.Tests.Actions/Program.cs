using System.Collections.Immutable;
using EnvStation.TestKit;

namespace EnvStation.Tests.Actions;

/// <summary>
/// 动作注册表 / 参数绑定 / 执行管线 测试。
///
/// <para><b>这一套用例守的是什么</b>：需求 A1 说"第三方包永远无法执行任意命令"，
/// 需求 A2 说"未授权的能力一律拒绝执行，不是跳过"。这两句话最终都落在一个地方——
/// <b>所有动作调用都必须经过执行管线</b>。本套用例逐条钉住管线的每个拒绝点，
/// 以及参数绑定这一层"类型错误在校验期就被拒绝"的承诺。</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("动作层测试");
        Console.WriteLine("（注册表 · 参数绑定 · 执行管线 · 路径守卫 · 配额 · 探测动作）");
        Console.WriteLine();

        var h = new TestHarness("动作层");

        RegistryCases(h);
        BinderCases(h);
        PipelineCases(h);
        GuardCases(h);
        QuotaCases(h);
        AuditCases(h);
        VariableCases(h);
        DetectCases(h);

        var code = h.Summarize();

        var jsonPath = TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return code;
    }

    // ══════════════════════════ 测试用动作实现 ══════════════════════════

    /// <summary>
    /// 可编程的测试动作：行为由构造函数注入，便于精确构造各种失败路径。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>实现 <see cref="IAction"/> 而由 <see cref="HarnessAction"/> 包装：
    /// 这样测试可以绕过"动作只能经由执行管线调用"的约束去直接测绑定与守卫，
    /// 而管线测试仍然走真实的 <see cref="IAction"/> 入口。
    /// </remarks>
    private sealed class ProbeAction(
        string actionId,
        string capability,
        bool requiresUser = false,
        int timeoutSeconds = 30,
        Func<CoreActions.ActionExecutionContext, AbsActions.ActionArguments, CancellationToken, ValueTask<AbsActions.ActionResult>>? body = null)
    {
        internal AbsActions.ActionDescriptor Descriptor { get; } = new(
            actionId,
            "1.0.0",
            capability,
            "测试动作",
            IsIdempotent: true,
            IsParallelSafe: true,
            HasInverse: false,
            RequiresUserPresence: requiresUser,
            DefaultTimeoutSeconds: timeoutSeconds,
            TouchedResources: [],
            Parameters: [.. ProbeSpecs]);

        internal ValueTask<AbsActions.ActionResult> RunAsync(
            CoreActions.ActionExecutionContext context,
            AbsActions.ActionArguments arguments,
            CancellationToken cancellationToken) =>
            body is null
                ? ValueTask.FromResult(AbsActions.ActionResult.Ok("ok"))
                : body(context, arguments, cancellationToken);
    }

    /// <summary>把 ProbeAction 适配成管线可调用的 IAction。</summary>
    private sealed class HarnessAction(ProbeAction inner) : AbsActions.IAction
    {
        public AbsActions.ActionDescriptor Descriptor => inner.Descriptor;

        public ValueTask<AbsActions.ActionResult> ExecuteAsync(
            AbsActions.IActionContext context,
            AbsActions.ActionArguments arguments,
            CancellationToken cancellationToken) =>
            inner.RunAsync((CoreActions.ActionExecutionContext)context, arguments, cancellationToken);
    }

    private static readonly AbsActions.ParameterSpec[] ProbeSpecs =
    [
        new("text", AbsActions.ParameterType.String, false, "任意文本", MaxLength: 64),
        new("count", AbsActions.ParameterType.Integer, false, "计数", Minimum: 1, Maximum: 10),
        new("mode", AbsActions.ParameterType.Enum, false, "模式", AllowedValues: ["fast", "slow"]),
        new("flag", AbsActions.ParameterType.Boolean, false, "开关", DefaultValue: "false"),
        new("secret_token", AbsActions.ParameterType.String, false, "敏感令牌", MaxLength: 128, IsSecret: true),
        new("path", AbsActions.ParameterType.Path, false, "路径", MaxLength: 512),
        new("tags", AbsActions.ParameterType.StringArray, false, "标签"),
    ];

    private static AbsActions.ActionDescriptor ProbeDescriptor(
        string actionId = "envstation.test.echo",
        string capability = AbsActions.CapabilityIds.Inspect,
        bool requiresUser = false,
        int timeoutSeconds = 30) =>
        new(actionId, "1.0.0", capability, "测试动作",
            IsIdempotent: true, IsParallelSafe: true, HasInverse: false,
            RequiresUserPresence: requiresUser, DefaultTimeoutSeconds: timeoutSeconds,
            TouchedResources: [], Parameters: [.. ProbeSpecs]);

    // ══════════════════════════ 注册表 ══════════════════════════

    private static void RegistryCases(TestHarness h)
    {
        h.Case("AC-01", "内建注册表可创建且无阻断问题", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CoreActions.ActionRegistry.CreateDefault(bag);
            Assert.True(r.IsSuccess, $"内建动作应全部合规，实际：{bag.ToReport().ToText()}");

            // 断言"至少实现了多少"而不是精确数量：
            // 精确数量会在每次新增动作时都变成一次无意义的用例修改，反而让人习惯性地改断言。
            // 真正该被钉住的是"各组的关键动作确实存在"，那由下面的逐组检查负责。
            Assert.True(r.Value.Count >= 74, $"动作库应已补齐（需求 21.2 规划 74 个），实际 {r.Value.Count}");
            Assert.False(bag.HasBlockers, "不应有阻断问题");

            string[] expected =
            [
                "envstation.detect.runtime", "envstation.detect.arch", "envstation.detect.os",
                "envstation.detect.command", "envstation.detect.conflict", "envstation.detect.disk",
                "envstation.detect.deps", "envstation.detect.network", "envstation.detect.env",
                "envstation.net.download", "envstation.net.head",
                "envstation.net.verify_hash", "envstation.net.fetch_text",
                "envstation.archive.extract", "envstation.archive.list", "envstation.archive.create",
                "envstation.env.backup", "envstation.env.set", "envstation.env.unset",
                "envstation.env.get", "envstation.env.diff", "envstation.env.restore", "envstation.env.validate",
                "envstation.path.ensure", "envstation.path.remove", "envstation.path.dedupe",
                "envstation.path.prioritize", "envstation.path.clean", "envstation.path.validate",
                "envstation.path.ensure_shim",
                "envstation.verify.version_output", "envstation.verify.command_resolves",
                "envstation.verify.env_effective", "envstation.verify.file_exists",
                "envstation.verify.conflict_clear", "envstation.verify.assert", "envstation.verify.config_restored",
                "envstation.ui.notify", "envstation.ui.prompt", "envstation.ui.progress",
                "envstation.report.generate", "envstation.report.export_diagnostics",
                "envstation.cleanup.temp", "envstation.cleanup.remove_package",
                "envstation.fs.copy", "envstation.fs.move",
                // A04 包管理器与安装
                "envstation.pkg.install", "envstation.pkg.uninstall", "envstation.pkg.list",
                "envstation.local.install", "envstation.local.register", "envstation.fs.link",
                // A07 运行时版本管理
                "envstation.runtime.install", "envstation.runtime.switch", "envstation.runtime.list",
                "envstation.runtime.remove", "envstation.runtime.components",
                // A08 配置与镜像源
                "envstation.config.detect", "envstation.config.backup", "envstation.config.verify_syntax",
                "envstation.config.set_kv", "envstation.mirror.list", "envstation.mirror.test",
                "envstation.mirror.set", "envstation.mirror.restore", "envstation.xml.set_mirror",
                "envstation.xml.set_repository", "envstation.xml.set_proxy", "envstation.xml.validate",
                "envstation.gradle.set_mirror", "envstation.file.write_template", "envstation.file.marked_block_remove",
                // A09 镜像验证
                "envstation.verify.mirror_effective", "envstation.verify.mirror_integrity",
            ];

            var missing = expected.Where(id => !r.Value.TryGet(id, out _)).ToArray();
            Assert.True(missing.Length == 0, $"以下动作应当已登记但缺失：{string.Join("、", missing)}");
        });

        h.Case("AC-02", "全部内建动作 ID 均为官方三段式且能力已知", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CoreActions.ActionRegistry.CreateDefault(bag);
            Assert.True(r.IsSuccess, "注册表应创建成功");

            foreach (var d in r.Value.Descriptors)
            {
                Assert.True(d.ActionId.StartsWith("envstation.", StringComparison.Ordinal), $"动作 {d.ActionId} 应使用官方前缀");
                Assert.Equal(3, d.ActionId.Split('.').Length, $"动作 {d.ActionId} 应是三段式");
                Assert.True(AbsActions.Capabilities.IsKnown(d.CapabilityId), $"动作 {d.ActionId} 的能力 {d.CapabilityId} 应在官方能力表内");
            }
        });

        h.Case("AC-03", "动作契约哈希稳定且随契约变化", () =>
        {
            var a = CoreActions.ActionRegistry.ComputeContractHash(ProbeDescriptor());
            var b = CoreActions.ActionRegistry.ComputeContractHash(ProbeDescriptor());
            Assert.Equal(a, b, "同一契约的哈希必须可复现");

            var changed = CoreActions.ActionRegistry.ComputeContractHash(
                ProbeDescriptor() with { DefaultTimeoutSeconds = 31 });
            Assert.NotEqual(a, changed, "超时变化应改变契约哈希");

            var changedCapability = CoreActions.ActionRegistry.ComputeContractHash(
                ProbeDescriptor() with { CapabilityId = EnvStation.Abstractions.Actions.CapabilityIds.Cleanup });
            Assert.NotEqual(a, changedCapability, "能力变化应改变契约哈希");

            Assert.True(a.StartsWith("sha256:", StringComparison.Ordinal), "哈希应带 sha256: 前缀");
            Assert.Equal(71, a.Length, "sha256: + 64 位十六进制");
        });

        h.Case("AC-04", "注册表拒绝非官方命名空间的动作", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CoreActions.ActionRegistry.Create([new HarnessAction(new ProbeAction("evil.env.set", AbsActions.CapabilityIds.Inspect))], bag);
            Assert.True(r.IsFailure, "非官方命名空间应被拒绝");
            Assert.Contains("不是合法的官方三段式 ID", bag.ToReport().ToText(), "应说明命名规则");
        });

        h.Case("AC-05", "注册表拒绝未知能力的动作", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CoreActions.ActionRegistry.Create([new HarnessAction(new ProbeAction("envstation.test.echo", "CAP.REGISTRY.WRITE"))], bag);
            Assert.True(r.IsFailure, "未知能力应被拒绝");
            Assert.Contains("不在官方能力表内", bag.ToReport().ToText(), "应说明能力表封闭");
        });

        h.Case("AC-06", "同一动作重复登记被拒绝", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var r = CoreActions.ActionRegistry.Create(
                [new HarnessAction(new ProbeAction("envstation.test.echo", AbsActions.CapabilityIds.Inspect)), new HarnessAction(new ProbeAction("envstation.test.echo", AbsActions.CapabilityIds.Inspect))],
                bag);
            Assert.True(r.IsFailure, "重复登记应被拒绝");
            Assert.Contains("被登记了多次", bag.ToReport().ToText(), "应说明原因");
        });

        h.Case("AC-07", "解析：版本不符 / 缺 hash / hash 不符 均被拒绝", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var registry = CoreActions.ActionRegistry.CreateDefault(bag).Value;
            const string id = "envstation.detect.os";
            var hash = registry.GetContractHash(id, "1.0.0");
            Assert.NotNull(hash, "应能取到契约哈希");

            Assert.True(
                registry.Resolve(new AbsPkg.ActionReference(id, "1.0.0", hash)).IsSuccess,
                "版本与哈希都正确时应解析成功");

            var wrongVersion = registry.Resolve(new AbsPkg.ActionReference(id, "2.0.0", hash));
            Assert.True(wrongVersion.IsFailure, "版本不符应被拒绝");
            Assert.Equal(Abs.EnvStationErrorCodes.ActionNotFound, wrongVersion.Error!.Code, "应返回动作未找到码");

            var noHash = registry.Resolve(new AbsPkg.ActionReference(id, "1.0.0", null));
            Assert.True(noHash.IsFailure, "缺 hash 应被拒绝（需求 S3）");
            Assert.Equal(Abs.EnvStationErrorCodes.ActionPinMismatch, noHash.Error!.Code, "应返回 pin 不符码");

            var badHash = registry.Resolve(new AbsPkg.ActionReference(id, "1.0.0", "sha256:" + new string('0', 64)));
            Assert.True(badHash.IsFailure, "哈希不符应被拒绝");
            Assert.Contains("契约哈希与包内声明不符", badHash.Error!.Message, "应说明原因");

            var unknown = registry.Resolve(new AbsPkg.ActionReference("envstation.nope.nope", "1.0.0", hash));
            Assert.True(unknown.IsFailure, "未知动作应被拒绝");
        });

        h.Case("AC-08", "哈希格式校验：必须显式带算法前缀", () =>
        {
            Assert.NotNull(CoreActions.ActionRegistry.NormalizeHash("sha256:" + new string('a', 64)), "合法哈希应被接受");

            // 刻意不接受裸哈希：显式算法标识可避免"哈希算法混淆"这类问题，
            // 也让错误信息能直接告诉包作者该写什么。
            Assert.Null(CoreActions.ActionRegistry.NormalizeHash(new string('a', 64)), "裸哈希应被拒绝（必须写 sha256:）");
            Assert.Null(CoreActions.ActionRegistry.NormalizeHash("md5:abc"), "非 sha256 应被拒绝");
            Assert.Null(CoreActions.ActionRegistry.NormalizeHash("sha256:xyz"), "长度不足应被拒绝");
            Assert.Null(CoreActions.ActionRegistry.NormalizeHash("sha256:" + new string('A', 64)), "大写十六进制应被拒绝（保持唯一约定）");
        });
    }

    // ══════════════════════════ 参数绑定 ══════════════════════════

    private static AbsDiag.FindingBag Bind(
        out Result<CoreActions.BoundArguments> result,
        params (string Name, AbsPkg.ScriptValue Value)[] values)
    {
        var bag = new AbsDiag.FindingBag();
        var dict = values.ToDictionary(static v => v.Name, static v => v.Value, StringComparer.Ordinal);
        result = CoreActions.ActionArgumentsBinder.Bind(ProbeDescriptor(), dict, bag, "workflow.toml:7");
        return bag;
    }

    private static void BinderCases(TestHarness h)
    {
        h.Case("AC-09", "绑定：合法参数全部转换成功，未提供的参数由默认值补齐", () =>
        {
            var bag = Bind(out var r,
                ("text", new AbsPkg.ScriptString("hello")),
                ("count", new AbsPkg.ScriptInteger(5)),
                ("mode", new AbsPkg.ScriptString("fast")),
                ("tags", new AbsPkg.ScriptArray([new AbsPkg.ScriptString("a"), new AbsPkg.ScriptString("b")])));

            Assert.True(r.IsSuccess, $"应绑定成功，实际：{bag.ToReport().ToText()}");
            Assert.Equal("hello", r.Value.Arguments.GetString("text"), "字符串");
            Assert.Equal(5L, r.Value.Arguments.GetInt64("count"), "整数");
            Assert.Equal(2, r.Value.Arguments.GetStringArray("tags").Length, "数组");

            // flag 未提供，但有默认值 false —— 应被补齐并记录下来（干跑预览需要标出"这一项是默认值"）
            Assert.Equal(false, r.Value.Arguments.GetBoolean("flag"), "未提供的布尔参数应取默认值");
            Assert.Equal(1, r.Value.UsedDefaults.Length, "应记录 1 个默认值补齐项");
            Assert.Equal("flag", r.Value.UsedDefaults[0], "补齐项应是 flag");
        });

        h.Case("AC-10", "绑定：未知参数名被拒绝（防拼写错误静默失效）", () =>
        {
            var bag = Bind(out var r, ("txet", new AbsPkg.ScriptString("typo")));
            Assert.True(r.IsFailure, "未知参数应被拒绝");
            Assert.Contains("不接受参数 txet", bag.ToReport().ToText(), "应指出具体参数名");
            Assert.Contains("支持的参数", bag.ToReport().ToText(), "应列出支持的参数");
        });

        h.Case("AC-11", "绑定：类型不符被拒绝", () =>
        {
            var bag = Bind(out var r, ("count", new AbsPkg.ScriptString("5")));
            Assert.True(r.IsFailure, "字符串冒充整数应被拒绝");
            Assert.Contains("期望整数", bag.ToReport().ToText(), "应说明期望类型");
        });

        h.Case("AC-12", "绑定：数值越界被拒绝", () =>
        {
            var bag = Bind(out var r, ("count", new AbsPkg.ScriptInteger(99)));
            Assert.True(r.IsFailure, "越界整数应被拒绝");
            Assert.Contains("不大于 10", bag.ToReport().ToText(), "应说明范围");
        });

        h.Case("AC-13", "绑定：枚举取值必须在允许集合内", () =>
        {
            var bag = Bind(out var r, ("mode", new AbsPkg.ScriptString("turbo")));
            Assert.True(r.IsFailure, "非法枚举值应被拒绝");
            Assert.Contains("fast、slow", bag.ToReport().ToText(), "应列出允许值");
        });

        h.Case("AC-14", "绑定：字符串超长被拒绝", () =>
        {
            var bag = Bind(out var r, ("text", new AbsPkg.ScriptString(new string('x', 200))));
            Assert.True(r.IsFailure, "超长字符串应被拒绝");
            Assert.Contains("长度不超过 64", bag.ToReport().ToText(), "应说明长度上限");
        });

        h.Case("AC-15", "绑定：数组不能冒充字符串（分离符语义必须显式定义）", () =>
        {
            var bag = Bind(out var r, ("text", new AbsPkg.ScriptArray([new AbsPkg.ScriptString("a")])));
            Assert.True(r.IsFailure, "数组冒充字符串应被拒绝");
            Assert.Contains("期望字符串", bag.ToReport().ToText(), "应说明期望类型");
        });

        h.Case("AC-16", "绑定：标量可无损转字符串", () =>
        {
            var bag = Bind(out var r, ("text", new AbsPkg.ScriptInteger(42)));
            Assert.True(r.IsSuccess, $"标量转字符串应被允许，实际：{bag.ToReport().ToText()}");
            Assert.Equal("42", r.Value.Arguments.GetString("text"), "应转换为十进制文本");
        });

        h.Case("AC-17", "绑定：敏感参数被标记", () =>
        {
            var bag = Bind(out var r, ("secret_token", new AbsPkg.ScriptString("t0ken")));
            Assert.True(r.IsSuccess, $"应绑定成功，实际：{bag.ToReport().ToText()}");
            Assert.True(r.Value.SecretNames.Contains("secret_token"), "敏感参数应被标记");
        });

        h.Case("AC-18", "绑定：未知参数与类型错误一次全部报出", () =>
        {
            var bag = Bind(out var r,
                ("nope", new AbsPkg.ScriptString("x")),
                ("count", new AbsPkg.ScriptString("not-a-number")),
                ("mode", new AbsPkg.ScriptString("turbo")));
            Assert.True(r.IsFailure, "应判失败");
            Assert.True(bag.BlockCount >= 3, $"应一次报出 3 项问题，实际 {bag.BlockCount}");
        });
    }

    // ══════════════════════════ 执行管线 ══════════════════════════

    private static CoreActions.ActionExecutionContext MakeContext(
        IEnumerable<string>? roots = null,
        IEnumerable<string>? capabilities = null,
        bool unattended = false,
        CoreActions.IAuditSink? audit = null,
        CoreActions.VariableTable? variables = null,
        CoreActions.QuotaMeter? quota = null)
    {
        return new CoreActions.ActionExecutionContext(
            "x-test.pkg",
            "run-0001",
            new AbsActions.CapabilitySet(capabilities ?? []),
            roots ?? [],
            variables ?? new CoreActions.VariableTable(),
            quota ?? new CoreActions.QuotaMeter(CoreActions.ResourceQuota.Default),
            audit ?? new CoreActions.MemoryAuditSink(),
            unattended: unattended);
    }

    private static void PipelineCases(TestHarness h)
    {
        h.Case("AC-19", "管线：未授权能力被拒绝（拒绝而非跳过）", () =>
        {
            var audit = new CoreActions.MemoryAuditSink();
            var ctx = MakeContext(audit: audit);
            var action = new HarnessAction(new ProbeAction("envstation.test.echo", AbsActions.CapabilityIds.EnvironmentUser));

            var bag = new AbsDiag.FindingBag();
            var bound = CoreActions.ActionArgumentsBinder.Bind(action.Descriptor, new Dictionary<string, AbsPkg.ScriptValue>(), bag).Value;

            var result = CoreActions.ActionExecutor.ExecuteAsync(action, ctx, bound).AsTask().GetAwaiter().GetResult();

            Assert.False(result.Success, "未授权能力应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.CapabilityDenied, result.ErrorCode, "应返回能力拒绝码");
            Assert.Equal(1, audit.Where("action.rejected").Count(), "应有拒绝审计记录");
            Assert.Equal(0, audit.Where("action.begin").Count(), "被拒绝的动作不应产生 begin 记录");
        });

        h.Case("AC-20", "管线：无人值守时交互动作被拒绝", () =>
        {
            var ctx = MakeContext(
                capabilities: [AbsActions.CapabilityIds.UserInteraction],
                unattended: true);
            var action = new HarnessAction(new ProbeAction("envstation.test.echo", AbsActions.CapabilityIds.UserInteraction, requiresUser: true));

            var bag = new AbsDiag.FindingBag();
            var bound = CoreActions.ActionArgumentsBinder.Bind(action.Descriptor, new Dictionary<string, AbsPkg.ScriptValue>(), bag).Value;

            var result = CoreActions.ActionExecutor.ExecuteAsync(action, ctx, bound).AsTask().GetAwaiter().GetResult();
            Assert.False(result.Success, "无人值守时交互动作应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.UnattendedInteraction, result.ErrorCode, "应返回无人值守码");
        });

        h.Case("AC-21", "★管线：动作抛出的异常被转为结构化失败（不穿透）", () =>
        {
            // 对应 M0 产品缺陷 D-1 的教训：异常穿透会跳过调用方的回滚逻辑。
            var audit = new CoreActions.MemoryAuditSink();
            var ctx = MakeContext(capabilities: [AbsActions.CapabilityIds.Inspect], audit: audit);
            var action = new HarnessAction(new ProbeAction(
                "envstation.test.echo",
                AbsActions.CapabilityIds.Inspect,
                body: (_, _, _) => throw new InvalidOperationException("内部炸了")));

            var bag = new AbsDiag.FindingBag();
            var bound = CoreActions.ActionArgumentsBinder.Bind(action.Descriptor, new Dictionary<string, AbsPkg.ScriptValue>(), bag).Value;

            var result = CoreActions.ActionExecutor.ExecuteAsync(action, ctx, bound).AsTask().GetAwaiter().GetResult();

            Assert.False(result.Success, "异常应被转为失败结果");
            Assert.Equal(Abs.EnvStationErrorCodes.ActionFailed, result.ErrorCode, "应返回动作失败码");
            Assert.Contains("内部炸了", result.Message, "应保留异常信息以便诊断");
            Assert.Equal(1, audit.Where("action.end").Count(), "异常路径也必须写收尾审计");
        });

        h.Case("AC-22", "管线：动作超时被中止", () =>
        {
            var ctx = MakeContext(capabilities: [AbsActions.CapabilityIds.Inspect]);
            var action = new HarnessAction(new ProbeAction(
                "envstation.test.echo",
                AbsActions.CapabilityIds.Inspect,
                timeoutSeconds: 1,
                body: async (_, _, ct) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    return AbsActions.ActionResult.Ok("不该到这里");
                }));

            var bag = new AbsDiag.FindingBag();
            var bound = CoreActions.ActionArgumentsBinder.Bind(action.Descriptor, new Dictionary<string, AbsPkg.ScriptValue>(), bag).Value;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = CoreActions.ActionExecutor.ExecuteAsync(action, ctx, bound).AsTask().GetAwaiter().GetResult();
            clock.Stop();

            Assert.False(result.Success, "超时应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.ActionTimeout, result.ErrorCode, "应返回超时码");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"应在超时后立即返回，实际耗时 {clock.Elapsed.TotalSeconds:F1} 秒");
        });

        h.Case("AC-23", "管线：成功路径写入 begin / end 审计", () =>
        {
            var audit = new CoreActions.MemoryAuditSink();
            var ctx = MakeContext(capabilities: [AbsActions.CapabilityIds.Inspect], audit: audit);
            var action = new HarnessAction(new ProbeAction("envstation.test.echo", AbsActions.CapabilityIds.Inspect));

            var bag = new AbsDiag.FindingBag();
            var bound = CoreActions.ActionArgumentsBinder.Bind(action.Descriptor, new Dictionary<string, AbsPkg.ScriptValue>(), bag).Value;

            var (result, invocation) = CoreActions.ActionExecutor
                .ExecuteWithRecordAsync(action, ctx, bound).AsTask().GetAwaiter().GetResult();

            Assert.True(result.Success, "应成功");
            Assert.Equal(1, audit.Where("action.begin").Count(), "应有 begin 记录");
            Assert.Equal(1, audit.Where("action.end").Count(), "应有 end 记录");
            Assert.Equal("envstation.test.echo", invocation.ActionId, "调用记录应含动作 ID");
            Assert.True(invocation.Elapsed >= TimeSpan.Zero, "调用记录应含耗时");
        });

        h.Case("AC-24", "★管线：审计中的敏感参数已脱敏", () =>
        {
            var audit = new CoreActions.MemoryAuditSink();
            var ctx = MakeContext(capabilities: [AbsActions.CapabilityIds.Inspect], audit: audit);
            var action = new HarnessAction(new ProbeAction("envstation.test.echo", AbsActions.CapabilityIds.Inspect));

            var bag = new AbsDiag.FindingBag();
            var bound = CoreActions.ActionArgumentsBinder.Bind(
                action.Descriptor,
                new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
                {
                    ["secret_token"] = new AbsPkg.ScriptString("SUPER-SECRET-VALUE"),
                    ["text"] = new AbsPkg.ScriptString("visible"),
                },
                bag).Value;

            CoreActions.ActionExecutor.ExecuteAsync(action, ctx, bound).AsTask().GetAwaiter().GetResult();

            var begin = audit.Where("action.begin").Single();
            Assert.Equal("***", begin.Fields["args"].Split(", ").Single(static p => p.StartsWith("secret_token=", StringComparison.Ordinal))["secret_token=".Length..], "敏感参数必须显示为 ***");
            Assert.Contains("visible", begin.Fields["args"], "非敏感参数应正常记录");
            Assert.NotContains("SUPER-SECRET-VALUE", begin.Fields["args"], "敏感值绝不能出现在审计里");
        });
    }

    // ══════════════════════════ 路径守卫 ══════════════════════════

    private static void GuardCases(TestHarness h)
    {
        var root = Path.Combine(Path.GetTempPath(), "envstation-guard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        try
        {
            var ctx = MakeContext(roots: [root]);

            h.Case("AC-25", "路径守卫：授权根内允许", () =>
            {
                var ok = ctx.GuardPath(Path.Combine(root, "bin"), "path");
                Assert.True(ok.IsSuccess, $"根内路径应允许，实际：{ok.Error?.Message}");
                Assert.True(ctx.IsUnderAuthorizedRoot(ok.Value), "返回值应落在授权根内");
            });

            h.Case("AC-26", "★路径守卫：越界路径被拒绝", () =>
            {
                var outside = Path.Combine(Path.GetTempPath(), "somewhere-else");
                var r = ctx.GuardPath(outside, "path");
                Assert.True(r.IsFailure, "越界路径应被拒绝");
                Assert.Equal(Abs.EnvStationErrorCodes.PathOutsideAuthorizedRoot, r.Error!.Code, "应返回越界码");
                Assert.Contains("不在本次运行的授权根目录内", r.Error.Message, "应说明原因");
            });

            h.Case("AC-27", "★路径守卫：前缀相似的兄弟目录不能被误判为根内", () =>
            {
                // 经典漏洞：以字符串前缀判断归属，"C:\app-evil" 会被当成 "C:\app" 的子目录。
                var sibling = root + "-evil";
                var r = ctx.GuardPath(Path.Combine(sibling, "x"), "path");
                Assert.True(r.IsFailure, "同前缀兄弟目录应被拒绝");
            });

            h.Case("AC-28", "★路径守卫：.. 穿越被规范化后拒绝", () =>
            {
                var traversal = Path.Combine(root, "..", "escaped");
                var r = ctx.GuardPath(traversal, "path");
                Assert.True(r.IsFailure, "上跳穿越应被拒绝");
            });

            h.Case("AC-29", "路径守卫：未解析变量与空字符被拒绝", () =>
            {
                Assert.True(ctx.GuardPath("${install_dir}/bin", "path").IsFailure, "未解析变量应被拒绝");
                Assert.True(ctx.GuardPath("bad\0path", "path").IsFailure, "空字符应被拒绝");
                Assert.True(ctx.GuardPath("   ", "path").IsFailure, "空白应被拒绝");
            });

            h.Case("AC-30", "路径守卫：未授权任何目录时一律拒绝", () =>
            {
                var empty = MakeContext();
                var r = empty.GuardPath(Path.Combine(root, "x"), "path");
                Assert.True(r.IsFailure, "无授权根时应拒绝");
                Assert.Contains("没有授权任何目录", r.Error!.Message, "应说明是配置问题");
            });
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响用例结论。
            }
        }
    }

    // ══════════════════════════ 配额 ══════════════════════════

    private static void QuotaCases(TestHarness h)
    {
        h.Case("AC-31", "配额：下载量超限被拒绝且不部分扣减", () =>
        {
            var meter = new CoreActions.QuotaMeter(new CoreActions.ResourceQuota(
                MaxDownloadBytes: 1000, MaxExtractedBytes: 1000, MaxFilesWritten: 10, MaxWallClock: TimeSpan.FromMinutes(1)));

            Assert.True(meter.TryConsume(CoreActions.QuotaKind.Download, 600).IsSuccess, "首次消耗应成功");
            Assert.Equal(600L, meter.DownloadedBytes, "计数应累加");

            var over = meter.TryConsume(CoreActions.QuotaKind.Download, 500);
            Assert.True(over.IsFailure, "超限应失败");
            Assert.Contains("超出资源配额", over.Error!.Message, "应说明配额用途");
            Assert.Equal(600L, meter.DownloadedBytes, "失败时不得部分扣减——否则失败重试会逐步吃掉配额");
        });

        h.Case("AC-32", "配额：文件数与解压量分别计量", () =>
        {
            var meter = new CoreActions.QuotaMeter(new CoreActions.ResourceQuota(1000, 1000, 3, TimeSpan.FromMinutes(1)));
            Assert.True(meter.TryConsume(CoreActions.QuotaKind.FileWrite, 2).IsSuccess, "2 个文件应允许");
            Assert.True(meter.TryConsume(CoreActions.QuotaKind.FileWrite, 2).IsFailure, "第 4 个文件应超限");
            Assert.True(meter.TryConsume(CoreActions.QuotaKind.Extract, 999).IsSuccess, "解压量独立计量");
        });

        h.Case("AC-32b", "★配额：整包时长超限时，剩余动作被拒绝且理由是「整包配额」而不是「动作超时」", () =>
        {
            // 这条守的是"配额接入实际计时"这件事本身。
            // MaxWallClock 曾经只是建模、没有接线，于是"整包最多 30 分钟"只是文档里的一句话。
            var quota = new CoreActions.QuotaMeter(new CoreActions.ResourceQuota(
                MaxDownloadBytes: 1 << 20,
                MaxExtractedBytes: 1 << 20,
                MaxFilesWritten: 10,
                MaxWallClock: TimeSpan.FromMilliseconds(150)));

            var audit = new CoreActions.MemoryAuditSink();
            var ctx = MakeContext(capabilities: [AbsActions.CapabilityIds.Inspect], audit: audit, quota: quota);
            Assert.True(ctx.RemainingWallClock > TimeSpan.Zero, "刚开始时应有剩余预算");

            // 让动作睡过整包预算。动作自身超时设为 30 秒，所以触发的必然是整包配额。
            var slow = new HarnessAction(new ProbeAction(
                "envstation.test.slow",
                AbsActions.CapabilityIds.Inspect,
                timeoutSeconds: 30,
                body: static async (_, _, token) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    return AbsActions.ActionResult.Ok("不该跑到这里");
                }));

            var bag = new AbsDiag.FindingBag();
            var bound = CoreActions.ActionArgumentsBinder.Bind(slow.Descriptor, new Dictionary<string, AbsPkg.ScriptValue>(), bag).Value;

            var result = CoreActions.ActionExecutor.ExecuteAsync(slow, ctx, bound).AsTask().GetAwaiter().GetResult();
            Assert.False(result.Success, "超过整包时长应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.ActionTimeout, result.ErrorCode, "应返回超时码");
            Assert.Contains("整包时长配额", result.Message, "文案必须指向整包配额，否则用户会去优化一个根本不需要优化的动作");
            Assert.True(ctx.RemainingWallClock <= TimeSpan.Zero, "此时剩余预算应为零或负");

            // 预算用尽之后，管线应当在"开始执行之前"就拒绝，而不是每个动作各等一次超时。
            var quick = new HarnessAction(new ProbeAction("envstation.test.echo", AbsActions.CapabilityIds.Inspect));
            var bound2 = CoreActions.ActionArgumentsBinder.Bind(quick.Descriptor, new Dictionary<string, AbsPkg.ScriptValue>(), bag).Value;
            var rejected = CoreActions.ActionExecutor.ExecuteAsync(quick, ctx, bound2).AsTask().GetAwaiter().GetResult();

            Assert.False(rejected.Success, "预算用尽后的动作应被拒绝");
            Assert.Contains("整包时长上限", rejected.Message, "应说明是整包上限导致");

            var begins = audit.Where("action.begin")
                .Select(static e => e.Fields.GetValueOrDefault("action", "?"))
                .ToList();
            Assert.Equal(1, begins.Count, $"只有真正开始执行的那个动作才应留下 begin 记录，实际：{string.Join(",", begins)}");
            Assert.Equal("envstation.test.slow", begins[0], "被配额拒绝的动作不应产生 begin 记录");
        });
    }

    // ══════════════════════════ 审计落盘 ══════════════════════════

    private static void AuditCases(TestHarness h)
    {
        h.Case("AC-48", "审计：JSONL 每行一个对象且目录自动创建", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "envstation-audit-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                // 目录刻意不给它建：构造时就该自己建好，否则第一次写审计就会丢事件。
                using var sink = new CoreActions.JsonlAuditSink(dir);

                sink.Write(Event("action.begin", ("action", "envstation.test.echo")));
                sink.Write(Event("action.end", ("ok", "1")));

                var lines = File.ReadAllLines(sink.CurrentPath);
                Assert.Equal(2, lines.Length, "两条事件应写两行");
                Assert.True(lines[0].StartsWith('{') && lines[0].EndsWith('}'), "每行必须是一个完整的 JSON 对象");

                // 逐行可解析是 JSONL 的全部意义：崩溃时最多丢最后一行，前面仍然可读。
                foreach (var line in lines)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    Assert.Equal("run-0001", doc.RootElement.GetProperty("runId").GetString()!, "应含 runId");
                }

                Assert.Contains("envstation.test.echo", lines[0], "字段应被完整写入");
            }
            finally
            {
                TryDelete(dir);
            }
        });

        h.Case("AC-49", "审计：超过单文件上限后轮转，且保留数量受限", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "envstation-audit-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                // 上限设成 4KB（构造时不允许更小），每条事件约 200 字节 → 必然轮转多次。
                using var sink = new CoreActions.JsonlAuditSink(dir, maxFileBytes: 4096, retentionFiles: 2);

                for (var i = 0; i < 200; i++)
                {
                    sink.Write(Event("action.begin", ("action", $"envstation.test.a{i:D3}")));
                }

                var files = sink.ListFiles();
                Assert.Equal(2, files.Count, "保留策略应只留下 2 个文件");
                Assert.True(files.All(static f => Path.GetFileName(f).StartsWith("audit-", StringComparison.Ordinal)),
                    "只允许清理本汇自己命名的文件");

                // 断言"保留的是最近的、丢掉的是最旧的"，而不是断言具体哪一行在哪个文件里——
                // 后者会把用例绑死在轮转边界上（每行字节数一变就会误报）。
                var kept = files.SelectMany(File.ReadAllLines).ToList();
                Assert.True(
                    kept.Exists(static l => l.Contains("envstation.test.a199", StringComparison.Ordinal)),
                    $"最新的事件必须还在保留的文件里；实际文件：{string.Join(", ", files.Select(Path.GetFileName))}");
                Assert.False(
                    kept.Exists(static l => l.Contains("envstation.test.a000", StringComparison.Ordinal)),
                    "最旧的事件应已被保留策略清理掉");
            }
            finally
            {
                TryDelete(dir);
            }
        });

        h.Case("AC-50", "★审计：经上下文写入的值一律先脱敏（敏感值不得落盘）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "envstation-audit-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                using var sink = new CoreActions.JsonlAuditSink(dir);

                var variables = new CoreActions.VariableTable();
                variables.Set("secret.proxy_password", "hunter2-secret");

                var ctx = MakeContext(audit: sink, variables: variables);
                ctx.Audit("net.download", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["url"] = "https://example.com/x.zip?token=hunter2-secret",
                });

                var text = File.ReadAllText(sink.CurrentPath);
                Assert.NotContains("hunter2-secret", text, "敏感值绝不能出现在审计文件里——审计文件是长期留存的");
                Assert.Contains("***", text, "应替换为 ***");
                Assert.Contains("example.com", text, "非敏感部分应保留，否则审计就没有价值了");
            }
            finally
            {
                TryDelete(dir);
            }
        });

        h.Case("AC-51", "审计：写盘失败不抛出，但通过事件上报（审计失败不能让动作崩）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "envstation-audit-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                using var sink = new CoreActions.JsonlAuditSink(dir);
                var reported = new List<string>();
                sink.AuditWriteFailed += (_, message) => reported.Add(message);

                // 用一个"文件"占住审计文件应该出现的位置，逼真地制造 IO 失败。
                var date = DateTimeOffset.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
                var blocked = Path.Combine(dir, $"audit-{date}.jsonl");
                Directory.CreateDirectory(blocked);
                File.WriteAllText(Path.Combine(blocked, "占位.txt"), "x");

                sink.Write(Event("action.begin", ("action", "envstation.test.echo")));

                Assert.Equal(1, reported.Count, "写盘失败必须被上报，不能静默");
                Assert.True(reported[0].Length > 0, "应带上原因");
            }
            finally
            {
                TryDelete(dir);
            }
        });
    }

    private static CoreActions.AuditEvent Event(string name, params (string Key, string Value)[] fields)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            builder[key] = value;
        }

        return new CoreActions.AuditEvent(DateTimeOffset.Now, "run-0001", "x-test.pkg", name, builder.ToImmutable());
    }

    private static void TryDelete(string directory)
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
            // 临时目录清理失败不影响结论。
        }
    }

    // ══════════════════════════ 变量表 ══════════════════════════

    private static void VariableCases(TestHarness h)
    {
        h.Case("AC-33", "变量表：包只能写 pkg 作用域并可通过表达式读取", () =>
        {
            var table = new CoreActions.VariableTable();
            table.SetPackageVariable("found", "true");
            table.Set("sys.arch", "x64");

            Assert.True(table.Exists("pkg.found"), "包变量应可读");
            Assert.Equal("true", table.Get("pkg.found"), "包变量值");
            Assert.Equal("x64", table.Get("sys.arch"), "宿主变量应可读");

            var r = CoreScript.ExpressionEvaluator.EvaluateCondition("${pkg.found} == true and ${sys.arch} == \"x64\"", table);
            Assert.True(r.IsSuccess && r.Value, $"表达式应能读取变量表，实际：{r.Error?.Message}");
        });

        h.Case("AC-34", "变量表：布尔与数字自动推断，其余保持字符串", () =>
        {
            var table = new CoreActions.VariableTable();
            table.Set("sys.a", "true");
            table.Set("sys.b", "3.12");
            table.Set("sys.c", "0012");
            table.Set("sys.d", "3.12.1");

            Assert.True(table.Exists("sys.a"), "变量存在");
            var resolver = (CoreScript.IVariableResolver)table;
            Assert.Equal(CoreScript.ExprKind.Boolean, resolver.Resolve("sys.a").Kind, "true 应推断为布尔");
            Assert.Equal(CoreScript.ExprKind.Number, resolver.Resolve("sys.b").Kind, "3.12 应推断为数字");
            Assert.Equal(CoreScript.ExprKind.String, resolver.Resolve("sys.c").Kind, "0012 带前导零，应保持字符串（否则会丢前导零）");
            Assert.Equal(CoreScript.ExprKind.String, resolver.Resolve("sys.d").Kind, "版本号应保持字符串");
            Assert.Equal(CoreScript.ExprKind.Missing, resolver.Resolve("sys.none").Kind, "不存在的变量应为 Missing");
        });

        h.Case("AC-35", "★变量表：secret 值在脱敏后不出现在任何文本中", () =>
        {
            var table = new CoreActions.VariableTable();
            table.Set("secret.proxy_password", "hunter2-secret");
            table.Set("pkg.x", "plain");

            var text = "连接代理使用 hunter2-secret 作为密码，注意 hunter2-secret 出现两次。";
            var redacted = table.Redact(text);

            Assert.NotContains("hunter2-secret", redacted, "敏感值必须被替换");
            Assert.Contains("***", redacted, "应替换为 ***");
            Assert.Equal("plain", table.Redact("plain"), "非敏感文本不应被改动");
            Assert.Equal(1, table.SecretCount, "应登记 1 个敏感值");
        });

        h.Case("AC-36", "变量表：枚举作用域时 secret 一律显示为掩码", () =>
        {
            var table = new CoreActions.VariableTable();
            table.Set("secret.token", "abc123");
            table.Set("user.name", "张三");

            var secrets = table.ListScope("secret").ToArray();
            Assert.Equal(1, secrets.Length, "应列出 1 项");
            Assert.Equal("***", secrets[0].Value, "secret 作用域的值必须是掩码");

            var users = table.ListScope("user").ToArray();
            Assert.Equal("张三", users[0].Value, "其他作用域正常显示");
        });
    }

    // ══════════════════════════ 探测动作（真实执行，只读） ══════════════════════════

    private static AbsActions.ActionResult RunBuiltin(string actionId, Dictionary<string, AbsPkg.ScriptValue>? args = null)
    {
        var bag = new AbsDiag.FindingBag();
        var registry = CoreActions.ActionRegistry.CreateDefault(bag);
        Assert.True(registry.IsSuccess, $"注册表应可用：{bag.ToReport().ToText()}");

        var action = registry.Value.Descriptors.Single(d => d.ActionId == actionId);
        var hash = registry.Value.GetContractHash(action.ActionId, action.Version);
        var resolved = registry.Value.Resolve(new AbsPkg.ActionReference(actionId, action.Version, hash));
        Assert.True(resolved.IsSuccess, $"应能解析 {actionId}：{resolved.Error?.Message}");

        var bindBag = new AbsDiag.FindingBag();
        var bound = CoreActions.ActionArgumentsBinder.Bind(
            resolved.Value.Descriptor,
            args ?? new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal),
            bindBag);
        Assert.True(bound.IsSuccess, $"参数应绑定成功：{bindBag.ToReport().ToText()}");

        var ctx = MakeContext(
            roots: [Path.GetTempPath()],
            capabilities: [AbsActions.CapabilityIds.Inspect]);

        return CoreActions.ActionExecutor
            .ExecuteAsync(resolved.Value, ctx, bound.Value)
            .AsTask().GetAwaiter().GetResult();
    }

    private static void DetectCases(TestHarness h)
    {
        h.Case("AC-37", "detect.arch：返回进程架构与系统架构", () =>
        {
            var result = RunBuiltin("envstation.detect.arch");
            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.True(result.Outputs.ContainsKey("os_arch"), "应含 os_arch");
            Assert.True(result.Outputs.ContainsKey("emulated"), "应含模拟运行标记（需求明确要求识别指令集不一致）");
            var arch = result.Outputs["os_arch"];
            Assert.True(arch is "x64" or "x86" or "arm64" or "arm", $"架构取值应规范化，实际 {arch}");
        });

        h.Case("AC-38", "detect.os：返回构建号并判断是否满足最低要求", () =>
        {
            var result = RunBuiltin("envstation.detect.os");
            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.True(result.Outputs.ContainsKey("build"), "应含构建号");
            Assert.True(result.Outputs.ContainsKey("supported"), "应含是否受支持");
            Assert.True(int.Parse(result.Outputs["build"], System.Globalization.CultureInfo.InvariantCulture) > 0, "构建号应为正数");
        });

        h.Case("AC-38b", "detect.os：Windows 11 的 ProductName 按构建号纠正（注册表仍写 Windows 10）", () =>
        {
            // 微软在 Windows 11 上刻意保留 ProductName=Windows 10，只认字符串会把 Windows 11 认成 10。
            Assert.Equal("Windows 11 Pro", EnvStation.Core.Actions.Builtin.OsInfo.NormalizeProductName("Windows 10 Pro", 22631)!,
                "构建号 ≥ 22000 时应改写为 Windows 11");
            Assert.Equal("Windows 10 Pro", EnvStation.Core.Actions.Builtin.OsInfo.NormalizeProductName("Windows 10 Pro", 19045)!,
                "Windows 10 的构建号不应被改写");
            Assert.True(EnvStation.Core.Actions.Builtin.OsInfo.NormalizeProductName(null, 22631) is null, "原本读不到名称时不应凭空造一个");
        });

        h.Case("AC-39", "detect.disk：对临时目录返回空间与可写性", () =>
        {
            var result = RunBuiltin("envstation.detect.disk", new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
            {
                ["path"] = new AbsPkg.ScriptString(Path.GetTempPath()),
            });
            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.True(long.Parse(result.Outputs["available_bytes"], System.Globalization.CultureInfo.InvariantCulture) > 0, "可用空间应为正");
            Assert.True(result.Outputs.ContainsKey("writable"), "应含可写性");
        });

        h.Case("AC-40", "detect.disk：空间不足时返回结构化失败", () =>
        {
            var result = RunBuiltin("envstation.detect.disk", new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
            {
                ["path"] = new AbsPkg.ScriptString(Path.GetTempPath()),
                ["required_bytes"] = new AbsPkg.ScriptInteger(long.MaxValue),
            });
            Assert.False(result.Success, "需求空间超过磁盘容量时应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.PreflightDiskShort, result.ErrorCode, "应返回磁盘不足码");
        });

        h.Case("AC-41", "detect.deps：返回缺失项清单", () =>
        {
            var result = RunBuiltin("envstation.detect.deps");
            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.True(result.Outputs.ContainsKey("missing"), "应含缺失清单");
            Assert.True(result.Outputs.ContainsKey("details"), "应含逐项说明");
        });

        h.Case("AC-42", "detect.env：读取指定变量并返回值类型", () =>
        {
            var result = RunBuiltin("envstation.detect.env", new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
            {
                ["names"] = new AbsPkg.ScriptArray([new AbsPkg.ScriptString("PATH")]),
                ["scope"] = new AbsPkg.ScriptString("machine"),
                ["include_values"] = new AbsPkg.ScriptBoolean(false),
            });
            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Contains("machine:PATH", string.Join(",", result.Outputs.Keys), "应返回 machine:PATH 键");
            Assert.Equal("String", result.Outputs["machine:PATH"], "include_values=false 时应只返回值类型");
        });

        h.Case("AC-43", "detect.command：能找到 cmd.exe 并给出解析路径", () =>
        {
            var result = RunBuiltin("envstation.detect.command", new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
            {
                ["command"] = new AbsPkg.ScriptString("cmd"),
            });
            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("true", result.Outputs["found"], "cmd 应当能找到");
            Assert.True(result.Outputs["resolved_path"].EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase),
                $"应解析到 cmd.exe，实际 {result.Outputs["resolved_path"]}");
        });

        h.Case("AC-44", "detect.command：拒绝接受路径作为命令名", () =>
        {
            var result = RunBuiltin("envstation.detect.command", new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
            {
                ["command"] = new AbsPkg.ScriptString(@"C:\Windows\System32\cmd.exe"),
            });
            Assert.False(result.Success, "传入路径应被拒绝");
            Assert.Contains("只接受命令名", result.Message, "应说明正确用法");
        });

        h.Case("AC-45", "detect.runtime：dotnet 应能被探测到", () =>
        {
            var result = RunBuiltin("envstation.detect.runtime", new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
            {
                ["kind"] = new AbsPkg.ScriptString("dotnet"),
            });
            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.True(result.Outputs.ContainsKey("found"), "应含 found");
            Assert.True(result.Outputs.ContainsKey("all_candidates"), "应列出全部候选证据");
        });

        h.Case("AC-46", "detect.runtime：未知 kind 被拒绝并列出可选值", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var registry = CoreActions.ActionRegistry.CreateDefault(bag).Value;
            var descriptor = registry.Descriptors.Single(static d => d.ActionId == "envstation.detect.runtime");

            var bindBag = new AbsDiag.FindingBag();
            var bound = CoreActions.ActionArgumentsBinder.Bind(
                descriptor,
                new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
                {
                    ["kind"] = new AbsPkg.ScriptString("brainfuck"),
                },
                bindBag);

            Assert.True(bound.IsFailure, "未知 kind 应在校验期被拒绝（枚举参数）");
            Assert.Contains("python", bindBag.ToReport().ToText(), "错误信息应列出允许的 kind");
        });

        h.Case("AC-47", "detect.conflict：能给出冲突结论而不抛异常", () =>
        {
            var result = RunBuiltin("envstation.detect.conflict");
            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.True(result.Outputs.ContainsKey("conflict_count"), "应含冲突计数");
            Assert.True(result.Outputs.ContainsKey("path_entry_count"), "应含 PATH 项数");
        });

        h.Case("AC-48", "★detect.network：不存在的域名必须判为不可达（含假阳性防护）", () =>
        {
            // 这条用例在真实环境里发现过一个严重的假阳性：
            // 某些网络（企业透明代理 / DNS 泛解析）会对**任意**主机名的 443 端口都成功建立 TCP 连接。
            // 因此"可达"的判据必须是 TCP 连通 **且** TLS 证书匹配目标主机名，缺一不可。
            var result = RunBuiltin("envstation.detect.network", new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal)
            {
                ["hosts"] = new AbsPkg.ScriptArray([new AbsPkg.ScriptString("this-host-must-not-exist.invalid")]),
                ["timeout_ms"] = new AbsPkg.ScriptInteger(3000),
            });

            Assert.True(result.Success, $"探测动作本身应成功（结论在输出里），实际：{result.Message}");
            Assert.Equal("0", result.Outputs["reachable_count"],
                $"不存在的域名必须判为不可达。实际结果：{result.Outputs["results"]}");
        });
    }
}
