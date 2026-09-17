using System.Collections.Immutable;
using EnvStation.Abstractions.Diagnostics;

namespace EnvStation.Core.Diagnostics;

/// <summary>
/// 安装向导的六个步骤（对应《需求分析.md》5.2 节的 S1~S6）。
/// </summary>
/// <remarks>
/// 顺序即语义：<see cref="Target"/> 之前不需要用户提供任何东西，
/// <see cref="Authorize"/> 是唯一允许用户说"不用改环境变量"的一步，
/// <see cref="Execute"/> 之前必须已经通过前置检查。
/// </remarks>
public enum InstallStep
{
    /// <summary>S1 选择目标：装哪门语言、哪个版本。</summary>
    Target = 0,

    /// <summary>S2 组件选择：运行时核心必选，文档/调试符号等可选。</summary>
    Components = 1,

    /// <summary>S3 安装位置：实时校验路径，不通过则不得继续。</summary>
    Location = 2,

    /// <summary>S4 前置检查：系统版本、架构、磁盘、网络、权限、冲突。</summary>
    Preflight = 3,

    /// <summary>S5 环境变量授权：默认全不勾，勾选时当场展示将写入的内容。</summary>
    Authorize = 4,

    /// <summary>S6 执行与验证：下载 → 校验 → 解压 → 影子目录 → 原子切换 → 登记 → 验证。</summary>
    Execute = 5,

    /// <summary>已完成。</summary>
    Done = 6,

    /// <summary>已取消（取消等同失败，走回滚，不留半成品）。</summary>
    Cancelled = 7,

    /// <summary>已失败并已回滚。</summary>
    Failed = 8,
}

/// <summary>
/// 安装向导的状态机。
/// </summary>
/// <remarks>
/// <para>
/// 这一步只承载<b>状态与转移</b>，不承载执行——执行由编排服务在 M2 接上。
/// 之所以先立这个类型：界面上"安装"这件事此前根本不存在，
/// 而六步流程里最容易做错的两处恰好是纯状态问题——
/// 「阻断级前置检查不通过时禁止前进」与「环境变量授权默认全不勾且可全部不勾」。
/// 把它们做成状态机的一部分，就不会被某个按钮的处理函数绕过。
/// </para>
/// <para>
/// 本类型不可变：每次转移返回新实例。界面只渲染快照，不做"改一半再重画"。
/// </para>
/// </remarks>
public sealed record InstallPlan
{
    private InstallPlan(
        string runtimeKind,
        string version,
        InstallStep current,
        string installDirectory,
        bool registerUserPath,
        bool registerMachinePath,
        bool setDedicatedVariables,
        ImmutableArray<Finding> findings,
        string? failure)
    {
        RuntimeKind = runtimeKind;
        Version = version;
        Current = current;
        InstallDirectory = installDirectory;
        RegisterUserPath = registerUserPath;
        RegisterMachinePath = registerMachinePath;
        SetDedicatedVariables = setDedicatedVariables;
        Findings = findings;
        Failure = failure;
    }

    /// <summary>运行时种类（如 <c>python</c>）。</summary>
    public string RuntimeKind { get; }

    /// <summary>目标版本。</summary>
    public string Version { get; }

    /// <summary>当前步骤。</summary>
    public InstallStep Current { get; }

    /// <summary>安装目录。</summary>
    public string InstallDirectory { get; }

    /// <summary>是否注册到用户级 PATH。<b>默认 false</b>。</summary>
    public bool RegisterUserPath { get; }

    /// <summary>是否注册到系统级 PATH。<b>默认 false，且需管理员权限</b>。</summary>
    public bool RegisterMachinePath { get; }

    /// <summary>是否设置语言专用变量（如 <c>JAVA_HOME</c>）。<b>默认 false</b>。</summary>
    public bool SetDedicatedVariables { get; }

    /// <summary>前置检查与路径校验的发现项。</summary>
    public ImmutableArray<Finding> Findings { get; }

    /// <summary>失败原因（仅 <see cref="InstallStep.Failed"/> 下有值）。</summary>
    public string? Failure { get; }

    /// <summary>是否存在阻断级发现。</summary>
    public bool HasBlockers => Findings.Any(static f => f.IsBlocker);

    /// <summary>是否处于进行中（未完成、未取消、未失败）。</summary>
    public bool IsInProgress => Current is not (InstallStep.Done or InstallStep.Cancelled or InstallStep.Failed);

    /// <summary>是否已经开始执行（<see cref="InstallStep.Execute"/> 及之后不可再改配置）。</summary>
    public bool IsExecuting => Current >= InstallStep.Execute && IsInProgress;

    /// <summary>用户是否授权了任何环境变量写入。</summary>
    public bool HasAnyEnvAuthorization =>
        RegisterUserPath || RegisterMachinePath || SetDedicatedVariables;

