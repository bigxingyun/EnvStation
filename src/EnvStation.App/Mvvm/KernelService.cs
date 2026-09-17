using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Core.Actions;
using EnvStation.Core.Workflow;
using AbsPkg = EnvStation.Abstractions.Packages;

namespace EnvStation.App.Mvvm;

/// <summary>
/// <see cref="IKernelService"/> 的真实实现：把调用转给 <see cref="KernelBridge"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是全工程唯一允许出现 <c>KernelBridge</c> 的地方</b>（纪律 X-1 由 <c>verify-m0.ps1</c> 机械检查）。
/// 目的不是形式上的整洁，而是让"界面能对机器做什么"这件事只有一处需要审——
/// 上一版把可写入口写好却没人调用，正是因为调用点散落在页面里、没有任何地方能一眼看全。
/// </para>
/// <para>
/// 内核建不起来（动作注册表校验失败）时本类仍然会被构造出来，但
/// <see cref="IsAvailable"/> 为 false。这是刻意的：界面必须能<b>显示</b>失败，
/// 而不是让每个页面各自处理"内核是 null"——上一版正是在这里留下了
/// 「永久读取中」与两个没反应的按钮（重构方案 4.2 节）。
/// </para>
/// </remarks>
internal sealed class KernelService : IKernelService
{
    private readonly KernelBridge? _bridge;

    private KernelService(KernelBridge? bridge, string? reason, IReadOnlyList<ActionDescriptor> descriptors)
    {
        _bridge = bridge;
        UnavailableReason = reason;
        Descriptors = descriptors;
    }

    /// <inheritdoc />
    public bool IsAvailable => _bridge is not null;

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    /// <inheritdoc />
    public int ActionCount => _bridge?.ActionCount ?? 0;

    /// <inheritdoc />
    public IReadOnlyList<ActionDescriptor> Descriptors { get; }

    /// <summary>
    /// 建立服务。
    /// </summary>
    /// <remarks>
    /// <b>内核建不起来时也返回一个实例</b>（<see cref="IsAvailable"/> 为 false、
    /// <see cref="UnavailableReason"/> 带上原因），而不是返回 null 或抛异常。
    /// 理由：视图模型要有一个非空的服务才构造得出来，而"内核挂了"本身就是一种要显示给用户的状态。
    /// 若这里返回 null，每个页面的构造点都得判空并各自发明一套"内核不可用"的呈现——
    /// 上一版正是在这种分散判断里留下了「永久显示读取中」与两个没反应的按钮（重构方案 4.2 节）。
    /// </remarks>
    /// <param name="findings">注册表建立过程中的发现项（用于把失败原因讲清楚）。</param>
    public static KernelService Create(Abstractions.Diagnostics.FindingBag findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var created = KernelBridge.Create(findings);
        if (created.IsFailure)
        {
            // 把发现项里最要紧的那几条拼进原因，界面上直接显示——
            // 只说"内核初始化失败"等于什么都没说（文案规范 MS-4：能给出具体对象就给具体对象）。
            var detail = findings.Items
                .Where(static f => f.IsBlocker)
                .Take(3)
                .Select(static f => f.Title)
                .ToArray();

            var reason = detail.Length > 0
                ? $"动作注册表校验未通过：{string.Join("；", detail)}"
                : created.Error.Message;

            return new KernelService(null, reason, []);
        }

        var bridge = created.Value;
        return new KernelService(bridge, null, [.. bridge.Descriptors]);
    }

    /// <inheritdoc />
    public Task<ActionResult> RunReadOnlyAsync(
        string actionId,
        Dictionary<string, AbsPkg.ScriptValue>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (_bridge is null)
        {
            return Task.FromResult(Unavailable());
        }

        return _bridge.RunReadOnlyAsync(actionId, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ActionResult> RunAsync(
        string actionId,
        Dictionary<string, AbsPkg.ScriptValue>? arguments,
        bool isDryRun,
        IReadOnlyCollection<string> granted,
        IReadOnlyCollection<string> authorizedRoots,
        CancellationToken cancellationToken = default)
    {
        if (_bridge is null)
        {
            return Task.FromResult(Unavailable());
        }

        return _bridge.RunAsync(actionId, arguments, isDryRun, granted, authorizedRoots, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Abstractions.Serialization.SnapshotIndexEntry>> ListSnapshotsAsync(
        CancellationToken cancellationToken = default) =>
        KernelBridge.ListSnapshotsAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<WorkflowOutcome> RunWorkflowAsync(
        AbsPkg.WorkflowDocument document,
        bool isDryRun,
        IReadOnlyCollection<string> granted,
        IReadOnlyCollection<string> authorizedRoots,
        IProgress<(int Percent, string? Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_bridge is null)
        {
            throw new InvalidOperationException(UnavailableReason ?? "内核不可用。");
        }

        var sink = progress is null ? null : new ProgressSink(progress);
        var (outcome, _) = await _bridge
            .RunWorkflowAsync(document, isDryRun, granted, authorizedRoots, sink, cancellationToken)
            .ConfigureAwait(true);

        return outcome;
    }

    private ActionResult Unavailable() =>
        ActionResult.Fail(
            EnvStationErrorCodes.ActionNotFound,
            UnavailableReason ?? "内核不可用，无法执行任何动作。");

    /// <summary>把 <see cref="IProgress{T}"/> 适配成内核认识的进度接收器。</summary>
    /// <remarks>
    /// 内核对进度的契约是接口 <c>IProgressSink</c>，而界面侧用的是 <c>IProgress&lt;T&gt;</c>。
    /// 这一层适配是必要的——上一版把 <c>DispatcherProgressSink</c> 写好了却<b>从未实例化</b>，
    /// 于是 <c>ui.progress</c> 动作推的进度没有任何消费者，界面上永远看不到进度。
    /// </remarks>
    private sealed class ProgressSink(IProgress<(int Percent, string? Message)> target) : IProgressSink
    {
        public void Report(int percent, string? message) => target.Report((percent, message));
    }
}
