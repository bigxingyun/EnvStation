using System.Collections.Immutable;
using EnvStation.Abstractions.Transactions;

namespace EnvStation.Abstractions.Actions;

/// <summary>
/// 能力标识符常量。能力是"动作即能力"铁律（需求 A1）的直接载体：
/// 包只能通过声明能力获得权限，而任何动作都无法超出其绑定的能力。
/// </summary>
/// <remarks>
/// 命名规则（与需求 S1 一致）：<c>CAP.&lt;域&gt;.&lt;动作族&gt;</c>，全大写。
/// 第三方包不得定义新能力——能力表由官方封闭维护，见 <see cref="Capabilities.All"/>。
/// </remarks>
public static class CapabilityIds
{
    /// <summary>只读探测：系统、磁盘、网络、已安装运行时、环境变量读取。</summary>
    public const string Inspect = "CAP.INSPECT";

    /// <summary>网络下载。必须配合包清单中声明的域名白名单。</summary>
    public const string NetDownload = "CAP.NET.DOWNLOAD";

    /// <summary>压缩包解压与打包。</summary>
    public const string Archive = "CAP.ARCHIVE";

    /// <summary>调用外部包管理器（winget / scoop / choco），参数经白名单过滤。</summary>
    public const string PackageManager = "CAP.PKG.MANAGER";

    /// <summary>向安装根目录写入文件（走影子目录加原子切换）。</summary>
    public const string FileSystemInstall = "CAP.FS.INSTALL";

    /// <summary>修改用户级环境变量。</summary>
    public const string EnvironmentUser = "CAP.ENV.USER";

    /// <summary>修改系统级（机器级）环境变量，需管理员权限。</summary>
    public const string EnvironmentMachine = "CAP.ENV.MACHINE";

    /// <summary>修改 PATH。</summary>
    public const string PathModify = "CAP.PATH.MODIFY";

    /// <summary>修改白名单内的应用程序配置文件（Maven / npm / pip / Gradle 等）。</summary>
    public const string ConfigApp = "CAP.CONFIG.APP";

    /// <summary>创建符号链接 / 目录联接。</summary>
    public const string Link = "CAP.LINK";

    /// <summary>启动受控子进程（仅限已登记运行时的可执行文件加参数白名单）。</summary>
    public const string ProcessLaunch = "CAP.PROCESS.LAUNCH";

    /// <summary>托管运行时的登记、切换、补装与版本管理。</summary>
    public const string RuntimeManage = "CAP.RUNTIME.MANAGE";

    /// <summary>向用户提问（交互式动作；无人值守执行时必须被拒绝或改用默认值）。</summary>
    public const string UserInteraction = "CAP.UI.INTERACT";

    /// <summary>清理本包产生的临时文件与缓存。</summary>
    public const string Cleanup = "CAP.CLEANUP";
}

/// <summary>
/// 能力描述条目。用于 UI 授权页展示（需求 T0-b~T0-e 的逐项授权与高风险确认）。
/// </summary>
/// <param name="Id">能力标识，取值见 <see cref="CapabilityIds"/>。</param>
/// <param name="DisplayName">中文展示名。</param>
/// <param name="RiskLevel">风险等级，决定授权交互强度。</param>
/// <param name="Explanation">向用户解释该能力会被用来做什么。</param>
/// <param name="RequiresElevation">是否通常需要管理员权限。</param>
public sealed record CapabilityDescriptor(
    string Id,
    string DisplayName,
    RiskLevel RiskLevel,
    string Explanation,
    bool RequiresElevation);

