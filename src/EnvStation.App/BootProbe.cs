using System.Diagnostics;
using System.Globalization;
using System.Text;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EnvStation.App;

/// <summary>
/// 启动探针：给 M0-P01（性能实测）与无人值守自检提供可观测性。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要一个探针，而不是"用秒表掐一下"</b>：冷启动的终点是"窗口真的可见"，
/// 这个时刻只有应用自己知道。外部脚本只能看到"进程存在"，那会把 XAML 初始化、
/// 主题字典加载、内核建表全都漏掉——而这三件事恰恰是启动耗时的主要来源。
/// </para>
/// <para>
/// <b>开关是环境变量，默认全关</b>：不设 <c>ENVSTATION_BOOT_LOG</c> 时本类不产生任何
/// 文件写入与计时开销。性能测量本身不应该成为常态开销。
/// </para>
/// <list type="bullet">
///   <item><c>ENVSTATION_BOOT_LOG</c>：日志文件路径。设置后才开始记录。</item>
///   <item><c>ENVSTATION_BOOT_EXIT_MS</c>：启动后多少毫秒自动退出（0 或不设 = 不退出）。
///         用于无人值守自检：不需要人工关窗口，也不会留下挂着的进程。</item>
/// </list>
/// </remarks>
internal static class BootProbe
{
    private static readonly string? Path = System.Environment.GetEnvironmentVariable("ENVSTATION_BOOT_LOG");

    /// <summary>是否启用记录。</summary>
    internal static bool Enabled => !string.IsNullOrWhiteSpace(Path);

