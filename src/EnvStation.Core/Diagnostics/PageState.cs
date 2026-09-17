namespace EnvStation.Core.Diagnostics;

/// <summary>
/// 页面（或任何异步加载单元）的状态。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这个枚举</b>：上一版界面没有状态层，加载态只能用一句「读取中…」塞进卡片，
/// 于是「正在加载」「加载完了但没数据」「出错了」「内核根本没起来」四种情况在屏幕上长得一模一样。
/// 实测后果之一：内核初始化失败时，概览页会<b>永久停在「读取中…」</b>，两个按钮点了都没反应
/// （见《重构与优化方案.md》4.2 节）。
/// </para>
/// <para>
/// 把这五态变成显式模型之后，「有失败却显示成功」这类问题就从"靠人记得处理"变成"类型系统要求你处理"。
/// </para>
/// </remarks>
public enum PageStateKind
{
    /// <summary>尚未开始加载。</summary>
    Idle = 0,

    /// <summary>正在加载。</summary>
    Loading = 1,

    /// <summary>加载完成，但结果为空（空态：必须给出一个主操作）。</summary>
    Empty = 2,

    /// <summary>加载完成且有数据。</summary>
    Ready = 3,

    /// <summary>加载失败（必须带可读原因与处置，不得只写一行灰字）。</summary>
    Error = 4,

    /// <summary>部分成功：拿到了数据，但其中若干项有问题。</summary>
    Partial = 5,
}

/// <summary>
/// 携带数据与错误的不可变状态快照。
/// </summary>
/// <typeparam name="T">数据载荷类型（无载荷时用 <see cref="bool"/> 占位）。</typeparam>
/// <remarks>
/// 刻意做成不可变记录：界面只渲染快照，不做"读一个可变字段再自己拼状态"。
/// 变更时整体替换，差量更新交给视图层比对，避免"一半新一半旧"的中间态被画出来。
/// </remarks>
public sealed record PageState<T>
{
    private PageState(PageStateKind kind, T? data, string? errorCode, string? errorMessage, string? remedy)
    {
        Kind = kind;
        Data = data;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Remedy = remedy;
    }

    /// <summary>当前状态。</summary>
    public PageStateKind Kind { get; }

    /// <summary>数据载荷；<see cref="PageStateKind.Ready"/> 与 <see cref="PageStateKind.Partial"/> 下有值。</summary>
    public T? Data { get; }

    /// <summary>错误码（等宽展示用，可为空）。</summary>
    public string? ErrorCode { get; }

    /// <summary>失败原因（一句话事实，可为空）。</summary>
    public string? ErrorMessage { get; }

    /// <summary>失败后的处置建议（可为空；有则界面必须给出对应操作）。</summary>
    public string? Remedy { get; }

    /// <summary>尚未加载。</summary>
    public static PageState<T> Idle { get; } = new(PageStateKind.Idle, default, null, null, null);

    /// <summary>加载中。</summary>
    public static PageState<T> Loading { get; } = new(PageStateKind.Loading, default, null, null, null);

    /// <summary>加载完成但为空。</summary>
    public static PageState<T> Empty { get; } = new(PageStateKind.Empty, default, null, null, null);

    /// <summary>加载成功。</summary>
    public static PageState<T> Ready(T data) => new(PageStateKind.Ready, data, null, null, null);

    /// <summary>部分成功（有数据，同时有需要用户处理的项）。</summary>
    public static PageState<T> Partial(T data, string message) => new(PageStateKind.Partial, data, null, message, null);

    /// <summary>加载失败。</summary>
    public static PageState<T> Error(string message, string? errorCode = null, string? remedy = null) =>
        new(PageStateKind.Error, default, errorCode, message, remedy);

    /// <summary>是否处于终态（非 Idle / Loading）。</summary>
    public bool IsSettled => Kind is not (PageStateKind.Idle or PageStateKind.Loading);

    /// <summary>是否拿到可用数据。</summary>
    public bool HasData => Kind is PageStateKind.Ready or PageStateKind.Partial;

    /// <summary>是否需要用户关注（出错或部分成功）。</summary>
    public bool NeedsAttention => Kind is PageStateKind.Error or PageStateKind.Partial;
}
