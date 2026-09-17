using System.Runtime.Versioning;
using System.Security;
using EnvStation.Abstractions;
using EnvStation.Abstractions.Environment;
using Microsoft.Win32;

namespace EnvStation.Core.Environment;

/// <summary>
/// 基于 Windows 注册表的环境变量存储（M0-P02 的正式实现）。
///
/// <para><b>关键设计（对应 SEC-11 / PE-4 / PE-5）：</b></para>
/// <list type="bullet">
///   <item>读取时使用 <see cref="RegistryKey.GetValue(string, object, RegistryValueOptions)"/> 并传入
///     <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/>，拿到<b>未展开</b>原始值，
///     从而保住 <c>%JAVA_HOME%\bin</c> 这类引用不被改写。</item>
///   <item>写入时显式传入 <see cref="RegistryValueKind"/>，<b>绝不</b>让 API 推断类型。</item>
///   <item><b>禁止使用 <c>Environment.SetEnvironmentVariable</c> 写持久化变量</b>——它会丢失值类型信息。</item>
/// </list>
///
/// <para><b>权限语义：</b>读取 <c>HKLM</c> 通常不需管理员；写入一定需要。权限不足返回
/// <see cref="EnvStationErrorCodes.EnvScopeDenied"/> 而不是抛未处理异常（需求 6.2 / 12 章）。</para>
/// </summary>
// 平台标注：注册表 API 为 Windows 专属。标注后若在非 Windows 目标中引用本类，编译器会给出 CA1416 提示。
[SupportedOSPlatform("windows")]
public sealed class RegistryEnvStore : IEnvironmentStore
{
    /// <summary>Windows 对单个环境变量值的上限（字符）。见需求 6.2：超出 32767 为阻断级。</summary>
    public const int MaxValueLength = 32767;

    /// <summary>传统"系统属性"GUI 编辑 PATH 的实际上限，超出后无法保存（需求 1.2 P2 / 风险 T2）。</summary>
    public const int LegacyEditorLimit = 2047;

    private const string UserKeyPath = "Environment";

    private const string MachineKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public RegistryEnvStore(EnvScope scope) => Scope = scope;

    public EnvScope Scope { get; }

    public Result<IReadOnlyList<EnvVariable>> ReadAll()
    {
        try
        {
            using var key = OpenRead();
            if (key is null)
            {
                return Result<IReadOnlyList<EnvVariable>>.Fail(
                    EnvStationErrorCodes.EnvScopeDenied,
                    "无法打开环境变量注册表项。",
                    Scope == EnvScope.Machine
                        ? "读取系统级变量通常无需管理员权限；被组策略限制时改用用户级。"
                        : "检查当前用户配置单元是否可访问。");
            }

            var list = new List<EnvVariable>();
            foreach (var name in key.GetValueNames())
            {
                // 畸形项：空名（注册表"默认值"）在环境变量区不应出现
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // P02-9：值名含 '=' 属畸形项，跳过而不崩溃
                if (name.Contains('=', StringComparison.Ordinal))
                {
                    continue;
                }

                var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var kind = key.GetValueKind(name);

                list.Add(new EnvVariable
                {
                    Name = name,
                    RawValue = raw as string ?? string.Empty,
                    Kind = ToEnvValueKind(kind),
                    ContainsVariableReference = LooksLikeExpandable(raw as string),
                });
            }

            return Result<IReadOnlyList<EnvVariable>>.Ok(list);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<EnvVariable>>.Fail(
                EnvStationErrorCodes.EnvScopeDenied,
                $"拒绝读取{ScopeLabel}环境变量。",
                "以管理员身份运行，或检查注册表权限。");
        }
    }

    public Result<EnvVariable?> Read(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Result<EnvVariable?>.Fail(EnvStationErrorCodes.EnvNameInvalid, "变量名不能为空。");
        }

        try
        {
            using var key = OpenRead();
            if (key is null)
            {
                return Result<EnvVariable?>.Fail(
                    EnvStationErrorCodes.EnvScopeDenied, "无法打开环境变量注册表项。");
            }

            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null)
            {
                return Result<EnvVariable?>.Ok(null);
            }

