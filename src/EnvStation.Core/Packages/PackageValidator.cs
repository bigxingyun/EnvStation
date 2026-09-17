using System.Collections.Immutable;
using System.Security.Cryptography;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Packages;

namespace EnvStation.Core.Packages;

/// <summary>V1~V4 四层验证的结果。</summary>
/// <param name="Report">统一的发现报告。</param>
/// <param name="Manifest">V1 通过后的清单（失败时为 null）。</param>
/// <param name="Workflow">V1 通过后的工作流（失败时为 null）。</param>
/// <param name="Signed">是否已通过签名验证。</param>
/// <param name="QualityScore">质量评分（0~100）。</param>
/// <param name="Layers">每一层的执行摘要，用于向用户解释"检查了什么"。</param>
public sealed record PackageValidationResult(
    ValidationReport Report,
    PackageManifest? Manifest,
    WorkflowDocument? Workflow,
    bool Signed,
    int QualityScore,
    ImmutableArray<string> Layers)
{
    /// <summary>是否允许导入（无阻断项）。</summary>
    public bool CanImport => !Report.HasBlockers && Manifest is not null && Workflow is not null;
}

/// <summary>
/// 包验证流水线（需求 24.1 的 V1~V4）。
/// </summary>
/// <remarks>
/// <para><b>四层各自回答什么问题</b>：</para>
/// <list type="number">
///   <item><b>V1 结构与内容</b>：格式对不对？每个文件的内容与清单里登记的哈希一致吗？签名有效吗？
///         ——这一层不关心"包做了什么"，只关心」包是不是它声称的那个包"。</item>
///   <item><b>V2 静态安全</b>：包引用了哪些动作、申请了哪些能力、参数里有没有可疑构造？
///         ——纯静态，不执行任何东西。</item>
///   <item><b>V3 策略与合规</b>：包声明的副作用、质量项、许可是否齐备？</item>
///   <item><b>V4 沙箱试运行</b>：在隔离环境里预演，把「声明」与「实际会发生什么」对照。
///         ——本层需要环境站运行时，由调用方在取得用户授权后单独触发。</item>
/// </list>
/// <para>
/// <b>任一层出现阻断即中止</b>（需求 IMP-1）。这不是「尽量多跑几层」的取舍：
/// 让一个 V2 已经不通过的包进入 V4，等于用真实环境去验证一个已知有问题的包。
/// </para>
/// </remarks>
public static class PackageValidator
{
    /// <summary>执行 V1~V3。V4 需要运行时环境，由调用方在取得授权后单独执行。</summary>
    /// <param name="packagePath">包文件路径。</param>
    /// <param name="trustedPublicKeyPem">可信公钥；为 null 表示不做签名验证（本地自有包）。</param>
    public static Task<PackageValidationResult> ValidateAsync(
        string packagePath,
        string? trustedPublicKeyPem = null,
        CancellationToken cancellationToken = default)
    {
        var findings = new FindingBag();
        var layers = ImmutableArray.CreateBuilder<string>();

        // ── V1：结构、内容哈希、签名 ──
        var contentsResult = PackageArchive.Read(packagePath, findings);
        if (contentsResult.IsFailure)
        {
            layers.Add("V1 结构与内容：失败（包无法读取）");
            return Task.FromResult(Failure(findings, layers));
        }

        var contents = contentsResult.Value;

        // 纯静态校验：不涉及任何 I/O 等待，因此刻意保持同步，避免"假异步"。
        var v1 = VerifyContent(contents, trustedPublicKeyPem, findings, cancellationToken);

        layers.Add(v1.Summary);

        var manifestText = contents.GetText(PackageLayout.ManifestEntry);
        if (manifestText is null)
        {
            findings.Block(
                "V1-01", "包内缺少清单",
                $"包内没有 {PackageLayout.ManifestEntry}。",
                null,
                PackageLayout.ManifestEntry);
            return Task.FromResult(Failure(findings, layers, signed: v1.Signed));
        }

        var workflowText = contents.GetText(PackageLayout.WorkflowEntry);
        if (workflowText is null)
        {
            findings.Block(
                "V1-02", "包内缺少工作流",
                $"包内没有 {PackageLayout.WorkflowEntry}。",
                null,
                PackageLayout.WorkflowEntry);
            return Task.FromResult(Failure(findings, layers, signed: v1.Signed));
        }

        var manifestResult = PackageManifestReader.Read(manifestText, findings);
        var workflowResult = WorkflowReader.Read(workflowText, findings);

        if (manifestResult.IsFailure || workflowResult.IsFailure)
        {
            layers.Add("V1 结构与内容：清单或工作流未通过");
            return Task.FromResult(Failure(findings, layers, signed: v1.Signed));
        }

        var manifest = manifestResult.Value;
        var workflow = workflowResult.Value;

        // ── V2：静态安全扫描 ──
        var beforeV2 = findings.Items.Count;
        StaticRuleEngine.Evaluate(manifest, workflow, contents, findings);
        var v2Added = findings.Items.Count - beforeV2;
        layers.Add($"V2 静态安全：{StaticRuleEngine.Rules.Length} 条规则，产生 {v2Added} 项发现");

        // ── V3：策略与合规（资源哈希登记、依赖声明等）──
        var v3Added = CheckPolicies(manifest, contents, findings);
        layers.Add($"V3 策略与合规：产生 {v3Added} 项发现");

        var report = findings.ToReport();
        var score = QualityScoreCalculator.Compute(manifest, workflow, contents, findings);

        layers.Add(report.HasBlockers
            ? "结论：存在阻断项，禁止导入"
            : "结论：通过 V1~V3，可进入能力授权与沙箱试运行（V4）");

        return Task.FromResult(new PackageValidationResult(
            report, manifest, workflow, v1.Signed, score, layers.ToImmutable()));
    }

