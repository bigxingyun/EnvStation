using System.Globalization;

using EnvStation.Core.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EnvStation.App;

/// <summary>
/// 主窗口的性能测量装置（partial）：帧率、滚轮、缩放风暴、可视树、点击到出帧。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独一个文件</b>：这些是 M0-P01 的测量载体，不是产品界面的一部分。
/// 它们此前和页面代码混在同一个 2048 行的文件里，读页面的人要穿过八百行探针代码才到得了目的地。
/// 拆开之后主文件回到可读规模（纪律 X-4：单文件 ≤ 800 行），而测量能力一点没少——
/// 同一个类、同一批方法，只是换了文件。
/// </para>
/// <para>
/// <b>仍然留在产品程序集里</b>：测的必须是真实视图栈，搬到独立测试程序就变成
/// "一个恰好也用 WinUI 的样板工程"，数字没有意义。它们由 <c>BootProbe</c> 的
/// 环境变量开关门控，不设环境变量时不产生任何开销。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    /// <summary>
    /// 从"点击"到"新页面第一帧画出来"之间发生了什么。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是唯一能回答"切页到底卡不卡"的测法：<c>点击响应</c>只说明处理器快不快，
    /// <c>构建耗时</c>只说明代码跑了多久；用户感知的是<b>画面什么时候换过去</b>，
    /// 而中间还有一趟布局与一次出帧。
    /// </para>
    /// <para>
    /// 同时数帧间隔：构建与布局都跑在 UI 线程上，只要它们超过 16.7 ms，
    /// 那一帧就交不出去——这正是"点了之后画面顿一下才换"的成因。
    /// </para>
    /// </remarks>
    internal async Task<string> MeasureClickToPaintAsync(string tag)
    {
        var gaps = new List<double>(120);
        var last = System.Diagnostics.Stopwatch.StartNew();
        var worst = 0.0;

        void OnRendering(object? sender, object e)
        {
            var gap = last.Elapsed.TotalMilliseconds;
            last.Restart();
            gaps.Add(gap);

            if (gap > worst)
            {
                worst = gap;
            }
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var loaded = new TaskCompletionSource();
        PageLoadedHook = loaded;

        // 先把上一次导航彻底落定：否则"点击响应"里会混进上一页还没跑完的构建。
        await WaitForPageAsync().ConfigureAwait(true);

        double clickMs;
        var loadedMs = 0.0;
        var reusedFromCache = false;

        try
        {
            Navigate(tag);
            clickMs = clock.Elapsed.TotalMilliseconds;

            // 等到新页面真的挂上可视树（Loaded 在首次布局与渲染之前触发）。
            // 缓存命中时内容没有发生替换，Loaded 不会再触发——所以必须带超时，
            // 否则测量自己会挂死在这里（第一次加缓存时就是这样，整个自检卡在第 45 秒）。
            var finished = await Task.WhenAny(loaded.Task, Task.Delay(1500)).ConfigureAwait(true);
            reusedFromCache = !ReferenceEquals(finished, loaded.Task);
            loadedMs = clock.Elapsed.TotalMilliseconds;

            // 再等几帧，把构建 + 布局 + 出帧这一段完整覆盖住。
            await Task.Delay(300).ConfigureAwait(true);
            clock.Stop();
        }
        finally
        {
            PageLoadedHook = null;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        var janky = gaps.Count(static gap => gap > 25.0);
        var paintMs = loadedMs - clickMs;

        if (reusedFromCache)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[{tag}] 复用已建页面：点击响应 {clickMs:F1} ms，无首次布局 · " +
                $"最长帧间隔 {worst:F1} ms · 卡顿 {janky} 帧 / 采样 {gaps.Count} 帧");
        }

        var layoutMs = paintMs - PageBuildMs;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{tag}] 点击 → 页面挂载 {paintMs:F1} ms · 其中造对象 {PageBuildMs:F1} + 布局出帧 {layoutMs:F1} · " +
            $"最长帧间隔 {worst:F1} ms · 卡顿 {janky} 帧 / 采样 {gaps.Count} 帧");
    }

    /// <summary>页面挂载回调；由 <c>Navigate</c> 在设置新内容时挂到页面根上。</summary>
    private static TaskCompletionSource? PageLoadedHook;

    /// <summary>最近一次页面"造对象"的耗时（不含布局与出帧）。</summary>
    private static double PageBuildMs;

    /// <summary>页面切换计时：构建 + 强制布局，单位毫秒。</summary>
    /// <remarks>
    /// 只对"构建 + 布局"计时，<b>不含 GPU 出帧</b>——出帧时刻在托管侧拿不到。
    /// 所以这个数是乐观下界，报告里必须写明；真实体感由录屏或 PresentMon 才能覆盖。
    /// </remarks>
    /// <summary>
    /// 页面切换计时：构建 + 强制布局，单位毫秒。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只对"构建 + 布局"计时，<b>不含 GPU 出帧</b>——出帧时刻在托管侧拿不到。
    /// 所以这个数是乐观下界，报告里必须写明。
    /// </para>
    /// <para>
    /// <b>这个数必须分成两段看</b>：<c>点击响应</c>是当帧真正被占住的时间（用户能感知的卡顿），
    /// <c>页面构建</c>是排在渲染之后的那一段（用户看到的是内容稍后出现，而不是点了没反应）。
    /// 只报一个合计数会把这两种完全不同的体感混为一谈——这正是第一版报告里
    /// "页面切换 30 ms"看起来不严重、实际每次都要丢两帧的原因。
    /// </para>
    /// </remarks>
    internal async Task<(double ClickMs, double TotalMs)> MeasureNavigateAsync(string tag)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Navigate(tag);
        var clickMs = clock.Elapsed.TotalMilliseconds;

        await WaitForPageAsync().ConfigureAwait(true);
        _root.UpdateLayout();
        clock.Stop();

        BootProbe.Write(
            $"页面 {tag} 点击响应 {clickMs:F1} ms + 构建 {clock.Elapsed.TotalMilliseconds - clickMs:F1} ms " +
            $"= 合计 {clock.Elapsed.TotalMilliseconds:F1} ms");
        return (clickMs, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 主题切换计时。
    /// </summary>
    /// <remarks>
    /// <b>必须把"重新显示当前页"算进去</b>：页面缓存让切页变快了，但也意味着换主题那一刻
    /// 缓存里全是旧画刷的页面、必须整册作废。用户按主题按钮时，感知到的是
    /// "窗口换色 + 当前页重新画出来"，只计前半段会把最贵的那一段漏掉。
    /// </remarks>
    internal async Task<double> MeasureThemeToggleAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CycleTheme();
        await WaitForPageAsync().ConfigureAwait(true);
        _root.UpdateLayout();
        clock.Stop();

        BootProbe.Write($"主题切换（含整壳重建 + 当前页重画 + 布局）{clock.Elapsed.TotalMilliseconds:F1} ms");
        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 产品页面清单（<b>不含</b>开发专用的性能自检页）。
    /// </summary>
    /// <remarks>
    /// 性能自检页已从产品界面下架，因此不在测量清单里；只有显式要求测量时才由
    /// <see cref="PageTags.Perf"/> 单独挂载。清单本身在 <see cref="PageTags.Product"/>——
    /// 这里只是给探针一个短名字，不再另存一份。
    /// </remarks>
    internal static IReadOnlyList<string> ProductPageTags => PageTags.Product;

    /// <summary>列出全部产品页面的构建耗时（自检脚本用）。</summary>
    internal void MeasureNavigationBuild()
    {
        foreach (var tag in ProductPageTags)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var page = BuildPageForTag(tag);
            clock.Stop();
            BootProbe.Write($"页面 {tag} 仅构建 {clock.Elapsed.TotalMilliseconds:F1} ms（未挂载）");
            GC.KeepAlive(page);
        }
    }

    /// <summary>按标签构建页面（路由的唯一实现，测量与正常导航共用一份）。</summary>
    private UIElement BuildPageForTag(string tag) => tag switch
    {
        PageTags.Doctor => BuildIssuesPage(),
        PageTags.Runtime => BuildRuntimePage(),
        PageTags.Env => BuildEnvPage(),
        PageTags.Packages => BuildPackagesPage(),
        PageTags.History => BuildHistoryPage(),
        PageTags.Actions => BuildActionsPage(),
        PageTags.Settings => BuildSettingsPage(),

        // 开发专用页：只显式要求测量时才构建。产品界面里没有入口，产品页面清单里也没有它。
        PageTags.Perf when BootProbe.MeasurementEnabled => BuildPerfPage(),
        _ => BuildReadinessPage(),
    };

    /// <summary>
    /// 性能自检页：1000 项虚拟化列表 + 5000 行日志面板 + 帧率实测。
    /// </summary>
    /// <remarks>
    /// 这不是给用户看的页面，而是 M0-P01 要求的测量载体：
    /// 「1000 项列表滚动稳定 60FPS、无 &gt;100ms 长任务」这条阈值只有在真实的高密度列表上才测得出来，
    /// 空窗口的数字没有意义（M0技术验证任务书 P01 的页面要求）。
    /// 之所以做成产品内的一个页面而不是单独的测试程序：这样测的就是真实视图栈，
    /// 而不是一个恰好也用 WinUI 的样板工程。
    /// </remarks>
    private UIElement BuildPerfPage(bool mounted = false)
    {
        var page = UiKit.Stack(UiKit.Space4);
        page.Children.Add(UiKit.Title("性能自检"));
        page.Children.Add(UiKit.Body(
            "1000 项列表与 5000 行日志，用于实测滚动帧率与长任务。", secondary: true));

        // ── 1000 项虚拟化列表 ──
        var list = new ListView
        {
            Height = 300,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
        };

        var items = new List<string>(1000);
        for (var i = 1; i <= 1000; i++)
        {
            items.Add($"PATH 项 {i:D4} · D:\\tools\\runtime-{i % 37:D2}\\bin");
        }

        list.ItemsSource = items;

        var (listCard, listBody) = UiKit.CardWithBody(UiKit.Space2);
        listBody.Children.Add(UiKit.SectionLabel("1000 项列表"));
        listBody.Children.Add(list);
        page.Children.Add(listCard);

        // ── 5000 行日志面板 ──
        var logList = new ListView
        {
            Height = 220,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
        };

        var lines = new List<string>(5000);
        for (var i = 1; i <= 5000; i++)
        {
            lines.Add($"[{i:D5}] envstation.step 执行中 · 状态 ok · 耗时 {i % 97} ms");
        }

        logList.ItemsSource = lines;

        var (logCard, logBody) = UiKit.CardWithBody(UiKit.Space2);
        logBody.Children.Add(UiKit.SectionLabel("5000 行日志"));
        logBody.Children.Add(logList);
        page.Children.Add(logCard);

        // ── 帧率实测 ──
        var (fpsCard, fpsBody) = UiKit.CardWithBody(UiKit.Space2);
        fpsBody.Children.Add(UiKit.SectionLabel("滚动帧率"));
        fpsBody.Children.Add(UiKit.Body(
            "滚动上方列表 5 秒，统计平均帧率与最长帧间隔。", secondary: true));

        var fpsReport = UiKit.Body("未测量。", secondary: true);
        fpsBody.Children.Add(fpsReport);
        fpsBody.Children.Add(UiKit.ButtonBar(
            UiKit.PrimaryButton("测量（5 秒）", () => _ = MeasureAndReportAsync(list, fpsReport))));
        page.Children.Add(fpsCard);

        // 只有真正挂到窗口上的那一份才登记：探针要靠它拿列表内部的 ScrollViewer，
        // 而未挂载的 ListView 模板还没实例化，根本取不到滚动容器。
        if (mounted)
        {
            _perfListView = list;
        }

        return UiKit.Scroll(page);
    }

    /// <summary>按钮入口：测量并把结论写进页面上那块文字。</summary>
    private async Task MeasureAndReportAsync(ListView list, TextBlock report)
    {
        report.Text = "测量中…";

        var scroller = await WaitForScrollViewerAsync(list).ConfigureAwait(true);
        report.Text = scroller is null
            ? "未找到滚动容器。"
            : await MeasureScrollFrameRateCoreAsync(scroller).ConfigureAwait(true);
    }

    /// <summary>供启动探针调用：对当前挂载的性能自检页测一次滚动帧率。</summary>
    /// <remarks>
    /// 探针在 <c>OnLaunched</c> 里被调用，此时窗口还没走过第一帧，ListView 的模板尚未实例化，
    /// 直接找滚动容器必然落空（第一版就是这样）。所以这里等一小拍、强制布局、再找，最多重试 1 秒。
    /// </remarks>
    internal async Task<string> MeasureFrameRateForProbeAsync()
    {
        if (_perfListView is not { } list)
        {
            return "性能自检页未挂载。";
        }

        var scroller = await WaitForScrollViewerAsync(list).ConfigureAwait(true);
        return scroller is null
            ? "未找到滚动容器。"
            : await MeasureScrollFrameRateCoreAsync(scroller).ConfigureAwait(true);
    }

    /// <summary>等到列表的模板实例化出 ScrollViewer 为止（最多 ~1 秒）。</summary>
    private async Task<ScrollViewer?> WaitForScrollViewerAsync(ListView list)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            _root.UpdateLayout();
            if (FindScrollViewer(list) is { } scroller)
            {
                return scroller;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return null;
    }

    /// <summary>
    /// 实测列表滚动的帧率。
    /// </summary>
    /// <remarks>
    /// <b>为什么要自研帧采样</b>：PresentMon 需要额外安装，而 <c>CompositionTarget.Rendering</c>
    /// 本来就是 XAML 的每帧回调——数它就是在数真实出帧次数，且零依赖。
    /// 滚动由代码驱动（每帧推进一段偏移），保证测量期间一定有渲染负载。
    /// </remarks>
    private async Task<string> MeasureScrollFrameRateCoreAsync(ScrollViewer scroller)
    {
        var frames = 0;
        var longestGapMs = 0.0;
        var lastFrame = System.Diagnostics.Stopwatch.StartNew();
        var total = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, object e)
        {
            var gap = lastFrame.Elapsed.TotalMilliseconds;
            lastFrame.Restart();

            if (gap > longestGapMs)
            {
                longestGapMs = gap;
            }

            frames++;

            // 每帧推进一段偏移：到底后回到顶部，保证 5 秒里一直在滚。
            var next = scroller.VerticalOffset + 24;
            if (next + scroller.ViewportHeight >= scroller.ExtentHeight)
            {
                next = 0;
            }

            scroller.ChangeView(null, next, null, disableAnimation: true);
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        try
        {
            await Task.Delay(5000).ConfigureAwait(true);
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        var seconds = total.Elapsed.TotalSeconds;
        var fps = frames / seconds;

        var report =
            $"{frames} 帧 / {seconds:F1} s · 平均 {fps:F1} FPS · " +
            $"最长帧 {longestGapMs:F1} ms · 可滚动 {scroller.ExtentHeight:F0} px";

        BootProbe.Write(
            $"滚动帧率：平均 {fps:F1} FPS（{frames} 帧 / {seconds:F1} s），最长帧间隔 {longestGapMs:F1} ms");
        return report;
    }

    /// <summary>在可视树里找第一个 ScrollViewer（ListView 的模板里有一个）。</summary>

    /// <summary>
    /// 用真实滚轮消息驱动滚动，测量帧间隔。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不满足于 <c>ChangeView</c> 测出来的数字</b>：<c>ChangeView</c> 走的是合成器内插，
    /// 而用户的手走的是输入路径——消息泵、命中测试、XAML 输入路由、滚动抑制（direct manipulation）。
    /// 这条路径上的卡顿，<c>ChangeView</c> 一次都测不到。
    /// </para>
    /// <para>
    /// 这里用真实的 <c>WM_MOUSEWHEEL</c> 让输入系统按正常顺序处理它，
    /// 滚动的是用户正盯着的那块内容，而不是"某个 ScrollViewer"。
    /// </para>
    /// </remarks>
    internal async Task<string> MeasureMouseWheelScrollAsync(string tag, int notches = 60, int intervalMs = 40)
    {
        Navigate(tag);
        await WaitForPageAsync().ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
        {
            return $"[{tag}] 拿不到窗口句柄。";
        }

        if (FindScrollViewer(_host) is { } scroller)
        {
            scroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        var gaps = new List<double>(300);
        var last = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, object e)
        {
            gaps.Add(last.Elapsed.TotalMilliseconds);
            last.Restart();
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        try
        {
            for (var i = 0; i < notches; i++)
            {
                // 滚轮每格 120；负值 = 向下滚。一次一整格，与真手一致。
                SendMouseWheel(hwnd, -120);
                await Task.Delay(intervalMs).ConfigureAwait(true);
            }

            await Task.Delay(500).ConfigureAwait(true);
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        if (gaps.Count < 2)
        {
            return $"[{tag}] 未采到帧。";
        }

        gaps.RemoveAt(0);
        WriteFrameSamples("wheel-" + tag, [.. gaps]);

        var sorted = new List<double>(gaps);
        sorted.Sort();

        var average = gaps.Sum() / gaps.Count;
        var worstIndex = gaps.IndexOf(sorted[^1]);
        var janky = gaps.Count(static gap => gap > 25.0);

        var offsetText = FindScrollViewer(_host) is { } after
            ? string.Create(CultureInfo.InvariantCulture, $"滚动到 {after.VerticalOffset:F0} px")
            : "未取到偏移";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{tag}] 滚轮 {notches} 格 · {gaps.Count} 帧 · 平均 {average:F1} ms（{1000.0 / average:F1} FPS）· " +
            $"P99 {sorted[(int)(sorted.Count * 0.99)]:F1} · 最长 {sorted[^1]:F1} ms（第 {worstIndex} 帧）· " +
            $"卡顿 {janky} 帧 · {offsetText}");
    }

    /// <summary>把光标移到窗口中心，再投一条滚轮消息。</summary>
    private static void SendMouseWheel(IntPtr hwnd, int delta)
    {
        _ = GetWindowRect(hwnd, out var rect);

        var x = (rect.Left + rect.Right) / 2;
        var y = (rect.Top + rect.Bottom) / 2;

        _ = SetCursorPos(x, y);

        // wParam 高字是滚动量；lParam 是屏幕坐标。
        // 先按无符号 32 位拼好再转指针：屏幕坐标的高位为 1 时数值会"看起来是负的"，
        // 用有符号路径去转就会触发 CA2020；而这本来就是一段位模式，无符号才是它的本意。
        var wParam = (IntPtr)((uint)(delta << 16) & 0xFFFF0000u);
        var lParam = (IntPtr)(((uint)y << 16) | ((uint)x & 0xFFFFu));

        _ = PostMessage(hwnd, WmMouseWheel, wParam, lParam);
    }


    /// <summary>
    /// 窗口尺寸连续变化时的帧间隔（拖拽窗口边缘 / 最大化时的体感来源）。
    /// </summary>
    /// <remarks>
    /// 尺寸变化会让<b>整棵可视树</b>重新测量与排列，代价与元素数成正比；
    /// 而下面这张表里最贵的一项往往是"会换行的文本"——换行要在每趟布局里重新做一次
    /// 断行计算，宽度每次都不一样，于是每一次都省不掉。
    /// </remarks>
    internal async Task<string> MeasureResizeStormAsync(int milliseconds = 2000)
    {
        var original = AppWindow.Size;
        var gaps = new List<double>(600);
        var last = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, object e)
        {
            gaps.Add(last.Elapsed.TotalMilliseconds);
            last.Restart();
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        var stop = false;
        var resizer = Task.Run(async () =>
        {
            var step = 0;
            while (!stop)
            {
                // 在 900~1240 之间来回：宽度每次都不同，布局缓存全部失效。
                var width = 900 + (step % 18) * 20;
                try
                {
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(width, original.Height));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    break;
                }

                step++;
                await Task.Delay(33).ConfigureAwait(false);
            }
        });

        try
        {
            await Task.Delay(milliseconds).ConfigureAwait(true);
        }
        finally
        {
            stop = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            await resizer.ConfigureAwait(true);

            try
            {
                AppWindow.Resize(original);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // 恢复原尺寸失败不影响结论。
            }
        }

        if (gaps.Count < 2)
        {
            return "尺寸变化期间未采到帧。";
        }

        gaps.RemoveAt(0);
        WriteFrameSamples("resize", [.. gaps]);

        var sorted = new List<double>(gaps);
        sorted.Sort();

        var average = gaps.Sum() / gaps.Count;
        var worst = sorted[^1];
        var janky = gaps.Count(static gap => gap > 25.0);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"尺寸连续变化 {milliseconds} ms：{gaps.Count} 帧 · 平均 {average:F1} ms/帧" +
            $"（{1000.0 / average:F1} FPS）· P99 {sorted[(int)(sorted.Count * 0.99)]:F1} · " +
            $"最长 {worst:F1} · 卡顿 {janky} 帧");
    }

    /// <summary>导航 → 新页面真实内容滚动，逐页测一遍（导航后的第一屏往往才是卡的所在）。</summary>
    internal async Task<string> MeasureNavigationThenScrollAsync(string tag)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Navigate(tag);
        await WaitForPageAsync().ConfigureAwait(true);
        clock.Stop();

        var navigateMs = clock.Elapsed.TotalMilliseconds;
        await Task.Delay(250).ConfigureAwait(true);

        var scroll = await MeasureContentScrollAsync().ConfigureAwait(true);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{tag}] 导航 {navigateMs:F1} ms · {scroll}");
    }

    /// <summary>
    /// 实测"当前页面滚动"的帧间隔分布。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与性能自检页里那个测量有三处不同，每一处都是为了测出真实体感：
    /// </para>
    /// <list type="number">
    ///   <item><b>测当前页面</b>，而不是固定的那个空列表——重的页面才有问题。</item>
    ///   <item><b>让合成器自己滚</b>（<c>ChangeView</c> 带动画），而不是每帧由代码硬推偏移。
    ///         代码硬推等于每帧强制一次同步布局，测的是"最坏情况"，不是用户滚动时的情况。</item>
    ///   <item><b>看帧间隔分布</b>，不只看平均值。人眼感觉到的是掉帧（长间隔），
    ///         而"平均 60 FPS"完全可以在若干次 100 ms 卡顿之后仍然成立。</item>
    /// </list>
    /// </remarks>
    internal async Task<string> MeasureContentScrollAsync()
    {
        if (FindScrollViewer(_host) is not { } scroller)
        {
            return "未找到滚动容器。";
        }

        var extent = scroller.ExtentHeight;
        var viewport = scroller.ViewportHeight;
        var scrollable = extent - viewport;

        if (scrollable < 200)
        {
            return $"内容不足一屏（可滚动 {scrollable:F0} px），跳过。";
        }

        var gaps = new List<double>(600);
        var last = System.Diagnostics.Stopwatch.StartNew();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, object e)
        {
            gaps.Add(last.Elapsed.TotalMilliseconds);
            last.Restart();
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        try
        {
            // 一个来回：向下滚一遍、再滚回来，全程由合成器插值，UI 线程不被强制同步布局。
            var oneWayMs = 1200;
            scroller.ChangeView(null, scrollable, null, disableAnimation: false);
            await Task.Delay(oneWayMs).ConfigureAwait(true);
            scroller.ChangeView(null, 0, null, disableAnimation: false);
            await Task.Delay(oneWayMs).ConfigureAwait(true);
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        clock.Stop();

        // 第一帧的间隔没有意义（它从订阅时刻算起），丢掉。
        if (gaps.Count > 1)
        {
            gaps.RemoveAt(0);
        }

        if (gaps.Count == 0)
        {
            return "未采到帧。";
        }

        var sorted = new List<double>(gaps);
        sorted.Sort();

        var average = gaps.Sum() / gaps.Count;
        var p50 = sorted[sorted.Count / 2];
        var p99 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];
        var worst = sorted[^1];

        // 掉帧判据：超过 1.5 个 60Hz 帧周期（25 ms）就算一次可感知的卡顿。
        var janky = gaps.Count(static gap => gap > 25.0);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"滚动 {clock.Elapsed.TotalSeconds:F1} s · {gaps.Count} 帧 · 平均 {average:F1} ms/帧" +
            $"（{1000.0 / average:F1} FPS）· 中位 {p50:F1} · P99 {p99:F1} · 最长 {worst:F1} · " +
            $"卡顿 {janky} 帧 · 可滚动 {scrollable:F0} px");
    }
}
