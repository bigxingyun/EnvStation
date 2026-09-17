using System.Collections.Immutable;
using EnvStation.Abstractions.Serialization;
using EnvStation.Core.Diagnostics;

namespace EnvStation.App.Mvvm;

/// <summary>
/// 快照时间线的视图模型。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独一个视图模型而不是页面里直接 <c>await</c></b>：上一版在页面构建函数里
/// 发异步调用并直接写控件，于是"读取中…"四个字既表示加载、也表示出错、也表示内核挂了。
/// 走 <see cref="ViewModelBase"/> 之后，五态由基类兜住，页面只渲染。
/// </para>
/// <para>
/// <b>排序在视图模型里做</b>：快照索引存储时按时间正序，而用户要看的是"最近发生了什么"。
/// 让每个渲染点各自 <c>Reverse()</c> 一次，迟早有一处忘掉。
/// </para>
/// </remarks>
internal sealed class SnapshotViewModel : ViewModelBase
{
    private PageState<ImmutableArray<SnapshotIndexEntry>> _state =
        PageState<ImmutableArray<SnapshotIndexEntry>>.Idle;

    internal SnapshotViewModel(IKernelService kernel)
        : base(kernel)
    {
    }

    /// <summary>当前状态。</summary>
    internal PageState<ImmutableArray<SnapshotIndexEntry>> State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>快照（<b>按时间倒序</b>，最新的在前）。</summary>
    internal ImmutableArray<SnapshotIndexEntry> Snapshots => _state.Data.IsDefault ? [] : _state.Data;

    /// <summary>基线快照数量（用户可回退的明确锚点）。</summary>
    internal int BaselineCount => Snapshots.Count(static s => s.IsBaseline);

    /// <summary>
    /// 读取快照索引。
    /// </summary>
    /// <remarks>
    /// 读索引而不是读全部快照内容：索引里已经有时间、触发原因、大小与内容哈希，
    /// 足够画时间线；把每个快照的内容都反序列化一遍只为显示一行字，是纯粹的浪费
    /// （快照内容包含全部环境变量，几十个快照就是几十次注册表镜像的解析）。
    /// </remarks>
    internal Task LoadAsync() => LoadAsync<ImmutableArray<SnapshotIndexEntry>>(
        async token =>
        {
            var list = await Kernel.ListSnapshotsAsync(token).ConfigureAwait(true);
            return [.. list.OrderByDescending(static s => s.CreatedAt)];
        },
        state => State = state);
}
