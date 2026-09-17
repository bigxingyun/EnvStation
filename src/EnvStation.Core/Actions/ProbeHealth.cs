using EnvStation.Abstractions.Actions;

namespace EnvStation.Core.Actions;

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
/// <c>supported</c> 是唯一的反向项；其余都是计数，非 0 即问题。
/// </para>
/// <para>
/// <b>找不到判据键时按"有问题"处理</b>：宁可多报一次让人来看，也不能因为键名写错就静默报平安。
/// </para>
/// </remarks>
public static class ProbeHealth
{
    /// <summary>判定"有问题"所依据的输出键；<c>supported</c> 为反向项（false 才算问题）。</summary>
    private static readonly Dictionary<string, string> ProblemKeys = new(StringComparer.Ordinal)
    {
        ["envstation.detect.os"] = "supported",
        ["envstation.path.validate"] = "problem_count",
        ["envstation.detect.deps"] = "missing_count",
        ["envstation.detect.conflict"] = "conflict_count",
    };

    /// <summary>这个动作是否属于"检测类"（有明确的问题判据）。</summary>
    public static bool IsProbe(string actionId) => ProblemKeys.ContainsKey(actionId);

    /// <summary>判断一次检测结果是否表示"有问题"。未知动作退回"看成功与否"。</summary>
    public static bool IsProblem(string actionId, ActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Success)
        {
            return true;
        }

        if (!ProblemKeys.TryGetValue(actionId, out var key))
        {
            return false;
        }

        if (!result.Outputs.TryGetValue(key, out var value))
        {
            // 动作成功但没给出判据键：说明契约对不上，按有问题处理并让人看见。
            return true;
        }

        return string.Equals(key, "supported", StringComparison.Ordinal)
            ? string.Equals(value, "false", StringComparison.Ordinal)
            : !string.Equals(value, "0", StringComparison.Ordinal);
    }

    /// <summary>检测结果的一行结论：<c>正常</c> / <c>异常</c> / <c>失败</c>。</summary>
    public static string Describe(string actionId, ActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return !result.Success ? "失败" : IsProblem(actionId, result) ? "异常" : "正常";
    }
}
