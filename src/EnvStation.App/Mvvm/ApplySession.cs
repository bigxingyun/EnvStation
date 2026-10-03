using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Transactions;
using EnvStation.App.Controls;
using EnvStation.Core.Diagnostics;
using Microsoft.UI.Xaml;

namespace EnvStation.App.Mvvm;

/// <summary>
/// 界面侧修复会话：把 RemediationApplier 接到确认框与内核服务。
/// 所有写路径必须经此会话，禁止页面私自调 IKernelService.RunAsync。
/// </summary>
internal sealed class ApplySession
{
    private readonly IKernelService _kernel;
    private readonly RemediationApplier _applier;

    internal ApplySession(IKernelService kernel)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _applier = new RemediationApplier(
            (actionId, arguments, isDryRun, granted, roots, token) =>
                _kernel.RunAsync(actionId, arguments, isDryRun, granted, roots, token),
            actionId =>
            {
                foreach (var descriptor in _kernel.Descriptors)
                {
                    if (string.Equals(descriptor.ActionId, actionId, StringComparison.Ordinal))
                    {
                        return descriptor;
                    }
                }

                return null;
            });
    }

    internal Task<RemediationApplyOutcome> ApplyRemedyAsync(
        RemedyItem item,
        XamlRoot xamlRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(xamlRoot);

        if (!item.ShouldOfferFix)
        {
            return Task.FromResult(RemediationApplyOutcome.PreviewFailed(
                "此项不可自动修复，或风险过高，环境站不代改。"));
        }

        return ApplyPlanAsync(
            item.Plan,
            item.Risk,
            item.Title,
            item.Impact,
            xamlRoot,
            cancellationToken);
    }

    internal Task<RemediationApplyOutcome> ApplyPlanAsync(
        RemediationPlan plan,
        RiskLevel risk,
        string title,
        string impact,
        XamlRoot xamlRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (!_kernel.IsAvailable)
        {
            return Task.FromResult(RemediationApplyOutcome.PreviewFailed(
                _kernel.UnavailableReason ?? "内核不可用。"));
        }

        return _applier.RunAsync(
            plan,
            risk,
            title,
            impact,
            (preview, _) => Confirm.ConfirmAsync(
                xamlRoot,
                preview.Title,
                preview.Impact,
                preview.DiffText,
                preview.Risk,
                confirmText: "执行"),
            authorizedRoots: [RemediationApplier.DefaultAuthorizedRoot],
            cancellationToken);
    }

    internal Task<RemediationPreview> PreviewPlanAsync(
        RemediationPlan plan,
        RiskLevel risk,
        string title,
        string impact,
        CancellationToken cancellationToken = default) =>
        _applier.PreviewAsync(
            plan,
            risk,
            title,
            impact,
            [RemediationApplier.DefaultAuthorizedRoot],
            cancellationToken);

    internal Task<RemediationApplyOutcome> ExecutePreviewAsync(
        RemediationPreview preview,
        CancellationToken cancellationToken = default) =>
        _applier.ApplyAsync(preview, cancellationToken);
}