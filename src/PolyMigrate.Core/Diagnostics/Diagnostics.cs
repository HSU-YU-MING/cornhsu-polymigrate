namespace PolyMigrate.Core.Diagnostics;

/// <summary>問題嚴重度(§3.8)。error 阻斷發布(exit 2);warning 記錄但不阻斷(exit 1)。</summary>
public enum Severity
{
    Warning,
    Error,
}

public static class SeverityWire
{
    /// <summary>
    /// CSV 與主控台輸出用的小寫字串("error"/"warning")。
    /// 清單以此字串排序,保持與 1.x 逐位元組一致("error" &lt; "warning",ordinal)。
    /// </summary>
    public static string Wire(this Severity severity) => severity == Severity.Error ? "error" : "warning";
}

/// <summary>路徑安全問題(§3.4):輸出於 path_issues.csv 的一列。取代原本的裸 value-tuple。</summary>
public sealed record PathIssue(Severity Severity, string Page, string Issue);
