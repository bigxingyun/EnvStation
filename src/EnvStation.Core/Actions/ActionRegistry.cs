using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Actions;
using EnvStation.Abstractions.Diagnostics;
using EnvStation.Abstractions.Packages;
using EnvStation.Core.Actions.Builtin;

namespace EnvStation.Core.Actions;

/// <summary>
/// 动作注册表。动作只能在这里登记，工作流只能引用已登记的动作——这是"动作即能力"（需求 A1）
/// 在代码层面的强制点：<b>没有登记的实现，包引用不到；登记的实现，其能力是写死的。</b>
/// </summary>
/// <remarks>
/// 刻意<b>不使用反射扫描</b>：Native AOT 下反射扫描需要保留元数据（体积与启动代价），
/// 且会让"哪些动作会被注册"变得不可静态枚举——这对安全审计是坏属性。
/// 全部内建动作在 <see cref="CreateDefault"/> 中显式列出。
/// </remarks>
public sealed class ActionRegistry
{
    private readonly ImmutableDictionary<string, IAction> _actions;
    private readonly ImmutableDictionary<string, string> _pins;

    private ActionRegistry(ImmutableDictionary<string, IAction> actions, ImmutableDictionary<string, string> pins)
    {
        _actions = actions;
        _pins = pins;
    }

    /// <summary>已登记的动作数量。</summary>
    public int Count => _actions.Count;

    /// <summary>按 ID 升序列出全部动作元数据（用于文档生成与 UI 展示）。</summary>
    public IEnumerable<ActionDescriptor> Descriptors =>
        _actions.Values.Select(static a => a.Descriptor).OrderBy(static d => d.ActionId, StringComparer.Ordinal);

    /// <summary>
    /// 创建一个只包含给定动作的注册表，并同时校验登记规则。
    /// </summary>
    /// <param name="actions">动作实现集合。</param>
    /// <param name="findings">登记期发现收集器；有阻断项时返回失败。</param>
    public static Result<ActionRegistry> Create(IEnumerable<IAction> actions, FindingBag findings)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(findings);

        var map = new Dictionary<string, IAction>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            var descriptor = action.Descriptor;

            if (!IsValidOfficialId(descriptor.ActionId))
            {
                findings.Block(
                    "ACT-01",
                    "动作 ID 不合规",
                    $"动作 {descriptor.ActionId} 不是合法的官方三段式 ID。",
                    "官方动作必须形如 envstation.<域>.<动作>，且域名与动作名只含小写字母、数字与下划线。",
                    subject: descriptor.ActionId);
                continue;
            }

            if (!Capabilities.IsKnown(descriptor.CapabilityId))
            {
                findings.Block(
                    "ACT-02",
                    "动作声明了未知能力",
                    $"动作 {descriptor.ActionId} 声明能力 {descriptor.CapabilityId}，但该能力不在官方能力表内。",
                    "能力表由官方封闭维护（Capabilities.All）。新增能力必须评审后加入该表。",
                    subject: descriptor.ActionId);
                continue;
            }

            var unknownConditional = descriptor.ConditionalCapabilities
                .OrEmpty()
                .FirstOrDefault(static c => !Capabilities.IsKnown(c));
            if (unknownConditional is not null)
            {
                findings.Block(
                    "ACT-02b",
                    "动作声明了未知的条件能力",
                    $"动作 {descriptor.ActionId} 的条件能力 {unknownConditional} 不在官方能力表内。",
                    "条件能力与基础能力来自同一张封闭表。",
                    subject: descriptor.ActionId);
                continue;
            }

            if (!SemanticVersion.TryParse(descriptor.Version, out _))
            {
                findings.Block(
                    "ACT-03",
                    "动作版本不是语义化版本",
                    $"动作 {descriptor.ActionId} 的版本 {descriptor.Version} 非法。",
                    "示例：1.0.0。",
                    subject: descriptor.ActionId);
                continue;
            }

            if (descriptor.DefaultTimeoutSeconds is < 1 or > 3600)
            {
                findings.Block(
                    "ACT-04",
                    "动作默认超时超出范围",
                    $"动作 {descriptor.ActionId} 的默认超时 {descriptor.DefaultTimeoutSeconds} 秒超出范围。",
                    "允许 1 到 3600 秒。",
                    subject: descriptor.ActionId);
                continue;
            }

            var duplicateParam = descriptor.Parameters.OrEmpty()
                .GroupBy(static p => p.Name, StringComparer.Ordinal)
                .FirstOrDefault(static g => g.Count() > 1);
            if (duplicateParam is not null)
            {
                findings.Block(
                    "ACT-05",
                    "动作参数名重复",
                    $"动作 {descriptor.ActionId} 重复声明了参数 {duplicateParam.Key}。",
                    "参数名必须唯一，否则后一个声明会永久遮蔽前一个。",
                    subject: descriptor.ActionId);
                continue;
            }

