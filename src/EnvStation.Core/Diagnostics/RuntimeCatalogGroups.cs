using System.Collections.Immutable;

namespace EnvStation.Core.Diagnostics;

/// <summary>运行时清单里的一个条目。</summary>
/// <param name="Kind">运行时种类标识（传给 <c>detect.runtime</c> 的 <c>kind</c>）。</param>
/// <param name="DisplayName">界面上的名字（中文，取自运行时候选表的 <c>DisplayName</c>）。</param>
public sealed record RuntimeEntry(string Kind, string DisplayName);

/// <summary>运行时分组（界面上的表格分组，也是"这块该有什么"的心智模型）。</summary>
/// <param name="Title">分组名。</param>
/// <param name="Entries">该组的运行时。</param>
public sealed record RuntimeGroup(string Title, ImmutableArray<RuntimeEntry> Entries);

/// <summary>
/// 要检测的运行时清单与分组。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有一份清单</b>：检测运行时需要逐个调 <c>detect.runtime</c> 并传 <c>kind</c>，
/// 而 <c>kind</c> 的合法取值藏在 <c>RuntimeCatalog</c>（内核内部）。界面若自己维护一份列表，
/// 两边迟早不一致——内核加了运行时，界面不知道；界面删了一个，内核还以为在检。
/// </para>
/// <para>
/// <b>分组不是装饰</b>：16 个运行时平铺成一张表，用户要在一堆陌生名字里找自己要的那个。
/// 按"这块该有什么"分组之后，找不到比预期少的东西时用户才知道是"没装"而不是"没找到"。
/// </para>
/// <para>
/// 顺序即界面顺序，按《详细设计文档》2 章的优先级排：三大件（C 系 / Java 系 / Python）在前。
/// </para>
/// </remarks>
public static class RuntimeCatalogGroups
{
    /// <summary>全部运行时分组。</summary>
    public static ImmutableArray<RuntimeGroup> All { get; } =
    [
        new("C 系工具链",
        [
            new("gcc", "GCC（MinGW-w64 / LLVM-MinGW）"),
            new("clang", "Clang / LLVM"),
            new("cmake", "CMake"),
            new("cpp-runtime", "Microsoft Visual C++ 运行库"),
        ]),
        new("Java 系",
        [
            new("java", "Java 开发工具包（JDK）"),
            new("maven", "Apache Maven"),
            new("gradle", "Gradle 构建工具"),
        ]),
        new("Python",
        [
            new("python", "Python 解释器"),
        ]),
        new("Node 与其他语言",
        [
            new("node", "Node.js 运行时"),
            new("go", "Go 工具链"),
            new("rust", "Rust 工具链"),
            new("dotnet", ".NET SDK / 运行时"),
            new("php", "PHP"),
            new("ruby", "Ruby"),
        ]),
        new("工具与数据库",
        [
            new("git", "Git for Windows"),
            new("mysql", "MySQL 数据库"),
        ]),
    ];

    /// <summary>扁平化的全部条目（检测循环用）。</summary>
    public static ImmutableArray<RuntimeEntry> AllEntries { get; } =
        [.. All.SelectMany(static g => g.Entries)];
}
