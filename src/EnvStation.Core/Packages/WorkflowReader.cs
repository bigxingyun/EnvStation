using System.Collections.Immutable;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Toml;

namespace EnvStation.Core.Packages;

/// <summary>
/// 工作流读取器：把 <c>workflow.toml</c> 解析为 <see cref="WorkflowDocument"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用 TOML 而不是 YAML</b>：TOML 没有隐式类型推断（YAML 的 <c>no</c> 会变成布尔值）、
/// 没有锚点与引用展开、没有多文档流——这三点都能被用来构造"作者看到的"与"解析器执行的"不一致，
/// 属于供应链攻击面（需求 TH-4）。TOML 的表达力对本标准完全够用。
/// </para>
/// <para>
/// <b>为什么 <c>with</c> 里禁止嵌套表</b>：嵌套结构会为"参数里藏动作"提供空间。
/// 扁平参数使静态检查可以逐个参数判定类型与来源（需求 24.2）。
/// </para>
/// </remarks>
public static class WorkflowReader
{
    /// <summary>步骤嵌套深度上限。超过即判为错误构造——正常工作流不需要这么深。</summary>
    public const int MaxStepDepth = 8;

    /// <summary>单层分支（then/else/do/parallel）的步骤数上限。</summary>
    public const int MaxStepsPerBlock = 256;

    private static readonly ImmutableArray<string> CommonKeys =
        ["id", "description", "when", "on_error", "timeout", "register"];

    private static readonly ImmutableArray<string> ActionKeys =
        ["uses", "with", "pin"];

    private static readonly ImmutableArray<string> ConditionalKeys =
        ["if", "then", "else"];

    private static readonly ImmutableArray<string> ForEachKeys =
        ["foreach", "as", "max", "do"];

    private static readonly ImmutableArray<string> ParallelKeys =
        ["parallel"];

    private static readonly ImmutableArray<string> AssertKeys =
        ["assert", "message"];

    private static readonly ImmutableArray<string> ConfirmKeys =
        ["confirm", "default"];

    /// <summary>
    /// 解析并校验工作流。
    /// </summary>
    /// <param name="tomlText">工作流文件内容。</param>
    /// <param name="findings">发现收集器。</param>
    public static Result<WorkflowDocument> Read(string tomlText, FindingBag findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var parsed = TomlReader.Parse(tomlText);
        if (parsed.IsFailure)
        {
            findings.Block(
                EnvStationErrorCodes.PackageParseFailed,
                "工作流无法解析",
                parsed.Error.Message,
                parsed.Error.Remediation,
                "workflow.toml");
            return parsed.Propagate<WorkflowDocument>();
        }

        var root = parsed.Value;
        var specVersion = root.GetString("spec_version") ?? PackageManifest.CurrentSpecVersion;

        var defaultPolicy = ErrorPolicy.Rollback;
        if (root.Get("default_on_error") is TomlString policyText)
        {
            if (TryParsePolicy(policyText.Value, out var parsedPolicy))
            {
                defaultPolicy = parsedPolicy;
            }
            else
            {
                findings.Block(
                    "WF-01",
                    "default_on_error 取值非法",
                    $"第 {policyText.Line} 行：default_on_error 的值 {policyText.Value} 不是合法策略。",
                    "合法值：abort（中止）、continue（继续）、rollback（回滚，默认）。",
                    $"workflow.toml:{policyText.Line}");
            }
        }

        var stepsNode = root.Get("steps");
        if (stepsNode is null)
        {
            findings.Block(
                "WF-02",
                "工作流缺少步骤",
                "workflow.toml 中没有任何步骤。",
                "至少提供一个 [[steps]] 块。说明型包（T0-a）把 tier 设为 T0 并使用 ui.notify 动作。",
                "workflow.toml");
            return Result<WorkflowDocument>.Fail(EnvStationErrorCodes.PackageManifestInvalid, "工作流缺少步骤。");
        }

        if (stepsNode is not TomlArray stepsArray)
        {
            findings.Block(
                "WF-03",
                "steps 类型错误",
                $"第 {stepsNode.Line} 行：steps 必须是数组或数组表。",
                "写法：[[steps]] 逐块声明，或用 steps 数组声明内联表序列。",
                $"workflow.toml:{stepsNode.Line}");
            return Result<WorkflowDocument>.Fail(EnvStationErrorCodes.PackageManifestInvalid, "steps 类型错误。");
        }

        CheckUnknownKeys(root, ["spec_version", "default_on_error", "steps", "description", "name"], findings, "workflow 根");

        var steps = ParseBlock(stepsArray, findings, depth: 0, "steps", defaultPolicy);

        if (findings.HasBlockers)
        {
            return Result<WorkflowDocument>.Fail(
                EnvStationErrorCodes.PackageManifestInvalid,
                $"工作流存在 {findings.BlockCount} 项阻断问题，详见校验发现列表。");
        }

        return Result<WorkflowDocument>.Ok(new WorkflowDocument(specVersion, defaultPolicy, steps));
    }

