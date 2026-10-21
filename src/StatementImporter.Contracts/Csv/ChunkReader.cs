using System.Buffers;
using System.Text;

namespace StatementImporter.Contracts.Csv;

public sealed record RawLine(int LineNumber, string Text);

public static class ChunkReader
{
    public static List<RawLine> Read(Stream file, ChunkInfo chunk)
    {
        file.Seek(chunk.StartOffset, SeekOrigin.Begin);
        var lines = new List<RawLine>(chunk.LineCount);
        var buffer = new byte[81920];
        var current = new ArrayBufferWriter<byte>(256);
        var lineNumber = chunk.FirstLineNumber;

        int read;
        while (lines.Count < chunk.LineCount && (read = file.Read(buffer)) > 0)
        {
            var span = buffer.AsSpan(0, read);
            while (lines.Count < chunk.LineCount)
            {
                var newline = span.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    current.Write(span);
                    break;
                }
                current.Write(span[..newline]);
                AddLine();
                span = span[(newline + 1)..];
            }
        }

        if (lines.Count < chunk.LineCount && current.WrittenCount > 0)
            AddLine();

        return lines;

        void AddLine()
        {
            var text = Encoding.UTF8.GetString(current.WrittenSpan).TrimEnd('\r');
            if (!CsvFormat.IsBlank(text))
                lines.Add(new RawLine(lineNumber, text));
            lineNumber++;
            current.ResetWrittenCount();
        }
    }
}
