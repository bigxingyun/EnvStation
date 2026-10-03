using System.Collections.Immutable;
using System.Text;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Packages;
using EnvStation.Abstractions.Transactions;

namespace EnvStation.Core.Diagnostics;

/// <summary>预演一步的结果。</summary>
public sealed record RemediationStepPreview(
    RemedyStep Step,
    ActionResult Result,
    IReadOnlyDictionary<string, ScriptValue> Arguments);

/// <summary>一次修复的预演包：确认框与真正执行必须共用这份数据（纪律 X-3）。</summary>
public sealed record RemediationPreview(
    RemediationPlan Plan,
    RiskLevel Risk,
    string Title,
    string Impact,
    string DiffText,
    ImmutableArray<string> Granted,
    ImmutableArray<string> AuthorizedRoots,
    ImmutableArray<RemediationStepPreview> StepPreviews)
{
    public bool Succeeded => StepPreviews.All(static s => s.Result.Success);
}

/// <summary>执行结果。</summary>
public sealed record RemediationApplyOutcome(
    bool Applied,
    bool Cancelled,
    string Message,
    ImmutableArray<ActionResult> StepResults)
{
    public bool Succeeded => Applied && !Cancelled && StepResults.Length > 0 && StepResults.All(static r => r.Success);

    public static RemediationApplyOutcome Cancel(string message) =>
        new(false, true, message, []);

    public static RemediationApplyOutcome PreviewFailed(string message) =>
        new(false, false, message, []);
}

/// <summary>把 RemedyArgument 转成动作可接受的 ScriptValue。</summary>
public static class RemedyArgumentMapper
{
    public static Dictionary<string, ScriptValue> ToScriptValues(ImmutableArray<RemedyArgument> arguments)
    {
        var map = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
        if (arguments.IsDefaultOrEmpty)
        {
            return map;
        }

        foreach (var argument in arguments)
        {
            if (argument.Flag is { } flag)
            {
                map[argument.Name] = new ScriptBoolean(flag);
            }
            else if (argument.Number is { } number)
            {
                map[argument.Name] = new ScriptInteger(number);
            }
            else
            {
                map[argument.Name] = new ScriptString(argument.Text ?? string.Empty);
            }
        }

        return map;
    }
}

