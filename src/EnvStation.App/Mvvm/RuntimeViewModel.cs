using EnvStation.Core.Diagnostics;

namespace EnvStation.App.Mvvm;

/// <summary>一个运行时的检测结果。</summary>
/// <param name="Entry">清单条目。</param>
/// <param name="Found">本机是否检出。</param>
/// <param name="Version">检出的版本；未检出为空。</param>
/// <param name="Path">检出的可执行文件路径；未检出为空。</param>
/// <param name="Source">来源（PATH / 主目录变量 / 常见安装位置）；未检出为空。</param>
/// <param name="Message">检测动作返回的一句话。</param>
/// <param name="CandidateCount">候选数量（未命中时用来说明"找到了但版本不符"）。</param>
/// <param name="Succeeded">检测动作本身是否成功返回。</param>
internal sealed record RuntimeStatus(
    RuntimeEntry Entry,
    bool Found,
    string Version,
    string Path,
    string Source,
    string Message,
    int CandidateCount,
    bool Succeeded)
{
    /// <summary>一行摘要（表格里的"已装"列）。</summary>
    internal string VersionText => Found ? (Version.Length > 0 ? Version : "已检测到") : "未检测到";

    /// <summary>状态语气。</summary>
    internal ToneStyle Tone => !Succeeded
        ? StatusTone.Error
        : Found ? StatusTone.Success : StatusTone.Neutral;

    /// <summary>
    /// 是否需要用户关注。
    /// </summary>
    /// <remarks>
    /// "未检测到"本身不是问题——用户没装 Go 是完全正常的。
    /// 这里刻意返回 false：把 16 个运行时里"没装的都报成问题"会制造一堆噪音，
    /// 而真正该提醒的是"检测本身失败了"（读不到注册表之类）。
    /// </remarks>
    internal bool NeedsAttention => !Succeeded;
}

/// <summary>
/// 运行时页的视图模型：分批检测清单里的每个运行时。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么分批而不是一次全检</b>：16 个运行时逐个调检测动作，每项都要读写注册表与文件系统。
/// 一次性检完再渲染，用户要盯着一个空页面等好几秒；分批则是"每检出一个就出现一行"，
/// 用户能立刻开始看，也能立刻发现"这不是我要的那个"。
/// </para>
/// <para>
/// <b>单项失败不中断</b>：某个运行时的检测抛异常时记下它继续检下一个——
/// 与体检编排同一原则，一次看全比半路停下有用。
/// </para>
/// </remarks>
internal sealed class RuntimeViewModel : ViewModelBase
{
    private readonly List<RuntimeStatus> _statuses = [];
    private System.Collections.Immutable.ImmutableArray<PackageManagerAvailability> _packageManagers = [];
    private PageState<IReadOnlyList<RuntimeStatus>> _state = PageState<IReadOnlyList<RuntimeStatus>>.Idle;
    private int _scanned;
    private int _total;

    internal RuntimeViewModel(IKernelService kernel)
        : base(kernel)
    {
    }

    /// <summary>当前状态。</summary>
    internal PageState<IReadOnlyList<RuntimeStatus>> State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>已检出的运行时（按清单顺序）。</summary>
    internal IReadOnlyList<RuntimeStatus> Statuses => _statuses;

    /// <summary>已检数量。</summary>
    internal int Scanned => _scanned;

    /// <summary>总数。</summary>
    internal int Total => _total;

    /// <summary>本机包管理器探测结果（供安装流程复用，避免再探一次）。</summary>
    internal System.Collections.Immutable.ImmutableArray<PackageManagerAvailability> PackageManagers => _packageManagers;

    /// <summary>检出的运行时数量。</summary>
    internal int FoundCount => _statuses.Count(static s => s.Found);

