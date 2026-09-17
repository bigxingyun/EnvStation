using System.Collections.Immutable;
using System.Globalization;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Packages;

namespace EnvStation.Core.Actions;

/// <summary>绑定后的动作参数：类型化参数包 + 敏感参数名单（供审计脱敏）。</summary>
/// <param name="Arguments">类型化参数。</param>
/// <param name="SecretNames">被标记为敏感的参数名。</param>
/// <param name="UsedDefaults">本次由默认值补齐的参数名（预演要标出来）。</param>
public sealed record BoundArguments(
    ActionArguments Arguments,
    ImmutableHashSet<string> SecretNames,
    ImmutableArray<string> UsedDefaults);

/// <summary>
/// 动作参数绑定器：把工作流里的脚本值按动作的参数规格转成强类型参数。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有这一层</b>：需求 AC-1 要求"类型错误在校验期就被拒绝，不留到运行期"。
/// 这一层是校验期与运行期的分界线——通过之后，动作实现内部不再做任何字符串解析，
/// 也就没有任何"把用户数据解析成命令"的机会（需求 S6 / AI-1）。
/// </para>
/// <para>
/// <b>刻意严格的地方</b>：
/// <list type="bullet">
///   <item><b>未知参数名直接报错</b>。写错参数名（如把 <c>mirror_of</c> 写成 <c>mirrorOf</c>）
///         是包作者最常见的错误；静默忽略会让包"看起来生效了但什么都没做"。</item>
///   <item><b>标量可以隐式转字符串，但数组不行</b>。标量转字符串是无损且直觉的；
///         数组转字符串必然要选一个分隔符，那属于语义决策，必须由动作显式定义。</item>
///   <item><b>枚举必须落在允许集合内</b>；枚举未声明允许集合时视为动作自身的缺陷，直接阻断。</item>
/// </list>
/// </para>
/// </remarks>
public static class ActionArgumentsBinder
{
    /// <summary>单次绑定允许的最大参数个数，防止用海量参数做拒绝服务。</summary>
    public const int MaxParameterCount = 64;

    /// <summary>
    /// 执行绑定与校验。
    /// </summary>
    /// <param name="descriptor">动作元数据。</param>
    /// <param name="values">工作流提供的原始值（已完成变量插值）。</param>
    /// <param name="findings">发现收集器。</param>
    /// <param name="location">定位信息（如 <c>workflow.toml:42</c>）。</param>
    public static Result<BoundArguments> Bind(
        ActionDescriptor descriptor,
        IReadOnlyDictionary<string, ScriptValue> values,
        FindingBag findings,
        string? location = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(findings);

        if (values.Count > MaxParameterCount)
        {
            findings.Block(
                EnvStationErrorCodes.ActionArgumentInvalid,
                "参数数量过多",
                $"动作 {descriptor.ActionId} 收到 {values.Count} 个参数，超过上限 {MaxParameterCount}。",
                null,
                location,
                descriptor.ActionId);
            return Result<BoundArguments>.Fail(EnvStationErrorCodes.ActionArgumentInvalid, "参数数量过多。");
        }

        var specs = descriptor.Parameters.OrEmpty().ToImmutableDictionary(static p => p.Name, StringComparer.Ordinal);
        var bound = new Dictionary<string, object?>(StringComparer.Ordinal);
        var secrets = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var defaults = ImmutableArray.CreateBuilder<string>();
        var hadError = false;

        // ① 未知参数名
        foreach (var key in values.Keys)
        {
            if (!specs.ContainsKey(key))
            {
                hadError = true;
                findings.Block(
                    EnvStationErrorCodes.ActionArgumentInvalid,
                    "动作收到未知参数",
                    $"动作 {descriptor.ActionId} 不接受参数 {key}。",
                    specs.Count == 0
                        ? "该动作不接受任何参数，删除 with 块。"
                        : $"该动作支持的参数：{string.Join("、", specs.Keys.OrderBy(static s => s, StringComparer.Ordinal))}。检查拼写是否有误。",
                    location,
                    $"{descriptor.ActionId}.{key}");
            }
        }

        // ② 逐个参数绑定
        foreach (var spec in descriptor.Parameters.OrEmpty())
        {
            if (values.TryGetValue(spec.Name, out var value))
            {
                var converted = ConvertValue(descriptor, spec, value, findings, location);
                if (converted.IsFailure)
                {
                    hadError = true;
                    continue;
                }

                bound[spec.Name] = converted.Value;
                if (spec.IsSecret)
                {
                    secrets.Add(spec.Name);
                }

                continue;
            }

            if (spec.DefaultValue is not null)
            {
                var defaulted = ConvertDefault(descriptor, spec, findings, location);
                if (defaulted.IsFailure)
                {
                    hadError = true;
                    continue;
                }

                bound[spec.Name] = defaulted.Value;
                defaults.Add(spec.Name);
                if (spec.IsSecret)
                {
                    secrets.Add(spec.Name);
                }

                continue;
            }

            if (spec.Required)
            {
                hadError = true;
                findings.Block(
                    EnvStationErrorCodes.ActionArgumentInvalid,
                    "缺少必填参数",
                    $"动作 {descriptor.ActionId} 缺少必填参数 {spec.Name}：{spec.Description}",
                    null,
                    location,
                    $"{descriptor.ActionId}.{spec.Name}");
            }
        }

        if (hadError)
        {
            return Result<BoundArguments>.Fail(
                EnvStationErrorCodes.ActionArgumentInvalid,
                $"动作 {descriptor.ActionId} 的参数未通过校验。");
        }

        return Result<BoundArguments>.Ok(new BoundArguments(
            new ActionArguments(bound),
            secrets.ToImmutable(),
            defaults.ToImmutable()));
    }

