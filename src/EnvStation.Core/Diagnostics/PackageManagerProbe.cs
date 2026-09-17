using System.Collections.Immutable;

namespace EnvStation.Core.Diagnostics;

/// <summary>一个包管理器的可用性。</summary>
/// <param name="Manager">管理器名（winget / scoop / choco）。</param>
/// <param name="ExecutablePath">解析到的可执行文件路径；不可用时为空。</param>
public sealed record PackageManagerAvailability(string Manager, string ExecutablePath)
{
    /// <summary>是否可用。</summary>
    public bool IsAvailable => ExecutablePath.Length > 0;
}

/// <summary>
/// 探测本机可用的包管理器。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么复用 <c>InstallActionBase.ResolveManagerExecutable</c> 而不是自己找一遍</b>：
/// 安装动作执行时用的是它，判定时若另写一套查找逻辑，就会出现
/// 「前置检查说 winget 可用、到执行时却说找不到」——两边各有一套路径规则，
/// 而这种不一致最难排查（本项目已有同类教训：D-44 的两处判据各写一遍）。
/// 因此本探针只是把那一个解析函数问一遍，不新增任何查找规则。
/// </para>
/// <para>
/// <b>为什么不直接用 <c>detect.command</c> 动作</b>：那个动作只在 PATH 里查找，
/// 而 winget 的常见位置是 <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>——
/// 它在多数机器上确实在 PATH 里，但**动作自身的解析器还额外查硬编码位置**。
/// 用两种口径判同一件事，正是要避免的。
/// </para>
/// </remarks>
public static class PackageManagerProbe
{
    /// <summary>受支持的管理器，按优先级排序（与安装动作的偏好一致）。</summary>
    public static ImmutableArray<string> Managers { get; } = ["winget", "scoop", "choco"];

    /// <summary>
    /// 探测全部管理器的可用性。
    /// </summary>
    /// <returns>每个管理器的结果（含不可用的，便于界面说明"为什么这条路走不通"）。</returns>
    public static ImmutableArray<PackageManagerAvailability> ProbeAll()
    {
        var builder = ImmutableArray.CreateBuilder<PackageManagerAvailability>(Managers.Length);
        foreach (var manager in Managers)
        {
            builder.Add(Probe(manager));
        }

        return builder.ToImmutable();
    }

    /// <summary>探测单个管理器。</summary>
    public static PackageManagerAvailability Probe(string manager)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manager);

        var path = Actions.Builtin.InstallActionBase.ResolveManagerExecutable(manager);
        return new PackageManagerAvailability(manager, path ?? string.Empty);
    }

    /// <summary>
    /// 把探测结果转成来源判定要用的环境事实。
    /// </summary>
    /// <param name="availability">探测结果。</param>
    /// <param name="localArchiveExists">本地归档是否存在。</param>
    /// <param name="networkAllowed">本次运行是否允许联网。</param>
    public static RuntimeEnvironmentFacts ToFacts(
        IEnumerable<PackageManagerAvailability> availability,
        bool localArchiveExists,
        bool networkAllowed)
    {
        ArgumentNullException.ThrowIfNull(availability);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in availability)
        {
            if (item.IsAvailable)
            {
                map[item.Manager] = item.ExecutablePath;
            }
        }

        return new RuntimeEnvironmentFacts(map, localArchiveExists, networkAllowed);
    }

    /// <summary>
    /// 生成一句"本机包管理器状况"的中文事实（供安装报告与前置检查显示）。
    /// </summary>
    /// <remarks>
    /// 三个都没装时要说清**缺的是什么、怎么补**，而不是只说"不可用"（文案规范 MS-4）。
    /// </remarks>
    public static string Describe(ImmutableArray<PackageManagerAvailability> availability)
    {
        var available = availability.Where(static a => a.IsAvailable).Select(static a => a.Manager).ToArray();
        if (available.Length > 0)
        {
            return $"可用：{string.Join("、", available)}。";
        }

        return "本机没有可用的包管理器。安装「应用安装程序」（winget）后重试，或改用官方归档方式安装。";
    }
}
