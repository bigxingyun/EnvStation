using System.Collections.Immutable;
using EnvStation.TestKit;

namespace EnvStation.Tests.EnvironmentActions;

/// <summary>
/// A05 环境变量组 + A06 PATH 专项组 的动作测试。
///
/// <para><b>这一套用例的核心承诺：物理上不可能碰真机环境。</b>
/// 全部动作都经由真实的注册表（动作注册表）、真实的参数绑定器、真实的执行管线运行，
/// 唯一被替换的是"值落到哪张表"——一张内存表。因此本套用例可以在任何人的开发机上反复运行，
/// 而不会污染 PATH 或环境变量。</para>
///
/// <para><b>另一条同样重要的覆盖</b>：那些"看起来会成功但其实很危险"的路径。
/// 比如系统级变量未授权、PATH 超长、变量被其他工具改过、快照损坏——
/// 这些场景在真实使用中一旦漏判，后果都是用户环境被破坏。</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("环境变量动作测试（全程使用内存沙箱，不触碰真机环境）");
        Console.WriteLine();

        var h = new TestHarness("环境变量与 PATH 动作");

        EnvironmentActionCases(h);
        PathActionCases(h);

        var code = h.Summarize();

        var jsonPath = TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return code;
    }

    // ══════════════════════════ 测试脚手架 ══════════════════════════

    private static readonly AbsActions.CapabilitySet AllCapabilities = new(
    [
        AbsActions.CapabilityIds.Inspect,
        AbsActions.CapabilityIds.EnvironmentUser,
        AbsActions.CapabilityIds.EnvironmentMachine,
        AbsActions.CapabilityIds.PathModify,
        AbsActions.CapabilityIds.FileSystemInstall,
    ]);

    /// <summary>执行一次内建动作（走真实的注册表解析 + 参数绑定 + 执行管线）。</summary>
    private static AbsActions.ActionResult Run(
        string actionId,
        SandboxEnvironment sandbox,
        Dictionary<string, AbsPkg.ScriptValue>? arguments = null,
        IEnumerable<string>? capabilities = null,
        CoreActions.VariableTable? variables = null)
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
            // 参数不合法时直接返回一个可断言的失败结果，避免用例里到处写 try/catch。
            return AbsActions.ActionResult.Fail(
                bound.Error.Code,
                "参数绑定失败：" + bindBag.ToReport().ToText());
        }

        var context = new CoreActions.ActionExecutionContext(
            "x-test.env-actions",
            "run-ea",
            new AbsActions.CapabilitySet(capabilities ?? AllCapabilities.Ids),
            [Path.GetTempPath()],
            variables ?? new CoreActions.VariableTable(),
            new CoreActions.QuotaMeter(CoreActions.ResourceQuota.Default),
            new CoreActions.MemoryAuditSink(),
            environment: sandbox);

        return CoreActions.ActionExecutor
            .ExecuteAsync(resolved.Value, context, bound.Value)
            .AsTask().GetAwaiter().GetResult();
    }

    private static Dictionary<string, AbsPkg.ScriptValue> Args(params (string Name, AbsPkg.ScriptValue Value)[] pairs) =>
        pairs.ToDictionary(static p => p.Name, static p => p.Value, StringComparer.Ordinal);

    private static AbsPkg.ScriptValue S(string value) => new AbsPkg.ScriptString(value);

    private static AbsPkg.ScriptValue I(long value) => new AbsPkg.ScriptInteger(value);

    private static AbsPkg.ScriptValue B(bool value) => new AbsPkg.ScriptBoolean(value);

    // ══════════════════════════ A05 环境变量 ══════════════════════════

    private static void EnvironmentActionCases(TestHarness h)
    {
        h.Case("EA-01", "★真机安全总闸：未注入环境操作时 env.set 明确失败", () =>
        {
            var bag = new AbsDiag.FindingBag();
            var registry = CoreActions.ActionRegistry.CreateDefault(bag).Value;
            var descriptor = registry.Descriptors.Single(static d => d.ActionId == "envstation.env.set");
            var bound = CoreActions.ActionArgumentsBinder.Bind(
                descriptor,
                Args(("scope", S("user")), ("name", S("EA_NO_ENV")), ("value", S("x"))),
                bag);

            var context = new CoreActions.ActionExecutionContext(
                "x-test.env-actions",
                "run-no-env",
                AllCapabilities,
                [Path.GetTempPath()],
                new CoreActions.VariableTable(),
                new CoreActions.QuotaMeter(CoreActions.ResourceQuota.Default),
                new CoreActions.MemoryAuditSink(),
                environment: null);

            var result = CoreActions.ActionExecutor
                .ExecuteAsync(registry.TryGet("envstation.env.set", out var action) ? action : throw new InvalidOperationException(),
                    context, bound.Value)
                .AsTask().GetAwaiter().GetResult();

            Assert.False(result.Success, "没有环境操作实现时必须失败");
            Assert.Contains("没有启用环境变量写入能力", result.Message, "应明确说明原因，而不是静默去改真实注册表");
        });

        h.Case("EA-02", "env.set：写入沙箱并保持值类型（原本存在则沿用原类型）", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_TYPE", @"%SystemRoot%\bin", AbsEnv.EnvValueKind.String);

            var result = Run("envstation.env.set", sandbox,
                Args(("scope", S("user")), ("name", S("EA_TYPE")), ("value", S(@"%SystemRoot%\tools"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal(@"%SystemRoot%\tools", sandbox.Peek(AbsEnv.EnvScope.User, "EA_TYPE"), "值应写入沙箱");
            Assert.Equal(AbsEnv.EnvValueKind.String, sandbox.PeekKind(AbsEnv.EnvScope.User, "EA_TYPE"), "必须沿用原有的 REG_SZ（SEC-11）");
        });

        h.Case("EA-03", "env.set：新建变量默认使用可展开类型", () =>
        {
            var sandbox = new SandboxEnvironment();
            var result = Run("envstation.env.set", sandbox,
                Args(("scope", S("user")), ("name", S("EA_NEW")), ("value", S(@"%USERPROFILE%\x"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal(AbsEnv.EnvValueKind.ExpandString, sandbox.PeekKind(AbsEnv.EnvScope.User, "EA_NEW"),
                "新建变量默认 REG_EXPAND_SZ —— 否则 %USERPROFILE% 会变成字面量");
        });

        h.Case("EA-04", "env.set：值相同则幂等，不产生变更", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_SAME", "same");

            var result = Run("envstation.env.set", sandbox,
                Args(("scope", S("user")), ("name", S("EA_SAME")), ("value", S("same"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("false", result.Outputs["changed"], "值相同时应报告未变更");
            Assert.Equal(0, sandbox.SnapshotCount, "幂等路径不应产生快照噪音");
        });

        h.Case("EA-05", "env.set：overwrite=false 且已存在时拒绝覆盖", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_KEEP", "original");

            var result = Run("envstation.env.set", sandbox,
                Args(("scope", S("user")), ("name", S("EA_KEEP")), ("value", S("new")), ("overwrite", B(false))));

            Assert.False(result.Success, "不应覆盖");
            Assert.Equal("original", sandbox.Peek(AbsEnv.EnvScope.User, "EA_KEEP"), "原值必须保持不变");
        });

        h.Case("EA-06", "env.set：非法变量名被拒绝", () =>
        {
            var sandbox = new SandboxEnvironment();
            var result = Run("envstation.env.set", sandbox,
                Args(("scope", S("user")), ("name", S("BAD=NAME")), ("value", S("x"))));

            Assert.False(result.Success, "含 = 的名称应被拒绝");
            Assert.Equal(0, sandbox.Count(AbsEnv.EnvScope.User), "沙箱不应有任何写入");
        });

        h.Case("EA-07", "★env.set：系统级未授权即拒绝（不是静默降级到用户级）", () =>
        {
            var sandbox = new SandboxEnvironment();
            var result = Run("envstation.env.set", sandbox,
                Args(("scope", S("machine")), ("name", S("EA_SYS")), ("value", S("x"))),
                capabilities: [AbsActions.CapabilityIds.Inspect, AbsActions.CapabilityIds.EnvironmentUser]);

            Assert.False(result.Success, "缺少 CAP.ENV.MACHINE 时应拒绝");
            Assert.Equal(Abs.EnvStationErrorCodes.CapabilityDenied, result.ErrorCode, "应返回能力拒绝码");
            Assert.Equal(0, sandbox.Count(AbsEnv.EnvScope.Machine), "系统级不应有任何写入");
        });

        h.Case("EA-08", "env.set：系统级已授权时可写入", () =>
        {
            var sandbox = new SandboxEnvironment();
            var result = Run("envstation.env.set", sandbox,
                Args(("scope", S("machine")), ("name", S("EA_SYS2")), ("value", S("v"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("v", sandbox.Peek(AbsEnv.EnvScope.Machine, "EA_SYS2"), "系统级应写入沙箱");
        });

        h.Case("EA-09", "★env.set：超长 PATH 默认拒绝（旧版对话框截断陷阱）", () =>
        {
            var sandbox = new SandboxEnvironment();
            var longPath = string.Join(';', Enumerable.Range(0, 60).Select(i => $@"C:\Tools\VeryLongToolDirectory{i:D3}\bin"));

            var rejected = Run("envstation.env.set", sandbox,
                Args(("scope", S("user")), ("name", S("PATH")), ("value", S(longPath))));

            Assert.False(rejected.Success, $"超过旧版编辑上限的 PATH 应默认拒绝（长度 {longPath.Length}）");
            Assert.Contains("静默截断", rejected.Message, "应说明为什么危险");

            var accepted = Run("envstation.env.set", sandbox,
                Args(("scope", S("user")), ("name", S("PATH")), ("value", S(longPath)), ("allow_legacy_length", B(true))));

            Assert.True(accepted.Success, $"显式确认后应允许：{accepted.Message}");
            Assert.Equal(longPath, sandbox.Peek(AbsEnv.EnvScope.User, "PATH"), "值应完整写入，绝不截断");
        });

        h.Case("EA-10", "env.unset：删除变量并保持幂等", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_DEL", "v");

            var first = Run("envstation.env.unset", sandbox, Args(("scope", S("user")), ("name", S("EA_DEL"))));
            Assert.True(first.Success, $"应成功：{first.Message}");
            Assert.Null(sandbox.Peek(AbsEnv.EnvScope.User, "EA_DEL"), "变量应被删除");

            var second = Run("envstation.env.unset", sandbox, Args(("scope", S("user")), ("name", S("EA_DEL"))));
            Assert.True(second.Success, "重复删除应幂等成功");
            Assert.Equal("false", second.Outputs["changed"], "第二次应报告未变更");
        });

        h.Case("EA-11", "★env.unset：expect_value 不符时拒绝删除（防误删被其他工具改过的变量）", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_EXPECT", "changed-by-other-tool");

            var result = Run("envstation.env.unset", sandbox,
                Args(("scope", S("user")), ("name", S("EA_EXPECT")), ("expect_value", S("what-we-installed"))));

            Assert.False(result.Success, "值不符时应拒绝删除");
            Assert.Equal("changed-by-other-tool", sandbox.Peek(AbsEnv.EnvScope.User, "EA_EXPECT"), "变量必须保留");
        });

        h.Case("EA-12", "env.get：both 作用域读取并正确判定生效层级", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.Machine, "EA_LAYER", "from-machine");
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_LAYER", "from-user");

            var result = Run("envstation.env.get", sandbox,
                Args(("name", S("EA_LAYER")), ("scope", S("both"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("from-machine", result.Outputs["machine.value"], "系统级值");
            Assert.Equal("from-user", result.Outputs["user.value"], "用户级值");
            Assert.Equal("from-user", result.Outputs["effective_value"], "生效值应是用户级（用户级覆盖系统级）");
            Assert.Equal("user", result.Outputs["effective_scope"], "生效作用域应是 user");
        });

        h.Case("EA-13", "env.backup：返回快照 ID 与两侧计数", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_B1", "1");
            sandbox.Seed(AbsEnv.EnvScope.Machine, "EA_B2", "2");

            var result = Run("envstation.env.backup", sandbox, Args(("scope", S("user")), ("note", S("测试备份"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.True(result.Outputs.ContainsKey("snapshot_id"), "应返回快照 ID");
            Assert.Equal("1", result.Outputs["user_count"], "用户级 1 项");
            Assert.Equal("1", result.Outputs["machine_count"], "系统级 1 项");
            Assert.Equal(1, sandbox.SnapshotCount, "应产生 1 个快照");
        });

        h.Case("EA-14", "env.diff：能识别新增、修改与删除", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_KEEP", "same");
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_CHANGE", "before");
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_GONE", "still-here");

            var backup = Run("envstation.env.backup", sandbox, Args(("scope", S("user"))));
            Assert.True(backup.Success, "备份应成功");
            var snapshotId = backup.Outputs["snapshot_id"];

            sandbox.Seed(AbsEnv.EnvScope.User, "EA_CHANGE", "after");
            _ = sandbox.RemoveVariable(AbsEnv.EnvScope.User, "EA_GONE", AbsTx.RiskLevel.Reversible, "test");
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_ADDED", "new");

            var diff = Run("envstation.env.diff", sandbox, Args(("snapshot_id", S(snapshotId))));
            Assert.True(diff.Success, $"应成功：{diff.Message}");
            Assert.Equal("3", diff.Outputs["diff_count"], $"应识别 3 处差异，实际：{diff.Outputs["details"]}");
            Assert.Contains("EA_CHANGE", diff.Outputs["details"], "应含被修改项");
            Assert.Contains("EA_ADDED", diff.Outputs["details"], "应含新增项");
            Assert.Contains("EA_GONE", diff.Outputs["details"], "应含被删除项");
            Assert.NotContains("EA_KEEP", diff.Outputs["details"], "未变化项不应出现");
        });

        h.Case("EA-15", "env.restore：全量还原到快照时刻", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_R1", "v1");
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_R2", "v2");

            var backup = Run("envstation.env.backup", sandbox, Args(("scope", S("user"))));
            var snapshotId = backup.Outputs["snapshot_id"];

            sandbox.Seed(AbsEnv.EnvScope.User, "EA_R1", "changed");
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_R3", "extra");

            var restore = Run("envstation.env.restore", sandbox, Args(("snapshot_id", S(snapshotId))));
            Assert.True(restore.Success, $"应成功：{restore.Message}");

            Assert.Equal("v1", sandbox.Peek(AbsEnv.EnvScope.User, "EA_R1"), "被修改项应还原");
            Assert.Equal("v2", sandbox.Peek(AbsEnv.EnvScope.User, "EA_R2"), "未变化项应保持");
            Assert.Null(sandbox.Peek(AbsEnv.EnvScope.User, "EA_R3"), "快照中不存在的项应被删除（全量还原）");
        });

        h.Case("EA-16", "env.restore：单变量还原不误伤其他变量", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_S1", "v1");
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_S2", "v2");

            var backup = Run("envstation.env.backup", sandbox, Args(("scope", S("user"))));
            var snapshotId = backup.Outputs["snapshot_id"];

            sandbox.Seed(AbsEnv.EnvScope.User, "EA_S1", "changed");
            sandbox.Seed(AbsEnv.EnvScope.User, "EA_S2", "changed-too");

            var restore = Run("envstation.env.restore", sandbox,
                Args(("snapshot_id", S(snapshotId)), ("names", new AbsPkg.ScriptArray([S("EA_S1")]))));

            Assert.True(restore.Success, $"应成功：{restore.Message}");
            Assert.Equal("v1", sandbox.Peek(AbsEnv.EnvScope.User, "EA_S1"), "目标变量应还原");
            Assert.Equal("changed-too", sandbox.Peek(AbsEnv.EnvScope.User, "EA_S2"), "非目标变量不得被改动");
        });

        h.Case("EA-17", "env.validate：合法值通过，并给出可操作的风险提醒", () =>
        {
            var sandbox = new SandboxEnvironment();

            var ok = Run("envstation.env.validate", sandbox, Args(("name", S("EA_OK")), ("value", S("plain"))));
            Assert.True(ok.Success, $"应成功：{ok.Message}");
            Assert.Equal("0", ok.Outputs["warning_count"], "普通值不应有警告");

            var risky = Run("envstation.env.validate", sandbox,
                Args(("name", S("EA_RISKY")), ("value", S(@"C:\A;;%UNPAIRED"))));
            Assert.True(risky.Success, "有风险的值本身仍然合法");
            Assert.True(int.Parse(risky.Outputs["warning_count"], System.Globalization.CultureInfo.InvariantCulture) >= 2,
                $"应报出空条目与 % 未配对两项风险，实际：{risky.Outputs["warnings"]}");
            Assert.Contains("安全风险", risky.Outputs["warnings"], "空条目必须点明安全含义");
        });

        h.Case("EA-18", "env.validate：非法名称返回结构化失败", () =>
        {
            var sandbox = new SandboxEnvironment();
            var result = Run("envstation.env.validate", sandbox, Args(("name", S("")), ("value", S("x"))));
            Assert.False(result.Success, "空名称应判失败");
        });
    }

    // ══════════════════════════ A06 PATH 专项 ══════════════════════════

    private static void PathActionCases(TestHarness h)
    {
        h.Case("EA-19", "path.ensure：默认追加到末尾，且不改变现有顺序", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows;C:\Windows\System32");

            var result = Run("envstation.path.ensure", sandbox,
                Args(("scope", S("user")), ("entry", S(@"D:\Dev\Python"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal(@"C:\Windows;C:\Windows\System32;D:\Dev\Python", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"),
                "应追加到末尾（默认不抢优先级）");
        });

        h.Case("EA-20", "path.ensure：已存在时不重复添加，也不产生快照", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows;D:\Dev\Python");

            var result = Run("envstation.path.ensure", sandbox,
                Args(("scope", S("user")), ("entry", S(@"d:\dev\python\"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("false", result.Outputs["changed"], "大小写与尾随分隔符不同也应判为已存在");
            Assert.Equal(0, sandbox.SnapshotCount, "幂等路径不应产生快照");
        });

        h.Case("EA-21", "path.ensure：显式 prepend 时插到最前（用于消解冲突）", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows;C:\Program Files\nodejs");

            var result = Run("envstation.path.ensure", sandbox,
                Args(("scope", S("user")), ("entry", S(@"D:\Hadoop\bin")), ("position", S("prepend"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal(@"D:\Hadoop\bin;C:\Windows;C:\Program Files\nodejs", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"),
                "prepend 应插到最前");
        });

        h.Case("EA-22", "★path.ensure：相对路径被拒绝", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows");

            var result = Run("envstation.path.ensure", sandbox,
                Args(("scope", S("user")), ("entry", S(@"Tools\bin"))));

            Assert.False(result.Success, "相对路径应被拒绝");
            Assert.Equal(@"C:\Windows", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"), "PATH 必须保持原样");
        });

        h.Case("EA-23", "path.remove：移除条目并保持幂等", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows;D:\Old;D:\Keep");

            var first = Run("envstation.path.remove", sandbox,
                Args(("scope", S("user")), ("entry", S(@"d:\old\"))));
            Assert.True(first.Success, $"应成功：{first.Message}");
            Assert.Equal(@"C:\Windows;D:\Keep", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"), "应移除目标项");

            var second = Run("envstation.path.remove", sandbox,
                Args(("scope", S("user")), ("entry", S(@"D:\Old"))));
            Assert.True(second.Success, "重复移除应幂等");
            Assert.Equal("false", second.Outputs["changed"], "第二次应报告未变更");
        });

        h.Case("EA-24", "path.dedupe：保序去重", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\A;C:\B;C:\A;C:\B;C:\C");

            var result = Run("envstation.path.dedupe", sandbox, Args(("scope", S("user"))));
            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal(@"C:\A;C:\B;C:\C", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"), "应保留首次出现并维持顺序");
        });

        h.Case("EA-25", "path.prioritize：把指定项移动到指定位置", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\A;C:\B;C:\C");

            var result = Run("envstation.path.prioritize", sandbox,
                Args(("scope", S("user")), ("entry", S(@"C:\C")), ("index", I(0))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal(@"C:\C;C:\A;C:\B", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"), "应移到最前");
        });

        h.Case("EA-26", "path.prioritize：目标位置越界被拒绝", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\A;C:\B");

            var result = Run("envstation.path.prioritize", sandbox,
                Args(("scope", S("user")), ("entry", S(@"C:\A")), ("index", I(9))));

            Assert.False(result.Success, "越界位置应被拒绝");
            Assert.Contains("超出 PATH 条目数", result.Message, "应说明合法范围");
        });

        h.Case("EA-27", "★path.clean：dry_run 只报告不修改", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows;;C:\DefinitelyNotExist_EA;\");

            var result = Run("envstation.path.clean", sandbox,
                Args(("scope", S("user")), ("dry_run", B(true))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("true", result.Outputs["dry_run"], "应标记为干跑");
            Assert.Equal(@"C:\Windows;;C:\DefinitelyNotExist_EA;\", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"),
                "干跑绝不能修改 PATH");
        });

        h.Case("EA-28", "path.clean：真实清理空项与不存在目录，但保留变量未解析项", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows;;C:\DefinitelyNotExist_EA;%EA_UNDEFINED_VAR%\bin");

            var result = Run("envstation.path.clean", sandbox, Args(("scope", S("user"))));
            Assert.True(result.Success, $"应成功：{result.Message}");

            var after = sandbox.Peek(AbsEnv.EnvScope.User, "PATH");
            Assert.Equal(@"C:\Windows;%EA_UNDEFINED_VAR%\bin", after,
                "空项与不存在目录应被移除；变量未解析项必须保留（否则会误删用户的可移植写法）");
        });

        h.Case("EA-29", "★path.validate：统计各类问题并点明空条目的安全含义", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows;;C:\DefinitelyNotExist_EA;C:\Windows");

            var result = Run("envstation.path.validate", sandbox, Args(("scope", S("user"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("4", result.Outputs["entry_count"], "应统计 4 项");
            Assert.Equal("1", result.Outputs["empty_count"], "应有 1 个空条目");
            Assert.Equal("1", result.Outputs["missing_count"], "应有 1 个不存在目录");
            Assert.Equal("1", result.Outputs["duplicate_count"], "应有 1 个重复项");
            Assert.Contains("安全风险", result.Message, "空条目的安全含义必须点明");
        });

        h.Case("EA-30", "path.validate：报告长度与旧版编辑上限", () =>
        {
            var sandbox = new SandboxEnvironment();

            // 构造一个确实超过 2047 字符的 PATH：
            // 旧版「环境变量」编辑对话框的上限是 2047，超过后一旦用该对话框保存就会被静默截断。
            var longValue = string.Join(';', Enumerable.Range(0, 80).Select(i => $@"C:\Tools\VeryLongToolDirectory{i:D3}\bin"));
            Assert.True(longValue.Length > 2047, $"测试数据本身必须超过旧版上限，实际 {longValue.Length} 字符");

            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", longValue);

            var result = Run("envstation.path.validate", sandbox, Args(("scope", S("user"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal("true", result.Outputs["over_legacy_limit"], "应识别出超过旧版编辑上限");
            Assert.Equal(longValue.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Outputs["length"], "应报告真实长度");
        });

        h.Case("EA-31", "path.ensure_shim：默认插到最前（托管层需要优先接管）", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows;C:\Program Files\nodejs");

            var result = Run("envstation.path.ensure_shim", sandbox,
                Args(("scope", S("user")), ("shim_dir", S(@"D:\EnvStation\shims"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal(@"D:\EnvStation\shims;C:\Windows;C:\Program Files\nodejs", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"),
                "shim 目录应插到最前");
            Assert.Equal("false", result.Outputs["dir_exists"], "目录尚不存在时应如实报告，且不因此失败");
        });

        h.Case("EA-32", "path.ensure_shim：非绝对路径被拒绝", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"C:\Windows");

            var result = Run("envstation.path.ensure_shim", sandbox,
                Args(("scope", S("user")), ("shim_dir", S("shims"))));

            Assert.False(result.Success, "相对路径应被拒绝");
            Assert.Equal(@"C:\Windows", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"), "PATH 必须保持原样");
        });

        h.Case("EA-33", "★PATH 写入：值类型必须保持（不能把 REG_EXPAND_SZ 降级为 REG_SZ）", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", @"%SystemRoot%;%SystemRoot%\System32", AbsEnv.EnvValueKind.ExpandString);

            var result = Run("envstation.path.ensure", sandbox,
                Args(("scope", S("user")), ("entry", S(@"D:\Dev"))));

            Assert.True(result.Success, $"应成功：{result.Message}");
            Assert.Equal(AbsEnv.EnvValueKind.ExpandString, sandbox.PeekKind(AbsEnv.EnvScope.User, "PATH"),
                "必须保持 REG_EXPAND_SZ，否则 %SystemRoot% 会永久变成字面量（PE-4）");
            Assert.Equal(@"%SystemRoot%;%SystemRoot%\System32;D:\Dev", sandbox.Peek(AbsEnv.EnvScope.User, "PATH"),
                "用户的 %VAR% 写法必须原样保留，不能被展开成绝对路径");
        });

        h.Case("EA-34", "PATH 动作：系统级未授权即拒绝", () =>
        {
            var sandbox = new SandboxEnvironment();
            sandbox.Seed(AbsEnv.EnvScope.Machine, "PATH", @"C:\Windows");

            var result = Run("envstation.path.ensure", sandbox,
                Args(("scope", S("machine")), ("entry", S(@"D:\Dev"))),
                capabilities: [AbsActions.CapabilityIds.Inspect, AbsActions.CapabilityIds.PathModify]);

            Assert.False(result.Success, "缺少 CAP.ENV.MACHINE 时应拒绝");
            Assert.Equal(AbsEnv.EnvValueKind.ExpandString, sandbox.PeekKind(AbsEnv.EnvScope.Machine, "PATH"), "类型不变");
            Assert.Equal(@"C:\Windows", sandbox.Peek(AbsEnv.EnvScope.Machine, "PATH"), "系统级 PATH 应保持原样");
        });

        h.Case("EA-35", "PATH 动作产生的变更可通过快照还原", () =>
        {
            var sandbox = new SandboxEnvironment();
            var original = @"C:\Windows;C:\Windows\System32";
            sandbox.Seed(AbsEnv.EnvScope.User, "PATH", original);

            var add = Run("envstation.path.ensure", sandbox,
                Args(("scope", S("user")), ("entry", S(@"D:\Dev\Python"))));
            Assert.True(add.Success, $"应成功：{add.Message}");

            var snapshotId = add.Outputs["snapshot_id"];
            Assert.True(snapshotId.Length > 0, "PATH 变更必须返回可反向操作的快照 ID（AC-4）");

            var restore = Run("envstation.env.restore", sandbox, Args(("snapshot_id", S(snapshotId))));
            Assert.True(restore.Success, $"应成功：{restore.Message}");
            Assert.Equal(original, sandbox.Peek(AbsEnv.EnvScope.User, "PATH"), "PATH 应还原到修改前");
        });
    }
}