    private static Result<object?> ConvertValue(
        ActionDescriptor descriptor,
        ParameterSpec spec,
        ScriptValue value,
        FindingBag findings,
        string? location)
    {
        switch (spec.Type)
        {
            case ParameterType.String:
            case ParameterType.VersionRange:
            case ParameterType.Path:
            case ParameterType.Url:
            case ParameterType.Sha256:
            case ParameterType.Enum:
                {
                    var text = AsText(value);
                    if (text is null)
                    {
                        return Fail(descriptor, spec, value, findings, location, "字符串");
                    }

                    if (spec.MaxLength > 0 && text.Length > spec.MaxLength)
                    {
                        return Fail(
                            descriptor, spec, value, findings, location,
                            $"长度不超过 {spec.MaxLength} 的字符串（当前 {text.Length} 字符）");
                    }

                    if (spec is { Type: ParameterType.Enum } && spec.AllowedValues.OrEmpty().Length == 0)
                    {
                        findings.Block(
                            "ACT-08",
                            "枚举参数未声明允许值",
                            $"动作 {descriptor.ActionId} 的参数 {spec.Name} 声明为枚举，但未声明 AllowedValues。",
                            "这是动作自身的缺陷，提交反馈。",
                            location,
                            $"{descriptor.ActionId}.{spec.Name}");
                        return Result<object?>.Fail(EnvStationErrorCodes.ActionArgumentInvalid, "枚举参数未声明允许值。");
                    }

                    if (spec.Type == ParameterType.Enum
                        && !spec.AllowedValues.OrEmpty().Contains(text, StringComparer.Ordinal))
                    {
                        return Fail(
                            descriptor, spec, value, findings, location,
                            $"允许的取值：{string.Join("、", spec.AllowedValues.OrEmpty())}");
                    }

                    if (spec.Type == ParameterType.Sha256 && ActionRegistry.NormalizeHash(text) is null)
                    {
                        return Fail(descriptor, spec, value, findings, location, "sha256:<64 位小写十六进制> 形式的哈希");
                    }

                    if (spec.Type == ParameterType.Url && !IsAcceptableUrl(text, out var urlReason))
                    {
                        return Fail(descriptor, spec, value, findings, location, urlReason);
                    }

                    if (spec.Type is ParameterType.String or ParameterType.Path && text.Length == 0)
                    {
                        return Fail(descriptor, spec, value, findings, location, "非空字符串");
                    }

                    return Result<object?>.Ok(text);
                }

            case ParameterType.Integer:
                {
                    if (value is not ScriptInteger number)
                    {
                        return Fail(descriptor, spec, value, findings, location, "整数");
                    }

                    if (spec.Minimum is { } min && number.Value < min)
                    {
                        return Fail(descriptor, spec, value, findings, location, $"不小于 {min} 的整数");
                    }

                    if (spec.Maximum is { } max && number.Value > max)
                    {
                        return Fail(descriptor, spec, value, findings, location, $"不大于 {max} 的整数");
                    }

                    return Result<object?>.Ok(number.Value);
                }

            case ParameterType.Boolean:
                return value is ScriptBoolean b
                    ? Result<object?>.Ok(b.Value)
                    : Fail(descriptor, spec, value, findings, location, "布尔值 true / false");

            case ParameterType.StringArray:
                {
                    if (value is not ScriptArray array)
                    {
                        return Fail(descriptor, spec, value, findings, location, "字符串数组");
                    }

                    var items = ImmutableArray.CreateBuilder<string>(array.Items.Length);
                    foreach (var item in array.Items)
                    {
                        var text = AsText(item);
                        if (text is null)
                        {
                            return Fail(descriptor, spec, value, findings, location, "只包含字符串的数组");
                        }

                        items.Add(text);
                    }

                    return Result<object?>.Ok(items.ToImmutable());
                }

            default:
                return Fail(descriptor, spec, value, findings, location, "受支持的类型");
        }
    }

