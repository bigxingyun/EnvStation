using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Packages;

namespace EnvStation.Core.Scripting;

/// <summary>表达式求值结果值的种类。</summary>
public enum ExprKind
{
    /// <summary>变量不存在。与"空字符串"刻意区分：前者是错误来源，后者是合法数据。</summary>
    Missing = 0,

    /// <summary>布尔值。</summary>
    Boolean = 1,

    /// <summary>数值（整数与浮点统一为 double 参与比较）。</summary>
    Number = 2,

    /// <summary>字符串。</summary>
    String = 3,
}

/// <summary>表达式求值得到的值。</summary>
/// <param name="Kind">值的种类。</param>
/// <param name="Boolean">布尔载荷。</param>
/// <param name="Number">数值载荷。</param>
/// <param name="Text">字符串载荷。</param>
public readonly record struct ExprValue(ExprKind Kind, bool Boolean, double Number, string? Text)
{
    /// <summary>缺失值。</summary>
    public static ExprValue Missing { get; } = new(ExprKind.Missing, false, 0, null);

    /// <summary>由布尔构造。</summary>
    public static ExprValue FromBoolean(bool value) => new(ExprKind.Boolean, value, value ? 1 : 0, null);

    /// <summary>由数值构造。</summary>
    public static ExprValue FromNumber(double value) => new(ExprKind.Number, value != 0, value, null);

    /// <summary>由字符串构造。</summary>
    public static ExprValue FromString(string value)
    {
        // 刻意不做"字符串自动转布尔/数字"：隐式类型转换是配置类脚本最常见的坑。
        // 需要转换时由内置函数显式完成。
        return new ExprValue(ExprKind.String, value.Length > 0, 0, value);
    }

    /// <summary>转成人类可读文本（用于插值与报告）。</summary>
    public string Render() => Kind switch
    {
        ExprKind.Boolean => Boolean ? "true" : "false",
        ExprKind.Number => Number == Math.Floor(Number) && Math.Abs(Number) < 1e15
            ? ((long)Number).ToString(CultureInfo.InvariantCulture)
            : Number.ToString("R", CultureInfo.InvariantCulture),
        ExprKind.String => Text ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>真值判定：仅布尔值可直接作为条件。字符串与数字必须显式比较，避免"非空字符串即真"这类隐蔽 bug。</summary>
    public bool? AsCondition() => Kind switch
    {
        ExprKind.Boolean => Boolean,
        ExprKind.Missing => null,
        _ => null,
    };

    public override string ToString() => Kind == ExprKind.Missing ? "<未定义>" : Render();
}

/// <summary>变量解析器接口。表达式与插值都通过它读取变量，从而实现作用域隔离（需求 20.7）。</summary>
public interface IVariableResolver
{
    /// <summary>按限定名（如 <c>py.found</c>）读取变量；不存在返回 <see cref="ExprValue.Missing"/>。</summary>
    ExprValue Resolve(string qualifiedName);
}

/// <summary>
/// 受限表达式语言（需求 20.6）。
/// </summary>
/// <remarks>
/// <para>
/// <b>安全设计的关键一步：变量引用是语法节点，不是文本替换。</b>
/// 一种常见做法是先把 <c>${var}</c> 文本替换进表达式字符串再解析——那样
/// 只要变量值来自用户输入（<c>ui.prompt</c>），用户就能把 <c>a == b or true</c> 之类的文本注入表达式，
/// 从而绕过条件判断。本实现把 <c>${...}</c> 解析为独立的变量节点，
/// 变量值只能作为"值"参与比较，永远无法变成"代码"（对应需求 S6 / AI-3 / TH-1）。
/// </para>
/// <para>
/// <b>表达能力刻意受限</b>：没有赋值、没有函数定义、没有成员访问穿透、没有 I/O、没有循环。
/// 唯一可能耗时的操作是 <c>matches()</c>，它被限制为非回溯正则并带独立超时。
/// </para>
/// </remarks>
public static class ExpressionEvaluator
{
    /// <summary>表达式最大长度。超过即判为错误构造。</summary>
    public const int MaxExpressionLength = 1024;

    /// <summary>AST 节点数上限，防止用超长链式表达式消耗 CPU。</summary>
    public const int MaxNodeCount = 256;

    /// <summary>正则模式最大长度。</summary>
    public const int MaxPatternLength = 200;

    /// <summary>单个正则的匹配超时（需求 20.6：限制复杂度与长度，防 ReDoS）。</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(20);

    /// <summary>整体求值超时（需求 20.6：≤ 50ms）。</summary>
    public static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>允许调用的内置纯函数。</summary>
    public static ImmutableArray<string> BuiltinFunctions { get; } =
    [
        "exists", "contains", "matches", "len", "semver_gt", "semver_satisfies", "path_join", "lower", "upper", "trim", "starts_with", "ends_with",
    ];

    /// <summary>编译并缓存表达式。失败时返回中文错误。</summary>
    public static Result<CompiledExpression> Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Result<CompiledExpression>.Fail(
                EnvStationErrorCodes.WorkflowExpressionFailed,
                "表达式为空：条件或 assert 缺少表达式文本。");
        }

        if (source.Length > MaxExpressionLength)
        {
            return Result<CompiledExpression>.Fail(
                EnvStationErrorCodes.WorkflowExpressionFailed,
                $"表达式长度 {source.Length} 个字符，超过上限 {MaxExpressionLength} 个字符。");
        }

        try
        {
            var parser = new Parser(source);
            var root = parser.ParseRoot();
            if (parser.NodeCount > MaxNodeCount)
            {
                return Result<CompiledExpression>.Fail(
                    EnvStationErrorCodes.WorkflowExpressionFailed,
                    $"表达式节点数 {parser.NodeCount}，超过上限 {MaxNodeCount}。");
            }

            return Result<CompiledExpression>.Ok(CompiledExpression.Create(source, root));
        }
        catch (ExpressionSyntaxException ex)
        {
            return Result<CompiledExpression>.Fail(
                EnvStationErrorCodes.WorkflowExpressionFailed,
                ex.Message,
                "表达式只支持比较（== != > < >= <=）、逻辑（and or not）、括号、字面量与内置纯函数。");
        }
    }

    /// <summary>
    /// 求值一个表达式文本。返回 <see cref="bool"/> 表示条件判定结果。
    /// </summary>
    public static Result<bool> EvaluateCondition(string source, IVariableResolver resolver)
    {
        var compiled = Compile(source);
        if (compiled.IsFailure)
        {
            return compiled.Propagate<bool>();
        }

        var evaluated = compiled.Value.Evaluate(resolver);
        if (evaluated.IsFailure)
        {
            return evaluated.Propagate<bool>();
        }

        var condition = evaluated.Value.AsCondition();
        if (condition is null)
        {
            var kindText = evaluated.Value.Kind switch
            {
                ExprKind.String => "字符串",
                ExprKind.Number => "数值",
                _ => "未知类型",
            };

            return Result<bool>.Fail(
                EnvStationErrorCodes.WorkflowExpressionFailed,
                evaluated.Value.Kind == ExprKind.Missing
                    ? $"表达式 {source} 求值得到未定义的变量。"
                    : $"表达式 {source} 的结果是{kindText}，不能直接作为条件。",
                "条件必须是布尔值：字符串写成 ${x} == 「期望值」 这样的比较，数值写成 ${x} > 0 这样的比较。");
        }

        return Result<bool>.Ok(condition.Value);
    }

    // ────────────────────────────── AST ──────────────────────────────

    internal abstract class Node
    {
        internal abstract ExprValue Eval(IVariableResolver resolver, Stopwatch clock);
    }

    private sealed class LiteralNode(ExprValue value) : Node
    {
        internal override ExprValue Eval(IVariableResolver resolver, Stopwatch clock) => value;
    }

    private sealed class VariableNode(string name) : Node
    {
        internal string Name => name;

        internal override ExprValue Eval(IVariableResolver resolver, Stopwatch clock) => resolver.Resolve(name);
    }

    private sealed class NotNode(Node operand) : Node
    {
        internal override ExprValue Eval(IVariableResolver resolver, Stopwatch clock)
        {
            var v = operand.Eval(resolver, clock);
            var condition = v.AsCondition();
            return condition is null
                ? ExprValue.Missing
                : ExprValue.FromBoolean(!condition.Value);
        }
    }

    private sealed class LogicalNode(bool isAnd, Node left, Node right) : Node
    {
        internal override ExprValue Eval(IVariableResolver resolver, Stopwatch clock)
        {
            var l = left.Eval(resolver, clock).AsCondition();
            if (l is null)
            {
                return ExprValue.Missing;
            }

            // 短路求值：and 左侧为假、or 左侧为真时不触碰右侧。
            if (isAnd && !l.Value)
            {
                return ExprValue.FromBoolean(false);
            }

            if (!isAnd && l.Value)
            {
                return ExprValue.FromBoolean(true);
            }

            var r = right.Eval(resolver, clock).AsCondition();
            return r is null ? ExprValue.Missing : ExprValue.FromBoolean(r.Value);
        }
    }

    private sealed class ComparisonNode(string op, Node left, Node right) : Node
    {
        internal override ExprValue Eval(IVariableResolver resolver, Stopwatch clock)
        {
            var l = left.Eval(resolver, clock);
            var r = right.Eval(resolver, clock);
            if (l.Kind == ExprKind.Missing || r.Kind == ExprKind.Missing)
            {
                // 缺失值参与比较 → 结果为"未知"，而不是静默地当成空字符串。
                return ExprValue.Missing;
            }

            return op switch
            {
                "==" => ExprValue.FromBoolean(AreEqual(l, r)),
                "!=" => ExprValue.FromBoolean(!AreEqual(l, r)),
                _ => CompareOrdered(op, l, r),
            };
        }

        private static bool AreEqual(ExprValue l, ExprValue r)
        {
            if (l.Kind == ExprKind.Number && r.Kind == ExprKind.Number)
            {
                return l.Number.Equals(r.Number);
            }

            if (l.Kind == ExprKind.Boolean && r.Kind == ExprKind.Boolean)
            {
                return l.Boolean == r.Boolean;
            }

            if (l.Kind == ExprKind.String && r.Kind == ExprKind.String)
            {
                // 路径与版本比较在 Windows 上应当大小写不敏感，但通用字符串比较保持 Ordinal——
                // 需要大小写不敏感时用 lower() 显式表达，避免"看起来相等却不相等"的隐蔽差异。
                return string.Equals(l.Text, r.Text, StringComparison.Ordinal);
            }

            return false;
        }

        private static ExprValue CompareOrdered(string op, ExprValue l, ExprValue r)
        {
            int cmp;
            if (l.Kind == ExprKind.Number && r.Kind == ExprKind.Number)
            {
                cmp = l.Number.CompareTo(r.Number);
            }
            else if (l.Kind == ExprKind.String && r.Kind == ExprKind.String)
            {
                // 版本号字符串按语义化版本比较，其余按序号比较。
                if (SemanticVersion.TryParse(l.Text, out var lv) && SemanticVersion.TryParse(r.Text, out var rv))
                {
                    cmp = lv.CompareTo(rv);
                }
                else
                {
                    cmp = string.CompareOrdinal(l.Text, r.Text);
                }
            }
            else
            {
                return ExprValue.Missing;
            }

            return op switch
            {
                ">" => ExprValue.FromBoolean(cmp > 0),
                ">=" => ExprValue.FromBoolean(cmp >= 0),
                "<" => ExprValue.FromBoolean(cmp < 0),
                "<=" => ExprValue.FromBoolean(cmp <= 0),
                _ => ExprValue.Missing,
            };
        }
    }

    private sealed class CallNode(string name, ImmutableArray<Node> arguments) : Node
    {
        internal override ExprValue Eval(IVariableResolver resolver, Stopwatch clock)
        {
            if (clock.Elapsed > EvaluationTimeout)
            {
                return ExprValue.Missing;
            }

            var args = new ExprValue[arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                args[i] = arguments[i].Eval(resolver, clock);
            }

            return Invoke(name, args);
        }

        private static ExprValue Invoke(string name, ExprValue[] args)
        {
            switch (name)
            {
                case "exists":
                    return args.Length == 1 ? ExprValue.FromBoolean(args[0].Kind != ExprKind.Missing) : ExprValue.Missing;

                case "len":
                    return args.Length == 1 && args[0].Kind == ExprKind.String
                        ? ExprValue.FromNumber((args[0].Text ?? string.Empty).Length)
                        : ExprValue.Missing;

                case "lower":
                    return args.Length == 1 && args[0].Kind == ExprKind.String
                        ? ExprValue.FromString((args[0].Text ?? string.Empty).ToLowerInvariant())
                        : ExprValue.Missing;

                case "upper":
                    return args.Length == 1 && args[0].Kind == ExprKind.String
                        ? ExprValue.FromString((args[0].Text ?? string.Empty).ToUpperInvariant())
                        : ExprValue.Missing;

                case "trim":
                    return args.Length == 1 && args[0].Kind == ExprKind.String
                        ? ExprValue.FromString((args[0].Text ?? string.Empty).Trim())
                        : ExprValue.Missing;

                case "contains":
                    return args.Length == 2 && args[0].Kind == ExprKind.String && args[1].Kind == ExprKind.String
                        ? ExprValue.FromBoolean((args[0].Text ?? string.Empty).Contains(args[1].Text ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        : ExprValue.Missing;

                case "starts_with":
                    return args.Length == 2 && args[0].Kind == ExprKind.String && args[1].Kind == ExprKind.String
                        ? ExprValue.FromBoolean((args[0].Text ?? string.Empty).StartsWith(args[1].Text ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        : ExprValue.Missing;

                case "ends_with":
                    return args.Length == 2 && args[0].Kind == ExprKind.String && args[1].Kind == ExprKind.String
                        ? ExprValue.FromBoolean((args[0].Text ?? string.Empty).EndsWith(args[1].Text ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        : ExprValue.Missing;

                case "matches":
                    return Matches(args);

                case "semver_gt":
                    return args.Length == 2
                        && SemanticVersion.TryParse(args[0].Text, out var a)
                        && SemanticVersion.TryParse(args[1].Text, out var b)
                        ? ExprValue.FromBoolean(a > b)
                        : ExprValue.Missing;

                case "semver_satisfies":
                    return args.Length == 2
                        && SemanticVersion.TryParse(args[0].Text, out var v)
                        ? ExprValue.FromBoolean(v.Satisfies(args[1].Text))
                        : ExprValue.Missing;

                case "path_join":
                    {
                        if (args.Length < 2)
                        {
                            return ExprValue.Missing;
                        }

                        var parts = new string[args.Length];
                        for (var i = 0; i < args.Length; i++)
                        {
                            if (args[i].Kind != ExprKind.String)
                            {
                                return ExprValue.Missing;
                            }

                            parts[i] = args[i].Text ?? string.Empty;
                        }

                        return ExprValue.FromString(Path.Combine(parts));
                    }

                default:
                    return ExprValue.Missing;
            }
        }

        private static ExprValue Matches(ExprValue[] args)
        {
            if (args.Length != 2 || args[0].Kind != ExprKind.String || args[1].Kind != ExprKind.String)
            {
                return ExprValue.Missing;
            }

            var input = args[0].Text ?? string.Empty;
            var pattern = args[1].Text ?? string.Empty;

            // 输入长度上限：正则的代价与输入规模直接相关。
            if (pattern.Length > MaxPatternLength || input.Length > 4096)
            {
                return ExprValue.Missing;
            }

            try
            {
                // NonBacktracking 从算法层面消除灾难性回溯（ReDoS），
                // 超时是第二道防线（NonBacktracking 对某些构造仍可能耗时）。
                var regex = new Regex(
                    pattern,
                    RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                    RegexTimeout);
                return ExprValue.FromBoolean(regex.IsMatch(input));
            }
            catch (RegexParseException)
            {
                // 模式本身非法：返回"未知"而不是 false——写错的规则必须被发现，不能被当成"不匹配"。
                return ExprValue.Missing;
            }
            catch (RegexMatchTimeoutException)
            {
                return ExprValue.Missing;
            }
        }
    }

    // ────────────────────────────── 语法分析 ──────────────────────────────

    private sealed class ExpressionSyntaxException(string message) : Exception(message);

    private sealed class Parser(string source)
    {
        private readonly string _text = source;
        private int _pos;

        internal int NodeCount { get; private set; }

        internal Node ParseRoot()
        {
            var node = ParseOr();
            SkipWhitespace();
            if (_pos < _text.Length)
            {
                throw new ExpressionSyntaxException($"第 {_pos + 1} 个字符处出现多余内容：{_text[_pos..]}。");
            }

            return node;
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (true)
            {
                SkipWhitespace();
                if (!TryConsumeKeyword("or"))
                {
                    return left;
                }

                NodeCount++;
                left = new LogicalNode(false, left, ParseAnd());
            }
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (true)
            {
                SkipWhitespace();
                if (!TryConsumeKeyword("and"))
                {
                    return left;
                }

                NodeCount++;
                left = new LogicalNode(true, left, ParseNot());
            }
        }

        private Node ParseNot()
        {
            SkipWhitespace();
            if (TryConsumeKeyword("not"))
            {
                NodeCount++;
                return new NotNode(ParseNot());
            }

            return ParseComparison();
        }

        private Node ParseComparison()
        {
            var left = ParsePrimary();
            SkipWhitespace();
            var op = TryConsumeComparisonOperator();
            if (op is null)
            {
                return left;
            }

            NodeCount++;
            return new ComparisonNode(op, left, ParsePrimary());
        }

        private string? TryConsumeComparisonOperator()
        {
            if (_pos >= _text.Length)
            {
                return null;
            }

            if (Matches("=="))
            {
                _pos += 2;
                return "==";
            }

            if (Matches("!="))
            {
                _pos += 2;
                return "!=";
            }

            if (Matches(">="))
            {
                _pos += 2;
                return ">=";
            }

            if (Matches("<="))
            {
                _pos += 2;
                return "<=";
            }

            var c = _text[_pos];
            if (c is '>' or '<')
            {
                _pos++;
                return c.ToString();
            }

            if (c == '=')
            {
                throw new ExpressionSyntaxException(
                    "第 {_pos + 1} 个字符处出现单等号 =：比较用 ==，表达式语言没有赋值能力。");
            }

            return null;
        }

        private Node ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
            {
                throw new ExpressionSyntaxException("表达式意外结束：期望一个字面量、变量或函数调用。");
            }

            NodeCount++;
            var c = _text[_pos];

            if (c == '(')
            {
                _pos++;
                var inner = ParseOr();
                SkipWhitespace();
                Expect(')');
                return inner;
            }

            if (c == '"' || c == '\'')
            {
                return new LiteralNode(ExprValue.FromString(ParseQuoted(c)));
            }

            if (c == '$' && _pos + 1 < _text.Length && _text[_pos + 1] == '{')
            {
                return new VariableNode(ParseVariable());
            }

            if (char.IsAsciiDigit(c) || (c == '-' && _pos + 1 < _text.Length && char.IsAsciiDigit(_text[_pos + 1])))
            {
                return new LiteralNode(ExprValue.FromNumber(ParseNumber()));
            }

            var identifier = ParseIdentifier();
            if (identifier.Length == 0)
            {
                throw new ExpressionSyntaxException($"第 {_pos + 1} 个字符处无法识别：'{c}'。");
            }

            switch (identifier)
            {
                case "true":
                    return new LiteralNode(ExprValue.FromBoolean(true));
                case "false":
                    return new LiteralNode(ExprValue.FromBoolean(false));
                case "null":
                    return new LiteralNode(ExprValue.Missing);
            }

            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '(')
            {
                _pos++;
                var args = ImmutableArray.CreateBuilder<Node>();
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == ')')
                {
                    _pos++;
                }
                else
                {
                    while (true)
                    {
                        args.Add(ParseOr());
                        SkipWhitespace();
                        if (_pos < _text.Length && _text[_pos] == ',')
                        {
                            _pos++;
                            continue;
                        }

                        Expect(')');
                        break;
                    }
                }

                if (!BuiltinFunctions.Contains(identifier, StringComparer.Ordinal))
                {
                    throw new ExpressionSyntaxException(
                        $"不允许调用函数 {identifier}()：表达式语言只允许这些内置纯函数：{string.Join("、", BuiltinFunctions)}。");
                }

                NodeCount++;
                return new CallNode(identifier, args.ToImmutable());
            }

            throw new ExpressionSyntaxException(
                $"第 {_pos + 1} 个字符处出现裸标识符 {identifier}：变量必须写成 ${{{identifier}}} 形式。");
        }

        private string ParseVariable()
        {
            _pos += 2; // 跳过 ${
            var start = _pos;
            while (_pos < _text.Length && _text[_pos] != '}')
            {
                _pos++;
            }

            if (_pos >= _text.Length)
            {
                throw new ExpressionSyntaxException("变量引用缺少右花括号 }：${ 之后未见 }。");
            }

            var name = _text[start.._pos].Trim();
            _pos++; // 跳过 }

            if (name.Length == 0)
            {
                throw new ExpressionSyntaxException("变量引用为空：形如 ${py.found}。");
            }

            if (!IsQualifiedName(name))
            {
                throw new ExpressionSyntaxException(
                    $"变量名 {name} 非法：变量名只能由字母、数字、下划线与点组成，且不能以下划线或数字开头。");
            }

            return name;
        }

        private static bool IsQualifiedName(string name)
        {
            var segments = name.Split('.');
            if (segments.Length is < 1 or > 3)
            {
                return false;
            }

            foreach (var segment in segments)
            {
                if (segment.Length == 0)
                {
                    return false;
                }

                if (!char.IsAsciiLetter(segment[0]) && segment[0] != '_')
                {
                    return false;
                }

                foreach (var ch in segment)
                {
                    if (!char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-')
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private string ParseQuoted(char quote)
        {
            _pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length)
                {
                    throw new ExpressionSyntaxException("字符串字面量未闭合。");
                }

                var c = _text[_pos];
                if (c == quote)
                {
                    _pos++;
                    return sb.ToString();
                }

                if (c == '\\' && _pos + 1 < _text.Length)
                {
                    _pos++;
                    sb.Append(_text[_pos] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        '\'' => '\'',
                        _ => _text[_pos],
                    });
                    _pos++;
                    continue;
                }

                sb.Append(c);
                _pos++;
            }
        }

        private double ParseNumber()
        {
            var start = _pos;
            if (_text[_pos] == '-')
            {
                _pos++;
            }

            while (_pos < _text.Length && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '.'))
            {
                _pos++;
            }

            var text = _text[start.._pos];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new ExpressionSyntaxException($"数值 {text} 格式非法：数值只能是十进制整数或小数。");
            }

            return value;
        }

        private string ParseIdentifier()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsAsciiLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
            {
                _pos++;
            }

            return _text[start.._pos];
        }

        private bool TryConsumeKeyword(string keyword)
        {
            if (!Matches(keyword))
            {
                return false;
            }

            var end = _pos + keyword.Length;
            if (end < _text.Length && (char.IsAsciiLetterOrDigit(_text[end]) || _text[end] == '_'))
            {
                return false;
            }

            _pos = end;
            return true;
        }

        private bool Matches(string token) =>
            _pos + token.Length <= _text.Length
            && string.CompareOrdinal(_text, _pos, token, 0, token.Length) == 0;

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }

        private void Expect(char expected)
        {
            if (_pos >= _text.Length || _text[_pos] != expected)
            {
                throw new ExpressionSyntaxException($"第 {_pos + 1} 个字符处期望 {expected}，实际读到 {(_pos < _text.Length ? _text[_pos].ToString() : "表达式结束")}。");
            }

            _pos++;
        }
    }

    /// <summary>已编译的表达式。编译一次可重复求值（例如 foreach 循环体内）。</summary>
    public sealed class CompiledExpression
    {
        private readonly Node _root;

        private CompiledExpression(string source, Node root)
        {
            Source = source;
            _root = root;
        }

        /// <summary>由解析器构造（仅外层类型可见）。</summary>
        internal static CompiledExpression Create(string source, Node root) => new(source, root);

        /// <summary>原始表达式文本。</summary>
        public string Source { get; }

        /// <summary>求值。返回"未知"表示变量缺失、类型不符或耗时超限。</summary>
        public Result<ExprValue> Evaluate(IVariableResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            var clock = Stopwatch.StartNew();
            var value = _root.Eval(resolver, clock);
            if (clock.Elapsed > EvaluationTimeout)
            {
                return Result<ExprValue>.Fail(
                    EnvStationErrorCodes.WorkflowExpressionFailed,
                    $"表达式 {Source} 求值耗时 {clock.Elapsed.TotalMilliseconds:F1} 毫秒，超过上限 {EvaluationTimeout.TotalMilliseconds} 毫秒。",
                    "简化表达式：避免对大字符串做正则匹配。");
            }

            return Result<ExprValue>.Ok(value);
        }
    }
}
