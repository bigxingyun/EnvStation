using System.Diagnostics;

namespace EnvStation.TestKit;

/// <summary>单个用例的执行结果。</summary>
public enum CaseOutcome
{
    Pass,
    Fail,
    Skip,
}

/// <summary>一条用例记录（供结论表与 CI 解析）。</summary>
public sealed record CaseResult(
    string Id,
    string Name,
    CaseOutcome Outcome,
    string? Detail,
    TimeSpan Elapsed);

/// <summary>
/// 零依赖测试宿主。
///
/// <para><b>为什么不用 xunit：</b>开发环境 NuGet 不可达（沙箱阻断出站网络，离线包缓存也没有测试框架）。
/// 本宿主只依赖 BCL，因此<b>任何环境都能跑</b>；等 NuGet 可用后可平移到 xunit，断言语义保持一致。</para>
///
/// <para><b>与 CI 的契约</b>：全部用例通过时进程退出码为 0，否则为 1。
/// 另有 <c>--json &lt;path&gt;</c> 输出机器可读结果，供 M0 报告聚合。</para>
/// </summary>
public sealed class TestHarness
{
    private readonly List<CaseResult> _results = [];
    private readonly string _suiteName;
    private readonly TextWriter _out;

    public TestHarness(string suiteName, TextWriter? output = null)
    {
        _suiteName = suiteName;
        _out = output ?? Console.Out;
    }

    /// <summary>执行一条用例。断言失败（<see cref="AssertionException"/>）记为 Fail；
    /// 抛出 <see cref="SkipException"/> 记为 Skip；其他异常记为 Fail 并打印堆栈。</summary>
    public void Case(string id, string name, Action body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            body();
            sw.Stop();
            Record(new CaseResult(id, name, CaseOutcome.Pass, null, sw.Elapsed));
        }
        catch (SkipException ex)
        {
            sw.Stop();
            Record(new CaseResult(id, name, CaseOutcome.Skip, ex.Message, sw.Elapsed));
        }
        catch (AssertionException ex)
        {
            sw.Stop();
            Record(new CaseResult(id, name, CaseOutcome.Fail, ex.Message, sw.Elapsed));
        }
        catch (Exception ex)
        {
            sw.Stop();
            Record(new CaseResult(id, name, CaseOutcome.Fail, $"意外异常：{ex.GetType().Name}: {ex.Message}", sw.Elapsed));
        }
    }

    /// <summary>异步用例。</summary>
    public void CaseAsync(string id, string name, Func<Task> body)
        => Case(id, name, () => body().GetAwaiter().GetResult());

    private void Record(CaseResult result)
    {
        _results.Add(result);

        var (mark, color) = result.Outcome switch
        {
            CaseOutcome.Pass => ("PASS", ConsoleColor.Green),
            CaseOutcome.Skip => ("SKIP", ConsoleColor.DarkYellow),
            _ => ("FAIL", ConsoleColor.Red),
        };

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        _out.Write(mark);
        Console.ForegroundColor = previous;

        _out.Write($"  {result.Id,-10} {result.Name}");
        if (result.Detail is not null)
        {
            _out.Write($"  — {result.Detail}");
        }

        _out.WriteLine($"  ({result.Elapsed.TotalMilliseconds:F0}ms)");
    }

    /// <summary>输出汇总并返回进程退出码。</summary>
    public int Summarize()
    {
        var pass = _results.Count(r => r.Outcome == CaseOutcome.Pass);
        var fail = _results.Count(r => r.Outcome == CaseOutcome.Fail);
        var skip = _results.Count(r => r.Outcome == CaseOutcome.Skip);

        _out.WriteLine();
        _out.WriteLine(new string('─', 78));
        _out.WriteLine($"{_suiteName}：共 {_results.Count} 项 · 通过 {pass} · 失败 {fail} · 跳过 {skip}");

        if (fail > 0)
        {
            _out.WriteLine();
            _out.WriteLine("失败用例：");
            foreach (var r in _results.Where(r => r.Outcome == CaseOutcome.Fail))
            {
                _out.WriteLine($"  [{r.Id}] {r.Name}");
                _out.WriteLine($"         {r.Detail}");
            }
        }

        _out.WriteLine(new string('─', 78));
        return fail == 0 ? 0 : 1;
    }

    /// <summary>把结果写到 JSON（无外部依赖，手写以保持零包依赖）。</summary>
    public void WriteJson(string path)
    {
        var pass = _results.Count(r => r.Outcome == CaseOutcome.Pass);
        var fail = _results.Count(r => r.Outcome == CaseOutcome.Fail);
        var skip = _results.Count(r => r.Outcome == CaseOutcome.Skip);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"suite\": {Quote(_suiteName)},");
        sb.AppendLine($"  \"total\": {_results.Count}, \"pass\": {pass}, \"fail\": {fail}, \"skip\": {skip},");
        sb.AppendLine($"  \"generatedAt\": {Quote(DateTimeOffset.Now.ToString("O"))},");
        sb.AppendLine("  \"cases\": [");
        for (var i = 0; i < _results.Count; i++)
        {
            var r = _results[i];
            var comma = i == _results.Count - 1 ? string.Empty : ",";
            sb.AppendLine(
                $"    {{ \"id\": {Quote(r.Id)}, \"name\": {Quote(r.Name)}, " +
                $"\"outcome\": {Quote(r.Outcome.ToString().ToLowerInvariant())}, " +
                $"\"detail\": {(r.Detail is null ? "null" : Quote(r.Detail))}, " +
                $"\"elapsedMs\": {r.Elapsed.TotalMilliseconds:F0} }}{comma}");
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
        _out.WriteLine($"结果已写入：{path}");
    }

    private static string Quote(string s)
        => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    /// <summary>解析通用命令行参数：<c>--json &lt;path&gt;</c>。</summary>
    public static string? GetJsonPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--json", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
