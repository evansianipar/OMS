using System.Text.Json.Serialization;

namespace OMS.Models;

public sealed record OmsUser(string Email, string FullName, string Role);

public sealed record OmsEvent(
    string EventId,
    string EventCode,
    string EventName,
    string Description,
    string StartDate,
    string EndDate,
    string Location,
    string Status);

public sealed record OmsParticipant(
    string ParticipantId,
    string EventId,
    string EmployeeNumber,
    string FullName,
    string Department,
    [property: JsonIgnore] string Email,
    bool IsActive,
    [property: JsonIgnore] string TokenHash = "");

public sealed record HomeViewModel(
    IReadOnlyList<OmsEvent> Events,
    DashboardSummary? Dashboard,
    string? ErrorMessage = null,
    string Page = "dashboard",
    IReadOnlyList<OmsSession>? ScheduleItems = null);

public sealed record EventScheduleViewModel(
    OmsEvent Event,
    IReadOnlyList<PublicScheduleItem> Items,
    DateTimeOffset CurrentTimeWib);

public sealed record PublicQrRequest(string Token);

public sealed record PublicParticipantQr(
    string FullName,
    string EmployeeNumber,
    string QrPngBase64);

public sealed record ParticipantPage(
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<OmsParticipant> Items);

public sealed record AttendanceReportRow(
    string EmployeeNumber,
    string FullName,
    string Department,
    string Status,
    string AttendanceTime,
    string Method,
    string CheckOutTime,
    string CheckOutMethod);

public sealed record AttendanceReportPage(
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<AttendanceReportRow> Items);

public sealed record InvitationBatchResult(
    int Sent,
    int Failed,
    int Skipped,
    int Processed,
    int Total,
    IReadOnlyList<string> Errors);

public sealed record InvitationBatchRequest(string EventId, int Offset = 0, int BatchSize = 10);

public sealed record IndividualInvitationRequest(string EventId, string ParticipantId);

public sealed record ImportSummary(int Imported, int Existing, int Invalid, int DuplicateSource);

public sealed record EventCreateRequest(
    string EventCode,
    string EventName,
    string? Description,
    string? StartDate,
    string? EndDate,
    string? Location,
    string? Status);

public sealed record ImportRequest(string EventId);

public sealed record EmployeeMasterImportRequest(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record EventDeletionSummary(
    int Participants,
    int Sessions,
    int AttendanceRecords,
    int AuditRecords);

public sealed record ScheduleItemRequest(
    string SessionName,
    string? Description,
    string StartTime,
    string EndTime,
    string? Location,
    string? Status);

public sealed record IssueQrRequest(string EventId, string ParticipantId);

public sealed record QrIssueResponse(string Token, string EmployeeNumber, string FullName);

public sealed record OmsSession(
    string SessionId,
    string SessionName,
    string Description,
    string StartTime,
    string EndTime,
    string Location,
    string SessionType,
    string Status);

public sealed record PublicScheduleItem(
    string SessionId,
    string SessionName,
    string Description,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string Location);

public sealed record DashboardSummary(
    OmsEvent Event,
    int Total,
    int Attended,
    int Absent,
    double AttendancePercent,
    IReadOnlyList<DepartmentAttendance> Departments,
    IReadOnlyList<RecentCheckIn> Recent,
    IReadOnlyList<OmsParticipant> AbsentParticipants);

public sealed record DepartmentAttendance(string Department, int Total, int Attended);

public sealed record RecentCheckIn(
    string FullName,
    string EmployeeNumber,
    string Department,
    string AttendanceTime,
    string Method);

public sealed record ScanRequest(string EventId, string? SessionId, string Token);

public sealed record ManualCheckInRequest(string EventId, string? SessionId, string ParticipantId);

public sealed record CheckOutRequest(
    string EventId,
    string? SessionId,
    string ParticipantId,
    string Method = "QR");

public sealed record ScanResponse(
    string Status,
    string Message,
    OmsParticipant? Participant = null,
    string? AttendanceTime = null,
    string? CheckOutTime = null);
