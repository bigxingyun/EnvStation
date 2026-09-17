using System.Collections.Immutable;

namespace EnvStation.Abstractions.Actions;

/// <summary>
/// 动作参数的类型标签。动作参数一律强类型（需求 AC-1），
/// 校验期即完成类型检查，运行期不再接受自由字符串。
/// </summary>
public enum ParameterType
{
    /// <summary>字符串。</summary>
    String,

    /// <summary>整数。</summary>
    Integer,

    /// <summary>布尔值。</summary>
    Boolean,

    /// <summary>字符串枚举（必须落在 <see cref="ParameterSpec.AllowedValues"/> 内）。</summary>
    Enum,

    /// <summary>字符串数组。</summary>
    StringArray,

    /// <summary>语义化版本约束字符串，如 <c>&gt;=3.10</c>。</summary>
    VersionRange,

    /// <summary>绝对路径。</summary>
    Path,

    /// <summary>SHA-256 十六进制摘要（64 位小写十六进制）。</summary>
    Sha256,

    /// <summary>URL（必须为 https，http 需用户显式降级确认）。</summary>
    Url,
}

/// <summary>
/// 动作参数的规格声明。用于校验期强类型检查、UI 表单生成与文档自动生成（需求 M13-4）。
/// </summary>
/// <param name="Name">参数名，小写蛇形（与包脚本书写风格一致）。</param>
/// <param name="Type">参数类型。</param>
/// <param name="Required">是否必填。</param>
/// <param name="Description">中文说明，进入界面提示与动作文档。写这个参数控制什么、各取值含义如何；
/// 用陈述短句，不加句号，取值与名称用「」引用，不写设计动机。</param>
/// <param name="AllowedValues">枚举型参数的取值集合；非枚举参数留空。
/// <b>注意</b>：留空可能是 <c>default</c> 而非 <c>Empty</c>，消费方必须过 <c>OrEmpty()</c>。</param>
/// <param name="DefaultValue">默认值（以字符串表示，按 <paramref name="Type"/> 解析）。</param>
/// <param name="MaxLength">字符串最大长度；0 表示不限制。</param>
/// <param name="Minimum">数值下限（含）；null 表示不限制。</param>
/// <param name="Maximum">数值上限（含）；null 表示不限制。</param>
/// <param name="IsSecret">是否为敏感参数（日志与报告中一律脱敏，需求 CF-6）。</param>
public sealed record ParameterSpec(
    string Name,
    ParameterType Type,
    bool Required,
    string Description,
    ImmutableArray<string> AllowedValues = default,
    string? DefaultValue = null,
    int MaxLength = 0,
    long? Minimum = null,
    long? Maximum = null,
    bool IsSecret = false);

/// <summary>
/// 动作元数据。注册表用它做静态检查，UI 用它展示"这个包会做什么"。
/// </summary>
/// <param name="ActionId">三段式全限定 ID，如 <c>envstation.env.set</c>。</param>
/// <param name="Version">语义化版本。</param>
/// <param name="CapabilityId">所需能力，取值见 <see cref="CapabilityIds"/>。</param>
/// <param name="DisplayName">中文展示名。</param>
/// <param name="IsIdempotent">幂等性声明（需求 AC-2）。</param>
/// <param name="IsParallelSafe">是否允许出现在 <c>parallel</c> 块中（需求 20.5）。</param>
/// <param name="HasInverse">是否提供反向动作（需求 AC-4）。无反向动作在 UI 中红色标注。</param>
/// <param name="RequiresUserPresence">是否需要用户在场（无人值守模式将被拒绝）。</param>
/// <param name="DefaultTimeoutSeconds">默认超时秒数（需求 AC-5）。</param>
/// <param name="TouchedResources">副作用声明（需求 AC-3）。</param>
/// <param name="Parameters">参数规格。</param>
/// <param name="ConditionalCapabilities">
/// <b>按参数取值才需要</b>的附加能力。典型例子：<c>envstation.env.set</c> 的基础能力是
/// <c>CAP.ENV.USER</c>，但当 <c>scope = machine</c> 时还需要 <c>CAP.ENV.MACHINE</c>。
/// <para>
/// 能力授权按<b>最坏情况</b>展示（基础能力 ∪ 全部条件能力），用户据此看到这个包最多能做什么；
/// 实际执行时由动作按参数取值判断，未授权即拒绝执行。
/// </para>
/// </param>
/// <param name="DegradesWhenUnattended">
/// 无人值守时该动作能否自行降级（例如「有默认值就用默认值，没有才失败」）。
/// <para>
/// <b>默认 false</b>：执行管线在无人值守下直接拒绝需要用户在场的动作。动作作者必须<b>显式</b>
/// 声明「无人值守时怎么办」才能接管这段逻辑；默认放行会让忘了处理无人值守的动作
/// 拿到空字符串或意外值继续往下跑。
/// </para>
/// <para>
/// 声明为 true 的动作必须自己保证：无人值守时要么用默认值并留痕，要么明确失败，
/// <b>不能静默使用可能不对的值</b>。
/// </para>
/// </param>
public sealed record ActionDescriptor(
    string ActionId,
    string Version,
    string CapabilityId,
    string DisplayName,
    bool IsIdempotent,
    bool IsParallelSafe,
    bool HasInverse,
    bool RequiresUserPresence,
    int DefaultTimeoutSeconds,
    ImmutableArray<string> TouchedResources,
    ImmutableArray<ParameterSpec> Parameters,
    ImmutableArray<string> ConditionalCapabilities = default,
    bool DegradesWhenUnattended = false);

