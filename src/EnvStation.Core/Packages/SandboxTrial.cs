using System.Collections.Immutable;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Actions;
using EnvStation.Core.Environment;
using EnvStation.Core.Workflow;

namespace EnvStation.Core.Packages;

/// <summary>
/// V4 沙箱试运行的结论。
/// </summary>
/// <param name="Report">发现报告（含阻断项与警告）。</param>
/// <param name="Outcome">预演结果。</param>
/// <param name="ExercisedCapabilities">实际用到的能力（来自运行账本，不是静态扫描）。</param>
/// <param name="UndeclaredCapabilities">用到但没声明的能力（阻断项）。</param>
/// <param name="UnusedCapabilities">声明了但本次没用到的能力（警告：用户会多授权）。</param>
/// <param name="TouchedPaths">预演期间被触碰的路径。</param>
/// <param name="SandboxRoot">本次试运行使用的沙箱根目录。</param>
public sealed record SandboxTrialResult(
    ValidationReport Report,
    WorkflowOutcome Outcome,
    ImmutableArray<string> ExercisedCapabilities,
    ImmutableArray<string> UndeclaredCapabilities,
    ImmutableArray<string> UnusedCapabilities,
    ImmutableArray<string> TouchedPaths,
    string SandboxRoot);

/// <summary>
/// V4：在隔离环境里预演一次，把「包声明的」与「实际发生的」对照起来（需求 24.1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>V4 回答的是前三层回答不了的那个问题</b>：V1 看格式、V2 静态扫危险模式、V3 看你申请了什么权限——
/// 三层都是"读声明"。而声明可能是<b>不完整</b>的：包在清单里只写了 <c>CAP.INSPECT</c>，
/// 工作流里却真的去调了写环境变量的动作（作者改了工作流却忘了改清单），
/// 或者反过来声明了一堆用不上的权限，让用户在能力授权前多按了几次同意。
/// 这两种偏差只有"真跑一遍"才能发现。
/// </para>
/// <para>
/// <b>为什么这样跑是安全的</b>（哪怕它跑的是别人分享的包）：
/// </para>
/// <list type="bullet">
///   <item>注入的是<b>只读</b>环境实现（<see cref="ReadOnlyEnvironmentOperations"/>）：
///         读得到真实现状（这样分支判断才有意义），写操作在结构上就没有可调用的实现；</item>
///   <item>网络通道<b>不注入</b>：试运行不产生任何真实出网请求；</item>
///   <item>可写根只有本次创建的沙箱临时目录，跑完即删；</item>
///   <item>无人值守：交互动作不会弹出任何东西打扰用户；</item>
///   <item>授权集合 = 包自己声明的能力——我们验证的是"声明是否属实"，不是"能不能干成"。</item>
/// </list>
/// <para>
/// <b>局限（必须说清楚，否则会被当成保证）</b>：
/// </para>
/// <list type="bullet">
///   <item>不注入网络，因此"下载"类步骤必然失败——V4 只能确认它<b>会走到</b>这一步、
///         需要哪个能力，不能确认下载地址是否可用（那是真实执行时的事）；</item>
///   <item>环境变量与注册表的写入被结构性拒绝，因此"写环境变量"类动作会以失败收场。
///         这正是我们要的：动作被调用的事实足以暴露副作用声明偏差，
///         而动作本身不可能改到真机；</item>
///   <item>文件写入只落在沙箱目录，跑完即删。</item>
/// </list>
/// </remarks>
public static class SandboxTrial
{
    /// <summary>执行一次沙箱试运行。</summary>
    /// <param name="manifest">已通过 V1~V3 的包清单。</param>
    /// <param name="workflow">包内工作流。</param>
    /// <param name="registry">动作注册表（用于读取每个动作的副作用声明）。</param>
    /// <param name="sandboxRoot">沙箱根目录；null 表示在临时目录下自动创建。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<SandboxTrialResult> RunAsync(
        PackageManifest manifest,
        WorkflowDocument workflow,
        ActionRegistry registry,
        string? sandboxRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(registry);

