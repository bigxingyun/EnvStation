namespace EnvStation.Core.Diagnostics;

/// <summary>一次输入校验的结论。</summary>
/// <param name="IsBlocking">是否为阻断级（禁止继续）。</param>
/// <param name="Message">面向用户的一句话说明。</param>
/// <param name="RuleId">触发的规则标识；无问题时为空。</param>
/// <param name="Suggestion">修正建议；可为空。</param>
public sealed record ValidationVerdict(bool IsBlocking, string Message, string RuleId, string? Suggestion = null)
{
    /// <summary>通过。</summary>
    public static ValidationVerdict Ok { get; } = new(false, string.Empty, string.Empty);

    /// <summary>是否通过（既非阻断也无需提示）。</summary>
    public bool IsClean => !IsBlocking && Message.Length == 0;

    /// <summary>警告级（可继续但要提示）。</summary>
    public static ValidationVerdict Warn(string ruleId, string message, string? suggestion = null) =>
        new(false, message, ruleId, suggestion);

    /// <summary>阻断级（禁止继续）。</summary>
    public static ValidationVerdict Block(string ruleId, string message, string? suggestion = null) =>
        new(true, message, ruleId, suggestion);
}

/// <summary>
/// 键入即校验的防抖状态机。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么把这段逻辑放在 Core 而不是控件里</b>：防抖是"什么时候该算"的纯时序问题，
/// 与 WinUI 无关；放在控件里就只能靠人工点界面来验证，而它恰恰是**最容易写错且最难手工发现**的一类逻辑——
/// 少防抖会让每个字符都触发一次磁盘/注册表检查，防抖窗口算错会让最后一次输入永远不生效。
/// 抽出来之后可以用假时钟精确测。
/// </para>
/// <para>
/// <b>与界面的分工</b>：本类只回答"现在该不该跑校验、这次结果还算不算数"。
/// 界面用一个定时器按 <see cref="DueIn"/> 唤醒它，然后在真正跑校验前调
/// <see cref="ShouldRun"/> 确认这一版输入还没被更新的输入取代。
/// </para>
/// <para>
/// <b>为什么还需要"结果还算不算数"</b>：校验通常要读磁盘或注册表，是异步的。
/// 用户在第 N 次输入触发的校验返回时，输入可能已经改到第 N+3 版了——
/// 这时把旧结论显示出来，用户会看到"我明明改对了它还说错"。
/// <see cref="IsCurrent"/> 就是拦这个的。
/// </para>
/// </remarks>
public sealed class DebouncedValidator
{
    private readonly long _windowMs;
    private long _version;
    private long _lastChangeMs;
    private long _validatedVersion = -1;
    private ValidationVerdict _verdict = ValidationVerdict.Ok;

    /// <param name="window">防抖窗口；默认 200ms（规范 6.1 节 V1 的取值）。</param>
    public DebouncedValidator(TimeSpan? window = null)
    {
        var value = window ?? TimeSpan.FromMilliseconds(200);
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "防抖窗口不能为负。");
        }

        _windowMs = (long)value.TotalMilliseconds;
    }

    /// <summary>防抖窗口。</summary>
    public TimeSpan Window => TimeSpan.FromMilliseconds(_windowMs);

    /// <summary>当前输入版本号（每次变更自增）。</summary>
    public long Version => _version;

    /// <summary>最近一次已被采纳的结论。</summary>
    public ValidationVerdict Verdict => _verdict;

    /// <summary>
    /// 记录一次输入变更。
    /// </summary>
    /// <param name="nowMs">
    /// 当前时刻，<b>单位毫秒</b>。与 <see cref="System.Environment.TickCount64"/> 同一口径，
    /// 调用方直接传它即可，不必换算。
    /// </param>
    /// <returns>本次变更后的版本号。</returns>
    /// <remarks>
    /// 单位刻意写进参数名：本类第一版用的是 <c>TimeSpan.Ticks</c> 口径，而调用方传的是
    /// <c>TickCount64</c>（毫秒），两者相差一万倍，表现为「防抖窗口永远不到期」。
    /// 单位不一致是这类时序代码最典型的错误，写进名字比写进注释有效。
    /// </remarks>
    public long Changed(long nowMs)
    {
        _version++;
        _lastChangeMs = nowMs;
        return _version;
    }

    /// <summary>
    /// 距离可以开始校验还有多久；已到期返回 <see cref="TimeSpan.Zero"/>。
    /// </summary>
    /// <param name="nowMs">当前时刻（毫秒）。</param>
    public TimeSpan DueIn(long nowMs)
    {
        var elapsed = nowMs - _lastChangeMs;
        var remaining = _windowMs - elapsed;
        return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
    }

    /// <summary>
    /// 现在是否该跑校验。
    /// </summary>
    /// <param name="nowMs">当前时刻（毫秒）。</param>
    /// <remarks>
    /// 两个条件缺一不可：防抖窗口已过，<b>且</b>当前版本还没有被校验过。
    /// 只看窗口会导致定时器多触发几次时重复跑同一版输入的校验。
    /// </remarks>
    public bool ShouldRun(long nowMs) =>
        DueIn(nowMs) == TimeSpan.Zero && _validatedVersion != _version;

    /// <summary>
    /// 提交一次校验结果。
    /// </summary>
    /// <param name="version">这次校验对应的输入版本（由 <see cref="Changed"/> 返回）。</param>
    /// <param name="verdict">结论。</param>
    /// <returns>结果是否被采纳（版本仍是最新的）。</returns>
    /// <remarks>陈旧结论被静默丢弃——这正是它该有的行为，不需要也不应该报错。</remarks>
    public bool Submit(long version, ValidationVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        if (!IsCurrent(version))
        {
            return false;
        }

        _validatedVersion = version;
        _verdict = verdict;
        return true;
    }

    /// <summary>某个版本是否仍然是最新输入。</summary>
    public bool IsCurrent(long version) => version == _version;

    /// <summary>是否已经有针对当前输入的结论。</summary>
    public bool HasFreshVerdict => _validatedVersion == _version;

    /// <summary>是否允许继续（阻断级结论会禁止继续）。</summary>
    /// <remarks>
    /// 还没有结论时<b>允许</b>继续？不——没有结论说明校验还没跑完，
    /// 这时放行等于"没检查就下一步"。因此这里对"尚无结论"返回禁止，
    /// 由界面把按钮置灰并显示"检查中"。
    /// </remarks>
    public bool CanProceed => HasFreshVerdict && !_verdict.IsBlocking;
}
