using System.Security.Cryptography;
using System.Text;
using StatementImporter.Contracts.Csv;

namespace StatementImporter.Api.Imports;

public sealed record StoredUpload(string Path, string Hash);

public sealed class UploadStorage
{
    public const long MaxUploadBytes = 200L * 1024 * 1024;
    private const int HeaderSniffLimit = 1024;

    private readonly string _root;

    public UploadStorage(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public async Task<StoredUpload?> SaveAsync(Guid importId, Stream body, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var filled = 0;
        var newline = -1;
        while (filled < HeaderSniffLimit)
        {
            var read = await body.ReadAsync(buffer.AsMemory(filled, HeaderSniffLimit - filled), cancellationToken);
            if (read == 0)
                break;
            var index = buffer.AsSpan(filled, read).IndexOf((byte)'\n');
            filled += read;
            if (index >= 0)
            {
                newline = filled - read + index;
                break;
            }
        }

        var header = Encoding.UTF8.GetString(buffer, 0, newline >= 0 ? newline : filled);
        if (!CsvFormat.IsHeader(header))
            return null;

        var path = Path.Combine(_root, $"{importId}.csv");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            hash.AppendData(buffer, 0, filled);
            await file.WriteAsync(buffer.AsMemory(0, filled), cancellationToken);

            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            Delete(path);
            throw;
        }

        return new StoredUpload(path, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
