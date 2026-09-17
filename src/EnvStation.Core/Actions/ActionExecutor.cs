using System.Collections.Immutable;
using System.Diagnostics;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;

namespace EnvStation.Core.Actions;

/// <summary>一次动作执行的可观测记录（用于报告与审计对照，需求 7.2 的收尾两步）。</summary>
/// <param name="ActionId">动作 ID。</param>
/// <param name="Version">动作版本。</param>
/// <param name="Capability">所需能力。</param>
/// <param name="ArgumentsSummary">已脱敏的参数摘要。</param>
/// <param name="Succeeded">是否成功。</param>
/// <param name="ErrorCode">错误码（成功为 <c>E_OK</c>）。</param>
/// <param name="Message">用户可见消息。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="TouchedPaths">触碰的路径。</param>
/// <param name="Outputs">结构化输出。</param>
/// <param name="ReversibleToken">反向操作凭据。</param>
public sealed record ActionInvocation(
    string ActionId,
    string Version,
    string Capability,
    string ArgumentsSummary,
    bool Succeeded,
    string ErrorCode,
    string Message,
    TimeSpan Elapsed,
    ImmutableArray<string> TouchedPaths,
    ImmutableDictionary<string, string> Outputs,
    string? ReversibleToken);

/// <summary>
/// 动作执行管线（需求 7.2 的八步）。
/// </summary>
/// <remarks>
/// <para><b>为什么所有动作都必须经由此处</b>：能力校验、超时、审计、异常归一这四件事
/// 一旦有一个动作绕过，整个安全模型就出现缺口。因此本项目<b>不提供</b>直接调用
/// <see cref="IAction.ExecuteAsync"/> 的公开入口——工作流解释器与 CLI 都只经过本类。</para>
/// <para><b>失败必须是返回值</b>：动作抛出的任何异常都会在这里被转成结构化失败。
/// 原因见 M0 的产品缺陷 D-1——异常穿透会跳过调用方的回滚逻辑，直接破坏"失败零污染"承诺。</para>
/// </remarks>
public static class ActionExecutor
{
    /// <summary>执行一个动作。调用方必须已完成参数绑定（<see cref="ActionArgumentsBinder"/>）。</summary>
    /// <param name="action">动作实现。</param>
    /// <param name="context">执行上下文。</param>
    /// <param name="bound">已绑定的参数。</param>
    /// <param name="timeoutSeconds">本步骤的超时秒数；null 表示使用动作默认值。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    public static async ValueTask<ActionResult> ExecuteAsync(
        IAction action,
        ActionExecutionContext context,
        BoundArguments bound,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bound);

        var descriptor = action.Descriptor;
        var summary = context.DescribeArguments(bound.Arguments, bound.SecretNames);

        // ── 第 1 步：能力校验（需求 A2：未授权直接拒绝，不是跳过）──
        if (!context.GrantedCapabilities.Contains(descriptor.CapabilityId))
        {
            return Reject(
                context, descriptor, summary,
                EnvStationErrorCodes.CapabilityDenied,
                $"动作 {descriptor.ActionId} 需要能力 {descriptor.CapabilityId}，本次运行未获得该授权。",
                "在能力授权中勾选该能力后重试。未授权的能力一律拒绝执行，不会静默跳过。");
        }

        // ── 无人值守保护 ──
        //
        // 默认行为是**直接拒绝**需要用户在场的动作。只有动作显式声明
        // DegradesWhenUnattended 时，才把"该怎么办"交给动作自己决定
        // （例如 ui.prompt：有默认值就用默认值，没有才失败）。
        // 这样既保留了最安全的默认，又允许动作给出更有用的降级行为。
        if (descriptor.RequiresUserPresence && !descriptor.DegradesWhenUnattended && context.Unattended)
        {
            return Reject(
                context, descriptor, summary,
                EnvStationErrorCodes.UnattendedInteraction,
                $"动作 {descriptor.ActionId} 需要用户在场确认，当前为无人值守模式。",
                "在带界面的环境中运行该包。");
        }

        // ── 第 2~4 步（参数 schema / 配额预检 / 路径守卫）已在前置阶段完成 ──
        //    参数：ActionArgumentsBinder 在绑定期完成类型、范围、枚举与未知参数检查。
        //    配额：动作在真正消耗资源前调用 context.Quota.TryConsume（下载/解压/写文件）。
        //    路径：动作对每个路径参数调用 context.GuardPath。
        //    这样做的原因：这三件事都需要"动作自己的语义"才能做准确，
        //    强行在管线里做通用版本只会做出一个既慢又不准的近似。

        // ── 第 5 步：整包时长配额（需求 22.3）──
        //
        // 本步骤的实际超时 = min(步骤超时, 整包剩余时间)。
        // 为什么必须在这里做：ResourceQuota.MaxWallClock 早就建模了，但一直没接入实际计时，
        // 于是"整包最多跑 30 分钟"只是一句文档——一个写错的包（或恶意包）可以无限拖下去，
        // 而用户看到的是界面卡在某个步骤上，没有任何解释。配额不接入等于没有。
        //
        // 位置放在 action.begin 审计**之前**：被拒绝的动作不应留下"已开始"的记录。
        // 这条与 AC-19（未授权动作不得有 begin 记录）是同一条契约，AC-32b 会钉住它。
        var stepTimeout = TimeSpan.FromSeconds(timeoutSeconds ?? descriptor.DefaultTimeoutSeconds);
        var remaining = context.RemainingWallClock;

        if (remaining <= TimeSpan.Zero)
        {
            return Reject(
                context, descriptor, summary,
                EnvStationErrorCodes.ActionTimeout,
                $"本次包运行已超过整包时长上限 {context.Quota.Limits.MaxWallClock.TotalMinutes:F0} 分钟，后续步骤不再执行。",
                "在设置中提高整包时长配额后重试，已完成的步骤可用快照回滚。");
        }

        var hitWallClockQuota = remaining < stepTimeout;
        var effectiveTimeout = hitWallClockQuota ? remaining : stepTimeout;

        // ── 第 6 步：审计开始（参数已脱敏）──
        context.Audit("action.begin", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = descriptor.ActionId,
            ["version"] = descriptor.Version,
            ["capability"] = descriptor.CapabilityId,
            ["args"] = summary,
        });

        var clock = Stopwatch.StartNew();

        using var timeoutSource = new CancellationTokenSource(effectiveTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        ActionResult result;
        try
        {
            result = await action.ExecuteAsync(context, bound.Arguments, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // 两种超时要给出不同的文案：一种是"这个动作本身太慢"，另一种是"整包预算用完了"。
            // 混成一句话会让用户去优化一个根本不需要优化的动作。
            result = ActionResult.Fail(
                EnvStationErrorCodes.ActionTimeout,
                hitWallClockQuota
                    ? $"动作 {descriptor.ActionId} 已中止：整包时长配额（{context.Quota.Limits.MaxWallClock.TotalMinutes:F0} 分钟）已用尽。"
                    : $"动作 {descriptor.ActionId} 已中止：单步执行超过 {effectiveTimeout.TotalSeconds:F0} 秒。");
        }
        catch (OperationCanceledException)
        {
            result = ActionResult.Fail(
                EnvStationErrorCodes.WorkflowAborted,
                $"动作 {descriptor.ActionId} 被用户取消。");
        }
        catch (Exception ex)
        {
            // 兜底：把任何穿透的异常转成结构化失败，保证调用方的回滚逻辑一定会执行。
            result = ActionResult.Fail(
                EnvStationErrorCodes.ActionFailed,
                $"动作 {descriptor.ActionId} 执行失败（内部错误）：{context.VariableTable.Redact(ex.Message)}");
        }

        clock.Stop();

        // ── 第 7~8 步：结果记录与审计收尾 ──
        context.Audit("action.end", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = descriptor.ActionId,
            ["ok"] = result.Success ? "1" : "0",
            ["code"] = result.ErrorCode,
            ["ms"] = ((long)clock.Elapsed.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["touched"] = result.TouchedPaths.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        // 账本记录必须在这里做，而不是只在 ExecuteWithRecordAsync 里做。
        // 理由：report.generate 动作读的是"本次运行到目前为止做过什么"，
        // 而绝大多数调用方（工作流解释器、CLI）走的是 ExecuteAsync。
        // 只在带记录版本里写账本，会让报告永远是空的——这是一次真实缺陷（VA-24 暴露）。
        var invocation = new ActionInvocation(
            descriptor.ActionId,
            descriptor.Version,
            descriptor.CapabilityId,
            summary,
            result.Success,
            result.ErrorCode,
            result.Message,
            clock.Elapsed,
            result.TouchedPaths,
            result.Outputs,
            result.ReversibleToken);

        context.Ledger.Record(invocation);

        return result;
    }

    /// <summary>执行并返回完整的调用记录（供需要立刻拿到本次记录的调用方使用）。</summary>
    /// <remarks>
    /// 账本本身由 <see cref="ExecuteAsync"/> 统一写入；本方法只是把同一条记录也返回给调用方，
    /// 避免调用方为了拿记录而重复执行一次动作。
    /// </remarks>
    public static async ValueTask<(ActionResult Result, ActionInvocation Invocation)> ExecuteWithRecordAsync(
        IAction action,
        ActionExecutionContext context,
        BoundArguments bound,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bound);

        var before = context.Ledger.Invocations.Count;
        var result = await ExecuteAsync(action, context, bound, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        // ExecuteAsync 一定会追加一条记录；若因某种原因没有，则在此补一条，
        // 保证调用方拿到的记录与账本内容始终一致。
        if (context.Ledger.Invocations.Count > before)
        {
            return (result, context.Ledger.Invocations[^1]);
        }

        var descriptor = action.Descriptor;
        var invocation = new ActionInvocation(
            descriptor.ActionId,
            descriptor.Version,
            descriptor.CapabilityId,
            context.DescribeArguments(bound.Arguments, bound.SecretNames),
            result.Success,
            result.ErrorCode,
            result.Message,
            TimeSpan.Zero,
            result.TouchedPaths,
            result.Outputs,
            result.ReversibleToken);

        context.Ledger.Record(invocation);
        return (result, invocation);
    }

    private static ActionResult Reject(
        ActionExecutionContext context,
        ActionDescriptor descriptor,
        string summary,
        string errorCode,
        string message,
        string remediation)
    {
        context.Audit("action.rejected", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = descriptor.ActionId,
            ["code"] = errorCode,
            ["args"] = summary,
        });

        return ActionResult.Fail(errorCode, message + " " + remediation);
    }
}
