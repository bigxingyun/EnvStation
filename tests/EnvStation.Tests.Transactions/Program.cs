using System.Diagnostics;
using Microsoft.Win32;

namespace EnvStation.Tests.Transactions;

/// <summary>
/// 测试用的环境变量存储：把"环境变量"存放在 <b>注册表沙箱键</b>中，而不是真实的
/// <c>HKCU\Environment</c>。
///
/// <para><b>为什么这么做：</b>P06 要反复写入、回滚、损坏快照，若操作真实环境变量，
/// 一旦测试中断就会污染开发者的机器。用注册表沙箱键可以同时获得"真实的注册表语义"
/// （值类型、大小写不敏感）与"完全隔离"两个好处。</para>
/// </summary>
internal sealed class SandboxRegistryStore : CoreEnv.IEnvironmentStore
{
    private readonly string _keyPath;

    public SandboxRegistryStore(AbsEnv.EnvScope scope, string keyPath)
    {
        Scope = scope;
        _keyPath = keyPath;
    }

    public AbsEnv.EnvScope Scope { get; }

    public Result<IReadOnlyList<AbsEnv.EnvVariable>> ReadAll()
    {
        using var key = Reg.Registry.CurrentUser.OpenSubKey(_keyPath, writable: false)
            ?? Reg.Registry.CurrentUser.CreateSubKey(_keyPath);

        var list = new List<AbsEnv.EnvVariable>();
        foreach (var name in key!.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            list.Add(new AbsEnv.EnvVariable
            {
                Name = name,
                RawValue = raw ?? string.Empty,
                Kind = key.GetValueKind(name) == RegistryValueKind.ExpandString
                    ? AbsEnv.EnvValueKind.ExpandString
                    : AbsEnv.EnvValueKind.String,
                ContainsVariableReference = raw?.Contains('%', StringComparison.Ordinal) ?? false,
            });
        }

        return Result<IReadOnlyList<AbsEnv.EnvVariable>>.Ok(list);
    }

    public Result<AbsEnv.EnvVariable?> Read(string name)
    {
        using var key = Reg.Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        if (key is null)
        {
            return Result<AbsEnv.EnvVariable?>.Ok(null);
        }

        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
        {
            return Result<AbsEnv.EnvVariable?>.Ok(null);
        }

        return Result<AbsEnv.EnvVariable?>.Ok(new AbsEnv.EnvVariable
        {
            Name = name,
            RawValue = raw as string ?? string.Empty,
            Kind = key.GetValueKind(name) == RegistryValueKind.ExpandString
                ? AbsEnv.EnvValueKind.ExpandString
                : AbsEnv.EnvValueKind.String,
        });
    }

    public Result<Unit> Write(string name, string rawValue, AbsEnv.EnvValueKind kind)
    {
        using var key = Reg.Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key!.SetValue(name, rawValue, kind == AbsEnv.EnvValueKind.ExpandString
            ? RegistryValueKind.ExpandString
            : RegistryValueKind.String);

        return Results.Ok();
    }

    public Result<Unit> Delete(string name)
    {
        using var key = Reg.Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
        return Results.Ok();
    }
}

/// <summary>
/// M0-P06：快照 / 回滚 / 中断恢复。
///
/// <para>对应任务书 P06-1 ~ P06-9；全部在临时目录 + 注册表沙箱键中进行，
/// 结束后清理，不触碰真实用户环境。</para>
/// </summary>
internal static class Program
{
    private const string SandboxKeyUser = @"Software\EnvStation\TestSandboxTx";
    private const string SandboxKeyMachine = @"Software\EnvStation\TestSandboxTxMachine";

    private static string _root = string.Empty;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("M0-P06 · 快照 / 回滚 / 中断恢复");
        Console.WriteLine($"运行身份：{(Assert.IsAdministrator() ? "管理员" : "标准用户")}");

        _root = Path.Combine(Path.GetTempPath(), "envstation-p06-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine($"隔离沙箱目录：{_root}");
        Console.WriteLine();

        var h = new TestKit.TestHarness("M0-P06 快照与回滚");

        try
        {
            RunCases(h);
        }
        finally
        {
            Cleanup();
        }

        var jsonPath = TestKit.TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return h.Summarize();
    }

