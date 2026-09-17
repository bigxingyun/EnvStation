using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using EnvStation.Abstractions.Actions;

namespace EnvStation.Abstractions.Packages;

/// <summary>
/// 脚本值。工作流参数值的中间表示——刻意与 TOML 解析器解耦，
/// 使 <c>EnvStation.Abstractions</c> 不依赖任何具体序列化格式（DL-1 依赖规则）。
/// </summary>
public abstract record ScriptValue(int Line)
{
    /// <summary>类型名的中文描述，用于校验期错误信息。</summary>
    public abstract string TypeName { get; }

    /// <summary>转为字符串（用于提示与日志；数组与表不参与此转换）。</summary>
    public abstract string ToDisplayString();
}

/// <summary>字符串值。</summary>
public sealed record ScriptString(string Value, int Line = 0) : ScriptValue(Line)
{
    /// <inheritdoc />
    public override string TypeName => "字符串";

    /// <inheritdoc />
    public override string ToDisplayString() => Value;
}

/// <summary>整数值。</summary>
public sealed record ScriptInteger(long Value, int Line = 0) : ScriptValue(Line)
{
    /// <inheritdoc />
    public override string TypeName => "整数";

    /// <inheritdoc />
    public override string ToDisplayString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>浮点数值。</summary>
public sealed record ScriptFloat(double Value, int Line = 0) : ScriptValue(Line)
{
    /// <inheritdoc />
    public override string TypeName => "浮点数";

    /// <inheritdoc />
    public override string ToDisplayString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>布尔值。</summary>
public sealed record ScriptBoolean(bool Value, int Line = 0) : ScriptValue(Line)
{
    /// <inheritdoc />
    public override string TypeName => "布尔值";

    /// <inheritdoc />
    public override string ToDisplayString() => Value ? "true" : "false";
}

/// <summary>字符串数组。</summary>
public sealed record ScriptArray(ImmutableArray<ScriptValue> Items, int Line = 0) : ScriptValue(Line), IReadOnlyList<ScriptValue>
{
    /// <inheritdoc />
    public override string TypeName => "数组";

    /// <inheritdoc />
    public override string ToDisplayString() => $"[{string.Join(", ", Items.Select(static i => i.ToDisplayString()))}]";

    /// <inheritdoc />
    public int Count => Items.Length;

    /// <inheritdoc />
    public ScriptValue this[int index] => Items[index];

