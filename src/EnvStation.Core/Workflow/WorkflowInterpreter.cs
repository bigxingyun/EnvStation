using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Actions;
using EnvStation.Core.Scripting;

namespace EnvStation.Core.Workflow;

/// <summary>工作流执行的最终结论。</summary>
/// <param name="Succeeded">是否成功完成。</param>
/// <param name="ErrorCode">失败时的错误码。</param>
/// <param name="Message">给用户看的结论说明。</param>
/// <param name="ExecutedSteps">实际执行的步骤数。</param>
/// <param name="SkippedSteps">因条件不成立而跳过的步骤数。</param>
/// <param name="RolledBack">是否已回滚。</param>
/// <param name="Elapsed">总耗时。</param>
public sealed record WorkflowOutcome(
    bool Succeeded,
    string ErrorCode,
    string Message,
    int ExecutedSteps,
    int SkippedSteps,
    bool RolledBack,
    TimeSpan Elapsed);

/// <summary>工作流执行选项。</summary>
/// <param name="DryRun">
/// 预演（DryRun）：只求值、只绑定参数、只检查能力，<b>不调用任何动作</b>。
/// 用于导入流程第 6 步的"变更清单预览"（需求 25.4）。
/// </param>
/// <param name="Unattended">无人值守模式（交互动作会被拒绝或降级）。</param>
/// <param name="MaxTotalSteps">整包累计执行步骤上限，防止用嵌套循环构造超长执行。</param>
/// <param name="CancellationCheckInterval">每执行多少步检查一次取消（用于长工作流的响应性）。</param>
public sealed record WorkflowOptions(
    bool DryRun = false,
    bool Unattended = false,
    int MaxTotalSteps = 2000,
    int CancellationCheckInterval = 1);

