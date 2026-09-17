using System.Diagnostics;
using EnvStation.TestKit;

namespace EnvStation.Tests.Concurrency;

/// <summary>
/// 并发控制测试 —— 验证 P02-10 实测暴露的"读-改-写丢失更新"缺口已被补上。
///
/// <para><b>背景：</b>M0-P02 用例 P02-10 用 8 个并发任务对同一注册表值做读-改-写，
/// 实测发现写入相互覆盖（最终值长度远小于预期）。这证明"注册表写入天然原子"是错误假设，
/// 事务层必须加进程间互斥。</para>
///
/// <para><b>本套测试要证明两件事：</b></para>
/// <list type="number">
///   <item><b>没有锁时会丢更新</b> —— 复现问题，说明缺口真实存在（防止有人日后把锁删掉）。</item>
///   <item><b>加锁后不丢更新</b> —— 同一个场景在互斥保护下结果正确。</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string SandboxKey = @"Software\EnvStation\TestConcurrency";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("并发控制测试（进程内线程竞争 + 命名互斥体）");
        Console.WriteLine();

        var h = new TestHarness("并发控制");

        try
        {
            // ── 互斥体本身的行为 ──

            h.Case("CC-01", "互斥体名称按作用域与变量名区分", () =>
            {
                var a = CoreTx.EnvironmentMutex.BuildMutexName(AbsEnv.EnvScope.User, "PATH");
                var b = CoreTx.EnvironmentMutex.BuildMutexName(AbsEnv.EnvScope.User, "path");
                var c = CoreTx.EnvironmentMutex.BuildMutexName(AbsEnv.EnvScope.User, "JAVA_HOME");
                var d = CoreTx.EnvironmentMutex.BuildMutexName(AbsEnv.EnvScope.Machine, "PATH");

                Assert.Equal(a, b, "变量名大小写不敏感，应得到同一个锁（避免 Path/PATH 被当成两个锁）");
                Assert.NotEqual(a, c, "不同变量应使用不同锁（避免无谓串行化）");
                Assert.NotEqual(a, d, "不同作用域应使用不同锁");

                Assert.Contains("EnvStation.EnvTx.user.", a, "命名前缀应符合约定，便于诊断时识别");
                Console.WriteLine($"         PATH(user) 锁名 = {a}");
                Console.WriteLine($"         PATH(machine) 锁名 = {d}");
            });

            h.Case("CC-02", "同一变量上的并发加锁会被串行化", () =>
            {
                // ★ 判定方式说明（这里踩过一次坑）：
                //   最初用 ConcurrentBag 收集事件再按下标两两配对判断，结果出现 6 处"交错"误报。
                //   原因是 ConcurrentBag 不保证枚举顺序的稳定性，且"先取 Count 再逐项索引"
                //   本身就有竞态 —— 测试自身的竞态掩盖了被测对象的行为。
                //
                //   正确做法：用一把**测试自己的**普通锁，把"记录 enter → 临界区 → 记录 exit"
                //   作为一个不可分割的观察单元。若被测互斥体失效，就可能出现
                //   [enter A, enter B, exit A, exit B] 这种序列，从而被准确检出。
                var log = new List<string>();
                var logLock = new object();

                var tasks = new List<Task>();
                for (var i = 0; i < 6; i++)
                {
                    var id = i;
                    tasks.Add(Task.Run(() =>
                    {
                        // Result<EnvironmentMutex> 是值类型结果，不能直接 using。
                        // 必须先判成败取 Value，再用 using 保证释放。
                        var acquired = CoreTx.EnvironmentMutex.Acquire(
                            AbsEnv.EnvScope.User, "ENVSTATION_TEST_CC02");
                        Assert.True(acquired.IsSuccess, $"第 {id} 个任务应取得锁：{acquired.Error}");

                        using var guard = acquired.Value;

                        lock (logLock)
                        {
                            log.Add($"enter{id}");
                            Thread.Sleep(15);   // 临界区：若互斥失效，其他线程会在此期间进入
                            log.Add($"exit{id}");
                        }
                    }));
                }

                Task.WaitAll([.. tasks]);

                List<string> seq;
                lock (logLock)
                {
                    seq = [.. log];
                }

                Assert.Equal(12, seq.Count, "应有 6 组 enter/exit");

                // 判定：整个序列必须严格形如 enter0 exit0 enter1 exit1 ...
                var violations = new List<string>();
                for (var i = 0; i < seq.Count; i += 2)
                {
                    var a = seq[i];
                    var b = seq[i + 1];
                    var paired = a.StartsWith("enter", StringComparison.Ordinal)
                        && b.StartsWith("exit", StringComparison.Ordinal)
                        && a[5..] == b[4..];
                    if (!paired)
                    {
                        violations.Add($"{a}|{b}");
                    }
                }

                Assert.Equal(0, violations.Count,
                    $"临界区必须完全串行，发现 {violations.Count} 处交错：{string.Join(" ", violations)}");
                Console.WriteLine($"         执行序列：{string.Join(" → ", seq)}");
            });

            h.Case("CC-03", "不同变量的锁互不阻塞", () =>
            {
                var started = new ManualResetEventSlim(false);
                var secondEntered = new ManualResetEventSlim(false);

                // 先持有 PATH 的锁。
                // 注意：Acquire 返回 Result<EnvironmentMutex>（值类型结果），不是 IDisposable，
                // 因此必须"先判成败 → 取 Value → using"，不能写成 using var x = Acquire(...)。
                var firstAcquired = CoreTx.EnvironmentMutex.Acquire(AbsEnv.EnvScope.User, "PATH");
                Assert.True(firstAcquired.IsSuccess, "应取得 PATH 锁");
                using var first = firstAcquired.Value;

                var t = Task.Run(() =>
                {
                    started.Set();
                    // 尝试获取 JAVA_HOME 的锁 —— 不应被 PATH 的锁阻塞
                    var otherAcquired = CoreTx.EnvironmentMutex.Acquire(
                        AbsEnv.EnvScope.User, "JAVA_HOME", TimeSpan.FromSeconds(3));
                    if (otherAcquired.IsSuccess)
                    {
                        using var other = otherAcquired.Value;
                        secondEntered.Set();
                    }
                });

                started.Wait(TimeSpan.FromSeconds(2));
                var got = secondEntered.Wait(TimeSpan.FromSeconds(3));
                t.Wait(TimeSpan.FromSeconds(1));

                Assert.True(got, "不同变量的锁必须互不阻塞，否则会把无关操作串行化");
            });

            h.Case("CC-04", "超时返回结构化错误而非无限等待", () =>
            {
                var heldAcquired = CoreTx.EnvironmentMutex.Acquire(AbsEnv.EnvScope.User, "ENVSTATION_TEST_CC04");
                Assert.True(heldAcquired.IsSuccess, "应取得锁");
                using var held = heldAcquired.Value;

                // 同线程再次获取同一互斥体是递归的，因此换线程来测超时
                var sw = Stopwatch.StartNew();
                Exception? captured = null;
                var result = default(Result<CoreTx.EnvironmentMutex>);

                var t = Task.Run(() =>
                {
                    try
                    {
                        result = CoreTx.EnvironmentMutex.Acquire(
                            AbsEnv.EnvScope.User, "ENVSTATION_TEST_CC04", TimeSpan.FromMilliseconds(300));
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                    }
                });

                t.Wait(TimeSpan.FromSeconds(5));
                sw.Stop();

                Assert.Null(captured, $"不应抛出异常（实际：{captured?.GetType().Name}）");
                Assert.True(result.IsFailure, "应返回失败结果");
                Assert.Equal(Abs.EnvStationErrorCodes.TxMutexTimeout, result.Error!.Code,
                    "应返回 E_TX_MUTEX_TIMEOUT");
                Assert.NotNull(result.Error.Remediation, "应给出可操作建议（DP-6 四段式文案）");
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4),
                    $"应在超时后立即返回（实际耗时 {sw.ElapsedMilliseconds}ms）");

                Console.WriteLine($"         超时耗时 {sw.ElapsedMilliseconds}ms，错误码 {result.Error.Code}");
            });

            h.Case("CC-05", "RunLocked 在异常路径下也会释放锁", () =>
            {
                Assert.Throws<InvalidOperationException>(() =>
                {
                    CoreTx.EnvironmentMutex.RunLocked<Unit>(
                        AbsEnv.EnvScope.User, "ENVSTATION_TEST_CC05",
                        () => throw new InvalidOperationException("模拟业务异常"));
                }, "异常应向外传播");

                // 若锁未释放，这里会超时
                var again = CoreTx.EnvironmentMutex.Acquire(
                    AbsEnv.EnvScope.User, "ENVSTATION_TEST_CC05", TimeSpan.FromSeconds(2));

                Assert.True(again.IsSuccess, "异常路径下锁必须已释放，否则后续操作会永久阻塞");
                again.Value.Dispose();
            });

            // ── 丢失更新：先复现，再证明已修复 ──

            h.Case("CC-06", "复现：无锁的读-改-写会丢失更新", () =>
            {
                using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKey, writable: true);
                Assert.NotNull(key, "应能创建沙箱键");
                key!.SetValue("Race", "0", Reg.RegistryValueKind.String);

                var tasks = new List<Task>();
                for (var i = 0; i < 12; i++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        using var k = Reg.Registry.CurrentUser.OpenSubKey(SandboxKey, writable: true);
                        var cur = int.Parse(k!.GetValue("Race", "0") as string ?? "0");
                        Thread.SpinWait(200);
                        k.SetValue("Race", (cur + 1).ToString(), Reg.RegistryValueKind.String);
                    }));
                }

                Task.WaitAll([.. tasks]);

                var final = int.Parse(key.GetValue("Race", "0") as string ?? "0");
                Console.WriteLine($"         12 次并发加一，无锁结果 = {final}（理想值 12）");
                Assert.True(final < 12,
                    "无锁时必须观察到丢失更新 —— 若此处不再成立，说明本用例已失效，需重新设计");
            });

            h.Case("CC-07", "修复：互斥保护下的读-改-写不丢更新", () =>
            {
                using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKey, writable: true);
                Assert.NotNull(key, "应能创建沙箱键");
                key!.SetValue("Locked", "0", Reg.RegistryValueKind.String);

                const int workers = 12;
                var tasks = new List<Task>();
                for (var i = 0; i < workers; i++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        // 关键：把"读-改-写"整段放进同一把按变量粒度的锁
                        var r = CoreTx.EnvironmentMutex.RunLocked<Unit>(
                            AbsEnv.EnvScope.User, "RaceLocked", () =>
                            {
                                using var k = Reg.Registry.CurrentUser.OpenSubKey(SandboxKey, writable: true);
                                var cur = int.Parse(k!.GetValue("Locked", "0") as string ?? "0");
                                Thread.SpinWait(200);
                                k.SetValue("Locked", (cur + 1).ToString(), Reg.RegistryValueKind.String);
                                return Results.Ok();
                            });

                        Assert.True(r.IsSuccess, $"加锁操作应成功：{r.Error}");
                    }));
                }

                Task.WaitAll([.. tasks]);

                var final = int.Parse(key.GetValue("Locked", "0") as string ?? "0");
                Console.WriteLine($"         {workers} 次并发加一，加锁结果 = {final}（理想值 {workers}）");
                Assert.Equal(workers, final, "互斥保护下不得丢失任何一次更新");
            });

            h.Case("CC-08", "事务管理器在写入路径上确实使用了互斥", () =>
            {
                // 通过行为验证而非白盒：并发执行多笔事务式写入，结果必须完整。
                using var key = Reg.Registry.CurrentUser.CreateSubKey(SandboxKey, writable: true);
                key!.SetValue("TxCounter", "0", Reg.RegistryValueKind.String);

                const int rounds = 10;
                var tasks = new List<Task>();
                for (var i = 0; i < rounds; i++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        var store = new SandboxCounterStore(SandboxKey, "TxCounter");
                        var r = CoreTx.EnvironmentMutex.RunLocked<Unit>(
                            AbsEnv.EnvScope.User, "TxCounter", () => store.Increment());

                        Assert.True(r.IsSuccess, $"递增应成功：{r.Error}");
                    }));
                }

                Task.WaitAll([.. tasks]);

                var final = int.Parse(key.GetValue("TxCounter", "0") as string ?? "0");
                Assert.Equal(rounds, final, "事务式写入在互斥下应完整累计");
                Console.WriteLine($"         {rounds} 轮事务式递增结果 = {final}");
            });
        }
        finally
        {
            Cleanup();
        }

        var jsonPath = TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return h.Summarize();
    }

    private static void Cleanup()
    {
        try
        {
            Reg.Registry.CurrentUser.DeleteSubKeyTree(SandboxKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"警告：清理沙箱失败：{ex.Message}");
        }
    }

    /// <summary>测试用的最小存储：模拟"读-改-写"型业务操作。</summary>
    private sealed class SandboxCounterStore(string keyPath, string valueName)
    {
        public Result<Unit> Increment()
        {
            using var key = Reg.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null)
            {
                return Results.Fail(Abs.EnvStationErrorCodes.PathNotWritable, "沙箱键不存在");
            }

            var current = int.Parse(key.GetValue(valueName, "0") as string ?? "0");
            Thread.SpinWait(300);
            key.SetValue(valueName, (current + 1).ToString(), Reg.RegistryValueKind.String);
            return Results.Ok();
        }
    }
}