    private static PackageValidationResult Failure(
        FindingBag findings, ImmutableArray<string>.Builder layers, bool signed = false)
    {
        layers.Add("结论：存在阻断项，禁止导入");
        return new PackageValidationResult(findings.ToReport(), null, null, signed, 0, layers.ToImmutable());
    }

    // ────────────────────────────── V1 ──────────────────────────────

    private readonly record struct ContentVerdict(bool Signed, string Summary);

    /// <summary>校验内容哈希清单与签名。</summary>
    private static ContentVerdict VerifyContent(
        PackageContents contents,
        string? trustedPublicKeyPem,
        FindingBag findings,
        CancellationToken cancellationToken)
    {
        var manifestHashText = contents.GetText(PackageLayout.HashManifestEntry);
        if (manifestHashText is null)
        {
            findings.Warn(
                "V1-03", "包内没有哈希清单",
                $"包内没有 {PackageLayout.HashManifestEntry}，无法逐文件校验内容完整性。",
                "共享包应携带哈希清单，本地自有包可忽略本条。",
                PackageLayout.HashManifestEntry);
            return new ContentVerdict(false, "V1 结构与内容：无哈希清单，跳过逐文件校验");
        }

        var parsed = PackageSigner.ParseManifest(manifestHashText);
        if (parsed.IsFailure)
        {
            findings.Block("V1-04", "哈希清单格式非法", parsed.Error.Message,
                parsed.Error.Remediation, PackageLayout.HashManifestEntry);
            return new ContentVerdict(false, "V1 结构与内容：哈希清单非法");
        }

        var declared = parsed.Value;
        var mismatched = 0;
        var missing = 0;
        var extra = 0;

        foreach (var (path, expected) in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!contents.Contains(path))
            {
                findings.Block(
                    "V1-05", "清单登记的文件缺失",
                    $"哈希清单里登记了 {path}，但包内没有这个文件。",
                    "用 envstation pack 重新打包，使清单与包内内容一致。",
                    path);
                missing++;
                continue;
            }

            var actual = contents.TextEntries.TryGetValue(path, out var text)
                ? PackageSigner.ComputeHash(text)
                : PackageSigner.ComputeHash(contents.BinaryEntries[path]);

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                findings.Block(
                    "V1-06", "文件内容与哈希清单不符",
                    $"{path} 的实际哈希是 {actual[..16]}…，清单声明 {expected[..16]}…。",
                    "文件在生成清单之后被改动过。已拒绝导入。",
                    path);
                mismatched++;
            }
        }

        foreach (var path in contents.AllPaths)
        {
            if (!declared.ContainsKey(path) && !PackageLayout.ExcludedFromManifest.Contains(path))
            {
                findings.Warn(
                    "V1-07", "存在未登记哈希的文件",
                    $"{path} 不在哈希清单里，因此无法判断它是否被改过。",
                    "用 envstation pack 重新打包以补全清单。",
                    path);
                extra++;
            }
        }

        if (mismatched > 0 || missing > 0)
        {
            return new ContentVerdict(false, $"V1 结构与内容：{mismatched} 个文件哈希不符，{missing} 个文件缺失");
        }

        // 签名验证
        if (trustedPublicKeyPem is null)
        {
            return new ContentVerdict(
                false,
                $"V1 结构与内容：逐文件哈希校验通过（{declared.Count} 个文件，{extra} 个未登记）；未提供可信公钥，跳过签名验证");
        }

        var signatureText = contents.GetText(PackageLayout.SignatureEntry);
        if (signatureText is null)
        {
            findings.Block(
                "V1-08", "包缺少签名",
                $"包内没有 {PackageLayout.SignatureEntry}。",
                "共享包必须携带签名。本地自有包用 envstation pack 签名。",
                PackageLayout.SignatureEntry);
            return new ContentVerdict(false, "V1 结构与内容：缺少签名");
        }

        var verify = PackageSigner.Verify(manifestHashText, signatureText, trustedPublicKeyPem);
        if (verify.IsFailure)
        {
            findings.Block("V1-09", "签名验证失败", verify.Error.Message,
                verify.Error.Remediation, PackageLayout.SignatureEntry);
            return new ContentVerdict(false, "V1 结构与内容：签名验证失败");
        }

        var fingerprint = PackageSigner.ComputeKeyFingerprint(trustedPublicKeyPem);
        var fingerprintText = fingerprint.IsSuccess ? fingerprint.Value : "（无法计算）";

        return new ContentVerdict(
            true,
            $"V1 结构与内容：逐文件哈希校验通过（{declared.Count} 个文件），签名有效（指纹 {fingerprintText}）");
    }

    // ────────────────────────────── V3 ──────────────────────────────

    private static int CheckPolicies(PackageManifest manifest, PackageContents contents, FindingBag findings)
    {
        var before = findings.Items.Count;

        // 资源文件都应有哈希登记（S-08 的落地检查）
        var resourceFiles = contents.AllPaths
            .Where(static p => p.StartsWith("resources/", StringComparison.Ordinal))
            .ToArray();

        if (resourceFiles.Length > 0 && !contents.Contains(PackageLayout.HashManifestEntry))
        {
            findings.Warn(
                "V3-01", "带资源文件但无哈希清单",
                $"包内有 {resourceFiles.Length} 个资源文件，但没有哈希清单。",
                "用 envstation pack 重新打包以登记资源哈希。",
                "resources/");
        }

        // 声明了下载能力却把大文件打进包 —— 体积与一致性都会变差
        if (manifest.DeclaredCapabilities().Contains(CapabilityIds.NetDownload) && resourceFiles.Length > 20)
        {
            findings.Info(
                "V3-02", "包内资源偏多",
                $"该包声明了下载能力，但包内仍有 {resourceFiles.Length} 个资源文件。",
                "把大文件改为运行时下载（带哈希校验），减小包体积。",
                "resources/");
        }

        // 不可逆操作必须说明可逆性
        if (manifest.SideEffects is { Irreversible: true, ReversibleBy.Length: 0 })
        {
            findings.Warn(
                "V3-03", "声明了不可逆操作但未说明后果",
                "side_effects.irreversible 为 true，但没有填写 reversible_by。",
                "在 side_effects.reversible_by 中说明哪些操作无法撤销。",
                "envstation.toml", manifest.Id);
        }

        return findings.Items.Count - before;
    }
}

