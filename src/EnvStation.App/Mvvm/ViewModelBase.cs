using EnvStation.Core.Diagnostics;

namespace EnvStation.App.Mvvm;

/// <summary>
/// 页面视图模型基类：持有状态、取消令牌与进度，页面只负责渲染。
/// </summary>
/// <remarks>
/// <para>
/// <b>它解决的具体问题</b>：上一版的页面构建函数一边造控件、一边发异步调用、一边把结果写进控件，
/// 于是「加载中」「加载完但没数据」「出错」「内核根本没起来」在屏幕上长得一模一样，
/// 最糟的一例是内核初始化失败后概览页<b>永久停在「读取中…」</b>（重构方案 4.2 节）。
/// </para>
/// <para>
/// <b>用结构而不是纪律来防</b>：加载必须走 <see cref="LoadAsync"/>，它在任何路径上都会落到
/// 一个终态（就绪 / 空 / 出错 / 部分成功），并且<b>先检查内核是否可用</b>。
/// 想写出"卡在加载中"反而需要刻意绕开基类。
/// </para>
/// <para>
/// 取消也是一样：基类持有 <see cref="CancellationTokenSource"/>，页面离开时调 <see cref="Cancel"/>。
/// 上一版一路都有 <c>CancellationToken</c> 参数，而界面里<b>一个 <c>CancellationTokenSource</c> 都没有</b>，
/// 于是"5 秒测量"能被连点两次、跑出两个帧回调抢同一个结果框。
/// </para>
/// </remarks>
internal abstract class ViewModelBase : ObservableObject, IDisposable
{
    private readonly IKernelService _kernel;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <param name="kernel">内核访问入口。</param>
    protected ViewModelBase(IKernelService kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _kernel = kernel;
    }

    /// <summary>内核访问入口（子类用它发起只读查询）。</summary>
    protected IKernelService Kernel => _kernel;

    /// <summary>当前是否正在加载（供按钮禁用与进度显示）。</summary>
    public bool IsLoading { get; private set; }

    /// <summary>最近一次的加载耗时（毫秒）；未加载过为 null。</summary>
    /// <remarks>界面用它把"慢"变成一个可核对的事实，而不是靠感觉。</remarks>
    public double? LastLoadMilliseconds { get; private set; }

    /// <summary>
    /// 按状态类型取一个安全的只读载荷；不在就绪态时返回 null。
    /// </summary>
    protected static T? DataOf<T>(PageState<T> state) => state.HasData ? state.Data : default;

    /// <summary>
    /// 执行一次加载，并把结果落到某个终态。
    /// </summary>
    /// <param name="work">实际的加载逻辑。</param>
    /// <param name="apply">把结果写入本视图模型状态的委托（通常是一句 <c>State = ...</c>）。</param>
    /// <param name="treatEmptyAsEmpty">结果为空时是否落到空态（默认 true）。</param>
    /// <remarks>
    /// 三条保证：
    /// <list type="number">
    /// <item>内核不可用时立刻落到错误态并带上原因，<b>不会去跑 work</b>。</item>
    /// <item>任何异常都被收敛成错误态（错误码 + 一句话事实 + 处置），不让异常穿透到未处理异常处理器。</item>
    /// <item>无论成功失败，<see cref="IsLoading"/> 都会回到 false——不会留下假的加载态。</item>
    /// </list>
    /// </remarks>
    protected async Task LoadAsync<T>(
        Func<CancellationToken, Task<T>> work,
        Action<PageState<T>> apply,
        bool treatEmptyAsEmpty = true)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(apply);

        if (!_kernel.IsAvailable)
        {
            apply(PageState<T>.Error(
                _kernel.UnavailableReason ?? "内核不可用。",
                "E_KERNEL_UNAVAILABLE",
                "重启应用；若仍然失败，导出诊断包。"));
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsLoading = true;
        Raise(nameof(IsLoading));
        var started = Environment.TickCount64;

        try
        {
            var data = await work(token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                // 被取消不是失败，也不算完成：保持当前状态不动，避免把"用户已经翻页"渲染成"出错了"。
                return;
            }

            var empty = treatEmptyAsEmpty && IsEmpty(data);
            apply(empty ? PageState<T>.Empty : PageState<T>.Ready(data));
        }
        catch (OperationCanceledException)
        {
            // 同上：取消不是错误。
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            apply(PageState<T>.Error(ex.Message, null, "重试；若反复失败，查看日志。"));
        }
        finally
        {
            LastLoadMilliseconds = Environment.TickCount64 - started;
            IsLoading = false;
            Raise(nameof(IsLoading));
            Raise(nameof(LastLoadMilliseconds));
        }
    }

    /// <summary>
    /// 取消正在进行的加载。
    /// </summary>
    /// <remarks>页面被切走或窗口关闭时调用。幂等。</remarks>
    public void Cancel()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过：取消是幂等操作，不必让调用方处理这个竞态。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>判断一次加载结果是否算"空"。</summary>
    /// <remarks>
    /// 默认只认 null 与长度为 0 的集合；字符串为空<b>不算</b>空态——本产品里空字符串
    /// 常是"该项没有值"的正常结果（例如未检测到版本号），把它当空态会让整页变成空态。
    /// </remarks>
    private static bool IsEmpty<T>(T data) => data switch
    {
        null => true,
        System.Collections.ICollection collection => collection.Count == 0,
        string => false,
        _ => false,
    };
}