/// <summary>
/// 动作执行结果。契约要求"失败也是返回值"——不允许用异常表达可预期的业务失败。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="ErrorCode">稳定错误码，形如 <c>E_ENV_SCOPE_DENIED</c>（需求 AC-6）。</param>
/// <param name="Message">中文用户可见文案（需求 AC-8）。</param>
/// <param name="Outputs">结构化输出，供工作流 <c>register</c> 绑定为变量。</param>
/// <param name="TouchedPaths">本次实际触碰的路径（用于审计与回滚账本）。</param>
/// <param name="ReversibleToken">反向操作所需的凭据（如快照 ID、备份 ID）；无反向操作时为 null。</param>
public sealed record ActionResult(
    bool Success,
    string ErrorCode,
    string Message,
    ImmutableDictionary<string, string> Outputs,
    ImmutableArray<string> TouchedPaths,
    string? ReversibleToken)
{
    /// <summary>构造一个成功结果。</summary>
    public static ActionResult Ok(
        string message,
        IReadOnlyDictionary<string, string>? outputs = null,
        IEnumerable<string>? touchedPaths = null,
        string? reversibleToken = null) =>
        new(true,
            EnvStationErrorCodes.Ok,
            message,
            outputs is null
                ? ImmutableDictionary<string, string>.Empty
                : ImmutableDictionary.CreateRange(StringComparer.Ordinal, outputs),
            touchedPaths is null ? ImmutableArray<string>.Empty : [.. touchedPaths],
            reversibleToken);

    /// <summary>构造一个失败结果。</summary>
    public static ActionResult Fail(string errorCode, string message) =>
        new(false,
            errorCode,
            message,
            ImmutableDictionary<string, string>.Empty,
            ImmutableArray<string>.Empty,
            null);
}

/// <summary>
/// 已解析的动作参数包。参数在进入动作前已完成类型转换与范围检查，
/// 动作实现内部不再做字符串解析（这是"防注入"的基础，需求 S6 / AI-1）。
/// </summary>
public sealed class ActionArguments
{
    private readonly ImmutableDictionary<string, object?> _values;