    /// <summary>写入一行记录；未启用时为空操作。</summary>
    /// <remarks>
    /// 文件带 UTF-8 BOM：不带 BOM 时 Windows PowerShell 5.1 的 <c>Get-Content</c>
    /// 会按 ANSI 读，中文全部变成乱码——日志是给人看的，读出来是乱码等于没记。
    /// </remarks>
    internal static void Write(string message)
    {
        if (Path is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{System.Environment.ProcessId}\t{App.StartupClock.ElapsedMilliseconds,6} ms\t{message}\n");

            var isNew = !File.Exists(path);
            File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: isNew));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 探针失败绝不影响主流程——它只是观测手段。
        }
    }

    /// <summary>记录当前工作集与私有内存（M0-P01 的 60MB / 200MB 两个阈值看这两个数）。</summary>
    internal static void WriteMemory(string stage)
    {
        if (!Enabled)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();

        Write(string.Create(
            CultureInfo.InvariantCulture,
            $"{stage}：工作集 {process.WorkingSet64 / 1048576.0:F1} MB，" +
            $"私有内存 {process.PrivateMemorySize64 / 1048576.0:F1} MB，" +
            $"托管堆 {GC.GetTotalMemory(false) / 1048576.0:F1} MB"));
    }

    /// <summary>
    /// 窗口可见之后调用：可选地跑一遍自检脚本，然后记一条内存快照，
    /// 并按 <c>ENVSTATION_BOOT_EXIT_MS</c> 决定是否自动退出。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内存快照刻意分成两个时刻记录：刚可见时（反映「能不能用」）与稳定后（反映「常态占用」）。
    /// 只记一个数容易被误读——XAML 首帧之后会有一轮资源释放，两个数往往差一倍。
    /// </para>
    /// <para>
    /// 自检脚本（<c>ENVSTATION_BOOT_SELFCHECK=1</c>）逐页导航、切换主题并计时，
    /// 这是 M0-P01 里「页面切换 ≤100ms」「主题切换 &lt;200ms」两项的唯一数据来源：
    /// 只有应用自己知道布局什么时候真的完成了。
    /// </para>
    /// </remarks>
    internal static void OnWindowVisible(Window window)
    {
        WriteMemory("窗口可见");

        var exitText = System.Environment.GetEnvironmentVariable("ENVSTATION_BOOT_EXIT_MS");
        var delay = int.TryParse(exitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        if (!Enabled)
        {
            return;
        }

        if (window is MainWindow main)
        {
            _ = RunSelfCheckAsync(main);
        }

        if (delay <= 0)
        {
            return;
        }

        // 必须把计时器存进静态字段。
        // 为什么：DispatcherQueueTimer 是 WinRT 对象，若只被局部变量引用，
        // 一次 GC 就能把它回收掉，Tick 从此再也不触发——表现为"设了 60 秒自动退出，
        // 结果 90 秒后进程还活着，而且日志里什么都没有"（首次 P01 实测就是这样，
        // 3 秒的短延时因为没赶上 GC 所以看不出问题）。
        ExitTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        ExitTimer.Interval = TimeSpan.FromMilliseconds(delay);
        ExitTimer.IsRepeating = false;
        ExitTimer.Tick += (_, _) =>
        {
            WriteMemory("稳定后");
            Write("自检结束：主动退出");
            window.Close();
        };

        ExitTimer.Start();
    }

    /// <summary>跑一遍页面切换、主题切换与滚动帧率计时（P01 的测量项）。</summary>
    private static async Task RunSelfCheckAsync(MainWindow main)
    {
        if (System.Environment.GetEnvironmentVariable("ENVSTATION_BOOT_SELFCHECK") != "1")
        {
            return;
        }

        foreach (var tag in (string[])["overview", "doctor", "packages", "history", "actions", "settings", "perf"])
        {
            var (clickMs, totalMs) = await main.MeasureNavigateAsync(tag).ConfigureAwait(true);
            Write($"页面切换 -> {tag}：点击响应 {clickMs:F1} ms，页面落地 {totalMs:F1} ms");
        }

        Write($"主题切换 -> 深色：{await main.MeasureThemeToggleAsync().ConfigureAwait(true):F1} ms");
        Write($"主题切换 -> 浅色：{await main.MeasureThemeToggleAsync().ConfigureAwait(true):F1} ms");

        main.MeasureNavigationBuild();

        // 帧率测量要挂在性能自检页上，所以放在最后：前面的导航已经把该页挂好了。
        Write("滚动帧率测量开始（5 秒）");
        Write(await main.MeasureFrameRateForProbeAsync().ConfigureAwait(true));

        if (System.Environment.GetEnvironmentVariable("ENVSTATION_BOOT_RENDER") == "1")
        {
            await RunRenderProbeAsync(main).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 渲染开销探针：逐个页面统计可视树规模、测量布局与首帧代价，并实测真实内容滚动时的帧间隔。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么"最长帧 24 ms、平均 60 FPS"不等于流畅</b>：那个数字来自性能自检页里一个空模板的
    /// ListView，可视树很小；而用户实际停留的页面（动作库、环境检测）可视树规模是它的几十倍。
    /// 实测对象必须是<b>用户真正看的那棵树</b>，否则测出来的只是一个恰好也用 WinUI 的样板。
    /// </para>
    /// <para>
    /// <c>ENVSTATION_BOOT_RENDER=1</c> 才启用：这一步要跑十几秒，不该成为常态开销。
    /// </para>
    /// </remarks>
    private static async Task RunRenderProbeAsync(MainWindow main)
    {
        Write("── 渲染开销探针开始 ──");

        // 可视树规模：切页贵不贵，第一个要看的就是"这棵树有多少元素"。
        // 连测三轮：第一轮含模板首次实例化的冷路径，后两轮才是常态。
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var tag in (string[])["overview", "doctor", "packages", "history", "actions", "settings", "perf"])
            {
                await main.MeasureNavigateAsync(tag).ConfigureAwait(true);
                Write(main.DescribeVisualTree(tag));
            }
        }

        // 尺寸连续变化：拖窗口边缘时的体感来源。
        await main.MeasureNavigateAsync("overview").ConfigureAwait(true);
        Write(await main.MeasureResizeStormAsync().ConfigureAwait(true));

        // 逐页"导航 + 真实内容滚动"。
        foreach (var tag in (string[])["overview", "doctor", "actions", "settings", "perf"])
        {
            Write(await main.MeasureNavigationThenScrollAsync(tag).ConfigureAwait(true));
        }

        // 真实滚轮输入路径：用户的手走的就是这条。
        foreach (var tag in (string[])["actions", "perf", "packages"])
        {
            Write(await main.MeasureMouseWheelScrollAsync(tag).ConfigureAwait(true));
        }

        // 点击 → 画面真的换过去：用户感知的那一段时间。
        // 第一轮先把页面逐出缓存，量的是"第一次进入"；第二轮量"再回来"。
        foreach (var tag in (string[])["actions", "packages", "doctor", "settings"])
        {
            main.EvictCurrentPage();
            Write(await main.MeasureClickToPaintAsync(tag).ConfigureAwait(true));
        }

        foreach (var tag in (string[])["actions", "packages", "doctor", "settings"])
        {
            Write(await main.MeasureClickToPaintAsync(tag).ConfigureAwait(true));
        }

        Write("── 渲染开销探针结束 ──");
    }

    /// <summary>自动退出计时器（静态持有，防止被 GC 回收导致 Tick 失效）。</summary>
    private static DispatcherQueueTimer? ExitTimer;
}