    // ────────────────────────────── 块解析 ──────────────────────────────

    private static ImmutableArray<WorkflowStep> ParseBlock(
        TomlArray array,
        FindingBag findings,
        int depth,
        string path,
        ErrorPolicy defaultPolicy)
    {
        if (depth > MaxStepDepth)
        {
            findings.Block(
                EnvStationErrorCodes.WorkflowCycleDetected,
                "步骤嵌套过深",
                $"第 {array.Line} 行：{path} 的嵌套层级超过上限 {MaxStepDepth}。",
                "拆分为多个包，或改用 foreach 有限循环。",
                $"workflow.toml:{array.Line}");
            return ImmutableArray<WorkflowStep>.Empty;
        }

        if (array.Count > MaxStepsPerBlock)
        {
            findings.Block(
                "WF-04",
                "单层步骤过多",
                $"第 {array.Line} 行：{path} 包含 {array.Count} 个步骤，超过上限 {MaxStepsPerBlock}。",
                "拆分为多个包，或改用 foreach 循环。",
                $"workflow.toml:{array.Line}");
            return ImmutableArray<WorkflowStep>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<WorkflowStep>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var elementPath = $"{path}[{i}]";
            if (array[i] is not TomlTable table)
            {
                findings.Block(
                    "WF-05",
                    "步骤不是表",
                    $"第 {array[i].Line} 行：{elementPath} 的类型是 {array[i].TypeName}，步骤必须是表。",
                    "使用 [[steps]] 块，或在 steps 数组内放内联表。",
                    $"workflow.toml:{array[i].Line}");
                continue;
            }

            var step = ParseStep(table, findings, depth, elementPath, defaultPolicy);
            if (step is not null)
            {
                builder.Add(step);
            }
        }

        return builder.ToImmutable();
    }

    private static WorkflowStep? ParseStep(
        TomlTable table,
        FindingBag findings,
        int depth,
        string path,
        ErrorPolicy defaultPolicy)
    {
        var location = $"workflow.toml:{table.Line}";
        var id = table.GetString("id");
        var description = table.GetString("description");
        var when = table.GetString("when");
        var register = table.GetString("register");

        var onError = ReadPolicy(table, findings, location);
        var timeout = ReadTimeout(table, findings, location);

        // 步骤种类判定：刻意要求"恰好一种"，多种混写一律报错而不是猜。
        var kinds = new List<string>(5);
        if (table.ContainsKey("uses"))
        {
            kinds.Add("uses");
        }

        if (table.ContainsKey("if"))
        {
            kinds.Add("if");
        }

        if (table.ContainsKey("foreach"))
        {
            kinds.Add("foreach");
        }

        if (table.ContainsKey("parallel"))
        {
            kinds.Add("parallel");
        }

        if (table.ContainsKey("assert"))
        {
            kinds.Add("assert");
        }

        if (table.ContainsKey("confirm"))
        {
            kinds.Add("confirm");
        }

        if (kinds.Count == 0)
        {
            findings.Block(
                "WF-06",
                "步骤缺少动作",
                $"{path}（第 {table.Line} 行）没有声明任何动作。",
                "每一步必须且只能包含 uses / if / foreach / parallel / assert / confirm 之一。",
                location);
            return null;
        }

        if (kinds.Count > 1)
        {
            findings.Block(
                "WF-07",
                "步骤混用了多种控制结构",
                $"{path}（第 {table.Line} 行）同时包含 {string.Join("、", kinds)}。",
                "把控制结构拆成多个步骤，一个步骤只做一件事。",
                location);
            return null;
        }

        return kinds[0] switch
        {
            "uses" => ParseActionStep(table, findings, id, description, when, onError, timeout, register, path),
            "if" => ParseConditionalStep(table, findings, depth, id, description, when, onError, timeout, register, path, defaultPolicy),
            "foreach" => ParseForEachStep(table, findings, depth, id, description, when, onError, timeout, register, path, defaultPolicy),
            "parallel" => ParseParallelStep(table, findings, depth, id, description, when, onError, timeout, register, path, defaultPolicy),
            "assert" => ParseAssertStep(table, findings, id, description, when, onError, timeout, register, path),
            _ => ParseConfirmStep(table, findings, id, description, when, onError, timeout, register, path),
        };
    }

