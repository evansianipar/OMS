using Microsoft.AspNetCore.DataProtection;

namespace OMS.Services;

public sealed class GoogleSheetsCredentialStore
{
    private readonly IDataProtector _protector;
    private readonly string _filePath;

    public GoogleSheetsCredentialStore(IDataProtectionProvider protectionProvider)
    {
        _protector = protectionProvider.CreateProtector("OMS.GoogleSheetsRefreshToken.v1");
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OMS",
            "google-sheets-token.dat");
    }

    public bool HasCredential => File.Exists(_filePath);

    public async Task SaveAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Google tidak mengembalikan refresh token.", nameof(refreshToken));

        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Lokasi penyimpanan kredensial Google tidak valid.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _filePath + ".tmp";
        var encryptedToken = _protector.Protect(refreshToken);
        await File.WriteAllTextAsync(temporaryPath, encryptedToken, cancellationToken);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return null;

        var encryptedToken = await File.ReadAllTextAsync(_filePath, cancellationToken);
        try
        {
            return _protector.Unprotect(encryptedToken);
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            throw new InvalidOperationException(
                "Kredensial Google Sheets tidak dapat dibuka. Pastikan kunci ASP.NET Data Protection yang sama tersedia.",
                exception);
        }
    }
}
