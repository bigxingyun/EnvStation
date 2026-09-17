using System.Collections.Immutable;
using System.Globalization;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Transactions;
using EnvStation.Core.Actions.Builtin;

namespace EnvStation.Core.Diagnostics;

/// <summary>
/// 把一次只读检测的结论翻译成待办项（含修复动作）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这一层</b>：检测与修复两个能力早就都在内核里，中间那根线从来没接。
/// 最刺眼的例子是「同名命令冲突」——<c>envstation.detect.conflict</c> 能查出 <c>yarn</c> 被 Hadoop 抢走，
/// 而专门用来消解它的 <c>envstation.path.prioritize</c> 就在同一个工程里，却从未被引用过
/// （见《重构与优化方案.md》3.3 节）。
/// </para>
/// <para>
/// <b>口径纪律（对应重构方案 X-2）</b>：每个检测项要么在这里声明它的修复动作，
/// 要么显式登记为「只提示、不可自动修复」。两者都没有的检测项由用例拦下——
/// 不允许再出现"检测得到、界面上却只能干看"的情况。
/// </para>
/// <para>
/// <b>判据来源</b>：所有输出键都取自各动作自己声明的 <c>Outputs</c>，本类不另立一套键名。
/// 键名写错会静默失效（读不到就是 0），因此每条映射都有对应用例盯着。
/// </para>
/// </remarks>
public static class RemedyCatalog
{
    /// <summary>PATH 中必须存在、缺了就可能导致系统命令不可用的目录。</summary>
    private static readonly ImmutableArray<string> RequiredPathDirectories =
        [@"%SystemRoot%\System32", @"%SystemRoot%"];

    /// <summary>已知前置依赖 id → 对应的安装途径说明。</summary>
    private static readonly ImmutableDictionary<string, string> DependencyGuides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vcredist-2015-2022-x64"] = "从微软官网下载「Visual C++ 2015-2022 可再发行组件（x64）」安装。",
            ["vcredist-2013-x64"] = "从微软官网下载 Visual C++ 2013 可再发行组件（x64）安装。",
            ["vcredist-2010-x64"] = "从微软官网下载 Visual C++ 2010 可再发行组件（x64）安装。",
            ["dotnet-desktop-8"] = "安装 .NET 8 桌面运行时。",
            ["webview2"] = "安装 Microsoft Edge WebView2 运行时（Windows 11 与较新的 Windows 10 已内置）。",
            ["powershell-7"] = "安装 PowerShell 7。",
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 由一次检测动作的结果产出待办项。
    /// </summary>
    /// <param name="actionId">检测动作 ID。</param>
    /// <param name="outputs">动作声明的输出键值。</param>
    /// <param name="scope">作用域（<c>user</c> / <c>machine</c>），非作用域检测传空。</param>
    public static ImmutableArray<RemedyItem> FromDetection(
        string actionId,
        IReadOnlyDictionary<string, string> outputs,
        string scope = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(outputs);

        if (ReportOnly.TryGetValue(actionId, out _))
        {
            return [];
        }

        return Judges.TryGetValue(actionId, out var judge) ? judge(outputs, scope) : [];
    }