    private static WorkflowStep? ParseActionStep(
        TomlTable table,
        FindingBag findings,
        string? id,
        string? description,
        string? when,
        ErrorPolicy? onError,
        int? timeout,
        string? register,
        string path)
    {
        var location = $"workflow.toml:{table.Line}";
        var usesNode = table.Get("uses");
        if (usesNode is not TomlString usesText)
        {
            findings.Block(
                "WF-08",
                "uses 类型错误",
                $"{path}（第 {table.Line} 行）：uses 必须是字符串。",
                "示例：uses = 「envstation.env.set@1.0.0」。",
                location);
            return null;
        }

        string? hash = null;
        if (table.Get("pin") is TomlTable pin)
        {
            hash = pin.GetString("hash");
            if (string.IsNullOrWhiteSpace(hash))
            {
                findings.Block(
                    EnvStationErrorCodes.ActionPinMismatch,
                    "pin 缺少 hash",
                    $"{path}（第 {pin.Line} 行）：声明了 pin 却没有提供 hash。",
                    "pin.hash 形如 sha256:<64 位十六进制>。不需要固定实现哈希时删除整个 pin 表。",
                    location,
                    usesText.Value);
            }
        }

        var reference = ActionReference.Parse(usesText.Value, hash, usesText.Line);
        if (reference.IsFailure)
        {
            findings.Block(
                reference.Error.Code,
                "动作引用非法",
                reference.Error.Message,
                reference.Error.Remediation,
                location,
                usesText.Value);
            return null;
        }

        var arguments = ImmutableDictionary.CreateBuilder<string, ScriptValue>(StringComparer.Ordinal);
        if (table.Get("with") is TomlTable withTable)
        {
            foreach (var (key, node) in withTable.Entries)
            {
                if (node is TomlTable nested)
                {
                    findings.Block(
                        "WF-09",
                        "动作参数中出现嵌套表",
                        $"{path}.with.{key}（第 {nested.Line} 行）是嵌套表。",
                        "动作参数只接受标量或标量数组，改用扁平参数。",
                        location,
                        key);
                    continue;
                }

                var converted = TomlScriptValues.Convert(node);
                if (converted.IsFailure)
                {
                    findings.Block(
                        converted.Error.Code,
                        "动作参数无法转换",
                        converted.Error.Message,
                        converted.Error.Remediation,
                        location,
                        key);
                    continue;
                }

                arguments[key] = converted.Value;
            }
        }

        CheckUnknownKeys(table, [.. CommonKeys, .. ActionKeys], findings, path);

        return new ActionCallStep(
            id, description, when, onError, timeout, register, table.Line,
            reference.Value,
            arguments.ToImmutable());
    }

