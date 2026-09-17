using System.Globalization;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnvStation.App;

/// <summary>
/// 主窗口性能测量装置的静态辅助与 Win32 互操作（partial）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么再拆一层</b>：探针文件拆出来后仍有 862 行，超过纪律 X-4 的 800 行上限；
/// 而"可视树遍历、帧样本落盘、屏幕信息"这类静态工具与"测什么、怎么测"本来就是两件事——
/// 前者是手段，后者是目的。混在一起时，想改一次测量口径要先翻过两百行互操作声明。
/// </para>
/// <para>
/// <b>为什么用 <c>DllImport</c> 而不是 <c>LibraryImport</c></b>：与 <c>Core</c> 里那处
/// <c>GetLongPathNameW</c> 不同，这些调用只服务于本机性能测量、不进入发布路径，
/// 因此不需要为 Native AOT 的封送开销做优化；用 <c>DllImport</c> 少一处 <c>AllowUnsafeBlocks</c> 依赖。
/// </para>
/// </remarks>
internal sealed partial class MainWindow
{
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller)
            {
                return scroller;
            }

            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 统计当前页面的可视树规模（元素总数 + 各类型分布）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 渲染开销几乎正比于可视树里的元素数：WinUI 的布局是两趟递归遍历，每个元素两趟都要参与
    /// 测量与排列，而文本元素还要额外做一次成型（glyph run 生成）。元素数是"这棵树贵不贵"的
    /// 第一手证据，比任何体感描述都可靠。
    /// </para>
    /// <para>
    /// <b>刻意不在这里做计时</b>：对一个已经算好的布局调 <c>UpdateLayout()</c> 是空转，
    /// 量出来永远是 0.00 ms，看着像"布局不要钱"。真正的布局代价只能由
    /// <see cref="MeasureClickToPaintAsync"/> 从"点击到页面挂上"这段时间里量。
    /// 一个会给出误导性数字的指标，比没有指标更糟。
    /// </para>
    /// </remarks>
    internal string DescribeVisualTree(string tag)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = CountElements(_host, counts);
        counts.TryGetValue("TextBlock", out var textBlocks);

        var top = counts
            .OrderByDescending(static pair => pair.Value)
            .Take(6)
            .Select(static pair => $"{pair.Key} {pair.Value}")
            .ToArray();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{tag}] 可视树 {total} 元素（文本 {textBlocks}）· {string.Join(" / ", top)}");
    }

    private static int CountElements(DependencyObject node, Dictionary<string, int> counts)
    {
        var name = node.GetType().Name;
        counts[name] = counts.TryGetValue(name, out var existing) ? existing + 1 : 1;

        var total = 1;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            total += CountElements(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), counts);
        }

        return total;
    }

    /// <summary>一行 CSV，记录帧间隔样本（供渲染开销分析）。</summary>
    /// <remarks>
    /// 写原始样本而不是写聚合值：聚合值只能回答"平均多少"，而"哪里卡了一下、卡在第几帧"
    /// 只有原始序列答得上来。这些文件放在临时目录，供 before/after 对照，不进入产品数据目录。
    /// </remarks>
    private static void WriteFrameSamples(string name, double[] samples)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "envstation-frames-" + name + ".csv");

            // 与启动探针同理：日志给人看，必须带 BOM。
            File.WriteAllText(
                path,
                string.Join('\n', samples.Select(static sample => sample.ToString("F2", CultureInfo.InvariantCulture))),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 采样落盘失败不影响测量结论本身。
        }
    }

    /// <summary>屏幕尺寸 / 缩放 / 刷新率。</summary>
    /// <remarks>
    /// <para>
    /// 前两项目走 Win32 直读；刷新率这里<b>不</b>从 <c>DEVMODE</c> 取——那个结构里有一个联合体，
    /// 字段偏移写错一位就会读出一个"看着像数、其实全错"的答案（本机实测读到过 1080x0 @ 0 Hz），
    /// 而错误的参照物比没有参照物更危险。
    /// </para>
    /// <para>
    /// 这两个数之所以值得记：<b>125% 缩放意味着界面上每一个逻辑像素都会落在非整数物理像素上</b>，
    /// 滚动时文本与 1px 描边是否稳定，取决于偏移量取整的方式——这是很多"看着不流畅"
    /// 与"帧率无关"的真实成因。
    /// </para>
    /// </remarks>
    private static string DescribeDisplay()
    {
        var width = GetSystemMetrics(SystemMetricCxScreen);
        var height = GetSystemMetrics(SystemMetricCyScreen);
        var dpi = GetDpiForSystem();
        var scaling = dpi / 96.0 * 100;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"屏幕 {width}x{height} · 缩放 {scaling:F0}%（{dpi} DPI）");
    }

    private const int SystemMetricCxScreen = 0;
    private const int SystemMetricCyScreen = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private const int WmMouseWheel = 0x020A;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
