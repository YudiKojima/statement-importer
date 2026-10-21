namespace StatementImporter.Contracts.Csv;

public sealed record ChunkInfo(int Index, long StartOffset, int FirstLineNumber, int LineCount);

public sealed record ChunkPlan(int TotalLines, IReadOnlyList<ChunkInfo> Chunks);

public static class ChunkPlanner
{
    public static ChunkPlan Plan(Stream file, int chunkSize)
    {
        var chunks = new List<ChunkInfo>();
        var buffer = new byte[81920];
        long position = 0, lineStart = 0, chunkStart = 0;
        int lineNumber = 1, total = 0, chunkFirstLine = 0, chunkCount = 0;
        var lineHasContent = false;

        int read;
        while ((read = file.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == (byte)'\n')
                {
                    EndLine();
                    lineStart = position + i + 1;
                }
                else if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
                {
                    lineHasContent = true;
                }
            }
            position += read;
        }

        if (position > lineStart)
            EndLine();
        if (chunkCount > 0)
            chunks.Add(new ChunkInfo(chunks.Count, chunkStart, chunkFirstLine, chunkCount));

        return new ChunkPlan(total, chunks);

        void EndLine()
        {
            var isDataLine = lineNumber > 1 && lineHasContent;
            if (isDataLine)
            {
                if (chunkCount == 0)
                {
                    chunkStart = lineStart;
                    chunkFirstLine = lineNumber;
                }
                chunkCount++;
                total++;
                if (chunkCount == chunkSize)
                {
                    chunks.Add(new ChunkInfo(chunks.Count, chunkStart, chunkFirstLine, chunkCount));
                    chunkCount = 0;
                }
            }
            lineNumber++;
            lineHasContent = false;
        }
    }
}
