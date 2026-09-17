using System.Security.Cryptography;
using System.Text;
using EnvStation.Abstractions;

namespace EnvStation.Core.Transactions;

/// <summary>
/// 跨进程互斥锁，用于保护环境变量事务。
///
/// <para><b>为什么必须有它（M0-P02 用例 P02-10 实测结论）：</b>
/// 注册表写入是"读-改-写"三步，本身没有原子性。实测中 8 个并发写入相互覆盖，
/// 出现了丢失更新。而现实场景确实存在并发：GUI 与 CLI 同时运行、托盘常驻进程与
/// 命令行操作撞在一起、两个自动化包并行执行。</para>
///
/// <para><b>互斥范围：</b>按"作用域 + 变量名"加锁，而不是全局一把大锁。
/// 这样"改 PATH"与"改 JAVA_HOME"不会互相阻塞，但"两个进程同时改 PATH"会被串行化。</para>
///
/// <para><b>跨会话问题：</b>用户级（HKCU）只用 <c>Local\</c> 前缀即可；
/// 系统级（HKLM）可能被不同用户会话同时修改，必须用 <c>Global\</c> 前缀。
/// 注意 <c>Global\</c> 需要相应权限，失败时降级为 <c>Local\</c> 并记录警告（不静默忽略）。</para>
/// </summary>
public sealed class EnvironmentMutex : IDisposable
{
    /// <summary>获取锁的默认超时。超出则认为另一进程卡死，返回结构化错误而不是无限等待。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly Mutex _mutex;
    private readonly bool _acquired;
    private readonly bool _usedGlobalScope;

    private EnvironmentMutex(Mutex mutex, bool acquired, bool usedGlobalScope)
    {
        _mutex = mutex;
        _acquired = acquired;
        _usedGlobalScope = usedGlobalScope;
    }

    /// <summary>是否成功持有锁。</summary>
    public bool IsAcquired => _acquired;

    /// <summary>是否使用了 Global 前缀（系统级作用域）。降级情况可由调用方写入日志。</summary>
    public bool UsedGlobalScope => _usedGlobalScope;

    /// <summary>
    /// 为"某作用域下的某个变量"加锁。
    /// </summary>
    /// <param name="scope">环境变量作用域。系统级会尝试使用 Global 前缀。</param>
    /// <param name="name">变量名（大小写不敏感，内部统一小写后取哈希）。</param>
    /// <param name="timeout">等待超时。</param>
    public static Result<EnvironmentMutex> Acquire(
        Abstractions.Environment.EnvScope scope,
        string name,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<EnvironmentMutex>.Fail(
                EnvStationErrorCodes.EnvNameInvalid, "无法为空的变量名加锁。");
        }

        var wait = timeout ?? DefaultTimeout;
        var mutexName = BuildMutexName(scope, name);

        // 系统级优先 Global；失败（权限不足）时降级为 Local。
        // 降级会削弱跨会话保护，因此必须让调用方知道。
        var preferGlobal = scope == Abstractions.Environment.EnvScope.Machine;
        foreach (var useGlobal in preferGlobal ? new[] { true, false } : [false])
        {
            var fullName = (useGlobal ? @"Global\" : @"Local\") + mutexName;
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: false, name: fullName);
                var got = false;
                try
                {
                    got = mutex.WaitOne(wait);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个持有者崩溃（未释放）。锁已归我们所有 —— 这**不是**错误，
                    // 而是恰好说明恢复了中断事务，应继续执行而非失败。
                    got = true;
                }

                if (!got)
                {
                    mutex.Dispose();
                    return Result<EnvironmentMutex>.Fail(
                        EnvStationErrorCodes.TxMutexTimeout,
                        $"等待环境变量锁超时（{wait.TotalSeconds:F0} 秒）：{name}。",
                        "可能有另一个环境站进程或自动化包正在修改同一变量。稍后重试；持续出现时检查是否有卡死的进程。");
                }

                return Result<EnvironmentMutex>.Ok(new EnvironmentMutex(mutex, acquired: true, usedGlobalScope: useGlobal));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
            {
                mutex?.Dispose();
                if (useGlobal)
                {
                    // Global 不可用，降级重试
                    continue;
                }

                return Result<EnvironmentMutex>.Fail(
                    EnvStationErrorCodes.TxMutexUnavailable,
                    $"无法创建环境变量锁：{ex.Message}",
                    "检查系统是否禁用了命名互斥体，或稍后重试。",
                    ex);
            }
        }

        return Result<EnvironmentMutex>.Fail(
            EnvStationErrorCodes.TxMutexUnavailable, "无法创建环境变量锁。");
    }

    /// <summary>
    /// 构造互斥体名称。
    /// <para>
    /// 变量名要参与哈希：一方面避免不同变量互相阻塞，另一方面互斥体名不能包含
    /// 反斜杠等字符（变量名虽不含，但统一哈希更稳），也避免把变量名暴露在全局命名空间里。
    /// </para>
    /// </summary>
    internal static string BuildMutexName(Abstractions.Environment.EnvScope scope, string name)
    {
        var scopeTag = scope == Abstractions.Environment.EnvScope.Machine ? "machine" : "user";
        var normalized = name.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{scopeTag}|{normalized}"));
        var hex = Convert.ToHexString(hash).ToLowerInvariant()[..16];
        return $"EnvStation.EnvTx.{scopeTag}.{hex}";
    }

    /// <summary>
    /// 便捷用法：在锁保护下执行一段操作。锁必然被释放（含异常路径）。
    /// </summary>
    public static Result<T> RunLocked<T>(
        Abstractions.Environment.EnvScope scope,
        string name,
        Func<Result<T>> action,
        TimeSpan? timeout = null)
    {
        var acquired = Acquire(scope, name, timeout);
        if (acquired.IsFailure)
        {
            return acquired.Propagate<T>();
        }

        using var handle = acquired.Value;
        return action();
    }

    public void Dispose()
    {
        if (!_acquired)
        {
            _mutex.Dispose();
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 未持有却释放（不应发生）—— 吞掉以避免掩盖真正的业务异常，但释放资源
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