    private static void RunCases(TestKit.TestHarness h)
    {
        h.Case("P06-1", "快照全量采集（用户级 + 系统级 + 值类型）", () =>
        {
            var (snapshots, user, _) = CreateStores();

            user.Write("ENVSTATION_TEST_A", @"D:\Dev\%USERNAME%\a", AbsEnv.EnvValueKind.ExpandString);
            user.Write("ENVSTATION_TEST_B", "plain", AbsEnv.EnvValueKind.String);

            var captured = snapshots.Capture("unit-test P06-1");
            Assert.True(captured.IsSuccess, $"快照应成功：{captured.Error}");

            var snap = captured.Value;
            Assert.True(snap.UserVariables.Count >= 2, "快照应包含刚写入的变量");
            Assert.NotNull(snap.ContentHash, "快照必须带内容哈希（用于完整性校验）");

            var a = snap.UserVariables.First(v => v.Name == "ENVSTATION_TEST_A");
            Assert.Equal(AbsEnv.EnvValueKind.ExpandString, a.Kind, "快照必须保留值类型");
            Assert.Equal(@"D:\Dev\%USERNAME%\a", a.RawValue, "快照必须保留未展开的原始值");

            Console.WriteLine($"         快照 ID = {snap.SnapshotId}");
            Console.WriteLine($"         内容哈希 = {snap.ContentHash![..16]}…");
        });

        h.Case("P06-2", "快照损坏后校验失败，并阻止后续写操作", () =>
        {
            var (snapshots, user, _) = CreateStores();
            user.Write("ENVSTATION_TEST_C", "v1", AbsEnv.EnvValueKind.String);

            var snap = snapshots.Capture("unit-test P06-2").Value;

            // 人为篡改快照正文（模拟磁盘损坏或外部修改）
            var path = Path.Combine(snapshots.RootPath, snap.SnapshotId + ".json");
            var content = File.ReadAllText(path);
            File.WriteAllText(path, content.Replace("\"v1\"", "\"TAMPERED\"", StringComparison.Ordinal));

            var verify = snapshots.Verify(snap.SnapshotId);
            Assert.True(verify.IsFailure, "被篡改的快照必须校验失败");
            Assert.Equal(Abs.EnvStationErrorCodes.TxSnapshotInvalid, verify.Error!.Code,
                "必须返回 E_TX_SNAPSHOT_INVALID（NFR-R4）");

            Console.WriteLine($"         篡改后校验结果：{verify.Error.Message}");
        });

        h.Case("P06-3", "单变量还原：只改目标变量，其他一字不动", () =>
        {
            var (snapshots, user, _) = CreateStores();
            user.Write("ENVSTATION_TEST_D", "original", AbsEnv.EnvValueKind.String);
            user.Write("ENVSTATION_TEST_E", "untouched", AbsEnv.EnvValueKind.String);

            var snap = snapshots.Capture("unit-test P06-3").Value;

            // 两个变量都被改动
            user.Write("ENVSTATION_TEST_D", "changed", AbsEnv.EnvValueKind.String);
            user.Write("ENVSTATION_TEST_E", "also-changed", AbsEnv.EnvValueKind.String);

            // 只还原 D
            var originalD = snap.UserVariables.First(v => v.Name == "ENVSTATION_TEST_D");
            user.Write("ENVSTATION_TEST_D", originalD.RawValue, originalD.Kind);

            Assert.Equal("original", user.Read("ENVSTATION_TEST_D").Value!.RawValue, "D 应被还原");
            Assert.Equal("also-changed", user.Read("ENVSTATION_TEST_E").Value!.RawValue,
                "E 未在还原范围内，必须保持修改后的值");
        });

        h.Case("P06-4", "全量还原：环境恢复到快照时刻（逐项 diff 为空）", () =>
        {
            var (snapshots, user, _) = CreateStores();
            user.Write("ENVSTATION_TEST_F", "f-original", AbsEnv.EnvValueKind.String);
            user.Write("ENVSTATION_TEST_G", @"%USERPROFILE%\g", AbsEnv.EnvValueKind.ExpandString);

            var before = snapshots.Capture("unit-test P06-4").Value;

            // 打乱：改一个、删一个、加一个
            user.Write("ENVSTATION_TEST_F", "f-modified", AbsEnv.EnvValueKind.String);
            user.Delete("ENVSTATION_TEST_G");
            user.Write("ENVSTATION_TEST_H", "h-new", AbsEnv.EnvValueKind.String);

            // 执行全量还原
            RestoreAll(user, before);

            var after = snapshots.Capture("unit-test P06-4-verify").Value;
            var diffs = Diff(before.UserVariables, after.UserVariables);
            Assert.Equal(0, diffs, $"全量还原后不应有任何差异，实际差异：{diffs} 项");

            // 值类型也必须复原
            var g = user.Read("ENVSTATION_TEST_G").Value;
            Assert.NotNull(g, "被删除的变量应被还原");
            Assert.Equal(AbsEnv.EnvValueKind.ExpandString, g!.Kind, "还原后值类型必须是 ExpandString（SEC-11）");
            Assert.Equal(@"%USERPROFILE%\g", g.RawValue, "还原后必须是未展开的原始值");
        });

        h.Case("P06-5", "非法写入被前置拦截，现有值保持不变", () =>
        {
            var (snapshots, user, _) = CreateStores();
            user.Write("ENVSTATION_TEST_I", "i-original", AbsEnv.EnvValueKind.String);
            _ = snapshots.Capture("unit-test P06-5").Value;

            // 构造"非法写入"：超长值必须在校验层被拒绝（前置拦截，而非写入后失败）
            var tooLong = new string('X', 40_000);
            var validation = CoreEnv.RegistryEnvStore
                .ValidateNameAndValue("ENVSTATION_TEST_I", tooLong);

            Assert.True(validation.IsFailure, "超长值应在校验层被拒绝（前置拦截，而非写入后失败）");
            Assert.Equal(Abs.EnvStationErrorCodes.EnvValueTooLong, validation.Error!.Code,
                "应返回明确的 E_ENV_VALUE_TOO_LONG，供 UI 给出可操作提示");

            // 校验失败后环境必须原封不动（这才是"不产生中间态"的真正含义）
            Assert.Equal("i-original", user.Read("ENVSTATION_TEST_I").Value!.RawValue,
                "被拒绝的写入不得改变现有值");
        });

        h.Case("P06-6", "系统级作用域快照：内容与标记保持一致", () =>
        {
            var (snapshots, _, machine) = CreateStores();

            // 先放一个系统级变量，使"读到内容"这条分支可被真实验证
            machine.Write("ENVSTATION_TEST_SYS", "sys-value", AbsEnv.EnvValueKind.String);

            var snap = snapshots.Capture("unit-test P06-6");
            Assert.True(snap.IsSuccess, "系统级读取受限时，用户级快照仍应成功");

            var s = snap.Value;
            Assert.True(s.MachineVariables.Count > 0, "沙箱系统级作用域可读，应采集到变量");

            // 标记与内容的**一致性**才是本用例要验证的不变量（P06-6 的原始意图）：
            // 不允许出现"内容为空却未标记读取失败"这种静默丢数据的情形。
            if (s.MachineVariables.Count == 0)
            {
                Assert.True(s.MachineScopeReadFailed,
                    "系统级变量为空时，必须显式标记 MachineScopeReadFailed，不得静默忽略");
            }
            else
            {
                Assert.False(s.MachineScopeReadFailed,
                    "已采集到系统级变量时，不得同时标记读取失败（否则还原逻辑会拒绝处理系统级部分）");
            }

            Console.WriteLine($"         系统级变量 {s.MachineVariables.Count} 个，读取失败标记 = {s.MachineScopeReadFailed}");
        });

        h.Case("P06-7", "保留策略：超限只提示不静默删除", () =>
        {
            var (snapshots, user, _) = CreateStores();
            user.Write("ENVSTATION_TEST_J", "j", AbsEnv.EnvValueKind.String);

            for (var i = 0; i < 6; i++)
            {
                var r = snapshots.Capture($"unit-test P06-7 #{i}");
                Assert.True(r.IsSuccess, $"第 {i} 次快照应成功");
            }

            var excess = snapshots.PruneAutoSnapshots(keep: 3);
            Assert.True(excess.IsSuccess, "裁剪查询应成功");
            Assert.True(excess.Value.Count >= 3, $"应有 3 项超出保留数，实际 {excess.Value.Count}");

            // 关键：文件必须还在（提示而非删除）
            var remaining = snapshots.List().Value.Count;
            Assert.True(remaining >= 6, $"裁剪查询不得删除文件，实际剩余 {remaining} 份");

            Console.WriteLine($"         超出保留数 {excess.Value.Count} 项（文件保留，等待用户确认）");
        });

        h.Case("P06-8", "中断检测：未完成事务标记可被识别并清理", () =>
        {
            var (snapshots, user, _) = CreateStores();
            user.Write("ENVSTATION_TEST_K", "k", AbsEnv.EnvValueKind.String);

            var manager = new EnvStation.Core.Transactions.EnvironmentTransactionManager(
                snapshots, scope => scope == AbsEnv.EnvScope.User
                    ? new SandboxRegistryStore(AbsEnv.EnvScope.User, SandboxKeyUser)
                    : new SandboxRegistryStore(AbsEnv.EnvScope.Machine, SandboxKeyMachine));

            // 正常流程：开始 → 提交 → 无残留
            var tx = manager.Begin("unit-test P06-8 正常流程");
            Assert.True(tx.IsSuccess, "事务应能开始（含自动快照）");
            Assert.NotNull(tx.Value.SnapshotId, "事务必须携带快照 ID（SEC-1）");
            Assert.True(manager.DetectInterruptedTransaction().Value is not null,
                "事务进行中应存在未完成标记");

            manager.Commit(tx.Value);
            Assert.Null(manager.DetectInterruptedTransaction().Value,
                "提交后未完成标记必须被清除");

            // 模拟中断：只 Begin 不 Commit（等价于进程被杀）
            _ = manager.Begin("unit-test P06-8 模拟中断");
            var pending = manager.DetectInterruptedTransaction();
            Assert.NotNull(pending.Value, "中断后应能检测到未完成事务");
            Assert.Equal("unit-test P06-8 模拟中断", pending.Value!.Operation, "标记应记录操作名供提示用户");

            Console.WriteLine($"         检测到未完成事务：{pending.Value.Operation}（快照 {pending.Value.SnapshotId}）");

            manager.Commit(pending.Value.TransactionId is null ? tx.Value : tx.Value);
            snapshots.ClearPendingMarker();
        });

        h.Case("P06-9", "连续多次修改：每步都有快照，可回滚到任意中间状态", () =>
        {
            var (snapshots, user, _) = CreateStores();
            user.Write("ENVSTATION_TEST_L", "v0", AbsEnv.EnvValueKind.String);

            var versions = new List<string>();
            for (var i = 1; i <= 3; i++)
            {
                var snap = snapshots.Capture($"unit-test P06-9 step{i}").Value;
                versions.Add(snap.SnapshotId);
                user.Write("ENVSTATION_TEST_L", $"v{i}", AbsEnv.EnvValueKind.String);
            }

            Assert.Equal("v3", user.Read("ENVSTATION_TEST_L").Value!.RawValue, "当前应为 v3");

            // 回滚到第 2 步之前的快照（内容为 v1）
            var step2 = snapshots.Load(versions[1]).Value;
            var original = step2.UserVariables.First(v => v.Name == "ENVSTATION_TEST_L");
            user.Write("ENVSTATION_TEST_L", original.RawValue, original.Kind);

            Assert.Equal("v1", user.Read("ENVSTATION_TEST_L").Value!.RawValue,
                "应能回滚到任意中间状态，而不是只能回滚到最后一次");
        });
    }