    /// <summary>本机包管理器状况（一行中文事实）。</summary>
    /// <remarks>
    /// 探测走 <see cref="PackageManagerProbe"/>，它复用安装动作自己的解析器——
    /// 判定与执行必须是同一套路径规则，否则会出现「检查说可用、执行说找不到」。
    /// </remarks>
    internal string PackageManagerSummary { get; private set; } = string.Empty;

    /// <summary>实际会被使用的包管理器名；无可用时为空。</summary>
    /// <remarks>
    /// 同时装了 winget 与 choco 时，「由它负责校验」里的"它"指谁是不清楚的。
    /// 判定层已按优先级选定第一个可用的，这里把它说出来——
    /// 用户需要知道"这次到底会调哪个工具"，而不只是"有工具可用"。
    /// </remarks>
    internal string SelectedPackageManager { get; private set; } = string.Empty;

    /// <summary>是否能通过包管理器安装（决定界面上怎么措辞）。</summary>
    internal bool CanInstallViaPackageManager { get; private set; }

    /// <summary>
    /// 逐个检测全部运行时。
    /// </summary>
    /// <param name="onItem">每检出一个就回调一次（页面据此增量追加一行）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal async Task ScanAsync(Action<RuntimeStatus>? onItem = null, CancellationToken cancellationToken = default)
    {
        _statuses.Clear();
        _scanned = 0;
        _total = RuntimeCatalogGroups.AllEntries.Length;

        if (!Kernel.IsAvailable)
        {
            State = PageState<IReadOnlyList<RuntimeStatus>>.Error(
                Kernel.UnavailableReason ?? "内核不可用。",
                "E_KERNEL_UNAVAILABLE",
                "重启应用；若仍然失败，导出诊断包。");
            return;
        }

        State = PageState<IReadOnlyList<RuntimeStatus>>.Loading;

        // 先探包管理器：它决定"能不能装"，是用户在看完检测结果之后的第一个问题。
        var managers = PackageManagerProbe.ProbeAll();
        _packageManagers = managers;
        PackageManagerSummary = PackageManagerProbe.Describe(managers);
        CanInstallViaPackageManager = managers.Any(static m => m.IsAvailable);
        SelectedPackageManager = managers.FirstOrDefault(static m => m.IsAvailable)?.Manager ?? string.Empty;
        Raise(nameof(PackageManagerSummary));
        Raise(nameof(SelectedPackageManager));
        Raise(nameof(CanInstallViaPackageManager));

        foreach (var entry in RuntimeCatalogGroups.AllEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RuntimeStatus status;
            try
            {
                var arguments = new Dictionary<string, Abstractions.Packages.ScriptValue>(StringComparer.Ordinal)
                {
                    ["kind"] = new Abstractions.Packages.ScriptString(entry.Kind),
                };

                var result = await Kernel
                    .RunReadOnlyAsync("envstation.detect.runtime", arguments, cancellationToken)
                    .ConfigureAwait(true);

                var outputs = result.Outputs;
                status = new RuntimeStatus(
                    entry,
                    Found: Get(outputs, "found") == "true",
                    Version: Get(outputs, "version"),
                    Path: Get(outputs, "path"),
                    Source: Get(outputs, "source"),
                    Message: result.Message,
                    CandidateCount: int.TryParse(Get(outputs, "candidate_count"), out var n) ? n : 0,
                    Succeeded: result.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                status = new RuntimeStatus(entry, false, string.Empty, string.Empty, string.Empty,
                    ex.Message, 0, Succeeded: false);
            }

            _statuses.Add(status);
            _scanned++;
            onItem?.Invoke(status);
        }

        // 一个都没检出时是空态而不是错误——"这台机器上什么都没装"是合法结论。
        State = _statuses.Any(static s => s.Found)
            ? PageState<IReadOnlyList<RuntimeStatus>>.Ready(_statuses)
            : PageState<IReadOnlyList<RuntimeStatus>>.Empty;
    }

    private static string Get(IReadOnlyDictionary<string, string> outputs, string key) =>
        outputs.TryGetValue(key, out var value) ? value : string.Empty;
}
