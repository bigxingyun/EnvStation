using System.Collections.Immutable;
using EnvStation.Abstractions.Transactions;

namespace EnvStation.Core.Diagnostics;

/// <summary>运行时内容的来源路线。</summary>
/// <remarks>
/// <para>
/// 三条路线的成本差一个量级（方案 A-1b）：<b>本地归档</b>最省（不用网、不用包管理器）、
/// <b>包管理器</b>居中（哈希由包管理器自己校验，我们不必维护版本与哈希表）、
/// <b>官方归档</b>最重（要维护可用版本列表与每个版本的哈希）。
/// </para>
/// <para>
/// 顺序即优先级，且**按"用户已经准备好什么"来排**：手里已经有归档就用归档，
/// 别让用户为了同一个东西再下一次。
/// </para>
/// </remarks>
public enum RuntimeSourceKind
{
    /// <summary>本地归档：用户已经下载好、并给了路径与哈希。</summary>
    LocalArchive = 0,

    /// <summary>包管理器（winget / scoop / choco）：哈希与版本由包管理器负责。</summary>
    PackageManager = 1,

    /// <summary>官方归档：给定 URL 与期望哈希，由环境站下载并校验。</summary>
    OfficialArchive = 2,

    /// <summary>无法确定来源。</summary>
    None = 3,
}

/// <summary>一条候选来源。</summary>
/// <param name="Kind">路线种类。</param>
/// <param name="PackageId">包管理器路线用的包 id（如 <c>Python.Python.3.12</c>）。</param>
/// <param name="Version">目标版本；可空表示"最新"。</param>
/// <param name="ArchivePath">本地归档路径。</param>
/// <param name="ArchiveUrl">官方归档地址。</param>
/// <param name="Sha256">官方归档的哈希（<c>sha256:…</c> 形式）。</param>
public sealed record RuntimeSourceCandidate(
    RuntimeSourceKind Kind,
    string? PackageId = null,
    string? Version = null,
    string? ArchivePath = null,
    string? ArchiveUrl = null,
    string? Sha256 = null);

/// <summary>环境实际具备的条件。</summary>
/// <param name="ManagerExecutables">可用的包管理器 → 可执行文件路径（只有真的找到路径的才在内）。</param>
/// <param name="LocalArchiveExists">本地归档路径是否真的存在。</param>
/// <param name="NetworkAllowed">本次运行是否允许联网（预演或未授权时为 false）。</param>
public sealed record RuntimeEnvironmentFacts(
    IReadOnlyDictionary<string, string> ManagerExecutables,
    bool LocalArchiveExists,
    bool NetworkAllowed)
{
    /// <summary>可用的包管理器名（按固定优先级排序）。</summary>
    public ImmutableArray<string> AvailableManagers { get; } =
        [.. ManagerExecutables.Keys.OrderBy(static k => k switch
        {
            "winget" => 0,
            "scoop" => 1,
            "choco" => 2,
            _ => 9,
        }, Comparer<int>.Default).ThenBy(static k => k, StringComparer.Ordinal)];
}

/// <summary>选定的一条路线，附带**为什么选它**。</summary>
/// <param name="Kind">选中的路线。</param>
/// <param name="Candidate">选中的候选（<see cref="RuntimeSourceKind.None"/> 时为 null）。</param>
/// <param name="Manager">包管理器路线选中的管理器名；其余为空。</param>
/// <param name="Reason">一行中文事实，供 S1 与安装报告显示。</param>
/// <param name="Blocker">阻断原因；无阻断时为 null。</param>
/// <param name="Remedy">阻断时的处置建议。</param>
public sealed record RuntimeSourceDecision(
    RuntimeSourceKind Kind,
    RuntimeSourceCandidate? Candidate,
    string Manager,
    string Reason,
    string? Blocker,
    string? Remedy)
{
    /// <summary>是否走不通。</summary>
    public bool IsBlocked => Blocker is not null;

    /// <summary>这条路线是否需要联网。</summary>
    public bool NeedsNetwork => Kind is RuntimeSourceKind.OfficialArchive or RuntimeSourceKind.PackageManager;

    /// <summary>这条路线是否需要外部包管理器。</summary>
    public bool NeedsPackageManager => Kind == RuntimeSourceKind.PackageManager;

    /// <summary>这条路线由谁负责校验下载内容的完整性。</summary>
    /// <remarks>
    /// 界面上要如实说清：走包管理器时哈希由包管理器校验，**不是环境站校验的**——
    /// 把别人的保障说成自己的，是这个产品最不该犯的错。
    /// </remarks>
    public string IntegrityGuarantor => Kind switch
    {
        RuntimeSourceKind.LocalArchive => "本地归档（由你提供哈希，环境站校验）",
        RuntimeSourceKind.OfficialArchive => "环境站校验 sha256",
        RuntimeSourceKind.PackageManager => "包管理器自行校验",
        _ => "无",
    };
}

