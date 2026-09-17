namespace EnvStation.Core.Diagnostics;
/// <summary>
/// 真实写入-删除测试的结论（需求 V6）。
/// </summary>
/// <param name="TargetDirectory">用户填的目标目录。</param>
/// <param name="TestedDirectory">实际做写入测试的目录。</param>
/// <param name="Ok">测试是否通过。</param>
/// <param name="FailureReason">未通过时的具体原因（可直接显示给用户）。</param>
/// <param name="ProbeFileName">本次测试文件的名字（未通过且未能删除时用于告诉用户残留了什么）。</param>
public sealed record TargetWriteResult(
    string TargetDirectory,
    string TestedDirectory,
    bool Ok,
    string? FailureReason,
    string? ProbeFileName)
{
    /// <summary>测试是否在别的目录上做的（目标目录尚不存在）。</summary>
    public bool Substituted =>
        !string.Equals(TargetDirectory, TestedDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>未通过即阻断（V6：不能只依赖规则判断）。</summary>
    public bool IsBlocking => !Ok;

    /// <summary>一行结论，供界面与审计使用。</summary>
    public string ToLine()
    {
        if (!Ok)
        {
            return FailureReason is { Length: > 0 } reason
                ? reason
                : $"写入测试未通过：{TestedDirectory}";
        }

        return Substituted
            ? $"写入测试通过：目标目录尚不存在，在上级 {TestedDirectory} 完成写入与删除。"
            : $"写入测试通过：{TestedDirectory} 可写入。";
    }
}

/// <summary>
/// 在目标目录上做一次<b>真实的</b>写入-读取-删除测试（需求 V6）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么规则判断不够</b>：R1~R17 全是"看名字、看属性"得出的结论。它们推不出
/// "这个目录到底能不能写"——真实原因可能是只读属性、组策略、磁盘满、杀毒软件拦截、
/// 权限被继承的 ACL 拒掉。这些只有真的写一次才知道。V6 的原话是
/// 「实际写入前，必须对目标目录做一次真实写入-删除测试，不能只依赖规则判断」。
/// </para>
/// <para>
/// <b>为什么读回一遍</b>：只写不读会漏掉一类真实故障——写入被重定向到别处
/// （目录联接、卷影副本、同步客户端的占位文件）。读回来的字节与写下去的不一致，
/// 就是"看似写完其实没落地"。因此本测试是<b>写入 → 读回 → 比对 → 删除</b>四步。
/// </para>
/// <para>
/// <b>为什么目标目录不存在时不建它</b>：前置检查本身不该有副作用。
/// 目标目录尚不存在时，测试退到最近的已存在上级目录，并在结论里<b>说清这一点</b>
/// （<see cref="TargetWriteResult.Substituted"/>）——用户需要知道测的是哪一层，
/// 而不是看到一个笼统的"通过"。
/// </para>
/// <para>
/// <b>本类只做文件级测试</b>：不创建子目录。安装确实需要建目录，但那一步失败时
/// 事务会回滚（I3），而"能不能建目录"与"能不能在这个卷上写文件"在同一 ACL 下是同一个答案。
/// 少一个副作用换来的确定性更值。
/// </para>
/// </remarks>
public static class TargetWriteProbe
{
    /// <summary>探测文件名前缀。刻意可识别：异常退出后用户能一眼看出这是谁留下的。</summary>
    public const string ProbeFileStem = ".envstation-write-probe-";

    /// <summary>测试用的内容。带一个每次不同的标记，避免"读到的其实是上次的残留"。</summary>
    private const string ContentPrefix = "EnvStation write probe ";

    /// <summary>
    /// 对 <paramref name="targetDirectory"/> 做一次写入-读回-删除测试。
    /// </summary>
    /// <exception cref="ArgumentException">未给出目标目录时抛出（调用方的编程错误，不是环境问题）。</exception>
    public static TargetWriteResult Run(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var target = targetDirectory.Replace('/', '\\');

        // ── 找到实际要测的目录 ──
        var tested = target;
        while (!Directory.Exists(tested))
        {
            // 上级是个文件：说明这条路径本身不成立（`D:\file.txt\python`）。
            // 必须在这里拦住——继续向上会走到盘根，而盘根通常可写，
            // 于是我们会给出一个"通过"，而这个目录根本建不出来。
            if (File.Exists(tested))
            {
                return Fail(target, tested, $"上级路径是一个文件，无法在其下建立目录：{tested}");
            }

            var parent = Path.GetDirectoryName(tested.TrimEnd('\\'));
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, tested, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(target, target, $"目标位置所在的磁盘或上级目录不存在：{target}");
            }

            tested = parent;
        }

        var probeName = ProbeFileStem + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var probePath = Path.Combine(tested, probeName);
        var content = ContentPrefix + Guid.NewGuid().ToString("N");

        try
        {
            // ── 写入 ──
            File.WriteAllText(probePath, content);

            // ── 读回 ──
            var readBack = File.ReadAllText(probePath);
            if (!string.Equals(readBack, content, StringComparison.Ordinal))
            {
                TryDelete(probePath);
                return Fail(target, tested, $"写入后读回的内容与写入不一致：{tested} 的写入未真正落盘。");
            }

            // ── 删除 ──
            File.Delete(probePath);
            if (File.Exists(probePath))
            {
                return Fail(target, tested, $"测试文件未能删除，已留在 {probePath}。");
            }

            return new TargetWriteResult(target, tested, Ok: true, FailureReason: null, ProbeFileName: probeName);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(probePath);
            return Fail(target, tested, $"没有写入权限：{tested}（{ex.Message}）");
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Security.SecurityException)
        {
            TryDelete(probePath);
            return Fail(target, tested, $"无法在 {tested} 写入：{ex.Message}");
        }
    }

    /// <summary>一行结论的快捷方式（供审计与报告）。</summary>
    public static string Describe(TargetWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ToLine();
    }

    private static TargetWriteResult Fail(string target, string tested, string reason) =>
        new(target, tested, Ok: false, FailureReason: reason, ProbeFileName: null);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不覆盖原始结论：用户看到的应该是"为什么写不进去"，
            // 而不是被"清理也失败了"顶掉。残留文件名已在结论里给出。
        }
    }
}
