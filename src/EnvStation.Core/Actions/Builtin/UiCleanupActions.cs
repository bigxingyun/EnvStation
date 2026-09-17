using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Core.Environment;

namespace EnvStation.Core.Actions.Builtin;

// ══════════════════════════════════════════════════════════════════════════════
//  A10 交互与报告（5 个动作） · A11 清理与生命周期（4 个动作）
//
//  A10 的两条纪律：
//    · 用户输入永远是**数据**，绝不进入命令行（AI-3）；
//    · 文案只做展示，不做"伪装系统弹窗"这类误导性呈现（需求 21.2 A10）。
//
//  A11 的两条纪律：
//    · 只清理自己的东西——临时目录、缓存、本包私有目录；
//    · 删除前必须确认目标落在授权范围内，且拒绝任何"指向包外"的参数。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary><c>envstation.ui.notify</c>：展示提示。</summary>
internal sealed class UiNotifyAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.ui.notify",
        "1.0.0",
        CapabilityIds.UserInteraction,
        "展示提示",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 10,
        TouchedResources: [],
        Parameters:
        [
            new ParameterSpec("level", ParameterType.Enum, true, "提示级别",
                AllowedValues: ["info", "success", "warning", "error"]),
            Str("message", true, "提示内容", maxLength: 1024),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var levelText = arguments.GetString("level")!;
        var message = arguments.GetString("message")!;

        var level = levelText switch
        {
            "success" => NotifyLevel.Success,
            "warning" => NotifyLevel.Warning,
            "error" => NotifyLevel.Error,
            _ => NotifyLevel.Info,
        };

        // 长度截断 + 转义由 UI 层负责；这里只保证不把超长文本灌进界面。
        var shown = message.Length <= 1024 ? message : message[..1024] + "…";

        context.Interaction.Notify(level, shown);
        context.Audit("ui.notify", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["level"] = levelText,
            ["message"] = shown,
        });

        return Ok(
            shown,
            Outputs(("level", levelText), ("shown", "true")));
    }
}

/// <summary><c>envstation.ui.prompt</c>：向用户收集输入。</summary>
internal sealed class UiPromptAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.ui.prompt",
        "1.0.0",
        CapabilityIds.UserInteraction,
        "向用户提问",
        IsIdempotent: false,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: true,
        DefaultTimeoutSeconds: 600,
        TouchedResources: [],
        Parameters:
        [
            Str("question", true, "问题内容", maxLength: 512),
            new ParameterSpec("type", ParameterType.Enum, false, "输入类型",
                AllowedValues: ["text", "confirm", "choice"]),
            Str("default", false, "默认值（用户直接确认时使用）", maxLength: 1024),
            StrArray("choices", false, "可选值（type 为 choice 时必需）"),
            Bool("secret", "是否为敏感输入（界面掩码显示，且不写入日志与报告）", false),
        ],
        DegradesWhenUnattended: true);

    /// <inheritdoc />
    protected override async ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var question = arguments.GetString("question")!;
        var typeText = arguments.GetString("type") ?? "text";
        var defaultValue = arguments.GetString("default");
        var choices = arguments.GetStringArray("choices");
        var isSecret = arguments.GetBoolean("secret", false);

        var promptType = typeText switch
        {
            "confirm" => PromptType.Confirm,
            "choice" => PromptType.Choice,
            _ => PromptType.Text,
        };

        if (promptType == PromptType.Choice && choices.Length == 0)
        {
            return FailResult(
                EnvStationErrorCodes.ActionArgumentInvalid,
                "type 为 choice 时必须提供 choices（可选项列表）。");
        }

        // 无人值守：不能静默使用默认值——那会让"用户确认过了"变成一句空话。
        if (context.Unattended)
        {
            if (defaultValue is not null)
            {
                context.Audit("ui.prompt.unattended_default", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["question"] = question,
                    ["used_default"] = "true",
                });

                return OkResult(
                    $"无人值守：问题「{question}」已使用默认值。",
                    Outputs(("answered", "true"), ("value", defaultValue), ("from_default", "true")));
            }

            return FailResult(
                EnvStationErrorCodes.UnattendedInteraction,
                $"无人值守模式下无法向用户提问「{question}」，且未提供默认值。",
                "为该步骤提供 default，或在带界面的环境中运行。");
        }

        var answer = await context.Interaction
            .PromptAsync(new PromptRequest(question, promptType, defaultValue, choices, isSecret), cancellationToken)
            .ConfigureAwait(false);

        if (!answer.Answered)
        {
            return FailResult(
                EnvStationErrorCodes.WorkflowAborted,
                $"用户取消了对「{question}」的回答。");
        }

        // 敏感输入不进审计：只记录"回答过了"，不记录内容。
        context.Audit("ui.prompt.answered", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["question"] = question,
            ["from_default"] = Bool(answer.FromDefault),
            ["is_secret"] = Bool(isSecret),
        });

        if (isSecret)
        {
            // 敏感值同时写入 secret.* 作用域：变量表会登记它，从而在日志/报告中自动脱敏。
            context.VariableTable.Set($"secret.{SanitizeName(question)}", answer.Value);
        }

        return OkResult(
            isSecret
                ? $"已收到「{question}」的回答（内容已按敏感信息处理）。"
                : $"「{question}」的回答：{answer.Value}",
            Outputs(
                ("answered", "true"),
                ("value", isSecret ? "***" : answer.Value),
                ("from_default", Bool(answer.FromDefault))));
    }

    /// <summary>把问题文本变成一个可用作变量名的片段。</summary>
    private static string SanitizeName(string question)
    {
        var chars = question.Where(char.IsAsciiLetterOrDigit).Take(24).ToArray();
        return chars.Length == 0 ? "answer" : new string(chars).ToLowerInvariant();
    }
}