    /// <summary>由已类型化的键值对构造。</summary>
    public ActionArguments(IEnumerable<KeyValuePair<string, object?>> values) =>
        _values = values.ToImmutableDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.Ordinal);

    /// <summary>空参数包。</summary>
    public static ActionArguments Empty { get; } = new([]);

    /// <summary>参数名集合。</summary>
    public IEnumerable<string> Names => _values.Keys;

    /// <summary>是否包含指定参数。</summary>
    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>读取字符串参数；缺失时返回 <paramref name="fallback"/>。</summary>
    public string? GetString(string name, string? fallback = null) =>
        _values.TryGetValue(name, out var v) && v is string s ? s : fallback;

    /// <summary>读取必填字符串参数；缺失时返回 null（调用方应已在校验期拦截）。</summary>
    public string? GetRequiredString(string name) => GetString(name);

    /// <summary>读取整数参数；缺失或类型不符时返回 <paramref name="fallback"/>。</summary>
    public long GetInt64(string name, long fallback = 0) =>
        _values.TryGetValue(name, out var v) && v is long l ? l : fallback;

    /// <summary>读取布尔参数；缺失或类型不符时返回 <paramref name="fallback"/>。</summary>
    public bool GetBoolean(string name, bool fallback = false) =>
        _values.TryGetValue(name, out var v) && v is bool b ? b : fallback;

    /// <summary>读取字符串数组参数；缺失时返回空数组。</summary>
    /// <remarks>
    /// 返回前统一过 <see cref="ImmutableArrayExtensions.OrEmpty{T}"/>：
    /// 参数值可能来自工作流解析（集合表达式产生的 default 也是"空"）。
    /// </remarks>
    public ImmutableArray<string> GetStringArray(string name) =>
        _values.TryGetValue(name, out var v) && v is ImmutableArray<string> a
            ? a.OrEmpty()
            : ImmutableArray<string>.Empty;

    /// <summary>枚举全部键值对（审计脱敏前使用）。</summary>
    public IEnumerable<KeyValuePair<string, object?>> Pairs => _values;

    /// <summary>尝试取出原始值。</summary>
    public bool TryGetValue(string name, out object? value) => _values.TryGetValue(name, out value);
}

/// <summary>
/// 动作执行上下文。动作只能通过它接触外部世界——没有它就没有任何系统访问能力。
/// </summary>
public interface IActionContext
{
    /// <summary>当前包 ID（用于隔离与私有目录定位）。</summary>
    string PackageId { get; }

    /// <summary>本次包运行的唯一 ID（贯穿审计日志）。</summary>
    string RunId { get; }

    /// <summary>本次运行被授予的能力集合。动作执行前会再次核对（需求 A2）。</summary>
    CapabilitySet GrantedCapabilities { get; }

    /// <summary>包被允许访问的根目录集合。所有路径参数必须落在其中（需求 ISO-1）。</summary>
    ImmutableArray<string> AuthorizedRoots { get; }

    /// <summary>变量表（<c>pkg.*</c> / <c>user.*</c> / <c>sys.*</c> / <c>env.*</c> / <c>secret.*</c>）。</summary>
    IVariableScope Variables { get; }

    /// <summary>进度上报回调（0~100 + 文案）。</summary>
    void ReportProgress(int percent, string? message);

    /// <summary>记录一条审计事件。</summary>
    void Audit(string eventName, IReadOnlyDictionary<string, string> fields);

    /// <summary>路径守卫：校验并规范化一个路径参数，越界即返回失败。</summary>
    Result<string> GuardPath(string rawPath, string parameterName);
}

/// <summary>
/// 变量作用域。五种作用域的可见性规则见需求 20.7；
/// <c>secret.*</c> 仅存内存，实现必须保证其不会出现在日志、报告与异常消息中。
/// </summary>
public interface IVariableScope
{
    /// <summary>按 <c>作用域.名称</c> 形式读取变量；不存在返回 null。</summary>
    string? Get(string qualifiedName);

    /// <summary>判断变量是否存在。</summary>
    bool Exists(string qualifiedName);

    /// <summary>写入包私有变量（<c>pkg.*</c>）。其他作用域由宿主写入，包不可写。</summary>
    void SetPackageVariable(string name, string value);
}

/// <summary>
/// 预制动作契约（需求 AC-1 ~ AC-10 的代码化表达）。
/// </summary>
/// <remarks>
/// 实现约束（不可协商）：
/// <list type="bullet">
///   <item>动作 ID 必须为官方命名空间 <c>envstation.*</c>；第三方动作使用 <c>x-&lt;作者&gt;.*</c>。</item>
///   <item>动作实现<b>不得</b>接受自由命令行字符串，不得调用 <c>Process.Start</c> 拼接参数。</item>
///   <item>动作实现只通过 <see cref="IActionContext"/> 接触系统，不得直接访问注册表之外的全局状态。</item>
/// </list>
/// </remarks>
public interface IAction
{
    /// <summary>动作元数据。</summary>
    ActionDescriptor Descriptor { get; }

    /// <summary>执行动作。必须尊重 <paramref name="cancellationToken"/>，超时由调用方控制。</summary>
    ValueTask<ActionResult> ExecuteAsync(
        IActionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken);
}
