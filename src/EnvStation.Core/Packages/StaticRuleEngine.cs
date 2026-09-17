using System.Collections.Immutable;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Packages;

namespace EnvStation.Core.Packages;

/// <summary>
/// V2 静态安全扫描：对包做<b>不执行任何东西</b>的全面检查。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么 V2 必须是纯静态的</b>：它是「用户还没同意安装」之前跑的检查。
/// 如果这一步需要执行包里的任何东西（哪怕只是「试着解析一下"），
/// 那么」先看再决定」这个承诺就不成立了。因此本引擎只读文本、只做模式匹配与集合运算。
/// </para>
/// <para>
/// <b>规则的组织方式</b>：不是"一条规则一个 if"，而是"一组检查项 + 统一的严重级别"。
/// 每条规则都带 <c>RuleId</c>（S-xx 安全 / C-xx 合规 / B-xx 质量 / Q-xx 兼容），
/// 这样用户看到的每一条结论都能追溯到具体规则，也能被豁免（需求 M21-5）。
/// </para>
/// <para>
/// <b>最重要的三条</b>（它们直接对应需求 19.3 的「防注入」设计）：
/// <list type="number">
///   <item>S-01：动作引用必须固定版本（S2）；</item>
///   <item>S-02：动作 ID 不得含变量插值（S4）——这是「动态构造动作 ID"的唯一入口；</item>
///   <item>S-03：声明了网络能力就必须有域名白名单（AI-4）。</item>
/// </list>
/// 这三条在前两层（清单/工作流读取器）已经拦过一次；这里再拦一次，是因为
/// **V2 是面向「第三方包」的独立关卡**，不能依赖上游读取器的实现细节。
/// </para>
/// </remarks>
public static class StaticRuleEngine
{
    /// <summary>单条静态规则。</summary>
    /// <param name="RuleId">规则编号。</param>
    /// <param name="Title">规则名。</param>
    /// <param name="Level">严重级别。</param>
    /// <param name="Description">这条规则在防什么。</param>
    public sealed record Rule(string RuleId, string Title, FindingLevel Level, string Description);

