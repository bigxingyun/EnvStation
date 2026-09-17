using System.Collections.Immutable;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Environment;
using EnvStation.Core.Environment;

namespace EnvStation.Core.Actions.Builtin;

/// <summary>
/// PATH 读取与合成。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须单独抽出来（一次真实缺陷）</b>：最初的实现把"合并 PATH"理解成
/// "有用户 PATH 就用用户 PATH，否则用系统 PATH"。这是错的，而且错得很隐蔽——
/// Windows 用户级 PATH 通常<b>不</b>包含 <c>C:\Windows\System32</c>
/// （那一条在系统级 PATH 里）。于是"合并"模式下连 <c>cmd</c> 都找不到，
/// 用户会看到"命令未找到"这种明显不合理的结论。
/// </para>
/// <para>
/// <b>正确语义</b>：Windows 在创建进程时把 PATH 组合为
/// <c>系统 PATH</c> + <c>;</c> + <c>用户 PATH</c>（系统在前）。因此"合并"必须是拼接，而不是二选一。
/// 顺序也不能反：反了会让用户级目录抢走系统命令的优先级。
/// </para>
/// <para>
/// 顺带处理第三个坑：注册表里的值可能是 <c>REG_EXPAND_SZ</c>（含 <c>%SystemRoot%</c> 之类），
/// 这里必须用<b>原始值</b>交给 <see cref="PathParser"/>，由它统一处理变量展开与去重，
/// 绝不能在这里自行展开——否则会把用户的 <c>%VAR%</c> 写法写坏（PE-4）。
/// </para>
/// </remarks>
internal static class EnvironmentPathResolver
{
    /// <summary>作用域取值，与动作参数的枚举保持一致（小写）。</summary>
    internal static ImmutableArray<string> Scopes { get; } = ["machine", "user", "process", "merged"];

    /// <summary>把 <see cref="EnvScope"/> 映射为动作参数里使用的小写取值。</summary>
    internal static string ToScopeName(EnvScope scope) => scope == EnvScope.User ? "user" : "machine";

    /// <summary>
    /// 作用域在消息里的中文标签。
    /// </summary>
    /// <remarks>
    /// <b>必须与 <see cref="ToScopeName"/> 分开用</b>：后者的取值是机器标识，会出现在输出字段键
    /// （<c>user.value</c> / <c>machine.kind</c>）与工作流变量里，改了就是破坏契约；
    /// 而消息是给人读的，写 <c>已设置 user变量 PATH</c> 这种中英夹生句是明确的文案缺陷。
    /// 简而言之：**键用 <c>ToScopeName</c>，话用 <c>ToScopeLabel</c>。**
    /// </remarks>
    internal static string ToScopeLabel(EnvScope scope) => scope == EnvScope.User ? "用户级" : "系统级";

    /// <summary>把 <c>scope</c> 参数的取值渲染为消息里的中文标签。</summary>
    internal static string ToScopeLabel(string scope) => scope switch
    {
        "user" => "用户级",
        "machine" => "系统级",
        "both" => "用户级与系统级",
        "process" => "进程级",
        _ => "合并",
    };

    /// <summary>
    /// 按作用域读取 PATH 原始值。
    /// </summary>
    /// <param name="scope">取值见 <see cref="Scopes"/>。</param>
    /// <returns>原始 PATH 文本；读取失败返回 null。</returns>
    internal static string? Read(string scope)
    {
        var machine = ReadRaw(EnvScope.Machine);
        var user = ReadRaw(EnvScope.User);

        return scope switch
        {
            "user" => user ?? machine,
            "machine" => machine ?? user,
            "process" => System.Environment.GetEnvironmentVariable("PATH") ?? Combine(machine, user),
            _ => Combine(machine, user) ?? System.Environment.GetEnvironmentVariable("PATH"),
        };
    }

    /// <summary>按 Windows 的组合顺序拼接系统级与用户级 PATH。</summary>
    internal static string? Combine(string? machine, string? user)
    {
        if (string.IsNullOrEmpty(machine))
        {
            return string.IsNullOrEmpty(user) ? null : user;
        }

        if (string.IsNullOrEmpty(user))
        {
            return machine;
        }

        // 用分号拼接：PATH 的分隔符语义交给 PathParser 统一处理（它还要过滤空项）。
        return machine.TrimEnd(';') + ";" + user.TrimStart(';');
    }

    private static string? ReadRaw(EnvScope scope)
    {
        var result = new RegistryEnvStore(scope).Read("PATH");
        return result is { IsSuccess: true, Value: { } variable } ? variable.RawValue : null;
    }
}