            return Result<EnvVariable?>.Ok(new EnvVariable
            {
                Name = name,
                RawValue = raw as string ?? string.Empty,
                Kind = ToEnvValueKind(key.GetValueKind(name)),
                ContainsVariableReference = LooksLikeExpandable(raw as string),
            });
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return Result<EnvVariable?>.Fail(
                EnvStationErrorCodes.EnvScopeDenied, $"拒绝读取{ScopeLabel}环境变量。");
        }
    }

    public Result<Unit> Write(string name, string rawValue, EnvValueKind kind)
    {
        var validation = ValidateNameAndValue(name, rawValue);
        if (validation.IsFailure)
        {
            return validation;
        }

        try
        {
            using var key = OpenWrite();
            if (key is null)
            {
                return Results.Fail(
                    EnvStationErrorCodes.EnvScopeDenied,
                    $"无法写入{ScopeLabel}环境变量。",
                    Scope == EnvScope.Machine
                        ? "修改系统级变量需要管理员权限（会弹出 UAC）。"
                        : "检查注册表权限。");
            }

            // 关键：显式传 kind，保持原有值类型（SEC-11 / PE-4）
            key.SetValue(name, rawValue, ToRegistryValueKind(kind));
            return Results.Ok();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return Results.Fail(
                EnvStationErrorCodes.EnvScopeDenied,
                $"拒绝写入{ScopeLabel}环境变量。",
                Scope == EnvScope.Machine ? "需要管理员权限（UAC）。" : "检查注册表权限。");
        }
    }

    public Result<Unit> Delete(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Results.Fail(EnvStationErrorCodes.EnvNameInvalid, "变量名不能为空。");
        }

        try
        {
            using var key = OpenWrite();
            if (key is null)
            {
                return Results.Fail(
                    EnvStationErrorCodes.EnvScopeDenied, $"拒绝打开{ScopeLabel}环境变量注册表项。");
            }

            // 幂等（动作契约 AC-2）：不存在也视为成功
            if (Array.IndexOf(key.GetValueNames(), name) >= 0)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }

            return Results.Ok();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return Results.Fail(EnvStationErrorCodes.EnvScopeDenied, $"拒绝删除{ScopeLabel}环境变量。");
        }
    }

    /// <summary>校验变量名与值（需求 6.2 规则的实现子集，完整规则由规则引擎在导入期执行）。</summary>
    public static Result<Unit> ValidateNameAndValue(string name, string rawValue)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Results.Fail(EnvStationErrorCodes.EnvNameInvalid, "变量名不能为空。");
        }

        if (name.Contains('=', StringComparison.Ordinal))
        {
            return Results.Fail(
                EnvStationErrorCodes.EnvNameInvalid,
                "变量名不能包含等号（=）。");
        }

        if (rawValue.Length > MaxValueLength)
        {
            return Results.Fail(
                EnvStationErrorCodes.EnvValueTooLong,
                $"变量值长度 {rawValue.Length} 超过 Windows 上限 {MaxValueLength}。",
                "缩短变量值，或改用托管目录收敛。");
        }

        return Results.Ok();
    }

    private string ScopeLabel => Scope == EnvScope.Machine ? "系统级" : "用户级";

    private RegistryKey? OpenRead() => Scope == EnvScope.Machine
        ? Registry.LocalMachine.OpenSubKey(MachineKeyPath, writable: false)
        : Registry.CurrentUser.OpenSubKey(UserKeyPath, writable: false);

    private RegistryKey? OpenWrite() => Scope == EnvScope.Machine
        ? Registry.LocalMachine.OpenSubKey(MachineKeyPath, writable: true)
        : Registry.CurrentUser.OpenSubKey(UserKeyPath, writable: true);

    internal static EnvValueKind ToEnvValueKind(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.ExpandString => EnvValueKind.ExpandString,
        _ => EnvValueKind.String,
    };

    internal static RegistryValueKind ToRegistryValueKind(EnvValueKind kind) => kind switch
    {
        EnvValueKind.ExpandString => RegistryValueKind.ExpandString,
        _ => RegistryValueKind.String,
    };

    private static bool LooksLikeExpandable(string? value)
        => !string.IsNullOrEmpty(value) && value.Contains('%', StringComparison.Ordinal);
}