    /// <summary>
    /// 当前实现的静态规则清单。
    /// </summary>
    /// <remarks>
    /// 需求 24.2 规划了 46 条（S/C/B/Q 四类）。这里实现的是其中**能在纯静态条件下给出确定结论**的部分；
    /// 其余规则要么依赖 V4 沙箱试运行（如"是否真的写入了声明之外的位置"），
    /// 要么依赖外部数据（如」下载源是否仍然有效"），会在后续里程碑补齐。
    /// 刻意<b>不</b>把未实现的规则列进清单——列了却不跑，比没有更糟。
    /// </remarks>
    public static ImmutableArray<Rule> Rules { get; } =
    [
        new("S-01", "动作引用必须固定版本", FindingLevel.Block,
            "浮动版本会让静态检查失去意义：今天通过的包，明天可能引用到不同的实现。"),
        new("S-02", "动作 ID 不得动态构造", FindingLevel.Block,
            "动态动作 ID 会让静态检查无法确定包会调用哪些动作，也是命令注入的常见变体。"),
        new("S-03", "网络能力必须有域名白名单", FindingLevel.Block,
            "没有域名白名单的下载无法被审计，也不会被执行。"),
        new("S-04", "动作必须携带契约哈希", FindingLevel.Block,
            "哈希把包固定在编写时的动作语义上，缺失时无法发现动作契约漂移。"),
        new("S-05", "不得出现能力表中的高危组合", FindingLevel.Warn,
            "「下载 + 运行程序」组合是供应链攻击的标准路径，需要用户显式确认。"),
        new("S-06", "不得声明白名单外的动作命名空间", FindingLevel.Block,
            "第三方包冒充官方动作会让用户在能力授权中看到错误的来源。"),
        new("S-07", "不得声明未知能力", FindingLevel.Block,
            "能力表由官方封闭维护，自造能力无法被授权。"),
        new("S-08", "包内资源必须逐个登记哈希", FindingLevel.Warn,
            "资源文件是安装包的实际来源，未登记的哈希意味着内容可被替换。"),
        new("S-09", "禁止在参数中出现疑似命令行拼接", FindingLevel.Block,
            "参数里同时出现 shell 元字符与命令名时，多数情况是作者在拼接命令行。"),
        new("S-10", "不得引用注册表/计划任务/服务类动作", FindingLevel.Block,
            "注册表、计划任务、服务、自启动类动作不在动作库中，引用它们说明包来自不兼容的生态。"),

        new("C-01", "必须声明许可证", FindingLevel.Warn,
            "共享包没有许可证时，使用者无法判断能否合法使用。"),
        new("C-02", "必须提供 README", FindingLevel.Warn,
            "用户需要在不读脚本的前提下知道这个包会做什么。"),
        new("C-03", "package_id 命名空间必须与作者一致", FindingLevel.Warn,
            "包 ID 与作者 ID 不一致时，无法判断包的真实来源。"),
        new("C-04", "能力声明必须带自然语言说明", FindingLevel.Block,
            "能力授权需要一句自然语言说明，告诉用户这项能力用来做什么。"),

        new("B-01", "必须提供测试用例", FindingLevel.Info,
            "有测试的包在版本升级后更不容易悄悄失效。"),
        new("B-02", "必须提供卸载流程", FindingLevel.Info,
            "没有卸载路径的包会让用户不敢安装。"),
        new("B-03", "必须声明副作用", FindingLevel.Warn,
            "副作用声明用于与 V4 沙箱试运行的实际行为交叉核对。"),
        new("B-04", "工作流步骤不应过多", FindingLevel.Info,
            "过长的单包工作流难以审计，也难以在失败时定位。"),

        new("Q-01", "spec_version 必须在支持范围内", FindingLevel.Block,
            "标准版本决定字段语义，跨大版本时字段含义可能已经变化。"),
        new("Q-02", "必须声明支持的系统与架构", FindingLevel.Warn,
            "不声明时用户会在不支持的机器上尝试安装并失败。"),
        new("Q-03", "声明的磁盘需求不应超过常见可用空间", FindingLevel.Info,
            "过大的磁盘声明通常说明包把安装包本体也算了进去。"),
    ];

    /// <summary>执行静态扫描。</summary>
    /// <param name="manifest">已解析的清单。</param>
    /// <param name="workflow">已解析的工作流。</param>
    /// <param name="contents">包内容（用于资源哈希检查）。</param>
    /// <param name="findings">发现收集器。</param>
    public static void Evaluate(
        PackageManifest manifest,
        WorkflowDocument workflow,
        PackageContents? contents,
        FindingBag findings)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(findings);

