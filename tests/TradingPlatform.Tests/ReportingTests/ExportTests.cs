using TradingPlatform.Reporting.Export;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ReportingTests;

public class CsvWriterTests
{
    [Fact]
    public void Write_SimpleValues_ProducesCommaJoinedRows()
    {
        var csv = CsvWriter.Write(new[] { "A", "B" }, new List<IReadOnlyList<string>> { new[] { "1", "2" }, new[] { "3", "4" } });
        Assert.Equal("A,B\n1,2\n3,4\n", csv);
    }

    [Fact]
    public void Write_ValueContainingComma_IsQuoted()
    {
        var csv = CsvWriter.Write(new[] { "A" }, new List<IReadOnlyList<string>> { new[] { "hello, world" } });
        Assert.Equal("A\n\"hello, world\"\n", csv);
    }

    [Fact]
    public void Write_ValueContainingQuote_IsEscapedByDoubling()
    {
        var csv = CsvWriter.Write(new[] { "A" }, new List<IReadOnlyList<string>> { new[] { "say \"hi\"" } });
        Assert.Equal("A\n\"say \"\"hi\"\"\"\n", csv);
    }

    [Fact]
    public void Write_PlainValue_IsNeverQuoted()
    {
        var csv = CsvWriter.Write(new[] { "A" }, new List<IReadOnlyList<string>> { new[] { "plain" } });
        Assert.Equal("A\nplain\n", csv);
    }

    [Fact]
    public void Write_NoRows_StillWritesHeaderLine()
    {
        var csv = CsvWriter.Write(new[] { "A", "B" }, Array.Empty<IReadOnlyList<string>>());
        Assert.Equal("A,B\n", csv);
    }
}

public class HtmlTableRendererTests
{
    [Fact]
    public void Render_IncludesTitle()
    {
        var html = HtmlTableRenderer.Render("My Report", null, null, null);
        Assert.True(html.Contains("<h1>My Report</h1>"));
    }

    [Fact]
    public void Render_EncodesHtmlSpecialCharacters()
    {
        var html = HtmlTableRenderer.Render("A < B & C", null, null, null);
        Assert.True(html.Contains("A &lt; B &amp; C"));
        Assert.False(html.Contains("A < B & C"));
    }

    [Fact]
    public void Render_WithSummary_IncludesDefinitionList()
    {
        var html = HtmlTableRenderer.Render("Report", new[] { ("Key1", "Value1") }, null, null);
        Assert.True(html.Contains("<dt>Key1</dt>"));
        Assert.True(html.Contains("<dd>Value1</dd>"));
    }

    [Fact]
    public void Render_WithTable_IncludesHeadersAndRows()
    {
        var html = HtmlTableRenderer.Render("Report", null, new[] { "Col1" }, new List<IReadOnlyList<string>> { new[] { "Val1" } });
        Assert.True(html.Contains("<th>Col1</th>"));
        Assert.True(html.Contains("<td>Val1</td>"));
    }

    [Fact]
    public void Render_NoSummaryNoTable_StillProducesValidShell()
    {
        var html = HtmlTableRenderer.Render("Empty", null, null, null);
        Assert.True(html.Contains("<html>"));
        Assert.True(html.Contains("</html>"));
    }
}
