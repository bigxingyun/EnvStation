using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Actions;

namespace EnvStation.Core.Workflow;

/// <summary>
/// 变量插值器：把参数值里的 <c>${...}</c> 替换为变量的实际取值。
/// </summary>
/// <remarks>
/// <para>
/// <b>插值结果永远是"数据"，永远不会变成"代码"（需求 S6 / AI-2）。</b>
/// 这一点值得展开说明，因为它是本项目"防注入"设计里最容易被做错的一环：
/// </para>
/// <list type="bullet">
///   <item>插值只发生在<b>参数值</b>位置。动作 ID、参数名、控制流关键字都不参与插值
///         （<see cref="ActionReference.Parse"/> 会直接拒绝含 <c>${</c> 的动作 ID）。</item>
///   <item>插值的产物是一个字符串值，被交给 <see cref="ActionArgumentsBinder"/> 做类型转换，
///         然后作为<b>已类型化的参数</b>交给动作。全程没有任何一步会把它拼进命令行——
///         需要运行程序的动作用的是 <c>ArgumentList</c>（数组），不是命令行字符串。</item>
///   <item>因此即便变量值里含 <c>&amp; calc.exe</c> 或 <c>| rm -rf</c>，
///         它最坏也只是"一个奇怪的文件名"，不会被执行。</item>
/// </list>
/// <para>
/// <b>变量不存在时一律失败，不替换为空串。</b>
/// 空串替换会让"我拼错了变量名"表现为"参数变成了空"，进而产生难以理解的下游错误；
/// 直接报错才能让包作者立刻发现。
/// </para>
/// </remarks>
public static class VariableInterpolator
{
    /// <summary>单个插值结果的最大长度。</summary>
    public const int MaxInterpolatedLength = 32_000;

    /// <summary>单次插值允许的最大替换次数（防止用超长模板放大内存占用）。</summary>
    public const int MaxReplacements = 256;

    /// <summary>列表变量在无换行时的分隔符。</summary>
    public const char ListSeparator = ';';

    /// <summary>
    /// 对脚本值做插值。字符串做替换；数组逐元素替换；其余类型原样返回。
    /// </summary>
    public static Result<ScriptValue> Interpolate(ScriptValue value, VariableTable variables)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(variables);

