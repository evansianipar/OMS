using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OMS.Configuration;
using OMS.Models;

namespace OMS.Services;

public sealed class OmsJsonDatabase
{
    private const string DatabasePath = "oms/oms-database.json";
    private static readonly SemaphoreSlim DatabaseLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<string, string[]> DefaultHeaders =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Users"] = ["UserId", "Email", "FullName", "Role", "IsActive", "CreatedAt", "UpdatedAt", "PasswordHash", "PasswordSalt"],
            ["Events"] = ["EventId", "EventCode", "EventName", "Description", "StartDate", "EndDate", "Location", "Status", "CreatedAt", "CreatedBy", "UpdatedAt"],
            ["Participants"] = ["ParticipantId", "EventId", "EmployeeNumber", "FullName", "Department", "PhoneNumber", "Email", "QrTokenHash", "IsActive", "CreatedAt", "UpdatedAt"],
            ["Sessions"] = ["SessionId", "EventId", "SessionName", "Description", "StartTime", "EndTime", "Location", "SessionType", "Status", "CreatedAt"],
            ["Attendance"] = ["AttendanceId", "EventId", "ParticipantId", "SessionId", "AttendanceTime", "CheckInMethod", "CheckedBy", "Notes", "CreatedAt", "CheckOutTime", "CheckOutMethod", "CheckedOutBy"],
            ["AuditLog"] = ["AuditId", "EventId", "ActorEmail", "Action", "EntityType", "EntityId", "Timestamp", "Details"],
            ["List All EMP LENGKAP"] = ["NIK", "EMPLOYEE_NAME", "ORG_NAME", "PHONE_NUMBER", "EMAIL", "CAMP", "TRANSPORT", "VEHICLE"]
        };

    private readonly VercelBlobStorage _blobStorage;
    private readonly ILogger<OmsJsonDatabase> _logger;
    private readonly string _localPath;
    private readonly OmsAuthOptions _authOptions;
    private DatabaseDocument? _document;
    private string? _blobETag;

    public OmsJsonDatabase(
        VercelBlobStorage blobStorage,
        IConfiguration configuration,
        IHostEnvironment environment,
        IOptions<OmsAuthOptions> authOptions,
        ILogger<OmsJsonDatabase> logger)
    {
        _blobStorage = blobStorage;
        _logger = logger;
        _authOptions = authOptions.Value;
        _localPath = configuration["OmsDatabase:LocalPath"] is { Length: > 0 } configuredPath
            ? Path.GetFullPath(configuredPath, environment.ContentRootPath)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OMS",
                "oms-database.json");

        if (string.Equals(
                configuration["ASPNETCORE_ENVIRONMENT"],
                Environments.Production,
                StringComparison.OrdinalIgnoreCase) &&
            !_blobStorage.IsConfigured)
            throw new InvalidOperationException(
                "BLOB_READ_WRITE_TOKEN wajib diatur pada Vercel. Database lokal tidak persisten untuk deployment.");
        if (!_blobStorage.IsConfigured &&
            !string.IsNullOrWhiteSpace(configuration["OmsDatabase:LocalPath"]) &&
            Path.GetExtension(configuration["OmsDatabase:LocalPath"]) is not (".json" or ".JSON"))
            throw new InvalidOperationException("Path database OMS harus menunjuk ke file .json.");
    }

    public async Task<IList<IList<object>>> ReadAsync(string range, CancellationToken cancellationToken)
    {
        var address = ParseRange(range);
        return await InDatabaseAsync(document =>
        {
            var table = GetTable(document, address.Sheet);
            var lastRow = address.EndRow ?? table.Rows.Count + 1;
            var firstRow = Math.Max(1, address.StartRow);
            var values = new List<IList<object>>();
            for (var rowNumber = firstRow; rowNumber <= lastRow; rowNumber++)
            {
                var source = rowNumber == 1 ? table.Headers : GetDataRow(table, rowNumber - 2);
                var row = new List<object>();
                for (var column = address.StartColumn; column <= address.EndColumn; column++)
                    row.Add(column < source.Count ? source[column] : string.Empty);
                values.Add(row);
            }
            return values;
        }, write: false, cancellationToken);
    }

    public Task AppendAsync(string range, IList<object> row, CancellationToken cancellationToken) =>
        AppendRowsAsync(range, [row], cancellationToken);

    public async Task<string> ResetSuperadminPasswordAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        if (_blobStorage.IsConfigured)
            throw new InvalidOperationException(
                "Perintah reset ini hanya untuk database lokal. Hapus BLOB_READ_WRITE_TOKEN sebelum menjalankannya.");
        if (password.Length is < 12 or > 128)
            throw new InvalidOperationException("Password baru harus 12–128 karakter.");

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var backupPath = _localPath + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
        await DatabaseLock.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadAsync(cancellationToken);
            var table = GetTable(document, "Users");
            var emailColumn = table.Headers.FindIndex(header =>
                header.Equals("Email", StringComparison.OrdinalIgnoreCase));
            var roleColumn = table.Headers.FindIndex(header =>
                header.Equals("Role", StringComparison.OrdinalIgnoreCase));
            var hashColumn = table.Headers.FindIndex(header =>
                header.Equals("PasswordHash", StringComparison.OrdinalIgnoreCase));
            var saltColumn = table.Headers.FindIndex(header =>
                header.Equals("PasswordSalt", StringComparison.OrdinalIgnoreCase));
            var updatedAtColumn = table.Headers.FindIndex(header =>
                header.Equals("UpdatedAt", StringComparison.OrdinalIgnoreCase));
            var rows = table.Rows
                .Where(row => emailColumn >= 0 &&
                    emailColumn < row.Count &&
                    row[emailColumn].Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (rows.Count != 1)
                throw new InvalidOperationException(
                    $"Akun '{normalizedEmail}' tidak ditemukan atau memiliki data duplikat.");
            var user = rows[0];
            if (roleColumn < 0 || roleColumn >= user.Count ||
                !user[roleColumn].Equals("SUPERADMIN", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Akun '{normalizedEmail}' bukan akun SUPERADMIN.");
            if (hashColumn < 0 || saltColumn < 0 || updatedAtColumn < 0)
                throw new InvalidOperationException("Kolom kredensial pada tabel Users tidak lengkap.");

            Directory.CreateDirectory(Path.GetDirectoryName(_localPath)!);
            if (File.Exists(_localPath))
                File.Copy(_localPath, backupPath);

            var (hash, salt) = PasswordCredential.Create(password);
            user[hashColumn] = hash;
            user[saltColumn] = salt;
            user[updatedAtColumn] = DateTimeOffset.UtcNow.ToString("O");
            await SaveAsync(document, cancellationToken);
            return File.Exists(backupPath) ? backupPath : string.Empty;
        }
        finally
        {
            DatabaseLock.Release();
        }
    }

    public async Task UpdateEventAsync(
        string eventId,
        EventCreateRequest request,
        CancellationToken cancellationToken)
    {
        await InDatabaseAsync(document =>
        {
            var table = GetTable(document, "Events");
            var idColumn = RequireColumn(table, "EventId");
            var codeColumn = RequireColumn(table, "EventCode");
            var row = FindSingleRow(table, idColumn, eventId, "Kegiatan tidak ditemukan.");
            if (table.Rows.Any(candidate =>
                    !ReferenceEquals(candidate, row) &&
                    codeColumn < candidate.Count &&
                    candidate[codeColumn].Equals(request.EventCode, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Kode kegiatan sudah digunakan.");

            SetRowValue(table, row, "EventCode", request.EventCode);
            SetRowValue(table, row, "EventName", request.EventName);
            SetRowValue(table, row, "Description", request.Description ?? string.Empty);
            SetRowValue(table, row, "StartDate", request.StartDate ?? string.Empty);
            SetRowValue(table, row, "EndDate", request.EndDate ?? string.Empty);
            SetRowValue(table, row, "Location", request.Location ?? string.Empty);
            SetRowValue(table, row, "Status", request.Status ?? string.Empty);
            SetRowValue(table, row, "UpdatedAt", DateTimeOffset.UtcNow.ToString("O"));
            return true;
        }, write: true, cancellationToken);
    }

    public async Task<string> CreateScheduleItemAsync(
        string eventId,
        ScheduleItemRequest request,
        CancellationToken cancellationToken)
    {
        return await InDatabaseAsync(document =>
        {
            EnsureScheduleTimeAvailable(document, eventId, request.StartTime, request.EndTime, request.Status, null);
            var table = GetTable(document, "Sessions");
            var row = new string[table.Headers.Count];
            var sessionId = Guid.NewGuid().ToString("N");
            SetSeedValue(table, row, "SessionId", sessionId);
            SetSeedValue(table, row, "EventId", eventId);
            SetSeedValue(table, row, "SessionName", request.SessionName);
            SetSeedValue(table, row, "Description", request.Description ?? string.Empty);
            SetSeedValue(table, row, "StartTime", request.StartTime);
            SetSeedValue(table, row, "EndTime", request.EndTime);
            SetSeedValue(table, row, "Location", request.Location ?? string.Empty);
            SetSeedValue(table, row, "SessionType", "Agenda");
            SetSeedValue(table, row, "Status", request.Status ?? "Active");
            SetSeedValue(table, row, "CreatedAt", DateTimeOffset.UtcNow.ToString("O"));
            table.Rows.Add(row.ToList());
            return sessionId;
        }, write: true, cancellationToken);
    }

    public async Task UpdateScheduleItemAsync(
        string eventId,
        string sessionId,
        ScheduleItemRequest request,
        CancellationToken cancellationToken)
    {
        await InDatabaseAsync(document =>
        {
            EnsureScheduleTimeAvailable(document, eventId, request.StartTime, request.EndTime, request.Status, sessionId);
            var table = GetTable(document, "Sessions");
            var idColumn = RequireColumn(table, "SessionId");
            var eventColumn = RequireColumn(table, "EventId");
            var row = table.Rows.SingleOrDefault(candidate =>
                idColumn < candidate.Count &&
                eventColumn < candidate.Count &&
                candidate[idColumn].Equals(sessionId, StringComparison.Ordinal) &&
                candidate[eventColumn].Equals(eventId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Agenda tidak ditemukan pada kegiatan ini.");
            SetRowValue(table, row, "SessionName", request.SessionName);
            SetRowValue(table, row, "Description", request.Description ?? string.Empty);
            SetRowValue(table, row, "StartTime", request.StartTime);
            SetRowValue(table, row, "EndTime", request.EndTime);
            SetRowValue(table, row, "Location", request.Location ?? string.Empty);
            SetRowValue(table, row, "SessionType", "Agenda");
            SetRowValue(table, row, "Status", request.Status ?? "Active");
            return true;
        }, write: true, cancellationToken);
    }

    public async Task DeleteScheduleItemAsync(
        string eventId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await InDatabaseAsync(document =>
        {
            var table = GetTable(document, "Sessions");
            var idColumn = RequireColumn(table, "SessionId");
            var eventColumn = RequireColumn(table, "EventId");
            var row = table.Rows.SingleOrDefault(candidate =>
                idColumn < candidate.Count &&
                eventColumn < candidate.Count &&
                candidate[idColumn].Equals(sessionId, StringComparison.Ordinal) &&
                candidate[eventColumn].Equals(eventId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Agenda tidak ditemukan pada kegiatan ini.");

            var attendance = GetTable(document, "Attendance");
            var attendanceSessionColumn = RequireColumn(attendance, "SessionId");
            if (attendance.Rows.Any(candidate =>
                    attendanceSessionColumn < candidate.Count &&
                    candidate[attendanceSessionColumn].Equals(sessionId, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "Agenda ini sudah memiliki catatan kehadiran. Nonaktifkan agenda agar riwayat check-in tetap utuh.");

            table.Rows.Remove(row);
            return true;
        }, write: true, cancellationToken);
    }

    public async Task DeleteParticipantAsync(
        string eventId,
        string participantId,
        CancellationToken cancellationToken)
    {
        await InDatabaseAsync(document =>
        {
            var participants = GetTable(document, "Participants");
            var participantEventColumn = RequireColumn(participants, "EventId");
            var participantIdColumn = RequireColumn(participants, "ParticipantId");
            var participant = participants.Rows.SingleOrDefault(row =>
                participantIdColumn < row.Count &&
                participantEventColumn < row.Count &&
                row[participantIdColumn].Equals(participantId, StringComparison.Ordinal) &&
                row[participantEventColumn].Equals(eventId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Peserta tidak ditemukan pada kegiatan ini.");
            participants.Rows.Remove(participant);

            var attendance = GetTable(document, "Attendance");
            var attendanceParticipantColumn = RequireColumn(attendance, "ParticipantId");
            attendance.Rows.RemoveAll(row =>
                attendanceParticipantColumn < row.Count &&
                row[attendanceParticipantColumn].Equals(participantId, StringComparison.Ordinal));

            var audit = GetTable(document, "AuditLog");
            var auditEntityTypeColumn = RequireColumn(audit, "EntityType");
            var auditEntityIdColumn = RequireColumn(audit, "EntityId");
            audit.Rows.RemoveAll(row =>
                auditEntityTypeColumn < row.Count &&
                auditEntityIdColumn < row.Count &&
                row[auditEntityTypeColumn].Equals("Participant", StringComparison.OrdinalIgnoreCase) &&
                row[auditEntityIdColumn].Equals(participantId, StringComparison.Ordinal));
            return true;
        }, write: true, cancellationToken);
    }

    public async Task<EventDeletionSummary> DeleteEventCascadeAsync(
        string eventId,
        CancellationToken cancellationToken)
    {
        return await InDatabaseAsync(document =>
        {
            var events = GetTable(document, "Events");
            var eventIdColumn = RequireColumn(events, "EventId");
            var eventRow = FindSingleRow(events, eventIdColumn, eventId, "Kegiatan tidak ditemukan.");

            var participants = GetTable(document, "Participants");
            var participantEventColumn = RequireColumn(participants, "EventId");
            var participantIdColumn = RequireColumn(participants, "ParticipantId");
            var participantIds = participants.Rows
                .Where(row => participantEventColumn < row.Count &&
                    row[participantEventColumn].Equals(eventId, StringComparison.Ordinal))
                .Where(row => participantIdColumn < row.Count)
                .Select(row => row[participantIdColumn])
                .ToHashSet(StringComparer.Ordinal);
            var participantCount = participantIds.Count;
            participants.Rows.RemoveAll(row =>
                participantEventColumn < row.Count &&
                row[participantEventColumn].Equals(eventId, StringComparison.Ordinal));

            var attendance = GetTable(document, "Attendance");
            var attendanceEventColumn = RequireColumn(attendance, "EventId");
            var attendanceParticipantColumn = RequireColumn(attendance, "ParticipantId");
            var attendanceCount = attendance.Rows.RemoveAll(row =>
                attendanceEventColumn < row.Count &&
                row[attendanceEventColumn].Equals(eventId, StringComparison.Ordinal) ||
                attendanceParticipantColumn < row.Count &&
                participantIds.Contains(row[attendanceParticipantColumn]));

            var sessions = GetTable(document, "Sessions");
            var sessionEventColumn = RequireColumn(sessions, "EventId");
            var sessionCount = sessions.Rows.RemoveAll(row =>
                sessionEventColumn < row.Count &&
                row[sessionEventColumn].Equals(eventId, StringComparison.Ordinal));

            var audit = GetTable(document, "AuditLog");
            var auditEventColumn = RequireColumn(audit, "EventId");
            var auditCount = audit.Rows.RemoveAll(row =>
                auditEventColumn < row.Count &&
                row[auditEventColumn].Equals(eventId, StringComparison.Ordinal));
            events.Rows.Remove(eventRow);

            return new EventDeletionSummary(
                participantCount, sessionCount, attendanceCount, auditCount);
        }, write: true, cancellationToken);
    }

    public async Task AppendRowsAsync(
        string range,
        IList<IList<object>> rows,
        CancellationToken cancellationToken)
    {
        var address = ParseRange(range);
        await InDatabaseAsync(document =>
        {
            var table = GetTable(document, address.Sheet);
            foreach (var values in rows)
            {
                var row = new List<string>();
                for (var index = 0; index < address.StartColumn; index++) row.Add(string.Empty);
                foreach (var value in values) row.Add(ToCell(value));
                EnsureColumnCount(table, row.Count);
                table.Rows.Add(row);
            }
            return true;
        }, write: true, cancellationToken);
    }

    public async Task UpdateAsync(
        string range,
        IList<IList<object>> rows,
        CancellationToken cancellationToken)
    {
        var address = ParseRange(range);
        await InDatabaseAsync(document =>
        {
            var table = GetTable(document, address.Sheet);
            for (var rowOffset = 0; rowOffset < rows.Count; rowOffset++)
            {
                var targetRowNumber = address.StartRow + rowOffset;
                if (targetRowNumber == 1)
                {
                    for (var columnOffset = 0; columnOffset < rows[rowOffset].Count; columnOffset++)
                        SetHeader(table, address.StartColumn + columnOffset, ToCell(rows[rowOffset][columnOffset]));
                    continue;
                }

                var dataIndex = targetRowNumber - 2;
                while (table.Rows.Count <= dataIndex) table.Rows.Add([]);
                var target = table.Rows[dataIndex];
                for (var columnOffset = 0; columnOffset < rows[rowOffset].Count; columnOffset++)
                    SetCell(table, target, address.StartColumn + columnOffset, ToCell(rows[rowOffset][columnOffset]));
            }
            return true;
        }, write: true, cancellationToken);
    }

    public async Task UpdateManyAsync(
        IList<JsonValueRange> ranges,
        CancellationToken cancellationToken)
    {
        if (ranges.Count == 0) return;
        await InDatabaseAsync(document =>
        {
            foreach (var update in ranges)
            {
                var address = ParseRange(update.Range);
                var table = GetTable(document, address.Sheet);
                for (var rowOffset = 0; rowOffset < update.Values.Count; rowOffset++)
                {
                    var targetRowNumber = address.StartRow + rowOffset;
                    if (targetRowNumber == 1)
                    {
                        for (var columnOffset = 0; columnOffset < update.Values[rowOffset].Count; columnOffset++)
                            SetHeader(table, address.StartColumn + columnOffset, ToCell(update.Values[rowOffset][columnOffset]));
                        continue;
                    }

                    var dataIndex = targetRowNumber - 2;
                    while (table.Rows.Count <= dataIndex) table.Rows.Add([]);
                    var target = table.Rows[dataIndex];
                    for (var columnOffset = 0; columnOffset < update.Values[rowOffset].Count; columnOffset++)
                        SetCell(table, target, address.StartColumn + columnOffset, ToCell(update.Values[rowOffset][columnOffset]));
                }
            }
            return true;
        }, write: true, cancellationToken);
    }

    public async Task ImportEmployeeMasterAsync(
        EmployeeMasterImportRequest request,
        CancellationToken cancellationToken)
    {
        var headers = request.Headers.Select(item => item.Trim()).ToList();
        if (headers.Count == 0 || headers.Count > 100)
            throw new InvalidOperationException("File master karyawan harus memiliki 1 sampai 100 kolom.");
        if (request.Rows.Count > 100_000)
            throw new InvalidOperationException("File master karyawan melebihi batas 100.000 baris.");

        var normalized = headers.Select(NormalizeHeader).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!normalized.Overlaps(["NIK", "EMPLOYEENUMBER", "NOMORPEGAWAI"]) ||
            !normalized.Overlaps(["EMPLOYEENAME", "FULLNAME", "NAMA"]))
            throw new InvalidOperationException("Header master harus memuat kolom NIK dan nama karyawan.");

        await InDatabaseAsync(document =>
        {
            var table = GetTable(document, "List All EMP LENGKAP");
            table.Headers = headers;
            table.Rows = request.Rows
                .Select(source => headers.Select((_, index) =>
                    index < source.Count ? source[index].Trim() : string.Empty).ToList())
                .ToList();
            return true;
        }, write: true, cancellationToken);
    }

    private async Task<T> InDatabaseAsync<T>(
        Func<DatabaseDocument, T> operation,
        bool write,
        CancellationToken cancellationToken)
    {
        await DatabaseLock.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadAsync(cancellationToken);
            var result = operation(document);
            if (write) await SaveAsync(document, cancellationToken);
            return result;
        }
        finally
        {
            DatabaseLock.Release();
        }
    }

    private async Task<DatabaseDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (_blobStorage.IsConfigured)
        {
            var blob = await _blobStorage.ReadTextAsync(DatabasePath, cancellationToken);
            if (blob is null)
            {
                _blobETag = null;
                _document = CreateEmptyDocument();
                await SeedBootstrapUserAsync(_document, cancellationToken);
                await SaveAsync(_document, cancellationToken);
                return _document;
            }
            _blobETag = blob.ETag;
            _document = JsonSerializer.Deserialize<DatabaseDocument>(blob.Content, JsonOptions)
                ?? throw new InvalidOperationException("File database JSON di Vercel Blob kosong atau formatnya tidak valid.");
        }
        else
        {
            if (_document is not null) return _document;
            if (File.Exists(_localPath))
            {
                await using var stream = File.OpenRead(_localPath);
                _document = await JsonSerializer.DeserializeAsync<DatabaseDocument>(stream, JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("File database JSON lokal kosong atau formatnya tidak valid.");
            }
            else
            {
                _document = CreateEmptyDocument();
                await SeedBootstrapUserAsync(_document, cancellationToken);
                await SaveAsync(_document, cancellationToken);
            }
        }

        MigrateDocument(_document);
        if (_document.Tables["Users"].Rows.Count == 0 &&
            !string.IsNullOrWhiteSpace(_authOptions.BootstrapEmail) &&
            !string.IsNullOrWhiteSpace(_authOptions.BootstrapPassword))
        {
            await SeedBootstrapUserAsync(_document, cancellationToken);
            await SaveAsync(_document, cancellationToken);
        }
        return _document;
    }

    private async Task SaveAsync(DatabaseDocument document, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (_blobStorage.IsConfigured)
        {
            _blobETag = await _blobStorage.WriteTextAsync(DatabasePath, json, _blobETag, cancellationToken);
        }
        else
        {
            var directory = Path.GetDirectoryName(_localPath)
                ?? throw new InvalidOperationException("Path database JSON lokal tidak valid.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _localPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, _localPath, overwrite: true);
        }
        _document = document;
    }

    private async Task SeedBootstrapUserAsync(DatabaseDocument document, CancellationToken cancellationToken)
    {
        var email = _authOptions.BootstrapEmail.Trim();
        var password = _authOptions.BootstrapPassword;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                    Environments.Production,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "OMS_AUTH__BOOTSTRAP_EMAIL dan OMS_AUTH__BOOTSTRAP_PASSWORD diperlukan untuk membuat superadmin pertama.");
            return;
        }
        if (password.Length < 12)
            throw new InvalidOperationException("Password superadmin awal harus minimal 12 karakter.");
        cancellationToken.ThrowIfCancellationRequested();

        var table = document.Tables["Users"];
        var (hash, salt) = PasswordCredential.Create(password);
        var row = new string[table.Headers.Count];
        SetSeedValue(table, row, "UserId", Guid.NewGuid().ToString("N"));
        SetSeedValue(table, row, "Email", email.ToLowerInvariant());
        SetSeedValue(table, row, "FullName", "PANITIA");
        SetSeedValue(table, row, "Role", "SUPERADMIN");
        SetSeedValue(table, row, "IsActive", "TRUE");
        SetSeedValue(table, row, "CreatedAt", DateTimeOffset.UtcNow.ToString("O"));
        SetSeedValue(table, row, "UpdatedAt", DateTimeOffset.UtcNow.ToString("O"));
        SetSeedValue(table, row, "PasswordHash", hash);
        SetSeedValue(table, row, "PasswordSalt", salt);
        table.Rows.Add(row.ToList());
        _logger.LogInformation("Initialized OMS JSON database with the configured superadmin user.");
    }

    private static DatabaseDocument CreateEmptyDocument() =>
        new()
        {
            Version = 1,
            Tables = DefaultHeaders.ToDictionary(
                pair => pair.Key,
                pair => new JsonTable { Headers = pair.Value.ToList(), Rows = [] },
                StringComparer.OrdinalIgnoreCase)
        };

    private static void MigrateDocument(DatabaseDocument document)
    {
        foreach (var pair in DefaultHeaders)
        {
            if (!document.Tables.TryGetValue(pair.Key, out var table))
            {
                document.Tables[pair.Key] = new JsonTable { Headers = pair.Value.ToList(), Rows = [] };
                continue;
            }

            foreach (var header in pair.Value)
                if (!table.Headers.Contains(header, StringComparer.OrdinalIgnoreCase))
                    table.Headers.Add(header);
            EnsureColumnCount(table, table.Headers.Count);
        }
    }

    private static JsonTable GetTable(DatabaseDocument document, string name) =>
        document.Tables.TryGetValue(name, out var table)
            ? table
            : throw new InvalidOperationException($"Tabel JSON '{name}' tidak dikenal.");

    private static List<string> GetDataRow(JsonTable table, int index) =>
        index >= 0 && index < table.Rows.Count ? table.Rows[index] : [];

    private static void SetCell(JsonTable table, List<string> row, int column, string value)
    {
        EnsureColumnCount(table, column + 1);
        while (row.Count <= column) row.Add(string.Empty);
        row[column] = value;
    }

    private static void SetHeader(JsonTable table, int column, string value)
    {
        EnsureColumnCount(table, column + 1);
        table.Headers[column] = value;
    }

    private static void EnsureColumnCount(JsonTable table, int count)
    {
        while (table.Headers.Count < count) table.Headers.Add(string.Empty);
        foreach (var row in table.Rows)
            while (row.Count < table.Headers.Count) row.Add(string.Empty);
    }

    private static void SetSeedValue(JsonTable table, string[] row, string name, string value)
    {
        var index = table.Headers.FindIndex(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) row[index] = value;
    }

    private static int RequireColumn(JsonTable table, string name)
    {
        var index = table.Headers.FindIndex(header =>
            header.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? index
            : throw new InvalidOperationException($"Kolom '{name}' tidak ditemukan pada tabel database.");
    }

    private static List<string> FindSingleRow(
        JsonTable table,
        int idColumn,
        string id,
        string notFoundMessage)
    {
        var matches = table.Rows.Where(row =>
            idColumn < row.Count &&
            row[idColumn].Equals(id, StringComparison.Ordinal)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                matches.Count == 0 ? notFoundMessage : "ID memiliki data duplikat pada database.");
    }

    private static void SetRowValue(JsonTable table, List<string> row, string name, string value)
    {
        var column = RequireColumn(table, name);
        while (row.Count <= column) row.Add(string.Empty);
        row[column] = value;
    }

    private static void EnsureScheduleTimeAvailable(
        DatabaseDocument document,
        string eventId,
        string startTime,
        string endTime,
        string? requestStatus,
        string? excludedSessionId)
    {
        var start = ParseScheduleTime(startTime);
        var end = ParseScheduleTime(endTime);
        if (end <= start)
            throw new InvalidOperationException("Waktu selesai harus setelah waktu mulai.");
        if (string.Equals(requestStatus, "Inactive", StringComparison.OrdinalIgnoreCase))
            return;

        var table = GetTable(document, "Sessions");
        var eventColumn = RequireColumn(table, "EventId");
        var idColumn = RequireColumn(table, "SessionId");
        var startColumn = RequireColumn(table, "StartTime");
        var endColumn = RequireColumn(table, "EndTime");
        var statusColumn = RequireColumn(table, "Status");
        foreach (var row in table.Rows)
        {
            if (eventColumn >= row.Count || idColumn >= row.Count ||
                startColumn >= row.Count || endColumn >= row.Count || statusColumn >= row.Count ||
                !row[eventColumn].Equals(eventId, StringComparison.Ordinal) ||
                !row[statusColumn].Equals("Active", StringComparison.OrdinalIgnoreCase) ||
                row[idColumn].Equals(excludedSessionId, StringComparison.Ordinal))
                continue;

            if (!TryParseScheduleTime(row[startColumn], out var existingStart) ||
                !TryParseScheduleTime(row[endColumn], out var existingEnd))
                continue;
            if (start < existingEnd && existingStart < end)
                throw new InvalidOperationException("Waktu agenda bertumpang tindih dengan agenda lain pada kegiatan ini.");
        }
    }

    private static DateTime ParseScheduleTime(string value)
    {
        if (!TryParseScheduleTime(value, out var parsed))
            throw new InvalidOperationException("Format waktu agenda tidak valid.");
        return parsed;
    }

    private static bool TryParseScheduleTime(string value, out DateTime parsed) =>
        DateTime.TryParseExact(
            value,
            ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out parsed);

    private static string ToCell(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static string NormalizeHeader(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static SheetRange ParseRange(string range)
    {
        var match = Regex.Match(
            range,
            @"^'(?<sheet>(?:[^']|'')+)'!(?<start>[A-Z]+)(?<row>\d*)?(?::(?<end>[A-Z]+)(?<endrow>\d*)?)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidOperationException($"Rentang database tidak valid: {range}");

        var startColumn = ParseColumn(match.Groups["start"].Value);
        var endColumn = match.Groups["end"].Success
            ? ParseColumn(match.Groups["end"].Value)
            : startColumn;
        var startRow = match.Groups["row"].Success && int.TryParse(match.Groups["row"].Value, out var parsedStart)
            ? parsedStart
            : 1;
        int? endRow = match.Groups["endrow"].Success && int.TryParse(match.Groups["endrow"].Value, out var parsedEnd)
            ? parsedEnd
            : null;
        return new SheetRange(
            match.Groups["sheet"].Value.Replace("''", "'", StringComparison.Ordinal),
            startColumn,
            endColumn,
            startRow,
            endRow);
    }

    private static int ParseColumn(string value)
    {
        var column = 0;
        foreach (var character in value.ToUpperInvariant())
            column = checked(column * 26 + character - 'A' + 1);
        return column - 1;
    }

    private sealed class DatabaseDocument
    {
        public DatabaseDocument() { }
        public int Version { get; set; }
        public Dictionary<string, JsonTable> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class JsonTable
    {
        public JsonTable() { }
        public List<string> Headers { get; set; } = [];
        public List<List<string>> Rows { get; set; } = [];
    }

    private sealed record SheetRange(string Sheet, int StartColumn, int EndColumn, int StartRow, int? EndRow);
}
