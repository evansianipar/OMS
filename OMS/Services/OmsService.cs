using Microsoft.Extensions.Options;
using OMS.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OMS.Services;

public sealed class OmsService(
    OmsJsonDatabase sheets,
    UserDirectoryService users,
    GmailSmtpInvitationService invitations,
    ILogger<OmsService> logger)
{
    private static readonly SemaphoreSlim AttendanceLock = new(1, 1);
    private static readonly string[] AttendanceBaseHeaders =
        ["AttendanceId", "EventId", "ParticipantId", "SessionId", "AttendanceTime", "CheckInMethod", "CheckedBy"];
    private static readonly string[] AttendanceCheckoutHeaders =
        ["CheckOutTime", "CheckOutMethod", "CheckedOutBy"];
    private sealed record EmployeeInvitationInfo(string Camp, string Transport, string Vehicle);

    public async Task<OmsUser> RequireRoleAsync(
        string email,
        IEnumerable<string> allowedRoles,
        CancellationToken cancellationToken)
    {
        var user = await users.FindActiveUserAsync(email, cancellationToken);
        if (user is null) throw new UnauthorizedAccessException("Akun tidak aktif atau tidak terdaftar pada database OMS.");
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

    public async Task UpdateEventAsync(
        string eventId,
        EventCreateRequest request,
        string actorEmail,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var code = RequiredText(request.EventCode, 40, "Kode kegiatan wajib diisi.");
        var name = RequiredText(request.EventName, 120, "Nama kegiatan wajib diisi.");
        var status = string.IsNullOrWhiteSpace(request.Status) ? "Draft" : request.Status.Trim();
        if (!new[] { "Draft", "Published", "Ongoing", "Completed", "Cancelled" }
            .Contains(status, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Status kegiatan tidak valid.");
        if (!string.IsNullOrWhiteSpace(request.StartDate) &&
            !string.IsNullOrWhiteSpace(request.EndDate) &&
            DateOnly.TryParse(request.StartDate, out var startDate) &&
            DateOnly.TryParse(request.EndDate, out var endDate) &&
            endDate < startDate)
            throw new InvalidOperationException("Tanggal selesai tidak boleh sebelum tanggal mulai.");

        var normalizedRequest = request with
        {
            EventCode = code,
            EventName = name,
            Description = SafeText(request.Description, 1000),
            Location = SafeText(request.Location, 200),
            Status = status
        };
        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            await sheets.UpdateEventAsync(eventId, normalizedRequest, cancellationToken);
        }
        finally
        {
            AttendanceLock.Release();
        }
        await AuditAsync(eventId, actorEmail, "UPDATE", "Event", eventId,
            "Kegiatan diperbarui.", cancellationToken);
    }

    public async Task<EventDeletionSummary> DeleteEventAsync(
        string eventId,
        CancellationToken cancellationToken)
    {
        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            await GetEventAsync(eventId, cancellationToken);
            return await sheets.DeleteEventCascadeAsync(eventId, cancellationToken);
        }
        finally
        {
            AttendanceLock.Release();
        }
    }

    public async Task DeleteParticipantAsync(
        string eventId,
        string participantId,
        CancellationToken cancellationToken)
    {
        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            await GetEventAsync(eventId, cancellationToken);
            await sheets.DeleteParticipantAsync(eventId, participantId, cancellationToken);
        }
        finally
        {
            AttendanceLock.Release();
        }
    }

    public async Task<string> SaveParticipantAsync(
        string eventId,
        string? participantId,
        ParticipantRequest request,
        string actorEmail,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            var savedId = await sheets.SaveParticipantAsync(eventId, participantId, request, cancellationToken);
            await AuditAsync(eventId, actorEmail,
                string.IsNullOrWhiteSpace(participantId) ? "CREATE" : "UPDATE",
                "Participant", savedId,
                $"Data peserta {request.EmployeeNumber.Trim()} disimpan.", cancellationToken);
            return savedId;
        }
        finally
        {
            AttendanceLock.Release();
        }
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
            var employeeTypeIndex = FindHeader(headers, "EMPLOYEETYPE", "TIPEKARYAWAN", "STATUSKARYAWAN");
            var positionIndex = FindHeader(headers, "POSITIONNAME", "POSITION", "JABATAN");
            var departmentIndex = FindHeader(headers, "ORGNAME", "DEPARTMENT", "DEPARTEMEN");
            var phoneIndex = FindHeader(headers, "PHONENUMBER", "PHONE", "NOMORHP");
            var emailIndex = FindHeader(headers, "EMAIL", "EMAILADDRESS");
            var genderIndex = FindHeader(headers, "GENDER", "JENISKELAMIN");
            var campIndex = FindHeader(headers, "CAMP");
            var transportIndex = FindHeader(headers, "TRANSPORT", "TRANSPORTASI");
            var vehicleIndex = FindHeader(headers, "MOBIL", "VEHICLE", "NOMOBIL");
            var groupNumberIndex = FindHeader(headers, "GROUPNUMBER", "NOMORGROUP", "GRUP");
            if (employeeIndex < 0 || nameIndex < 0)
                throw new InvalidOperationException("Kolom wajib NIK dan EMPLOYEE_NAME tidak ditemukan pada master karyawan JSON.");

            var participantsRows = await sheets.ReadAsync("'Participants'!A1:R", cancellationToken);
            var existingRows = ToRecords(participantsRows)
                .Where(row => Get(row, "EventId") == eventId)
                .ToList();
            var existing = existingRows
                .Select(row => Get(row, "EmployeeNumber"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seenSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceGroupNumbers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                if (!sourceGroupNumbers.ContainsKey(employeeNumber))
                    sourceGroupNumbers[employeeNumber] = Cell(row, groupNumberIndex).Trim();
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
                    true, at, at,
                    Cell(row, employeeTypeIndex).Trim(),
                    Cell(row, positionIndex).Trim(),
                    Cell(row, genderIndex).Trim(),
                    Cell(row, campIndex).Trim(),
                    Cell(row, transportIndex).Trim(),
                    Cell(row, vehicleIndex).Trim(),
                    Cell(row, groupNumberIndex).Trim()
                });
            }

            if (additions.Count > 0)
                await sheets.AppendRowsAsync("'Participants'!A:R", additions, cancellationToken);
            if (groupNumberIndex >= 0)
            {
                var participantHeaders = participantsRows[0]
                    .Select(value => Convert.ToString(value)?.Trim() ?? string.Empty).ToList();
                var participantGroupIndex = participantHeaders.FindIndex(header =>
                    NormalizeHeader(header) == NormalizeHeader("GroupNumber"));
                var participantEventIndex = participantHeaders.FindIndex(header =>
                    NormalizeHeader(header) == NormalizeHeader("EventId"));
                var participantEmployeeIndex = participantHeaders.FindIndex(header =>
                    NormalizeHeader(header) == NormalizeHeader("EmployeeNumber"));
                if (new[] { participantGroupIndex, participantEventIndex, participantEmployeeIndex }.Any(index => index < 0))
                    throw new InvalidOperationException("Kolom GROUP_NUMBER tidak tersedia pada struktur database peserta.");

                var groupUpdates = new List<JsonValueRange>();
                for (var index = 1; index < participantsRows.Count; index++)
                {
                    var row = participantsRows[index];
                    if (Cell(row, participantEventIndex) != eventId ||
                        !sourceGroupNumbers.TryGetValue(Cell(row, participantEmployeeIndex), out var groupNumber) ||
                        string.IsNullOrWhiteSpace(groupNumber))
                        continue;
                    groupUpdates.Add(new JsonValueRange
                    {
                        Range = $"'Participants'!{ColumnName(participantGroupIndex)}{index + 1}:{ColumnName(participantGroupIndex)}{index + 1}",
                        Values = new List<IList<object>> { new List<object> { groupNumber } }
                    });
                }
                if (groupUpdates.Count > 0)
                    await sheets.UpdateManyAsync(groupUpdates, cancellationToken);
            }
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
                item.EmployeeType.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.PositionName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Department.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Gender.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Email.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Camp.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Transport.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Vehicle.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.GroupNumber.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        var results = participants.OrderBy(item => item.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var items = results.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new ParticipantListItem(
                item.ParticipantId,
                item.EmployeeNumber,
                item.FullName,
                item.EmployeeType,
                item.PositionName,
                item.Department,
                item.Gender,
                item.Email,
                item.Camp,
                item.Transport,
                item.Vehicle,
                item.GroupNumber,
                item.IsActive))
            .ToList();
        return new ParticipantPage(results.Count, page, pageSize, items);
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
                throw new InvalidOperationException("Struktur peserta pada database JSON tidak sesuai dengan konfigurasi OMS.");

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
                    new JsonValueRange
                    {
                        Range = $"'Participants'!H{sheetRow}:H{sheetRow}",
                        Values = new List<IList<object>> { new List<object> { HashToken(token) } }
                    },
                    new JsonValueRange
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
            CsvRow(["Nomor Pegawai", "Nama", "Departemen", "Status Kehadiran", "Waktu Check-in", "Waktu Check-out", "Metode Check-in", "Metode Check-out"])
        };
        foreach (var participant in participants)
        {
            attendance.TryGetValue(participant.ParticipantId, out var row);
            lines.Add(CsvRow([
                participant.EmployeeNumber, participant.FullName, participant.Department,
                row is null ? "Belum hadir" : "Hadir",
                row is null ? string.Empty : Get(row, "AttendanceTime"),
                row is null ? string.Empty : Get(row, "CheckOutTime"),
                row is null ? string.Empty : Get(row, "CheckInMethod"),
                row is null ? string.Empty : Get(row, "CheckOutMethod")
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
                    row is null ? string.Empty : Get(row, "CheckInMethod"),
                    row is null ? string.Empty : Get(row, "CheckOutTime"),
                    row is null ? string.Empty : Get(row, "CheckOutMethod"));
            }).ToList();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        return new AttendanceReportPage(
            report.Count, page, pageSize, report.Skip((page - 1) * pageSize).Take(pageSize).ToList());
    }

    public async Task<InvitationBatchResult> SendInvitationBatchAsync(
        string eventId,
        string actorEmail,
        string scheduleUrl,
        int offset,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var participants = (await GetParticipantsAsync(eventId, cancellationToken))
            .Where(item => item.IsActive)
            .ToList();
        var candidates = new List<OmsParticipant>();
        foreach (var participant in participants)
        {
            if (System.Net.Mail.MailAddress.TryCreate(participant.Email.Trim(), out _))
                candidates.Add(participant);
        }

        offset = Math.Clamp(offset, 0, candidates.Count);
        batchSize = Math.Clamp(batchSize, 1, 20);
        var batch = candidates.Skip(offset).Take(batchSize).ToList();
        var invitationInfoByEmployee = batch.Count == 0
            ? new Dictionary<string, EmployeeInvitationInfo>(StringComparer.OrdinalIgnoreCase)
            : await GetEmployeeInvitationInfoAsync(cancellationToken);
        var sent = 0;
        var errors = new List<string>();
        foreach (var participant in batch)
        {
            try
            {
                await SendInvitationToParticipantAsync(
                    eventId, participant, actorEmail, invitationInfoByEmployee, scheduleUrl, cancellationToken);
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

    public async Task<InvitationBatchResult> SendIndividualInvitationAsync(
        string eventId,
        string participantId,
        string actorEmail,
        string scheduleUrl,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var participant = (await GetParticipantsAsync(eventId, cancellationToken))
            .FirstOrDefault(item => item.ParticipantId == participantId);
        if (participant is null)
            throw new InvalidOperationException("Peserta tidak ditemukan pada kegiatan ini");
        if (!participant.IsActive)
            throw new InvalidOperationException("Peserta tidak aktif");
        if (!System.Net.Mail.MailAddress.TryCreate(participant.Email.Trim(), out _))
            throw new InvalidOperationException("Alamat email peserta kosong atau tidak valid");

        var invitationInfoByEmployee = await GetEmployeeInvitationInfoAsync(cancellationToken);
        await SendInvitationToParticipantAsync(
            eventId, participant, actorEmail, invitationInfoByEmployee, scheduleUrl, cancellationToken);
        return new InvitationBatchResult(1, 0, 0, 1, 1, []);
    }

    private async Task SendInvitationToParticipantAsync(
        string eventId,
        OmsParticipant participant,
        string actorEmail,
        IReadOnlyDictionary<string, EmployeeInvitationInfo> invitationInfoByEmployee,
        string scheduleUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(participant.Email))
            throw new InvalidOperationException($"Email peserta NIK {participant.EmployeeNumber} belum diisi.");
        if (!invitationInfoByEmployee.TryGetValue(participant.EmployeeNumber.Trim(), out var invitationInfo))
            invitationInfo = new EmployeeInvitationInfo(
                participant.Camp, participant.Transport, participant.Vehicle);
        else
            invitationInfo = new EmployeeInvitationInfo(
                string.IsNullOrWhiteSpace(participant.Camp) ? invitationInfo.Camp : participant.Camp,
                string.IsNullOrWhiteSpace(participant.Transport) ? invitationInfo.Transport : participant.Transport,
                string.IsNullOrWhiteSpace(participant.Vehicle) ? invitationInfo.Vehicle : participant.Vehicle);

        var qr = await IssueQrAsync(eventId, participant.ParticipantId, actorEmail, cancellationToken);
        var outing = await GetEventAsync(eventId, cancellationToken);
        await invitations.SendAsync(
            outing,
            participant with { Email = participant.Email.Trim() },
            qr.Token,
            invitationInfo.Camp,
            invitationInfo.Transport,
            invitationInfo.Vehicle,
            $"{scheduleUrl}#participantQr={Uri.EscapeDataString(qr.Token)}",
            cancellationToken);
        await AuditAsync(eventId, actorEmail, "SEND_INVITATION", "Participant", participant.ParticipantId,
            "Undangan QR dikirim.", cancellationToken);
    }

    private async Task<Dictionary<string, EmployeeInvitationInfo>> GetEmployeeInvitationInfoAsync(
        CancellationToken cancellationToken)
    {
        var master = await sheets.ReadAsync("'List All EMP LENGKAP'!A1:Z", cancellationToken);
        if (master.Count < 2) return new Dictionary<string, EmployeeInvitationInfo>(StringComparer.OrdinalIgnoreCase);

        var headers = master[0].Select(value => NormalizeHeader(Convert.ToString(value))).ToList();
        var employeeIndex = FindHeader(headers, "NIK", "EMPLOYEENUMBER", "NOMORPEGAWAI");
        var campIndex = FindHeader(headers, "CAMP");
        var transportIndex = FindHeader(headers, "TRANSPORT");
        var vehicleIndex = FindHeader(headers, "MOBIL", "VEHICLE", "NOMOBIL");
        if (employeeIndex < 0) return new Dictionary<string, EmployeeInvitationInfo>(StringComparer.OrdinalIgnoreCase);

        var invitationInfoByEmployee = new Dictionary<string, EmployeeInvitationInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in master.Skip(1))
        {
            var employeeNumber = Cell(row, employeeIndex).Trim();
            if (string.IsNullOrWhiteSpace(employeeNumber)) continue;

            var info = new EmployeeInvitationInfo(
                Cell(row, campIndex).Trim(),
                Cell(row, transportIndex).Trim(),
                Cell(row, vehicleIndex).Trim());
            if (!invitationInfoByEmployee.TryGetValue(employeeNumber, out var existingInfo) ||
                string.IsNullOrWhiteSpace(existingInfo.Camp) ||
                string.IsNullOrWhiteSpace(existingInfo.Transport) ||
                string.IsNullOrWhiteSpace(existingInfo.Vehicle))
                invitationInfoByEmployee[employeeNumber] = info;
        }

        return invitationInfoByEmployee;
    }

    public async Task<IReadOnlyList<OmsSession>> ListSessionsAsync(string eventId, CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var rows = await sheets.ReadAsync("'Sessions'!A1:Z", cancellationToken);
        return ToRecords(rows)
            .Where(row => Get(row, "EventId") == eventId)
            .Select(row => new OmsSession(
                Get(row, "SessionId"),
                Get(row, "SessionName"),
                Get(row, "Description"),
                Get(row, "StartTime"),
                Get(row, "EndTime"),
                Get(row, "Location"),
                Get(row, "SessionType"),
                Get(row, "Status")))
            .OrderBy(item => item.StartTime, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<string> CreateScheduleItemAsync(
        string eventId,
        ScheduleItemRequest request,
        string actorEmail,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var validated = ValidateScheduleRequest(request);
        var sessionId = await sheets.CreateScheduleItemAsync(eventId, validated, cancellationToken);
        await AuditAsync(eventId, actorEmail, "CREATE", "ScheduleItem", sessionId,
            "Agenda kegiatan dibuat.", cancellationToken);
        return sessionId;
    }

    public async Task UpdateScheduleItemAsync(
        string eventId,
        string sessionId,
        ScheduleItemRequest request,
        string actorEmail,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        var validated = ValidateScheduleRequest(request);
        await sheets.UpdateScheduleItemAsync(eventId, sessionId, validated, cancellationToken);
        await AuditAsync(eventId, actorEmail, "UPDATE", "ScheduleItem", sessionId,
            "Agenda kegiatan diperbarui.", cancellationToken);
    }

    public async Task DeleteScheduleItemAsync(
        string eventId,
        string sessionId,
        string actorEmail,
        CancellationToken cancellationToken)
    {
        await GetEventAsync(eventId, cancellationToken);
        await sheets.DeleteScheduleItemAsync(eventId, sessionId, cancellationToken);
        await AuditAsync(eventId, actorEmail, "DELETE", "ScheduleItem", sessionId,
            "Agenda kegiatan dihapus.", cancellationToken);
    }

    public async Task<EventScheduleViewModel?> GetPublicEventScheduleAsync(
        string eventId,
        CancellationToken cancellationToken)
    {
        var @event = (await ListEventsAsync(cancellationToken))
            .FirstOrDefault(item => item.EventId == eventId);
        if (@event is null) return null;
        var rows = await sheets.ReadAsync("'Sessions'!A1:Z", cancellationToken);
        var sessions = ToRecords(rows)
            .Where(row => Get(row, "EventId") == eventId)
            .Select(row => new OmsSession(
                Get(row, "SessionId"),
                Get(row, "SessionName"),
                Get(row, "Description"),
                Get(row, "StartTime"),
                Get(row, "EndTime"),
                Get(row, "Location"),
                Get(row, "SessionType"),
                Get(row, "Status")))
            .ToList();
        var items = sessions
            .Where(item => item.Status.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.StartTime) &&
                !string.IsNullOrWhiteSpace(item.EndTime))
            .Select(item => new PublicScheduleItem(
                item.SessionId,
                item.SessionName,
                item.Description,
                ToWibDateTime(item.StartTime),
                ToWibDateTime(item.EndTime),
                item.Location))
            .OrderBy(item => item.StartTime)
            .ToList();
        return new EventScheduleViewModel(
            @event,
            items,
            DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)));
    }

    public async Task<OmsParticipant?> FindPublicParticipantByQrAsync(
        string eventId,
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
            return null;

        var hash = HashToken(token);
        return (await GetParticipantsAsync(eventId, cancellationToken))
            .FirstOrDefault(participant =>
                participant.IsActive &&
                participant.TokenHash.Equals(hash, StringComparison.Ordinal));
    }

    public async Task<ScanResponse> CheckInQrAsync(
        string eventId,
        string? sessionId,
        string token,
        string checkedBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
            return new ScanResponse("INVALID_QR", "QR TIDAK VALID");

        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            var @event = await GetEventAsync(eventId, cancellationToken);
            if (new[] { "Draft", "Cancelled", "Completed" }.Contains(@event.Status, StringComparer.OrdinalIgnoreCase))
                return new ScanResponse("EVENT_INACTIVE", "CHECK-IN TIDAK TERSEDIA UNTUK KEGIATAN INI");
            var sessionError = await ValidateSessionAsync(eventId, sessionId, cancellationToken);
            if (sessionError is not null) return new ScanResponse("SESSION_INACTIVE", sessionError);

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            var participants = await GetParticipantsAsync(eventId, cancellationToken);
            var participant = participants.FirstOrDefault(item => item.TokenHash.Equals(hash, StringComparison.Ordinal));
            if (participant is null) return new ScanResponse("INVALID_QR", "QR TIDAK VALID");
            if (!participant.IsActive) return new ScanResponse("PARTICIPANT_INACTIVE", "PESERTA TIDAK AKTIF");
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
                throw new InvalidOperationException("Check-in tidak tersedia untuk status kegiatan " + @event.Status);
            var sessionError = await ValidateSessionAsync(eventId, sessionId, cancellationToken);
            if (sessionError is not null) throw new InvalidOperationException(sessionError);

            var participant = (await GetParticipantsAsync(eventId, cancellationToken))
                .FirstOrDefault(item => item.ParticipantId == participantId);
            if (participant is null) throw new InvalidOperationException("Peserta tidak terdaftar pada kegiatan ini");
            if (!participant.IsActive) throw new InvalidOperationException("Peserta tidak aktif");
            return await SaveAttendanceAsync(eventId, sessionId, participant, "MANUAL", checkedBy, cancellationToken);
        }
        finally
        {
            AttendanceLock.Release();
        }
    }

    public async Task<ScanResponse> CheckOutAsync(
        string eventId,
        string? sessionId,
        string participantId,
        string method,
        string checkedBy,
        CancellationToken cancellationToken)
    {
        if (method is not ("QR" or "MANUAL"))
            throw new InvalidOperationException("Metode check-out tidak valid");

        await AttendanceLock.WaitAsync(cancellationToken);
        try
        {
            var @event = await GetEventAsync(eventId, cancellationToken);
            if (new[] { "Draft", "Cancelled", "Completed" }.Contains(@event.Status, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Check-out tidak tersedia untuk status kegiatan " + @event.Status);
            var sessionError = await ValidateSessionAsync(eventId, sessionId, cancellationToken);
            if (sessionError is not null) throw new InvalidOperationException(sessionError);

            var participant = (await GetParticipantsAsync(eventId, cancellationToken))
                .FirstOrDefault(item => item.ParticipantId == participantId);
            if (participant is null) throw new InvalidOperationException("Peserta tidak terdaftar pada kegiatan ini");
            if (!participant.IsActive) throw new InvalidOperationException("Peserta tidak aktif");

            var rows = await ReadAttendanceRowsWithCheckoutHeadersAsync(cancellationToken);
            var headers = GetAttendanceHeaders(rows);
            var participantColumn = GetRequiredHeaderIndex(headers, "ParticipantId");
            var eventColumn = GetRequiredHeaderIndex(headers, "EventId");
            var sessionColumn = GetRequiredHeaderIndex(headers, "SessionId");
            var checkInColumn = GetRequiredHeaderIndex(headers, "AttendanceTime");
            var checkOutColumn = GetRequiredHeaderIndex(headers, "CheckOutTime");
            var checkOutMethodColumn = GetRequiredHeaderIndex(headers, "CheckOutMethod");
            var checkedOutByColumn = GetRequiredHeaderIndex(headers, "CheckedOutBy");
            var matchingRows = FindAttendanceRows(
                rows, eventColumn, participantColumn, sessionColumn,
                eventId, participantId, sessionId ?? string.Empty);

            if (matchingRows.Count == 0)
                return new ScanResponse("CHECKIN_REQUIRED", "CHECK-IN BELUM TERCATAT", participant);
            if (matchingRows.Count > 1)
                throw new InvalidOperationException(
                    "Terdapat lebih dari satu baris check-in untuk peserta dan sesi ini. Periksa data Attendance sebelum check-out");

            var rowIndex = matchingRows[0];
            var row = rows[rowIndex];
            var checkInTime = Cell(row, checkInColumn).Trim();
            var existingCheckOut = Cell(row, checkOutColumn).Trim();
            if (string.IsNullOrWhiteSpace(checkInTime))
                return new ScanResponse("CHECKIN_REQUIRED", "CHECK-IN BELUM TERCATAT", participant);
            if (!string.IsNullOrWhiteSpace(existingCheckOut))
                return new ScanResponse(
                    "ALREADY_CHECKED_OUT", "PESERTA SUDAH CHECK-OUT", participant, checkInTime, existingCheckOut);

            var now = DateTimeOffset.UtcNow;
            var checkOutTime = now.ToOffset(TimeSpan.FromHours(7))
                .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            var auditTimestamp = now.ToString("O", CultureInfo.InvariantCulture);
            var sheetRow = rowIndex + 1;
            await sheets.UpdateManyAsync(
            [
                new JsonValueRange
                {
                    Range = AttendanceCellRange(checkOutColumn, sheetRow),
                    Values = new List<IList<object>> { new List<object> { checkOutTime } }
                },
                new JsonValueRange
                {
                    Range = AttendanceCellRange(checkOutMethodColumn, sheetRow),
                    Values = new List<IList<object>> { new List<object> { method } }
                },
                new JsonValueRange
                {
                    Range = AttendanceCellRange(checkedOutByColumn, sheetRow),
                    Values = new List<IList<object>> { new List<object> { checkedBy } }
                }
            ], cancellationToken);

            try
            {
                await sheets.AppendAsync("'AuditLog'!A:H",
                [
                    Guid.NewGuid().ToString(), eventId, checkedBy, "CHECK_OUT", "Attendance",
                    Cell(row, GetRequiredHeaderIndex(headers, "AttendanceId")), auditTimestamp,
                    "Check-out dengan metode " + method + "."
                ], cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Check-out peserta {ParticipantId} tersimpan tetapi audit log gagal ditulis.", participantId);
            }

            return new ScanResponse(
                "CHECK_OUT_SUCCESS", "CHECK-OUT BERHASIL", participant, checkInTime, checkOutTime);
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
        var attendanceRows = await ReadAttendanceRowsWithCheckoutHeadersAsync(cancellationToken);
        var headers = GetAttendanceHeaders(attendanceRows);
        var eventColumn = GetRequiredHeaderIndex(headers, "EventId");
        var participantColumn = GetRequiredHeaderIndex(headers, "ParticipantId");
        var sessionColumn = GetRequiredHeaderIndex(headers, "SessionId");
        var checkInColumn = GetRequiredHeaderIndex(headers, "AttendanceTime");
        var checkOutColumn = GetRequiredHeaderIndex(headers, "CheckOutTime");
        var existingRows = FindAttendanceRows(
            attendanceRows, eventColumn, participantColumn, sessionColumn,
            eventId, participant.ParticipantId, sessionId ?? string.Empty);
        if (existingRows.Count > 0)
        {
            var existing = attendanceRows[existingRows[0]];
            var checkOutTime = Cell(existing, checkOutColumn).Trim();
            return new ScanResponse(
                "DUPLICATE",
                string.IsNullOrWhiteSpace(checkOutTime)
                    ? "SUDAH CHECK-IN"
                    : "PESERTA SUDAH CHECK-IN DAN CHECK-OUT",
                participant,
                Cell(existing, checkInColumn),
                checkOutTime);
        }

        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToOffset(TimeSpan.FromHours(7))
            .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var auditTimestamp = now.ToString("O", CultureInfo.InvariantCulture);
        var id = Guid.NewGuid().ToString();
        await sheets.AppendAsync("'Attendance'!A:I",
        [
            id, eventId, participant.ParticipantId, sessionId ?? string.Empty,
            timestamp, method, checkedBy, string.Empty, auditTimestamp
        ], cancellationToken);

        try
        {
            await sheets.AppendAsync("'AuditLog'!A:H",
            [
                Guid.NewGuid().ToString(), eventId, checkedBy, "CHECK_IN", "Attendance",
                id, auditTimestamp, "Check-in dengan metode " + method + "."
            ], cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Check-in {AttendanceId} tersimpan tetapi audit log gagal ditulis.", id);
        }

        return new ScanResponse("SUCCESS", "BERHASIL CHECK-IN", participant, timestamp);
    }

    private async Task<IList<IList<object>>> ReadAttendanceRowsWithCheckoutHeadersAsync(
        CancellationToken cancellationToken)
    {
        var rows = await sheets.ReadAsync("'Attendance'!A1:AZ", cancellationToken);
        if (rows.Count == 0)
            throw new InvalidOperationException("Struktur tabel Attendance pada database JSON belum tersedia.");

        var headers = GetAttendanceHeaders(rows);
        foreach (var requiredHeader in AttendanceBaseHeaders)
        {
            if (headers.FindIndex(header => NormalizeHeader(header) == NormalizeHeader(requiredHeader)) < 0)
                throw new InvalidOperationException(
                    $"Kolom {requiredHeader} tidak ditemukan pada tabel Attendance JSON.");
        }

        var missingHeaders = AttendanceCheckoutHeaders
            .Where(requiredHeader =>
                headers.FindIndex(header => NormalizeHeader(header) == NormalizeHeader(requiredHeader)) < 0)
            .ToList();
        if (missingHeaders.Count == 0) return rows;

        var lastHeaderIndex = headers.FindLastIndex(header => !string.IsNullOrWhiteSpace(header));
        if (lastHeaderIndex + missingHeaders.Count >= 52)
            throw new InvalidOperationException("Tidak ada kolom kosong untuk menambahkan data check-out pada tabel Attendance JSON.");

        var firstNewColumn = lastHeaderIndex + 1;
        var lastNewColumn = firstNewColumn + missingHeaders.Count - 1;
        await sheets.UpdateAsync(
            $"'Attendance'!{ColumnName(firstNewColumn)}1:{ColumnName(lastNewColumn)}1",
            [missingHeaders.Cast<object>().ToList()],
            cancellationToken);

        for (var index = 0; index < missingHeaders.Count; index++)
        {
            headers.Add(missingHeaders[index]);
            rows[0].Add(missingHeaders[index]);
        }
        return rows;
    }

    private static List<string> GetAttendanceHeaders(IList<IList<object>> rows) =>
        rows[0].Select(Convert.ToString).Select(value => value?.Trim() ?? string.Empty).ToList();

    private static int GetRequiredHeaderIndex(IReadOnlyList<string> headers, string name)
    {
        var index = headers.ToList().FindIndex(header => NormalizeHeader(header) == NormalizeHeader(name));
        return index >= 0
            ? index
            : throw new InvalidOperationException($"Kolom {name} tidak ditemukan pada tabel Attendance JSON.");
    }

    private static List<int> FindAttendanceRows(
        IList<IList<object>> rows,
        int eventColumn,
        int participantColumn,
        int sessionColumn,
        string eventId,
        string participantId,
        string sessionId)
    {
        var matchingRows = new List<int>();
        for (var index = 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (Cell(row, eventColumn).Trim() == eventId &&
                Cell(row, participantColumn).Trim() == participantId &&
                Cell(row, sessionColumn).Trim() == sessionId)
                matchingRows.Add(index);
        }

        return matchingRows;
    }

    private static string AttendanceCellRange(int columnIndex, int rowIndex)
    {
        var column = ColumnName(columnIndex);
        return $"'Attendance'!{column}{rowIndex}:{column}{rowIndex}";
    }

    private static string ColumnName(int zeroBasedIndex)
    {
        var columnNumber = zeroBasedIndex + 1;
        var name = string.Empty;
        while (columnNumber > 0)
        {
            columnNumber--;
            name = (char)('A' + columnNumber % 26) + name;
            columnNumber /= 26;
        }

        return name;
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
            : "SESI TIDAK AKTIF";
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
                Get(row, "QrTokenHash"),
                Get(row, "EmployeeType"),
                Get(row, "PositionName"),
                Get(row, "Gender"),
                Get(row, "Camp"),
                Get(row, "Transport"),
                Get(row, "Vehicle"),
                Get(row, "GroupNumber")))
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

    private static ScheduleItemRequest ValidateScheduleRequest(ScheduleItemRequest request)
    {
        var name = RequiredText(request.SessionName, 160, "Nama agenda wajib diisi.");
        var description = SafeText(request.Description, 1000);
        var location = SafeText(request.Location, 200);
        var status = string.IsNullOrWhiteSpace(request.Status) ? "Active" : request.Status.Trim();
        if (status is not ("Active" or "Inactive"))
            throw new InvalidOperationException("Status agenda harus Active atau Inactive.");
        if (!TryParseScheduleTime(request.StartTime, out var start) ||
            !TryParseScheduleTime(request.EndTime, out var end))
            throw new InvalidOperationException("Waktu agenda harus diisi dengan tanggal dan jam yang valid.");
        if (end <= start)
            throw new InvalidOperationException("Waktu selesai agenda harus setelah waktu mulai.");

        return request with
        {
            SessionName = name,
            Description = description,
            Location = location,
            StartTime = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            EndTime = end.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            Status = status
        };
    }

    private static bool TryParseScheduleTime(string? value, out DateTime parsed)
    {
        return DateTime.TryParseExact(
            value,
            ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out parsed);
    }

    private static DateTimeOffset ToWibDateTime(string value)
    {
        if (!TryParseScheduleTime(value, out var parsed))
            throw new InvalidOperationException("Waktu agenda pada database tidak valid.");
        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), TimeSpan.FromHours(7));
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

    private static DateTimeOffset ParseDate(string value)
    {
        if (DateTime.TryParseExact(
            value,
            "dd/MM/yyyy HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var jakartaTime))
        {
            return new DateTimeOffset(
                DateTime.SpecifyKind(jakartaTime, DateTimeKind.Unspecified),
                TimeSpan.FromHours(7));
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var date)
            ? date
            : DateTimeOffset.MinValue;
    }
}