/// <summary>
/// 包质量评分（0~100，需求 24.3）。
/// </summary>
/// <remarks>
/// <para>
/// 评分不是"打分排名"，而是<b>给用户一个可以横向比较的信号</b>：
/// 同样两个都能装 Python 的包，一个有测试、有卸载、有许可证、声明齐全，另一个什么都没有——
/// 用户需要一眼看出差别。
/// </para>
/// <para>
/// 因此评分刻意只扣分不加分：满分是"该有的都有"，任何缺失都扣分，
/// 阻断项直接归零。这样分数不会因为某个包写了很长的 README 就虚高。
/// </para>
/// </remarks>
public static class QualityScoreCalculator
{
    /// <summary>计算质量评分。</summary>
    public static int Compute(
        PackageManifest manifest, WorkflowDocument workflow, PackageContents contents, FindingBag findings)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(findings);

        if (findings.HasBlockers)
        {
            return 0;
        }

        var score = 100;

        // 清单完整性（每项 4 分）
        if (string.IsNullOrWhiteSpace(manifest.License))
        {
            score -= 4;
        }

        if (manifest.Requirements is null)
        {
            score -= 4;
        }

        if (manifest.SideEffects is null)
        {
            score -= 4;
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            score -= 4;
        }

        if (manifest.Author is null || string.IsNullOrWhiteSpace(manifest.Author.Contact))
        {
            score -= 4;
        }

