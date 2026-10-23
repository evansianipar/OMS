using OMS.Models;

namespace OMS.Services;

public sealed class UserDirectoryService(OmsJsonDatabase database)
{
    public async Task<OmsUser?> FindActiveUserAsync(string email, CancellationToken cancellationToken)
    {
        var rows = await database.ReadAsync("'Users'!A1:Z", cancellationToken);
        if (rows.Count < 2) return null;

        var records = ToRecords(rows);
        var record = records.FirstOrDefault(item =>
            Get(item, "Email").Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));
        if (record is null || !bool.TryParse(Get(record, "IsActive"), out var active) || !active)
            return null;

        var role = Get(record, "Role").ToUpperInvariant();
        if (!new[] { "SUPERADMIN", "ADMIN", "REGISTRATION", "VIEWER" }.Contains(role))
            throw new InvalidOperationException("Role pada database JSON Users tidak dikenal.");

        var fullName = Get(record, "FullName");
        return new OmsUser(email.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(fullName) ? email.Trim() : fullName, role);
    }

    public async Task<OmsUser?> VerifyCredentialsAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var rows = await database.ReadAsync("'Users'!A1:Z", cancellationToken);
        var record = ToRecords(rows).FirstOrDefault(item =>
            Get(item, "Email").Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));
        if (record is null ||
            !bool.TryParse(Get(record, "IsActive"), out var active) ||
            !active ||
            !PasswordCredential.Verify(password, Get(record, "PasswordHash"), Get(record, "PasswordSalt")))
            return null;

        var role = Get(record, "Role").ToUpperInvariant();
        if (!new[] { "SUPERADMIN", "ADMIN", "REGISTRATION", "VIEWER" }.Contains(role))
            throw new InvalidOperationException("Role pada database JSON Users tidak dikenal.");

        var fullName = Get(record, "FullName");
        var normalizedEmail = Get(record, "Email").Trim().ToLowerInvariant();
        return new OmsUser(normalizedEmail, string.IsNullOrWhiteSpace(fullName) ? normalizedEmail : fullName, role);
    }

    private static IEnumerable<Dictionary<string, string>> ToRecords(IList<IList<object>> rows)
    {
        if (rows.Count < 2) return [];
        var headers = rows[0].Select(Convert.ToString).Select(value => value?.Trim() ?? string.Empty).ToList();
        return rows.Skip(1)
            .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(Convert.ToString(value))))
            .Select(row => headers.Select((header, index) => (header, index))
                .Where(item => !string.IsNullOrWhiteSpace(item.header))
                .ToDictionary(
                    item => item.header,
                    item => item.index < row.Count ? Convert.ToString(row[item.index]) ?? string.Empty : string.Empty,
                    StringComparer.OrdinalIgnoreCase));
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
}