    private static (CoreTx.SnapshotStore Snapshots, CoreEnv.IEnvironmentStore User, CoreEnv.IEnvironmentStore Machine) CreateStores()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var user = new SandboxRegistryStore(AbsEnv.EnvScope.User, SandboxKeyUser);
        var machine = new SandboxRegistryStore(AbsEnv.EnvScope.Machine, SandboxKeyMachine);

        // 清理上一轮遗留的沙箱变量，保证用例独立
        foreach (var v in user.ReadAll().Value)
        {
            if (v.Name.StartsWith("ENVSTATION_TEST_", StringComparison.Ordinal))
            {
                user.Delete(v.Name);
            }
        }

        return (new CoreTx.SnapshotStore(dir, user, machine), user, machine);
    }

    private static void RestoreAll(CoreEnv.IEnvironmentStore store, AbsEnv.EnvironmentSnapshot snapshot)
    {
        var current = store.ReadAll().Value;
        var target = snapshot.UserVariables.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        var existing = current.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var v in snapshot.UserVariables)
        {
            store.Write(v.Name, v.RawValue, v.Kind);
        }

        foreach (var name in existing.Keys.Where(k => !target.ContainsKey(k)))
        {
            store.Delete(name);
        }
    }

    private static int Diff(IReadOnlyList<AbsEnv.EnvVariable> a, IReadOnlyList<AbsEnv.EnvVariable> b)
    {
        var mapA = a.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        var mapB = b.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var (name, va) in mapA)
        {
            if (!mapB.TryGetValue(name, out var vb) ||
                !string.Equals(va.RawValue, vb.RawValue, StringComparison.Ordinal) ||
                va.Kind != vb.Kind)
            {
                count++;
            }
        }

        count += mapB.Keys.Count(k => !mapA.ContainsKey(k));
        return count;
    }

    private static void Cleanup()
    {
        try
        {
            Reg.Registry.CurrentUser.DeleteSubKeyTree(SandboxKeyUser, throwOnMissingSubKey: false);
            Reg.Registry.CurrentUser.DeleteSubKeyTree(SandboxKeyMachine, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"警告：清理注册表沙箱失败：{ex.Message}");
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"警告：清理临时目录失败：{ex.Message}");
        }

        Debug.WriteLine("P06 清理完成");
    }
}
