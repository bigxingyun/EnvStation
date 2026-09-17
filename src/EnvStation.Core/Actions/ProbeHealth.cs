using System.Collections.Immutable;
using System.Globalization;
using EnvStation.Abstractions.Actions;

namespace EnvStation.Core.Actions;

/// <summary>一次探测的结论状态。</summary>
public enum ProbeState
{
    /// <summary>不是检测类动作（写操作等）：执行成功本身就是结论。</summary>
    NotApplicable = 0,

    /// <summary>检测通过。</summary>
    Healthy = 1,

    /// <summary>动作执行成功，但结论是"有问题"——缺依赖、PATH 有坏条目、命令冲突都属于这一类。</summary>
    Problem = 2,

    /// <summary>动作本身失败（没能得出结论）。</summary>
    Failed = 3,

    /// <summary>是检测类动作，但没有登记判据：<b>无法判断这一项是否正常</b>。</summary>
    Undeclared = 4,
}

/// <summary>一次探测的评估结论。</summary>
/// <param name="State">状态。</param>
/// <param name="Basis">判据说明（这一项凭什么算正常/异常）。</param>
/// <param name="Reason">异常或未登记时的具体原因；正常时为空。</param>
public sealed record ProbeAssessment(ProbeState State, string Basis, string? Reason = null)
{
    /// <summary>是否可以直接给"正常"的绿灯。只有 <see cref="ProbeState.Healthy"/> 可以。</summary>
    public bool IsGreen => State == ProbeState.Healthy;
}

/// <summary>问题判据：读输出键，返回 null 表示这一项没问题，否则返回一句"哪里不对"。</summary>
/// <param name="Basis">判据说明（写给人看，说明这一项凭什么算异常）。</param>
/// <param name="Evaluate">判定函数；返回 null = 正常。</param>
public sealed record ProbeJudge(string Basis, Func<IReadOnlyDictionary<string, string>, string?> Evaluate);

/// <summary>
/// 把探测类动作的输出翻译成"这一项到底有没有问题"。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它，而不是各处自己判断</b>：界面与 CLI 都要在检测结果旁边给一个结论徽标/符号。
/// 第一版两边各自猜输出键（都用 <c>problem_count</c>），结果 <c>detect.deps</c>（输出
/// <c>missing_count</c>）与 <c>detect.conflict</c>（输出 <c>conflict_count</c>）在真有缺失、真有冲突时
/// 仍然显示"正常/未发现异常"——<b>徽标说没问题、正文说缺 1 项</b>，是这类检测页最不能犯的错。
/// 两份代码各猜一次，必然有一天猜到不一样；所以判据集中在这里，两边都调它。
/// </para>
/// <para>
/// <b>判据来自各动作自己的输出契约</b>（见 <c>需求分析.md</c> 21.2 的动作表），不是猜的。
/// 判据键缺失时按"有问题"处理：宁可多报一次让人来看，也不能因为键名写错就静默报平安。
/// </para>
/// <para>
/// <b>判据表必须覆盖全部探测动作（M2-8 的 X-2）</b>：这张表原先只有 4 项，
/// 而 CLI 的 <c>doctor</c> 已经跑了 <c>detect.arch</c> / <c>detect.disk</c> / <c>detect.env</c>——
/// 它们<b>永远显示绿色对勾</b>，哪怕磁盘写入测试没过、哪怕进程正跑在模拟模式下。
/// 现在没有判据的探测动作会落到 <see cref="ProbeState.Undeclared"/>，
/// 界面上显示"未登记判据"而不是一个绿灯；用例遍历动作注册表盯着这张表不再漏项。
/// </para>
/// <para>
/// <b>三个判据键的语义要看清</b>：<c>supported</c> / <c>enough</c> / <c>writable</c> / <c>found</c> /
/// <c>emulated</c> 是反向项（<c>false</c> 才是问题，<c>emulated</c> 反过来以 <c>true</c> 为问题），
/// 其余计数键非 0 即问题。混用会让结论整体反过来，所以每条判据都在这里显式写出来。
/// </para>
/// </remarks>
public static class ProbeHealth
{
    /// <summary>
    /// 全部检测类动作：<c>需求分析.md</c> 21.2 的九个 <c>detect.*</c> 探测动作，
    /// 外加 <c>envstation.path.validate</c>（只读校验，与探测同类：动作成功不等于没问题）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这张清单同时是<b>契约</b>：新增一个检测类动作而不在这里登记判据，用例会失败。
    /// 没有这道闸，"新加的检测在界面上永远显示正常"就是必然会发生的事。
    /// </para>
    /// <para>
    /// <c>path.validate</c> 是补进来的：它原本有判据，改造这张表时漏掉了，
    /// 于是 CLI 的体检从"4 项异常"变成"2 项异常"——<b>两处 PATH 异常被报成正常</b>。
    /// 这正是本类存在的理由，也说明判据表的覆盖必须由用例遍历注册表来盯，不能靠人工回忆。
    /// </para>
    /// </remarks>
    public static ImmutableArray<string> Probes { get; } =
    [
        "envstation.detect.os",
        "envstation.detect.arch",
        "envstation.detect.command",
        "envstation.detect.runtime",
        "envstation.detect.disk",
        "envstation.detect.deps",
        "envstation.detect.network",
        "envstation.detect.conflict",
        "envstation.detect.env",
        "envstation.path.validate",
    ];