        var root = sandboxRoot ?? Path.Combine(
            Path.GetTempPath(),
            "envstation-v4-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        var findings = new FindingBag();

        // 授权集合 = 声明的能力 ∪ CAP.INSPECT。
        // 为什么要送 CAP.INSPECT：只读探测动作是绝大多数包的第一步（"先看看装没装"），
        // 而 V2 规则允许包不显式声明它（它改不了任何东西）。不送的话几乎每个包都会
        // 在第一步就因未授权而失败，V4 会变成"什么都测不出来"。
        var declared = manifest.Permissions.Keys.ToList();
        var granted = declared
            .Append(CapabilityIds.Inspect)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var ledger = new RunLedger
        {
            RunId = $"v4-{DateTime.Now:yyyyMMdd-HHmmss}",
            PackageId = manifest.Id,
            StartedAt = DateTimeOffset.Now,
        };

        var audit = new MemoryAuditSink();

        using var context = new ActionExecutionContext(
            packageId: manifest.Id,
            runId: ledger.RunId,
            grantedCapabilities: new CapabilitySet(granted),
            authorizedRoots: [root],
            variables: new VariableTable(),
            quota: new QuotaMeter(ResourceQuota.Default),
            audit: audit,
            progress: NullProgressSink.Instance,
            unattended: true,
            environment: ReadOnlyEnvironmentOperations.CreateDefault(),
            interaction: NullInteractionSink.Instance,
            ledger: ledger,
            allowedHosts: [.. manifest.AllowedHosts],
            network: null);

        var interpreter = new WorkflowInterpreter(registry);

        // 这里刻意用 DryRun: false——**沙箱隔离靠的是注入的实现，不是靠"不执行"**。
        //
        // 为什么不能用 DryRun: true：预演会跳过一切有副作用的动作（见缺陷 D-23 后的定义），
        // 于是"这个包实际需要哪些能力""实际会调用哪些动作"根本无从得知——
        // 而这两件事正是 V4 要回答的。一个什么都看不到的试运行等于没跑。
        //
        // 那为什么这样跑是安全的：真机副作用的通道只有三条，三条都被结构性掐断——
        //   ① 环境变量/注册表写入 → 注入的是 ReadOnlyEnvironmentOperations（没有可写的实现）；
        //   ② 网络 → 不注入 INetworkTransport；
        //   ③ 文件写入 → 可写根只有本次创建的沙箱临时目录。
        // 也就是说"隔离"体现在依赖注入上，而不是体现在"假装不执行"上。
        var outcome = await interpreter
            .RunAsync(workflow, context, new WorkflowOptions(DryRun: false, Unattended: true), cancellationToken)
            .ConfigureAwait(false);

        var exercised = CollectExercisedCapabilities(registry, ledger, audit);

        var undeclared = exercised
            .Where(c => !declared.Contains(c, StringComparer.Ordinal) && !string.Equals(c, CapabilityIds.Inspect, StringComparison.Ordinal))
            .ToImmutableArray();

        // 只统计"能力授权会展示给用户"的能力：CAP.INSPECT 由环境站隐式授予，不参与"多授权"的讨论。
        var unused = declared
            .Where(c => !exercised.Contains(c, StringComparer.Ordinal))
            .OrderBy(static c => c, StringComparer.Ordinal)
            .ToImmutableArray();

        foreach (var capability in undeclared)
        {
            findings.Block(
                "V4-01",
                "实际用到了未声明的能力",
                $"包在工作流里用到了能力 {capability}，但清单的 [permissions] 里没有声明它。",
                $"在清单的 [permissions] 中补上 {capability} 及其自然语言说明。",
                capability);
        }

        if (unused.Length > 0)
        {
            findings.Warn(
                "V4-02",
                "能力声明未被用到",
                $"清单声明了 {string.Join("、", unused)}，但本次试运行没有走到需要它们的步骤。",
                "条件分支未命中时属正常情况。确实用不上时删除声明，用户可少授权。",
                string.Join(",", unused));
        }

        CheckSideEffectDeclarations(manifest, registry, ledger, exercised, findings);

        if (!outcome.Succeeded)
        {
            findings.Warn(
                "V4-03",
                "沙箱试运行未通过",
                $"试运行在已授予全部声明能力的情况下失败：{outcome.Message}（{outcome.ErrorCode}）。",
                "缺少前置依赖时可在真实执行阶段安装。出现 E_CAPABILITY_DENIED 说明声明与工作流不一致，先修正声明。",
                outcome.ErrorCode);
        }

        var touched = ledger.Invocations
            .SelectMany(static i => i.TouchedPaths.OrEmpty())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new SandboxTrialResult(
            findings.ToReport(),
            outcome,
            exercised,
            undeclared,
            unused,
            touched,
            root);
    }