    /// <summary>新建一个计划（所有授权默认关闭——这是需求 G2 与 D5 的落地点）。</summary>
    public static InstallPlan For(string runtimeKind, string version) =>
        new(runtimeKind, version, InstallStep.Target, string.Empty,
            registerUserPath: false, registerMachinePath: false, setDedicatedVariables: false,
            findings: [], failure: null);

    /// <summary>
    /// 尝试前进到下一步。
    /// </summary>
    /// <returns>成功时返回新状态；被阻断时返回原状态，原因写入 <paramref name="blockedBy"/>。</returns>
    /// <remarks>
    /// 阻断规则集中在这里，而不是散在各页面的"下一步"按钮里：
    /// <list type="number">
    /// <item>有阻断级发现时不得离开 <see cref="InstallStep.Preflight"/>。</item>
    /// <item>目录为空时不得离开 <see cref="InstallStep.Location"/>。</item>
    /// <item>未选择运行时种类时不得离开 <see cref="InstallStep.Target"/>。</item>
    /// </list>
    /// </remarks>
    public InstallPlan TryAdvance(out string? blockedBy)
    {
        blockedBy = null;

        if (!IsInProgress)
        {
            blockedBy = "当前流程已结束。";
            return this;
        }

        switch (Current)
        {
            case InstallStep.Target when RuntimeKind.Length == 0:
                blockedBy = "尚未选择要安装的运行时。";
                return this;

            case InstallStep.Location when InstallDirectory.Length == 0:
                blockedBy = "尚未选择安装位置。";
                return this;

            case InstallStep.Preflight when HasBlockers:
                blockedBy = $"有 {Findings.Count(static f => f.IsBlocker)} 项阻断级问题未处理。";
                return this;

            case InstallStep.Preflight when InstallDirectory.Length == 0:
                blockedBy = "尚未选择安装位置。";
                return this;
        }

        return Current switch
        {
            InstallStep.Target => With(step: InstallStep.Components),
            InstallStep.Components => With(step: InstallStep.Location),
            InstallStep.Location => With(step: InstallStep.Preflight),
            InstallStep.Preflight => With(step: InstallStep.Authorize),
            InstallStep.Authorize => With(step: InstallStep.Execute),
            InstallStep.Execute => this, // 执行阶段的推进由编排服务驱动，界面不直接改
            _ => this,
        };
    }

    /// <summary>退回到上一步（不丢失已填内容）。</summary>
    public InstallPlan Back()
    {
        if (Current is InstallStep.Target or InstallStep.Components || !IsInProgress)
        {
            return this;
        }

        // 执行阶段不允许后退（文件已在写）。
        if (Current == InstallStep.Execute)
        {
            return this;
        }

        return With(step: Current - 1);
    }

    /// <summary>设置运行时种类与版本。</summary>
    public InstallPlan WithTarget(string runtimeKind, string version) =>
        With(runtimeKind: runtimeKind, version: version);

    /// <summary>设置安装目录（是否合法由路径校验器判定，写入 <see cref="Finding"/> 后再前进）。</summary>
    public InstallPlan WithDirectory(string directory) => With(directory: directory);

    /// <summary>设置环境变量授权。三个开关默认全关，随时可以全部取消勾选。</summary>
    public InstallPlan WithAuthorization(bool userPath, bool machinePath, bool dedicatedVariables) =>
        With(userPath: userPath, machinePath: machinePath, dedicated: dedicatedVariables);

    /// <summary>替换发现项集合。</summary>
    public InstallPlan WithFindings(IEnumerable<Finding> findings) => With(findings: [.. findings]);

    /// <summary>标记完成。</summary>
    public InstallPlan Complete() => With(step: InstallStep.Done);

    /// <summary>标记取消（取消等同失败，进入回滚）。</summary>
    public InstallPlan Cancel() => With(step: InstallStep.Cancelled, failure: null);

    /// <summary>标记失败。</summary>
    public InstallPlan Fail(string reason) => With(step: InstallStep.Failed, failure: reason);

    private InstallPlan With(
        string? runtimeKind = null,
        string? version = null,
        InstallStep? step = null,
        string? directory = null,
        bool? userPath = null,
        bool? machinePath = null,
        bool? dedicated = null,
        ImmutableArray<Finding>? findings = null,
        string? failure = null) =>
        new(runtimeKind ?? RuntimeKind,
            version ?? Version,
            step ?? Current,
            directory ?? InstallDirectory,
            userPath ?? RegisterUserPath,
            machinePath ?? RegisterMachinePath,
            dedicated ?? SetDedicatedVariables,
            findings ?? Findings,
            failure ?? Failure);
}