/// <summary><c>envstation.ui.progress</c>：上报进度。</summary>
internal sealed class UiProgressAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.ui.progress",
        "1.0.0",
        CapabilityIds.UserInteraction,
        "上报进度",
        IsIdempotent: true,
        IsParallelSafe: true,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 10,
        TouchedResources: [],
        Parameters:
        [
            new ParameterSpec("percent", ParameterType.Integer, true, "进度百分比（0~100）",
                Minimum: 0, Maximum: 100),
            Str("message", false, "进度说明", maxLength: 256),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var percent = (int)arguments.GetInt64("percent");
        var message = arguments.GetString("message");

        context.ReportProgress(percent, message);
        return Ok(
            string.IsNullOrEmpty(message) ? $"进度 {percent}%" : $"进度 {percent}%：{message}",
            Outputs(("percent", percent.ToString(CultureInfo.InvariantCulture))));
    }
}

/// <summary><c>envstation.report.generate</c>：生成执行报告。</summary>
internal sealed class ReportGenerateAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.report.generate",
        "1.0.0",
        CapabilityIds.Cleanup,
        "生成执行报告",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 30,
        TouchedResources: ["filesystem.write"],
        Parameters:
        [
            new ParameterSpec("format", ParameterType.Enum, false, "报告格式",
                AllowedValues: ["markdown", "json", "text"]),
            Path_("out", false, "输出文件路径，省略时只返回报告内容不落盘"),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var formatText = arguments.GetString("format") ?? "markdown";
        var outPath = arguments.GetString("out");

        var format = formatText switch
        {
            "json" => ReportFormat.Json,
            "text" => ReportFormat.Text,
            _ => ReportFormat.Markdown,
        };

        var content = RunReportBuilder.Build(context, format);

        if (outPath is null)
        {
            return Ok(
                $"报告已生成（{formatText}，{content.Length} 字符）。",
                Outputs(
                    ("format", formatText),
                    ("content", content),
                    ("written_to", string.Empty)));
        }

        var guard = context.GuardPath(outPath, "out");
        if (guard.IsFailure)
        {
            return Fail(guard.Error.Code, guard.Error.Message + " " + guard.Error.Remediation);
        }

        var directory = Path.GetDirectoryName(guard.Value);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            File.WriteAllText(guard.Value, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"报告写入失败：{ex.Message}");
        }

        return Ok(
            $"报告已写入 {guard.Value}（{formatText}）。",
            Outputs(
                ("format", formatText),
                ("written_to", guard.Value),
                ("bytes", new FileInfo(guard.Value).Length.ToString(CultureInfo.InvariantCulture))),
            touched: [guard.Value]);
    }
}

/// <summary>执行报告生成器（<c>report.generate</c> 与 CLI 共用）。</summary>
internal static class RunReportBuilder
{
    internal static string Build(ActionExecutionContext context, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ledger = context.Ledger;
        return format switch
        {
            ReportFormat.Json => BuildJson(context, ledger),
            ReportFormat.Text => BuildText(context, ledger),
            _ => BuildMarkdown(context, ledger),
        };
    }

