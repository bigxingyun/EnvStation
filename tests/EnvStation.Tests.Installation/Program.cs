
namespace EnvStation.Tests.Installation;

/// <summary>
/// M0-P05：影子目录与原子切换。
/// 对应任务书 P05-1 ~ P05-8，全部在临时目录中进行。
/// </summary>
internal static class Program
{
    private static string _root = string.Empty;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("M0-P05 · 影子目录与原子切换");

        _root = Path.Combine(Path.GetTempPath(), "envstation-p05-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Console.WriteLine($"隔离沙箱根：{_root}");

        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
        Console.WriteLine($"可用固定磁盘：{string.Join(", ", drives)}");
        Console.WriteLine();

        var h = new TestKit.TestHarness("M0-P05 影子目录与原子切换");

        try
        {
            RunCases(h, drives);
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

    private static void RunCases(TestKit.TestHarness h, List<string> drives)
    {
        h.Case("P05-1", "同卷原子切换：target 从不存在到完整可用", () =>
        {
            var (installer, stagingRoot, target) = SetupSameVolume();

            var plan = installer.CreatePlan("tx-p05-1", target).Value;
            Assert.Equal(CoreInstall.SwitchStrategy.AtomicSameVolume, plan.Strategy,
                "staging 与 target 同卷时应选择原子改名策略");

            CoreInstall.ShadowInstaller.Stage(plan);
            WritePayload(plan.StagingPath, "python.exe", "v3.12.4", fileCount: 40);

            // 提交前：目标必须完全不存在（证明确实"零污染"）
            Assert.False(Directory.Exists(target), "提交前目标目录不得存在");

            var commit = installer.CommitAsync(plan).GetAwaiter().GetResult();
            Assert.True(commit.IsSuccess, $"同卷切换应成功：{commit.Error}");
            Assert.True(commit.Value.Succeeded, "结果应标记成功");
            Assert.True(Directory.Exists(target), "切换后目标目录应存在");
            // 注意：WritePayload 会额外写 1 个标记文件，故总数为 fileCount + 1
            Assert.Equal(41, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length,
                "目标目录文件数应与影子目录一致（40 个填充文件 + 1 个标记文件）");
            Assert.False(Directory.Exists(plan.StagingPath), "成功后影子目录应消失（已被改名）");

            Console.WriteLine($"         切换耗时 {commit.Value.Elapsed.TotalMilliseconds:F0}ms（40 个文件）");
            _ = stagingRoot;
        });

        h.Case("P05-2", "跨卷降级：自动选择 StagedCrossVolume 并成功", () =>
        {
            var other = drives.FirstOrDefault(d =>
                !string.Equals(d, Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase));

            if (other is null)
            {
                Assert.Skip("本机只有一个固定磁盘，无法验证跨卷路径");
            }

            // staging 放在 _root（当前卷），target 放在另一卷
            var otherRoot = Path.Combine(other!, "envstation-p05-cross");
            var target = Path.Combine(otherRoot, "target");
            var installer = new CoreInstall.ShadowInstaller(Path.Combine(_root, "staging-cross"));

            try
            {
                var plan = installer.CreatePlan("tx-p05-2", target).Value;
                Assert.Equal(CoreInstall.SwitchStrategy.StagedCrossVolume, plan.Strategy,
                    "跨卷时必须选择降级策略（否则 Directory.Move 会失败）");

                CoreInstall.ShadowInstaller.Stage(plan);
                WritePayload(plan.StagingPath, "tool.exe", "cross-volume", fileCount: 12);

                var commit = installer.CommitAsync(plan).GetAwaiter().GetResult();
                Assert.True(commit.IsSuccess, $"跨卷切换应成功：{commit.Error}");
                Assert.Equal(13, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length,
                    "跨卷切换后文件应完整（12 个填充文件 + 1 个标记文件）");
                Assert.False(Directory.Exists(plan.SwapTempPath), "交换临时目录应被清理");

                Console.WriteLine($"         跨卷路径：{Path.GetPathRoot(_root)} → {other}");
            }
            finally
            {
                TryDelete(otherRoot);
            }
        });

        h.Case("P05-3", "目标已存在（升级）：旧目录被备份后替换，失败可还原", () =>
        {
            var (installer, _, target) = SetupSameVolume();

            // 先放一个"旧版本"
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "version.txt"), "v1-old");

            var plan = installer.CreatePlan("tx-p05-3", target).Value;
            CoreInstall.ShadowInstaller.Stage(plan);
            WritePayload(plan.StagingPath, "version.txt", "v2-new", fileCount: 5);

            var commit = installer.CommitAsync(plan).GetAwaiter().GetResult();
            Assert.True(commit.IsSuccess, $"升级切换应成功：{commit.Error}");
            Assert.True(commit.Value.ReplacedExistingTarget, "应标记为替换了已存在的目录");

            var content = File.ReadAllText(Path.Combine(target, "version.txt"));
            Assert.Equal("v2-new", content, "目标目录内容应为新版本");
            Assert.False(Directory.Exists(plan.BackupPath), "成功后备份目录应被清理");
        });

        h.Case("P05-4", "取消提交：目标保持原状，影子目录被丢弃", () =>
        {
            var (installer, _, target) = SetupSameVolume();

            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "marker.txt"), "original");

            var plan = installer.CreatePlan("tx-p05-4", target).Value;
            CoreInstall.ShadowInstaller.Stage(plan);
            WritePayload(plan.StagingPath, "marker.txt", "would-be-new", fileCount: 3);

            // 模拟用户在确认前取消
            var discard = CoreInstall.ShadowInstaller.Discard(plan);
            Assert.True(discard.IsSuccess, "丢弃应成功");
            Assert.False(Directory.Exists(plan.StagingPath), "影子目录应被删除");
            Assert.Equal("original", File.ReadAllText(Path.Combine(target, "marker.txt")),
                "目标目录内容必须保持原样");
        });

        h.Case("P05-5", "中途崩溃：Recover 能还原目标或清理残留", () =>
        {
            var (_, _, target) = SetupSameVolume();

            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "marker.txt"), "original");

            // 注入故障：备份完成后、改名之前"崩溃"
            var crashing = new CoreInstall.ShadowInstaller(
                Path.Combine(_root, "staging-crash"),
                fault => throw new InvalidOperationException("模拟切换中途进程被杀"));

            var plan = crashing.CreatePlan("tx-p05-5", target).Value;
            CoreInstall.ShadowInstaller.Stage(plan);
            WritePayload(plan.StagingPath, "marker.txt", "new-version", fileCount: 3);

            var commit = crashing.CommitAsync(plan).GetAwaiter().GetResult();
            Assert.True(commit.IsFailure, "注入了故障，提交应失败");

            // 故障发生在改名之前，因此目标此刻可能已被移为 .bak
            var targetExists = Directory.Exists(target);
            var backupExists = Directory.Exists(plan.BackupPath);
            Console.WriteLine($"         故障后状态：target={targetExists}, bak={backupExists}");

            // 恢复：无论处于哪种中间态，Recover 都必须让目标可用
            var cleaned = CoreInstall.ShadowInstaller.Recover(target);
            Console.WriteLine($"         Recover 清理：{(cleaned.Count == 0 ? "无需清理" : string.Join(", ", cleaned.Select(Path.GetFileName)))}");

            Assert.True(Directory.Exists(target), "恢复后目标目录必须存在且可用");
            Assert.Equal("original", File.ReadAllText(Path.Combine(target, "marker.txt")),
                "恢复后目标内容应为崩溃前的原始内容");
            Assert.False(Directory.Exists(plan.BackupPath), "恢复后不应残留备份目录");
        });

        h.Case("P05-6", "目标被占用：切换失败并给出可操作的错误", () =>
        {
            var (installer, _, target) = SetupSameVolume();

            Directory.CreateDirectory(target);
            var locked = Path.Combine(target, "locked.bin");

            var plan = installer.CreatePlan("tx-p05-6", target).Value;
            CoreInstall.ShadowInstaller.Stage(plan);
            WritePayload(plan.StagingPath, "new.txt", "new", fileCount: 3);

            // 持续持有文件句柄，模拟"程序正在运行"
            using var handle = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            var commit = installer.CommitAsync(plan).GetAwaiter().GetResult();

            if (commit.IsSuccess)
            {
                // 某些 Windows 配置下目录改名不受文件句柄影响，这本身是有价值的结论
                Console.WriteLine("         注意：本机允许在文件被占用时改名目录 —— 该结论需记录到 P05 报告");
                Assert.True(Directory.Exists(target), "目标应存在");
            }
            else
            {
                Assert.NotNull(commit.Error, "失败必须返回结构化错误");
                Assert.Contains("占用", commit.Error!.Remediation ?? string.Empty,
                    "错误建议应说明'目标被占用'并提示关闭程序（DP-6 四段式）");
                Assert.True(Directory.Exists(target), "失败的切换不得破坏目标目录");
                Console.WriteLine($"         失败路径已验证：{commit.Error.Message}");
            }
        });

        h.Case("P05-7", "根目录保护：拒绝把磁盘根作为安装目标", () =>
        {
            var installer = new CoreInstall.ShadowInstaller(Path.Combine(_root, "staging-root"));

            foreach (var drive in drives)
            {
                var plan = installer.CreatePlan("tx-p05-7", drive);
                Assert.True(plan.IsFailure, $"必须拒绝把 {drive} 作为安装目标");
                Assert.Equal(Abs.EnvStationErrorCodes.PathOutsideAuthorizedRoot, plan.Error!.Code,
                    "应返回 E_PATH_OUTSIDE_ROOT");
            }
        });

        h.Case("P05-8", "非法路径：超长与含非法字符被前置拦截", () =>
        {
            var installer = new CoreInstall.ShadowInstaller(Path.Combine(_root, "staging-invalid"));

            var tooLong = @"C:\" + new string('x', 300);
            var plan = installer.CreatePlan("tx-p05-8", tooLong);
            Assert.True(plan.IsFailure, "超长路径应在创建计划阶段被拒绝");

            var empty = installer.CreatePlan("tx-p05-8b", "   ");
            Assert.True(empty.IsFailure, "空白路径应被拒绝");

            var emptyTx = installer.CreatePlan(string.Empty, Path.Combine(_root, "x"));
            Assert.True(emptyTx.IsFailure, "空事务 ID 应被拒绝");
        });
    }

    private static (CoreInstall.ShadowInstaller Installer, string StagingRoot, string Target) SetupSameVolume()
    {
        var caseRoot = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        var stagingRoot = Path.Combine(caseRoot, "staging");
        var target = Path.Combine(caseRoot, "target");
        Directory.CreateDirectory(caseRoot);
        return (new CoreInstall.ShadowInstaller(stagingRoot), stagingRoot, target);
    }

    private static void WritePayload(string stagingPath, string markerFile, string markerContent, int fileCount)
    {
        Directory.CreateDirectory(stagingPath);
        File.WriteAllText(Path.Combine(stagingPath, markerFile), markerContent);

        for (var i = 0; i < fileCount; i++)
        {
            var sub = Path.Combine(stagingPath, "lib", $"mod{i % 4}");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, $"file{i}.bin"), $"payload-{i}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    private static void Cleanup() => TryDelete(_root);
}
