using OMS.Models;

namespace OMS.Services;

public sealed class UserDirectoryService(GoogleSheetsService sheets)
{
    public async Task<OmsUser?> FindActiveUserAsync(string email, CancellationToken cancellationToken)
    {
        var rows = await sheets.ReadAsync("'Users'!A1:G", cancellationToken);
        if (rows.Count < 2) return null;

        var headers = rows[0].Select(value => Convert.ToString(value)?.Trim() ?? string.Empty).ToList();
        var emailIndex = headers.IndexOf("Email");
        var nameIndex = headers.IndexOf("FullName");
        var roleIndex = headers.IndexOf("Role");
        var activeIndex = headers.IndexOf("IsActive");
        if (emailIndex < 0 || roleIndex < 0 || activeIndex < 0)
            throw new InvalidOperationException("Header sheet Users harus memiliki Email, Role, dan IsActive.");

        foreach (var row in rows.Skip(1))
        {
            var rowEmail = GetCell(row, emailIndex).Trim();
            if (!rowEmail.Equals(email, StringComparison.OrdinalIgnoreCase)) continue;
            if (!bool.TryParse(GetCell(row, activeIndex), out var isActive) || !isActive) return null;

            var role = GetCell(row, roleIndex).Trim().ToUpperInvariant();
            if (!new[] { "SUPERADMIN", "ADMIN", "REGISTRATION", "VIEWER" }.Contains(role))
                throw new InvalidOperationException("Role pada sheet Users tidak dikenal.");
            var fullName = nameIndex < 0 ? email : GetCell(row, nameIndex).Trim();
            return new OmsUser(email, string.IsNullOrWhiteSpace(fullName) ? email : fullName, role);
        }
        return null;
    }

    private static string GetCell(IList<object> row, int index) =>
        index >= 0 && index < row.Count ? Convert.ToString(row[index]) ?? string.Empty : string.Empty;
}
