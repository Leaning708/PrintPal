using System.Security.Cryptography;
using PrintPal.Core;

namespace PrintPal.Capture;

public sealed record StoredDocument(
    string Path,
    DocumentFormat Format,
    long Size,
    string Sha256,
    PageCountResult PageCount);

/// <summary>Stores captured documents under a per-job GUID name and counts their pages.</summary>
public sealed class DocumentStore
{
    private readonly string _rootDirectory;

    public DocumentStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A document root directory is required.", nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public async Task<StoredDocument> SaveAsync(
        byte[] document,
        DocumentFormat declaredFormat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var format = DocumentSniffer.Sniff(document);
        if (format == DocumentFormat.Unknown)
        {
            format = declaredFormat;
        }

        var fileName = Guid.NewGuid().ToString("N") + format.Extension();
        var path = Path.Combine(_rootDirectory, fileName);

        await File.WriteAllBytesAsync(path, document, cancellationToken).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant();
        var pageCount = PageCounters.Count(path, format);

        return new StoredDocument(path, format, document.LongLength, hash, pageCount);
    }

    public async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(path);
        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    public void Delete(string path)
    {
        var fullPath = ResolvePath(path);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(_rootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, _rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Document path is outside the document store.");
        }

        return fullPath;
    }
}
