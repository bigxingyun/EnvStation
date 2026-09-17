using System.Runtime.Versioning;
using System.Security.Principal;

namespace EnvStation.TestKit;

/// <summary>断言失败。被 <see cref="TestHarness"/> 捕获并记为 Fail。</summary>
public sealed class AssertionException(string message) : Exception(message);

/// <summary>用例前置条件不满足（如需要管理员权限）。记为 Skip 而非 Fail。</summary>
public sealed class SkipException(string reason) : Exception(reason);

/// <summary>
/// 极简断言器。语义与 xunit 对齐，便于将来平移。
/// 失败消息一律包含"期望 vs 实际"，便于直接定位。
/// </summary>
public static class Assert
{
    public static void True(bool condition, string because)
    {
        if (!condition)
        {
            throw new AssertionException("期望为真，实际为假。" + because);
        }
    }

    public static void False(bool condition, string because)
    {
        if (condition)
        {
            throw new AssertionException("期望为假，实际为真。" + because);
        }
    }

    public static void Equal<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException(
                "期望 " + Describe(expected) + "，实际 " + Describe(actual) + "。" + because);
        }
    }

    public static void NotEqual<T>(T notExpected, T actual, string because)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            throw new AssertionException(
                "期望不等于 " + Describe(notExpected) + "，但实际相等。" + because);
        }
    }

    public static void NotNull<T>(T? value, string because)
        where T : class
    {
        if (value is null)
        {
            throw new AssertionException("期望非 null。" + because);
        }
    }

    public static void Null<T>(T? value, string because)
        where T : class
    {
        if (value is not null)
        {
            throw new AssertionException("期望为 null，实际为 " + Describe(value) + "。" + because);
        }
    }

    public static void Contains(string expectedSubstring, string? actual, string because)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new AssertionException(
                "期望包含 " + Describe(expectedSubstring) + "，实际为 " + Describe(actual) + "。" + because);
        }
    }

    public static void NotContains(string unexpectedSubstring, string? actual, string because)
    {
        if (actual is not null && actual.Contains(unexpectedSubstring, StringComparison.Ordinal))
        {
            throw new AssertionException(
                "期望不包含 " + Describe(unexpectedSubstring) + "，但实际包含。" + because);
        }
    }

    public static void Throws<TException>(Action body, string because)
        where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionException(
                "期望抛出 " + typeof(TException).Name + "，实际抛出 " + ex.GetType().Name + "。" + because);
        }

        throw new AssertionException("期望抛出 " + typeof(TException).Name + "，但未抛出。" + because);
    }

    public static void Skip(string reason) => throw new SkipException(reason);

    /// <summary>要求当前进程具备管理员权限，否则跳过该用例。</summary>
    [SupportedOSPlatform("windows")]
    public static void RequireAdministrator(string because)
    {
        if (!IsAdministrator())
        {
            throw new SkipException("需要管理员权限。" + because);
        }
    }

    [SupportedOSPlatform("windows")]
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string Describe(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string s)
        {
            return "\"" + s + "\"";
        }

        return value.ToString() ?? "?";
    }
}
