using System.Diagnostics.CodeAnalysis;

namespace EnvStation.Abstractions;

/// <summary>
/// 无返回值的成功标记类型（Unit 模式）。
/// 用于让"只关心成功/失败"的操作也能复用 <see cref="Result{T}"/>，避免出现 <c>Result&lt;object&gt;</c> 这类噪音。
/// </summary>
public readonly struct Unit
{
    public static readonly Unit Value = default;

    public override string ToString() => "()";
}

/// <summary>
/// 业务结果类型。设计约定（AP-4）：业务失败用 <see cref="Result{T}"/> 表达，
/// 不用异常做流程控制；异常仅用于意外（真正的 bug）。
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(bool ok, T? value, EnvStationError? error)
    {
        IsSuccess = ok;
        _value = value;
        Error = error;
    }

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    public EnvStationError? Error { get; }

    /// <summary>成功时取值；失败时抛出（调用方应先判 <see cref="IsSuccess"/>）。</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"结果失败，无法取值：{Error?.Code}。");

    public static Result<T> Ok(T value) => new(true, value, null);

    public static Result<T> Fail(EnvStationError error) => new(false, default, error);

    public static Result<T> Fail(string code, string message, string? remediation = null, object? detail = null)
        => new(false, default, new EnvStationError(code, message, remediation, detail));

    /// <summary>把失败结果映射为另一种载荷类型，保留错误；成功时抛（不应发生）。</summary>
    public Result<TOther> Propagate<TOther>()
        => IsFailure
            ? Result<TOther>.Fail(Error)
            : throw new InvalidOperationException("只有失败结果才需要传播。");

    /// <summary>丢弃载荷，只保留成败语义。</summary>
    public Result<Unit> Discard()
        => IsSuccess ? Result<Unit>.Ok(Unit.Value) : Result<Unit>.Fail(Error);

    /// <summary>成功时映射载荷，失败时原样传播。</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> mapper)
        => IsSuccess ? Result<TOut>.Ok(mapper(_value!)) : Result<TOut>.Fail(Error);

    public override string ToString() => IsSuccess ? $"Ok({_value})" : $"Fail({Error})";
}

/// <summary>
/// 无载荷结果的便捷入口。
/// <para>
/// <b>命名说明</b>：刻意叫 <c>Results</c> 而不是 <c>Result</c>——因为 <see cref="Result{T}"/> 是泛型类型，
/// 若再声明一个同名的非泛型静态类 <c>Result</c>，C# 的名称查找会优先命中非泛型类，
/// 导致 <c>Result&lt;Unit&gt;</c> 被解析为"对静态类做泛型引用"而报 CS8898 / CS0246。
/// </para>
/// </summary>
public static class Results
{
    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);

    /// <summary>无载荷成功。</summary>
    public static Result<Unit> Ok() => Result<Unit>.Ok(Unit.Value);

    public static Result<T> Fail<T>(string code, string message, string? remediation = null, object? detail = null)
        => Result<T>.Fail(code, message, remediation, detail);

    /// <summary>无载荷失败。</summary>
    public static Result<Unit> Fail(string code, string message, string? remediation = null, object? detail = null)
        => Result<Unit>.Fail(code, message, remediation, detail);
}

/// <summary>
/// 结构化错误。含用户可见要素：现象（Message）、怎么办（Remediation）。
/// 原因与影响由调用方在 UI 层拼装（对应设计原则 DP-6 / 需求 U-4）。
/// </summary>
public sealed record EnvStationError(
    string Code,
    string Message,
    string? Remediation = null,
    object? Detail = null)
{
    /// <summary>关联 ID：贯穿 UI→Application→Core→Helper→Host，便于定位（OBS-D）。</summary>
    public string? CorrelationId { get; init; }

    /// <summary>本地化资源键，供 UI 取多语言文案（需求 NFR-I1）。</summary>
    public string MessageKey => $"Error.{Code}";

    public override string ToString()
        => Remediation is null ? $"{Code}: {Message}" : $"{Code}: {Message}（建议：{Remediation}）";
}
