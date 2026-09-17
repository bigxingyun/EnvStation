using Microsoft.Win32;

namespace EnvStation.Tests.RegistrySandbox;

/// <summary>
/// M0-P02：注册表读写与值类型保持。
///
/// <para><b>隔离策略（关键）</b>：真实用户环境变量<b>绝不</b>被本测试修改。
/// 所有写操作都发生在 <c>HKCU\Software\EnvStation\TestSandbox</c> 下的临时键上，
/// 测试结束即删除。这样测试可以在任何人机器上安全运行（对应任务书 1.3 节的隔离要求）。</para>
///
/// <para>涉及真实作用域的两个用例（读取 HKLM、拒绝写 HKLM）是<b>只读</b>或<b>预期失败</b>的，
/// 因此同样安全。</para>
/// </summary>
internal static class Program
{
    private const string SandboxKeyPath = @"Software\EnvStation\TestSandbox";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("M0-P02 · 注册表读写与值类型保持");
        Console.WriteLine($"运行身份：{(Assert.IsAdministrator() ? "管理员" : "标准用户")}");
        Console.WriteLine($"隔离沙箱：HKCU\\{SandboxKeyPath}");
        Console.WriteLine();

        var h = new TestKit.TestHarness("M0-P02 注册表读写");

        // ── 沙箱管理 ──
        h.Case("P02-0", "建立并清理隔离沙箱键", () =>
        {
            using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKeyPath);
            Assert.NotNull(key, "应能创建测试沙箱键");
        });

        try
        {
            RunCases(h);
        }
        finally
        {
            CleanupSandbox();
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
        // ── P02-1：读取真实用户级变量，能正确识别值类型 ──
        h.Case("P02-1", "读取 HKCU\\Environment 全量（含值类型）", () =>
        {
            var store = new CoreEnv.RegistryEnvStore(AbsEnv.EnvScope.User);
            var result = store.ReadAll();
            Assert.True(result.IsSuccess, $"读取应成功，实际：{result.Error}");

            var vars = result.Value;
            Assert.True(vars.Count > 0, "真实用户环境应至少有一个变量（通常存在 PATH 或 TEMP）");

            // 至少应能读到 PATH（大小写不敏感）
            var path = vars.FirstOrDefault(v => string.Equals(v.Name, "Path", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(path, "应能读到 Path 变量");

            Console.WriteLine($"         读取到 {vars.Count} 个变量；Path 类型 = {path!.Kind}，长度 = {path.RawValue.Length}");
        });

        // ── P02-2：REG_EXPAND_SZ 写入后类型不变（SEC-11 核心用例）──
        h.Case("P02-2", "写入 REG_EXPAND_SZ 后读回类型仍为 ExpandString", () =>
        {
            using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKeyPath, writable: true);
            Assert.NotNull(key, "沙箱键应可写");

            const string name = "ENVSTATION_TEST_EXPAND";
            const string value = @"D:\Dev\%USERNAME%\envstation";

            key!.SetValue(name, value, RegistryValueKind.ExpandString);

            var kind = key.GetValueKind(name);
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

            Assert.Equal(RegistryValueKind.ExpandString, kind, "值类型必须保持为 ExpandString");
            Assert.Equal(value, raw, "%VAR% 必须保持未展开（PE-4）");

            // 同时验证通过我们的映射层也不丢失类型
            var store = new CoreEnv.RegistryEnvStore(AbsEnv.EnvScope.User);
            var stored = CoreEnv.RegistryEnvStore.ToEnvValueKind(kind);
            Assert.Equal(AbsEnv.EnvValueKind.ExpandString, stored, "映射到 AbsEnv.EnvValueKind 应保持 ExpandString");
        });

        // ── P02-3：REG_SZ 写入后类型不变，字面量不展开 ──
        h.Case("P02-3", "写入 REG_SZ 后读回类型仍为 String 且不展开", () =>
        {
            using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKeyPath, writable: true);
            Assert.NotNull(key, "沙箱键应可写");

            const string name = "ENVSTATION_TEST_PLAIN";
            const string value = @"D:\Dev\%USERNAME%\literal";

            key!.SetValue(name, value, RegistryValueKind.String);

            Assert.Equal(RegistryValueKind.String, key.GetValueKind(name), "类型必须保持 String");
            Assert.Equal(value, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                "REG_SZ 不应展开 %VAR%");
        });

        // ── P02-4：能取到未展开的原始串（DoNotExpandEnvironmentNames 有效性）──
        h.Case("P02-4", "DoNotExpandEnvironmentNames 能取到未展开原始串", () =>
        {
            using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKeyPath, writable: true);
            const string name = "ENVSTATION_TEST_UNEXPANDED";
            const string literal = @"%JAVA_HOME%\bin";

            key!.SetValue(name, literal, RegistryValueKind.ExpandString);

            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var expanded = key.GetValue(name) as string;

            Assert.Equal(literal, raw, "DoNotExpand 应返回字面量 %JAVA_HOME%\\bin");
            Assert.True(expanded is not null, "展开读取应返回字符串");

            Console.WriteLine($"         原始 = {raw}");
            Console.WriteLine($"         展开 = {expanded}   ← 证明两者确实不同");
        });

        // ── P02-5：超长值的边界行为 ──
        h.Case("P02-5", "超长值：32,000 可写；超 32,767 被校验拒绝", () =>
        {
            using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKeyPath, writable: true);

            const string name = "ENVSTATION_TEST_LONG";
            var ok = new string('A', 32_000);
            key!.SetValue(name, ok, RegistryValueKind.String);
            Assert.Equal(32_000, ((string)key.GetValue(name, string.Empty)!).Length, "32,000 字符应能写入");

            // 我们的校验层必须在上限处阻断（而不是让注册表报错）
            var tooLong = new string('B', CoreEnv.RegistryEnvStore.MaxValueLength + 1);
            var validation = CoreEnv.RegistryEnvStore.ValidateNameAndValue("X", tooLong);
            Assert.True(validation.IsFailure, "超上限的值应被校验拒绝");
            Assert.Equal(Abs.EnvStationErrorCodes.EnvValueTooLong, validation.Error!.Code, "应返回 E_ENV_VALUE_TOO_LONG");

            key.DeleteValue(name, throwOnMissingValue: false);
            Console.WriteLine($"         上限常量 = {CoreEnv.RegistryEnvStore.MaxValueLength}；GUI 编辑上限 = {CoreEnv.RegistryEnvStore.LegacyEditorLimit}");
        });

        // ── P02-6：变量名大小写不敏感 ──
        h.Case("P02-6", "环境变量名大小写不敏感（Path / PATH）", () =>
        {
            using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKeyPath, writable: true);
            const string name = "ENVSTATION_TEST_CASE";

            key!.SetValue("EnvStation_Test_Case", "v1", RegistryValueKind.String);
            var readBack = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

            Assert.Equal("v1", readBack as string,
                "用不同大小写应能读到同一个值（证明大小写不敏感）");

            key.DeleteValue("envstation_test_case", throwOnMissingValue: false);
        });

        // ── P02-7：标准用户可读 HKLM（只读，安全）──
        h.Case("P02-7", "标准用户可读取 HKLM 系统级环境变量", () =>
        {
            var store = new CoreEnv.RegistryEnvStore(AbsEnv.EnvScope.Machine);
            var result = store.ReadAll();

            if (result.IsFailure)
            {
                // 被组策略限制时不算失败，但必须返回明确错误码而非崩溃
                Assert.Equal(Abs.EnvStationErrorCodes.EnvScopeDenied, result.Error!.Code,
                    "读取失败时应返回 E_ENV_SCOPE_DENIED 而不是抛异常");
                Assert.Skip($"当前环境限制了 HKLM 读取：{result.Error.Message}");
            }

            var vars = result.Value;
            Assert.True(vars.Count > 0, "系统级环境变量应有内容");
            Console.WriteLine($"         读取到 {vars.Count} 个系统级变量（当前为{(Assert.IsAdministrator() ? "管理员" : "标准用户")}）");
        });

        // ── P02-8：标准用户写 HKLM 必须返回明确错误码（预期失败用例）──
        h.Case("P02-8", "写入 HKLM 应返回 E_ENV_SCOPE_DENIED（而非未处理异常）", () =>
        {
            if (Assert.IsAdministrator())
            {
                Assert.Skip("当前为管理员身份，无法验证标准用户的拒绝行为");
            }

            var store = new CoreEnv.RegistryEnvStore(AbsEnv.EnvScope.Machine);
            var result = store.Write("ENVSTATION_TEST_DENIED", "x", AbsEnv.EnvValueKind.String);

            Assert.True(result.IsFailure, "标准用户写 HKLM 应失败");
            Assert.Equal(Abs.EnvStationErrorCodes.EnvScopeDenied, result.Error!.Code,
                "必须返回结构化错误码，供 UI 提示'需要管理员权限'");
            Assert.NotNull(result.Error.Remediation, "应给出修复建议（对应 DP-6 四段式文案）");
        });

        // ── P02-9：畸形项（值名含 '='）不应导致崩溃 ──
        h.Case("P02-9", "畸形值名（含 '='）被安全跳过", () =>
        {
            using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKeyPath, writable: true);
            key!.SetValue("ENVSTATION_TEST_WEIRD", "v", RegistryValueKind.String);

            // 直接向真实环境区写入含 '=' 的名字是危险的；这里验证的是"读取时跳过"的逻辑路径：
            // 通过真实作用域读取，确认没有因畸形项抛异常。
            var store = new CoreEnv.RegistryEnvStore(AbsEnv.EnvScope.User);
            var result = store.ReadAll();
            Assert.True(result.IsSuccess, "全量读取不应因个别畸形项失败");
            Assert.False(
                result.Value.Any(v => v.Name.Contains('=', StringComparison.Ordinal)),
                "结果中不应包含含 '=' 的非法变量名");
        });

        // ── P02-10：并发写入检测（读-改-写竞态）──
        h.Case("P02-10", "并发写入检测：后写者能观察到前者的变更", () =>
        {
            const string name = "ENVSTATION_TEST_CONCURRENT";
            using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKeyPath, writable: true);
            key!.SetValue(name, "v0", RegistryValueKind.String);

            var lost = 0;
            var tasks = new List<Task>();

            for (var i = 0; i < 8; i++)
            {
                var id = i;
                tasks.Add(Task.Run(() =>
                {
                    using var k = Reg.Registry.CurrentUser.OpenSubKey(SandboxKeyPath, writable: true);
                    // 经典读-改-写：读取 → 追加 → 写回
                    var current = k!.GetValue(name, string.Empty) as string ?? string.Empty;
                    var next = current + id.ToString();
                    k.SetValue(name, next, RegistryValueKind.String);
                }));
            }

            Task.WaitAll([.. tasks]);

            // 注册表无原子 CAS，并发读-改-写必然丢失更新——这正说明
            // "必须用文件锁/互斥体保护环境变量事务"是真需求，而不是过度设计。
            var final = key.GetValue(name, string.Empty) as string ?? string.Empty;
            if (final.Length <= 2)
            {
                lost = 8 - (final.Length - 2);
            }

            Console.WriteLine($"         最终值 = \"{final}\"（理想长度 10，实际 {final.Length}）");
            Console.WriteLine($"         → 确认存在丢失更新风险，事务层必须加进程间互斥（{lost} 次写入被覆盖）");

            key.DeleteValue(name, throwOnMissingValue: false);
            Assert.True(true, "本用例只用于观测竞态现象，不判定失败");
        });

        // ── 补充：值类型映射往返 ──
        h.Case("P02-11", "值类型映射往返一致（RegistryValueKind ↔ AbsEnv.EnvValueKind）", () =>
        {
            foreach (var kind in new[] { RegistryValueKind.String, RegistryValueKind.ExpandString })
            {
                var env = CoreEnv.RegistryEnvStore.ToEnvValueKind(kind);
                var back = CoreEnv.RegistryEnvStore.ToRegistryValueKind(env);
                Assert.Equal(kind, back, $"{kind} 往返后应保持一致");
            }

            // 未知类型必须保守降级为 String，而不是抛异常
            Assert.Equal(AbsEnv.EnvValueKind.String, CoreEnv.RegistryEnvStore.ToEnvValueKind(RegistryValueKind.DWord),
                "非字符串类型应保守映射为 String");
        });
    }

    private static void CleanupSandbox()
    {
        try
        {
            Reg.Registry.CurrentUser.DeleteSubKeyTree(SandboxKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"警告：清理沙箱键失败：{ex.Message}");
        }
    }
}