/// <summary>
/// 按可用条件在候选来源里选一条路线，并给出理由。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要单独一层</b>：三种路线的成本差一个量级，而"该走哪条"取决于用户机器上的条件
/// （winget 装没装、归档在不在、能不能联网）。把这些判断散在界面按钮的处理函数里，
/// 就会出现"同一台机器从 CLI 走一条路、从界面走另一条路"——两边各判一次，
/// 迟早给出不同答案（本项目已有同类教训：D-44 与 D-47）。
/// </para>
/// <para>
/// <b>理由文案是本类的产物，不是界面的</b>。用户需要知道"为什么这次要从网上下"，
/// 而这只有做决策的地方说得清。文案口径按《文案规范.md》：先事实、后处置、不解释设计动机。
/// </para>
/// </remarks>
public static class RuntimeSourceRouter
{
    /// <summary>
    /// 选一条路线。
    /// </summary>
    /// <param name="candidates">候选来源，按用户或内容库里声明的顺序。</param>
    /// <param name="facts">环境实际具备的条件。</param>
    /// <returns>选中的路线；一条都走不通时 <see cref="RuntimeSourceDecision.IsBlocked"/> 为真。</returns>
    /// <remarks>
    /// 判定顺序是<b>先"能不能用"，再"按声明顺序挑第一个能用的"</b>：
    /// 声明顺序表达的是作者的偏好，而"能不能用"是客观事实。
    /// 两者混在一起（比如"本地归档优先，但不存在时静默改用网络"）会让安装报告说不清到底走了哪条。
    /// </remarks>
    public static RuntimeSourceDecision Decide(
        IEnumerable<RuntimeSourceCandidate> candidates,
        RuntimeEnvironmentFacts facts)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(facts);

        var list = candidates as IReadOnlyList<RuntimeSourceCandidate> ?? [.. candidates];

        if (list.Count == 0)
        {
            return Blocked(
                "这一项没有声明任何来源。",
                "在组件定义里补上本地归档、官方归档地址或包管理器包 id 之一。");
        }

        // 逐个试用：第一个"条件满足"的胜出，理由由它自己给。
        foreach (var candidate in list)
        {
            var decision = TryCandidate(candidate, facts);
            if (decision is not null)
            {
                return decision;
            }
        }

