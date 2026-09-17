using System.Collections;
using System.Globalization;
using System.Text;
using EnvStation.Abstractions;

namespace EnvStation.Core.Toml;

/// <summary>TOML 值基类。所有节点都携带源码行号，便于给出精确定位的中文错误信息。</summary>
public abstract class TomlValue
{
    /// <summary>该值在源文件中的行号（1 起）。</summary>
    public int Line { get; init; }

    /// <summary>紧凑的类型名，用于错误信息。</summary>
    public abstract string TypeName { get; }
}

/// <summary>TOML 字符串（基本字符串、字面量字符串、多行字符串统一为该类型）。</summary>
public sealed class TomlString : TomlValue
{
    public TomlString(string value, int line, bool multiline = false)
    {
        Value = value;
        Line = line;
        IsMultiline = multiline;
    }

    public string Value { get; }

    /// <summary>是否来自多行字符串（用于模板类配置的原样保留判断）。</summary>
    public bool IsMultiline { get; }

    /// <inheritdoc />
    public override string TypeName => "字符串";

    public override string ToString() => Value;
}

/// <summary>TOML 整数（64 位）。</summary>
public sealed class TomlInteger : TomlValue
{
    public TomlInteger(long value, int line)
    {
        Value = value;
        Line = line;
    }

    public long Value { get; }

    /// <inheritdoc />
    public override string TypeName => "整数";

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>TOML 浮点数。</summary>
public sealed class TomlFloat : TomlValue
{
    public TomlFloat(double value, int line)
    {
        Value = value;
        Line = line;
    }

    public double Value { get; }

    /// <inheritdoc />
    public override string TypeName => "浮点数";

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>TOML 布尔值。</summary>
public sealed class TomlBoolean : TomlValue
{
    public TomlBoolean(bool value, int line)
    {
        Value = value;
        Line = line;
    }

    public bool Value { get; }

    /// <inheritdoc />
    public override string TypeName => "布尔值";

    public override string ToString() => Value ? "true" : "false";
}

/// <summary>TOML 日期时间。本项目按不透明字符串处理（保留原始文本），避免引入时区语义分歧。</summary>
public sealed class TomlDateTime : TomlValue
{
    public TomlDateTime(string raw, int line)
    {
        Raw = raw;
        Line = line;
    }

    public string Raw { get; }

    /// <inheritdoc />
    public override string TypeName => "日期时间";

    public override string ToString() => Raw;
}

/// <summary>TOML 数组。TOML 1.0 允许异构数组，因此元素类型为基类。</summary>
public sealed class TomlArray : TomlValue, IReadOnlyList<TomlValue>
{
    private readonly List<TomlValue> _items;

    public TomlArray(List<TomlValue> items, int line)
    {
        _items = items;
        Line = line;
    }

    /// <inheritdoc />
    public override string TypeName => "数组";

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public TomlValue this[int index] => _items[index];

    /// <inheritdoc />
    public IEnumerator<TomlValue> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Add(TomlValue value) => _items.Add(value);

    public override string ToString() => $"[{string.Join(", ", _items)}]";
}

/// <summary>TOML 表（含内联表与 <c>[table]</c> 头部定义的表）。</summary>
public sealed class TomlTable : TomlValue
{
    private readonly Dictionary<string, TomlValue> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _keys = [];

    public TomlTable(int line) => Line = line;

    /// <inheritdoc />
    public override string TypeName => "表";

    /// <summary>按插入顺序返回键名。</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>键值对数量。</summary>
    public int Count => _keys.Count;

    /// <summary>尝试取子节点。</summary>
    public bool TryGet(string key, out TomlValue value) => _entries.TryGetValue(key, out value!);

    /// <summary>取子节点；不存在返回 null。</summary>
    public TomlValue? Get(string key) => _entries.TryGetValue(key, out var v) ? v : null;

    /// <summary>判断键是否存在。</summary>
    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    /// <summary>按插入顺序枚举键值对。</summary>
    public IEnumerable<KeyValuePair<string, TomlValue>> Entries
    {
        get
        {
            foreach (var k in _keys)
            {
                yield return new KeyValuePair<string, TomlValue>(k, _entries[k]);
            }
        }
    }

