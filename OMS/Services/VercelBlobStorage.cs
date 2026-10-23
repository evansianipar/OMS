using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OMS.Services;

public sealed class VercelBlobStorage
{
    private const string ApiVersion = "13";
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly string _storeId;

    public VercelBlobStorage(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClient = httpClientFactory.CreateClient();
        _token = configuration["BLOB_READ_WRITE_TOKEN"]?.Trim() ?? string.Empty;
        _storeId = ParseStoreId(_token);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_token);

    public async Task<BlobReadResult?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var url = $"https://{_storeId}.private.blob.vercel-storage.com/{EscapePath(path)}?cache=0";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Gagal membaca file privat Vercel Blob ({(int)response.StatusCode}): {details}");
        }
        return new BlobReadResult(
            await response.Content.ReadAsStringAsync(cancellationToken),
            response.Headers.ETag?.Tag);
    }

    public async Task<string?> WriteTextAsync(
        string path,
        string content,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var url = "https://vercel.com/api/blob/?pathname=" + Uri.EscapeDataString(path);
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.TryAddWithoutValidation("x-api-version", ApiVersion);
        request.Headers.TryAddWithoutValidation(
            "x-api-blob-request-id",
            $"{_storeId}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}");
        request.Headers.TryAddWithoutValidation("x-vercel-blob-store-id", _storeId);
        request.Headers.TryAddWithoutValidation("x-api-blob-request-attempt", "0");
        request.Headers.TryAddWithoutValidation("x-vercel-blob-access", "private");
        request.Headers.TryAddWithoutValidation("x-add-random-suffix", "0");
        if (ifMatch is null)
            request.Headers.TryAddWithoutValidation("x-allow-overwrite", "0");
        else
        {
            request.Headers.TryAddWithoutValidation("x-if-match", ifMatch);
            request.Headers.TryAddWithoutValidation("x-allow-overwrite", "1");
        }
        request.Headers.TryAddWithoutValidation("x-content-type", "application/json");
        request.Content = new StringContent(content, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Gagal menyimpan file privat ke Vercel Blob ({(int)response.StatusCode}): {details}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(responseJson);
        return document.RootElement.TryGetProperty("etag", out var etag) &&
               etag.ValueKind == JsonValueKind.String
            ? etag.GetString()
            : null;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("BLOB_READ_WRITE_TOKEN belum dikonfigurasi.");
    }

    private static string ParseStoreId(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;
        var parts = token.Split('_');
        if (parts.Length < 4 || !parts[0].Equals("vercel", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("BLOB_READ_WRITE_TOKEN bukan token Vercel Blob yang valid.");
        return parts[3];
    }

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public sealed record BlobReadResult(string Content, string? ETag);
}
