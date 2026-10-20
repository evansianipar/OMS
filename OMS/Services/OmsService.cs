using Microsoft.Extensions.Options;
using OMS.Models;
using System.Security.Cryptography;
using System.Text;

namespace OMS.Services;

public sealed class OmsService(
    GoogleSheetsService sheets,
    UserDirectoryService users,
    AppsScriptInvitationService invitations,
    ILogger<OmsService> logger)
{
    private static readonly SemaphoreSlim AttendanceLock = new(1, 1);

    public async Task<OmsUser> RequireRoleAsync(
        string email,
        IEnumerable<string> allowedRoles,
        CancellationToken cancellationToken)
    {
        var user = await users.FindActiveUserAsync(email, cancellationToken);
        if (user is null) throw new UnauthorizedAccessException("Akun tidak aktif atau tidak terdaftar pada sheet Users.");
        if (!allowedRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Role akun tidak memiliki akses ke fungsi ini.");
        return user;
    }

    public async Task<IReadOnlyList<OmsEvent>> ListEventsAsync(CancellationToken cancellationToken)
    {
        var rows = await sheets.ReadAsync("'Events'!A1:Z", cancellationToken);
        return ToRecords(rows).Select(row => new OmsEvent(
            Get(row, "EventId"), Get(row, "EventCode"), Get(row, "EventName"),
            Get(row, "Description"), Get(row, "StartDate"), Get(row, "EndDate"),
            Get(row, "Location"), Get(row, "Status"))).ToList();
    }

    public async Task<DashboardSummary> GetDashboardAsync(string eventId, CancellationToken cancellationToken)
    {
        var @event = await GetEventAsync(eventId, cancellationToken);
        var participants = (await GetParticipantsAsync(eventId, cancellationToken))
            .Where(item => item.IsActive).ToList();
        var attendanceRows = ToRecords(await sheets.ReadAsync("'Attendance'!A1:Z", cancellationToken))
            .Where(row => Get(row, "EventId") == eventId).ToList();
        var participantById = participants.ToDictionary(item => item.ParticipantId, StringComparer.Ordinal);
        var attendedIds = attendanceRows.Select(row => Get(row, "ParticipantId"))
            .Where(participantById.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var departments = participants
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Department) ? "Belum diisi" : item.Department)
            .Select(group => new DepartmentAttendance(
                group.Key, group.Count(), group.Count(item => attendedIds.Contains(item.ParticipantId))))
            .OrderByDescending(item => item.Total)
            .ToList();
        var recent = attendanceRows
            .OrderByDescending(row => ParseDate(Get(row, "AttendanceTime")))
            .Take(10)
            .Select(row =>
            {
                participantById.TryGetValue(Get(row, "ParticipantId"), out var participant);
                return new RecentCheckIn(
                    participant?.FullName ?? "Data peserta tidak ditemukan",
                    participant?.EmployeeNumber ?? string.Empty,
                    participant?.Department ?? string.Empty,
                    Get(row, "AttendanceTime"),
                    Get(row, "CheckInMethod"));
            }).ToList();
        var absent = participants.Where(item => !attendedIds.Contains(item.ParticipantId)).Take(30).ToList();
        var attended = participants.Count(item => attendedIds.Contains(item.ParticipantId));
        return new DashboardSummary(
            @event, participants.Count, attended, participants.Count - attended,
            participants.Count == 0 ? 0 : Math.Round(attended * 1000d / participants.Count) / 10d,
            departments, recent, absent);
    }

    public async Task<IReadOnlyList<OmsParticipant>> SearchParticipantsAsync(
        string eventId,
        string query,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var term = query.Trim();
        if (term.Length < 2) return [];
        return (await GetParticipantsAsync(eventId, cancellationToken))
            .Where(item => item.IsActive &&
                (item.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 item.EmployeeNumber.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(10)
            .ToList();
    }

    public async Task<string> CreateEventAsync(
        EventCreateRequest request,
        string actorEmail,
        CancellationToken cancellationToken)
    {
        var code = RequiredText(request.EventCode, 40, "Kode kegiatan wajib diisi.");
        var name = RequiredText(request.EventName, 120, "Nama kegiatan wajib diisi.");
        var status = string.IsNullOrWhiteSpace(request.Status) ? "Draft" : request.Status.Trim();
        if (!new[] { "Draft", "Published", "Ongoing", "Completed", "Cancelled" }
            .Contains(status, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Status kegiatan tidak valid.");

        var existing = await ListEventsAsync(cancellationToken);
        if (existing.Any(item => item.EventCode.Equals(code, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Kode kegiatan sudah digunakan.");
        var at = DateTimeOffset.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString();
        await sheets.AppendAsync("'Events'!A:K",
        [
            id, code, name, SafeText(request.Description, 1000), request.StartDate ?? string.Empty,
            request.EndDate ?? string.Empty, SafeText(request.Location, 200), status, at, actorEmail, at
        ], cancellationToken);
        await AuditAsync(id, actorEmail, "CREATE", "Event", id, "Kegiatan dibuat.", cancellationToken);
        return id;
    }

    public async Task<ImportSummary> ImportEmployeeMasterAsync(
        string eventId,
        string actorEmail,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            var master = await sheets.ReadAsync("'List All EMP LENGKAP'!A1:Z", cancellationToken);
            if (master.Count < 2) throw new InvalidOperationException("Sheet master karyawan tidak ditemukan atau kosong.");
            var headers = master[0].Select(value => NormalizeHeader(Convert.ToString(value))).ToList();
            var employeeIndex = FindHeader(headers, "NIK", "EMPLOYEENUMBER", "NOMORPEGAWAI");
            var nameIndex = FindHeader(headers, "EMPLOYEENAME", "FULLNAME", "NAMA");
            var departmentIndex = FindHeader(headers, "ORGNAME", "DEPARTMENT", "DEPARTEMEN");
            var phoneIndex = FindHeader(headers, "PHONENUMBER", "PHONE", "NOMORHP");
            var emailIndex = FindHeader(headers, "EMAIL", "EMAILADDRESS");
            if (employeeIndex < 0 || nameIndex < 0)
                throw new InvalidOperationException("Kolom wajib NIK dan EMPLOYEE_NAME tidak ditemukan pada sheet master.");

            var participantsRows = await sheets.ReadAsync("'Participants'!A1:K", cancellationToken);
            var existing = ToRecords(participantsRows)
                .Where(row => Get(row, "EventId") == eventId)
                .Select(row => Get(row, "EmployeeNumber"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seenSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var additions = new List<IList<object>>();
            var invalid = 0;
            var duplicateSource = 0;

            foreach (var row in master.Skip(1))
            {
                var employeeNumber = Cell(row, employeeIndex).Trim();
                var fullName = Cell(row, nameIndex).Trim();
                if (string.IsNullOrWhiteSpace(employeeNumber) || string.IsNullOrWhiteSpace(fullName))
                {
                    if (row.Any(value => !string.IsNullOrWhiteSpace(Convert.ToString(value)))) invalid++;
                    continue;
                }
                if (existing.Contains(employeeNumber)) continue;
                if (!seenSource.Add(employeeNumber))
                {
                    duplicateSource++;
                    continue;
                }

                var at = DateTimeOffset.UtcNow.ToString("O");
                additions.Add(new List<object>
                {
                    Guid.NewGuid().ToString(), eventId, employeeNumber, fullName,
                    Cell(row, departmentIndex).Trim(), Cell(row, phoneIndex).Trim(),
                    Cell(row, emailIndex).Trim().ToLowerInvariant(), HashToken(NewQrToken()),
                    true, at, at
                });
            }

            if (additions.Count > 0)
                await sheets.AppendRowsAsync("'Participants'!A:K", additions, cancellationToken);
            await AuditAsync(eventId, actorEmail, "IMPORT", "Participant", string.Empty,
                $"Impor {additions.Count} peserta; baris invalid {invalid}; duplikat sumber {duplicateSource}.",
                cancellationToken);
            return new ImportSummary(additions.Count, existing.Count, invalid, duplicateSource);
        }
        finally
        {
            AttendanceLock.Release();
        }
    }

    public async Task<ParticipantPage> ListParticipantsAsync(
        string eventId,
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var participants = (await GetParticipantsAsync(eventId, cancellationToken)).AsEnumerable();
        var term = query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            participants = participants.Where(item =>
                item.EmployeeNumber.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Department.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        var results = participants.OrderBy(item => item.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        return new ParticipantPage(results.Count, page, pageSize, results.Skip((page - 1) * pageSize).Take(pageSize).ToList());
    }

    public async Task<QrIssueResponse> IssueQrAsync(
        string eventId,
        string participantId,
        string actorEmail,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            var rows = await sheets.ReadAsync("'Participants'!A1:K", cancellationToken);
            if (rows.Count < 2) throw new InvalidOperationException("Peserta tidak ditemukan.");
            var headers = rows[0].Select(value => Convert.ToString(value)?.Trim() ?? string.Empty).ToList();
            var idColumn = headers.IndexOf("ParticipantId");
            var eventColumn = headers.IndexOf("EventId");
            var activeColumn = headers.IndexOf("IsActive");
            var hashColumn = headers.IndexOf("QrTokenHash");
            var updatedColumn = headers.IndexOf("UpdatedAt");
            var employeeColumn = headers.IndexOf("EmployeeNumber");
            var nameColumn = headers.IndexOf("FullName");
            if (new[] { idColumn, eventColumn, activeColumn, hashColumn, updatedColumn }.Any(index => index < 0))
                throw new InvalidOperationException("Header sheet Participants tidak sesuai dengan konfigurasi OMS.");

            for (var index = 1; index < rows.Count; index++)
            {
                var row = rows[index];
                if (Cell(row, idColumn) != participantId || Cell(row, eventColumn) != eventId) continue;
                if (!bool.TryParse(Cell(row, activeColumn), out var active) || !active)
                    throw new InvalidOperationException("Peserta tidak aktif.");
                var token = NewQrToken();
                var sheetRow = index + 1;
                var now = DateTimeOffset.UtcNow.ToString("O");
                await sheets.UpdateManyAsync(
                [
                    new Google.Apis.Sheets.v4.Data.ValueRange
                    {
                        Range = $"'Participants'!H{sheetRow}:H{sheetRow}",
                        Values = new List<IList<object>> { new List<object> { HashToken(token) } }
                    },
                    new Google.Apis.Sheets.v4.Data.ValueRange
                    {
                        Range = $"'Participants'!K{sheetRow}:K{sheetRow}",
                        Values = new List<IList<object>> { new List<object> { now } }
                    }
                ], cancellationToken);
                await AuditAsync(eventId, actorEmail, "REGENERATE_QR", "Participant", participantId,
                    "Token QR diterbitkan ulang.", cancellationToken);
                return new QrIssueResponse(token, Cell(row, employeeColumn), Cell(row, nameColumn));
            }
            throw new InvalidOperationException("Peserta tidak ditemukan pada kegiatan ini.");
        }
        finally
        {
            AttendanceLock.Release();
        }
    }

    public async Task<string> ExportAttendanceCsvAsync(string eventId, CancellationToken cancellationToken)
    {
        var @event = await GetEventAsync(eventId, cancellationToken);
        var participants = await GetParticipantsAsync(eventId, cancellationToken);
        var attendance = ToRecords(await sheets.ReadAsync("'Attendance'!A1:Z", cancellationToken))
            .Where(row => Get(row, "EventId") == eventId)
            .GroupBy(row => Get(row, "ParticipantId"), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(row => ParseDate(Get(row, "AttendanceTime"))).First(),
                StringComparer.Ordinal);

        var lines = new List<string>
        {
            CsvRow(["Kegiatan", @event.EventName]),
            CsvRow(["Waktu ekspor UTC", DateTimeOffset.UtcNow.ToString("O")]),
            string.Empty,
            CsvRow(["Nomor Pegawai", "Nama", "Departemen", "Status Kehadiran", "Waktu Check-in", "Metode"])
        };
        foreach (var participant in participants)
        {
            attendance.TryGetValue(participant.ParticipantId, out var row);
            lines.Add(CsvRow([
                participant.EmployeeNumber, participant.FullName, participant.Department,
                row is null ? "Belum hadir" : "Hadir",
                row is null ? string.Empty : Get(row, "AttendanceTime"),
                row is null ? string.Empty : Get(row, "CheckInMethod")
            ]));
        }
        return "\uFEFF" + string.Join("\r\n", lines);
    }

    public async Task<AttendanceReportPage> GetAttendanceReportAsync(
        string eventId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var participants = await GetParticipantsAsync(eventId, cancellationToken);
        var participantIds = participants.Select(item => item.ParticipantId).ToHashSet(StringComparer.Ordinal);
        var attendance = ToRecords(await sheets.ReadAsync("'Attendance'!A1:Z", cancellationToken))
            .Where(row => Get(row, "EventId") == eventId && participantIds.Contains(Get(row, "ParticipantId")))
            .GroupBy(row => Get(row, "ParticipantId"), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(row => ParseDate(Get(row, "AttendanceTime"))).First(),
                StringComparer.Ordinal);
        var report = participants
            .OrderBy(item => item.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(participant =>
            {
                attendance.TryGetValue(participant.ParticipantId, out var row);
                return new AttendanceReportRow(
                    participant.EmployeeNumber,
                    participant.FullName,
                    participant.Department,
                    row is null ? "Belum hadir" : "Hadir",
                    row is null ? string.Empty : Get(row, "AttendanceTime"),
                    row is null ? string.Empty : Get(row, "CheckInMethod"));
            }).ToList();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        return new AttendanceReportPage(
            report.Count, page, pageSize, report.Skip((page - 1) * pageSize).Take(pageSize).ToList());
    }

    public async Task<InvitationBatchResult> SendInvitationBatchAsync(
        string eventId,
        string actorEmail,
        int offset,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var outing = await GetEventAsync(eventId, cancellationToken);
        var participants = (await GetParticipantsAsync(eventId, cancellationToken))
            .Where(item => item.IsActive)
            .ToList();
        var candidates = new List<OmsParticipant>();
        foreach (var participant in participants)
        {
            if (System.Net.Mail.MailAddress.TryCreate(participant.Email, out _))
                candidates.Add(participant);
        }

        offset = Math.Clamp(offset, 0, candidates.Count);
        batchSize = Math.Clamp(batchSize, 1, 20);
        var batch = candidates.Skip(offset).Take(batchSize).ToList();
        var sent = 0;
        var errors = new List<string>();
        foreach (var participant in batch)
        {
            try
            {
                var qr = await IssueQrAsync(eventId, participant.ParticipantId, actorEmail, cancellationToken);
                await invitations.SendAsync(
                    eventId,
                    participant with { Email = participant.Email.Trim() },
                    qr.Token,
                    cancellationToken);
                await AuditAsync(eventId, actorEmail, "SEND_INVITATION", "Participant", participant.ParticipantId,
                    "Undangan QR dikirim.", cancellationToken);
                sent++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to send event invitation to participant {ParticipantId}.",
                    participant.ParticipantId);
                errors.Add($"{participant.EmployeeNumber}: {exception.Message}");
            }
        }

        return new InvitationBatchResult(
            sent,
            errors.Count,
            offset == 0 ? participants.Count - candidates.Count : 0,
            batch.Count,
            candidates.Count,
            errors.Take(10).ToList());
    }

    public async Task<IReadOnlyList<OmsSession>> ListSessionsAsync(string eventId, CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var rows = await sheets.ReadAsync("'Sessions'!A1:Z", cancellationToken);
        return ToRecords(rows)
            .Where(row => Get(row, "EventId") == eventId)
            .Select(row => new OmsSession(Get(row, "SessionId"), Get(row, "SessionName"), Get(row, "Status")))
            .ToList();
    }

    public async Task<ScanResponse> CheckInQrAsync(
        string eventId,
        string? sessionId,
        string token,
        string checkedBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
            return new ScanResponse("INVALID_QR", "QR TIDAK VALID.");

        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            var @event = await GetEventAsync(eventId, cancellationToken);
            if (new[] { "Draft", "Cancelled", "Completed" }.Contains(@event.Status, StringComparer.OrdinalIgnoreCase))
                return new ScanResponse("EVENT_INACTIVE", "CHECK-IN TIDAK TERSEDIA UNTUK KEGIATAN INI.");
            var sessionError = await ValidateSessionAsync(eventId, sessionId, cancellationToken);
            if (sessionError is not null) return new ScanResponse("SESSION_INACTIVE", sessionError);

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            var participants = await GetParticipantsAsync(eventId, cancellationToken);
            var participant = participants.FirstOrDefault(item => item.TokenHash.Equals(hash, StringComparison.Ordinal));
            if (participant is null) return new ScanResponse("INVALID_QR", "QR TIDAK VALID.");
            if (!participant.IsActive) return new ScanResponse("PARTICIPANT_INACTIVE", "PESERTA TIDAK AKTIF.");
            return await SaveAttendanceAsync(eventId, sessionId, participant, "QR", checkedBy, cancellationToken);
        }
        finally
        {
            AttendanceLock.Release();
        }
    }

    public async Task<ScanResponse> CheckInManualAsync(
        string eventId,
        string? sessionId,
        string participantId,
        string checkedBy,
        CancellationToken cancellationToken)
    {
        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            var @event = await GetEventAsync(eventId, cancellationToken);
            if (new[] { "Draft", "Cancelled", "Completed" }.Contains(@event.Status, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Check-in tidak tersedia untuk status kegiatan " + @event.Status + ".");
            var sessionError = await ValidateSessionAsync(eventId, sessionId, cancellationToken);
            if (sessionError is not null) throw new InvalidOperationException(sessionError);

            var participant = (await GetParticipantsAsync(eventId, cancellationToken))
                .FirstOrDefault(item => item.ParticipantId == participantId);
            if (participant is null) throw new InvalidOperationException("Peserta tidak terdaftar pada kegiatan ini.");
            if (!participant.IsActive) throw new InvalidOperationException("Peserta tidak aktif.");
            return await SaveAttendanceAsync(eventId, sessionId, participant, "MANUAL", checkedBy, cancellationToken);
        }
        finally
        {
            AttendanceLock.Release();
        }
    }

    private async Task<ScanResponse> SaveAttendanceAsync(
        string eventId,
        string? sessionId,
        OmsParticipant participant,
        string method,
        string checkedBy,
        CancellationToken cancellationToken)
    {
        var rows = ToRecords(await sheets.ReadAsync("'Attendance'!A1:Z", cancellationToken));
        var existing = rows.FirstOrDefault(row =>
            Get(row, "EventId") == eventId &&
            Get(row, "ParticipantId") == participant.ParticipantId &&
            Get(row, "SessionId") == (sessionId ?? string.Empty));
        if (existing is not null)
            return new ScanResponse("DUPLICATE", "SUDAH CHECK-IN.", participant, Get(existing, "AttendanceTime"));

        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString();
        await sheets.AppendAsync("'Attendance'!A:I",
        [
            id, eventId, participant.ParticipantId, sessionId ?? string.Empty,
            timestamp, method, checkedBy, string.Empty, timestamp
        ], cancellationToken);

        try
        {
            await sheets.AppendAsync("'AuditLog'!A:H",
            [
                Guid.NewGuid().ToString(), eventId, checkedBy, "CHECK_IN", "Attendance",
                id, timestamp, "Check-in dengan metode " + method + "."
            ], cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Check-in {AttendanceId} tersimpan tetapi audit log gagal ditulis.", id);
        }

        return new ScanResponse("SUCCESS", "BERHASIL CHECK-IN.", participant, timestamp);
    }

    private async Task<string?> ValidateSessionAsync(
        string eventId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var sessions = await ListSessionsAsync(eventId, cancellationToken);
        return sessions.Any(session => session.SessionId == sessionId && session.Status == "Active")
            ? null
            : "SESI TIDAK AKTIF.";
    }

    private async Task<OmsEvent> GetEventAsync(string eventId, CancellationToken cancellationToken)
    {
        var @event = (await ListEventsAsync(cancellationToken)).FirstOrDefault(item => item.EventId == eventId);
        return @event ?? throw new InvalidOperationException("Kegiatan tidak ditemukan.");
    }

    private async Task<List<OmsParticipant>> GetParticipantsAsync(string eventId, CancellationToken cancellationToken)
    {
        var rows = ToRecords(await sheets.ReadAsync("'Participants'!A1:Z", cancellationToken));
        return rows.Where(row => Get(row, "EventId") == eventId)
            .Select(row => new OmsParticipant(
                Get(row, "ParticipantId"), Get(row, "EventId"), Get(row, "EmployeeNumber"),
                Get(row, "FullName"), Get(row, "Department"), Get(row, "Email"),
                bool.TryParse(Get(row, "IsActive"), out var active) && active,
                Get(row, "QrTokenHash")))
            .ToList();
    }

    private static IEnumerable<Dictionary<string, string>> ToRecords(IList<IList<object>> rows)
    {
        if (rows.Count < 2) return [];
        var headers = rows[0].Select(Convert.ToString).Select(value => value?.Trim() ?? string.Empty).ToList();
        return rows.Skip(1).Where(row => row.Any(value => !string.IsNullOrWhiteSpace(Convert.ToString(value))))
            .Select(row => headers.Select((header, index) => (header, index))
                .Where(item => !string.IsNullOrWhiteSpace(item.header))
                .ToDictionary(item => item.header, item => item.index < row.Count ? Convert.ToString(row[item.index]) ?? string.Empty : string.Empty,
                    StringComparer.Ordinal));
    }

    private async Task AuditAsync(
        string eventId,
        string actorEmail,
        string action,
        string entityType,
        string entityId,
        string details,
        CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            await sheets.AppendAsync("'AuditLog'!A:H",
                [Guid.NewGuid().ToString(), eventId, actorEmail, action, entityType, entityId, at, details],
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Audit record failed for {Action} on {EntityType} {EntityId}.",
                action, entityType, entityId);
        }
    }

    private static string NewQrToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string RequiredText(string? value, int maxLength, string error)
    {
        var text = SafeText(value, maxLength);
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidOperationException(error) : text;
    }

    private static string SafeText(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length > maxLength ? throw new InvalidOperationException($"Input terlalu panjang (maksimal {maxLength} karakter).") : text;
    }

    private static string NormalizeHeader(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static int FindHeader(IReadOnlyList<string> headers, params string[] names)
    {
        foreach (var name in names)
        {
            var index = headers.ToList().IndexOf(NormalizeHeader(name));
            if (index >= 0) return index;
        }
        return -1;
    }

    private static string Cell(IList<object> row, int index) =>
        index >= 0 && index < row.Count ? Convert.ToString(row[index]) ?? string.Empty : string.Empty;

    private static string CsvRow(IEnumerable<string> fields) =>
        string.Join(",", fields.Select(CsvCell));

    private static string CsvCell(string? value)
    {
        var text = value ?? string.Empty;
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] is '=' or '+' or '-' or '@'))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var date) ? date : DateTimeOffset.MinValue;
}
