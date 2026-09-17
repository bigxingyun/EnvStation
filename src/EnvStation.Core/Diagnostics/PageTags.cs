namespace EnvStation.Core.Diagnostics;

/// <summary>
/// 界面页面标签清单。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独有一个类型</b>：标签字符串此前散在三处——导航构造、路由 switch、测量清单。
/// 三处各写一遍的后果是"加一页要记得改三个地方"，而漏掉任何一处都不会编译报错，
/// 只会在运行时表现为"这一页进不去"或"测不到它"。
/// </para>
/// <para>
/// <b>为什么放在 Core 而不是界面工程</b>：这份清单要参与"上次所在页面是否合法"的判断，
/// 而那条判断是纯逻辑、必须能测。放在界面工程里会被 WinUI 依赖挡住，测不到；
/// 放这里则 CLI、界面与测试三方共用同一份定义。
/// </para>
/// <para>
/// 本类型只管"有哪些页面"和"某个标签认不认识"；具体路由仍在界面的 <c>BuildPageForTag</c>——
/// 那是唯一需要 switch 的地方。
/// </para>
/// </remarks>
public static class PageTags
{
    /// <summary>环境就绪（首页）。</summary>
    public const string Overview = "overview";

    /// <summary>待处理问题。</summary>
    public const string Doctor = "doctor";

    /// <summary>运行时。</summary>
    public const string Runtime = "runtime";

    /// <summary>环境变量。</summary>
    public const string Env = "env";

    /// <summary>导入包。</summary>
    public const string Packages = "packages";

    /// <summary>快照与回滚。</summary>
    public const string History = "history";

    /// <summary>动作库。</summary>
    /// <remarks>
    /// <b>它不是一级页面</b>（决策 D4）：74 个动作的清单有价值，但价值对象是<b>包作者与审计者</b>，
    /// 不是配置环境的用户。放在一级导航里会让用户以为"这个软件是给写脚本的人用的"。
    /// 入口移到「设置」，不进产品导航。
    /// </remarks>
    public const string Actions = "actions";

    /// <summary>设置。</summary>
    public const string Settings = "settings";

    /// <summary>开发专用页面：性能自检（不在产品界面，仅测量模式下可达）。</summary>
    public const string Perf = "perf";

    /// <summary>
    /// 产品页面（<b>不含</b>开发专用的性能自检页）。顺序即导航栏顺序。
    /// </summary>
    /// <remarks>
    /// 测量脚本、探针与"上次所在页面"的合法性判断都以这份清单为准。
    /// 性能自检页刻意不在其中：它带着上千条编造数据，不该出现在用户能走到的地方。
    /// </remarks>
    public static IReadOnlyList<string> Product { get; } =
        [Overview, Doctor, Runtime, Env, Packages, History, Settings];

    /// <summary>
    /// 可供"下次启动落点"使用的标签（产品页面）。
    /// </summary>
    /// <remarks>
    /// 当前与 <see cref="Product"/> 相同。分开声明是为了让"动作库不进导航但仍是可达页面"
    /// 这件事在类型上说得清：它不在 <see cref="Product"/> 里，因此不该成为启动落点。
    /// </remarks>
    public static IReadOnlyList<string> Navigable => Product;

    /// <summary>某个标签是不是已知的产品页面。</summary>
    /// <remarks>
    /// <para>
    /// 用于"上次所在页面"的合法性判断：设置文件里可能存着一个已经不存在的标签
    /// （手工改过、或从新版本回退到旧版本）。这时必须回落到首页，而不是去构建一个不存在的页面
    /// ——那会得到一个空白窗口，而用户不知道该怎么修。
    /// </para>
    /// <para>
    /// <b>开发专用页面不算已知</b>：即使设置文件里写着 <c>perf</c>，也不该让下次启动落在那一页。
    /// 这条是被用例钉住的（UI-80）。
    /// </para>
    /// </remarks>
    public static bool IsKnown(string? tag) =>
        tag is { Length: > 0 } && Product.Contains(tag, StringComparer.Ordinal);

    /// <summary>把一个可能是任意外来值的标签规整成可用的落点。</summary>
    public static string Normalize(string? tag) => IsKnown(tag) ? tag! : Overview;
}