    private static WorkflowStep? ParseConditionalStep(
        TomlTable table,
        FindingBag findings,
        int depth,
        string? id,
        string? description,
        string? when,
        ErrorPolicy? onError,
        int? timeout,
        string? register,
        string path,
        ErrorPolicy defaultPolicy)
    {
        var location = $"workflow.toml:{table.Line}";
        var condition = table.GetString("if");
        if (string.IsNullOrWhiteSpace(condition))
        {
            findings.Block("WF-10", "if 条件为空", $"{path}（第 {table.Line} 行）：if 必须是非空表达式。", null, location);
            return null;
        }

        if (table.Get("then") is not TomlArray thenArray || thenArray.Count == 0)
        {
            findings.Block(
                "WF-11",
                "if 缺少 then 分支",
                $"{path}（第 {table.Line} 行）：条件步骤必须提供至少一个 then 步骤。",
                "写法：[[steps.then]] 后跟具体步骤。",
                location);
            return null;
        }

        var thenSteps = ParseBlock(thenArray, findings, depth + 1, $"{path}.then", defaultPolicy);
        var elseSteps = ImmutableArray<WorkflowStep>.Empty;
        if (table.Get("else") is TomlArray elseArray)
        {
            elseSteps = ParseBlock(elseArray, findings, depth + 1, $"{path}.else", defaultPolicy);
        }

        CheckUnknownKeys(table, [.. CommonKeys, .. ConditionalKeys], findings, path);

        return new ConditionalStep(
            id, description, when, onError, timeout, register, table.Line,
            condition, thenSteps, elseSteps);
    }

    private static WorkflowStep? ParseForEachStep(
        TomlTable table,
        FindingBag findings,
        int depth,
        string? id,
        string? description,
        string? when,
        ErrorPolicy? onError,
        int? timeout,
        string? register,
        string path,
        ErrorPolicy defaultPolicy)
    {
        var location = $"workflow.toml:{table.Line}";
        var source = table.GetString("foreach");
        if (string.IsNullOrWhiteSpace(source))
        {
            findings.Block(
                "WF-12",
                "foreach 数据源为空",
                $"{path}（第 {table.Line} 行）：foreach 必须指向一个变量或表达式。",
                "示例：foreach = 「${profile.mirrors}」。",
                location);
            return null;
        }

        if (table.GetString("as") is not { Length: > 0 } itemName)
        {
            findings.Block(
                "WF-13",
                "foreach 缺少 as",
                $"{path}（第 {table.Line} 行）：foreach 必须声明迭代变量名。",
                "示例：as = 「m」，循环体内用 ${m.target} 引用。",
                location);
            return null;
        }

        var max = ForEachStep.DefaultMaxIterations;
        if (table.Get("max") is TomlInteger maxNode)
        {
            if (maxNode.Value < 1 || maxNode.Value > ForEachStep.HardMaxIterations)
            {
                findings.Block(
                    EnvStationErrorCodes.WorkflowLoopLimitExceeded,
                    "foreach 上限非法",
                    $"{path}（第 {maxNode.Line} 行）：max = {maxNode.Value}，必须在 1 到 {ForEachStep.HardMaxIterations} 之间。",
                    "省略 max 时默认 100 次。",
                    location);
                return null;
            }

            max = (int)maxNode.Value;
        }

        if (table.Get("do") is not TomlArray body || body.Count == 0)
        {
            findings.Block(
                "WF-14",
                "foreach 缺少循环体",
                $"{path}（第 {table.Line} 行）：foreach 必须提供 do 步骤。",
                "写法：[[steps.do]] 后跟具体步骤。",
                location);
            return null;
        }

        CheckUnknownKeys(table, [.. CommonKeys, .. ForEachKeys], findings, path);

        return new ForEachStep(
            id, description, when, onError, timeout, register, table.Line,
            source, itemName, max,
            ParseBlock(body, findings, depth + 1, $"{path}.do", defaultPolicy));
    }

    private static WorkflowStep? ParseParallelStep(
        TomlTable table,
        FindingBag findings,
        int depth,
        string? id,
        string? description,
        string? when,
        ErrorPolicy? onError,
        int? timeout,
        string? register,
        string path,
        ErrorPolicy defaultPolicy)
    {
        var location = $"workflow.toml:{table.Line}";
        if (table.Get("parallel") is not TomlArray branches || branches.Count == 0)
        {
            findings.Block(
                "WF-15",
                "parallel 缺少分支",
                $"{path}（第 {table.Line} 行）：parallel 必须提供至少一个分支步骤。",
                "写法：[[steps.parallel]] 后跟具体步骤。",
                location);
            return null;
        }

        CheckUnknownKeys(table, [.. CommonKeys, .. ParallelKeys], findings, path);

        return new ParallelStep(
            id, description, when, onError, timeout, register, table.Line,
            ParseBlock(branches, findings, depth + 1, $"{path}.parallel", defaultPolicy));
    }

