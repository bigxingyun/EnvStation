using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Actions;
using EnvStation.Core.Environment;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AbsPkg = EnvStation.Abstractions.Packages;

namespace EnvStation.App;

/// <summary>
/// 界面与内核之间的桥。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么界面不直接调动作</b>：需求 A2 要求"未授权即拒绝"，而授权是用户在主界面上
/// 逐项点出来的。因此所有动作调用都必须经过这里——由它负责构造带授权的执行上下文，
/// 并且<b>在预演模式下注入只读环境实现</b>，让"预览"这件事在结构上不可能改动机器。
/// </para>
/// <para>
/// <b>界面线程与后台线程</b>：动作大多会做文件 IO 或网络 IO。本项目统一用
/// <c>Task.Run</c> 把它们放到后台，界面只负责 await 与更新——绝不在 UI 线程上同步等待，
/// 那是"界面卡住"的唯一成因（需求 9.3 节）。
/// </para>
/// </remarks>
internal sealed class KernelBridge
{
    private readonly ActionRegistry _registry;

    private KernelBridge(ActionRegistry registry) => _registry = registry;

    /// <summary>动作总数（用于概览页展示）。</summary>
    internal int ActionCount => _registry.Count;

    /// <summary>建立内核（动作注册表在建表期就会做完整校验）。</summary>
    internal static Result<KernelBridge> Create(Abstractions.Diagnostics.FindingBag findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var registry = ActionRegistry.CreateDefault(findings);
        return registry.IsFailure
            ? Result<KernelBridge>.Fail(registry.Error)
            : Result<KernelBridge>.Ok(new KernelBridge(registry.Value));
    }

    /// <summary>列出全部动作（用于"动作库"页）。</summary>
    internal IEnumerable<ActionDescriptor> Descriptors => _registry.Descriptors;

    /// <summary>
    /// 执行一个只读动作（探测类）。
    /// </summary>
    /// <remarks>
    /// 只读动作只申请 <c>CAP.INSPECT</c>，因此在架构上不可能改动机器，
    /// 界面可以直接调用而不需要用户授权——这是"看一眼我的环境"零摩擦的前提。
    /// </remarks>
    internal Task<ActionResult> RunReadOnlyAsync(
        string actionId,
        Dictionary<string, ScriptValue>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => RunCore(actionId, arguments, isDryRun: true, granted: [CapabilityIds.Inspect], authorizedRoots: [], cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 执行一个可能写入的动作。
    /// </summary>
    /// <param name="actionId">动作 ID。</param>
    /// <param name="arguments">参数。</param>
    /// <param name="isDryRun">true = 预演（注入只读环境实现，不产生任何修改）。</param>
    /// <param name="granted">用户已授权的能力集合。</param>
    /// <param name="authorizedRoots">用户授权的可写根目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal Task<ActionResult> RunAsync(
        string actionId,
        Dictionary<string, ScriptValue>? arguments,
        bool isDryRun,
        IEnumerable<string> granted,
        IEnumerable<string> authorizedRoots,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => RunCore(actionId, arguments, isDryRun, granted, authorizedRoots, cancellationToken),
            cancellationToken);
    }

    private ActionResult RunCore(
        string actionId,
        Dictionary<string, ScriptValue>? arguments,
        bool isDryRun,
        IEnumerable<string> granted,
        IEnumerable<string> authorizedRoots,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(actionId, out var action))
        {
            return ActionResult.Fail(EnvStationErrorCodes.ActionNotFound, $"动作 {actionId} 不存在。");
        }

        var rootList = authorizedRoots as IReadOnlyList<string> ?? [.. authorizedRoots];

        var findings = new Abstractions.Diagnostics.FindingBag();
        var bound = ActionArgumentsBinder.Bind(
            action.Descriptor,
            arguments ?? new Dictionary<string, ScriptValue>(StringComparer.Ordinal),
            findings);

        if (bound.IsFailure)
        {
            return ActionResult.Fail(EnvStationErrorCodes.ActionArgumentInvalid, findings.ToReport().ToText());
        }