    private static string BuildMarkdown(ActionExecutionContext context, RunLedger ledger)
    {
        var sb = new StringBuilder();
        var redact = context.VariableTable.Redact;

        sb.Append("# 环境站执行报告\n\n");
        sb.Append("| 项 | 值 |\n| --- | --- |\n");
        sb.Append("| 包 | ").Append(ledger.PackageId).Append(" |\n");
        sb.Append("| 运行 ID | ").Append(ledger.RunId).Append(" |\n");
        sb.Append("| 开始时间 | ").Append(ledger.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" |\n");
        if (ledger.CompletedAt is { } completed)
        {
            sb.Append("| 结束时间 | ").Append(completed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" |\n");
        }

        sb.Append("| 结论 | ").Append(redact(ledger.Outcome ?? "进行中")).Append(" |\n");
        sb.Append("| 步骤数 | ").Append(ledger.Invocations.Count.ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        sb.Append("| 失败数 | ").Append(ledger.Failures().Count().ToString(CultureInfo.InvariantCulture)).Append(" |\n\n");

        sb.Append("## 步骤明细\n\n");
        sb.Append("| # | 动作 | 结果 | 耗时 | 说明 |\n| --- | --- | --- | --- | --- |\n");
        var index = 1;
        foreach (var invocation in ledger.Invocations)
        {
            sb.Append("| ").Append(index++.ToString(CultureInfo.InvariantCulture))
              .Append(" | `").Append(invocation.ActionId).Append('`')
              .Append(" | ").Append(invocation.Succeeded ? "成功" : "**失败**")
              .Append(" | ").Append(((long)invocation.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)).Append(" ms")
              .Append(" | ").Append(redact(OneLine(invocation.Message)))
              .Append(" |\n");
        }

        var touched = ledger.TouchedPaths();
        if (touched.Length > 0)
        {
            sb.Append("\n## 触碰的文件与环境\n\n");
            foreach (var path in touched)
            {
                sb.Append("- `").Append(path).Append("`\n");
            }
        }

        var tokens = ledger.ReversibleTokens();
        if (tokens.Length > 0)
        {
            sb.Append("\n## 可回滚快照\n\n");
            sb.Append("以下快照可用于回滚本次全部修改：\n\n");
            foreach (var token in tokens)
            {
                sb.Append("- `").Append(token).Append("`\n");
            }
        }

        var failures = ledger.Failures().ToArray();
        if (failures.Length > 0)
        {
            sb.Append("\n## 失败原因\n\n");
            foreach (var failure in failures)
            {
                sb.Append("### ").Append(failure.ActionId).Append("\n\n");
                sb.Append("- 错误码：`").Append(failure.ErrorCode).Append("`\n");
                sb.Append("- 说明：").Append(redact(failure.Message)).Append("\n\n");
            }
        }

        return sb.ToString();
    }

    private static string BuildText(ActionExecutionContext context, RunLedger ledger)
    {
        var sb = new StringBuilder();
        var redact = context.VariableTable.Redact;

        sb.Append("环境站执行报告\n");
        sb.Append(new string('=', 60)).Append('\n');
        sb.Append("包：").Append(ledger.PackageId).Append('\n');
        sb.Append("运行 ID：").Append(ledger.RunId).Append('\n');
        sb.Append("结论：").Append(redact(ledger.Outcome ?? "进行中")).Append('\n');
        sb.Append("步骤：").Append(ledger.Invocations.Count.ToString(CultureInfo.InvariantCulture))
          .Append("，失败 ").Append(ledger.Failures().Count().ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append('\n');

        var index = 1;
        foreach (var invocation in ledger.Invocations)
        {
            sb.Append(index++.ToString(CultureInfo.InvariantCulture)).Append(". ")
              .Append(invocation.Succeeded ? "[成功] " : "[失败] ")
              .Append(invocation.ActionId).Append("  ")
              .Append(redact(OneLine(invocation.Message))).Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildJson(ActionExecutionContext context, RunLedger ledger)
    {
        var redact = context.VariableTable.Redact;

        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"packageId\": ").Append(Json(ledger.PackageId)).Append(",\n");
        sb.Append("  \"runId\": ").Append(Json(ledger.RunId)).Append(",\n");
        sb.Append("  \"startedAt\": ").Append(Json(ledger.StartedAt.ToString("O", CultureInfo.InvariantCulture))).Append(",\n");
        sb.Append("  \"completedAt\": ")
          .Append(ledger.CompletedAt is { } c ? Json(c.ToString("O", CultureInfo.InvariantCulture)) : "null").Append(",\n");
        sb.Append("  \"outcome\": ").Append(redact(ledger.Outcome) is { Length: > 0 } o ? Json(o) : "null").Append(",\n");
        sb.Append("  \"stepCount\": ").Append(ledger.Invocations.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("  \"failureCount\": ").Append(ledger.Failures().Count().ToString(CultureInfo.InvariantCulture)).Append(",\n");

        sb.Append("  \"steps\": [\n");
        for (var i = 0; i < ledger.Invocations.Count; i++)
        {
            var invocation = ledger.Invocations[i];
            sb.Append("    { \"action\": ").Append(Json(invocation.ActionId))
              .Append(", \"version\": ").Append(Json(invocation.Version))
              .Append(", \"succeeded\": ").Append(invocation.Succeeded ? "true" : "false")
              .Append(", \"errorCode\": ").Append(Json(invocation.ErrorCode))
              .Append(", \"message\": ").Append(Json(redact(invocation.Message)))
              .Append(", \"elapsedMs\": ").Append(((long)invocation.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture))
              .Append(", \"reversibleToken\": ")
              .Append(invocation.ReversibleToken is { Length: > 0 } t ? Json(t) : "null")
              .Append(" }").Append(i == ledger.Invocations.Count - 1 ? "\n" : ",\n");
        }

        sb.Append("  ],\n");

        sb.Append("  \"touchedPaths\": [");
        var touched = ledger.TouchedPaths();
        for (var i = 0; i < touched.Length; i++)
        {
            sb.Append(i == 0 ? string.Empty : ", ").Append(Json(touched[i]));
        }

        sb.Append("],\n");

        sb.Append("  \"reversibleTokens\": [");
        var tokens = ledger.ReversibleTokens();
        for (var i = 0; i < tokens.Length; i++)
        {
            sb.Append(i == 0 ? string.Empty : ", ").Append(Json(tokens[i]));
        }

        sb.Append("]\n}\n");
        return sb.ToString();
    }

    private static string OneLine(string text)
    {
        var flat = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    private static string Json(string? text)
    {
        if (text is null)
        {
            return "null";
        }

        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ')
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary><c>envstation.report.export_diagnostics</c>：打包诊断信息（自动脱敏）。</summary>
internal sealed class ReportExportDiagnosticsAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.report.export_diagnostics",
        "1.0.0",
        CapabilityIds.Cleanup,
        "导出诊断信息",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 120,
        TouchedResources: ["filesystem.write", "registry.read"],
        Parameters:
        [
            Path_("out", true, "输出的 zip 文件路径"),
            Bool("redact", "是否自动脱敏（用户名、家目录、敏感变量）", true),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var outRaw = arguments.GetString("out")!;
        var redact = arguments.GetBoolean("redact", true);

        var guard = context.GuardPath(outRaw, "out");
        if (guard.IsFailure)
        {
            return Fail(guard.Error.Code, guard.Error.Message + " " + guard.Error.Remediation);
        }

        var outPath = guard.Value;
        var directory = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var collected = new List<(string Name, string Content)>
        {
            ("report.md", RunReportBuilder.Build(context, ReportFormat.Markdown)),
            ("run.json", RunReportBuilder.Build(context, ReportFormat.Json)),
            ("system-info.txt", DiagnosticsCollector.SystemInfo()),
            ("env-summary.txt", DiagnosticsCollector.EnvironmentSummary(context)),
        };

        try
        {
            using var zipStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            foreach (var (name, content) in collected)
            {
                var final = redact ? DiagnosticsCollector.RedactPersonalData(content) : content;
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(final);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"诊断包写入失败：{ex.Message}");
        }

        var size = new FileInfo(outPath).Length;
        return Ok(
            $"诊断包已导出到 {outPath}（{size / 1024.0:F1} KB，包含 {collected.Count} 个文件{(redact ? "，已脱敏" : "，未脱敏")}）。",
            Outputs(
                ("out", outPath),
                ("bytes", size.ToString(CultureInfo.InvariantCulture)),
                ("file_count", collected.Count.ToString(CultureInfo.InvariantCulture)),
                ("redacted", Bool(redact))),
            touched: [outPath]);
    }
}

/// <summary>诊断信息收集与脱敏。</summary>
internal static class DiagnosticsCollector
{
    internal static string SystemInfo()
    {
        var sb = new StringBuilder();
        sb.Append("环境站诊断信息\n");
        sb.Append(new string('=', 50)).Append('\n');
        sb.Append("生成时间：").Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("操作系统：").Append(System.Environment.OSVersion.VersionString).Append('\n');
        sb.Append("系统架构：").Append(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture).Append('\n');
        sb.Append("进程架构：").Append(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture).Append('\n');
        sb.Append("运行时：").Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription).Append('\n');
        sb.Append("处理器数：").Append(System.Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("是否 AOT：").Append(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "否（JIT）" : "是").Append('\n');
        sb.Append("界面语言：").Append(CultureInfo.CurrentUICulture.Name).Append('\n');

        var (product, display, ubr) = OsInfo.ReadFromRegistry();
        sb.Append("Windows 版本：").Append(product ?? "未知").Append(' ')
          .Append(display ?? string.Empty).Append(" (UBR ").Append(ubr?.ToString(CultureInfo.InvariantCulture) ?? "?").Append(")\n");

        return sb.ToString();
    }

    /// <summary>
    /// 环境摘要：<b>只列变量名与个数，绝不含值</b>。
    /// </summary>
    /// <remarks>
    /// 这是刻意的：诊断包经常被用户发到公开论坛或工单系统。
    /// 环境变量的值里可能含 token、代理密码、内部域名，
    /// 而"变量名 + 个数"已经足够定位绝大多数问题。
    /// </remarks>
    internal static string EnvironmentSummary(ActionExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.Append("环境变量摘要（仅名称，不含值）\n");
        sb.Append(new string('=', 50)).Append('\n');

        foreach (var scope in new[] { EnvStation.Abstractions.Environment.EnvScope.User, EnvStation.Abstractions.Environment.EnvScope.Machine })
        {
            var scopeName = EnvironmentPathResolver.ToScopeName(scope);
            if (context.Environment is null)
            {
                sb.Append('[').Append(scopeName).Append("] 本次运行未启用环境操作，跳过。\n");
                continue;
            }

            var all = context.Environment.ReadAll(scope);
            if (all.IsFailure)
            {
                sb.Append('[').Append(scopeName).Append("] 读取失败：").Append(all.Error.Message).Append('\n');
                continue;
            }

            sb.Append('[').Append(scopeName).Append("] 共 ")
              .Append(all.Value.Count.ToString(CultureInfo.InvariantCulture)).Append(" 个变量：\n");

            foreach (var variable in all.Value.OrderBy(static v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("  ").Append(variable.Name).Append(" (").Append(variable.Kind).Append(")\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 脱敏：把用户名、家目录等在诊断包里替换成占位符。
    /// </summary>
    /// <remarks>
    /// 这一层的目标不是"加密"，而是"让用户能放心把诊断包发出去"。
    /// 因此替换的是最常见、也最容易被忽略的泄露源：用户名与其家目录路径。
    /// </remarks>
    internal static string RedactPersonalData(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var result = content;
        var user = System.Environment.UserName;
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(home))
        {
            result = result.Replace(home, @"%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        if (user.Length >= 3)
        {
            result = result.Replace(user, "<用户>", StringComparison.OrdinalIgnoreCase);
        }

        var computer = System.Environment.MachineName;
        if (computer.Length >= 3)
        {
            result = result.Replace(computer, "<计算机>", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}

/// <summary><c>envstation.cleanup.temp</c>：清理包私有临时目录与过期缓存。</summary>
internal sealed class CleanupTempAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.cleanup.temp",
        "1.0.0",
        CapabilityIds.Cleanup,
        "清理临时文件",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 120,
        TouchedResources: ["filesystem.write"],
        Parameters:
        [
            Path_("directory", false, "要清理的目录，省略时使用环境站为本次运行准备的临时目录"),
            Int("older_than_hours", "只清理超过该小时数的文件，0 表示全部清理", 0, 8760, 0),
            Bool("dry_run", "只报告将要删除的内容，不实际删除", false),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var raw = arguments.GetString("directory");
        var olderThanHours = arguments.GetInt64("older_than_hours", 0);
        var dryRun = arguments.GetBoolean("dry_run", false);

        var directory = raw ?? Path.Combine(Path.GetTempPath(), "EnvStation", context.PackageId);
        var guard = context.GuardPath(directory, "directory");
        if (guard.IsFailure)
        {
            return Fail(guard.Error.Code, guard.Error.Message + " " + guard.Error.Remediation);
        }

        var target = guard.Value;
        if (!Directory.Exists(target))
        {
            return Ok(
                $"目录 {target} 不存在，无需清理。",
                Outputs(("removed_files", "0"), ("removed_bytes", "0"), ("directory", target)));
        }

        // 刻意拒绝清理磁盘根与系统目录：这类参数错误一旦成真，后果不可挽回。
        if (IsDangerousDirectory(target))
        {
            return Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"拒绝清理 {target}：该路径是磁盘根或系统关键目录。" +
                " 清理动作只作用于环境站自己的临时目录与包私有目录。");
        }

        var cutoff = olderThanHours > 0
            ? DateTime.UtcNow.AddHours(-olderThanHours)
            : DateTime.MaxValue;

        long removedBytes = 0;
        var removedFiles = 0;
        var skipped = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"无法枚举 {target}：{ex.Message}");
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > cutoff)
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    info.Delete();
                }

                removedFiles++;
                removedBytes += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        var verb = dryRun ? "将清理" : "已清理";
        var outputs = Outputs(
            ("directory", target),
            ("dry_run", Bool(dryRun)),
            ("removed_files", removedFiles.ToString(CultureInfo.InvariantCulture)),
            ("removed_bytes", removedBytes.ToString(CultureInfo.InvariantCulture)),
            ("skipped", skipped.ToString(CultureInfo.InvariantCulture)));

        return Ok(
            $"{verb} {target}：{removedFiles} 个文件，{removedBytes / 1024.0:F1} KB" +
            (skipped > 0 ? $"（跳过 {skipped} 个：未到清理时间或无法删除）" : string.Empty) +
            (dryRun ? "。这是预演，未做任何实际删除。" : "。"),
            outputs);
    }

    /// <summary>判断路径是否是绝对不能清理的位置。</summary>
    internal static bool IsDangerousDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 磁盘根
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return true;
        }

        var windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        var programFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        foreach (var critical in new[] { windows, programFiles, userProfile })
        {
            if (string.IsNullOrEmpty(critical))
            {
                continue;
            }

            var normalized = critical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmed, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary><c>envstation.cleanup.remove_package</c>：删除本包私有目录（仅限包私有）。</summary>
internal sealed class CleanupRemovePackageAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.cleanup.remove_package",
        "1.0.0",
        CapabilityIds.Cleanup,
        "删除包私有目录",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 120,
        TouchedResources: ["filesystem.write"],
        Parameters:
        [
            Str("package_id", true, "要删除的包 ID，必须等于当前包的 ID", maxLength: 128),
            Path_("directory", false, "要删除的目录，省略时使用包私有目录"),
            Bool("dry_run", "只报告将要删除的内容，不实际删除", false),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var packageId = arguments.GetString("package_id")!;

        // 这是本动作最重要的一行：包只能删自己的目录。
        // 需求 21.2 A11 明确写了 "package_id（必须是自身）"，
        // 否则一个包就能删掉另一个包的数据，隔离模型直接失效。
        if (!string.Equals(packageId, context.PackageId, StringComparison.Ordinal))
        {
            return Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"package_id 为 {packageId}，与当前包 {context.PackageId} 不一致，已拒绝删除。" +
                " 一个包只能清理自己的私有目录。");
        }

        var raw = arguments.GetString("directory")
            ?? Path.Combine(Path.GetTempPath(), "EnvStation", context.PackageId);

        var guard = context.GuardPath(raw, "directory");
        if (guard.IsFailure)
        {
            return Fail(guard.Error.Code, guard.Error.Message + " " + guard.Error.Remediation);
        }

        var target = guard.Value;
        var dryRun = arguments.GetBoolean("dry_run", false);

        if (CleanupTempAction.IsDangerousDirectory(target))
        {
            return Fail(
                EnvStationErrorCodes.PathOutsideAuthorizedRoot,
                $"拒绝删除 {target}：该路径是磁盘根或系统关键目录。");
        }

        if (!Directory.Exists(target))
        {
            return Ok(
                $"目录 {target} 不存在，无需删除。",
                Outputs(("directory", target), ("removed", "false")));
        }

        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch (IOException)
                {
                    // 统计失败不影响删除。
                }
            }

            if (!dryRun)
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(
                EnvStationErrorCodes.PathNotWritable,
                $"删除 {target} 失败：{ex.Message}。可能有文件正被占用，关闭相关程序后重试。");
        }

        return Ok(
            dryRun
                ? $"预演：将删除 {target}（{files} 个文件，{bytes / 1024.0:F1} KB），未做任何实际删除。"
                : $"已删除包私有目录 {target}（{files} 个文件，{bytes / 1024.0:F1} KB）。",
            Outputs(
                ("directory", target),
                ("removed", Bool(!dryRun)),
                ("file_count", files.ToString(CultureInfo.InvariantCulture)),
                ("bytes", bytes.ToString(CultureInfo.InvariantCulture))),
            touched: dryRun ? null : [target]);
    }
}

/// <summary><c>envstation.fs.copy</c>：复制文件/目录。</summary>
internal sealed class FileSystemCopyAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.fs.copy",
        "1.0.0",
        CapabilityIds.FileSystemInstall,
        "复制文件或目录",
        IsIdempotent: true,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 300,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("src", true, "源文件或源目录"),
            Path_("dest", true, "目标路径"),
            Bool("overwrite", "目标已存在时是否覆盖", false),
            Bool("verify", "复制后是否逐文件校验大小", true),
            Bool("dry_run", "只报告将要复制的文件数与体积", false),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var srcGuard = context.GuardPath(arguments.GetString("src")!, "src");
        if (srcGuard.IsFailure)
        {
            return Fail(srcGuard.Error.Code, srcGuard.Error.Message + " " + srcGuard.Error.Remediation);
        }

        var destGuard = context.GuardPath(arguments.GetString("dest")!, "dest");
        if (destGuard.IsFailure)
        {
            return Fail(destGuard.Error.Code, destGuard.Error.Message + " " + destGuard.Error.Remediation);
        }

        var src = srcGuard.Value;
        var dest = destGuard.Value;
        var overwrite = arguments.GetBoolean("overwrite", false);
        var verify = arguments.GetBoolean("verify", true);
        var dryRun = arguments.GetBoolean("dry_run", false);

        var isFile = File.Exists(src);
        var isDirectory = Directory.Exists(src);
        if (!isFile && !isDirectory)
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"源路径不存在：{src}");
        }

        if (File.Exists(dest) || Directory.Exists(dest))
        {
            if (!overwrite)
            {
                return Fail(
                    EnvStationErrorCodes.ActionFailed,
                    $"目标已存在：{dest}。若要覆盖，把 overwrite 设为 true。");
            }
        }

        // 体积统计与配额预检（需求 22.3：先估后做，不要做到一半才发现超限）。
        var (fileCount, totalBytes) = FileSystemMeter.Measure(src, isDirectory);
        var quota = context.Quota.TryConsume(QuotaKind.FileWrite, fileCount);
        if (quota.IsFailure)
        {
            return Fail(quota.Error.Code, quota.Error.Message + " " + quota.Error.Remediation);
        }

        if (dryRun)
        {
            return Ok(
                $"预演：将从 {src} 复制 {fileCount} 个文件（{totalBytes / 1024.0 / 1024.0:F1} MB）到 {dest}，未做任何实际写入。",
                Outputs(
                    ("dry_run", "true"),
                    ("file_count", fileCount.ToString(CultureInfo.InvariantCulture)),
                    ("bytes", totalBytes.ToString(CultureInfo.InvariantCulture))));
        }

        try
        {
            if (isFile)
            {
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(src, dest, overwrite: true);

                if (verify && new FileInfo(src).Length != new FileInfo(dest).Length)
                {
                    return Fail(
                        EnvStationErrorCodes.ActionFailed,
                        $"复制后大小不一致：源 {new FileInfo(src).Length} 字节，目标 {new FileInfo(dest).Length} 字节。");
                }
            }
            else
            {
                FileSystemCopier.CopyDirectory(src, dest, overwrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"复制失败：{ex.Message}");
        }

        return Ok(
            $"已复制 {fileCount} 个文件（{totalBytes / 1024.0 / 1024.0:F1} MB）：{src} → {dest}",
            Outputs(
                ("dry_run", "false"),
                ("file_count", fileCount.ToString(CultureInfo.InvariantCulture)),
                ("bytes", totalBytes.ToString(CultureInfo.InvariantCulture)),
                ("src", src),
                ("dest", dest)),
            touched: [dest]);
    }
}

/// <summary><c>envstation.fs.move</c>：移动目录（用于迁移安装位置）。</summary>
internal sealed class FileSystemMoveAction : ActionBase
{
    /// <inheritdoc />
    public override ActionDescriptor Descriptor { get; } = new(
        "envstation.fs.move",
        "1.0.0",
        CapabilityIds.FileSystemInstall,
        "移动文件或目录",
        IsIdempotent: false,
        IsParallelSafe: false,
        HasInverse: false,
        RequiresUserPresence: false,
        DefaultTimeoutSeconds: 300,
        TouchedResources: ["filesystem.read", "filesystem.write"],
        Parameters:
        [
            Path_("src", true, "源路径"),
            Path_("dest", true, "目标路径"),
            Bool("dry_run", "只报告将要移动的内容", false),
        ]);

    /// <inheritdoc />
    protected override ValueTask<ActionResult> ExecuteAsync(
        ActionExecutionContext context, ActionArguments arguments, CancellationToken cancellationToken)
    {
        var srcGuard = context.GuardPath(arguments.GetString("src")!, "src");
        if (srcGuard.IsFailure)
        {
            return Fail(srcGuard.Error.Code, srcGuard.Error.Message + " " + srcGuard.Error.Remediation);
        }

        var destGuard = context.GuardPath(arguments.GetString("dest")!, "dest");
        if (destGuard.IsFailure)
        {
            return Fail(destGuard.Error.Code, destGuard.Error.Message + " " + destGuard.Error.Remediation);
        }

        var src = srcGuard.Value;
        var dest = destGuard.Value;
        var dryRun = arguments.GetBoolean("dry_run", false);

        if (!File.Exists(src) && !Directory.Exists(src))
        {
            return Fail(EnvStationErrorCodes.AssertFailed, $"源路径不存在：{src}");
        }

        if (File.Exists(dest) || Directory.Exists(dest))
        {
            return Fail(
                EnvStationErrorCodes.ActionFailed,
                $"目标已存在：{dest}。移动不会覆盖已有内容，先确认目标位置或改用复制。");
        }

        var isDirectory = Directory.Exists(src);
        var (fileCount, totalBytes) = FileSystemMeter.Measure(src, isDirectory);

        if (dryRun)
        {
            // 跨卷移动实际上是"复制 + 删除"，风险比同卷改名高得多，预演必须点明。
            var sameVolume = string.Equals(
                Path.GetPathRoot(src), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase);

            return Ok(
                $"预演：将移动 {src} → {dest}（{fileCount} 个文件，{totalBytes / 1024.0 / 1024.0:F1} MB）。" +
                (sameVolume ? " 同一磁盘内为原子改名，失败不留残留。" : " 跨磁盘移动需先复制再删除，中途失败会留下不完整的目标。"),
                Outputs(
                    ("dry_run", "true"),
                    ("same_volume", Bool(sameVolume)),
                    ("file_count", fileCount.ToString(CultureInfo.InvariantCulture))));
        }

        try
        {
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            if (isDirectory)
            {
                Directory.Move(src, dest);
            }
            else
            {
                File.Move(src, dest);
            }
        }
        catch (IOException ex) when (ex.Message.Contains("same disk", StringComparison.OrdinalIgnoreCase)
                                     || ex.HResult == unchecked((int)0x80070011))
        {
            // 跨卷：降级为"复制 + 删除"，并在失败时保留源目录（宁可留重复，不可丢数据）。
            try
            {
                if (isDirectory)
                {
                    FileSystemCopier.CopyDirectory(src, dest, overwrite: false);
                    Directory.Delete(src, recursive: true);
                }
                else
                {
                    File.Copy(src, dest, overwrite: false);
                    File.Delete(src);
                }
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                return Fail(
                    EnvStationErrorCodes.ActionFailed,
                    $"跨磁盘移动失败：{inner.Message}。源目录 {src} 已保留，未丢失任何数据。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(EnvStationErrorCodes.PathNotWritable, $"移动失败：{ex.Message}");
        }

        return Ok(
            $"已移动 {src} → {dest}（{fileCount} 个文件）。",
            Outputs(
                ("dry_run", "false"),
                ("file_count", fileCount.ToString(CultureInfo.InvariantCulture)),
                ("src", src),
                ("dest", dest)),
            touched: [src, dest]);
    }
}

/// <summary>文件系统计量与复制工具（<c>fs.copy</c> / <c>fs.move</c> 共用）。</summary>
internal static class FileSystemMeter
{
    internal static (int FileCount, long TotalBytes) Measure(string path, bool isDirectory)
    {
        if (!isDirectory)
        {
            try
            {
                return (1, new FileInfo(path).Length);
            }
            catch (IOException)
            {
                return (1, 0);
            }
        }

        var count = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                count++;
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // 单个文件统计失败不影响整体估算。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 枚举中断：返回已统计的部分。
        }

        return (count, bytes);
    }
}

/// <summary>目录复制实现。</summary>
internal static class FileSystemCopier
{
    internal static void CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);

            var targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(file, target, overwrite);
        }
    }
}