    /// <summary>
    /// 汇总"这个包实际需要哪些能力"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>必须同时看账本与审计，不能只看账本</b>：能力不足的动作会在管线里被<b>拒绝</b>，
    /// 而拒绝发生在写账本之前——也就是说"包想用但没声明"的那些能力，恰恰<b>不会</b>出现在账本里。
    /// 只看账本会让 V4-01（用到未声明的能力）永远不触发，而它正是本层最重要的那条结论。
    /// 审计里的 <c>action.rejected</c> + <c>E_CAPABILITY_DENIED</c> 才是这个事实的可靠来源。
    /// </para>
    /// <para>
    /// 顺带说明为什么这条检测有意义：它覆盖的是"作者改了工作流忘了改清单"，
    /// 而能力授权完全按清单渲染——没声明的能力用户看不到，也就无从授权或拒绝。
    /// </para>
    /// </remarks>
    private static ImmutableArray<string> CollectExercisedCapabilities(
        ActionRegistry registry,
        RunLedger ledger,
        MemoryAuditSink audit)
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var invocation in ledger.Invocations)
        {
            capabilities.Add(invocation.Capability);
        }

        foreach (var rejection in audit.Where("action.rejected"))
        {
            var denied = string.Equals(
                rejection.Fields.GetValueOrDefault("code"),
                EnvStationErrorCodes.CapabilityDenied,
                StringComparison.Ordinal);

            if (!denied)
            {
                continue;
            }

            var actionId = rejection.Fields.GetValueOrDefault("action");
            if (actionId is not null && registry.TryGet(actionId, out var action))
            {
                capabilities.Add(action.Descriptor.CapabilityId);
            }
        }

        return [.. capabilities.OrderBy(static c => c, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 核对"声明的副作用"与"实际调用的动作"是否一致。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>判据用"能力"而不是"动作声明的资源"</b>。第一版用的是
    /// <see cref="ActionDescriptor.TouchedResources"/>（动作<b>可能</b>触碰什么），
    /// 结果示例包被误判成"未声明写文件"——因为 <c>ui.notify</c> 声明了它可能写报告文件，
    /// 而这次根本没走到那条分支。用"可能"去判"实际"，必然产生假阻断，
    /// 而假阻断的代价是<b>好包被拦下</b>。
    /// </para>
    /// <para>
    /// 能力语义是精确的：<c>CAP.ENV.USER</c>/<c>CAP.ENV.MACHINE</c> 就代表"会写环境变量"，
    /// <c>CAP.FS.INSTALL</c> 就代表"会往文件系统写"。用这些判"实际会写什么"既准确又可解释。
    /// 资源声明的偏差降级为提示（V4-07），不再阻断。
    /// </para>
    /// </remarks>
    private static void CheckSideEffectDeclarations(
        PackageManifest manifest,
        ActionRegistry registry,
        RunLedger ledger,
        ImmutableArray<string> exercised,
        FindingBag findings)
    {
        var sideEffects = manifest.SideEffects
            ?? throw new InvalidOperationException("清单缺少副作用声明，V1 应已拦截。");

        var writesEnv = exercised.Contains(CapabilityIds.EnvironmentUser)
            || exercised.Contains(CapabilityIds.EnvironmentMachine)
            || exercised.Contains(CapabilityIds.PathModify);

        var writesFiles = exercised.Contains(CapabilityIds.FileSystemInstall)
            || exercised.Contains(CapabilityIds.Archive)
            || exercised.Contains(CapabilityIds.Cleanup);

        if (writesEnv && !sideEffects.WritesEnv)
        {
            findings.Block(
                "V4-04",
                "环境变量写入未声明",
                "试运行实际用到了会写环境变量的能力，但清单的 [side_effects].writes_env 为 false。",
                "把 [side_effects].writes_env 改为 true。",
                "writes_env");
        }

        if (writesFiles && !sideEffects.WritesFiles)
        {
            findings.Block(
                "V4-05",
                "文件写入未声明",
                "试运行实际用到了会写文件系统的能力，但清单的 [side_effects].writes_files 为 false。",
                "把 [side_effects].writes_files 改为 true。",
                "writes_files");
        }

        // 反向偏差只提示：声明得比实际"重"是安全的（少承诺、多披露），不值得阻断。
        if (!writesEnv && sideEffects.WritesEnv)
        {
            findings.Warn(
                "V4-06",
                "环境变量写入声明未用到",
                "清单声明会写环境变量，本次试运行没有走到任何写环境变量的步骤。",
                "条件分支未命中时可保留声明。",
                "writes_env");
        }

        WarnOnResourceDeclarationGaps(registry, ledger, findings);
    }

    /// <summary>
    /// 提示"动作声明可能触碰的资源"与清单声明的差异。
    /// </summary>
    /// <remarks>
    /// 刻意只是提示：<see cref="ActionDescriptor.TouchedResources"/> 描述的是动作<b>可能</b>触碰什么，
    /// 不等于本次真的触碰了。把它当阻断判据会拦下本来没问题的包（示例包就踩过一次）。
    /// </remarks>
    private static void WarnOnResourceDeclarationGaps(
        ActionRegistry registry,
        RunLedger ledger,
        FindingBag findings)
    {
        var resources = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var invocation in ledger.Invocations)
        {
            if (registry.TryGet(invocation.ActionId, out var action))
            {
                foreach (var resource in action.Descriptor.TouchedResources.OrEmpty())
                {
                    resources.Add(resource);
                }
            }
        }

        if (resources.Count > 0)
        {
            findings.Info(
                "V4-07",
                "动作声明可能触碰的资源",
                $"涉及 {resources.Count} 项：{string.Join("、", resources)}。",
                "用于核对副作用摘要是否完整，不代表本次真的发生了写入。",
                string.Join(",", resources));
        }
    }
}