    /// <inheritdoc />
    public IEnumerator<ScriptValue> GetEnumerator() => ((IEnumerable<ScriptValue>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// 动作引用（需求 20.4）。<c>uses</c> 字段的三段式全限定 ID 加版本，配合 <c>pin.hash</c>。
/// </summary>
/// <param name="ActionId">三段式动作 ID，如 <c>envstation.env.set</c> 或 <c>x-zhangsan.foo.bar</c>。</param>
/// <param name="Version">语义化版本，禁止 latest / *。</param>
/// <param name="Hash">实现内容哈希，形如 <c>sha256:abcd...</c>；缺失即校验失败（需求 S3）。</param>
public sealed record ActionReference(string ActionId, string Version, string? Hash)
{
    /// <summary>官方动作命名空间前缀。</summary>
    public const string OfficialPrefix = "envstation.";

    /// <summary>第三方动作命名空间前缀。</summary>
    public const string ThirdPartyPrefix = "x-";

    /// <summary>是否为官方动作。</summary>
    public bool IsOfficial => ActionId.StartsWith(OfficialPrefix, StringComparison.Ordinal);

    /// <summary>是否为第三方动作。</summary>
    public bool IsThirdParty => ActionId.StartsWith(ThirdPartyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 解析 <c>uses</c> 字段。格式：<c>&lt;动作ID&gt;@&lt;版本&gt;</c>。
    /// </summary>
    /// <remarks>
    /// 刻意不做任何"宽松解析"：缺少 <c>@</c>、使用 <c>latest</c> / <c>*</c>、ID 段数不足
    /// 一律判为失败——需求 S2 要求"禁止动态/浮动版本"，宽松解析会让静态分析失去意义。
    /// </remarks>
    public static Result<ActionReference> Parse(string uses, string? hash, int line)
    {
        if (string.IsNullOrWhiteSpace(uses))
        {
            return Result<ActionReference>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                $"第 {line} 行：uses 不能为空。");
        }

        var at = uses.LastIndexOf('@');
        if (at <= 0 || at == uses.Length - 1)
        {
            return Result<ActionReference>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                $"第 {line} 行：动作引用 {uses} 缺少版本。格式必须为 <动作ID>@<语义化版本>，例如 envstation.env.set@1.4.0。");
        }

        var id = uses[..at];
        var version = uses[(at + 1)..];

        if (version is "latest" or "*" || version.Contains('*', StringComparison.Ordinal))
        {
            return Result<ActionReference>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                $"第 {line} 行：动作引用 {uses} 使用了浮动版本 {version}，必须改为具体的语义化版本。");
        }

        var segments = id.Split('.');
        if (segments.Length < 3)
        {
            return Result<ActionReference>.Fail(
                EnvStationErrorCodes.PackageNamespaceViolation,
                $"第 {line} 行：动作 ID {id} 不是三段式命名。必须形如 envstation.<域>.<动作> 或 x-<作者ID>.<域>.<动作>。");
        }

        if (!id.StartsWith(OfficialPrefix, StringComparison.Ordinal)
            && !id.StartsWith(ThirdPartyPrefix, StringComparison.Ordinal))
        {
            return Result<ActionReference>.Fail(
                EnvStationErrorCodes.PackageNamespaceViolation,
                $"第 {line} 行：动作 ID {id} 未使用合法命名空间。官方动作用 envstation. 前缀，第三方动作用 x-<作者ID>. 前缀。");
        }

        if (id.StartsWith(OfficialPrefix, StringComparison.Ordinal) && segments.Length != 3)
        {
            return Result<ActionReference>.Fail(
                EnvStationErrorCodes.PackageNamespaceViolation,
                $"第 {line} 行：官方动作 ID {id} 必须恰好三段（envstation.<域>.<动作>）。");
        }

        // 需求 S5：变量插值只能出现在参数值位置，不得出现在动作 ID 位置。
        if (id.Contains("${", StringComparison.Ordinal))
        {
            return Result<ActionReference>.Fail(
                EnvStationErrorCodes.PackageNamespaceViolation,
                $"第 {line} 行：动作 ID 中不允许出现变量插值标记，变量只能出现在参数值里。");
        }

        return Result<ActionReference>.Ok(new ActionReference(id, version, hash));
    }

    public override string ToString() => $"{ActionId}@{Version}";
}

/// <summary>步骤失败策略（需求 20.5）。</summary>
public enum ErrorPolicy
{
    /// <summary>中止整个工作流，保留已完成的变更（需用户手动处理）。</summary>
    Abort = 0,

    /// <summary>继续执行后续步骤（仅用于探测型与可忽略型步骤）。</summary>
    Continue = 1,

