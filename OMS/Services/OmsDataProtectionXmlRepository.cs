using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace OMS.Services;

public sealed class OmsDataProtectionXmlRepository(
    VercelBlobStorage blobStorage) : IXmlRepository
{
    private const string BlobPath = "oms/data-protection-keyring.json";
    private static readonly SemaphoreSlim KeyRingLock = new(1, 1);
    private readonly string _localPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OMS",
        "data-protection-keyring.json");

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        KeyRingLock.Wait();
        try
        {
            var content = ReadJsonAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(content?.Content)) return [];
            var elements = JsonSerializer.Deserialize<List<string>>(content.Content) ?? [];
            return elements.Select(XElement.Parse).ToList();
        }
        finally
        {
            KeyRingLock.Release();
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        KeyRingLock.Wait();
        try
        {
            var content = ReadJsonAsync(CancellationToken.None).GetAwaiter().GetResult();
            var elements = string.IsNullOrWhiteSpace(content?.Content)
                ? []
                : JsonSerializer.Deserialize<List<string>>(content.Content) ?? [];
            elements.Add(element.ToString(SaveOptions.DisableFormatting));
            WriteJsonAsync(JsonSerializer.Serialize(elements), content?.ETag, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        finally
        {
            KeyRingLock.Release();
        }
    }

    private Task<VercelBlobStorage.BlobReadResult?> ReadJsonAsync(CancellationToken cancellationToken) =>
        blobStorage.IsConfigured
            ? blobStorage.ReadTextAsync(BlobPath, cancellationToken)
            : ReadLocalAsync(cancellationToken);

    private async Task<VercelBlobStorage.BlobReadResult?> ReadLocalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_localPath)) return null;
        return new VercelBlobStorage.BlobReadResult(
            await File.ReadAllTextAsync(_localPath, cancellationToken), null);
    }

    private Task WriteJsonAsync(string json, string? etag, CancellationToken cancellationToken) =>
        blobStorage.IsConfigured
            ? WriteBlobAsync(json, etag, cancellationToken)
            : WriteLocalAsync(json, cancellationToken);

    private async Task WriteBlobAsync(string json, string? etag, CancellationToken cancellationToken)
    {
        await blobStorage.WriteTextAsync(BlobPath, json, etag, cancellationToken);
    }

    private async Task WriteLocalAsync(string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_localPath)
            ?? throw new InvalidOperationException("Path key ring Data Protection tidak valid.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _localPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, _localPath, overwrite: true);
    }
}
