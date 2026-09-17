using System.Globalization;

namespace EnvStation.Abstractions.Packages;

/// <summary>
/// 语义化版本（SemVer 2.0 的实用子集）。用于动作引用、包版本与版本约束求值。
/// </summary>
/// <remarks>
/// 只实现本项目真正需要的部分：<c>主.次.修订[-预发布]</c>。
/// 构建元数据（<c>+build</c>）在比较时被忽略（符合 SemVer 规范），但原文保留以便展示。
/// </remarks>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string? preRelease, string original)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        Original = original;
    }

    /// <summary>主版本号。</summary>
    public int Major { get; }

    /// <summary>次版本号。</summary>
    public int Minor { get; }

    /// <summary>修订号。</summary>
    public int Patch { get; }

    /// <summary>预发布标识（不含前导连字符）；正式版为 null。</summary>
    public string? PreRelease { get; }

    /// <summary>原始文本（保留构建元数据）。</summary>
    public string Original { get; }

    /// <summary>是否为预发布版本。</summary>
    public bool IsPreRelease => PreRelease is not null;

    /// <summary>
    /// 解析语义化版本。缺少次版本或修订号时按 0 补齐（<c>3.12</c> 等价于 <c>3.12.0</c>），
    /// 这是运行时版本号的常见写法，刻意宽松；但主版本号必须存在且为数字。
    /// </summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var original = text.Trim();
        var body = original;

        // 去掉构建元数据
        var plus = body.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            body = body[..plus];
        }

        string? pre = null;
        var dash = body.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = body[(dash + 1)..];
            body = body[..dash];
            if (pre.Length == 0)
            {
                return false;
            }
        }

        var parts = body.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])
                || numbers[i] < 0)
            {
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], pre, original);
        return true;
    }

    /// <summary>解析失败时返回 null（用于可选字段）。</summary>
    public static SemanticVersion? ParseOrNull(string? text) => TryParse(text, out var v) ? v : null;

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        // SemVer：有预发布标识的版本小于同号正式版
        if (PreRelease is null && other.PreRelease is null)
        {
            return 0;
        }

        if (PreRelease is null)
        {
            return 1;
        }

        if (other.PreRelease is null)
        {
            return -1;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string left, string right)
    {
        var l = left.Split('.');
        var r = right.Split('.');
        var n = Math.Min(l.Length, r.Length);
        for (var i = 0; i < n; i++)
        {
            var ln = int.TryParse(l[i], NumberStyles.None, CultureInfo.InvariantCulture, out var li);
            var rn = int.TryParse(r[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ri);
            int c;
            if (ln && rn)
            {
                c = li.CompareTo(ri);
            }
            else if (ln)
            {
                c = -1; // 数字标识符优先级低于字母标识符
            }
            else if (rn)
            {
                c = 1;
            }
            else
            {
                c = string.CompareOrdinal(l[i], r[i]);
            }

            if (c != 0)
            {
                return c;
            }
        }

        return l.Length.CompareTo(r.Length);
    }

    /// <summary>是否满足版本约束表达式，如 <c>&gt;=3.12</c>、<c>&gt;=3.10,&lt;4</c>、<c>*</c>。</summary>
    public bool Satisfies(string? range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*")
        {
            return true;
        }

        foreach (var clause in range.Split(','))
        {
            var text = clause.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var (op, rest) = SplitOperator(text);
            if (!TryParse(rest, out var target))
            {
                // 约束本身写错时按"不满足"处理——安全侧：宁可提示用户改包，也不静默放行。
                return false;
            }

            var cmp = CompareTo(target);
            var ok = op switch
            {
                ">=" => cmp >= 0,
                ">" => cmp > 0,
                "<=" => cmp <= 0,
                "<" => cmp < 0,
                "==" or "=" => cmp == 0,
                "!=" => cmp != 0,
                "^" => Major == target.Major && cmp >= 0,
                "~" => Major == target.Major && Minor == target.Minor && cmp >= 0,
                _ => false,
            };

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static (string Operator, string Version) SplitOperator(string text)
    {
        if (text.StartsWith(">=", StringComparison.Ordinal))
        {
            return (">=", text[2..]);
        }

        if (text.StartsWith("<=", StringComparison.Ordinal))
        {
            return ("<=", text[2..]);
        }

        if (text.StartsWith("==", StringComparison.Ordinal))
        {
            return ("==", text[2..]);
        }

        if (text.StartsWith("!=", StringComparison.Ordinal))
        {
            return ("!=", text[2..]);
        }

        if (text.StartsWith('>'))
        {
            return (">", text[1..]);
        }

        if (text.StartsWith('<'))
        {
            return ("<", text[1..]);
        }

        if (text.StartsWith('='))
        {
            return ("=", text[1..]);
        }

        if (text.StartsWith('^'))
        {
            return ("^", text[1..]);
        }

        if (text.StartsWith('~'))
        {
            return ("~", text[1..]);
        }

        return ("==", text);
    }

    /// <inheritdoc />
    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion v && Equals(v);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    /// <summary>相等运算符。</summary>
    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);

    /// <summary>小于运算符。</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>小于等于运算符。</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>大于运算符。</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>大于等于运算符。</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() =>
        string.IsNullOrEmpty(Original) ? $"{Major}.{Minor}.{Patch}" : Original;
}
