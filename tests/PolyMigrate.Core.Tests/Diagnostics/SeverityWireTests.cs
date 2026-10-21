using PolyMigrate.Core.Diagnostics;

namespace PolyMigrate.Core.Tests.Diagnostics;

public class SeverityWireTests
{
    [Fact]
    public void WireStrings_AreTheCsvAndConsoleValues()
    {
        // path_issues.csv / verify_report.csv 的 severity 欄與主控台 [error]/[warning] 都是這兩個字串;
        // 清單也依它排序("error" < "warning",ordinal)——改字面值等於同時改格式與排序
        Assert.Equal("error", Severity.Error.Wire());
        Assert.Equal("warning", Severity.Warning.Wire());
        Assert.True(string.CompareOrdinal(Severity.Error.Wire(), Severity.Warning.Wire()) < 0);
    }
}