/// <summary>
/// 修复编排：预演 →（外部确认）→ 执行。不依赖 WinUI。
/// </summary>
public sealed class RemediationApplier
{
    public static string DefaultAuthorizedRoot { get; } = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "EnvStation");

    private readonly Func<string, Dictionary<string, ScriptValue>?, bool, IReadOnlyCollection<string>, IReadOnlyCollection<string>, CancellationToken, Task<ActionResult>> _run;
    private readonly Func<string, ActionDescriptor?> _resolve;

    public RemediationApplier(
        Func<string, Dictionary<string, ScriptValue>?, bool, IReadOnlyCollection<string>, IReadOnlyCollection<string>, CancellationToken, Task<ActionResult>> run,
        Func<string, ActionDescriptor?> resolve)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    public ImmutableArray<string> CollectGranted(RemediationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            var descriptor = _resolve(step.ActionId);
            if (descriptor is null)
            {
                continue;
            }

            set.Add(descriptor.CapabilityId);

            var args = RemedyArgumentMapper.ToScriptValues(step.Arguments);
            var needsMachine = args.TryGetValue("scope", out var scopeValue)
                && scopeValue is ScriptString scopeText
                && string.Equals(scopeText.Value, "machine", StringComparison.OrdinalIgnoreCase);

            needsMachine |= args.TryGetValue("allow_machine", out var allow)
                && allow is ScriptBoolean { Value: true };

            if (needsMachine && !descriptor.ConditionalCapabilities.IsDefaultOrEmpty)
            {
                foreach (var extra in descriptor.ConditionalCapabilities)
                {
                    set.Add(extra);
                }
            }
        }

        return [.. set.Order(StringComparer.Ordinal)];
    }

    public async Task<RemediationPreview> PreviewAsync(
        RemediationPlan plan,
        RiskLevel risk,
        string title,
        string impact,
        IReadOnlyCollection<string>? authorizedRoots = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (!plan.HasSteps)
        {
            return new RemediationPreview(
                plan, risk, title, impact, "没有可执行的修复步骤。",
                [], [.. (authorizedRoots ?? [DefaultAuthorizedRoot])], []);
        }

        var granted = CollectGranted(plan);
        var roots = ImmutableArray.CreateRange(authorizedRoots ?? [DefaultAuthorizedRoot]);
        var previews = ImmutableArray.CreateBuilder<RemediationStepPreview>(plan.Steps.Length);

        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptor = _resolve(step.ActionId);
            var args = RemedyArgumentMapper.ToScriptValues(step.Arguments);

            ActionResult result;
            if (descriptor is not null && HasDryRunParameter(descriptor))
            {
                args["dry_run"] = new ScriptBoolean(true);
                result = await _run(step.ActionId, args, false, granted, roots, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                result = ActionResult.Ok(
                    "将执行：" + step.Label,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["dry_run"] = "synthetic",
                        ["summary"] = step.Label,
                    });
            }

            previews.Add(new RemediationStepPreview(step, result, args));
        }

        var steps = previews.ToImmutable();
        return new RemediationPreview(
            plan, risk, title, impact, BuildDiffText(plan, steps), granted, roots, steps);
    }

    public async Task<RemediationApplyOutcome> ApplyAsync(
        RemediationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (!preview.Succeeded)
        {
            var failed = preview.StepPreviews.FirstOrDefault(static s => !s.Result.Success);
            return RemediationApplyOutcome.PreviewFailed(
                failed?.Result.Message ?? "预演未通过，未执行任何修改。");
        }

        var results = ImmutableArray.CreateBuilder<ActionResult>(preview.StepPreviews.Length);

        foreach (var stepPreview in preview.StepPreviews)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var args = new Dictionary<string, ScriptValue>(stepPreview.Arguments, StringComparer.Ordinal);
            args["dry_run"] = new ScriptBoolean(false);

            var result = await _run(
                    stepPreview.Step.ActionId,
                    args,
                    false,
                    preview.Granted,
                    preview.AuthorizedRoots,
                    cancellationToken)
                .ConfigureAwait(false);

            results.Add(result);
            if (!result.Success)
            {
                return new RemediationApplyOutcome(true, false, result.Message, results.ToImmutable());
            }
        }

        var all = results.ToImmutable();
        var summary = all.Length == 1
            ? all[0].Message
            : "已完成 " + all.Length + " 步：" + string.Join("；", all.Select(static r => r.Message));

        return new RemediationApplyOutcome(true, false, summary, all);
    }

    public async Task<RemediationApplyOutcome> RunAsync(
        RemediationPlan plan,
        RiskLevel risk,
        string title,
        string impact,
        Func<RemediationPreview, CancellationToken, Task<bool>> confirm,
        IReadOnlyCollection<string>? authorizedRoots = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirm);

        var preview = await PreviewAsync(plan, risk, title, impact, authorizedRoots, cancellationToken)
            .ConfigureAwait(false);

        if (!preview.Succeeded)
        {
            return RemediationApplyOutcome.PreviewFailed(
                preview.StepPreviews.FirstOrDefault(static s => !s.Result.Success)?.Result.Message
                ?? "预演未通过。");
        }

        if (!await confirm(preview, cancellationToken).ConfigureAwait(false))
        {
            return RemediationApplyOutcome.Cancel("已取消，未修改本机环境。");
        }

        return await ApplyAsync(preview, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasDryRunParameter(ActionDescriptor descriptor) =>
        !descriptor.Parameters.IsDefaultOrEmpty
        && descriptor.Parameters.Any(static p => string.Equals(p.Name, "dry_run", StringComparison.Ordinal));

    private static string BuildDiffText(RemediationPlan plan, ImmutableArray<RemediationStepPreview> steps)
    {
        var sb = new StringBuilder();
        if (plan.Summary.Length > 0)
        {
            sb.AppendLine(plan.Summary);
            sb.AppendLine();
        }

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            sb.Append(i + 1).Append(". ").AppendLine(step.Step.Label);

            if (step.Result.Outputs.TryGetValue("would_remove", out var wouldRemove) && wouldRemove.Length > 0)
            {
                foreach (var part in wouldRemove.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.Append("   - ").AppendLine(part);
                }
            }
            else if (step.Result.Outputs.TryGetValue("changes", out var changes) && changes.Length > 0)
            {
                foreach (var part in changes.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.Append("   - ").AppendLine(part);
                }
            }
            else if (step.Result.Outputs.TryGetValue("summary", out var summary) && summary.Length > 0)
            {
                sb.Append("   ").AppendLine(summary);
            }
            else if (step.Result.Message.Length > 0)
            {
                sb.Append("   ").AppendLine(step.Result.Message);
            }
        }

        return sb.ToString().TrimEnd();
    }
}