    private static Result<object?> ConvertDefault(
        ActionDescriptor descriptor,
        ParameterSpec spec,
        FindingBag findings,
        string? location)
    {
        var raw = spec.DefaultValue!;
        ScriptValue value = spec.Type switch
        {
            ParameterType.Integer => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? new ScriptInteger(l)
                : new ScriptString(raw),
            ParameterType.Boolean => bool.TryParse(raw, out var b) ? new ScriptBoolean(b) : new ScriptString(raw),
            ParameterType.StringArray => new ScriptArray(ImmutableArray<ScriptValue>.Empty),
            _ => new ScriptString(raw),
        };

        var result = ConvertValue(descriptor, spec, value, findings, location);
        if (result.IsFailure)
        {
            findings.Block(
                "ACT-09",
                "动作默认值不合法",
                $"动作 {descriptor.ActionId} 的参数 {spec.Name} 的默认值「{raw}」不符合其类型要求。",
                "这是动作自身的缺陷，提交反馈。",
                location,
                $"{descriptor.ActionId}.{spec.Name}");
        }

        return result;
    }

    private static Result<object?> Fail(
        ActionDescriptor descriptor,
        ParameterSpec spec,
        ScriptValue value,
        FindingBag findings,
        string? location,
        string expectation)
    {
        var actual = value is ScriptString s
            ? $"字符串「{Truncate(s.Value)}」"
            : $"{value.TypeName} {Truncate(value.ToDisplayString())}";

        findings.Block(
            EnvStationErrorCodes.ActionArgumentInvalid,
            "参数类型或取值不合法",
            $"动作 {descriptor.ActionId} 的参数 {spec.Name} 期望{expectation}，实际是{actual}。",
            spec.Description,
            location ?? (value.Line > 0 ? $"workflow.toml:{value.Line}" : null),
            $"{descriptor.ActionId}.{spec.Name}");

        return Result<object?>.Fail(EnvStationErrorCodes.ActionArgumentInvalid, "参数不合法。");
    }

    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static string? AsText(ScriptValue value) => value switch
    {
        ScriptString s => s.Value,
        ScriptInteger i => i.Value.ToString(CultureInfo.InvariantCulture),
        ScriptFloat f => f.Value.ToString("R", CultureInfo.InvariantCulture),
        ScriptBoolean b => b.Value ? "true" : "false",
        _ => null,
    };

    private static bool IsAcceptableUrl(string text, out string reason)
    {
        reason = string.Empty;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            reason = "绝对 URL，例如 https://mirrors.aliyun.com/simple";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            // http 不直接拒绝：内网私服常常只有 http。但必须由用户显式确认降级（需求 MS-2 / PN-1），
            // 该确认在授权阶段完成，这里只放行并让上层标注风险。
            return true;
        }

        reason = "http 或 https 协议的 URL";
        return false;
    }
}