    /// <summary>取子表；键不存在或类型不符时返回 null。</summary>
    public TomlTable? GetTable(string key) => Get(key) as TomlTable;

    /// <summary>取数组；键不存在或类型不符时返回 null。</summary>
    public TomlArray? GetArray(string key) => Get(key) as TomlArray;

    /// <summary>取字符串；键不存在或类型不符时返回 null。</summary>
    public string? GetString(string key) => (Get(key) as TomlString)?.Value;

    /// <summary>取整数；键不存在或类型不符时返回 null。</summary>
    public long? GetInteger(string key) => (Get(key) as TomlInteger)?.Value;

    /// <summary>取布尔值；键不存在或类型不符时返回 null。</summary>
    public bool? GetBoolean(string key) => (Get(key) as TomlBoolean)?.Value;

    /// <summary>按点号路径取值（如 <c>a.b.c</c>）。</summary>
    public TomlValue? GetPath(string dottedPath)
    {
        var current = (TomlValue?)this;
        foreach (var part in dottedPath.Split('.'))
        {
            if (current is not TomlTable t || !t.TryGet(part, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>按点号路径取字符串。</summary>
    public string? GetPathString(string dottedPath) => (GetPath(dottedPath) as TomlString)?.Value;

    /// <summary>新增或覆盖子节点（解析器内部使用）。</summary>
    internal void Set(string key, TomlValue value)
    {
        if (!_entries.ContainsKey(key))
        {
            _keys.Add(key);
        }

        _entries[key] = value;
    }

    /// <summary>子节点是否由解析器显式定义过（用于检测重复表头）。</summary>
    internal bool IsExplicitlyDefined { get; set; }

    internal bool IsInline { get; set; }

    /// <summary>子节点是否由 <c>[[array]]</c> 创建。</summary>
    internal bool IsArrayElement { get; set; }

    public override string ToString() =>
        "{" + string.Join(", ", _keys.Select(k => $"{k} = {_entries[k]}")) + "}";
}

/// <summary>解析期错误。仅在 <see cref="TomlReader"/> 内部传播，公开边界会转换为 <c>Result</c>。</summary>
public sealed class TomlParseException : Exception
{
    public TomlParseException(string message, int line, int column)
        : base($"第 {line} 行第 {column} 列：{message}")
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }

    public int Column { get; }
}

/// <summary>
/// 最小 TOML 1.0 子集解析器——零第三方依赖，AOT 友好，全中文错误信息。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自己写而不用 Tomlyn</b>：M0 阶段实测本机与项目约定的构建环境无法获取 NuGet 包（离线），
/// 而 TOML 解析属于"必须自主可控"的基础设施（安全审计要求：包解析器不得引入不可审计的黑盒依赖）。
/// 自研解析器同时满足 AOT 与体积预算要求。
/// </para>
/// <para>
/// <b>支持范围</b>：<c>[table]</c>、<c>[[array of tables]]</c>、点号键、裸键与引号键、
/// 基本字符串（含转义）、字面量字符串、多行字符串、整数（含 0x/0o/0b 与下划线）、浮点数、
/// 布尔值、日期时间（按不透明字符串）、数组、内联表、注释。
/// </para>
/// <para>
/// <b>明确不支持</b>（遇到即报错，不会静默忽略）：异构数组合并、<c>\uXXXX</c> 之外的转义序列。
/// 解析器刻意"宁可报错也不猜测"，避免包作者写出被静默曲解的脚本。
/// </para>
/// </remarks>
public static class TomlReader
{
    /// <summary>硬性上限：单个 TOML 文件最大字符数，防止用超大文件做拒绝服务。</summary>
    public const int MaxDocumentLength = 4 * 1024 * 1024;

    /// <summary>硬性上限：表嵌套深度。超过即视为恶意或错误构造。</summary>
    public const int MaxDepth = 32;

    /// <summary>
    /// 解析 TOML 文本。失败时返回带行列号的中文错误，不抛异常。
    /// </summary>
    /// <remarks>
    /// <b>会先剥掉开头的 BOM。</b>这一条不是可有可无的兼容：
    /// Windows 上的常见工具（PowerShell 的 <c>Out-File -Encoding UTF8</c>、记事本的"UTF-8 带 BOM"、
    /// Visual Studio 保存的部分配置）默认就会写入 BOM。若解析器不处理，包作者会遇到
    /// "文件看起来完全正常，却报第 1 行第 1 列键名非法"这种几乎无法自行定位的错误。
    /// TOML 规范也允许实现忽略前导 BOM。
    /// </remarks>
    public static Result<TomlTable> Parse(string text)
    {
        try
        {
            var parser = new Parser(StripBom(text));
            return Result<TomlTable>.Ok(parser.ParseDocument());
        }
        catch (TomlParseException ex)
        {
            return Result<TomlTable>.Fail(
                EnvStationErrorCodes.PackageParseFailed,
                ex.Message,
                "检查该文件的 TOML 语法：常见错误为引号未闭合、表头重复定义、键名重复。");
        }
    }

    /// <summary>剥掉前导 BOM（U+FEFF）。</summary>
    public static string StripBom(string text) =>
        text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private int _pos;
        private int _line = 1;
        private int _lineStart;

        private int Column => _pos - _lineStart + 1;

        internal TomlTable ParseDocument()
        {
            if (_text.Length > MaxDocumentLength)
            {
                throw new TomlParseException(
                    $"文件过大：{_text.Length} 个字符，超过上限 {MaxDocumentLength} 个字符。", 1, 1);
            }

            var root = new TomlTable(1) { IsExplicitlyDefined = true };
            var current = root;

            SkipWhitespaceAndComments();
            while (!AtEnd)
            {
                if (Peek() == '[')
                {
                    current = ParseTableHeader(root);
                }
                else
                {
                    ParseKeyValue(current);
                }

                SkipWhitespaceAndComments();
            }

            return root;
        }

        // ────────────────────────────── 表头 ──────────────────────────────

        private TomlTable ParseTableHeader(TomlTable root)
        {
            var headerLine = _line;
            Expect('[');
            var isArray = false;
            if (!AtEnd && Peek() == '[')
            {
                isArray = true;
                Advance();
            }

            SkipInlineWhitespace();
            var path = ParseKeyPath();
            SkipInlineWhitespace();
            Expect(']');
            if (isArray)
            {
                Expect(']');
            }

            if (path.Count > MaxDepth)
            {
                throw new TomlParseException($"表嵌套层级超过上限 {MaxDepth} 级。", headerLine, 1);
            }

            var parent = root;
            for (var i = 0; i < path.Count - 1; i++)
            {
                parent = DescendForHeader(parent, path[i], headerLine);
            }

            var leaf = path[^1];
            if (isArray)
            {
                TomlArray array;
                if (parent.TryGet(leaf, out var existing))
                {
                    if (existing is not TomlArray arr)
                    {
                        throw new TomlParseException(
                            $"键 {leaf} 已被定义为 {existing.TypeName}，不能用作数组表头。", headerLine, 1);
                    }

                    array = arr;
                }
                else
                {
                    array = new TomlArray([], headerLine);
                    parent.Set(leaf, array);
                }

                var element = new TomlTable(headerLine) { IsExplicitlyDefined = true, IsArrayElement = true };
                array.Add(element);
                return element;
            }

            if (parent.TryGet(leaf, out var node))
            {
                if (node is not TomlTable table)
                {
                    throw new TomlParseException(
                        $"键 {leaf} 已被定义为 {node.TypeName}，不能用作表头。", headerLine, 1);
                }

                if (table.IsExplicitlyDefined || table.IsInline)
                {
                    throw new TomlParseException($"表 {string.Join('.', path)} 重复定义。", headerLine, 1);
                }

                table.IsExplicitlyDefined = true;
                return table;
            }

            var created = new TomlTable(headerLine) { IsExplicitlyDefined = true };
            parent.Set(leaf, created);
            return created;
        }

        private static TomlTable DescendForHeader(TomlTable parent, string key, int line)
        {
            if (parent.TryGet(key, out var node))
            {
                if (node is TomlTable t)
                {
                    if (t.IsInline)
                    {
                        throw new TomlParseException($"内联表 {key} 不能继续扩展。", line, 1);
                    }

                    return t;
                }

                if (node is TomlArray arr && arr.Count > 0 && arr[^1] is TomlTable last && last.IsArrayElement)
                {
                    return last;
                }

                throw new TomlParseException(
                    $"键 {key} 已被定义为 {node.TypeName}，无法作为上级表使用。", line, 1);
            }

            var created = new TomlTable(line);
            parent.Set(key, created);
            return created;
        }

        // ────────────────────────────── 键值对 ──────────────────────────────

        private void ParseKeyValue(TomlTable table)
        {
            var line = _line;
            var path = ParseKeyPath();
            SkipInlineWhitespace();
            Expect('=');
            SkipInlineWhitespace();
            var value = ParseValue();

            var target = table;
            for (var i = 0; i < path.Count - 1; i++)
            {
                var key = path[i];
                if (target.TryGet(key, out var node))
                {
                    if (node is not TomlTable sub)
                    {
                        throw new TomlParseException(
                            $"键 {key} 已被定义为 {node.TypeName}，不能用作上级表。", line, 1);
                    }

                    target = sub;
                }
                else
                {
                    var created = new TomlTable(line);
                    target.Set(key, created);
                    target = created;
                }
            }

            var leaf = path[^1];
            if (target.ContainsKey(leaf))
            {
                throw new TomlParseException($"键 {leaf} 重复定义。", line, 1);
            }

            target.Set(leaf, value);
        }

        private List<string> ParseKeyPath()
        {
            var parts = new List<string> { ParseKeySegment() };
            SkipInlineWhitespace();
            while (!AtEnd && Peek() == '.')
            {
                Advance();
                SkipInlineWhitespace();
                parts.Add(ParseKeySegment());
                SkipInlineWhitespace();
            }

            return parts;
        }

        private string ParseKeySegment()
        {
            if (AtEnd)
            {
                throw new TomlParseException("期望键名，但文件已结束。", _line, Column);
            }

            var c = Peek();
            if (c == '"')
            {
                return ParseBasicString(multilineAllowed: false);
            }

            if (c == '\'')
            {
                return ParseLiteralString(multilineAllowed: false);
            }

            var start = _pos;
            while (!AtEnd)
            {
                var ch = Peek();
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-')
                {
                    Advance();
                }
                else
                {
                    break;
                }
            }

            if (_pos == start)
            {
                throw new TomlParseException($"键名包含非法字符 {Describe(c)}：裸键只允许字母、数字、下划线与连字符，其他字符用引号包裹。", _line, Column);
            }

            return _text[start.._pos];
        }

        // ────────────────────────────── 值 ──────────────────────────────

        private TomlValue ParseValue()
        {
            if (AtEnd)
            {
                throw new TomlParseException("期望值，但文件已结束。", _line, Column);
            }

            var line = _line;
            var c = Peek();
            switch (c)
            {
                case '"':
                    if (LookingAt("\"\"\""))
                    {
                        return new TomlString(ParseMultiLineBasicString(), line, multiline: true);
                    }

                    return new TomlString(ParseBasicString(multilineAllowed: false), line);

                case '\'':
                    if (LookingAt("'''"))
                    {
                        return new TomlString(ParseMultiLineLiteralString(), line, multiline: true);
                    }

                    return new TomlString(ParseLiteralString(multilineAllowed: false), line);

                case '[':
                    return ParseArray();

                case '{':
                    return ParseInlineTable();

                default:
                    return ParseScalar();
            }
        }

        private TomlValue ParseScalar()
        {
            var line = _line;
            var start = _pos;
            while (!AtEnd)
            {
                var ch = Peek();
                if (ch is '\n' or '\r' or '#' or ',' or ']' or '}')
                {
                    break;
                }

                Advance();
            }

            var token = _text[start.._pos].Trim();
            if (token.Length == 0)
            {
                throw new TomlParseException("期望值，但读到空白。", line, Column);
            }

            if (token is "true")
            {
                return new TomlBoolean(true, line);
            }

            if (token is "false")
            {
                return new TomlBoolean(false, line);
            }

            var cleaned = token.Replace("_", string.Empty, StringComparison.Ordinal);

            if (LooksLikeDateTime(token))
            {
                return new TomlDateTime(token, line);
            }

            if (TryParseInteger(cleaned, out var integer))
            {
                return new TomlInteger(integer, line);
            }

            if (TryParseFloat(cleaned, out var real))
            {
                return new TomlFloat(real, line);
            }

            if (cleaned is "inf" or "+inf" or "-inf" or "nan" or "+nan" or "-nan")
            {
                return new TomlFloat(
                    cleaned.EndsWith("nan", StringComparison.Ordinal)
                        ? double.NaN
                        : cleaned.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity,
                    line);
            }

            throw new TomlParseException(
                $"无法识别的值 {token}：字符串必须用引号包裹，期望整数、浮点数、布尔值或日期时间。",
                line,
                1);
        }

        private static bool LooksLikeDateTime(string token)
        {
            // 形如 1979-05-27T07:32:00Z / 1979-05-27 07:32:00 / 07:32:00
            if (token.Length < 5)
            {
                return false;
            }

            var digits = 0;
            foreach (var ch in token)
            {
                if (char.IsAsciiDigit(ch))
                {
                    digits++;
                }
            }

            var hasDateSeparator = token.Contains('-', StringComparison.Ordinal) && token.Contains(':', StringComparison.Ordinal);
            var isLocalTime = token.Contains(':', StringComparison.Ordinal) && !token.Contains('-', StringComparison.Ordinal);
            return digits >= 6 && (hasDateSeparator || isLocalTime);
        }

        private static bool TryParseInteger(string token, out long value)
        {
            value = 0;
            if (token.Length == 0)
            {
                return false;
            }

            var body = token;
            var negative = false;
            if (body[0] is '+' or '-')
            {
                negative = body[0] == '-';
                body = body[1..];
            }

            if (body.Length == 0)
            {
                return false;
            }

            try
            {
                if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    value = Convert.ToInt64(body[2..], 16);
                }
                else if (body.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
                {
                    value = Convert.ToInt64(body[2..], 8);
                }
                else if (body.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                {
                    value = Convert.ToInt64(body[2..], 2);
                }
                else
                {
                    if (!body.All(char.IsAsciiDigit))
                    {
                        return false;
                    }

                    value = long.Parse(body, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
            {
                return false;
            }

            if (negative)
            {
                value = -value;
            }

            return true;
        }

        private static bool TryParseFloat(string token, out double value)
        {
            value = 0;
            if (token.Length == 0)
            {
                return false;
            }

            var hasMarker = token.Contains('.', StringComparison.Ordinal)
                || token.Contains('e', StringComparison.Ordinal)
                || token.Contains('E', StringComparison.Ordinal);
            if (!hasMarker)
            {
                return false;
            }

            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private TomlArray ParseArray()
        {
            var line = _line;
            Expect('[');
            var items = new List<TomlValue>();
            while (true)
            {
                SkipWhitespaceAndComments();
                if (AtEnd)
                {
                    throw new TomlParseException("数组未闭合，期望 ]。", line, 1);
                }

                if (Peek() == ']')
                {
                    Advance();
                    break;
                }

                items.Add(ParseValue());
                SkipWhitespaceAndComments();
                if (!AtEnd && Peek() == ',')
                {
                    Advance();
                    continue;
                }

                SkipWhitespaceAndComments();
                if (!AtEnd && Peek() == ']')
                {
                    Advance();
                    break;
                }

                throw new TomlParseException("数组元素之间缺少逗号，或数组未闭合。", _line, Column);
            }

            return new TomlArray(items, line);
        }

        private TomlTable ParseInlineTable()
        {
            var line = _line;
            Expect('{');
            var table = new TomlTable(line) { IsInline = true, IsExplicitlyDefined = true };
            SkipInlineWhitespace();
            if (!AtEnd && Peek() == '}')
            {
                Advance();
                return table;
            }

            while (true)
            {
                SkipInlineWhitespace();
                if (AtEnd)
                {
                    throw new TomlParseException("内联表未闭合，期望 }。", line, 1);
                }

                var path = ParseKeyPath();
                SkipInlineWhitespace();
                Expect('=');
                SkipInlineWhitespace();
                var value = ParseValue();

                var target = table;
                for (var i = 0; i < path.Count - 1; i++)
                {
                    if (target.TryGet(path[i], out var node) && node is TomlTable sub)
                    {
                        target = sub;
                    }
                    else
                    {
                        var created = new TomlTable(line) { IsInline = true };
                        target.Set(path[i], created);
                        target = created;
                    }
                }

                if (target.ContainsKey(path[^1]))
                {
                    throw new TomlParseException($"内联表中的键 {path[^1]} 重复定义。", line, 1);
                }

                target.Set(path[^1], value);

                SkipInlineWhitespace();
                if (!AtEnd && Peek() == ',')
                {
                    Advance();
                    continue;
                }

                SkipInlineWhitespace();
                if (!AtEnd && Peek() == '}')
                {
                    Advance();
                    break;
                }

                throw new TomlParseException("内联表成员之间缺少逗号，或未闭合。", _line, Column);
            }

            return table;
        }

        // ────────────────────────────── 字符串 ──────────────────────────────

        private string ParseBasicString(bool multilineAllowed)
        {
            var startLine = _line;
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new TomlParseException("字符串未闭合，期望双引号。", startLine, 1);
                }

                var c = Peek();
                if (c == '"')
                {
                    Advance();
                    break;
                }

                if (c == '\n')
                {
                    if (!multilineAllowed)
                    {
                        throw new TomlParseException("基本字符串不能跨行：跨行改用三引号多行字符串。", startLine, 1);
                    }

                    Advance();
                    sb.Append('\n');
                    continue;
                }

                if (c == '\\')
                {
                    Advance();
                    sb.Append(ParseEscape(startLine));
                    continue;
                }

                sb.Append(c);
                Advance();
            }

            return sb.ToString();
        }

        private string ParseLiteralString(bool multilineAllowed)
        {
            var startLine = _line;
            Expect('\'');
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new TomlParseException("字符串未闭合，期望单引号。", startLine, 1);
                }

                var c = Peek();
                if (c == '\'')
                {
                    Advance();
                    break;
                }

                if (c == '\n' && !multilineAllowed)
                {
                    throw new TomlParseException("字面量字符串不能跨行：跨行改用三单引号。", startLine, 1);
                }

                sb.Append(c);
                Advance();
            }

            return sb.ToString();
        }

        private string ParseMultiLineBasicString()
        {
            var startLine = _line;
            Advance(3);
            SkipImmediateNewline();
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new TomlParseException("多行字符串未闭合，期望三引号。", startLine, 1);
                }

                if (LookingAt("\"\"\""))
                {
                    Advance(3);
                    break;
                }

                var c = Peek();
                if (c == '\\')
                {
                    var save = _pos;
                    Advance();
                    // 行尾续行符：反斜杠后仅剩空白与换行时，吞掉空白与换行。
                    var probe = _pos;
                    while (probe < _text.Length && (_text[probe] == ' ' || _text[probe] == '\t'))
                    {
                        probe++;
                    }

                    if (probe < _text.Length && (_text[probe] == '\n' || _text[probe] == '\r'))
                    {
                        _pos = probe;
                        while (!AtEnd && Peek() is ' ' or '\t' or '\n' or '\r')
                        {
                            Advance();
                        }

                        continue;
                    }

                    _pos = save;
                    Advance();
                    sb.Append(ParseEscape(startLine));
                    continue;
                }

                AppendNormalizedNewline(sb, c);
            }

            return sb.ToString();
        }

        private string ParseMultiLineLiteralString()
        {
            var startLine = _line;
            Advance(3);
            SkipImmediateNewline();
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new TomlParseException("多行字面量字符串未闭合，期望三单引号。", startLine, 1);
                }

                if (LookingAt("'''"))
                {
                    Advance(3);
                    return sb.ToString();
                }

                // 字面量字符串同样要规范化换行。不能直接取原文切片：
                // CRLF 源文件里的切片会带回车，解析结果跟着带，多行字符串的换行就成了两个字符。
                AppendNormalizedNewline(sb, Peek());
            }
        }

