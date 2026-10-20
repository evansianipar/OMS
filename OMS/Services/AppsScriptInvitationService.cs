using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Options;
using OMS.Configuration;
using OMS.Models;
using QRCoder;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OMS.Services;

public sealed class AppsScriptInvitationService(
    GoogleSheetsService google,
    IOptions<GoogleWorkspaceOptions> options,
    IHttpClientFactory httpClientFactory)
{
    private const string SendMailScope = "https://www.googleapis.com/auth/script.send_mail";
    private readonly GoogleWorkspaceOptions _options = options.Value;

    public async Task SendAsync(
        string eventId,
        OmsParticipant participant,
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AppsScriptId))
            throw new InvalidOperationException("Konfigurasi GoogleWorkspace:AppsScriptId belum diisi.");

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(token, QRCodeGenerator.ECCLevel.Q);
        var qrPng = new PngByteQRCode(qrCodeData).GetGraphic(8);
        var qrDataUrl = "data:image/png;base64," + Convert.ToBase64String(qrPng);

        var credential = await google.CreateUserCredentialAsync(
            [SheetsService.Scope.Spreadsheets, SendMailScope],
            cancellationToken);
        var accessToken = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://script.googleapis.com/v1/scripts/{Uri.EscapeDataString(_options.AppsScriptId)}:run");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            function = "sendInvitationMail",
            parameters = new object[] { eventId, participant.ParticipantId, token, qrDataUrl }
        });

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Apps Script API gagal ({(int)response.StatusCode}): {GetGoogleErrorMessage(root)}");

        if (root.TryGetProperty("error", out var executionError))
            throw new InvalidOperationException(
                "Apps Script gagal mengirim undangan: " + GetErrorMessage(executionError));

        if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Eksekusi Apps Script tidak selesai.");

        if (!root.TryGetProperty("response", out var scriptResponse) ||
            !scriptResponse.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Apps Script tidak mengonfirmasi pengiriman undangan.");
    }

    private static string GetGoogleErrorMessage(JsonElement root) =>
        root.TryGetProperty("error", out var error)
            ? GetErrorMessage(error)
            : "Google tidak memberikan rincian kesalahan.";

    private static string GetErrorMessage(JsonElement error) =>
        error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() ?? "Kesalahan tidak diketahui."
            : "Kesalahan tidak diketahui.";
}
