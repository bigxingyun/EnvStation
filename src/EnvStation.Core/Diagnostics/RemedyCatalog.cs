using System.Collections.Immutable;
using System.Globalization;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Transactions;

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

        return actionId switch
        {
            "envstation.detect.os" => FromOs(outputs),
            "envstation.path.validate" => FromPathValidate(outputs, scope),
            "envstation.detect.deps" => FromDependencies(outputs),
            "envstation.detect.conflict" => FromConflicts(outputs),
            "envstation.detect.runtime" => FromRuntime(outputs),
            _ => [],
        };
    }

    /// <summary>该检测项是否已登记映射（无论是否可修复）。未登记者由用例拦下。</summary>
    public static bool IsMapped(string actionId) => actionId switch
    {
        "envstation.detect.os" or
        "envstation.path.validate" or
        "envstation.detect.deps" or
        "envstation.detect.conflict" or
        "envstation.detect.runtime" => true,
        _ => false,
    };

    /// <summary>当前已登记映射的检测项（用例据此比对动作注册表，防止新增检测项时漏接）。</summary>
    public static ImmutableArray<string> MappedDetections { get; } =
        ["envstation.detect.os", "envstation.path.validate", "envstation.detect.deps",
         "envstation.detect.conflict", "envstation.detect.runtime"];

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

    private static ImmutableArray<RemedyItem> FromRuntime(IReadOnlyDictionary<string, string> outputs)
    {
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