/// <summary>
/// 工作流解释器：把 <see cref="WorkflowDocument"/> 跑起来。
/// </summary>
/// <remarks>
/// <para>
/// <b>它与动作执行管线的分工</b>：管线负责"一个动作怎么安全地做"，
/// 解释器负责"这些动作按什么顺序、在什么条件下做，出错了怎么办"。
/// 解释器<b>不直接调用 <c>IAction.ExecuteAsync</c></b>——所有调用都经
/// <see cref="ActionExecutor"/>，从而保证能力校验、超时、审计、异常归一这四件事无法被绕过。
/// </para>
/// <para>
/// <b>三条并发与隔离纪律</b>：
/// <list type="number">
///   <item><c>parallel</c> 只允许声明为 <c>IsParallelSafe</c> 的动作，否则在<b>执行前</b>就报错——
///         把"不可并行的动作写进了 parallel"这件事留到运行期才发现，
///         会变成偶发的、难以复现的数据竞争。</item>
///   <item>任意一步失败时的默认策略是 <c>rollback</c>：整包中止并回滚。
///         这是"失败零污染"承诺在工作流层的落点。</item>
///   <item>变量作用域只有 <c>pkg.*</c> 可写，<c>secret.*</c> 全程不落盘。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class WorkflowInterpreter
{
    private readonly ActionRegistry _registry;

    /// <summary>构造解释器。</summary>
    public WorkflowInterpreter(ActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// 执行工作流。
    /// </summary>
    /// <param name="document">已解析并通过校验的工作流。</param>
    /// <param name="context">执行上下文（能力、授权根、变量表、配额、审计、交互）。</param>
    /// <param name="options">执行选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask<WorkflowOutcome> RunAsync(
        WorkflowDocument document,
        ActionExecutionContext context,
        WorkflowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        options ??= new WorkflowOptions();

        var clock = Stopwatch.StartNew();
        var state = new RunState(document.DefaultErrorPolicy, options);

        context.Ledger.StartedAt.ToString("O", CultureInfo.InvariantCulture);

        try
        {
            var result = await ExecuteBlockAsync(document.Steps, context, state, cancellationToken)
                .ConfigureAwait(false);

            clock.Stop();

            if (result.Failure is not null)
            {
                return HandleFailure(result.Failure, context, state, clock);
            }

            context.Ledger.CompletedAt = DateTimeOffset.Now;
            context.Ledger.Outcome = "成功完成";

            return new WorkflowOutcome(
                true,
                EnvStationErrorCodes.Ok,
                $"工作流执行完成：执行 {state.ExecutedSteps} 步，跳过 {state.SkippedSteps} 步。",
                state.ExecutedSteps,
                state.SkippedSteps,
                RolledBack: false,
                clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            context.Ledger.CompletedAt = DateTimeOffset.Now;
            context.Ledger.Outcome = "被用户取消";

            return new WorkflowOutcome(
                false,
                EnvStationErrorCodes.WorkflowAborted,
                "工作流已取消。已完成的修改未回滚，可在「快照与回滚」中回滚。",
                state.ExecutedSteps,
                state.SkippedSteps,
                RolledBack: false,
                clock.Elapsed);
        }
    }

    // ────────────────────────────── 执行 ──────────────────────────────

    /// <summary>一次块级执行的结果。</summary>
    private readonly record struct BlockResult(StepFailure? Failure);

    /// <summary>步骤失败信息。</summary>
    private sealed record StepFailure(
        WorkflowStep Step,
        ErrorPolicy Policy,
        string ErrorCode,
        string Message);

    private sealed class RunState(ErrorPolicy defaultPolicy, WorkflowOptions options)
    {
        internal ErrorPolicy DefaultPolicy { get; } = defaultPolicy;

        internal WorkflowOptions Options { get; } = options;

        internal int ExecutedSteps { get; set; }

        internal int SkippedSteps { get; set; }

        /// <summary>已登记的"需要回滚"凭据（快照 ID）。</summary>
        internal List<string> ReversibleTokens { get; } = [];

        /// <summary>预演时被跳过的有副作用步骤所对应的 register 名。</summary>
        internal HashSet<string> SimulatedRegisters { get; } = new(StringComparer.Ordinal);

        internal bool LimitExceeded { get; set; }
    }

    /// <summary>
    /// 判断一个动作是否"只读"。
    /// </summary>
    /// <remarks>
    /// 判据只有一个：它只申请 <c>CAP.INSPECT</c>。这不是"看动作名猜"，而是看它<b>声明的能力</b>——
    /// 能力是执行管线的强制约束，因此"只申请 INSPECT"等价于"它在架构上就不可能改动机器"。
    /// 这样预演就可以放心地真正执行这些动作。
    /// </remarks>
    private static bool IsReadOnly(ActionDescriptor descriptor) =>
        descriptor.CapabilityId == CapabilityIds.Inspect
        && descriptor.ConditionalCapabilities.OrEmpty().Length == 0;

    /// <summary>判断某一步的参数是否引用了被模拟的变量。</summary>
    private static bool ReferencesSimulated(RunState state, ImmutableDictionary<string, ScriptValue> arguments)
    {
        if (state.SimulatedRegisters.Count == 0)
        {
            return false;
        }

        foreach (var value in arguments.Values)
        {
            foreach (var name in state.SimulatedRegisters)
            {
                if (value is ScriptString text && text.Value.Contains($"${{pkg.{name}.", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async ValueTask<BlockResult> ExecuteBlockAsync(
        ImmutableArray<WorkflowStep> steps,
        ActionExecutionContext context,
        RunState state,
        CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (state.ExecutedSteps >= state.Options.MaxTotalSteps)
            {
                state.LimitExceeded = true;
                return new BlockResult(new StepFailure(
                    step,
                    ErrorPolicy.Rollback,
                    EnvStationErrorCodes.WorkflowLoopLimitExceeded,
                    $"本次运行累计执行步骤数超过上限 {state.Options.MaxTotalSteps} 步。"));
            }

            // 步骤级 when 守卫
            if (step.When is { Length: > 0 } guard)
            {
                var guardResult = ExpressionEvaluator.EvaluateCondition(guard, context.VariableTable);
                if (guardResult.IsFailure)
                {
                    return new BlockResult(new StepFailure(
                        step, ResolvePolicy(step, state), guardResult.Error.Code, guardResult.Error.Message));
                }

                if (!guardResult.Value)
                {
                    state.SkippedSteps++;
                    continue;
                }
            }

            var result = await ExecuteStepAsync(step, context, state, cancellationToken).ConfigureAwait(false);
            if (result.Failure is { } failure)
            {
                if (failure.Policy == ErrorPolicy.Continue)
                {
                    // continue：记录后继续。这是包作者显式声明的"这一步失败不要紧"。
                    context.Audit("workflow.step.continued", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["step"] = step.Id ?? step.KindName,
                        ["code"] = failure.ErrorCode,
                        ["message"] = failure.Message,
                    });

                    state.SkippedSteps++;
                    continue;
                }

                return result;
            }
        }

        return default;
    }

    private async ValueTask<BlockResult> ExecuteStepAsync(
        WorkflowStep step,
        ActionExecutionContext context,
        RunState state,
        CancellationToken cancellationToken)
    {
        switch (step)
        {
            case ActionCallStep call:
                return await ExecuteActionStepAsync(call, context, state, cancellationToken).ConfigureAwait(false);

            case AssertStep assert:
                {
                    state.ExecutedSteps++;
                    var evaluated = ExpressionEvaluator.EvaluateCondition(assert.Condition, context.VariableTable);
                    if (evaluated.IsFailure)
                    {
                        return new BlockResult(new StepFailure(
                            step, ResolvePolicy(step, state), evaluated.Error.Code, evaluated.Error.Message));
                    }

                    if (!evaluated.Value)
                    {
                        return new BlockResult(new StepFailure(
                            step,
                            ResolvePolicy(step, state),
                            EnvStationErrorCodes.AssertFailed,
                            assert.Message));
                    }

                    return default;
                }

            case ConditionalStep conditional:
                {
                    var evaluated = ExpressionEvaluator.EvaluateCondition(conditional.Condition, context.VariableTable);
                    if (evaluated.IsFailure)
                    {
                        return new BlockResult(new StepFailure(
                            step, ResolvePolicy(step, state), evaluated.Error.Code, evaluated.Error.Message));
                    }

                    var branch = evaluated.Value ? conditional.Then : conditional.Else;
                    return await ExecuteBlockAsync(branch, context, state, cancellationToken).ConfigureAwait(false);
                }

            case ForEachStep loop:
                return await ExecuteForEachAsync(loop, context, state, cancellationToken).ConfigureAwait(false);

            case ParallelStep parallel:
                return await ExecuteParallelAsync(parallel, context, state, cancellationToken).ConfigureAwait(false);

            case ConfirmStep confirm:
                return await ExecuteConfirmAsync(confirm, context, state, cancellationToken).ConfigureAwait(false);

            default:
                return new BlockResult(new StepFailure(
                    step,
                    ErrorPolicy.Rollback,
                    EnvStationErrorCodes.WorkflowExpressionFailed,
                    $"未知的步骤类型：{step.KindName}。"));
        }
    }

    private async ValueTask<BlockResult> ExecuteActionStepAsync(
        ActionCallStep step,
        ActionExecutionContext context,
        RunState state,
        CancellationToken cancellationToken)
    {
        // ① 解析动作（含版本与契约哈希核对）
        var resolved = _registry.Resolve(step.Reference);
        if (resolved.IsFailure)
        {
            return new BlockResult(new StepFailure(
                step, ResolvePolicy(step, state), resolved.Error.Code, Describe(resolved.Error)));
        }

        var action = resolved.Value;

        // ② 参数插值 → ③ 强类型绑定（都在动作执行之前，因此错误不会留下半成品）
        var interpolated = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
        foreach (var (name, value) in step.Arguments)
        {
            var result = VariableInterpolator.Interpolate(value, context.VariableTable);
            if (result.IsFailure)
            {
                return new BlockResult(new StepFailure(
                    step, ResolvePolicy(step, state), result.Error.Code, Describe(result.Error)));
            }

            interpolated[name] = result.Value;
        }

        var findings = new FindingBag();
        var bound = ActionArgumentsBinder.Bind(action.Descriptor, interpolated, findings, Locate(step));
        if (bound.IsFailure)
        {
            // 预演时，某些变量来自被跳过的**有副作用步骤**，此时它的值要等到真正执行才存在。
            // 这种情况下跳过整个步骤并说明原因，而不是判失败——
            // 否则一个正常的多步工作流在预演下必然"失败"，预演就失去了意义。
            if (state.Options.DryRun
                && findings.Items.Any(static f => f.RuleId == EnvStationErrorCodes.WorkflowVariableUndefined)
                && ReferencesSimulated(state, step.Arguments))
            {
                state.SkippedSteps++;
                return default;
            }

            return new BlockResult(new StepFailure(
                step, ResolvePolicy(step, state), bound.Error.Code, findings.ToReport().ToText()));
        }

        // ④ 预演：只读动作照常执行，有副作用的动作一律跳过。
        //
        // 这个划分是整个预演机制能否有用的关键：
        //   · 只读动作（CAP.INSPECT）本来就不会改动机器，**真正执行**才能算出后续步骤需要的真实值；
        //   · 有副作用的动作**一个都不执行**，其输出被标记为"模拟"。
        // 只做后者的话，任何依赖前面探测结果的工作流在预演下都无法继续，
        // 用户也就得不到真正的"变更预览"。
        if (state.Options.DryRun && !IsReadOnly(action.Descriptor))
        {
            state.ExecutedSteps++;

            if (step.Register is { Length: > 0 } simulatedName)
            {
                context.VariableTable.Set($"pkg.{simulatedName}.simulated", "true");
                context.VariableTable.Set($"pkg.{simulatedName}.success", "true");
                context.VariableTable.Set($"pkg.{simulatedName}.message", "预演：该步骤未执行");
                state.SimulatedRegisters.Add(simulatedName);
            }

            return default;
        }

        // ⑤ 执行
        var (actionResult, invocation) = await ActionExecutor
            .ExecuteWithRecordAsync(action, context, bound.Value, step.TimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        state.ExecutedSteps++;

        if (invocation.ReversibleToken is { Length: > 0 } token)
        {
            state.ReversibleTokens.Add(token);
        }

        // ⑥ register：把输出绑定为 pkg.* 变量
        if (step.Register is { Length: > 0 } variableName)
        {
            BindOutputs(context, variableName, actionResult);
        }

        if (!actionResult.Success)
        {
            return new BlockResult(new StepFailure(
                step, ResolvePolicy(step, state), actionResult.ErrorCode, actionResult.Message));
        }

        _ = step;
        return default;
    }

    /// <summary>
    /// 把动作的结构化输出绑定为变量。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两层命名：<c>pkg.&lt;register&gt;.&lt;key&gt;</c> 取单个输出字段，
    /// <c>pkg.&lt;register&gt;.success</c> / <c>.message</c> 始终可用（便于写 <c>if</c> 条件）。
    /// </para>
    /// <para>
    /// 所有值都以字符串存储，由 <see cref="VariableTable"/> 做保守的类型推断
    /// （小写 true/false → 布尔；自身规范形式的数字 → 数值；其余保持字符串）。
    /// </para>
    /// </remarks>
    private static void BindOutputs(ActionExecutionContext context, string variableName, ActionResult result)
    {
        var table = context.VariableTable;
        table.Set($"pkg.{variableName}.success", result.Success ? "true" : "false");
        table.Set($"pkg.{variableName}.error_code", result.ErrorCode);
        table.Set($"pkg.{variableName}.message", result.Message);

        foreach (var (key, value) in result.Outputs)
        {
            table.Set($"pkg.{variableName}.{NormalizeKey(key)}", value);
        }
    }

    private static string NormalizeKey(string key)
    {
        // 输出键可能含冒号或空格（例如 env.get 的 "user:PATH"）。
        // 变量名只允许字母数字下划线点，因此把非法字符统一换成下划线，
        // 这样包作者始终能用一个合法变量名引用到它。
        var chars = key.Select(static c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private async ValueTask<BlockResult> ExecuteForEachAsync(
        ForEachStep loop,
        ActionExecutionContext context,
        RunState state,
        CancellationToken cancellationToken)
    {
        // 数据源必须是一个变量（需求 20.5：foreach 的数据集必须来自变量）。
        var source = loop.Source;
        var collected = VariableInterpolator.ResolveList(source, context.VariableTable);
        if (collected.IsFailure)
        {
            return new BlockResult(new StepFailure(
                loop, ResolvePolicy(loop, state), collected.Error.Code, Describe(collected.Error)));
        }

        var items = collected.Value;

        if (items.Length > loop.MaxIterations)
        {
            return new BlockResult(new StepFailure(
                loop,
                ResolvePolicy(loop, state),
                EnvStationErrorCodes.WorkflowLoopLimitExceeded,
                $"foreach 的数据集有 {items.Length} 项，超过声明的上限 max = {loop.MaxIterations}。" +
                "提高 max（硬上限 1000）或缩小数据集。"));
        }

        for (var i = 0; i < items.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 迭代变量：pkg.<as> 直接是当前项，pkg.<as>.index 是序号。
            context.VariableTable.Set($"pkg.{loop.ItemName}", items[i]);
            context.VariableTable.Set($"pkg.{loop.ItemName}.index", i.ToString(CultureInfo.InvariantCulture));
            context.VariableTable.Set($"pkg.{loop.ItemName}.count", items.Length.ToString(CultureInfo.InvariantCulture));

            var result = await ExecuteBlockAsync(loop.Body, context, state, cancellationToken).ConfigureAwait(false);
            if (result.Failure is { } failure)
            {
                return new BlockResult(failure);
            }
        }

        return default;
    }

    private async ValueTask<BlockResult> ExecuteParallelAsync(
        ParallelStep parallel,
        ActionExecutionContext context,
        RunState state,
        CancellationToken cancellationToken)
    {
        // 并行前的静态检查：分支里只要出现一个未声明可并行的动作，就整体拒绝。
        //
        // 为什么要在执行前查而不是执行时报错：一旦真的并发跑起来，
        // 不可并行的动作可能已经产生了副作用，而"哪一步先跑"是不确定的。
        // 那种失败模式极难复现，因此在开跑之前就拦住。
        var unsafeBranches = parallel.Branches
            .Select(static b => b)
            .Where(static b => b is not ActionCallStep)
            .ToArray();

        if (unsafeBranches.Length > 0)
        {
            return new BlockResult(new StepFailure(
                parallel,
                ResolvePolicy(parallel, state),
                EnvStationErrorCodes.ActionArgumentInvalid,
                "parallel 的分支只能是动作调用步骤：if、foreach 这类控制结构不能放进并行块。"));
        }

        foreach (var branch in parallel.Branches)
        {
            var call = (ActionCallStep)branch;
            var resolved = _registry.Resolve(call.Reference);
            if (resolved.IsFailure)
            {
                return new BlockResult(new StepFailure(
                    parallel, ResolvePolicy(parallel, state), resolved.Error.Code, Describe(resolved.Error)));
            }

            if (!resolved.Value.Descriptor.IsParallelSafe)
            {
                return new BlockResult(new StepFailure(
                    parallel,
                    ResolvePolicy(parallel, state),
                    EnvStationErrorCodes.ActionArgumentInvalid,
                    $"动作 {call.Reference.ActionId} 未声明可并行（IsParallelSafe = false），不能出现在 parallel 块中。"));
            }
        }

        if (state.Options.DryRun)
        {
            state.ExecutedSteps += parallel.Branches.Length;
            return default;
        }

        // 真正并行：每个分支一个任务。分支之间不允许共享可写状态——
        // 唯一的共享点是变量表，而并行分支里的动作只读变量（写变量需要 register，见下）。
        var tasks = parallel.Branches
            .Cast<ActionCallStep>()
            .Select(call => ExecuteActionStepAsync(call, context, state, cancellationToken).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var result in results)
        {
            if (result.Failure is { } failure)
            {
                return new BlockResult(failure);
            }
        }

        return default;
    }

    private static async ValueTask<BlockResult> ExecuteConfirmAsync(
        ConfirmStep confirm,
        ActionExecutionContext context,
        RunState state,
        CancellationToken cancellationToken)
    {
        state.ExecutedSteps++;

        if (state.Options.Unattended || state.Options.DryRun)
        {
            // 无人值守/预演时按声明的默认走向处理，并留痕——
            // 绝不假装"用户同意了"。
            context.Audit("workflow.confirm.auto", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["step"] = confirm.Id ?? "confirm",
                ["default"] = confirm.DefaultYes ? "yes" : "no",
                ["mode"] = state.Options.DryRun ? "dry-run" : "unattended",
            });

            return confirm.DefaultYes
                ? default
                : new BlockResult(new StepFailure(
                    confirm,
                    ErrorPolicy.Abort,
                    EnvStationErrorCodes.WorkflowAborted,
                    $"步骤「{confirm.Message}」需要用户确认，在无人值守或预演模式下按默认值（否）处理，已中止。"));
        }

        var answer = await context.Interaction
            .PromptAsync(
                new PromptRequest(confirm.Message, PromptType.Confirm, confirm.DefaultYes ? "yes" : "no", [], false),
                cancellationToken)
            .ConfigureAwait(false);

        if (!answer.Answered || !IsYes(answer.Value))
        {
            return new BlockResult(new StepFailure(
                confirm,
                ErrorPolicy.Abort,
                EnvStationErrorCodes.WorkflowAborted,
                $"用户未确认：{confirm.Message}"));
        }

        return default;
    }

    private static bool IsYes(string value) =>
        value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("y", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("是", StringComparison.Ordinal);

    private static ErrorPolicy ResolvePolicy(WorkflowStep step, RunState state) =>
        step.OnError ?? state.DefaultPolicy;

    // ────────────────────────────── 失败处理 ──────────────────────────────

    private static WorkflowOutcome HandleFailure(
        StepFailure failure,
        ActionExecutionContext context,
        RunState state,
        Stopwatch clock)
    {
        clock.Stop();

        var stepName = failure.Step.Id ?? failure.Step.KindName;

        context.Audit("workflow.failed", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["step"] = stepName,
            ["code"] = failure.ErrorCode,
            ["policy"] = failure.Policy.ToString(),
        });

        switch (failure.Policy)
        {
            case ErrorPolicy.Abort:
                context.Ledger.CompletedAt = DateTimeOffset.Now;
                context.Ledger.Outcome = $"已中止：步骤 {stepName} 失败";

                return new WorkflowOutcome(
                    false,
                    failure.ErrorCode,
                    $"步骤「{stepName}」失败：{failure.Message}" +
                    " 工作流已中止，已完成的修改未回滚（on_error = abort）。" +
                    " 可在「快照与回滚」中选择要回滚到的快照。",
                    state.ExecutedSteps,
                    state.SkippedSteps,
                    RolledBack: false,
                    clock.Elapsed);

            case ErrorPolicy.Continue:
                // Continue 不会走到这里（已在调用方处理），留作防御。
                return new WorkflowOutcome(
                    true, EnvStationErrorCodes.Ok, "已按 continue 策略忽略该失败。",
                    state.ExecutedSteps, state.SkippedSteps, false, clock.Elapsed);

            default:
                var rolledBack = TryRollback(context, state);

                context.Ledger.CompletedAt = DateTimeOffset.Now;
                context.Ledger.Outcome = rolledBack
                    ? $"已回滚：步骤 {stepName} 失败"
                    : $"回滚失败：步骤 {stepName} 失败";

                return new WorkflowOutcome(
                    false,
                    rolledBack ? failure.ErrorCode : EnvStationErrorCodes.TxRollbackFailed,
                    rolledBack
                        ? $"步骤「{stepName}」失败：{failure.Message} 已回滚到修改前的状态。"
                        : $"步骤「{stepName}」失败：{failure.Message}" +
                          " 自动回滚未完全成功，已完成的修改可能仍在，立即在「快照与回滚」中手动处理。",
                    state.ExecutedSteps,
                    state.SkippedSteps,
                    rolledBack,
                    clock.Elapsed);
        }
    }

    /// <summary>
    /// 按"最近一次快照"回滚。
    /// </summary>
    /// <remarks>
    /// 只回滚<b>最早那个</b>快照：因为包运行期间的所有环境变量修改都串在同一个事务链上，
    /// 回到最早那个快照等价于"回到包开始之前"。逐个回滚反而会把中间状态挨个走一遍，
    /// 既慢又可能在中间状态上触发别的工具的自动反应。
    /// </remarks>
    private static bool TryRollback(
        ActionExecutionContext context,
        RunState state)
    {
        // 没有任何可逆凭据 = 本次运行没有产生需要回滚的环境副作用。
        //
        // 这个判断必须**先于**"有没有环境实现"：否则预演（本来就不注入环境实现）会被报成
        // "回滚失败"，而真相是"根本没有东西需要回滚"。
        // 这是一次真实缺陷：那句误导性的告警会让用户以为环境已经被改坏了。
        var earliest = state.ReversibleTokens.FirstOrDefault();
        if (earliest is null)
        {
            return true;
        }

        if (context.Environment is null)
        {
            state.ReversibleTokens.Clear();
            return false;
        }

        // 按加入顺序，第一个加入的最早。

        var snapshot = context.Environment.LoadSnapshot(earliest);
        if (snapshot.IsFailure)
        {
            return false;
        }

        var restore = context.Environment.Restore(snapshot.Value, names: null);

        context.Audit("workflow.rollback", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["snapshot_id"] = earliest,
            ["ok"] = restore.IsSuccess ? "1" : "0",
        });

        state.ReversibleTokens.Clear();
        return restore.IsSuccess;
    }

    private static string Describe(EnvStationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Remediation is null ? error.Message : error.Message + " " + error.Remediation;
    }

    private static string Locate(WorkflowStep step) =>
        step.Line > 0 ? $"workflow.toml:{step.Line}" : "workflow.toml";
}