        /// <summary>
        /// 追加一个字符，把换行统一成 <c>\n</c>。
        /// </summary>
        /// <remarks>
        /// TOML 规范要求多行字符串里的换行以 <c>\n</c> 表示，
        /// 因此 CRLF 与单独的 CR 都要归一。这不是为了好看：
        /// 不归一的话，同一份 TOML 在 LF 与 CRLF 的工作区里会解析出不同的值，
        /// 而这种差异在 Windows 上按检出设置随机出现——本仓库真的因此挂过一个用例
        /// （SC-05，源文件被检出成 CRLF，测试题面里的换行跟着变成 CRLF）。
        /// </remarks>
        private void AppendNormalizedNewline(StringBuilder sb, char c)
        {
            if (c == '\r')
            {
                Advance();

                if (!AtEnd && Peek() == '\n')
                {
                    Advance();
                }

                sb.Append('\n');
                return;
            }

            sb.Append(c);
            Advance();
        }

        private string ParseEscape(int startLine)
        {
            if (AtEnd)
            {
                throw new TomlParseException("转义序列不完整：反斜杠之后缺少转义字符。", startLine, 1);
            }

            var c = Peek();
            Advance();
            switch (c)
            {
                case 'b': return "\b";
                case 't': return "\t";
                case 'n': return "\n";
                case 'f': return "\f";
                case 'r': return "\r";
                case '"': return "\"";
                case '\\': return "\\";
                case 'u': return char.ConvertFromUtf32(ParseHex(4, startLine));
                case 'U': return char.ConvertFromUtf32(ParseHex(8, startLine));
                default:
                    throw new TomlParseException(
                        $"不支持的转义序列 \\{c}：TOML 只允许 \\b \\t \\n \\f \\r \\\" \\\\ \\uXXXX \\UXXXXXXXX。",
                        startLine,
                        1);
            }
        }