        var context = new ActionExecutionContext(
            packageId: "envstation.app",
            runId: $"ui-{DateTime.Now:yyyyMMdd-HHmmss}",
            grantedCapabilities: new CapabilitySet(granted),
            authorizedRoots: rootList,
            variables: new VariableTable(),
            quota: new QuotaMeter(ResourceQuota.Default),
            audit: CreateAuditSink(),
            unattended: false,
            environment: CreateEnvironment(isDryRun, rootList),
            // 界面里不做网络下载：下载走包的工作流，由用户显式授权的动作完成。
            network: null);

        try
        {
            return ActionExecutor
                .ExecuteAsync(action, context, bound.Value, timeoutSeconds: null, cancellationToken)
                .AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            context.Dispose();
        }
    }

    /// <summary>校验一个包（V1~V3），在后台线程执行。</summary>
    /// <remarks>
    /// 与 CLI 的 <c>envstation check</c> 行为一致：<b>不传可信公钥</b>，因此签名只做"自洽性"验证
    /// （签名与包内公钥对得上），不做"信任"判断。信任锚点由用户在使用现场逐包确认，
    /// 见 需求分析 24.3 节的信任模型——环境站不预置任何官方根密钥。
    /// </remarks>
    internal static Task<Core.Packages.PackageValidationResult> ValidatePackageAsync(
        string packagePath, CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Core.Packages.PackageValidator.ValidateAsync(packagePath, null, cancellationToken)
                .GetAwaiter().GetResult(),
            cancellationToken);

    /// <summary>运行工作流（预演或执行）。</summary>
    internal Task<(Core.Workflow.WorkflowOutcome Outcome, ActionExecutionContext Context)> RunWorkflowAsync(
        WorkflowDocument document,
        bool isDryRun,
        IEnumerable<string> granted,
        IEnumerable<string> authorizedRoots,
        IProgressSink? progress,
        CancellationToken cancellationToken = default)
    {
        var rootList = authorizedRoots as IReadOnlyList<string> ?? [.. authorizedRoots];

        return Task.Run(
            () =>
            {
                var context = new ActionExecutionContext(
                    packageId: "envstation.app",
                    runId: $"ui-{DateTime.Now:yyyyMMdd-HHmmss}",
                    grantedCapabilities: new CapabilitySet(granted),
                    authorizedRoots: rootList,
                    variables: new VariableTable(),
                    quota: new QuotaMeter(ResourceQuota.Default),
                    audit: CreateAuditSink(),
                    unattended: false,
                    environment: CreateEnvironment(isDryRun, rootList),
                    network: isDryRun ? null : new HttpNetworkTransport(),
                    progress: progress);

                var interpreter = new Core.Workflow.WorkflowInterpreter(_registry);
                var outcome = interpreter
                    .RunAsync(document, context, new Core.Workflow.WorkflowOptions(DryRun: isDryRun, Unattended: false), cancellationToken)
                    .AsTask().GetAwaiter().GetResult();

                return (outcome, context);
            },
            cancellationToken);
    }

    /// <summary>读取包清单与工作流（用于能力授权与预演）。</summary>
    internal static Result<(PackageManifest Manifest, WorkflowDocument Workflow, Core.Packages.PackageContents Contents)> ReadPackage(string packagePath)
    {
        var findings = new Abstractions.Diagnostics.FindingBag();

        var contents = Core.Packages.PackageArchive.Read(packagePath, findings);
        if (contents.IsFailure)
        {
            return Result<(PackageManifest, WorkflowDocument, Core.Packages.PackageContents)>.Fail(contents.Error);
        }

        var manifestText = contents.Value.GetText(Core.Packages.PackageLayout.ManifestEntry);
        var workflowText = contents.Value.GetText(Core.Packages.PackageLayout.WorkflowEntry);

        if (manifestText is null || workflowText is null)
        {
            return Result<(PackageManifest, WorkflowDocument, Core.Packages.PackageContents)>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                "包内缺少 envstation.toml 或 workflow.toml。");
        }

        var manifest = Core.Packages.PackageManifestReader.Read(manifestText, findings);
        var workflow = Core.Packages.WorkflowReader.Read(workflowText, findings);

        if (manifest.IsFailure || workflow.IsFailure)
        {
            return Result<(PackageManifest, WorkflowDocument, Core.Packages.PackageContents)>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                "包清单或工作流未通过校验。");
        }

        return Result<(PackageManifest, WorkflowDocument, Core.Packages.PackageContents)>.Ok(
            (manifest.Value, workflow.Value, contents.Value));
    }

    /// <summary>
    /// 建立审计汇（需求 8.2）。
    /// </summary>
    /// <remarks>
    /// 与 CLI 用同一套落地实现与同一个目录（<c>%LOCALAPPDATA%\EnvStation\audit</c>），
    /// 这样"改用命令行跑"和"在界面里跑"留下的是同一份连续记录，而不是两套互相看不见的日志。
    /// 写不进去时降级为内存实现：审计失败不该阻止用户完成操作，但状态栏会说明。
    /// </remarks>
    private static IAuditSink CreateAuditSink()
    {
        try
        {
            var directory = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "EnvStation",
                "audit");

            return new JsonlAuditSink(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new MemoryAuditSink();
        }
    }

    /// <summary>
    /// 沙箱试运行（V4）：在隔离目录里真实执行一遍，核对"声明"与"实际"。
    /// </summary>
    /// <remarks>
    /// 界面里点这个按钮不会有任何真机副作用，理由与 CLI 完全一致：
    /// 只读环境实现、不注入网络、可写根只有本次创建的临时沙箱目录。
    /// 沙箱目录在这里用完即删，所以界面拿到的只有结论。
    /// </remarks>
    internal static Task<Core.Packages.SandboxTrialResult?> RunSandboxTrialAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                var read = ReadPackage(packagePath);
                if (read.IsFailure)
                {
                    return null;
                }

                var findings = new Abstractions.Diagnostics.FindingBag();
                var registry = ActionRegistry.CreateDefault(findings);
                if (registry.IsFailure)
                {
                    return null;
                }

                var sandbox = Path.Combine(Path.GetTempPath(), "envstation-v4-" + Guid.NewGuid().ToString("N")[..8]);

                try
                {
                    var (manifest, workflow, _) = read.Value;
                    return Core.Packages.SandboxTrial
                        .RunAsync(manifest, workflow, registry.Value, sandbox, cancellationToken)
                        .GetAwaiter().GetResult();
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(sandbox))
                        {
                            Directory.Delete(sandbox, recursive: true);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 沙箱清理失败不影响结论。
                    }
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// 按"这次调用是否可能写入"选择环境实现。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只读调用必须拿到只读实现，而不是 null。</b>环境写入是唯一一条真机副作用通道，
    /// 所以"没有授权根 ⇒ 不注入实现"看起来最安全；但只读动作（<c>path.validate</c> 这类）
    /// 本来就只申请 <c>CAP.INSPECT</c>、也不需要授权根，把它们的实现也一起掐掉，
    /// 结果是"环境检测"页永远报「未注入环境操作实现」——功能直接不可用（真机首次运行就是这样）。
    /// </para>
    /// <para>
    /// 正确的分界是"读还是写"，不是"有没有根目录"：只读一律注入
    /// <see cref="ReadOnlyEnvironmentOperations"/>（读真实状态、写结构性拒绝）；
    /// 只有真正要写时才要求授权根，此时若没有根就返回 null，动作照旧明确失败。
    /// </para>
    /// </remarks>
    private static IEnvironmentOperations? CreateEnvironment(bool isDryRun, IReadOnlyList<string> authorizedRoots)
    {
        if (isDryRun)
        {
            return ReadOnlyEnvironmentOperations.CreateDefault();
        }

        return authorizedRoots.Count == 0 ? null : RegistryEnvironmentOperations.CreateDefault();
    }

    /// <summary>读取环境变量快照历史（用于「快照与回滚」页）。</summary>
    internal static Task<IReadOnlyList<Abstractions.Serialization.SnapshotIndexEntry>> ListSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                var store = new Core.Transactions.SnapshotStore(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnvStation", "snapshots"),
                    new RegistryEnvStore(EnvScope.User),
                    new RegistryEnvStore(EnvScope.Machine));
                var list = store.List();
                return list.IsSuccess ? list.Value : (IReadOnlyList<Abstractions.Serialization.SnapshotIndexEntry>)[];
            },
            cancellationToken);
    }
}

/// <summary>把进度回调转成界面更新（在 UI 线程上执行）。</summary>
internal sealed class DispatcherProgressSink : IProgressSink
{
    private readonly Action<int, string?> _update;

    internal DispatcherProgressSink(Action<int, string?> update) => _update = update;

    public void Report(int percent, string? message) => _update(percent, message);
}
