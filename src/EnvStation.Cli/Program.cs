using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Environment;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Actions;
using EnvStation.Core.Environment;
using EnvStation.Core.Packages;
using EnvStation.Core.Workflow;
using AbsPkg = EnvStation.Abstractions.Packages;

namespace EnvStation.Cli;

/// <summary>
/// 环境站命令行界面。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有 CLI</b>（需求 BLD-1：GUI + CLI 双二进制共用内核）：
/// </para>
/// <list type="bullet">
///   <item>CI / 批量部署场景下没有人会去点界面；</item>
///   <item>排查问题时，命令行能给出比界面更细的原始信息；</item>
///   <item>它是"内核可独立工作"的最好证明——如果某个能力只有界面能触发，那说明内核分层没做干净。</item>
/// </list>
/// <para>
/// <b>安全默认值</b>：<c>run</c> 默认是<b>预演</b>。要真正执行必须显式给出 <c>--apply</c>，
/// 并且必须用 <c>--allow</c> 逐项授予能力。没有"全部同意"这种开关——
/// 需求 IMP-3 明确要求权限不得一键放行。
/// </para>
/// <para>退出码：0 成功 / 1 业务失败 / 2 用法错误 / 3 存在阻断项。</para>
/// </remarks>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;
    private const int ExitBlocked = 3;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        try
        {
            return args[0] switch
            {
                "doctor" => await DoctorAsync(args[1..]).ConfigureAwait(false),
                "actions" => ActionsCommand(args[1..]),
                "check" => await CheckAsync(args[1..]).ConfigureAwait(false),
                "pack" => await PackAsync(args[1..]).ConfigureAwait(false),
                "keygen" => Keygen(args[1..]),
                "run" => await RunAsync(args[1..]).ConfigureAwait(false),
                "env" => await EnvAsync(args[1..]).ConfigureAwait(false),
                "path" => await PathAsync(args[1..]).ConfigureAwait(false),
                "fingerprint" => Fingerprint(args[1..]),
                _ => Unknown(args[0]),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("已取消。");
            return ExitFailure;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        Console.Error.WriteLine("运行 envstation --help 查看可用命令。");
        return ExitUsage;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            环境站 EnvStation · Windows 环境配置与回滚（命令行）

            用法：envstation <命令> [必填参数] [可选参数]

            命令：
              doctor                          检测本机环境（系统、磁盘、PATH、依赖、已装运行时）
              actions [--json]                列出全部可用动作及其参数
              check <包文件> [--pubkey <公钥>] [--v4]   校验 .envstation 包（V1 结构 → V2 静态 → V3 合规；加 --v4 再做沙箱试运行）
              pack <源目录> [--out <文件>] [--key <私钥>] [--force]   把源目录打成 .envstation（自动补全动作哈希）
              keygen [--out <目录>]           生成一对签名密钥（ECDSA P-256）
              fingerprint <公钥文件>          计算密钥指纹
              run <包> [--apply] [--allow <能力ID>] [--root <目录>]   执行包内工作流
              env get|set|unset|diff|restore|validate
              path list|ensure|remove|clean|validate

            安全默认值：
              run 默认只做预演，打印将要发生的每一步；env、path 的写子命令默认只读。
              要真正执行必须同时给出：
                --apply            确认执行（不加则只预演）
                --allow <能力ID>   逐项授权，可重复，没有全部同意开关
                --root <目录>      授权可写的根目录，可重复
              示例：
                envstation run pkg.envstation --apply --allow CAP.ENV.USER --root "%LOCALAPPDATA%\EnvStation"

            退出码：
              0  成功
              1  业务失败
              2  用法错误
              3  存在阻断项

            环境隔离：
              所有写操作都先建快照，失败自动回滚。
              包只能访问自己声明的域名与授权目录。
              未显式授权时不会修改任何环境变量。
            """);
    }

    // ══════════════════════════ doctor ══════════════════════════

    private static async Task<int> DoctorAsync(string[] args)
    {
        var findings = new FindingBag();
        var registry = ActionRegistry.CreateDefault(findings);
        if (registry.IsFailure)
        {
            Console.Error.WriteLine("动作注册表无法建立：" + findings.ToReport().ToText());
            return ExitFailure;
        }

        using var context = CreateContext(registry.Value, isDryRun: true);

        Console.WriteLine("环境站 · 环境检测");
        Console.WriteLine(new string('─', 68));

        // 只读探测：不会修改任何东西，因此不需要授权。
        var probes = new (string Action, Dictionary<string, AbsPkg.ScriptValue> Arguments)[]
        {
            ("envstation.detect.os", []),
            ("envstation.detect.arch", []),
            ("envstation.detect.disk", new(StringComparer.Ordinal)
            {
                ["path"] = new AbsPkg.ScriptString(Path.GetTempPath()),
            }),
            ("envstation.detect.env", new(StringComparer.Ordinal)
            {
                ["names"] = new AbsPkg.ScriptArray([new AbsPkg.ScriptString("PATH")]),
                ["scope"] = new AbsPkg.ScriptString("both"),
                ["include_values"] = new AbsPkg.ScriptBoolean(false),
            }),
            ("envstation.detect.deps", []),
            ("envstation.path.validate", new(StringComparer.Ordinal) { ["scope"] = new AbsPkg.ScriptString("user") }),
            ("envstation.path.validate", new(StringComparer.Ordinal) { ["scope"] = new AbsPkg.ScriptString("machine") }),
            ("envstation.detect.conflict", []),
        };

        var problems = 0;

        foreach (var (actionId, arguments) in probes)
        {
            var result = await InvokeAsync(registry.Value, context, actionId, arguments).ConfigureAwait(false);

            // 判据统一走 ProbeHealth：动作"执行成功"不等于"这一项没问题"——
            // 缺前置依赖、PATH 有失效条目、同名命令冲突都属于"成功返回但有问题"。
            // 早先这里只看 result.Success，于是紧紧挨着"缺少 1 项前置依赖"的下方会打印
            // "未发现异常项"（界面侧同源缺陷见 D-44）。
            var isProblem = ProbeHealth.IsProblem(actionId, result);
            var mark = !result.Success ? "✗" : isProblem ? "!" : "✓";

            // 数据行不带句末句号：这是表格里的值，不是句子（文案规范第 5 节）。
            Console.WriteLine($"  {mark} {StripAction(actionId),-28} {TrimSentence(OneLine(result.Message))}");

            if (isProblem)
            {
                problems++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(problems == 0
            ? $"未发现异常项（共 {probes.Length} 项）。"
            : $"共 {probes.Length} 项，其中 {problems} 项异常。");

        return ExitOk;
    }

    /// <summary>
    /// 从账本记录还原一个最小 <see cref="ActionResult"/>，只为复用 <see cref="ProbeHealth"/> 的判据。
    /// </summary>
    /// <remarks>
    /// 账本记的是"这次调用发生了什么"，没有完整的输出字典；探测类动作的问题判据依赖输出键，
    /// 因此这里按动作 ID 重新算一次——<see cref="ProbeHealth"/> 对没有输出的探测动作会保守地
    /// 判为"有问题"，所以调用方只在 <see cref="ProbeHealth.IsProbe"/> 为真时才用它。
    /// </remarks>
    private static ActionResult Rehydrated(ActionInvocation invocation) =>
        ActionResult.Ok(invocation.Message, invocation.Outputs);
    private static string StripAction(string actionId) => actionId["envstation.".Length..];

    // ══════════════════════════ actions ══════════════════════════

    private static int ActionsCommand(string[] args)
    {
        var findings = new FindingBag();
        var registry = ActionRegistry.CreateDefault(findings);
        if (registry.IsFailure)
        {
            return ExitFailure;
        }

        var groups = registry.Value.Descriptors
            .GroupBy(static d => d.ActionId.Split('.')[1], StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            Console.WriteLine();
            Console.WriteLine($"【{group.Key}】");
            foreach (var descriptor in group.OrderBy(static d => d.ActionId, StringComparer.Ordinal))
            {
                var flags = new List<string>(4);
                if (descriptor.IsIdempotent)
                {
                    flags.Add("幂等");
                }

                if (descriptor.IsParallelSafe)
                {
                    flags.Add("可并行");
                }

                if (descriptor.HasInverse)
                {
                    flags.Add("可逆");
                }

                if (descriptor.RequiresUserPresence)
                {
                    flags.Add("需交互");
                }

                Console.WriteLine($"  {descriptor.ActionId,-38} {descriptor.CapabilityId,-22} {string.Join('/', flags)}");
                Console.WriteLine($"      {descriptor.DisplayName}");

                foreach (var parameter in descriptor.Parameters.OrEmpty())
                {
                    var required = parameter.Required ? "必填" : "可选";
                    var type = parameter.Type.ToString().ToLowerInvariant();
                    Console.WriteLine($"      · {parameter.Name} ({type}, {required}) {parameter.Description}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"共 {registry.Value.Count} 个动作。");
        return ExitOk;
    }

    // ══════════════════════════ check ══════════════════════════

    private static async Task<int> CheckAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：envstation check <包文件> [--pubkey <公钥>] [--v4]");
            return ExitUsage;
        }

        var packagePath = args[0];
        var publicKey = ReadOption(args, "--pubkey");
        var publicKeyPem = publicKey is null ? null : await File.ReadAllTextAsync(publicKey).ConfigureAwait(false);

        var result = await PackageValidator.ValidateAsync(packagePath, publicKeyPem).ConfigureAwait(false);

        Console.WriteLine($"校验 {Path.GetFileName(packagePath)}");
        Console.WriteLine(new string('─', 68));

        foreach (var layer in result.Layers)
        {
            Console.WriteLine("  " + layer);
        }

        Console.WriteLine();
        Console.WriteLine(result.Report.ToText());

        if (result.Manifest is { } manifest)
        {
            Console.WriteLine();
            Console.WriteLine($"包名　　：{manifest.Name}");
            Console.WriteLine($"包 ID　 ：{manifest.Id}");
            Console.WriteLine($"版本　　：{manifest.Version}（标准 {manifest.SpecVersion} / 档位 {manifest.Tier}）");
            Console.WriteLine($"质量评分：{result.QualityScore} / 100 —— {QualityScoreCalculator.Describe(result.QualityScore)}");

            Console.WriteLine();
            Console.WriteLine("能力授权（该包申请的能力）：");
            foreach (var (capability, explanation) in manifest.Permissions.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"  · {capability}");
                Console.WriteLine($"      {explanation}");
            }
        }

        // ── V4 沙箱试运行（可选，需要运行时）──
        if (args.Contains("--v4", StringComparer.Ordinal))
        {
            if (result.Report.HasBlockers)
            {
                Console.WriteLine();
                Console.WriteLine("V4 沙箱试运行：已跳过（V1~V3 存在阻断项）。");
                return ExitBlocked;
            }

            var v4 = await RunSandboxTrialAsync(result).ConfigureAwait(false);
            if (v4 is not null)
            {
                return v4.Report.HasBlockers ? ExitBlocked : ExitOk;
            }
        }

        return result.Report.HasBlockers ? ExitBlocked : result.CanImport ? ExitOk : ExitFailure;
    }

    /// <summary>
    /// 执行 V4 沙箱试运行并打印结论。
    /// </summary>
    /// <remarks>
    /// 试运行会真的调用动作，只是把三条副作用通道全部掐断（只读环境实现 / 不注入网络 /
    /// 可写根仅沙箱目录）。所以它能回答前三层回答不了的问题：这个包<b>实际</b>用到哪些能力、
    /// 实际调用哪些动作——以及这些是否与清单声明一致。
    /// </remarks>
    private static async Task<EnvStation.Core.Packages.SandboxTrialResult?> RunSandboxTrialAsync(EnvStation.Core.Packages.PackageValidationResult validation)
    {
        if (validation.Manifest is not { } manifest || validation.Workflow is not { } workflow)
        {
            return null;
        }

        var findings = new FindingBag();
        var registry = ActionRegistry.CreateDefault(findings);

        if (registry.IsFailure)
        {
            Console.Error.WriteLine("无法建立动作注册表，V4 已跳过。");
            return null;
        }

        Console.WriteLine();
        Console.WriteLine("V4 沙箱试运行（在隔离目录中执行，不修改环境变量、不联网）");
        Console.WriteLine(new string('─', 68));

        var sandbox = Path.Combine(Path.GetTempPath(), "envstation-v4-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var trial = await EnvStation.Core.Packages.SandboxTrial
                .RunAsync(manifest, workflow, registry.Value, sandbox)
                .ConfigureAwait(false);

            Console.WriteLine($"  试运行　　：{trial.Outcome.Message}");
            Console.WriteLine($"  实际用到　：{(trial.ExercisedCapabilities.Length == 0 ? "（无）" : string.Join("、", trial.ExercisedCapabilities))}");
            Console.WriteLine($"  未声明　　：{(trial.UndeclaredCapabilities.Length == 0 ? "（无）" : string.Join("、", trial.UndeclaredCapabilities))}");
            Console.WriteLine($"  声明未用　：{(trial.UnusedCapabilities.Length == 0 ? "（无）" : string.Join("、", trial.UnusedCapabilities))}");
            Console.WriteLine();
            Console.WriteLine(trial.Report.ToText());

            return trial;
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandbox))
                {
                    Directory.Delete(sandbox, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"沙箱目录未能删除：{sandbox}");
            }
        }
    }

    // ══════════════════════════ pack / keygen / fingerprint ══════════════════════════

    private static async Task<int> PackAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：envstation pack <源目录> [--out <文件>] [--key <私钥>] [--force]");
            return ExitUsage;
        }

        var source = args[0];
        var output = ReadOption(args, "--out")
            ?? Path.Combine(
                Directory.GetCurrentDirectory(),
                Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source))) + ".envstation");

        var keyPath = ReadOption(args, "--key");
        var privateKey = keyPath is null ? null : await File.ReadAllTextAsync(keyPath).ConfigureAwait(false);
        var force = args.Contains("--force", StringComparer.Ordinal);

        var result = await PackagePacker
            .PackAsync(source, output, privateKey, force)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            Console.Error.WriteLine("打包失败：" + result.Error.Message);
            if (result.Error.Remediation is { Length: > 0 } remediation)
            {
                Console.Error.WriteLine("            " + remediation);
            }

            return ExitFailure;
        }

        var pack = result.Value;
        Console.WriteLine($"已生成 {pack.OutputPath}");
        Console.WriteLine($"  体积　　：{pack.SizeBytes / 1024.0:F1} KB");
        Console.WriteLine($"  文件数　：{pack.FileCount}");
        Console.WriteLine($"  自动补全：{pack.StampedActions} 处动作哈希");
        Console.WriteLine($"  签名　　：{(pack.Signed ? $"已签名（指纹 {pack.KeyFingerprint}）" : "未签名（仅适合本地使用）")}");

        if (!pack.Signed)
        {
            Console.WriteLine();
            Console.WriteLine("未签名的包在别人机器上会被拒绝导入。分发前用 --key 指定私钥签名。");
        }

        return ExitOk;
    }

    private static int Keygen(string[] args)
    {
        var directory = ReadOption(args, "--out") ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        var (privateKey, publicKey) = PackageSigner.GenerateKeyPair();

        var privatePath = Path.Combine(directory, "envstation-private.p256");
        var publicPath = Path.Combine(directory, "envstation-public.p256");

        File.WriteAllText(privatePath, privateKey, new UTF8Encoding(false));
        File.WriteAllText(publicPath, publicKey, new UTF8Encoding(false));

        var fingerprint = PackageSigner.ComputeKeyFingerprint(publicKey);

        Console.WriteLine("已生成密钥对：");
        Console.WriteLine($"  私钥：{privatePath}   ← 自行保管，不要分发");
        Console.WriteLine($"  公钥：{publicPath}   ← 分发给使用者，用于验证你的包");
        Console.WriteLine();
        Console.WriteLine("指纹（通过可信渠道告知使用者）：");
        Console.WriteLine("  " + (fingerprint.IsSuccess ? fingerprint.Value : "（无法计算）"));
        Console.WriteLine();
        Console.WriteLine("使用者核对指纹后即可确认后续所有包都来自你。");

        return ExitOk;
    }

    private static int Fingerprint(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：envstation fingerprint <公钥文件>");
            return ExitUsage;
        }

        var pem = File.ReadAllText(args[0]);
        var fingerprint = PackageSigner.ComputeKeyFingerprint(pem);

        if (fingerprint.IsFailure)
        {
            Console.Error.WriteLine(fingerprint.Error.Message);
            return ExitFailure;
        }

        Console.WriteLine(PackageSigner.DescribeFingerprint(fingerprint.Value));
        return ExitOk;
    }

    // ══════════════════════════ run ══════════════════════════

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：envstation run <包> [--apply] [--allow <能力ID>] [--root <目录>]");
            return ExitUsage;
        }

        var packagePath = args[0];
        var apply = args.Contains("--apply", StringComparer.Ordinal);
        var allowed = ReadOptions(args, "--allow");
        var roots = ReadOptions(args, "--root");

        var findings = new FindingBag();
        var contents = PackageArchive.Read(packagePath, findings);
        if (contents.IsFailure)
        {
            Console.Error.WriteLine(findings.ToReport().ToText());
            return ExitBlocked;
        }

        var manifestText = contents.Value.GetText(PackageLayout.ManifestEntry);
        var workflowText = contents.Value.GetText(PackageLayout.WorkflowEntry);
        if (manifestText is null || workflowText is null)
        {
            Console.Error.WriteLine("包内缺少清单或工作流。");
            return ExitBlocked;
        }

        var manifest = PackageManifestReader.Read(manifestText, findings);
        var workflow = WorkflowReader.Read(workflowText, findings);

        if (manifest.IsFailure || workflow.IsFailure)
        {
            Console.Error.WriteLine(findings.ToReport().ToText());
            return ExitBlocked;
        }

        // 未授予任何能力时，只能预演 —— 这是刻意的：默认不做任何修改。
        var effectiveRoots = roots.Count > 0
            ? roots
            : [Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "EnvStation")];

        var registryResult = ActionRegistry.CreateDefault(findings);
        if (registryResult.IsFailure)
        {
            return ExitFailure;
        }

        var capabilities = new CapabilitySet(allowed);
        var context = CreateContext(
            registryResult.Value,
            isDryRun: !apply,
            capabilities: capabilities,
            roots: effectiveRoots,
            unattended: true);

        Console.WriteLine($"包　　：{manifest.Value.Name}（{manifest.Value.Id} @{manifest.Value.Version}）");
        Console.WriteLine($"模式　：{(apply ? "执行（--apply）" : "预演（未做任何修改）")}");
        Console.WriteLine($"授权　：{(allowed.Count == 0 ? "（未授予任何能力）" : string.Join("、", allowed))}");
        Console.WriteLine($"可写根：{string.Join("、", effectiveRoots)}");
        Console.WriteLine(new string('─', 68));

        var interpreter = new WorkflowInterpreter(registryResult.Value);
        var outcome = await interpreter
            .RunAsync(workflow.Value, context, new WorkflowOptions(DryRun: !apply, Unattended: true))
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(outcome.Succeeded ? "✓ " + outcome.Message : "✗ " + outcome.Message);

        PrintLedger(context);
        PrintAuditLocation(context);

        if (!apply && outcome.Succeeded)
        {
            Console.WriteLine();
            Console.WriteLine("这是预演结果。确认无误后加 --apply 并逐项授予能力即可执行。");
        }

        return outcome.Succeeded ? ExitOk : ExitFailure;
    }

    private static void PrintLedger(ActionExecutionContext context)
    {
        var ledger = context.Ledger;
        if (ledger.Invocations.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("步骤明细：");
        var index = 1;
        foreach (var invocation in ledger.Invocations)
        {
            // 三态标记：执行失败 ✗ / 执行成功但报告了异常 ! / 一切正常 ✓。
            // 早先只看 Succeeded，于是"PATH 共 14 项，其中 4 项异常"这一行前面挂着 ✓，
            // 与 doctor 的结论口径也不一致（同源问题见 D-47）。
            var mark = !invocation.Succeeded ? "✗"
                : ProbeHealth.IsProblem(invocation.ActionId, Rehydrated(invocation)) ? "!"
                : "✓";

            Console.WriteLine(
                $"  {index++,2}. {mark} {invocation.ActionId,-38} {TrimSentence(OneLine(invocation.Message))}");
        }

        var tokens = ledger.ReversibleTokens();
        if (tokens.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("可回滚快照：");
            foreach (var token in tokens)
            {
                Console.WriteLine($"  envstation env restore {token}");
            }
        }
    }

    /// <summary>
    /// 告诉用户这次运行的审计记录落在哪里。
    /// </summary>
    /// <remarks>
    /// <b>必须主动打印</b>：审计的价值在"事后追查"，而用户不会去猜文件在哪。
    /// 一行路径的成本可以忽略，换来的是"出事时知道去哪里找证据"。
    /// 审计汇不是 JSONL 实现时（内存实现，例如目录建不起来）明确说明"未落盘"——
    /// 绝不让用户以为有记录而实际没有。
    /// </remarks>
    private static void PrintAuditLocation(ActionExecutionContext context)
    {
        if (context.AuditSink is EnvStation.Core.Actions.JsonlAuditSink jsonl)
        {
            Console.WriteLine();
            Console.WriteLine($"审计记录：{jsonl.CurrentPath}");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("审计记录：（未落盘，仅内存）");
        }
    }

    // ══════════════════════════ env ══════════════════════════

    private static async Task<int> EnvAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：envstation env get|set|unset|diff|restore|validate [参数]");
            return ExitUsage;
        }

        var sub = args[0];
        var rest = args[1..];

        var findings = new FindingBag();
        var registry = ActionRegistry.CreateDefault(findings);
        if (registry.IsFailure)
        {
            return ExitFailure;
        }

        var granted = await ConfirmWritesAsync(sub).ConfigureAwait(false);
        using var context = CreateContext(
            registry.Value,
            isDryRun: !granted,
            capabilities: granted
                ? new CapabilitySet([CapabilityIds.Inspect, CapabilityIds.EnvironmentUser])
                : new CapabilitySet([CapabilityIds.Inspect]));

        var (action, arguments) = sub switch
        {
            "get" => ("envstation.env.get", Args(
                ("name", Required(rest, 0, "用法：envstation env get <变量名> [--scope user|machine|both]")),
                ("scope", Option(rest, "--scope") ?? "both"))),

            "set" => ("envstation.env.set", Args(
                ("scope", Option(rest, "--scope") ?? "user"),
                ("name", Required(rest, 0, "用法：envstation env set <变量名> <值> [--scope user|machine]")),
                ("value", Required(rest, 1, "用法：envstation env set <变量名> <值> [--scope user|machine]")))),

            "unset" => ("envstation.env.unset", Args(
                ("scope", Option(rest, "--scope") ?? "user"),
                ("name", Required(rest, 0, "用法：envstation env unset <变量名> [--scope user|machine]")))),

            "diff" => ("envstation.env.diff", Args(
                ("snapshot_id", Option(rest, "--snapshot") ?? "latest"))),

            "restore" => ("envstation.env.restore", Args(
                ("snapshot_id", Required(rest, 0, "用法：envstation env restore <快照ID|latest>")))),

            "validate" => ("envstation.env.validate", Args(
                ("name", Required(rest, 0, "用法：envstation env validate <变量名> [值]")),
                ("value", rest.Length > 1 ? rest[1] : string.Empty))),

            _ => (string.Empty, Args()),
        };

        if (action.Length == 0)
        {
            Console.Error.WriteLine($"未知子命令：env {sub}");
            return ExitUsage;
        }

        var result = await InvokeAsync(registry.Value, context, action, arguments).ConfigureAwait(false);
        Console.WriteLine(result.Message);

        if (!granted && sub is "set" or "unset")
        {
            Console.WriteLine();
            Console.WriteLine("这是预演。要真正修改，加上 --apply。");
        }

        return result.Success ? ExitOk : ExitFailure;
    }

    // ══════════════════════════ path ══════════════════════════

    private static async Task<int> PathAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：envstation path list|ensure|remove|clean|validate [参数]");
            return ExitUsage;
        }

        var sub = args[0];
        var rest = args[1..];
        var scope = Option(rest, "--scope") ?? "user";

        var findings = new FindingBag();
        var registry = ActionRegistry.CreateDefault(findings);
        if (registry.IsFailure)
        {
            return ExitFailure;
        }

        var granted = await ConfirmWritesAsync(sub).ConfigureAwait(false);
        using var context = CreateContext(
            registry.Value,
            isDryRun: !granted,
            capabilities: granted
                ? new CapabilitySet([CapabilityIds.Inspect, CapabilityIds.PathModify])
                : new CapabilitySet([CapabilityIds.Inspect]));

        var (action, arguments) = sub switch
        {
            "list" or "validate" => ("envstation.path.validate", Args(("scope", scope))),
            "ensure" => ("envstation.path.ensure", Args(
                ("scope", scope),
                ("entry", Required(rest, 0, "用法：envstation path ensure <目录> [--scope user|machine]")),
                ("position", Option(rest, "--position") ?? "append"))),
            "remove" => ("envstation.path.remove", Args(
                ("scope", scope),
                ("entry", Required(rest, 0, "用法：envstation path remove <目录> [--scope user|machine]")))),
            "clean" => ("envstation.path.clean", CleanArgs(scope, granted)),

            // path clean 在未授权时强制预演：这样即使用户忘了 --apply，
            // 也只会在"预览"与"执行"之间二选一，不存在"以为在预览其实删了东西"的可能。

            _ => (string.Empty, Args()),
        };

        if (action.Length == 0)
        {
            Console.Error.WriteLine($"未知子命令：path {sub}");
            return ExitUsage;
        }

        var result = await InvokeAsync(registry.Value, context, action, arguments).ConfigureAwait(false);
        Console.WriteLine(result.Message);

        if (!granted && sub is "ensure" or "remove" or "clean")
        {
            Console.WriteLine();
            Console.WriteLine("这是预演。要真正修改，加上 --apply。");
        }

        return result.Success ? ExitOk : ExitFailure;
    }

    private static async Task<bool> ConfirmWritesAsync(string sub)
    {
        var isWrite = sub is "set" or "unset" or "restore" or "ensure" or "remove" or "clean";
        if (!isWrite)
        {
            return false;
        }

        // 写操作必须显式给出 --apply。这里不做交互式提问：
        // 一个 y/N 提示很容易被脚本里的一行 yes 管道绕过，"显式传参"才是真正的确认。
        await Task.CompletedTask.ConfigureAwait(false);
        return System.Environment.GetCommandLineArgs()
            .Contains("--apply", StringComparer.Ordinal);
    }

    // ══════════════════════════ 公共设施 ══════════════════════════

    private static ActionExecutionContext CreateContext(
        ActionRegistry registry,
        bool isDryRun,
        CapabilitySet? capabilities = null,
        IReadOnlyList<string>? roots = null,
        bool unattended = false)
    {
        _ = registry;

        var authorizedRoots = roots
            ?? [Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "EnvStation")];

        // 预演时注入**只读**环境实现：读得到真实现状（预览才有意义），但写操作在结构上被拒绝。
        // 这不是"靠分支克制"，而是"没有可写的实现可调用"。
        //
        // 网络通道在预演下一律不注入：网络访问会留下真实痕迹（对端可见），
        // 而且下载动作本身就有副作用（落盘）。预览时"N 个文件将被下载"这类信息
        // 由 net.head 之类的只读动作提供，不需要真的去下。
        IEnvironmentOperations environment = isDryRun
            ? ReadOnlyEnvironmentOperations.CreateDefault()
            : RegistryEnvironmentOperations.CreateDefault();

        var network = isDryRun ? null : new HttpNetworkTransport();

        return new ActionExecutionContext(
            packageId: "envstation.cli",
            runId: $"cli-{DateTime.Now:yyyyMMdd-HHmmss}",
            grantedCapabilities: capabilities ?? new CapabilitySet([CapabilityIds.Inspect]),
            authorizedRoots: authorizedRoots,
            variables: new VariableTable(),
            quota: new QuotaMeter(ResourceQuota.Default),
            // 审计落盘（需求 8.2）：CLI 的每次运行都要留下可追溯的记录。
            // 预演也记——预演记录恰恰是"用户当时看到会发生什么"的证据。
            audit: CreateAuditSink(),
            unattended: unattended,
            environment: environment,
            network: network);
    }

    /// <summary>
    /// 建立审计汇。
    /// </summary>
    /// <remarks>
    /// 目录固定为 <c>%LOCALAPPDATA%\EnvStation\audit</c>：审计是"用户机器上发生过什么"的记录，
    /// 属于用户自己的数据，放 LocalAppData 既不污染安装目录，也不会被安装包升级覆盖。
    /// 写不进去时降级为内存实现并给出提示——审计失败不应该让整个命令失败，
    /// 但必须让用户知道"这次没有留下记录"。
    /// </remarks>
    private static IAuditSink CreateAuditSink()
    {
        try
        {
            var directory = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "EnvStation",
                "audit");

            var sink = new JsonlAuditSink(directory);
            sink.AuditWriteFailed += (_, message) =>
                Console.Error.WriteLine($"警告：审计日志写入失败（{message}）。本次运行不会留下记录。");
            return sink;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"警告：无法建立审计目录（{ex.Message}）。本次运行仅在内存中记录。");
            return new MemoryAuditSink();
        }
    }

    private static async Task<ActionResult> InvokeAsync(
        ActionRegistry registry,
        ActionExecutionContext context,
        string actionId,
        Dictionary<string, AbsPkg.ScriptValue> arguments)
    {
        var resolved = registry.TryGet(actionId, out var action);
        if (!resolved)
        {
            return ActionResult.Fail(EnvStationErrorCodes.ActionNotFound, $"动作 {actionId} 不存在。");
        }

        var findings = new FindingBag();
        var bound = ActionArgumentsBinder.Bind(action.Descriptor, arguments, findings);

        return bound.IsFailure
            ? ActionResult.Fail(EnvStationErrorCodes.ActionArgumentInvalid, findings.ToReport().ToText())
            : await ActionExecutor.ExecuteAsync(action, context, bound.Value).ConfigureAwait(false);
    }

    private static Dictionary<string, AbsPkg.ScriptValue> Args(params (string Name, string Value)[] pairs)
    {
        var builder = new Dictionary<string, AbsPkg.ScriptValue>(StringComparer.Ordinal);
        foreach (var (name, value) in pairs)
        {
            builder[name] = new AbsPkg.ScriptString(value);
        }

        return builder;
    }

    /// <summary>构造 path clean 的参数；未授权时强制预演。</summary>
    private static Dictionary<string, AbsPkg.ScriptValue> CleanArgs(string scope, bool granted) =>
        new(StringComparer.Ordinal)
        {
            ["scope"] = new AbsPkg.ScriptString(scope),
            ["dry_run"] = new AbsPkg.ScriptBoolean(!granted),
        };

    private static string Required(string[] args, int index, string usage)
    {
        var positional = args.Where(static a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (index < positional.Length)
        {
            return positional[index];
        }

        throw new UsageException(usage);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static List<string> ReadOptions(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                values.Add(args[i + 1]);
            }
        }

        return values;
    }

    private static string? Option(string[] args, string name) => ReadOption(args, name);

    /// <summary>
    /// 去掉句末的句号。
    /// </summary>
    /// <remarks>
    /// 同一条消息在界面里是句子（要句号），在 CLI 的字段行/表格里是值（不要句号）。
    /// 动作消息是两边共用的，所以由 CLI 在打印时收尾，而不是让动作写成两种。
    /// </remarks>
    private static string TrimSentence(string text) =>
        text.EndsWith('。') ? text[..^1] : text;
    private static string OneLine(string text)
    {
        var flat = text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return flat.Length <= 110 ? flat : flat[..110] + "…";
    }

    private sealed class UsageException(string usage) : Exception(usage);
}