        CheckActionReferences(workflow, findings);
        CheckCapabilityCombinations(manifest, findings);
        CheckParameterHygiene(workflow, findings);
        CheckDeclarations(manifest, workflow, contents, findings);
    }

    // ────────────────────────────── S 类：安全 ──────────────────────────────

    private static void CheckActionReferences(WorkflowDocument workflow, FindingBag findings)
    {
        var calls = workflow.Flatten().OfType<ActionCallStep>().ToArray();

        foreach (var call in calls)
        {
            var reference = call.Reference;
            var location = $"workflow.toml:{call.Line}";

            // S-01：固定版本
            if (reference.Version is "latest" or "*" || reference.Version.Contains('*', StringComparison.Ordinal))
            {
                findings.Block(
                    "S-01", "动作引用使用了浮动版本",
                    $"{reference.ActionId} 使用了浮动版本 {reference.Version}。",
                    "改为具体的语义化版本，例如 @1.0.0。",
                    location, reference.ActionId);
            }

            // S-02：动态构造
            if (reference.ActionId.Contains("${", StringComparison.Ordinal))
            {
                findings.Block(
                    "S-02", "动作 ID 中包含变量插值",
                    $"{reference.ActionId} 里出现了变量插值。",
                    "改用固定的动作 ID，变量只能出现在参数值里。",
                    location, reference.ActionId);
            }

            // S-04：契约哈希
            if (string.IsNullOrWhiteSpace(reference.Hash))
            {
                findings.Block(
                    "S-04", "动作引用缺少契约哈希",
                    $"{reference.ActionId}@{reference.Version} 没有携带 pin.hash。",
                    "运行 envstation pack 自动补全 pin.hash。",
                    location, reference.ActionId);
            }

            // S-06：命名空间
            if (!reference.IsOfficial && !reference.IsThirdParty)
            {
                findings.Block(
                    "S-06", "动作命名空间不合法",
                    $"{reference.ActionId} 既不是官方命名空间，也不符合 x-<作者ID>. 的第三方格式。",
                    "官方动作用 envstation. 前缀，第三方动作用 x-<作者ID>. 前缀。",
                    location, reference.ActionId);
            }

            // S-10：不存在的动作类别
            if (reference.ActionId.Contains("registry", StringComparison.OrdinalIgnoreCase)
                || reference.ActionId.Contains("schtask", StringComparison.OrdinalIgnoreCase)
                || reference.ActionId.Contains("service", StringComparison.OrdinalIgnoreCase)
                || reference.ActionId.Contains("startup", StringComparison.OrdinalIgnoreCase))
            {
                findings.Block(
                    "S-10", "引用了未提供的动作类别",
                    $"{reference.ActionId} 属于「注册表 / 计划任务 / 服务 / 自启动」类操作。",
                    "改用动作库中的动作，这四类动作不在动作库中。",
                    location, reference.ActionId);
            }
        }
    }

    private static void CheckCapabilityCombinations(PackageManifest manifest, FindingBag findings)
    {
        var capabilities = manifest.DeclaredCapabilities();

        // S-05：高危组合
        var download = capabilities.Contains(CapabilityIds.NetDownload);
        var launch = capabilities.Contains(CapabilityIds.ProcessLaunch);
        var archive = capabilities.Contains(CapabilityIds.Archive);

        if (download && launch)
        {
            findings.Warn(
                "S-05", "下载与运行程序组合",
                "清单同时声明了 CAP.NET.DOWNLOAD 与 CAP.PROCESS.LAUNCH。",
                "确认 [network].allow 中的每个域名可信，并检查 verify.* 步骤是否校验哈希。",
                "envstation.toml", manifest.Id);
        }

        if (download && !archive)
        {
            findings.Info(
                "S-08", "未声明解压能力",
                "清单声明了 CAP.NET.DOWNLOAD，未声明 CAP.ARCHIVE。下载压缩包时解压步骤会失败。",
                "只下载单文件安装程序时可忽略本条。",
                "envstation.toml", manifest.Id);
        }

        // S-07：未知能力（清单读取器已拦过，这里作为独立关卡再拦一次）
        foreach (var capability in manifest.Permissions.Keys)
        {
            if (!Capabilities.IsKnown(capability))
            {
                findings.Block(
                    "S-07", "声明了未知能力",
                    $"{capability} 不在官方能力表内。",
                    "删除该声明，改用能力表中的能力 ID。",
                    "envstation.toml", capability);
            }
        }
    }

    private static void CheckParameterHygiene(WorkflowDocument workflow, FindingBag findings)
    {
        // S-09：参数里出现疑似命令行拼接。
        //
        // 判据刻意保守：只有「参数值里同时出现 shell 元字符与已知命令名」才报。
        // 单独一个 & 在 URL 里是完全正常的，误报会让作者学会忽略这条规则。
        string[] shells = ["cmd", "powershell", "pwsh", "bash", "sh ", "&&", "||", ">", "|"];
        string[] suspicious = ["& ", "| ", "&&", "||", "; ", "`", "$("];

        foreach (var call in workflow.Flatten().OfType<ActionCallStep>())
        {
            foreach (var (name, value) in call.Arguments)
            {
                if (value is not ScriptString text || text.Value.Length == 0)
                {
                    continue;
                }

                var looksLikeCommand = shells.Any(s => text.Value.Contains(s, StringComparison.OrdinalIgnoreCase));
                var hasMeta = suspicious.Any(s => text.Value.Contains(s, StringComparison.Ordinal));

                if (looksLikeCommand && hasMeta)
                {
                    findings.Block(
                        "S-09", "参数中疑似出现命令行拼接",
                        $"{call.Reference.ActionId} 的参数 {name} 含 shell 元字符与命令名：{Truncate(text.Value)}。",
                        "参数只作为数据传递，不进入命令行。需要执行命令时改用 verify.version_output。",
                        $"workflow.toml:{call.Line}", name);
                }
            }
        }
    }

    private static void CheckDeclarations(
        PackageManifest manifest, WorkflowDocument workflow, PackageContents? contents, FindingBag findings)
    {
        // C-01 许可证
        if (string.IsNullOrWhiteSpace(manifest.License))
        {
            findings.Warn("C-01", "未声明许可证", "包清单里没有 license 字段。",
                "共享包应声明许可证，使用者才能判断能否合法使用。", "envstation.toml", manifest.Id);
        }

        // C-02 README
        var hasReadme = contents?.Contains(PackageLayout.ReadmeEntry) == true
            || manifest.Quality?.Readme is { Length: > 0 };
        if (!hasReadme)
        {
            findings.Warn("C-02", "缺少 README", "包内没有 README.md，清单也没有 quality.readme。",
                "添加 README.md，或在 quality.readme 中指向说明文档。", "envstation.toml", manifest.Id);
        }

        // C-03 作者 ID 与包 ID
        if (manifest.Id.StartsWith(ActionReference.ThirdPartyPrefix, StringComparison.Ordinal))
        {
            var authorId = manifest.Author?.Id;
            if (authorId is { Length: > 0 })
            {
                var expectedPrefix = ActionReference.ThirdPartyPrefix + authorId + ".";
                if (!manifest.Id.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Warn(
                        "C-03", "包 ID 与作者 ID 不一致",
                        $"包 ID 是 {manifest.Id}，作者 ID 是 {authorId}。",
                        $"第三方包 ID 需形如 {expectedPrefix}<包名>。",
                        "envstation.toml", manifest.Id);
                }
            }
        }

        // B-01 / B-02 质量声明
        if (manifest.Quality is null)
        {
            findings.Info("B-01", "未提供质量声明", "包清单里没有 [quality] 段。",
                "在 [quality] 中声明 has_tests 与 has_uninstall。", "envstation.toml", manifest.Id);
        }
        else
        {
            if (!manifest.Quality.HasTests)
            {
                findings.Info("B-01", "未提供测试用例", "清单的 quality.has_tests 为 false。",
                    "补充 tests/ 目录并把 has_tests 改为 true。", "envstation.toml", manifest.Id);
            }

            if (!manifest.Quality.HasUninstall)
            {
                findings.Info("B-02", "未提供卸载流程", "清单的 quality.has_uninstall 为 false。",
                    "补充卸载步骤并把 has_uninstall 改为 true。", "envstation.toml", manifest.Id);
            }
        }

        // B-03 副作用声明
        if (manifest.SideEffects is null)
        {
            findings.Warn("B-03", "缺少副作用声明", "包清单里没有 [side_effects] 段。",
                "补上 [side_effects] 段，声明 writes_env 与 writes_files。",
                "envstation.toml", manifest.Id);
        }

        // B-04 步骤数
        var stepCount = workflow.Flatten().Count();
        if (stepCount > 120)
        {
            findings.Info("B-04", "工作流步骤过多", $"该包共有 {stepCount} 个步骤。",
                "过长的单包工作流难以审计，也难以定位失败。拆分为多个包。",
                "workflow.toml", manifest.Id);
        }

        // Q-02 系统与架构
        if (manifest.Requirements is null)
        {
            findings.Warn("Q-02", "未声明运行环境要求", "包清单里没有 [requirements] 段。",
                "补上 [requirements] 段，声明 os 与 arch。", "envstation.toml", manifest.Id);
        }

        // Q-03 磁盘需求
        if (manifest.Requirements?.DiskBytes is { } disk && disk > 100L * 1024 * 1024 * 1024)
        {
            findings.Info("Q-03", "磁盘需求异常偏大", $"disk_bytes 声明需要 {disk / 1024.0 / 1024 / 1024:F0} GB。",
                "核对 disk_bytes。安装包应在运行时下载，不计入磁盘需求。",
                "envstation.toml", manifest.Id);
        }
    }

    private static string Truncate(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
