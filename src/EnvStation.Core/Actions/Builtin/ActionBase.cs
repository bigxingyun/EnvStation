using System.Collections.Immutable;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;

namespace EnvStation.Core.Actions.Builtin;

/// <summary>
/// 官方动作实现基类。
/// </summary>
/// <remarks>
/// <para>
/// 基类做了一件看起来多余但很关键的事：把 <see cref="IAction.ExecuteAsync"/> 显式实现并转发到
/// 强类型重载。这样动作实现里拿到的就是 <see cref="ActionExecutionContext"/>，
/// 而<b>不是</b>接口——因为配额计量、审计、变量表都只在具体类型上可用。
/// 反过来，外部（工作流解释器）仍然只能看到接口，无法绕过执行管线。
/// </para>
/// <para>
/// 动作实现的三条纪律：
/// <list type="number">
///   <item>不做参数解析——参数已由 <see cref="ActionArgumentsBinder"/> 转成强类型。</item>
///   <item>不抛异常表达业务失败——返回 <see cref="ActionResult.Fail"/>。</item>
///   <item>不直接触碰文件系统以外的全局状态，且每个路径参数都必须过 <c>context.GuardPath</c>。</item>
/// </list>
/// </para>
/// </remarks>
internal abstract class ActionBase : IAction
{
    /// <inheritdoc />
    public abstract ActionDescriptor Descriptor { get; }

    /// <inheritdoc />
    ValueTask<ActionResult> IAction.ExecuteAsync(
        IActionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (context is not ActionExecutionContext typed)
        {
            return ValueTask.FromResult(ActionResult.Fail(
                EnvStationErrorCodes.ActionFailed,
                "动作只能在环境站的执行上下文中运行。"));
        }

        return ExecuteAsync(typed, arguments, cancellationToken);
    }

    /// <summary>动作的强类型实现入口。</summary>
    protected abstract ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context,
        ActionArguments arguments,
        CancellationToken cancellationToken);

    // ────────────────────────────── 参数声明辅助 ──────────────────────────────

    /// <summary>声明一个字符串参数。</summary>
    protected static ParameterSpec Str(string name, bool required, string description, int maxLength = 1024, string? defaultValue = null) =>
        new(name, ParameterType.String, required, description, MaxLength: maxLength, DefaultValue: defaultValue);

    /// <summary>声明一个枚举参数。</summary>
    protected static ParameterSpec Enum(string name, bool required, string description, params string[] allowed) =>
        new(name, ParameterType.Enum, required, description, [.. allowed]);

    /// <summary>声明一个布尔参数。</summary>
    protected static ParameterSpec Bool(string name, string description, bool defaultValue) =>
        new(name, ParameterType.Boolean, false, description, DefaultValue: defaultValue ? "true" : "false");

    /// <summary>声明一个整数参数。</summary>
    protected static ParameterSpec Int(string name, string description, long min, long max, long? defaultValue = null) =>
        new(
            name,
            ParameterType.Integer,
            false,
            description,
            Minimum: min,
            Maximum: max,
            DefaultValue: defaultValue?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>声明一个字符串数组参数。</summary>
    protected static ParameterSpec StrArray(string name, bool required, string description) =>
        new(name, ParameterType.StringArray, required, description, MaxLength: 256);

    /// <summary>声明一个路径参数。</summary>
    protected static ParameterSpec Path_(string name, bool required, string description) =>
        new(name, ParameterType.Path, required, description, MaxLength: 512);

    /// <summary>声明一个 URL 参数。</summary>
    protected static ParameterSpec Url(string name, bool required, string description) =>
        new(name, ParameterType.Url, required, description, MaxLength: 2048);

    /// <summary>声明一个 SHA-256 参数。</summary>
    protected static ParameterSpec Sha256(string name, bool required, string description) =>
        new(name, ParameterType.Sha256, required, description, MaxLength: 80);

    /// <summary>声明一个版本约束参数。</summary>
    protected static ParameterSpec VersionRange(string name, bool required, string description) =>
        new(name, ParameterType.VersionRange, required, description, MaxLength: 128);

    // ────────────────────────────── 结果辅助 ──────────────────────────────

    /// <summary>构造成功结果。</summary>
    protected static ValueTask<ActionResult> Ok(
        string message,
        IReadOnlyDictionary<string, string>? outputs = null,
        IEnumerable<string>? touched = null,
        string? reversibleToken = null)
    {
        return ValueTask.FromResult(ActionResult.Ok(message, outputs, touched, reversibleToken));
    }

    /// <summary>
    /// 构造失败结果。<paramref name="remediation"/> 是"该怎么办"——需求 U-4 要求
    /// 任何失败都必须同时给出原因与处置建议，因此把它做成同一方法的一个可选参数，
    /// 避免调用方"忘了写"。
    /// </summary>
    protected static ValueTask<ActionResult> Fail(string code, string message, string? remediation = null) =>
        ValueTask.FromResult(ActionResult.Fail(
            code,
            remediation is { Length: > 0 } ? message + " " + remediation : message));

    /// <summary>
    /// 构造成功结果的<b>同步</b>形式。
    /// </summary>
    /// <remarks>
    /// 异步动作方法里不能写 <c>return Ok(...)</c>——那会返回 <c>ValueTask&lt;ActionResult&gt;</c>
    /// 而方法签名要求 <c>ActionResult</c>（CS4016），并且可能触发 CA2012（ValueTask 被多次使用）。
    /// 异步动作请使用本方法。
    /// </remarks>
    protected static ActionResult OkResult(
        string message,
        IReadOnlyDictionary<string, string>? outputs = null,
        IEnumerable<string>? touched = null,
        string? reversibleToken = null) =>
        ActionResult.Ok(message, outputs, touched, reversibleToken);

    /// <summary>构造失败结果的<b>同步</b>形式（供异步动作方法使用）。</summary>
    protected static ActionResult FailResult(string code, string message, string? remediation = null) =>
        ActionResult.Fail(
            code,
            remediation is { Length: > 0 } ? message + " " + remediation : message);

    /// <summary>把 <see cref="Result{T}"/> 的失败直接转成动作失败。</summary>
    protected static ValueTask<ActionResult> Propagate(EnvStationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = error.Remediation is null ? error.Message : error.Message + " " + error.Remediation;
        return ValueTask.FromResult(ActionResult.Fail(error.Code, message));
    }

    /// <summary>构造输出字典。</summary>
    protected static ImmutableDictionary<string, string> Outputs(params (string Key, string Value)[] pairs)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// 把布尔值渲染为脚本世界的规范写法（小写）。
    /// </summary>
    /// <remarks>
    /// <b>必须用它，不要用 <c>bool.ToString()</c>。</b>
    /// <c>bool.ToString()</c> 得到的是 <c>True</c> / <c>False</c>（首字母大写），
    /// 而工作流里的布尔字面量是小写 <c>true</c> / <c>false</c>。
    /// 两者一旦混用，<c>${x.some_flag} == true</c> 会静默地判为假——
    /// 这是那种"看起来完全正确却永远不生效"的缺陷，极难排查。
    /// </remarks>
    protected static string Bool(bool value) => value ? "true" : "false";
}
