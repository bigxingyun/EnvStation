using System.Collections.Immutable;
using EnvStation.Abstractions.Actions;

namespace EnvStation.Core.Actions;

/// <summary>通知级别（对应 UI 设计规范里的四种提示语义）。</summary>
public enum NotifyLevel
{
    /// <summary>普通信息。</summary>
    Info = 0,

    /// <summary>成功。</summary>
    Success = 1,

    /// <summary>警告：需要用户注意但不阻断。</summary>
    Warning = 2,

    /// <summary>错误。</summary>
    Error = 3,
}

/// <summary>用户输入的类型。</summary>
public enum PromptType
{
    /// <summary>单行文本。</summary>
    Text = 0,

    /// <summary>是 / 否。</summary>
    Confirm = 1,

    /// <summary>从固定选项中选择。</summary>
    Choice = 2,
}

/// <summary>一次向用户提问的内容。</summary>
/// <param name="Question">问题文本。</param>
/// <param name="Type">输入类型。</param>
/// <param name="DefaultValue">默认值（用户直接回车时使用）。</param>
/// <param name="Choices">可选值（<see cref="PromptType.Choice"/> 时必需）。</param>
/// <param name="IsSecret">是否为敏感输入（UI 必须掩码显示，且不入日志）。</param>
public sealed record PromptRequest(
    string Question,
    PromptType Type,
    string? DefaultValue,
    ImmutableArray<string> Choices,
    bool IsSecret);

/// <summary>用户的回答。</summary>
/// <param name="Answered">用户是否作出了回答（取消时为 false）。</param>
/// <param name="Value">回答内容。</param>
/// <param name="FromDefault">是否使用了默认值。</param>
public sealed record PromptAnswer(bool Answered, string Value, bool FromDefault)
{
    /// <summary>用户取消。</summary>
    public static PromptAnswer Cancelled { get; } = new(false, string.Empty, false);

    /// <summary>使用默认值。</summary>
    public static PromptAnswer Default(string value) => new(true, value, true);
}

/// <summary>
/// 交互通道。CLI 与图形界面各自实现；无人值守时不注入。
/// </summary>
/// <remarks>
/// <b>这条通道刻意只传送"数据"</b>：通知是文本、提问得到的是文本，
/// 返回的字符串<b>永远不会</b>被当作命令执行（需求 AI-3）。
/// 用户输入若需要参与后续动作，只能作为参数值传递。
/// </remarks>
public interface IInteractionSink
{
    /// <summary>展示一条提示。</summary>
    void Notify(NotifyLevel level, string message);

    /// <summary>向用户提问并等待回答。</summary>
    ValueTask<PromptAnswer> PromptAsync(PromptRequest request, CancellationToken cancellationToken);
}

/// <summary>什么都不做的交互实现（无人值守 / 预演）。</summary>
public sealed class NullInteractionSink : IInteractionSink
{
    /// <summary>单例。</summary>
    public static NullInteractionSink Instance { get; } = new();

    /// <inheritdoc />
    public void Notify(NotifyLevel level, string message)
    {
        // 刻意留空：通知类动作在无人值守下不应产生任何副作用，也不应失败。
    }

    /// <inheritdoc />
    public ValueTask<PromptAnswer> PromptAsync(PromptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 提问类动作在无人值守下**不能**静默使用默认值：
        // 那会让"我确认过了"变成一句空话。因此返回"未回答"，
        // 由动作层转成明确失败。
        return ValueTask.FromResult(PromptAnswer.Cancelled);
    }
}

/// <summary>
/// 运行账本：记录本次包运行中每一次动作调用的完整信息。
/// </summary>
/// <remarks>
/// 它与审计日志的分工是：审计日志面向"事后追责与合规"（逐事件、不可变、落盘）；
/// 账本面向"这次跑完给用户看什么"（按步骤聚合、可生成报告、可计算汇总）。
/// 两者都由执行管线写入，但消费者完全不同，因此不合并。
/// </remarks>
public sealed class RunLedger
{
    private readonly List<ActionInvocation> _invocations = [];

    /// <summary>运行 ID。</summary>
    public required string RunId { get; init; }

    /// <summary>包 ID。</summary>
    public required string PackageId { get; init; }

    /// <summary>开始时间。</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>结束时间（未结束时为 null）。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>工作流层面的结论（成功 / 失败原因）。</summary>
    public string? Outcome { get; set; }

    /// <summary>全部动作调用记录（按发生顺序）。</summary>
    public IReadOnlyList<ActionInvocation> Invocations => _invocations;

    /// <summary>已触碰的路径（去重、保序）。</summary>
    public ImmutableArray<string> TouchedPaths() =>
        [.. _invocations
            .SelectMany(static i => i.TouchedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)];

    /// <summary>可反向操作的凭据（快照 ID、备份 ID），去重后按出现顺序返回。</summary>
    public ImmutableArray<string> ReversibleTokens() =>
        [.. _invocations
            .Select(static i => i.ReversibleToken)
            .Where(static t => !string.IsNullOrEmpty(t))
            .Select(static t => t!)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>失败的动作调用。</summary>
    public IEnumerable<ActionInvocation> Failures() => _invocations.Where(static i => !i.Succeeded);

    /// <summary>总耗时（进程内累计的动作耗时之和）。</summary>
    public TimeSpan TotalActionTime() =>
        _invocations.Aggregate(TimeSpan.Zero, static (acc, i) => acc + i.Elapsed);

    /// <summary>把一次调用追加进账本。</summary>
    public void Record(ActionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        _invocations.Add(invocation);
    }
}

/// <summary>报告输出格式。</summary>
public enum ReportFormat
{
    /// <summary>Markdown（默认，便于贴到工单或文档）。</summary>
    Markdown = 0,

    /// <summary>JSON（机器可读）。</summary>
    Json = 1,

    /// <summary>纯文本（无格式，便于复制）。</summary>
    Text = 2,
}
