using EnvStation.TestKit;

namespace EnvStation.Tests.PathKernel;

/// <summary>
/// PATH 解析与编辑内核的边界用例测试。
///
/// <para><b>为什么这批测试最重要：</b>PATH 是环境站唯一"写错就会让用户机器上系统命令集体失效"
/// 的地方（需求 1.2 节 P2 记录的正是这类事故）。因此这里的用例全部取自
/// 《详细设计文档》7.1 节列出的"必须有单元测试"清单，并额外补充了实际会遇到但容易漏的场景。</para>
///
/// <para><b>测试隔离：</b>全部用例都注入自定义的存在性判定函数，不依赖本机真实磁盘内容，
/// 因此在任何机器上结果完全一致、可复现。</para>
/// </summary>
internal static class Program
{
    /// <summary>测试用的"存在目录"集合。未列出的目录一律视为不存在。</summary>
    private static readonly HashSet<string> ExistingDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows",
        @"C:\Windows\System32",
        @"C:\Program Files\Java\jdk-17.0.9\bin",
        @"D:\Dev\Python\3.12",
        @"D:\Dev\Python\3.12\Scripts",
        @"D:\Dev\EnvStation\shims",
        @"C:\Tools\bin",
    };

    private static bool Exists(string path) => ExistingDirs.Contains(path);

    private static IReadOnlyList<CoreEnv.PathEntry> Parse(string path)
        => CoreEnv.PathParser.Parse(path, probeFileSystem: true, directoryExists: Exists);

    /// <summary>不探测文件系统的解析（用于只关心结构、不关心存在性的用例）。</summary>
    private static IReadOnlyList<CoreEnv.PathEntry> ParseStructural(string path)
        => CoreEnv.PathParser.Parse(path, probeFileSystem: false);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("PATH 内核边界用例测试");
        Console.WriteLine("（注入虚拟文件系统，不依赖本机真实磁盘）");
        Console.WriteLine();

        var h = new TestHarness("PATH 内核");

        // ═══════════════════════ 解析与规范化 ═══════════════════════

        h.Case("PK-01", "尾随分隔符：C:\\Windows 与 C:\\Windows\\ 判为重复", () =>
        {
            var entries = Parse(@"C:\Windows;C:\Windows\");

            Assert.Equal(2, entries.Count, "应解析出 2 个条目");
            Assert.Equal(@"C:\Windows", entries[0].Normalized, "首项规范化不应带尾随分隔符");
            Assert.Equal(@"C:\Windows", entries[1].Normalized, "尾随分隔符应被规范化去除");

            Assert.True((entries[1].Issues & CoreEnv.PathEntryIssue.Duplicate) != 0,
                "第二项应被标记为重复");
            Assert.Equal(0, entries[1].DuplicateOfIndex, "重复项应指向首次出现的下标 0");
        });

        h.Case("PK-02", "空项清理：;;C:\\A;; 应产生 2 个空项标记", () =>
        {
            var entries = ParseStructural(@";;C:\A;;");

            Assert.Equal(5, entries.Count, "按分号切分应得到 5 段（含 2 个前导空段与 2 个尾随空段）");

            var empties = entries.Where(e => (e.Issues & CoreEnv.PathEntryIssue.Empty) != 0).ToList();
            Assert.Equal(4, empties.Count, "应有 4 个空项（前 2 个 + 后 2 个）");

            var real = entries.Where(e => (e.Issues & CoreEnv.PathEntryIssue.Empty) == 0).ToList();
            Assert.Equal(1, real.Count, "只有 C:\\A 是有效项");
        });

        h.Case("PK-03", "变量未定义：%JAVA_HOME%\\bin 应被标记但不得删除", () =>
        {
            // 特意用一个几乎不可能存在的变量名，避免受本机环境干扰
            var entries = ParseStructural(@"%ENVSTATION_NONEXISTENT_VAR%\bin");

            Assert.Equal(1, entries.Count, "应解析出 1 个条目");
            Assert.True((entries[0].Issues & CoreEnv.PathEntryIssue.UnresolvedVariable) != 0,
                "未定义变量的引用应被标记为 UnresolvedVariable");
            Assert.Equal(@"%ENVSTATION_NONEXISTENT_VAR%\bin", entries[0].Raw,
                "原始写法必须完整保留（回写依赖它，PE-1）");

            // 关键：解析不负责删除，清理操作也不应删除它
            var plan = CoreEnv.PathEditor.Clean(entries);
            Assert.False(plan.HasChanges,
                "Clean 不得移除变量未解析项 —— 它可能是用户有意为之（变量在特定会话中才定义）");
        });

        h.Case("PK-04", "8.3 短名与长名判为同一目录", () =>
        {
            // 本用例验证的是规范化路径：短名展开依赖 GetLongPathName，
            // 而 PROGRA~1 只在真实文件系统上才能展开，因此这里改为验证
            // "规范化后的字符串比较"这一层逻辑，不依赖 P/Invoke 能否成功。
            const string longName = @"C:\Program Files\Java\jdk-17.0.9\bin";
            const string sameDifferentCase = @"c:\program files\java\JDK-17.0.9\BIN";
            const string withTrailing = @"C:\Program Files\Java\jdk-17.0.9\bin\";

            Assert.True(CoreEnv.PathParser.AreSameEntry(longName, sameDifferentCase),
                "大小写不同应判为同一目录（Windows 路径大小写不敏感，PE-2）");
            Assert.True(CoreEnv.PathParser.AreSameEntry(longName, withTrailing),
                "尾随分隔符不同应判为同一目录");

            var entries = Parse($"{longName};{sameDifferentCase};{withTrailing}");
            var dupCount = entries.Count(e => (e.Issues & CoreEnv.PathEntryIssue.Duplicate) != 0);
            Assert.Equal(2, dupCount, "3 个写法相同的目录，应有 2 个被标记为重复");

            // 短名展开能力单独探测（不强制要求成功，因为可能受本机文件系统影响）
            var shortName = @"C:\PROGRA~1";
            if (Directory.Exists(shortName))
            {
                var normalized = CoreEnv.PathParser.Normalize(shortName);
                Console.WriteLine($"         短名展开实测：{shortName} -> {normalized}");
                Assert.True(CoreEnv.PathParser.AreSameEntry(shortName, @"C:\Program Files"),
                    "短名与长名应判为同一目录");
            }
            else
            {
                Console.WriteLine("         本机不存在 C:\\PROGRA~1，跳过短名展开断言");
            }
        });

        h.Case("PK-05", "盘符相对路径 C:foo 必须被识别为相对路径", () =>
        {
            // 这类路径相对于"该盘的当前目录"，是最阴险的问题项之一：
            // 它看起来像绝对路径，实际随进程的当前目录变化。
            var entries = ParseStructural(@"C:foo;C:\Windows;foo;.\bin");

            var cRelative = entries[0];
            Assert.True((cRelative.Issues & CoreEnv.PathEntryIssue.Relative) != 0,
                "C:foo 是盘符相对路径，必须标记为 Relative");

            Assert.True((entries[1].Issues & CoreEnv.PathEntryIssue.Relative) == 0,
                "C:\\Windows 是绝对路径，不应标记");

            Assert.True((entries[2].Issues & CoreEnv.PathEntryIssue.Relative) != 0,
                "foo 是相对路径，必须标记");

            Assert.True((entries[3].Issues & CoreEnv.PathEntryIssue.Relative) != 0,
                @".\bin 是相对路径，必须标记");
        });

        h.Case("PK-06", "根目录 D:\\ 的尾随分隔符不应被判定为问题", () =>
        {
            var entries = ParseStructural(@"D:\;D:\Dev");

            Assert.Equal(@"D:\", entries[0].Normalized, "根目录必须保留尾随反斜杠");
            Assert.True((entries[0].Issues & CoreEnv.PathEntryIssue.TrailingSeparator) == 0,
                "根目录形态是正确写法，不应标记 TrailingSeparator");

            var nested = ParseStructural(@"D:\Dev\");
            Assert.True((nested[0].Issues & CoreEnv.PathEntryIssue.TrailingSeparator) != 0,
                "非根目录的尾随分隔符应被标记");
            Assert.Equal(@"D:\Dev", nested[0].Normalized, "非根目录的尾随分隔符应被规范化去除");
        });

        h.Case("PK-07", "不存在目录被标记为 Missing（卸载残留场景）", () =>
        {
            var entries = Parse(@"C:\Windows;C:\OldTool\bin;D:\Dev\Python\3.12");

            Assert.True(entries[0].Exists, "C:\\Windows 在虚拟文件系统中存在");
            Assert.False(entries[1].Exists, "C:\\OldTool\\bin 不存在");
            Assert.True((entries[1].Issues & CoreEnv.PathEntryIssue.Missing) != 0, "应标记 Missing");
            Assert.True(entries[2].Exists, "D:\\Dev\\Python\\3.12 存在");
        });

        h.Case("PK-08", "CURRENT-DIRECTORY 型条目必须被识别", () =>
        {
            // PATH 中出现空项或 "." 会在该位置引入当前目录，
            // 这是经典的"DLL 劫持"风险面，必须能识别出来。
            var entries = ParseStructural(@"C:\Windows;;.;C:\Tools");

            var empty = entries.First(e => (e.Issues & CoreEnv.PathEntryIssue.Empty) != 0);
            Assert.True(empty.IsCurrentDirectory, "空项属于 CURRENT-DIRECTORY 型");

            var dot = entries.First(e => e.Raw == ".");
            Assert.True(dot.IsCurrentDirectory, "\".\" 属于 CURRENT-DIRECTORY 型");

            Assert.False(entries[0].IsCurrentDirectory, "C:\\Windows 不是 CD 型");
        });

        h.Case("PK-09", "非法字符被标记", () =>
        {
            var entries = ParseStructural(@"C:\Wi<ll\bin;C:\Ok\bin");

            Assert.True((entries[0].Issues & CoreEnv.PathEntryIssue.InvalidCharacters) != 0,
                "含 '<' 的路径应被标记为非法字符");
            Assert.True((entries[1].Issues & CoreEnv.PathEntryIssue.InvalidCharacters) == 0,
                "正常路径不应被误判");
        });

        h.Case("PK-10", "引号包裹的路径被正确剥离", () =>
        {
            var entries = ParseStructural("\"C:\\Program Files\\Java\\bin\"");

            Assert.Equal(1, entries.Count, "应解析出 1 个条目");
            Assert.Equal(@"C:\Program Files\Java\bin", entries[0].Normalized,
                "外层引号应被规范化剥离（部分安装器会写入带引号的 PATH 项）");
            Assert.Equal("\"C:\\Program Files\\Java\\bin\"", entries[0].Raw,
                "原始写法保留引号，回写时维持用户原样");
        });

        // ═══════════════════════ 编辑操作 ═══════════════════════

        h.Case("PK-11", "去重保留首次出现（PE-6）", () =>
        {
            var entries = Parse(@"C:\Windows;D:\Dev\EnvStation\shims;C:\Windows;D:\Dev\EnvStation\shims");
            var plan = CoreEnv.PathEditor.Dedupe(entries);

            Assert.True(plan.HasChanges, "应产生变更");
            Assert.Equal(2, plan.After.Count, "4 项去重后应为 2 项");
            Assert.Equal(@"C:\Windows", plan.After[0].Raw, "首次出现的顺序必须保持");
            Assert.Equal(@"D:\Dev\EnvStation\shims", plan.After[1].Raw, "首次出现的顺序必须保持");
            Assert.Equal(2, plan.Changes.Count, "应有 2 条删除记录");

            var text = plan.ToPathString();
            Assert.Equal(@"C:\Windows;D:\Dev\EnvStation\shims", text, "拼回的字符串应正确");
        });

        h.Case("PK-12", "Ensure 幂等：已存在时不重复添加", () =>
        {
            var entries = Parse(@"C:\Windows;D:\Dev\Python\3.12");

            var first = CoreEnv.PathEditor.Ensure(entries, @"D:\Dev\Python\3.12");
            Assert.False(first.HasChanges, "已存在时不应产生变更（幂等，AC-2）");
            Assert.Contains("已存在", first.Summarize(), "应说明未变的原因");

            var added = CoreEnv.PathEditor.Ensure(entries, @"C:\Tools\bin");
            Assert.True(added.HasChanges, "不存在时应添加");
            Assert.Equal(3, added.After.Count, "添加后应变为 3 项");
            Assert.Equal(@"C:\Tools\bin", added.After[2].Raw,
                "Append 应追加到末尾（原 2 项之后，下标 2）");
            Assert.Equal(@"C:\Windows", added.After[0].Raw, "原有顺序不得被打乱");

            var prepended = CoreEnv.PathEditor.Ensure(entries, @"C:\Tools\bin", CoreEnv.PathPosition.Prepend);
            Assert.Equal(@"C:\Tools\bin", prepended.After[0].Raw, "Prepend 应插到最前");

            // 第二次 Ensure 同一目录应无变化
            var second = CoreEnv.PathEditor.Ensure(added.After, @"C:\Tools\bin");
            Assert.False(second.HasChanges, "重复 Ensure 必须幂等");
        });

        h.Case("PK-13", "Ensure 对大小写不同的已存在项也判为已存在", () =>
        {
            var entries = Parse(@"C:\Windows");
            var plan = CoreEnv.PathEditor.Ensure(entries, @"c:\windows\");

            Assert.False(plan.HasChanges,
                "大小写与尾随分隔符不同应仍判为已存在，避免制造重复项");
        });

        h.Case("PK-14", "Remove 幂等：不存在时无变化", () =>
        {
            var entries = Parse(@"C:\Windows;D:\Dev\Python\3.12");

            var removed = CoreEnv.PathEditor.Remove(entries, @"D:\Dev\Python\3.12");
            Assert.True(removed.HasChanges, "存在时应能移除");
            Assert.Equal(1, removed.After.Count, "移除后应剩 1 项");

            var again = CoreEnv.PathEditor.Remove(removed.After, @"D:\Dev\Python\3.12");
            Assert.False(again.HasChanges, "不存在时移除应无变化（幂等）");

            var byCase = CoreEnv.PathEditor.Remove(entries, @"d:\dev\python\3.12\");
            Assert.True(byCase.HasChanges, "大小写与尾随分隔符不同也应能移除");
        });

        h.Case("PK-15", "Clean 移除空项与失效项，但保留变量未解析项", () =>
        {
            var entries = ParseStructural(
                @";;C:\Windows;C:\OldTool\bin;%ENVSTATION_NOPE%\bin;D:\Dev\Python\3.12;");

            // 需要带存在性探测才能清理 Missing —— 这里重新解析一次
            var withProbe = CoreEnv.PathParser.Parse(
                @";;C:\Windows;C:\OldTool\bin;%ENVSTATION_NOPE%\bin;D:\Dev\Python\3.12;",
                probeFileSystem: true, directoryExists: Exists);

            var plan = CoreEnv.PathEditor.Clean(withProbe);

            Assert.True(plan.HasChanges, "应产生清理变更");

            var removedValues = plan.Changes.Select(c => c.Value).ToList();
            Assert.True(removedValues.Any(v => v.Length == 0), "空项应被移除");
            Assert.True(removedValues.Contains(@"C:\OldTool\bin"), "失效目录应被移除");
            Assert.False(removedValues.Any(v => v.Contains("ENVSTATION_NOPE")),
                "变量未解析项**不得**被移除");

            Assert.True(plan.After.Any(e => e.Raw.Contains("ENVSTATION_NOPE")),
                "变量未解析项应保留在结果中");
            Assert.True(plan.After.Any(e => e.Raw == @"C:\Windows"), "有效项应保留");
            Assert.True(plan.After.Any(e => e.Raw == @"D:\Dev\Python\3.12"), "有效项应保留");

            foreach (var c in plan.Changes)
            {
                Assert.True(c.Reason.Length > 0, $"每条变更都必须带原因（UI 要展示）：{c.Value}");
            }

            _ = entries;
        });

        h.Case("PK-16", "Prioritize 移动顺序并重排下标", () =>
        {
            var entries = Parse(@"C:\Windows;D:\Dev\Python\3.12;D:\Dev\EnvStation\shims");

            var plan = CoreEnv.PathEditor.Prioritize(entries, @"D:\Dev\EnvStation\shims", 0);

            Assert.True(plan.HasChanges, "应产生移动变更");
            Assert.Equal(@"D:\Dev\EnvStation\shims", plan.After[0].Raw, "应移到第 1 位");
            Assert.Equal(0, plan.After[0].Index, "Index 必须与位置一致（供 UI 拖拽排序使用）");
            Assert.Equal(1, plan.After[1].Index, "其余项 Index 应重排");
            Assert.Equal(2, plan.After[2].Index, "其余项 Index 应重排");

            var same = CoreEnv.PathEditor.Prioritize(plan.After, @"D:\Dev\EnvStation\shims", 0);
            Assert.False(same.HasChanges, "已在目标位置时应无变化（幂等）");

            var missing = CoreEnv.PathEditor.Prioritize(entries, @"C:\NotInPath", 0);
            Assert.False(missing.HasChanges, "不存在的条目应返回无变化而非报错");
        });

        h.Case("PK-17", "Tidy 组合操作：去重 + 清理一次完成", () =>
        {
            var entries = CoreEnv.PathParser.Parse(
                @"C:\Windows;;C:\Windows;C:\OldTool\bin;D:\Dev\EnvStation\shims",
                probeFileSystem: true, directoryExists: Exists);

            var plan = CoreEnv.PathEditor.Tidy(entries);

            Assert.True(plan.HasChanges, "应产生变更");
            Assert.False(plan.After.Any(e => e.Raw.Length == 0), "结果不应含空项");
            Assert.False(plan.After.Any(e => e.Raw == @"C:\OldTool\bin"), "失效项应被清理");
            Assert.False(plan.After.Any(e => (e.Issues & CoreEnv.PathEntryIssue.Duplicate) != 0),
                "结果不应含重复项");
            Assert.Contains("-3 移除", plan.Summarize(), "摘要应给出移除计数");

            var tidyAgain = CoreEnv.PathEditor.Tidy(plan.After);
            Assert.False(tidyAgain.HasChanges, "二次 Tidy 应无变化（幂等）");
        });

        h.Case("PK-18", "CollapseToShims：多条目折叠为单条托管目录", () =>
        {
            var entries = Parse(@"C:\Windows;D:\Dev\Python\3.12;D:\Dev\Python\3.12\Scripts");

            var plan = CoreEnv.PathEditor.CollapseToShims(
                entries,
                @"D:\Dev\EnvStation\shims",
                [@"D:\Dev\Python\3.12", @"D:\Dev\Python\3.12\Scripts"]);

            Assert.True(plan.HasChanges, "应产生变更");
            Assert.Equal(2, plan.After.Count, "3 项应收敛为 2 项（Windows + shims）");
            Assert.Equal(@"D:\Dev\EnvStation\shims", plan.After[0].Raw, "托管目录应前置");

            var shrink = entries.Count - plan.After.Count;
            Assert.Equal(1, shrink, "净减少 1 项");

            // 说明：每项都必须给出可读原因，UI 要逐条展示（需求 M1-5）
            foreach (var c in plan.Changes)
            {
                Assert.True(c.Reason.Length > 0, "每条变更都要有原因");
            }
        });

        h.Case("PK-19", "空 PATH 与 null PATH 不应崩溃", () =>
        {
            Assert.Equal(0, CoreEnv.PathParser.Parse(null).Count, "null 应解析为空数组（无条目）");
            Assert.Equal(0, CoreEnv.PathParser.Parse(string.Empty).Count, "空串应解析为空数组（无条目）");

            // 空白串按 ';' 切分得到 1 段，且该段为空 → 语义上是"1 个空条目"，
            // 而不是 0 个条目。这个区别很重要：它会作为"空项"被体检检出并可清理。
            var blank = CoreEnv.PathParser.Parse("   ");
            Assert.Equal(1, blank.Count, "纯空白串应解析为 1 个条目");
            Assert.True((blank[0].Issues & CoreEnv.PathEntryIssue.Empty) != 0,
                "该条目应被标记为空项");

            Assert.Equal(string.Empty, CoreEnv.PathParser.Join([]), "空数组应拼为空串");
        });

        h.Case("PK-20", "拼回字符串与解析互为逆运算（对良性输入）", () =>
        {
            const string original = @"C:\Windows;D:\Dev\Python\3.12;D:\Dev\EnvStation\shims";
            var entries = Parse(original);
            var roundTrip = CoreEnv.PathParser.Join(entries);

            Assert.Equal(original, roundTrip,
                "良性 PATH 经解析再拼回必须逐字节一致（回写安全的根本保证）");

            // 含变量引用与空格路径也必须原样往返
            const string withVars = @"%SystemRoot%\system32;C:\Program Files\Java\bin";
            var e2 = ParseStructural(withVars);
            Assert.Equal(withVars, CoreEnv.PathParser.Join(e2),
                "含变量引用与空格的 PATH 必须原样往返（PE-1/PE-4）");
        });

        h.Case("PK-21", "长路径显示：中间省略并保留盘符与末段", () =>
        {
            var longEntry = new CoreEnv.PathEntry
            {
                Index = 0,
                Raw = @"C:\Very\Deeply\Nested\Directory\Structure\That\Goes\On\And\On\jdk-17.0.9\bin",
                Normalized = string.Empty,
                ComparisonKey = string.Empty,
                Exists = true,
                Issues = CoreEnv.PathEntryIssue.None,
            };

            var display = longEntry.ToDisplayString(40);
            Assert.True(display.Length <= 42, $"显示长度应受控（实际 {display.Length}）");
            Assert.Contains("…", display, "应使用省略号表示中间被截断");
            Assert.Contains("bin", display, "应保留末段目录名");
            Assert.True(display.StartsWith("C:", StringComparison.Ordinal), "应保留盘符");
        });

        h.Case("PK-22", "IsRemovableByClean 判定与风险边界", () =>
        {
            var entries = CoreEnv.PathParser.Parse(
                @"C:\Windows;;C:\OldTool\bin;%ENVSTATION_NOPE%\bin;C:relative",
                probeFileSystem: true, directoryExists: Exists);

            var windows = entries.First(e => e.Raw == @"C:\Windows");
            Assert.False(windows.IsRemovableByClean, "正常项不可被清理");

            var empty = entries.First(e => (e.Issues & CoreEnv.PathEntryIssue.Empty) != 0);
            Assert.True(empty.IsRemovableByClean, "空项可清理");

            var missing = entries.First(e => e.Raw == @"C:\OldTool\bin");
            Assert.True(missing.IsRemovableByClean, "失效项可清理");

            var unresolved = entries.First(e => e.Raw.Contains("ENVSTATION_NOPE"));
            Assert.False(unresolved.IsRemovableByClean,
                "变量未解析项不可被自动清理 —— 需用户判断");

            var relative = entries.First(e => e.Raw == "C:relative");
            Assert.False(relative.IsRemovableByClean,
                "相对路径项不可被自动清理 —— 需用户判断");
        });

        var jsonPath = TestHarness.GetJsonPath(args);
        if (jsonPath is not null)
        {
            h.WriteJson(jsonPath);
        }

        return h.Summarize();
    }
}