/// <summary>
/// 官方封闭能力表。动作只能引用这里列出的能力；缺失的能力在加载期即报错。
/// </summary>
public static class Capabilities
{
    /// <summary>全部官方能力的只读清单。</summary>
    public static ImmutableArray<CapabilityDescriptor> All { get; } =
    [
        new(CapabilityIds.Inspect, "读取系统信息", RiskLevel.Safe,
            "读取系统版本、CPU 架构、磁盘空间、已安装运行时与环境变量。不会修改任何内容。", false),

        new(CapabilityIds.NetDownload, "下载文件", RiskLevel.Reversible,
            "从包清单声明的域名下载文件。每次下载都必须携带 SHA-256 校验值，校验失败即中止。", false),

        new(CapabilityIds.Archive, "解压与打包", RiskLevel.Reversible,
            "把压缩包解压到授权目录。拒绝含上级目录跳转的条目，拒绝体积异常膨胀的压缩包。", false),

        new(CapabilityIds.PackageManager, "调用包管理器", RiskLevel.High,
            "调用 winget / scoop / choco 安装或卸载软件。命令与参数经过白名单过滤，不接受任意命令行。", false),

        new(CapabilityIds.FileSystemInstall, "写入安装目录", RiskLevel.Reversible,
            "向授权根目录内写入文件。写入失败时自动回滚到写入前状态。", false),

        new(CapabilityIds.EnvironmentUser, "修改用户环境变量", RiskLevel.High,
            "修改当前用户的环境变量。写入前自动创建快照，之后可回滚。", false),

        new(CapabilityIds.EnvironmentMachine, "修改系统环境变量", RiskLevel.Dangerous,
            "修改系统级环境变量，对本机所有用户生效。需要管理员权限，使用时必须显式确认。", true),

        new(CapabilityIds.PathModify, "修改 PATH", RiskLevel.High,
            "向 PATH 添加或移除目录。PATH 超过系统长度上限时拒绝写入。", false),

        new(CapabilityIds.ConfigApp, "修改应用配置", RiskLevel.High,
            "修改 Maven / npm / pip / Gradle 等工具的配置文件。默认合并写入，保留已有的私服与凭据配置。", false),

        new(CapabilityIds.Link, "创建链接", RiskLevel.Reversible,
            "创建符号链接或目录联接，用于多版本切换。创建失败时会降级为复制。", false),

        new(CapabilityIds.ProcessLaunch, "运行程序", RiskLevel.High,
            "运行已登记运行时的命令以验证安装结果（例如执行 python --version）。不接受包自定义的命令行。", false),

        new(CapabilityIds.RuntimeManage, "管理运行时版本", RiskLevel.High,
            "安装、切换、移除受托管运行时版本。移除版本时不删除项目数据目录。", false),

        new(CapabilityIds.UserInteraction, "向用户提问", RiskLevel.Safe,
            "执行过程中向用户提问或收集信息（例如代理账号）。用户输入的内容只作为数据使用，不会被当成命令执行。无人值守时不弹出提示。", false),

        new(CapabilityIds.Cleanup, "清理临时文件", RiskLevel.Safe,
            "清理本包自己的临时目录与过期缓存，不触碰其他包与用户文件。", false),
    ];

    private static readonly ImmutableDictionary<string, CapabilityDescriptor> Index =
        All.ToImmutableDictionary(static d => d.Id, StringComparer.Ordinal);

    /// <summary>按 ID 查找能力描述。</summary>
    public static bool TryGet(string id, out CapabilityDescriptor descriptor) =>
        Index.TryGetValue(id, out descriptor!);

    /// <summary>判断给定 ID 是否为官方定义的能力。</summary>
    public static bool IsKnown(string id) => Index.ContainsKey(id);
}

/// <summary>
/// 一组能力的集合运算。授权判定使用集合包含语义：包声明的能力集必须被授予集覆盖。
/// </summary>
public sealed class CapabilitySet
{
    /// <summary>空集合。</summary>
    public static CapabilitySet Empty { get; } = new([]);

    private readonly ImmutableHashSet<string> _ids;

    /// <summary>由能力 ID 序列构造集合。</summary>
    public CapabilitySet(IEnumerable<string> ids) =>
        _ids = ids.ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>集合成员数量。</summary>
    public int Count => _ids.Count;

    /// <summary>枚举集合中的能力 ID。</summary>
    public IEnumerable<string> Ids => _ids;

    /// <summary>是否包含指定能力。</summary>
    public bool Contains(string id) => _ids.Contains(id);

    /// <summary>是否包含 <paramref name="other"/> 的全部能力。</summary>
    public bool ContainsAll(CapabilitySet other) => _ids.IsSupersetOf(other._ids);

    /// <summary>返回 <paramref name="other"/> 中不被本集合包含的能力（用于生成缺哪项授权的提示）。</summary>
    public ImmutableArray<string> MissingFrom(CapabilitySet other) =>
        [.. other._ids.Where(id => !_ids.Contains(id)).OrderBy(static s => s, StringComparer.Ordinal)];

    /// <summary>合并两个集合。</summary>
    public CapabilitySet Union(CapabilitySet other) => new(_ids.Union(other._ids));

    /// <summary>最高风险等级（用于决定授权交互强度）。</summary>
    public RiskLevel MaxRisk()
    {
        var max = RiskLevel.Safe;
        foreach (var id in _ids)
        {
            if (Capabilities.TryGet(id, out var d) && d.RiskLevel > max)
            {
                max = d.RiskLevel;
            }
        }

        return max;
    }

    /// <summary>按稳定顺序输出，便于日志与测试比对。</summary>
    public override string ToString() =>
        string.Join(", ", _ids.OrderBy(static s => s, StringComparer.Ordinal));
}