        private int ParseHex(int digits, int startLine)
        {
            if (_pos + digits > _text.Length)
            {
                throw new TomlParseException("Unicode 转义序列长度不足：\\u 之后需 4 位、\\U 之后需 8 位十六进制数。", startLine, 1);
            }

            var span = _text.AsSpan(_pos, digits);
            if (!long.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var wide))
            {
                throw new TomlParseException($"Unicode 转义序列 {span.ToString()} 不是合法的十六进制数：只允许 0-9、a-f、A-F。", startLine, 1);
            }

            _pos += digits;
            if (wide is < 0 or > 0x10FFFF)
            {
                throw new TomlParseException($"Unicode 码位 U+{wide:X} 超出合法范围：上限为 U+10FFFF。", startLine, 1);
            }

            return (int)wide;
        }

        // ────────────────────────────── 底层扫描 ──────────────────────────────

        private bool AtEnd => _pos >= _text.Length;

        private char Peek() => _text[_pos];

        private void Advance(int count = 1)
        {
            for (var i = 0; i < count && _pos < _text.Length; i++)
            {
                if (_text[_pos] == '\n')
                {
                    _line++;
                    _lineStart = _pos + 1;
                }

                _pos++;
            }
        }

        private bool LookingAt(string token) =>
            _pos + token.Length <= _text.Length
            && string.CompareOrdinal(_text, _pos, token, 0, token.Length) == 0;

        private void SkipImmediateNewline()
        {
            if (!AtEnd && Peek() == '\r')
            {
                Advance();
            }

            if (!AtEnd && Peek() == '\n')
            {
                Advance();
            }
        }

        private void SkipInlineWhitespace()
        {
            while (!AtEnd && (Peek() == ' ' || Peek() == '\t'))
            {
                Advance();
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (!AtEnd)
            {
                var c = Peek();
                if (c is ' ' or '\t' or '\r' or '\n')
                {
                    Advance();
                    continue;
                }

                if (c == '#')
                {
                    while (!AtEnd && Peek() != '\n')
                    {
                        Advance();
                    }

                    continue;
                }

                break;
            }
        }

        private void Expect(char expected)
        {
            if (AtEnd || Peek() != expected)
            {
                var found = AtEnd ? "文件结束" : Describe(Peek());
                throw new TomlParseException($"期望 {Describe(expected)}，实际读到 {found}。", _line, Column);
            }

            Advance();
        }

        private static string Describe(char c) => c switch
        {
            '\n' => "换行",
            '\r' => "回车",
            '\t' => "制表符",
            ' ' => "空格",
            _ => $"'{c}'",
        };
    }
}
