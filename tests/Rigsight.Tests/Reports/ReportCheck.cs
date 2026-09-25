using System.Collections;
using System.Reflection;
using Rigsight.Core.Reports;

namespace Rigsight.Tests.Reports;

public static class ReportCheck
{
    /// <summary>
    /// Walks every public property of the report (apps, sessions, days, peaks, insights…) and fails on any NaN,
    /// infinity or negative number: all of them are times, temperatures, loads, counts or sizes.
    /// </summary>
    public static void NoBadNumbers(object report)
    {
        var problems = new List<string>();
        Walk(report, "report", problems, new HashSet<object>(ReferenceEqualityComparer.Instance));
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    private static void Walk(object? value, string path, List<string> problems, HashSet<object> seen)
    {
        switch (value)
        {
            case null or string or DateTime or TimeSpan or Enum or bool:
                return;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d) || d < 0) problems.Add($"{path} = {d}");
                return;
            case float f:
                if (float.IsNaN(f) || float.IsInfinity(f) || f < 0) problems.Add($"{path} = {f}");
                return;
            case int or long:
                if (Convert.ToInt64(value) < 0) problems.Add($"{path} = {value}");
                return;
        }
        var type = value.GetType();
        if (type.IsPrimitive) return;
        if (!type.IsValueType && !seen.Add(value)) return;
        if (value is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict) Walk(e.Value, $"{path}[{e.Key}]", problems, seen);
            return;
        }
        if (value is IEnumerable list)
        {
            int i = 0;
            foreach (var item in list) Walk(item, $"{path}[{i++}]", problems, seen);
            return;
        }
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name == "EqualityContract") continue;
            Walk(p.GetValue(value), $"{path}.{p.Name}", problems, seen);
        }
    }
}

public sealed class ReportCheckTests
{
    [Fact]
    public void The_walker_finds_bad_numbers_deep_inside()
    {
        var r = new Report { OnSec = 1 };
        r.Apps.Add(new AppStat { Name = "x", CpuTempAvg = double.NaN });
        r.Days.Add(new DayBucket { Day = DateTime.Today });
        r.Days[0].ActiveByCategory[Rigsight.Core.Settings.AppCategory.Game] = -5;
        r.CpuTempPeak = new Peak(double.PositiveInfinity, DateTime.Now, null);
        var ex = Assert.ThrowsAny<Exception>(() => ReportCheck.NoBadNumbers(r));
        Assert.Contains("report.Apps[0].CpuTempAvg = NaN", ex.Message);
        Assert.Contains("report.Days[0].ActiveByCategory[Game] = -5", ex.Message);
        Assert.Contains("report.CpuTempPeak.Value", ex.Message);
    }
}