        switch (value)
        {
            case ScriptString text:
                var replaced = InterpolateText(text.Value, variables);
                return replaced.IsFailure
                    ? replaced.Propagate<ScriptValue>()
                    : Result<ScriptValue>.Ok(new ScriptString(replaced.Value, text.Line));

            case ScriptArray array:
                {
                    var builder = ImmutableArray.CreateBuilder<ScriptValue>(array.Items.Length);
                    foreach (var item in array.Items)
                    {
                        var result = Interpolate(item, variables);
                        if (result.IsFailure)
                        {
                            return result;
                        }

                        builder.Add(result.Value);
                    }

                    return Result<ScriptValue>.Ok(new ScriptArray(builder.ToImmutable(), array.Line));
                }

            default:
                return Result<ScriptValue>.Ok(value);
        }
    }

    /// <summary>对一段文本做插值。</summary>
    public static Result<string> InterpolateText(string template, VariableTable variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        if (!template.Contains("${", StringComparison.Ordinal))
        {
            // 没有变量引用的常见情况直接返回（避免无谓的 StringBuilder 分配）。
            return Result<string>.Ok(Unescape(template));
        }

        var builder = new StringBuilder(template.Length + 32);
        var replacements = 0;
        var index = 0;

        while (index < template.Length)
        {
            var start = template.IndexOf("${", index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(Unescape(template[index..]));
                break;
            }

            // 转义：$${ 表示字面量 ${
            if (start > 0 && template[start - 1] == '$' && (start < 2 || template[start - 2] != '$'))
            {
                builder.Append(Unescape(template[index..(start - 1)]));
                builder.Append("${");
                index = start + 2;
                continue;
            }

            builder.Append(Unescape(template[index..start]));

            var end = template.IndexOf('}', start + 2);
            if (end < 0)
            {
                return Result<string>.Fail(
                    EnvStationErrorCodes.PackageParseFailed,
                    $"参数值中的变量引用缺少右花括号：{Truncate(template)}",
                    "变量插值必须写成 ${变量名} 形式；需要输出字面的 ${ 时写成 $${。");
            }

            var name = template[(start + 2)..end].Trim();
            if (name.Length == 0)
            {
                return Result<string>.Fail(
                    EnvStationErrorCodes.PackageParseFailed,
                    "参数值中存在空的变量引用 ${}。");
            }

            if (!variables.Exists(name))
            {
                return Result<string>.Fail(
                    EnvStationErrorCodes.WorkflowVariableUndefined,
                    $"变量 {name} 未定义，无法完成参数插值。",
                    "常见原因：引用了尚未 register 的步骤输出（步骤顺序不对），或变量名拼写有误。" +
                    " 可用的变量名见预演输出。");
            }

            builder.Append(variables.Get(name));

            if (++replacements > MaxReplacements)
            {
                return Result<string>.Fail(
                    EnvStationErrorCodes.WorkflowExpressionFailed,
                    $"参数值中的变量引用超过 {MaxReplacements} 处，已中止。");
            }

            if (builder.Length > MaxInterpolatedLength)
            {
                return Result<string>.Fail(
                    EnvStationErrorCodes.EnvValueTooLong,
                    $"插值后的参数值长度超过 {MaxInterpolatedLength} 字符，已中止。",
                    "常见原因：把一个大文件的内容当作变量传入了参数。");
            }

            index = end + 1;
        }

        return Result<string>.Ok(builder.ToString());
    }

    /// <summary>
    /// 解析 <c>foreach</c> 的数据源为一个列表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>约定：列表变量以换行分隔；若整个值只有一行，则以分号分隔。</b>
    /// 之所以要定一个约定，是因为环境变量与动作输出本质上都是字符串，
    /// 而"一个字符串到底代表一个值还是一个列表"必须由标准明确规定，
    /// 否则同一份包在不同实现下会得到不同结果（这正是需求 STD-1 要防的）。
    /// </para>
    /// <para>选择这两个分隔符的理由：换行是"多行文本"的天然分界；
    /// 分号与 PATH 的写法一致，是 Windows 用户最熟悉的列表分隔符。</para>
    /// </remarks>
    public static Result<ImmutableArray<string>> ResolveList(string source, VariableTable variables)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(variables);

        var trimmed = source.Trim();
        if (trimmed.Length == 0)
        {
            return Result<ImmutableArray<string>>.Fail(
                EnvStationErrorCodes.WorkflowExpressionFailed,
                "foreach 的数据源为空。");
        }

        if (!trimmed.StartsWith("${", StringComparison.Ordinal) || !trimmed.EndsWith('}'))
        {
            return Result<ImmutableArray<string>>.Fail(
                EnvStationErrorCodes.WorkflowExpressionFailed,
                $"foreach 的数据源必须是变量引用（形如 ${{items}}），实际为：{Truncate(trimmed)}",
                "foreach 的数据集必须来自变量，不能是内联字面量：循环次数要在静态检查阶段确定。");
        }

        var interpolated = InterpolateText(trimmed, variables);
        if (interpolated.IsFailure)
        {
            return interpolated.Propagate<ImmutableArray<string>>();
        }

        var value = interpolated.Value;
        var parts = value.Contains('\n', StringComparison.Ordinal)
            ? value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : value.Split(ListSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return Result<ImmutableArray<string>>.Ok([.. parts.Where(static p => p.Length > 0)]);
    }

    private static string Unescape(string text) =>
        text.Contains("$$", StringComparison.Ordinal)
            ? text.Replace("$${", "${", StringComparison.Ordinal)
            : text;

    private static string Truncate(string text) => text.Length <= 120 ? text : text[..120] + "…";

    /// <summary>把列表渲染为约定的字符串形式（供测试与文档生成使用）。</summary>
    public static string FormatList(IEnumerable<string> items) =>
        string.Join('\n', items.Select(static i => i.Replace("\n", " ", StringComparison.Ordinal)));

    /// <summary>把数字渲染为不变文化字符串。</summary>
    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
