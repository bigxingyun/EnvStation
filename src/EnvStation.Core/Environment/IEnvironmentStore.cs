using EnvStation.Abstractions;
using EnvStation.Abstractions.Environment;

namespace EnvStation.Core.Environment;

/// <summary>
/// 环境变量持久化存储的抽象。
/// <para>
/// 刻意不暴露"设置当前进程环境变量"的能力——本工具的职责是修改<b>持久化</b>环境，
/// 进程内环境由内核自行管理，避免误改宿主进程（SEC-4 最小权限、A5 副作用可逆）。
/// </para>
/// </summary>
public interface IEnvironmentStore
{
    /// <summary>作用域。</summary>
    EnvScope Scope { get; }

    /// <summary>
    /// 读取该作用域下全部环境变量（未展开的原始值 + 值类型）。
    /// 只读操作通常无需管理员权限，但系统级仍可能因策略被拒。
    /// </summary>
    Result<IReadOnlyList<EnvVariable>> ReadAll();

    /// <summary>读取单个变量；不存在时返回 <c>null</c> 值的成功结果。</summary>
    Result<EnvVariable?> Read(string name);

    /// <summary>
    /// 写入变量，<b>显式指定值类型</b>。
    /// 调用方必须先取快照（SEC-1），本方法不负责事务。
    /// </summary>
    Result<Unit> Write(string name, string rawValue, EnvValueKind kind);

    /// <summary>删除变量。变量不存在时视为成功（幂等）。</summary>
    Result<Unit> Delete(string name);
}