        // 包内文档（每项 6 分）
        if (!contents.Contains(PackageLayout.ReadmeEntry))
        {
            score -= 6;
        }

        if (!contents.Contains(PackageLayout.LicenseEntry))
        {
            score -= 3;
        }

        if (!contents.Contains(PackageLayout.ChangeLogEntry))
        {
            score -= 3;
        }

        // 质量声明（每项 8 分）
        if (manifest.Quality is null)
        {
            score -= 16;
        }
        else
        {
            if (!manifest.Quality.HasTests)
            {
                score -= 8;
            }

            if (!manifest.Quality.HasUninstall)
            {
                score -= 8;
            }
        }

        // 工程化程度（每项 6 分）
        if (!contents.Contains(PackageLayout.HashManifestEntry))
        {
            score -= 6;
        }

        if (!contents.Contains(PackageLayout.SignatureEntry))
        {
            score -= 6;
        }

        if (!contents.Contains(PackageLayout.SbomEntry))
        {
            score -= 3;
        }

        // 工作流可维护性：有 id 与 description 的步骤比例
        var steps = workflow.Flatten().ToArray();
        if (steps.Length > 0)
        {
            var documented = steps.Count(static s => !string.IsNullOrWhiteSpace(s.Description));
            var ratio = documented / (double)steps.Length;
            if (ratio < 0.5)
            {
                score -= 6;
            }
            else if (ratio < 0.9)
            {
                score -= 3;
            }
        }

        // 警告也扣分（每项 2 分，最多扣 10）
        score -= Math.Min(10, findings.WarnCount * 2);

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>把分数渲染为一句评价（与 UI 设计规范的分档一致）。</summary>
    public static string Describe(int score) => score switch
    {
        >= 90 => "优秀：声明齐全、有测试与卸载流程、内容可校验",
        >= 75 => "良好：主要项齐备，个别声明缺失",
        >= 60 => "可用：可以安装，缺少测试或卸载等长期维护项",
        >= 40 => "偏弱：多项声明缺失，导入前先阅读工作流",
        _ => "较差：缺少关键声明，谨慎导入",
    };
}
