using Obstruo.Service.Data;
using Xunit;

namespace Obstruo.Tests;

/// <summary>RFC 4180 CSV escaping for the log export.</summary>
public class LogExporterTests
{
    [Theory]
    [InlineData("example.com", "example.com")]        // plain — untouched
    [InlineData("", "")]
    [InlineData("a,b", "\"a,b\"")]                      // comma → quoted
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]  // quote → doubled + wrapped
    [InlineData("line1\nline2", "\"line1\nline2\"")]  // newline → quoted
    public void EscapeCsv_HandlesSpecialChars(string input, string expected)
        => Assert.Equal(expected, LogExporter.EscapeCsv(input));

    [Fact]
    public void EscapeCsv_NullBecomesEmpty()
        => Assert.Equal("", LogExporter.EscapeCsv(null));
}
