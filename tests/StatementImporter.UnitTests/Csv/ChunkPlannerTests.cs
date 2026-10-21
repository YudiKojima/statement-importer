using System.Text;
using StatementImporter.Contracts.Csv;

namespace StatementImporter.UnitTests.Csv;

public class ChunkPlannerTests
{
    private static MemoryStream Csv(string content, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new MemoryStream(bom ? [.. Encoding.UTF8.GetPreamble(), .. bytes] : bytes);
    }

    private static string Lines(int count, string newline = "\n") =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"TX-{i},2026-03-15,LINHA {i},1.00{newline}"));

    private static List<RawLine> ReadAll(Stream file, ChunkPlan plan) =>
        plan.Chunks.SelectMany(chunk => ChunkReader.Read(file, chunk)).ToList();

    [Fact]
    public void Header_only_file_has_no_lines_and_no_chunks()
    {
        var plan = ChunkPlanner.Plan(Csv(CsvFormat.Header + "\n"), chunkSize: 10);

        Assert.Equal(0, plan.TotalLines);
        Assert.Empty(plan.Chunks);
    }

    [Fact]
    public void Lines_are_split_into_chunks_of_the_given_size()
    {
        var plan = ChunkPlanner.Plan(Csv(CsvFormat.Header + "\n" + Lines(25)), chunkSize: 10);

        Assert.Equal(25, plan.TotalLines);
        Assert.Equal([10, 10, 5], plan.Chunks.Select(c => c.LineCount));
        Assert.Equal([0, 1, 2], plan.Chunks.Select(c => c.Index));
        Assert.Equal([2, 12, 22], plan.Chunks.Select(c => c.FirstLineNumber));
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    public void Reading_every_chunk_returns_every_line_once_in_order(string newline, bool bom)
    {
        var file = Csv(CsvFormat.Header + newline + Lines(23, newline), bom);
        var plan = ChunkPlanner.Plan(file, chunkSize: 5);

        var lines = ReadAll(file, plan);

        Assert.Equal(23, lines.Count);
        Assert.Equal(Enumerable.Range(1, 23).Select(i => $"TX-{i},2026-03-15,LINHA {i},1.00"), lines.Select(l => l.Text));
        Assert.Equal(Enumerable.Range(2, 23), lines.Select(l => l.LineNumber));
    }

    [Fact]
    public void Blank_lines_are_skipped_but_keep_physical_line_numbers()
    {
        var content = CsvFormat.Header + "\nTX-1,2026-03-15,A,1.00\n\n   \nTX-2,2026-03-15,B,1.00\n\nTX-3,2026-03-15,C,1.00\n";
        var file = Csv(content);
        var plan = ChunkPlanner.Plan(file, chunkSize: 2);

        var lines = ReadAll(file, plan);

        Assert.Equal(3, plan.TotalLines);
        Assert.Equal([2, 1], plan.Chunks.Select(c => c.LineCount));
        Assert.Equal([2, 5, 7], lines.Select(l => l.LineNumber));
    }

    [Fact]
    public void Last_line_without_newline_is_counted()
    {
        var file = Csv(CsvFormat.Header + "\nTX-1,2026-03-15,A,1.00\nTX-2,2026-03-15,B,1.00");
        var plan = ChunkPlanner.Plan(file, chunkSize: 10);

        var lines = ReadAll(file, plan);

        Assert.Equal(2, plan.TotalLines);
        Assert.Equal("TX-2,2026-03-15,B,1.00", lines[^1].Text);
    }

    [Fact]
    public void Multibyte_characters_do_not_break_offsets()
    {
        var file = Csv(CsvFormat.Header + "\nTX-1,2026-03-15,PADARIA SÃO JOÃO,1.00\nTX-2,2026-03-15,AÇAÍ,2.00\n");
        var plan = ChunkPlanner.Plan(file, chunkSize: 1);

        var lines = ReadAll(file, plan);

        Assert.Equal(["TX-1,2026-03-15,PADARIA SÃO JOÃO,1.00", "TX-2,2026-03-15,AÇAÍ,2.00"], lines.Select(l => l.Text));
    }
}
