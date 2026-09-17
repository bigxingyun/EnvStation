using System.Collections.Immutable;

namespace EnvStation.Core.Diagnostics;

/// <summary>要跑的一项检测。</summary>
/// <param name="ActionId">检测动作 ID。</param>
/// <param name="Label">界面上这一项的名字。</param>
/// <param name="Scope">作用域（<c>user</c> / <c>machine</c>），非作用域检测为空。</param>
/// <param name="Arguments">动作参数（可为空）。</param>
public sealed record TriageCheck(
    string ActionId,
    string Label,
    string Scope = "",
    IReadOnlyDictionary<string, string>? Arguments = null);

/// <summary>
/// 体检编排：把一组只读检测跑完，汇成一份 <see cref="DiagnosticReport"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要单独一层</b>：上一版把"跑哪几项检测"写死在页面的按钮处理函数里
/// （<c>MainWindow</c> 的 <c>RunDoctorAsync</c>：一个字符串数组 + 一层循环）。
/// 后果是三件事同时变难：CLI 想跑同一套体检得再抄一遍清单；
/// 页面想在检测过程中显示进度得改那段循环；想知道"某条待办项是哪个检测发现的"也无从对应。
/// </para>
/// <para>
/// <b>本类不依赖界面，也不依赖内核</b>：它只要一个"执行只读动作"的委托。
/// 因此 CLI 与图形界面可以共用同一份清单与同一套结论口径——
/// 两边各写一份清单，早晚会出现"命令行说 4 项异常、界面说 2 项"（历史上已经出过一次，缺陷 D-44/D-47）。
/// </para>
/// <para>
/// <b>单项失败不中断整体</b>：某一项检测抛异常或返回失败时，把它记成"未完成"继续跑下一项。
/// 体检的价值在于一次看全，半路中断比少一项更糟。
/// </para>
/// </remarks>
public sealed class TriagePlan
{
    private readonly ImmutableArray<TriageCheck> _checks;

    /// <param name="checks">要跑的检测项；为空时用 <see cref="Default"/>。</param>
    public TriagePlan(IEnumerable<TriageCheck>? checks = null)
    {
        _checks = checks is null ? Default : [.. checks];
    }

    /// <summary>要跑的检测项。</summary>
    public ImmutableArray<TriageCheck> Checks => _checks;

    /// <summary>
    /// 体检时检查的安装根目录：环境站自己的用户级目录。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意不用 <c>Path.GetTempPath()</c>：临时目录几乎永远可写，拿它做写入测试等于没测。
    /// 这里要问的是"用户级安装默认会落在哪、那儿能不能写"。
    /// </para>
    /// <para>
    /// 取不到 <c>%LOCALAPPDATA%</c> 时返回空串而<b>不是</b>编一个路径出来：
    /// 空串会让这一项检测直接失败并显示为失败，那才是这台机器的真实状态。
    /// </para>
    /// <para>
    /// <b>声明顺序不能改</b>：它必须排在 <see cref="Default"/> 前面——静态初始化按声明顺序执行，
    /// 排在后面时 <see cref="Default"/> 读到的就是 null。编译器把这条当 CS8601 拦了下来
    /// （本项目"警告即错误"，所以它第一次编译就炸了，而不是等到运行时体检去写一个空路径）。
    /// </para>
    /// </remarks>
    private static string InstallRoot { get; } = BuildInstallRoot();

    /// <summary>
    /// 默认检测清单（图形界面首页与 CLI <c>doctor</c> 共用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 每一项都在 <see cref="RemedyCatalog.MappedDetections"/> 里有映射，
    /// 否则它检出的问题在界面上只能干看——这条由用例盯着。
    /// </para>
    /// <para>
    /// <b>清单里放什么、不放什么</b>：只放"本地、快、结论明确"的项。因此
    /// <c>detect.arch</c>（模拟运行）、<c>detect.command</c>（winget 是不是在）与
    /// <c>detect.disk</c>（环境站自己的安装根目录能不能写）都进来了——
    /// 它们决定"接下来能不能装东西"，而此前体检根本不看这三件事，
    /// 用户看到"未发现问题"，然后在安装时才第一次撞上"winget 不存在"。
    /// </para>
    /// <para>
    /// <b>网络检测刻意不在默认清单里</b>：它要联网、要几秒钟，而且检测哪些主机
    /// 取决于安装源的选择（镜像源定下来之前，测谁都是猜）。它由安装前置检查按目标源调用。
    /// </para>
    /// </remarks>
    public static ImmutableArray<TriageCheck> Default { get; } =
    [
        new("envstation.detect.os", "系统版本"),
        new("envstation.detect.arch", "CPU 架构"),
        new("envstation.path.validate", "用户 PATH", "user"),
        new("envstation.path.validate", "系统 PATH", "machine"),
        new("envstation.detect.deps", "前置依赖"),
        new("envstation.detect.conflict", "命令冲突"),
        new("envstation.detect.command", "包管理器", Arguments: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command"] = "winget",
        }),
        new("envstation.detect.disk", "安装位置可写性", Arguments: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["path"] = InstallRoot,
        }),
    ];

    private static string BuildInstallRoot() =>
        Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData) ?? string.Empty,
            "EnvStation");

    /// <summary>体检时检查的安装根目录（界面与报告里显示用）。</summary>
    public static string DefaultInstallRoot => InstallRoot;

    /// <summary>
    /// 跑完全部检测并汇成报告。
    /// </summary>
    /// <param name="run">
    /// 执行一次只读动作：给定动作 ID、参数与作用域，返回（是否成功、说明、输出键值、错误码）。
    /// </param>
    /// <param name="progress">每跑完一项回报一次（已完成数、总数、当前项名字）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<DiagnosticReport> RunAsync(
        Func<TriageCheck, CancellationToken, Task<TriageCheckResult>> run,
        IProgress<(int Done, int Total, string Label)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var records = ImmutableArray.CreateBuilder<DetectionRecord>(_checks.Length);
        var done = 0;

        foreach (var check in _checks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TriageCheckResult result;
            try
            {
                result = await run(check, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // 单项炸了不影响其余：体检要的是一次看全。
                result = TriageCheckResult.Failed(ex.Message, "E_DETECT_THREW");
            }

            records.Add(new DetectionRecord(
                check.ActionId,
                check.Label,
                result.Succeeded,
                result.Message,
                result.Outputs,
                result.ErrorCode,
                result.ElapsedMilliseconds,
                check.Scope));

            done++;
            progress?.Report((done, _checks.Length, check.Label));
        }

        return DiagnosticReport.From(records);
    }
}

/// <summary>一次检测的执行结果（与内核的 <c>ActionResult</c> 解耦，便于测试）。</summary>
/// <param name="Succeeded">动作本身是否成功返回。</param>
/// <param name="Message">一句话说明。</param>
/// <param name="Outputs">动作声明的输出键值。</param>
/// <param name="ErrorCode">失败时的错误码。</param>
/// <param name="ElapsedMilliseconds">耗时（毫秒）。</param>
public sealed record TriageCheckResult(
    bool Succeeded,
    string Message,
    IReadOnlyDictionary<string, string> Outputs,
    string? ErrorCode = null,
    long ElapsedMilliseconds = 0)
{
    /// <summary>检测失败。</summary>
    public static TriageCheckResult Failed(string message, string? errorCode = null) =>
        new(false, message, new Dictionary<string, string>(StringComparer.Ordinal), errorCode);
}