    private static WorkflowStep? ParseAssertStep(
        TomlTable table,
        FindingBag findings,
        string? id,
        string? description,
        string? when,
        ErrorPolicy? onError,
        int? timeout,
        string? register,
        string path)
    {
        var location = $"workflow.toml:{table.Line}";
        var condition = table.GetString("assert");
        if (string.IsNullOrWhiteSpace(condition))
        {
            findings.Block("WF-16", "assert 条件为空", $"{path}（第 {table.Line} 行）：assert 必须是非空表达式。", null, location);
            return null;
        }

        var message = table.GetString("message");
        if (string.IsNullOrWhiteSpace(message))
        {
            findings.Warn(
                "WF-17",
                "assert 缺少失败提示",
                $"{path}（第 {table.Line} 行）：断言失败时用户只会看到默认文案。",
                "填写 message，说明期望状态与不满足时的处理方式。",
                location);
            message = "断言未通过。";
        }

        CheckUnknownKeys(table, [.. CommonKeys, .. AssertKeys], findings, path);

        return new AssertStep(
            id, description, when, onError, timeout, register, table.Line,
            condition, message);
    }

    private static WorkflowStep? ParseConfirmStep(
        TomlTable table,
        FindingBag findings,
        string? id,
        string? description,
        string? when,
        ErrorPolicy? onError,
        int? timeout,
        string? register,
        string path)
    {
        var location = $"workflow.toml:{table.Line}";
        var message = table.GetString("confirm");
        if (string.IsNullOrWhiteSpace(message))
        {
            findings.Block("WF-18", "confirm 文案为空", $"{path}（第 {table.Line} 行）：confirm 必须给出要用户确认的内容。", null, location);
            return null;
        }

        CheckUnknownKeys(table, [.. CommonKeys, .. ConfirmKeys], findings, path);

        return new ConfirmStep(
            id, description, when, onError, timeout, register, table.Line,
            message, table.GetBoolean("default") ?? false);
    }

    // ────────────────────────────── 公共字段 ──────────────────────────────

    private static ErrorPolicy? ReadPolicy(TomlTable table, FindingBag findings, string location)
    {
        if (table.Get("on_error") is not TomlString node)
        {
            return null;
        }

        if (TryParsePolicy(node.Value, out var policy))
        {
            return policy;
        }

        findings.Block(
            "WF-19",
            "on_error 取值非法",
            $"第 {node.Line} 行：on_error 的值 {node.Value} 不是合法策略。",
            "合法值：abort（中止）、continue（继续）、rollback（回滚，默认）。",
            location);
        return null;
    }

    private static bool TryParsePolicy(string text, out ErrorPolicy policy)
    {
        switch (text)
        {
            case "abort":
                policy = ErrorPolicy.Abort;
                return true;
            case "continue":
                policy = ErrorPolicy.Continue;
                return true;
            case "rollback":
                policy = ErrorPolicy.Rollback;
                return true;
            default:
                policy = ErrorPolicy.Rollback;
                return false;
        }
    }

    private static int? ReadTimeout(TomlTable table, FindingBag findings, string location)
    {
        if (table.Get("timeout") is not TomlInteger node)
        {
            return null;
        }

        // 上限 1 小时：动作级超时是"卡死保护"，不是调度器。更长的等待应拆分为可观测的多步。
        if (node.Value < 1 || node.Value > 3600)
        {
            findings.Block(
                "WF-20",
                "timeout 超出范围",
                $"第 {node.Line} 行：timeout = {node.Value} 秒，必须在 1 到 3600 之间。",
                "需要长时间操作时拆分为多个步骤，以便显示进度与中断。",
                location);
            return null;
        }

        return (int)node.Value;
    }

    private static void CheckUnknownKeys(TomlTable table, ImmutableArray<string> known, FindingBag findings, string path)
    {
        foreach (var key in table.Keys)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                findings.Warn(
                    "WF-21",
                    "步骤包含未知字段",
                    $"{path}（第 {table.Line} 行）中的字段 {key} 不被识别。",
                    "可能是拼写错误。该字段会被忽略。",
                    $"workflow.toml:{table.Line}",
                    key);
            }
        }
    }
}