    /// <summary>中止并回滚到工作流开始时的快照——<b>默认策略</b>。</summary>
    Rollback = 2,
}

/// <summary>工作流步骤基类。所有步骤共有 id / when / on_error / timeout / register。</summary>
/// <param name="Id">步骤 ID（可选，但引用它做日志与报告时建议填写）。</param>
/// <param name="Description">中文说明，会出现在预演与执行报告中。</param>
/// <param name="When">条件表达式；为空表示无条件执行。</param>
/// <param name="OnError">失败策略；null 表示继承工作流默认值。</param>
/// <param name="TimeoutSeconds">步骤级超时秒数；null 表示使用动作默认超时。</param>
/// <param name="Register">把结果绑定到的变量名（写入 <c>pkg.*</c> 作用域）。</param>
/// <param name="Line">源码行号。</param>
public abstract record WorkflowStep(
    string? Id,
    string? Description,
    string? When,
    ErrorPolicy? OnError,
    int? TimeoutSeconds,
    string? Register,
    int Line)
{
    /// <summary>步骤种类的中文名，用于报告与错误信息。</summary>
    public abstract string KindName { get; }
}

/// <summary>动作调用步骤。</summary>
public sealed record ActionCallStep(
    string? Id,
    string? Description,
    string? When,
    ErrorPolicy? OnError,
    int? TimeoutSeconds,
    string? Register,
    int Line,
    ActionReference Reference,
    ImmutableDictionary<string, ScriptValue> Arguments)
    : WorkflowStep(Id, Description, When, OnError, TimeoutSeconds, Register, Line)
{
    /// <inheritdoc />
    public override string KindName => "动作调用";
}

/// <summary>条件分支步骤。</summary>
public sealed record ConditionalStep(
    string? Id,
    string? Description,
    string? When,
    ErrorPolicy? OnError,
    int? TimeoutSeconds,
    string? Register,
    int Line,
    string Condition,
    ImmutableArray<WorkflowStep> Then,
    ImmutableArray<WorkflowStep> Else)
    : WorkflowStep(Id, Description, When, OnError, TimeoutSeconds, Register, Line)
{
    /// <inheritdoc />
    public override string KindName => "条件分支";
}

/// <summary>有限循环步骤。</summary>
public sealed record ForEachStep(
    string? Id,
    string? Description,
    string? When,
    ErrorPolicy? OnError,
    int? TimeoutSeconds,
    string? Register,
    int Line,
    string Source,
    string ItemName,
    int MaxIterations,
    ImmutableArray<WorkflowStep> Body)
    : WorkflowStep(Id, Description, When, OnError, TimeoutSeconds, Register, Line)
{
    /// <summary>循环次数硬上限（需求 20.5）。</summary>
    public const int HardMaxIterations = 1000;

    /// <summary>未声明 <c>max</c> 时的默认上限。</summary>
    public const int DefaultMaxIterations = 100;

    /// <inheritdoc />
    public override string KindName => "有限循环";
}

/// <summary>并行步骤。<b>仅允许声明为可并行的动作</b>出现在分支中（需求 20.5）。</summary>
public sealed record ParallelStep(
    string? Id,
    string? Description,
    string? When,
    ErrorPolicy? OnError,
    int? TimeoutSeconds,
    string? Register,
    int Line,
    ImmutableArray<WorkflowStep> Branches)
    : WorkflowStep(Id, Description, When, OnError, TimeoutSeconds, Register, Line)
{
    /// <inheritdoc />
    public override string KindName => "并行";
}

/// <summary>断言步骤。失败按 <c>on_error</c> 处理。</summary>
public sealed record AssertStep(
    string? Id,
    string? Description,
    string? When,
    ErrorPolicy? OnError,
    int? TimeoutSeconds,
    string? Register,
    int Line,
    string Condition,
    string Message)
    : WorkflowStep(Id, Description, When, OnError, TimeoutSeconds, Register, Line)
{
    /// <inheritdoc />
    public override string KindName => "断言";
}

/// <summary>用户确认步骤：在预演之外，执行到该点时必须得到用户明确同意。</summary>
public sealed record ConfirmStep(
    string? Id,
    string? Description,
    string? When,
    ErrorPolicy? OnError,
    int? TimeoutSeconds,
    string? Register,
    int Line,
    string Message,
    bool DefaultYes)
    : WorkflowStep(Id, Description, When, OnError, TimeoutSeconds, Register, Line)
{
    /// <inheritdoc />
    public override string KindName => "用户确认";
}

/// <summary>
/// 工作流文档（<c>workflow.toml</c> 的强类型形态）。
/// </summary>
/// <param name="SpecVersion">标准版本，与 <see cref="PackageManifest.SpecVersion"/> 一致。</param>
/// <param name="DefaultErrorPolicy">未在步骤上声明时使用的默认失败策略。</param>
/// <param name="Steps">顶层步骤序列。</param>
public sealed record WorkflowDocument(
    string SpecVersion,
    ErrorPolicy DefaultErrorPolicy,
    ImmutableArray<WorkflowStep> Steps)
{
    /// <summary>深度优先枚举所有步骤（含嵌套），用于静态检查与权限汇总。</summary>
    public IEnumerable<WorkflowStep> Flatten()
    {
        var stack = new Stack<WorkflowStep>(Steps.Reverse());
        while (stack.Count > 0)
        {
            var step = stack.Pop();
            yield return step;
            switch (step)
            {
                case ConditionalStep c:
                    foreach (var s in c.Else.Reverse())
                    {
                        stack.Push(s);
                    }

                    foreach (var s in c.Then.Reverse())
                    {
                        stack.Push(s);
                    }

                    break;
                case ForEachStep f:
                    foreach (var s in f.Body.Reverse())
                    {
                        stack.Push(s);
                    }

                    break;
                case ParallelStep p:
                    foreach (var s in p.Branches.Reverse())
                    {
                        stack.Push(s);
                    }

                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>汇总工作流中出现的全部动作引用（去重，按 ID 排序）。</summary>
    public ImmutableArray<ActionReference> ReferencedActions() =>
        [.. Flatten()
            .OfType<ActionCallStep>()
            .Select(static s => s.Reference)
            .DistinctBy(static r => r.ActionId, StringComparer.Ordinal)
            .OrderBy(static r => r.ActionId, StringComparer.Ordinal)];
}

/// <summary>
/// 包清单（<c>envstation.toml</c> 的强类型形态，字段对应需求 25.2）。
/// </summary>
public sealed record PackageManifest
{
    /// <summary>当前客户端支持的标准版本（需求 STD-1 / M13-7）。</summary>
    public const string CurrentSpecVersion = "1.0";

    /// <summary>客户端向后兼容的标准大版本数（需求 STD-2）。</summary>
    public const int BackwardCompatibleMajorVersions = 2;

    /// <summary>标准版本，形如 <c>1.0</c>。</summary>
    public required string SpecVersion { get; init; }

    /// <summary>包 ID。官方包 <c>envstation.&lt;名&gt;</c>，第三方包 <c>x-&lt;作者ID&gt;.&lt;名&gt;</c>。</summary>
    public required string Id { get; init; }

    /// <summary>包版本（语义化版本）。</summary>
    public required string Version { get; init; }

    /// <summary>展示名。</summary>
    public required string Name { get; init; }

    /// <summary>描述。</summary>
    public string? Description { get; init; }

    /// <summary>作者信息。</summary>
    public PackageAuthor? Author { get; init; }

    /// <summary>许可证标识（如 MIT）。</summary>
    public string? License { get; init; }

    /// <summary>能力档位：T0 / T1 / T2 / T3（需求 20.3）。</summary>
    public required string Tier { get; init; }

    /// <summary>运行环境要求。</summary>
    public PackageRequirements? Requirements { get; init; }

    /// <summary>目标运行时声明（可选）。</summary>
    public PackageRuntimeTarget? Runtime { get; init; }

    /// <summary>能力声明：能力 ID 到自然语言说明的映射（需求 IMP-2）。</summary>
    public ImmutableDictionary<string, string> Permissions { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>网络域名白名单（需求 AI-4：强制声明）。</summary>
    public ImmutableArray<string> AllowedHosts { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>副作用声明（V4 会核对，需求 25.2）。</summary>
    public PackageSideEffects? SideEffects { get; init; }

    /// <summary>质量声明。</summary>
    public PackageQuality? Quality { get; init; }

    /// <summary>源文件行号（用于错误定位）。</summary>
    public int Line { get; init; }

    /// <summary>包 ID 是否使用合法命名空间。</summary>
    public bool HasValidNamespace =>
        Id.StartsWith(ActionReference.OfficialPrefix, StringComparison.Ordinal)
        || Id.StartsWith(ActionReference.ThirdPartyPrefix, StringComparison.Ordinal);

    /// <summary>声明的能力 ID 集合。</summary>
    public CapabilitySet DeclaredCapabilities()
    {
        var ids = Permissions.Keys
            .Where(static k => Capabilities.IsKnown(k))
            .ToArray();
        return new CapabilitySet(ids);
    }
}

/// <summary>作者信息。</summary>
/// <param name="Name">显示名。</param>
/// <param name="Id">稳定 ID（第三方命名空间使用）。</param>
/// <param name="Contact">联系方式。</param>
public sealed record PackageAuthor(string Name, string? Id, string? Contact);

/// <summary>运行环境要求。</summary>
/// <param name="Os">系统版本约束，如 <c>&gt;=10.0.17763</c>。</param>
/// <param name="Arch">支持的架构集合，如 <c>["x64","arm64"]</c>。</param>
/// <param name="DiskBytes">所需磁盘空间。</param>
public sealed record PackageRequirements(string? Os, ImmutableArray<string> Arch, long? DiskBytes);

/// <summary>目标运行时声明。</summary>
/// <param name="Kind">运行时种类，如 <c>python</c>。</param>
/// <param name="Version">版本约束，如 <c>&gt;=3.12</c>。</param>
public sealed record PackageRuntimeTarget(string? Kind, string? Version);

/// <summary>副作用声明。导入时会与静态检查结果交叉核对（声明不符即判失败）。</summary>
/// <param name="WritesEnv">是否写入环境变量。</param>
/// <param name="WritesFiles">是否写入文件。</param>
/// <param name="ModifiesConfig">会修改的配置文件列表。</param>
/// <param name="Irreversible">是否存在不可逆操作。</param>
/// <param name="ReversibleBy">可逆性说明。</param>
public sealed record PackageSideEffects(
    bool WritesEnv,
    bool WritesFiles,
    ImmutableArray<string> ModifiesConfig,
    bool Irreversible,
    string? ReversibleBy);

/// <summary>质量声明（进入质量评分卡的初筛项，需求 24.3）。</summary>
/// <param name="HasTests">是否提供测试用例。</param>
/// <param name="HasUninstall">是否提供卸载流程。</param>
/// <param name="Readme">README 相对路径。</param>
public sealed record PackageQuality(bool HasTests, bool HasUninstall, string? Readme);

/// <summary>脚本值的构造工具。</summary>
public static class ScriptValues
{
    /// <summary>把脚本值渲染为用于审计日志的单行文本（不截断；截断由调用方按需处理）。</summary>
    public static string Render(ScriptValue value) => value switch
    {
        ScriptString s => s.Value,
        ScriptArray a => string.Join(", ", a.Items.Select(Render)),
        _ => value.ToDisplayString(),
    };

    /// <summary>判断脚本值是否为可参与插值的标量。</summary>
    public static bool IsScalar(ScriptValue value) => value is not ScriptArray;

    /// <summary>把脚本值规范化：字符串数组补齐为不可变数组。</summary>
    public static ScriptArray EmptyArray { get; } = new(ImmutableArray<ScriptValue>.Empty, 0);

    /// <summary>调试输出。</summary>
    public static string Debug(ScriptValue value)
    {
        var sb = new StringBuilder();
        Append(sb, value);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, ScriptValue value)
    {
        switch (value)
        {
            case ScriptArray a:
                sb.Append('[');
                for (var i = 0; i < a.Items.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Append(sb, a.Items[i]);
                }

                sb.Append(']');
                break;
            case ScriptString s:
                sb.Append('"').Append(s.Value).Append('"');
                break;
            default:
                sb.Append(value.ToDisplayString());
                break;
        }
    }
}
