using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Util.Store;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Options;
using OMS.Configuration;

namespace OMS.Services;

public sealed class GoogleSheetsService
{
    private readonly GoogleWorkspaceOptions _options;
    private readonly IConfiguration _configuration;
    private readonly GoogleSheetsCredentialStore _credentialStore;
    private readonly Lazy<Task<SheetsService>> _sheets;

    public GoogleSheetsService(
        IOptions<GoogleWorkspaceOptions> options,
        IConfiguration configuration,
        GoogleSheetsCredentialStore credentialStore)
    {
        _options = options.Value;
        _configuration = configuration;
        _credentialStore = credentialStore;
        _sheets = new Lazy<Task<SheetsService>>(CreateServiceAsync);
    }

    public async Task<IList<IList<object>>> ReadAsync(string range, CancellationToken cancellationToken)
    {
        var sheets = await _sheets.Value;
        var request = sheets.Spreadsheets.Values.Get(_options.SpreadsheetId, range);
        request.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.FORMATTEDVALUE;
        var response = await request.ExecuteAsync(cancellationToken);
        return response.Values ?? new List<IList<object>>();
    }

    public async Task AppendAsync(string range, IList<object> row, CancellationToken cancellationToken)
    {
        await AppendRowsAsync(range, [row], cancellationToken);
    }

    public async Task AppendRowsAsync(string range, IList<IList<object>> rows, CancellationToken cancellationToken)
    {
        var sheets = await _sheets.Value;
        var body = new ValueRange { Values = rows };
        var request = sheets.Spreadsheets.Values.Append(body, _options.SpreadsheetId, range);
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.RAW;
        request.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
        await request.ExecuteAsync(cancellationToken);
    }

    public async Task UpdateAsync(string range, IList<IList<object>> rows, CancellationToken cancellationToken)
    {
        var sheets = await _sheets.Value;
        var body = new ValueRange { Values = rows };
        var request = sheets.Spreadsheets.Values.Update(body, _options.SpreadsheetId, range);
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        await request.ExecuteAsync(cancellationToken);
    }

    public async Task UpdateManyAsync(IList<ValueRange> ranges, CancellationToken cancellationToken)
    {
        var sheets = await _sheets.Value;
        var body = new BatchUpdateValuesRequest
        {
            ValueInputOption = "RAW",
            Data = ranges
        };
        var request = sheets.Spreadsheets.Values.BatchUpdate(body, _options.SpreadsheetId);
        await request.ExecuteAsync(cancellationToken);
    }

    public async Task<UserCredential> CreateUserCredentialAsync(
        string[] scopes,
        CancellationToken cancellationToken)
    {
        var clientId = _configuration["Authentication:Google:ClientId"];
        var clientSecret = _configuration["Authentication:Google:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Konfigurasi OAuth Google ClientId dan ClientSecret belum diisi.");

        var refreshToken = await _credentialStore.ReadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(
                "Akun pemilik spreadsheet belum dihubungkan. Buka /account/connect-sheets dan izinkan akses Google.");

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = scopes,
            DataStore = new NullDataStore()
        });
        return new UserCredential(flow, _options.SheetsUser, new TokenResponse { RefreshToken = refreshToken });
    }

    private async Task<SheetsService> CreateServiceAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.SpreadsheetId))
            throw new InvalidOperationException("Konfigurasi GoogleWorkspace:SpreadsheetId belum diisi.");
        var credential = await CreateUserCredentialAsync(
            [SheetsService.Scope.Spreadsheets],
            CancellationToken.None);

        return new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "OMS Outing Management System"
        });
    }
}
