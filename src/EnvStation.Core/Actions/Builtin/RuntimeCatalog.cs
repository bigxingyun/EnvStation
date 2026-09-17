using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace EnvStation.Core.Actions.Builtin;

/// <summary>
/// 一个已知运行时的探测定义。
/// </summary>
/// <param name="Kind">运行时种类标识（工作流里 <c>kind</c> 参数的取值）。</param>
/// <param name="DisplayName">中文展示名。</param>
/// <param name="Executables">用于判断"这个目录是不是该运行时"的可执行文件名。</param>
/// <param name="HomeEnvironmentVariables">常见的主目录环境变量（按优先级）。</param>
/// <param name="WellKnownDirectories">常见安装位置（支持 <c>%VAR%</c> 展开）。</param>
/// <param name="VersionDirectoryPattern">从目录名中提取版本号的正则（命名组 <c>v</c>）。</param>
internal sealed record RuntimeDefinition(
    string Kind,
    string DisplayName,
    ImmutableArray<string> Executables,
    ImmutableArray<string> HomeEnvironmentVariables,
    ImmutableArray<string> WellKnownDirectories,
    string VersionDirectoryPattern);

/// <summary>
/// 运行时探测目录。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不调用可执行文件问版本</b>：那需要 <c>CAP.PROCESS.LAUNCH</c>（高风险能力），
/// 而"系统里装了没有 Python"这种只读探测不应该要求运行程序的权限。
/// 本目录改用三条**无需运行任何程序**的证据链：环境变量指向的主目录、PATH 中的可执行文件、
/// 常见安装位置下的版本化目录名。只有显式的
/// <c>envstation.verify.version_output</c> 动作才会真正运行程序。
/// </para>
/// <para>
/// <b>数据与代码分离</b>：本表是内置兜底数据。按详细设计 5.3 节，正式的组件定义放在
/// <c>data/runtimes/&lt;id&gt;.json</c> 并支持热更新；内置表保证离线可用（需求 AC-9）。
/// </para>
/// </remarks>
internal static class RuntimeCatalog
{
    /// <summary>全部已知运行时（P0 三大件 + P1 常用依赖）。</summary>
    internal static ImmutableArray<RuntimeDefinition> All { get; } =
    [
        new("java", "Java 开发工具包（JDK）",
            ["java.exe"],
            ["JAVA_HOME"],
            [@"%ProgramFiles%\Java", @"%ProgramFiles%\Eclipse Adoptium", @"%ProgramFiles%\Microsoft\jdk",
             @"%ProgramFiles%\Amazon Corretto", @"%ProgramFiles%\Zulu", @"%ProgramFiles%\BellSoft",
             @"%ProgramFiles%\RedHat", @"%ProgramFiles%\Semeru", @"%LOCALAPPDATA%\Programs\Eclipse Adoptium"],
            @"^(?:jdk|jre|temurin|zulu|corretto|amazon-corretto|openjdk|microsoft-jdk)?-?(?<v>\d+(?:\.\d+){0,3}(?:[._-]\d+)?)$"),

        new("python", "Python 解释器",
            ["python.exe"],
            ["PYTHON_HOME", "PYTHON_ROOT"],
            [@"%LOCALAPPDATA%\Programs\Python", @"%ProgramFiles%\Python*", @"C:\Python*", @"%USERPROFILE%\AppData\Local\Programs\Python"],
            @"^(?:Python\s*)?(?<v>\d+\.\d+(?:\.\d+)?)$"),

        new("node", "Node.js 运行时",
            ["node.exe"],
            ["NODE_HOME", "NODEJS_HOME"],
            [@"%ProgramFiles%\nodejs", @"%ProgramFiles%\nodejs*", @"%LOCALAPPDATA%\Programs\nodejs", @"%APPDATA%\nvm"],
            @"^(?:node-)?v?(?<v>\d+\.\d+\.\d+)(?:-win-[a-z0-9]+)?$"),

        new("go", "Go 工具链",
            ["go.exe"],
            ["GOROOT"],
            [@"%ProgramFiles%\Go", @"C:\Go", @"%LOCALAPPDATA%\Programs\Go"],
            @"^go(?<v>\d+\.\d+(?:\.\d+)?)$"),

        new("dotnet", ".NET SDK / 运行时",
            ["dotnet.exe"],
            ["DOTNET_ROOT", "DOTNET_ROOT(x86)"],
            [@"%ProgramFiles%\dotnet", @"%ProgramFiles(x86)%\dotnet"],
            @"^(?:dotnet-)?(?<v>\d+\.\d+(?:\.\d+)?)$"),

        new("maven", "Apache Maven",
            ["mvn.cmd", "mvn.bat"],
            ["MAVEN_HOME", "M2_HOME"],
            [@"%ProgramFiles%\apache-maven*", @"%ProgramData%\chocolatey\lib\maven", @"%USERPROFILE%\apache-maven*", @"C:\apache-maven*"],
            @"^apache-maven-(?<v>\d+(?:\.\d+){1,2})$"),

        new("gradle", "Gradle 构建工具",
            ["gradle.bat", "gradle"],
            ["GRADLE_HOME"],
            [@"%ProgramFiles%\gradle*", @"%USERPROFILE%\gradle*", @"C:\Gradle\gradle*"],
            @"^(?:gradle-)?(?<v>\d+(?:\.\d+){1,2})$"),

        new("gcc", "GCC（MinGW-w64 / LLVM-MinGW）",
            ["gcc.exe"],
            ["MINGW_HOME"],
            [@"C:\mingw64", @"C:\msys64\mingw64", @"%ProgramFiles%\mingw64", @"%LOCALAPPDATA%\Programs\mingw64",
             @"C:\mingw32", @"C:\msys64\ucrt64", @"%ProgramFiles%\LLVM-MinGW*"],
            @"^(?<v>\d+(?:\.\d+){1,2})?.*$"),

        new("clang", "Clang / LLVM",
            ["clang.exe"],
            ["LLVM_HOME"],
            [@"%ProgramFiles%\LLVM", @"%ProgramFiles%\LLVM*", @"C:\LLVM", @"%ProgramFiles%\Microsoft Visual Studio\2022\*\VC\Tools\Llvm\x64\bin"],
            @"^(?:LLVM-?)?(?<v>\d+(?:\.\d+){1,2})?.*$"),

        new("cmake", "CMake",
            ["cmake.exe"],
            ["CMAKE_ROOT"],
            [@"%ProgramFiles%\CMake", @"%ProgramFiles%\CMake\bin", @"%LOCALAPPDATA%\Programs\CMake"],
            @"^(?:cmake-)?(?<v>\d+(?:\.\d+){1,2})?.*$"),

        new("git", "Git for Windows",
            ["git.exe"],
            ["GIT_HOME"],
            [@"%ProgramFiles%\Git", @"%LOCALAPPDATA%\Programs\Git", @"%ProgramFiles(x86)%\Git"],
            @"^Git$"),

        new("mysql", "MySQL 数据库",
            ["mysql.exe"],
            ["MYSQL_HOME"],
            [@"%ProgramFiles%\MySQL\MySQL Server *", @"%ProgramFiles%\MySQL", @"C:\mysql*", @"%ProgramData%\MySQL"],
            @"^(?:MySQL Server\s*)?(?<v>\d+\.\d+(?:\.\d+)?)$"),

        new("rust", "Rust 工具链",
            ["cargo.exe", "rustc.exe"],
            ["CARGO_HOME", "RUSTUP_HOME"],
            [@"%USERPROFILE%\.cargo\bin", @"%USERPROFILE%\.rustup\toolchains"],
            @"^(?:stable|nightly|beta)?-?(?<v>\d+\.\d+(?:\.\d+)?)?.*$"),

        new("php", "PHP",
            ["php.exe"],
            ["PHP_HOME"],
            [@"C:\php", @"C:\php*", @"%ProgramFiles%\php*"],
            @"^(?:php-?)?(?<v>\d+\.\d+(?:\.\d+)?)?.*$"),

        new("ruby", "Ruby",
            ["ruby.exe"],
            ["RUBY_HOME"],
            [@"C:\Ruby*", @"%ProgramFiles%\Ruby*"],
            @"^Ruby(?<v>\d+(?:\.\d+){1,2})?.*$"),

        new("cpp-runtime", "Microsoft Visual C++ 运行库",
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,
            [@"%SystemRoot%\System32"],
            @"^(?<v>\d+(?:\.\d+)*)$"),
    ];

    private static readonly ImmutableDictionary<string, RuntimeDefinition> Index =
        All.ToImmutableDictionary(static d => d.Kind, StringComparer.Ordinal);

    /// <summary>全部已知 kind（用于校验期给出可选值）。</summary>
    internal static ImmutableArray<string> Kinds { get; } = [.. All.Select(static d => d.Kind)];

    /// <summary>按 kind 查找定义。</summary>
    internal static bool TryGet(string kind, out RuntimeDefinition definition) =>
        Index.TryGetValue(kind, out definition!);

    /// <summary>从目录名中提取版本号。</summary>
    internal static string? ExtractVersionFromDirectoryName(RuntimeDefinition definition, string directoryName)
    {
        if (string.IsNullOrEmpty(directoryName))
        {
            return null;
        }

        try
        {
            var match = Regex.Match(
                directoryName,
                definition.VersionDirectoryPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50));

            if (match.Success && match.Groups["v"].Success)
            {
                return match.Groups["v"].Value;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }
}