    private static readonly ImmutableDictionary<string, ProbeJudge> Judges =
        new Dictionary<string, ProbeJudge>(StringComparer.Ordinal)
        {
            ["envstation.detect.os"] = new(
                "supported=false 表示系统低于最低要求",
                outputs => Flag(outputs, "supported", problemWhenTrue: false, "当前系统低于环境站的最低要求（Windows 10 1809 / 构建 17763）")),

            ["envstation.detect.arch"] = new(
                "emulated=true 表示进程架构与系统架构不一致（正在模拟运行）",
                outputs => Flag(outputs, "emulated", problemWhenTrue: true,
                    $"当前进程以 {Value(outputs, "process_arch")} 运行，而系统架构是 {Value(outputs, "os_arch")}：正处于模拟运行状态。安装原生组件前需改用原生版本的环境站")),

            ["envstation.detect.command"] = new(
                "found=false 表示在 PATH 中找不到该命令",
                outputs => Flag(outputs, "found", problemWhenTrue: false,
                    $"命令 {Value(outputs, "command")} 在 PATH 中未找到")),

            ["envstation.detect.runtime"] = new(
                "found=false 表示本机没有该运行时",
                outputs => Flag(outputs, "found", problemWhenTrue: false,
                    $"未检测到 {Value(outputs, "kind")}")),

            ["envstation.detect.disk"] = new(
                "enough=false（空间不足）或 writable=false（真实写入测试未通过）",
                outputs =>
                    Flag(outputs, "enough", problemWhenTrue: false, $"磁盘 {Value(outputs, "drive")} 可用空间不足")
                    ?? Flag(outputs, "writable", problemWhenTrue: false,
                        Value(outputs, "write_reason") is { Length: > 0 } reason
                            ? reason
                            : $"目标位置 {Value(outputs, "probed_path")} 的真实写入测试未通过")),

            ["envstation.detect.deps"] = new(
                "missing_count 非 0 表示缺少前置依赖",
                outputs => NonZero(outputs, "missing_count",
                    count => $"缺少 {count} 项前置依赖：{Value(outputs, "missing")}")),

            ["envstation.detect.network"] = new(
                "reachable_count < total 表示有主机不可达",
                Unreachable),

            ["envstation.detect.conflict"] = new(
                "conflict_count 非 0 表示存在同名命令冲突",
                outputs => NonZero(outputs, "conflict_count", count => $"发现 {count} 处同名命令冲突")),

            ["envstation.detect.env"] = new(
                "found_count=0 表示一个变量都没读到（请求了变量却读到 0 个属于异常）",
                outputs => Zero(outputs, "found_count", "没有读到任何环境变量")),

            ["envstation.path.validate"] = new(
                "problem_count 非 0 表示 PATH 中存在空条目、失效目录、重复项或未解析变量",
                outputs => NonZero(outputs, "problem_count",
                    count => $"PATH 中有 {count} 项异常（不存在 {Value(outputs, "missing_count")}、" +
                             $"重复 {Value(outputs, "duplicate_count")}、" +
                             $"空条目 {Value(outputs, "empty_count")}、" +
                             $"变量未解析 {Value(outputs, "unresolved_count")}）")),
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>这个动作是否属于"检测类"。</summary>
    public static bool IsProbe(string actionId) => Judges.ContainsKey(actionId);

    /// <summary>取一项的判据说明；未登记时为空。</summary>
    public static string? BasisOf(string actionId) =>
        Judges.TryGetValue(actionId, out var judge) ? judge.Basis : null;

    /// <summary>评估一次动作结果。</summary>
    public static ProbeAssessment Assess(string actionId, ActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!Judges.TryGetValue(actionId, out var judge))
        {
            // 非检测类动作（写操作等）：执行成功就是结论，没有"结论异常"这一说。
            // 但**长得像检测动作的**（envstation.detect.*）落到这里就是漏登记，必须说出来。
            if (!actionId.StartsWith("envstation.detect.", StringComparison.Ordinal))
            {
                return new ProbeAssessment(
                    result.Success ? ProbeState.NotApplicable : ProbeState.Failed,
                    "非检测类动作：执行成功即结论",
                    result.Success ? null : result.Message);
            }

            return new ProbeAssessment(
                ProbeState.Undeclared,
                "未登记判据",
                $"{actionId} 是检测类动作但没有登记问题判据，无法判断这一项是否正常。");
        }

        if (!result.Success)
        {
            return new ProbeAssessment(ProbeState.Failed, judge.Basis, result.Message);
        }

        var reason = judge.Evaluate(result.Outputs);
        return reason is null
            ? new ProbeAssessment(ProbeState.Healthy, judge.Basis)
            : new ProbeAssessment(ProbeState.Problem, judge.Basis, reason);
    }