        // 一条都用不上：把"为什么都用不上"讲清楚，而不是笼统说"没有可用来源"。
        return ExplainExhausted(list, facts);
    }

    /// <summary>试一条候选；条件不满足时返回 null（继续试下一条）。</summary>
    private static RuntimeSourceDecision? TryCandidate(
        RuntimeSourceCandidate candidate,
        RuntimeEnvironmentFacts facts)
    {
        switch (candidate.Kind)
        {
            case RuntimeSourceKind.LocalArchive:
                if (!facts.LocalArchiveExists)
                {
                    return null;
                }

                return new RuntimeSourceDecision(
                    RuntimeSourceKind.LocalArchive,
                    candidate,
                    Manager: string.Empty,
                    Reason: $"使用本地归档（{candidate.ArchivePath}），不联网。",
                    Blocker: null,
                    Remedy: null);

            case RuntimeSourceKind.OfficialArchive:
                if (!facts.NetworkAllowed)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(candidate.ArchiveUrl))
                {
                    return null;
                }

                return new RuntimeSourceDecision(
                    RuntimeSourceKind.OfficialArchive,
                    candidate,
                    Manager: string.Empty,
                    Reason: $"从官方地址下载并校验 sha256：{candidate.ArchiveUrl}",
                    Blocker: null,
                    Remedy: null);

            case RuntimeSourceKind.PackageManager:
                if (!facts.NetworkAllowed)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(candidate.PackageId))
                {
                    return null;
                }

                // 选一个真的可用的包管理器，而不是写死 winget：
                // 用户装了 scoop 而没装 winget 是常见情况，写死会让这条路白判为不可用。
                // AvailableManagers 已按 winget → scoop → choco 排好优先级。
                if (facts.AvailableManagers.IsEmpty)
                {
                    return null;
                }

                var manager = facts.AvailableManagers[0];
                return new RuntimeSourceDecision(
                    RuntimeSourceKind.PackageManager,
                    candidate,
                    manager,
                    Reason: $"通过 {manager} 安装 {candidate.PackageId}，下载与完整性由 {manager} 负责。",
                    Blocker: null,
                    Remedy: null);

            default:
                return null;
        }
    }

    /// <summary>一条都用不上时，说清是哪一类条件不满足。</summary>
    private static RuntimeSourceDecision ExplainExhausted(
        IReadOnlyList<RuntimeSourceCandidate> candidates,
        RuntimeEnvironmentFacts facts)
    {
        var kinds = candidates.Select(static c => c.Kind).Distinct().ToArray();

        // 只有包管理器路线，而机器上没有包管理器：这是最常见的一种，处置很具体。
        if (kinds.Length == 1 && kinds[0] == RuntimeSourceKind.PackageManager && facts.AvailableManagers.IsEmpty)
        {
            return Blocked(
                "本机没有可用的包管理器（winget / scoop / choco 都没找到）。",
                "安装「应用安装程序」（winget）后重试，或改用官方归档方式安装。");
        }

        if (!facts.NetworkAllowed && kinds.All(static k => k != RuntimeSourceKind.LocalArchive))
        {
            return Blocked(
                "这次运行不允许联网，而声明的来源都需要联网。",
                "把安装包下载到本机后改用本地归档，或在允许联网的运行中重试。");
        }

        if (kinds.Contains(RuntimeSourceKind.LocalArchive))
        {
            var path = candidates.First(static c => c.Kind == RuntimeSourceKind.LocalArchive).ArchivePath;
            return Blocked(
                $"声明的本地归档不存在：{path}",
                "确认文件路径，或改用官方归档方式安装。");
        }

        return Blocked(
            "声明的来源在当前条件下都不可用。",
            "检查网络与包管理器是否可用，或提供本地归档。");
    }

    private static RuntimeSourceDecision Blocked(string blocker, string remedy) =>
        new(RuntimeSourceKind.None, null, string.Empty,
            Reason: "尚无可用来源。", Blocker: blocker, Remedy: remedy);
}

/// <summary>
/// 来源路线的能力需求 → 与安装计划的风险级对应。
/// </summary>
/// <remarks>
/// 单独一个映射而不是散在界面里：三条路线的风险差别很大——本地归档几乎无风险，
/// 官方归档多一次网络下载，包管理器则把"装到哪、装什么"的决定权交给了外部工具。
/// 界面要按这个级别决定确认方式（L0/L1 一次确认、L2 二次确认）。
/// </remarks>
public static class RuntimeSourceRisk
{
    /// <summary>取一条路线对应的风险级。</summary>
    public static RiskLevel Of(RuntimeSourceKind kind) => kind switch
    {
        RuntimeSourceKind.LocalArchive => RiskLevel.Reversible,
        RuntimeSourceKind.OfficialArchive => RiskLevel.Reversible,
        RuntimeSourceKind.PackageManager => RiskLevel.High,
        _ => RiskLevel.Safe,
    };
}
