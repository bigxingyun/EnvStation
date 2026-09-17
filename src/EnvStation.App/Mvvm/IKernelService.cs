using EnvStation.Abstractions.Actions;
using EnvStation.Core.Workflow;
using AbsPkg = EnvStation.Abstractions.Packages;
namespace EnvStation.App.Mvvm;

/// <summary>
/// 界面访问内核的唯一入口。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个接口存在的理由（纪律 X-1）</b>：上一版界面里，页面构建函数直接抓 <c>KernelBridge</c>
/// 发异步调用并把结果写进控件，于是既测不了、也换不掉、更没法表达"加载/空/错"。
/// 把访问收成一个接口之后：页面只认接口，测试可以注入替身，而"谁能写机器"这件事
/// 也变成了一处可审的声明。
/// </para>
/// <para>
/// <b>为什么放在 App 而不是 Ui.Shared</b>：它引用的 <c>WorkflowDocument</c> 与 <c>ActionResult</c>
/// 都在内核程序集里，而它的消费者只有界面。放这里可以让 Core 保持"不认识界面"。
/// </para>
/// </remarks>
internal interface IKernelService
{
    /// <summary>内核是否可用。为 false 时任何页面都必须落到错误态，而不是停在加载中。</summary>
    bool IsAvailable { get; }

    /// <summary>内核不可用时的原因（供错误态展示）。</summary>
    string? UnavailableReason { get; }

    /// <summary>已登记动作总数。</summary>
    int ActionCount { get; }

    /// <summary>全部动作描述（能力清单页用）。</summary>
    IReadOnlyList<ActionDescriptor> Descriptors { get; }

    /// <summary>
    /// 执行只读动作。
    /// </summary>
    /// <remarks>只读动作只申请 <c>CAP.INSPECT</c>，在架构上不可能改动机器，因此不需要用户授权。</remarks>
    Task<ActionResult> RunReadOnlyAsync(
        string actionId,
        Dictionary<string, AbsPkg.ScriptValue>? arguments = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行可能写入的动作。
    /// </summary>
    /// <param name="isDryRun">true = 预演（注入只读环境实现，不产生任何修改）。</param>
    /// <param name="granted">用户已授权的能力集合。<b>空集合等于什么都不许做</b>，不是"全部允许"。</param>
    /// <param name="authorizedRoots">用户授权的可写根目录；为空时任何写入路径都会被拒绝。</param>
    Task<ActionResult> RunAsync(
        string actionId,
        Dictionary<string, AbsPkg.ScriptValue>? arguments,
        bool isDryRun,
        IReadOnlyCollection<string> granted,
        IReadOnlyCollection<string> authorizedRoots,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取快照索引（按存储顺序；时间倒序由调用方决定）。
    /// </summary>
    /// <remarks>
    /// 只读操作，且只读索引不读内容——索引里已有时间、触发原因、大小与内容哈希，够画时间线。
    /// </remarks>
    Task<IReadOnlyList<Abstractions.Serialization.SnapshotIndexEntry>> ListSnapshotsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>运行一份工作流（预演或执行）。</summary>
    Task<WorkflowOutcome> RunWorkflowAsync(
        AbsPkg.WorkflowDocument document,
        bool isDryRun,
        IReadOnlyCollection<string> granted,
        IReadOnlyCollection<string> authorizedRoots,
        IProgress<(int Percent, string? Message)>? progress = null,
        CancellationToken cancellationToken = default);
}