    /// <summary>判断一次检测结果是否表示"有问题"（失败也算）。</summary>
    public static bool IsProblem(string actionId, ActionResult result)
    {
        var state = Assess(actionId, result).State;
        return state is ProbeState.Problem or ProbeState.Failed;
    }

    /// <summary>检测结果的一行结论：<c>正常</c> / <c>异常</c> / <c>失败</c> / <c>未登记判据</c>。</summary>
    public static string Describe(string actionId, ActionResult result) =>
        Assess(actionId, result).State switch
        {
            ProbeState.Healthy => "正常",
            ProbeState.Problem => "异常",
            ProbeState.Failed => "失败",
            ProbeState.Undeclared => "未登记判据",
            _ => "—",
        };

    /// <summary>结果标记：<c>✓</c> 正常 / <c>!</c> 异常 / <c>✗</c> 失败 / <c>?</c> 未登记判据。</summary>
    /// <remarks>
    /// 问号是刻意加的：未登记判据时给对勾，等于把一个没检查过的项说成检查过了。
    /// </remarks>
    public static string Mark(string actionId, ActionResult result) =>
        Assess(actionId, result).State switch
        {
            ProbeState.Problem => "!",
            ProbeState.Failed => "✗",
            ProbeState.Undeclared => "?",
            _ => "✓",
        };

    /// <summary>读一个反向/正向布尔判据键。</summary>
    private static string? Flag(
        IReadOnlyDictionary<string, string> outputs,
        string key,
        bool problemWhenTrue,
        string reason)
    {
        if (!outputs.TryGetValue(key, out var value))
        {
            return $"输出缺少判据键 {key}（动作契约对不上）";
        }

        var isTrue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        return isTrue == problemWhenTrue ? reason : null;
    }

    /// <summary>
    /// 读一个"等于 0 即问题"的计数判据键。
    /// </summary>
    /// <remarks>
    /// 方向必须写清楚：多数计数键是"非 0 即问题"（缺了几个、冲突几处），
    /// 而"读到了几个"恰好相反——读到 0 个才是问题。两者用同一个辅助函数会让结论整体反过来，
    /// 所以这里分成 <see cref="NonZero"/> 与 <see cref="Zero"/> 两个名字。
    /// 键缺失时同样判为问题：读不到结论就报平安正是本类要根治的缺陷。
    /// </remarks>
    private static string? Zero(
        IReadOnlyDictionary<string, string> outputs,
        string key,
        string reason)
    {
        if (!outputs.TryGetValue(key, out var value) ||
            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return $"输出缺少判据键 {key}（动作契约对不上）";
        }

        return count == 0 ? reason : null;
    }

    /// <summary>读一个计数判据键：非 0 即问题；键缺失或不是数字时同样判为问题。</summary>
    /// <remarks>
    /// 键缺失<b>不能</b>当成 0：那等于"读不到结论就报平安"，而这正是本类要根治的缺陷。
    /// </remarks>
    private static string? NonZero(
        IReadOnlyDictionary<string, string> outputs,
        string key,
        Func<int, string> describe)
    {
        if (!outputs.TryGetValue(key, out var value) ||
            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return $"输出缺少判据键 {key}（动作契约对不上）";
        }

        return count == 0 ? null : describe(count);
    }

    private static string? Unreachable(IReadOnlyDictionary<string, string> outputs)
    {
        if (!outputs.TryGetValue("reachable_count", out var rawReachable) ||
            !int.TryParse(rawReachable, NumberStyles.Integer, CultureInfo.InvariantCulture, out var reachable))
        {
            return "输出缺少判据键 reachable_count（动作契约对不上）";
        }

        if (!outputs.TryGetValue("total", out var rawTotal) ||
            !int.TryParse(rawTotal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
        {
            return "输出缺少判据键 total（动作契约对不上）";
        }

        if (total == 0)
        {
            return "没有指定要检测的主机";
        }

        return reachable < total ? $"{reachable}/{total} 个主机不可达：{Value(outputs, "results")}" : null;
    }

    private static string Value(IReadOnlyDictionary<string, string> outputs, string key) =>
        outputs.TryGetValue(key, out var value) && value.Length > 0 ? value : "未知";
}
