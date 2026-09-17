using System.Collections.Immutable;
using EnvStation.Core.Diagnostics;

namespace EnvStation.App.Mvvm;

/// <summary>PATH 里的一条（分条视图的一行）。</summary>
/// <param name="Index">在所属作用域内的序号（0 起）。</param>
/// <param name="Scope">作用域（<c>user</c> / <c>machine</c>）。</param>
/// <param name="Raw">原始写法（未展开 <c>%VAR%</c>）。</param>
/// <param name="IssueText">问题说明；无问题时为空。</param>
internal sealed record PathEntryView(int Index, string Scope, string Raw, string IssueText)
{
    /// <summary>作用域的界面说法。</summary>
    internal string ScopeLabel => Scope == "machine" ? "系统" : "用户";

    /// <summary>是否健康。</summary>
    internal bool IsHealthy => IssueText.Length == 0;

    /// <summary>状态语气：健康为正常，不健康为警告。</summary>
    internal ToneStyle Tone => IsHealthy ? StatusTone.Success : StatusTone.Warning;
}

/// <summary>一个环境变量的展示形态。</summary>
/// <param name="Name">变量名。</param>
/// <param name="RawValue">原始值（未展开）。</param>
internal sealed record EnvVariableView(string Name, string RawValue)
{
    /// <summary>值的展示形态：超长时截断，避免一行撑爆布局。</summary>
    internal string DisplayValue => RawValue.Length <= 200 ? RawValue : RawValue[..200] + "…";

    /// <summary>值长度。</summary>
    internal int ValueLength => RawValue.Length;

    /// <summary>是否超过旧版编辑对话框的 2047 字符上限。</summary>
    internal bool IsLong => RawValue.Length > 2047;
}

/// <summary>
/// 环境变量页的视图模型。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么 PATH 单独走 <c>path.validate</c> 而不是自己分号切</b>：
/// 判断"这一项存不存在""是不是重复""变量有没有解析出来"需要读注册表与文件系统，
/// 而且口径必须与检测页、CLI 的 <c>doctor</c>、以及将来修复时的差量预览完全一致。
/// 自己切分号会造出第二套判据——这正是缺陷 D-44 的成因。
/// </para>
/// <para>
/// <b>PATH 条目从 <c>details</c> 解析</b>：<c>path.validate</c> 的输出里，
/// <c>details</c> 是「原始写法 —— 问题说明」按 <c> || </c> 连接的行；无问题的条目不进 details，
/// 所以条目总数以 <c>entry_count</c> 为准。两处结合才能既拿到总数又拿到问题——只看 details 会漏掉健康项。
/// </para>
/// </remarks>
internal sealed class EnvPageViewModel : ViewModelBase
{
    private PageState<bool> _state = PageState<bool>.Idle;
    private ImmutableArray<PathEntryView> _pathEntries = [];
    private ImmutableArray<EnvVariableView> _userVariables = [];
    private ImmutableArray<EnvVariableView> _machineVariables = [];

    internal EnvPageViewModel(IKernelService kernel)
        : base(kernel)
    {
    }

    /// <summary>当前状态。</summary>
    internal PageState<bool> State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>PATH 全部条目（用户级在前，系统级在后）。</summary>
    internal ImmutableArray<PathEntryView> PathEntries => _pathEntries;

    /// <summary>用户级变量。</summary>
    internal ImmutableArray<EnvVariableView> UserVariables => _userVariables;

    /// <summary>系统级变量。</summary>
    internal ImmutableArray<EnvVariableView> MachineVariables => _machineVariables;

    /// <summary>PATH 里有问题的条目数（两级合计）。</summary>
    internal int TotalProblemCount => _pathEntries.Count(static e => !e.IsHealthy);

    /// <summary>
    /// 读取环境变量与 PATH。
    /// </summary>
    internal Task LoadAsync() => LoadAsync<bool>(
        async token =>
        {
            await ReadPathAsync("user", token).ConfigureAwait(true);
            await ReadPathAsync("machine", token).ConfigureAwait(true);
            await ReadVariablesAsync("user", token).ConfigureAwait(true);
            await ReadVariablesAsync("machine", token).ConfigureAwait(true);
            return true;
        },
        state => State = state,
        treatEmptyAsEmpty: false);

    private async Task ReadPathAsync(string scope, CancellationToken token)
    {
        var arguments = new Dictionary<string, Abstractions.Packages.ScriptValue>(StringComparer.Ordinal)
        {
            ["scope"] = new Abstractions.Packages.ScriptString(scope),
        };

        var result = await Kernel
            .RunReadOnlyAsync("envstation.path.validate", arguments, token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            return;
        }

        var outputs = result.Outputs;
        var parsed = ParseEntries(Get(outputs, "entries"), scope);

        _pathEntries = scope == "user"
            ? [.. parsed]
            : [.. _pathEntries, .. parsed];
    }

    /// <summary>
    /// 解析 <c>path.validate</c> 的 <c>entries</c>：每行「序号 \t 原始写法 \t 问题」，行间以 <c>\n</c> 连接。
    /// </summary>
    /// <remarks>
    /// 解析失败的整行会被跳过而不是抛异常：这是展示层，一条读不出来不该让整页读不出来。
    /// 但会保留原始写法为空的行（它代表一个空条目，本身就是要展示的事实）。
    /// </remarks>
    private static ImmutableArray<PathEntryView> ParseEntries(string entries, string scope)
    {
        if (entries.Length == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<PathEntryView>();

        foreach (var line in entries.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length < 2)
            {
                continue;
            }

            var index = int.TryParse(fields[0], out var n) ? n : builder.Count;
            var raw = fields[1];
            var issue = fields.Length > 2 ? fields[2] : string.Empty;

            builder.Add(new PathEntryView(index, scope, raw, issue));
        }

        return builder.ToImmutable();
    }

    private async Task ReadVariablesAsync(string scope, CancellationToken token)
    {
        var arguments = new Dictionary<string, Abstractions.Packages.ScriptValue>(StringComparer.Ordinal)
        {
            ["scope"] = new Abstractions.Packages.ScriptString(scope),
        };

        var result = await Kernel
            .RunReadOnlyAsync("envstation.detect.env", arguments, token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            return;
        }

        var variables = result.Outputs
            .Where(kv => kv.Key.StartsWith(scope + ":", StringComparison.Ordinal))
            .Select(kv => new EnvVariableView(kv.Key[(scope.Length + 1)..], kv.Value))
            .OrderBy(static v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        if (scope == "user")
        {
            _userVariables = variables;
        }
        else
        {
            _machineVariables = variables;
        }
    }

    private static string Get(IReadOnlyDictionary<string, string> outputs, string key) =>
        outputs.TryGetValue(key, out var value) ? value : string.Empty;
}