            var badParam = descriptor.Parameters.OrEmpty().FirstOrDefault(static p => !IsValidParameterName(p.Name));
            if (badParam is not null)
            {
                findings.Block(
                    "ACT-06",
                    "动作参数名不合规",
                    $"动作 {descriptor.ActionId} 的参数名 {badParam.Name} 非法。",
                    "参数名使用小写蛇形命名（如 mirror_of），只含小写字母、数字与下划线。",
                    subject: descriptor.ActionId);
                continue;
            }

            if (map.ContainsKey(descriptor.ActionId))
            {
                findings.Block(
                    "ACT-07",
                    "动作重复登记",
                    $"动作 {descriptor.ActionId} 被登记了多次。",
                    "同名动作在注册表中只能存在一份，否则行为取决于登记顺序。",
                    subject: descriptor.ActionId);
                continue;
            }

            map[descriptor.ActionId] = action;
        }

        if (findings.HasBlockers)
        {
            return Result<ActionRegistry>.Fail(
                EnvStationErrorCodes.ActionNotFound,
                $"动作注册表存在 {findings.BlockCount} 项阻断问题。");
        }

        // 说明：注册表内部统一保存**裸**十六进制摘要（与 NormalizeHash 的返回一致），
        // 对外展示时才补上 sha256: 前缀。两边混用会导致"哈希正确却校验不通过"这类极难排查的问题。
        var pins = map.Values
            .Select(static a => a.Descriptor)
            .ToImmutableDictionary(
                static d => $"{d.ActionId}@{d.Version}",
                static d => NormalizeHash(ComputeContractHash(d))!,
                StringComparer.Ordinal);

        return Result<ActionRegistry>.Ok(new ActionRegistry(map.ToImmutableDictionary(StringComparer.Ordinal), pins));
    }

    /// <summary>创建仅含官方内建动作的注册表。</summary>
    public static Result<ActionRegistry> CreateDefault(FindingBag findings) =>
        Create(BuiltinActions.All(), findings);

    /// <summary>按动作 ID 查找实现。</summary>
    public bool TryGet(string actionId, out IAction action) => _actions.TryGetValue(actionId, out action!);

    /// <summary>
    /// 按包内引用解析动作。会同时核对版本与 <c>pin.hash</c>。
    /// </summary>
    /// <remarks>
    /// <b>关于 pin.hash 的口径</b>：本实现中它是动作<b>契约</b>的内容哈希
    /// （动作 ID、版本、能力、参数规格、幂等/可逆/并行声明、超时、副作用声明的规范化摘要），
    /// 而<b>不是</b>动作实现代码的哈希。理由与局限：
    /// <list type="bullet">
    ///   <item>官方动作代码全部编译进同一个签名二进制，用包内哈希去钉二进制会导致
    ///         "客户端一升级，旧包全废"，直接违反需求 STD-4。</item>
    ///   <item>包真正依赖的是动作的<b>契约</b>：参数名、语义、错误码。契约变了而版本没变，
    ///         就是包作者必须知道的事——这正是本哈希要拦的。</item>
    ///   <item>局限：动作内部的实现缺陷无法靠该哈希发现，只能靠版本号与官方签名。
    ///         若将来引入按动作分发的独立程序集，应升级为真正的代码哈希（见 TD-9）。</item>
    /// </list>
    /// </remarks>
    public Result<IAction> Resolve(ActionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!_actions.TryGetValue(reference.ActionId, out var action))
        {
            return Result<IAction>.Fail(
                EnvStationErrorCodes.ActionNotFound,
                $"动作 {reference.ActionId} 不存在。",
                "可能是包写错了动作 ID，或使用了当前客户端尚未提供的新动作。可用动作清单见动作文档。");
        }

        var descriptor = action.Descriptor;
        if (!string.Equals(descriptor.Version, reference.Version, StringComparison.Ordinal))
        {
            return Result<IAction>.Fail(
                EnvStationErrorCodes.ActionNotFound,
                $"动作 {reference.ActionId} 的版本不匹配：包要求 {reference.Version}，客户端提供 {descriptor.Version}。",
                "动作引用必须与客户端提供的版本一致。让包作者按该版本重新生成引用与契约哈希。");
        }

        if (reference.Hash is null)
        {
            return Result<IAction>.Fail(
                EnvStationErrorCodes.ActionPinMismatch,
                $"动作引用 {reference} 缺少 pin.hash。",
                "动作引用必须携带契约哈希。让包作者补齐 pin.hash。");
        }

        var expected = NormalizeHash(reference.Hash);
        if (expected is null)
        {
            return Result<IAction>.Fail(
                EnvStationErrorCodes.ActionPinMismatch,
                $"pin.hash 格式非法：{reference.Hash}。",
                "正确格式为 sha256:<64 位小写十六进制>。");
        }

        if (!_pins.TryGetValue($"{descriptor.ActionId}@{descriptor.Version}", out var actual))
        {
            return Result<IAction>.Fail(
                EnvStationErrorCodes.ActionPinMismatch,
                $"无法计算动作 {reference} 的契约哈希。",
                "这是客户端内部状态异常，提交反馈。");
        }

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return Result<IAction>.Fail(
                EnvStationErrorCodes.ActionPinMismatch,
                $"动作 {reference.ActionId} 的契约哈希与包内声明不符。包声明 {expected}，实际 {actual}。",
                "包可能按另一个动作契约版本编写。参数语义可能已变化，继续执行会产生非预期结果。");
        }

        return Result<IAction>.Ok(action);
    }

    /// <summary>取某个动作契约的哈希（供包作者与文档使用），带 <c>sha256:</c> 前缀，可直接写入 <c>pin.hash</c>。</summary>
    public string? GetContractHash(string actionId, string version) =>
        _pins.TryGetValue($"{actionId}@{version}", out var hash) ? "sha256:" + hash : null;

    /// <summary>
    /// 计算动作契约的内容哈希。
    /// </summary>
    /// <remarks>
    /// 规范化格式刻意是"字段名 = 值"的逐行文本而不是 JSON：
    /// 不依赖任何序列化库的行为（属性顺序、转义策略），因此哈希在任何实现下都可复现。
    /// </remarks>
    public static string ComputeContractHash(ActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var sb = new StringBuilder();
        sb.Append("actionId=").Append(descriptor.ActionId).Append('\n');
        sb.Append("version=").Append(descriptor.Version).Append('\n');
        sb.Append("capability=").Append(descriptor.CapabilityId).Append('\n');
        sb.Append("idempotent=").Append(descriptor.IsIdempotent ? "1" : "0").Append('\n');
        sb.Append("parallelSafe=").Append(descriptor.IsParallelSafe ? "1" : "0").Append('\n');
        sb.Append("hasInverse=").Append(descriptor.HasInverse ? "1" : "0").Append('\n');
        sb.Append("requiresUser=").Append(descriptor.RequiresUserPresence ? "1" : "0").Append('\n');
        sb.Append("timeout=").Append(descriptor.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');

        sb.Append("touched=");
        foreach (var resource in descriptor.TouchedResources.OrEmpty().OrderBy(static s => s, StringComparer.Ordinal))
        {
            sb.Append(resource).Append(',');
        }

        sb.Append('\n');

        sb.Append("conditionalCaps=");
        foreach (var capability in descriptor.ConditionalCapabilities.OrEmpty().OrderBy(static s => s, StringComparer.Ordinal))
        {
            sb.Append(capability).Append(',');
        }

        sb.Append('\n');

        foreach (var p in descriptor.Parameters.OrEmpty().OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            sb.Append("param=").Append(p.Name)
              .Append(':').Append(p.Type)
              .Append(':').Append(p.Required ? "1" : "0")
              .Append(':').Append(p.IsSecret ? "1" : "0")
              .Append(':').Append(p.MaxLength.ToString(CultureInfo.InvariantCulture))
              .Append(':').Append(p.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "-")
              .Append(':').Append(p.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "-")
              .Append(':').Append(p.DefaultValue ?? "-")
              .Append(':');

            foreach (var v in p.AllowedValues.OrEmpty().OrderBy(static s => s, StringComparer.Ordinal))
            {
                sb.Append(v).Append(',');
            }

            sb.Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>规范化并校验哈希文本；非法时返回 null。</summary>
    public static string? NormalizeHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var text = hash.Trim();
        if (!text.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return null;
        }

        var body = text["sha256:".Length..].Trim();
        if (body.Length != 64)
        {
            return null;
        }

        foreach (var ch in body)
        {
            if (!char.IsAsciiHexDigitLower(ch) && !char.IsAsciiDigit(ch))
            {
                return null;
            }
        }

        return body;
    }

    private static bool IsValidOfficialId(string actionId)
    {
        if (!actionId.StartsWith(ActionReference.OfficialPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var segments = actionId.Split('.');
        if (segments.Length != 3 || segments[0] != "envstation")
        {
            return false;
        }

        return IsLowerSnake(segments[1]) && IsLowerSnake(segments[2]);
    }

    private static bool IsLowerSnake(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidParameterName(string name) => IsLowerSnake(name);
}
