namespace OMS.Configuration;

public sealed class GoogleWorkspaceOptions
{
    public string Domain { get; set; } = "wom.co.id";
    public string SpreadsheetId { get; set; } = string.Empty;
    public string SheetsUser { get; set; } = "itengineer@wom.co.id";
    public string AppsScriptId { get; set; } = string.Empty;
    public string CredentialDirectory { get; set; } = string.Empty;
}
