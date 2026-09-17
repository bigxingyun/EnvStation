using System.Collections.Immutable;

namespace EnvStation.Abstractions;

/// <summary>
/// <see cref="ImmutableArray{T}"/> 的规范化工具。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这个类（一次真实踩坑）</b>：<c>ImmutableArray&lt;T&gt;</c> 有两个"空"的状态——
/// <c>IsDefault == true</c>（底层数组为 null）与 <c>IsEmpty == true</c>（底层数组长度为 0）。
/// 二者在 <c>.Length</c> 上看起来一样（都是 0），但<b>枚举、排序、LINQ 会在 default 上直接抛</b>
/// <c>InvalidOperationException: This operation cannot be performed on a default instance of ImmutableArray&lt;T&gt;</c>。
/// </para>
/// <para>
/// 更麻烦的是：用集合表达式 <c>[]</c> 给 <c>ImmutableArray&lt;T&gt;</c> 赋值时，
/// 结果可能是 <c>default</c> 而不是 <c>Empty</c>。于是"动作没有参数""清单没有域名白名单"
/// 这类完全正常的空集合，会在某个下游 LINQ 调用处炸掉。
/// </para>
/// <para>
/// <b>本项目采取的纪律</b>：
/// <list type="number">
///   <item>在<b>产生</b>空集合的地方显式写 <c>ImmutableArray&lt;T&gt;.Empty</c>；</item>
///   <item>在<b>消费</b>可能来自外部的集合（动作描述符、包清单等）时统一过 <see cref="OrEmpty{T}"/>。</item>
/// </list>
/// 两条同时做，是因为只做第二条会留下大量噪声，只做第一条则挡不住未来新增的代码路径。
/// </para>
/// </remarks>
public static class ImmutableArrayExtensions
{
    /// <summary>把 default 视为空集合；已是正常数组时原样返回（不复制）。</summary>
    public static ImmutableArray<T> OrEmpty<T>(this ImmutableArray<T> array) =>
        array.IsDefault ? ImmutableArray<T>.Empty : array;

    /// <summary>安全枚举：default 与 Empty 都产生空序列。</summary>
    public static IEnumerable<T> SafeEnumerate<T>(this ImmutableArray<T> array) =>
        array.IsDefault ? [] : array;
}
