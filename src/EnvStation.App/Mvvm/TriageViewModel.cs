using EnvStation.Core.Diagnostics;

namespace EnvStation.App.Mvvm;

/// <summary>
/// 体检视图模型：首页与「待处理问题」页共用同一份结论。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么两页共用一个视图模型</b>：首页显示"我缺什么"的结论，问题页显示逐条处置。
/// 若各自跑一遍检测，两个页面会给出可能不一致的数字（同一环境下两次探测结果不同是常事：",
/// 磁盘空间在两秒内就会变），用户看到"首页说 3 项、点进去只有 2 项"就再也不信这个程序了。
/// 一份结论、两个视图，是唯一不会自相矛盾的做法。
/// </para>
/// <para>
/// 这是本项目第一个真正走 <see cref="ViewModelBase"/> 的视图模型：加载必须落到终态、
/// 取消要用得上、进度要真的报出来——三件事都由基类兜住，页面只负责画。
/// </para>
/// </remarks>
internal sealed class TriageViewModel : ViewModelBase
{
    private readonly TriagePlan _plan;
    private PageState<DiagnosticReport> _state = PageState<DiagnosticReport>.Idle;
    private int _progressDone;
    private int _progressTotal;
    private string _progressLabel = string.Empty;

    internal TriageViewModel(IKernelService kernel, TriagePlan? plan = null)
        : base(kernel)
    {
        _plan = plan ?? new TriagePlan();
    }

    /// <summary>当前状态。</summary>
    internal PageState<DiagnosticReport> State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(Report));
                Raise(nameof(HasReport));
                Raise(nameof(Summary));
                Raise(nameof(Groups));
            }
        }
    }

    /// <summary>结论；未就绪时为 null。</summary>
    internal DiagnosticReport? Report => _state.Data;

    /// <summary>是否已有结论。</summary>
    internal bool HasReport => _state.HasData && Report is not null;

    /// <summary>顶部汇总句。</summary>
    internal string Summary => Report?.Summarize() ?? "尚未检测。";

    /// <summary>按严重度分好组的待办项。</summary>
    internal RemedyGroups Groups =>
        Report is null ? RemedyGroups.From([]) : RemedyGroups.From(Report.Remedies);

    /// <summary>已完成的检测项数（进度显示用）。</summary>
    internal int ProgressDone => _progressDone;

    /// <summary>检测项总数。</summary>
    internal int ProgressTotal => _progressTotal;

    /// <summary>正在跑的那一项的名字。</summary>
    internal string ProgressLabel => _progressLabel;

    /// <summary>
    /// 跑一次体检。
    /// </summary>
    /// <remarks>
    /// 进度回调里只在值真的变化时通知界面：一次体检会回报五次，
    /// 每回报一次都无条件刷新会让"进度更新"本身成为掉帧来源。
    /// </remarks>
    internal Task RunAsync() => LoadAsync<DiagnosticReport>(
        async token =>
        {
            _progressTotal = _plan.Checks.Length;
            _progressDone = 0;
            Raise(nameof(ProgressTotal));
            Raise(nameof(ProgressDone));

            var progress = new Progress<(int Done, int Total, string Label)>(report =>
            {
                _progressDone = report.Done;
                _progressLabel = report.Label;
                Raise(nameof(ProgressDone));
                Raise(nameof(ProgressLabel));
            });

            return await _plan.RunAsync(RunCheckAsync, progress, token).ConfigureAwait(true);
        },
        state => State = state,
        treatEmptyAsEmpty: false);

    private async Task<TriageCheckResult> RunCheckAsync(TriageCheck check, CancellationToken token)
    {
        var arguments = new Dictionary<string, Abstractions.Packages.ScriptValue>(StringComparer.Ordinal);
        if (check.Scope.Length > 0)
        {
            arguments["scope"] = new Abstractions.Packages.ScriptString(check.Scope);
        }

        if (check.Arguments is { Count: > 0 } extra)
        {
            foreach (var (key, value) in extra)
            {
                arguments[key] = new Abstractions.Packages.ScriptString(value);
            }
        }

        var started = Environment.TickCount64;
        var result = await Kernel
            .RunReadOnlyAsync(check.ActionId, arguments.Count > 0 ? arguments : null, token)
            .ConfigureAwait(true);

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in result.Outputs)
        {
            outputs[key] = value;
        }

        return new TriageCheckResult(
            result.Success,
            result.Message,
            outputs,
            result.Success ? null : result.ErrorCode,
            Environment.TickCount64 - started);
    }
}