    /// <summary>已登记判据的检测项（动作 ID → 判定函数）。</summary>
    /// <remarks>
    /// <b>为什么是三张表要合成一张</b>：早先"有哪些检测项"这件事写在三个地方
    /// （<c>FromDetection</c> 的 <c>switch</c>、<c>IsMapped</c> 的 <c>switch</c>、
    /// <c>MappedDetections</c> 的数组），加一个检测项要改三处，漏一处就是
    /// "检测得出问题、界面上只显示一行字"。现在只有这一张表，另外两个 API 从它派生。
    /// </remarks>
    private static readonly ImmutableDictionary<string, Func<IReadOnlyDictionary<string, string>, string, ImmutableArray<RemedyItem>>> Judges =
        new Dictionary<string, Func<IReadOnlyDictionary<string, string>, string, ImmutableArray<RemedyItem>>>(StringComparer.Ordinal)
        {
            ["envstation.detect.os"] = (outputs, _) => FromOs(outputs),
            ["envstation.detect.arch"] = (outputs, _) => FromArch(outputs),
            ["envstation.detect.command"] = (outputs, _) => FromCommand(outputs),
            ["envstation.detect.runtime"] = (outputs, _) => FromRuntime(outputs),
            ["envstation.detect.disk"] = (outputs, _) => FromDisk(outputs),
            ["envstation.detect.deps"] = (outputs, _) => FromDependencies(outputs),
            ["envstation.detect.network"] = (outputs, _) => FromNetwork(outputs),
            ["envstation.detect.conflict"] = (outputs, _) => FromConflicts(outputs),
            ["envstation.detect.env"] = (outputs, _) => FromEnvironment(outputs),
            ["envstation.path.validate"] = (outputs, scope) => FromPathValidate(outputs, scope),
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 显式声明"只报告、不产出待办项"的检测项 → 理由。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要有这张表</b>：一个检测项如果既没有判据、又没有在这里声明，
    /// 那么它检出的问题在界面上就是<b>静默消失</b>的——用户看不到，我们也不知道。
    /// 用例遍历动作注册表，要求每个检测类动作要么在 <see cref="Judges"/> 里，要么在这里，
    /// 两者都没有即失败。
    /// </para>
    /// <para>
    /// 目前为空：全部 10 个检测项都有判据。空表不是"这张表没用"——
    /// 它是下一个新增检测项必须经过的那道门。
    /// </para>
    /// </remarks>
    public static ImmutableDictionary<string, string> ReportOnly { get; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>该检测项是否已登记判据（产出待办项，或显式声明只报告）。未登记者由用例拦下。</summary>
    public static bool IsMapped(string actionId) =>
        Judges.ContainsKey(actionId) || ReportOnly.ContainsKey(actionId);

    /// <summary>当前已登记判据的检测项（用例据此比对动作注册表，防止新增检测项时漏接）。</summary>
    public static ImmutableArray<string> MappedDetections { get; } =
        [.. Judges.Keys.Order(StringComparer.Ordinal)];

    private static ImmutableArray<RemedyItem> FromOs(IReadOnlyDictionary<string, string> outputs)
    {
        if (!IsTrue(outputs, "supported"))
        {
            var build = Get(outputs, "build");
            return
            [
                RemedyItem.Notice(
                    "os.unsupported",
                    RemedySeverity.Critical,
                    "系统版本低于最低要求",
                    $"当前系统构建号为 {Or(build, "未知")}。",
                    "环境站要求 Windows 10 1809（构建 17763）或更高版本。",
                    "低于该版本的系统缺少环境站依赖的若干系统接口，安装与回滚流程可能中途失败。",
                    "envstation.detect.os",
                    RiskLevel.Safe),
            ];
        }

        return [];
    }

    private static ImmutableArray<RemedyItem> FromPathValidate(
        IReadOnlyDictionary<string, string> outputs,
        string scope)
    {
        var items = ImmutableArray.CreateBuilder<RemedyItem>();
        var scopeName = scope.Length > 0 ? scope : Get(outputs, "scope");
        var scopeLabel = scopeName switch
        {
            "machine" => "系统级",
            "user" => "用户级",
            _ => "合并后的",
        };
        var scopeArgument = scopeName.Length > 0 ? scopeName : "user";

        var missing = Count(outputs, "missing_count");
        var duplicates = Count(outputs, "duplicate_count");
        var empty = Count(outputs, "empty_count");
        var unresolved = Count(outputs, "unresolved_count");

        if (empty > 0)
        {
            items.Add(new RemedyItem(
                $"path.{scopeName}.empty",
                RemedySeverity.Critical,
                $"{scopeLabel} PATH 中存在 {empty} 个空条目",
                "PATH 里有一项是空的（常见于多余的分号）。",
                "空条目会被 Windows 解释为「当前目录」。",
                "在任意目录下执行命令时，系统会先在该目录里找同名程序——这既是安全隐患，也会让命令解析变得不可预期。",
                RemediationPlan.Single(
                    RemedyStep.Of("envstation.path.clean", $"移除 {scopeLabel} PATH 中的空条目",
                        RemedyArgument.Of("scope", scopeArgument),
                        RemedyArgument.Of("include_duplicates", false)),
                    $"清理 {scopeLabel} PATH 中的空条目"),
                RiskLevel.Reversible,
                "envstation.path.validate"));
        }

        if (missing > 0)
        {
            items.Add(new RemedyItem(
                $"path.{scopeName}.missing",
                RemedySeverity.Warning,
                $"{scopeLabel} PATH 中有 {missing} 项指向不存在的目录",
                "PATH 里列着一些已经被删除或移动的目录。",
                "多数是卸载软件时残留，少数是安装目录被移走。",
                "每一项都要在命令解析时被检查一次，既拖慢解析，也让「到底哪个版本生效」更难判断。",
                RemediationPlan.Single(
                    RemedyStep.Of("envstation.path.clean", $"移除 {scopeLabel} PATH 中失效的目录",
                        RemedyArgument.Of("scope", scopeArgument),
                        RemedyArgument.Of("include_duplicates", false)),
                    $"清理 {scopeLabel} PATH 中已不存在的目录"),
                RiskLevel.Reversible,
                "envstation.path.validate"));
        }

        if (duplicates > 0)
        {
            items.Add(new RemedyItem(
                $"path.{scopeName}.duplicate",
                RemedySeverity.Warning,
                $"{scopeLabel} PATH 中有 {duplicates} 项重复",
                "同一个目录在 PATH 里出现了多次。",
                "多次安装或手工追加时没有先去重。",
                "PATH 变长并接近旧版编辑对话框的上限，一旦用系统自带界面编辑过就可能被静默截断。",
                RemediationPlan.Single(
                    RemedyStep.Of("envstation.path.dedupe", $"去掉 {scopeLabel} PATH 中的重复项",
                        RemedyArgument.Of("scope", scopeArgument)),
                    $"保留首次出现，去掉 {scopeLabel} PATH 中的重复项"),
                RiskLevel.Reversible,
                "envstation.path.validate"));
        }

        if (unresolved > 0)
        {
            items.Add(RemedyItem.Notice(
                $"path.{scopeName}.unresolved",
                RemedySeverity.Warning,
                $"{scopeLabel} PATH 中有 {unresolved} 项含未展开的变量",
                "PATH 里存在引用已经不存在变量的条目。",
                "引用的变量被删除或改名了。",
                "这些条目在命令解析时不会指向任何真实目录。",
                "envstation.path.validate",
                RiskLevel.Safe));
        }

        if (IsTrue(outputs, "over_legacy_limit"))
        {
            items.Add(RemedyItem.Notice(
                $"path.{scopeName}.over-legacy-limit",
                RemedySeverity.Warning,
                $"{scopeLabel} PATH 长度已超过旧版编辑上限",
                $"当前长度 {Or(Get(outputs, "length"), "未知")} 个字符。",
                "Windows 传统的环境变量编辑对话框对 PATH 有 2047 字符的编辑限制。",
                "一旦用系统自带界面打开并保存，超出的部分会被静默截断且无法找回。",
                "envstation.path.validate",
                RiskLevel.Safe));
        }

        // 系统目录缺失是单独一类：它不属于"某个软件装坏了"，而是 PATH 整体被覆盖过。
        var details = Get(outputs, "details");
        if (scopeName is "machine" or "merged" or ""
            && missing > 0
            && RequiredPathDirectories.Any(dir => !PathTextContains(details, dir)))
        {
            items.Add(RemedyItem.Notice(
                $"path.{scopeName}.system-dirs",
                RemedySeverity.Critical,
                "PATH 中可能缺少系统目录",
                "在 PATH 的异常项里没有看到 %SystemRoot%\\System32。",
                "某个软件在写入 PATH 时整体覆盖了原值。",
                "ipconfig、ping、where 等系统自带命令会找不到，且影响全机。",
                "envstation.path.validate",
                RiskLevel.Safe));
        }

        return items.ToImmutable();
    }

    private static ImmutableArray<RemedyItem> FromDependencies(IReadOnlyDictionary<string, string> outputs)
    {
        var missingCount = Count(outputs, "missing_count");
        if (missingCount == 0)
        {
            return [];
        }

        var builders = ImmutableArray.CreateBuilder<RemedyItem>();
        var ids = Get(outputs, "missing")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var id in ids)
        {
            var guide = DependencyGuides.TryGetValue(id, out var text)
                ? text
                : "该组件不在已知清单内，需要手工确认安装途径。";

            builders.Add(RemedyItem.Notice(
                $"dep.{id}",
                RemedySeverity.Warning,
                $"缺少前置依赖：{id}",
                $"{id} 未在本机检出。",
                "该组件没有被安装，或安装信息未登记到系统。",
                $"{guide}缺少它时依赖它的程序会在运行时才报错，且报错信息通常与真正原因无关。",
                "envstation.detect.deps",
                RiskLevel.Safe));
        }

        return builders.ToImmutable();
    }

    private static ImmutableArray<RemedyItem> FromConflicts(IReadOnlyDictionary<string, string> outputs)
    {
        var count = Count(outputs, "conflict_count");
        if (count == 0)
        {
            return [];
        }

        var conflicts = Get(outputs, "conflicts")
            .Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var builders = ImmutableArray.CreateBuilder<RemedyItem>();
        for (var i = 0; i < conflicts.Length; i++)
        {
            builders.Add(RemedyItem.Notice(
                $"conflict.{i}",
                RemedySeverity.Warning,
                "存在同名命令冲突",
                conflicts[i],
                "多个软件的安装目录提供同名命令，谁先生效由 PATH 顺序决定。",
                "你可能以为在用其中一个工具，实际调用的是另一个，表现为「某个命令行为诡异」。",
                "envstation.detect.conflict",
                RiskLevel.High));
        }

        return builders.ToImmutable();
    }

    /// <summary>
    /// 架构检测：进程架构与系统架构不一致时，程序正跑在模拟模式下。
    /// </summary>
    /// <remarks>
    /// 需求 5.8 要求"及时制止"：在 ARM64 上用 x64 仿真跑环境站，装的却是原生组件，
    /// 结果通常是"装完了但用不了"。因此这一条必须成一条待办项，而不是界面上一个不起眼的角标。
    /// </remarks>
    private static ImmutableArray<RemedyItem> FromArch(IReadOnlyDictionary<string, string> outputs)
    {
        if (!IsTrue(outputs, "emulated"))
        {
            return [];
        }

        var process = Or(Get(outputs, "process_arch"), "未知");
        var os = Or(Get(outputs, "os_arch"), "未知");

        return
        [
            RemedyItem.Notice(
                "arch.emulated",
                RemedySeverity.Warning,
                "环境站正以模拟方式运行",
                $"当前进程架构为 {process}，而系统架构是 {os}。",
                "在 64 位系统上运行了另一个指令集的程序，Windows 会自动用模拟层承载它。",
                "模拟层下的进程无法可靠判断本机架构，安装原生组件时可能选错版本（典型结果是装完但用不了）。" +
                $"请改用与系统架构匹配的环境站版本（{os}）。",
                "envstation.detect.arch",
                RiskLevel.Safe),
        ];
    }

    /// <summary>命令未找到时给出去哪里装的具体去处。</summary>
    private static readonly ImmutableDictionary<string, string> CommandGuides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["winget"] = "Windows 11 与较新的 Windows 10 自带「应用安装程序」（即 winget）。若已被卸载，可在 Microsoft Store 里重新安装「应用安装程序」。",
            ["git"] = "从 git-scm.com 下载 Git for Windows 安装。",
            ["python"] = "从 python.org 下载安装，或在环境站的运行时页选择版本安装。",
            ["python3"] = "从 python.org 下载安装，或在环境站的运行时页选择版本安装。",
            ["java"] = "安装 JDK（如 Eclipse Temurin 或 Microsoft Build of OpenJDK）。",
            ["javac"] = "安装 JDK（只装 JRE 不会有 javac）。",
            ["node"] = "从 nodejs.org 下载 LTS 版本安装，或用 winget 安装 OpenJS.NodeJS.LTS。",
            ["npm"] = "npm 随 Node.js 一起安装，请先装 Node.js。",
            ["dotnet"] = "安装 .NET SDK。",
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 命令解析追踪：找不到命令时给出去处。
    /// </summary>
    /// <remarks>
    /// 这一条是安装链路的<b>前置事实</b>：winget 不在就装不了任何东西，
    /// 而此前界面与命令行都只是把"未找到"打印出来，用户拿不到下一步。
    /// </remarks>
    private static ImmutableArray<RemedyItem> FromCommand(IReadOnlyDictionary<string, string> outputs)
    {
        if (IsTrue(outputs, "found"))
        {
            return [];
        }

        var command = Get(outputs, "command");
        var name = command.Length > 0 ? command : "目标命令";
        var guide = CommandGuides.TryGetValue(name, out var text)
            ? text
            : "该命令没有安装，或它所在的目录不在 PATH 中。";

        return
        [
            RemedyItem.Notice(
                $"command.{name}.missing",
                RemedySeverity.Warning,
                $"未找到命令 {name}",
                $"在{EnvironmentPathResolver.ToScopeLabel(Or(Get(outputs, "scope"), "merged"))} PATH 中没有解析到 {name}。",
                "程序未安装，或安装后没有把所在目录加入 PATH。",
                $"{guide}依赖它的流程会在调用时才失败，而不是在检查阶段。",
                "envstation.detect.command",
                RiskLevel.Safe),
        ];
    }

    /// <summary>
    /// 磁盘检测：空间不足与"真实写入测试未通过"分开报。
    /// </summary>
    /// <remarks>
    /// 两者都是阻断级（需求 5.8），但处置完全不同：一个要腾地方，另一个要换位置或改权限。
    /// 合成一条会让用户去删文件却依然写不进去。
    /// </remarks>
    private static ImmutableArray<RemedyItem> FromDisk(IReadOnlyDictionary<string, string> outputs)
    {
        var items = ImmutableArray.CreateBuilder<RemedyItem>();
        var drive = Or(Get(outputs, "drive"), "目标磁盘");

        if (!IsTrue(outputs, "enough"))
        {
            items.Add(RemedyItem.Notice(
                "disk.insufficient",
                RemedySeverity.Critical,
                $"磁盘 {drive} 空间不足",
                $"可用 {Get(outputs, "available_bytes")} 字节，而本次需要 {Get(outputs, "required_bytes")} 字节（含 1.5 倍余量）。",
                "目标盘剩余空间低于安装所需。",
                "装到一半空间耗尽会留下半成品环境，而且此时回滚本身也可能因为没空间而失败。请先腾出空间或改到别的磁盘。",
                "envstation.detect.disk",
                RiskLevel.Safe));
        }

        if (!IsTrue(outputs, "writable"))
        {
            items.Add(RemedyItem.Notice(
                "disk.not-writable",
                RemedySeverity.Critical,
                "目标位置写入测试未通过",
                Or(Get(outputs, "write_reason"), $"在 {Get(outputs, "probed_path")} 写入并删除测试文件失败。"),
                "前置检查真的写了一个文件再删掉（需求 V6），因此这不是推测：这个位置当前写不进去。",
                "常见原因是目录只读、权限被组策略限制、被安全软件拦截，或上级路径其实是一个文件。" +
                "换一个用户可写的位置，或调整权限后重试。",
                "envstation.detect.disk",
                RiskLevel.Safe));
        }

        return items.ToImmutable();
    }

    /// <summary>网络检测：部分或全部主机不可达。</summary>
    private static ImmutableArray<RemedyItem> FromNetwork(IReadOnlyDictionary<string, string> outputs)
    {
        var total = Count(outputs, "total");
        var reachable = Count(outputs, "reachable_count");
        if (total <= 0 || reachable >= total)
        {
            return [];
        }

        var severity = reachable == 0 ? RemedySeverity.Critical : RemedySeverity.Warning;

        return
        [
            RemedyItem.Notice(
                "network.unreachable",
                severity,
                reachable == 0 ? "所有检测目标都不可达" : $"{total - reachable} 个检测目标不可达",
                $"{reachable}/{total} 个主机可达：{Get(outputs, "results")}",
                "网络不通、被代理拦截，或目标站点本身故障。探测会校验 TLS 证书，"
                + "因此「能连上但证书不对」（透明代理、DNS 泛解析）同样判为不可达。",
                reachable == 0
                    ? "在线安装与镜像下载都无法进行，请改用本地归档安装包，或先解决网络。"
                    : "相关下载可能很慢或失败，可在设置里换用镜像源，或改用本地归档安装包。",
                "envstation.detect.network",
                RiskLevel.Safe),
        ];
    }

    /// <summary>
    /// 环境变量读取：读取失败与"请求的变量未定义"分开报。
    /// </summary>
    /// <remarks>
    /// 刻意不把"变量未定义"一律当问题：只有调用方<b>点名要</b>的变量没读到才算问题
    /// （检测本身没有"这个变量该不该有"的上下文）。因此按明细行里是否出现"未定义"来判断，
    /// 而不是按 <c>found_count</c>——列全部变量时读到 0 个只说明这台机器的环境被清空了。
    /// </remarks>
    private static ImmutableArray<RemedyItem> FromEnvironment(IReadOnlyDictionary<string, string> outputs)
    {
        var details = Get(outputs, "details");
        var items = ImmutableArray.CreateBuilder<RemedyItem>();

        if (details.Contains("读取失败", StringComparison.Ordinal))
        {
            items.Add(RemedyItem.Notice(
                "env.unreadable",
                RemedySeverity.Warning,
                "部分环境变量读取失败",
                FirstLineContaining(details, "读取失败"),
                "注册表中的用户级或系统级环境变量键不可读（权限不足或被安全软件锁定）。",
                "读不到就无法判断这些变量的现状，任何基于它们的结论都不可靠。请以管理员身份重试，或检查注册表权限。",
                "envstation.detect.env",
                RiskLevel.Safe));
        }

        var undefined = UndefinedNames(details);
        if (undefined.Length > 0)
        {
            items.Add(RemedyItem.Notice(
                "env.variable-undefined",
                RemedySeverity.Warning,
                $"变量 {string.Join("、", undefined)} 未定义",
                "点名读取的环境变量在本机不存在。",
                "该变量从未设置，或被某个安装程序清掉了。",
                "依赖它的程序会读到空值，而空值往往被当成「没配置」以外的意思，导致行为难以判断。",
                "envstation.detect.env",
                RiskLevel.Safe));
        }

        return items.ToImmutable();
    }

    /// <summary>从明细里挑出"未定义"的变量名。</summary>
    private static ImmutableArray<string> UndefinedNames(string details)
    {
        var names = ImmutableArray.CreateBuilder<string>();
        foreach (var line in details.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string marker = " 未定义";
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            // 明细行的形状是 "[user] JAVA_HOME 未定义"：取掉作用域前缀之后就是变量名。
            var head = line[..index];
            var close = head.IndexOf(']', StringComparison.Ordinal);
            var name = (close >= 0 ? head[(close + 1)..] : head).Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names.ToImmutable();
    }

    private static string FirstLineContaining(string details, string marker)
    {
        foreach (var line in details.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                return line;
            }
        }

        return marker;
    }

    private static ImmutableArray<RemedyItem> FromRuntime(IReadOnlyDictionary<string, string> outputs)    {
        if (IsTrue(outputs, "found"))
        {
            return [];
        }

        var kind = Get(outputs, "kind");
        var display = kind.Length > 0 ? kind : "目标运行时";

        return
        [
            new RemedyItem(
                $"runtime.{kind}.missing",
                RemedySeverity.Warning,
                $"未检测到 {display}",
                $"本机没有找到 {display} 的可执行文件或安装目录。",
                "尚未安装，或安装在非常规位置且未登记主目录变量。",
                "依赖它的构建与调试流程无法进行。",
                RemediationPlan.Single(
                    RemedyStep.Of("envstation.runtime.install", $"安装 {display}",
                        RemedyArgument.Of("kind", kind)),
                    $"下载并安装 {display}"),
                RiskLevel.High,
                "envstation.detect.runtime"),
        ];
    }

    private static string Get(IReadOnlyDictionary<string, string> outputs, string key) =>
        outputs.TryGetValue(key, out var value) ? value : string.Empty;

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static bool IsTrue(IReadOnlyDictionary<string, string> outputs, string key) =>
        string.Equals(Get(outputs, key), "true", StringComparison.OrdinalIgnoreCase);

    private static int Count(IReadOnlyDictionary<string, string> outputs, string key) =>
        int.TryParse(Get(outputs, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static bool PathTextContains(string details, string directory) =>
        details.Contains(directory, StringComparison.OrdinalIgnoreCase);
}
