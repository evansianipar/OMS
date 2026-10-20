using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMS.Models;
using OMS.Services;

namespace OMS.Controllers;

[ApiController]
[Route("api/oms")]
[Authorize(Policy = "OmsUser")]
public sealed class OmsApiController(OmsService oms) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard([FromQuery] string eventId, CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "REGISTRATION", "VIEWER"], cancellationToken);
        return Ok(await oms.GetDashboardAsync(eventId, cancellationToken));
    }

    [HttpGet("events")]
    public async Task<IActionResult> Events(CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "REGISTRATION", "VIEWER"], cancellationToken);
        return Ok(await oms.ListEventsAsync(cancellationToken));
    }

    [HttpPost("events")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateEvent(
        [FromBody] EventCreateRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        var eventId = await oms.CreateEventAsync(request, CurrentEmail(), cancellationToken);
        return Ok(new { eventId });
    }

    [HttpGet("participants")]
    public async Task<IActionResult> Participants(
        [FromQuery] string eventId,
        [FromQuery] string? query,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "REGISTRATION"], cancellationToken);
        return Ok(await oms.ListParticipantsAsync(eventId, query, page, pageSize, cancellationToken));
    }

    [HttpPost("participants/import-master")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportMaster(
        [FromBody] ImportRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        return Ok(await oms.ImportEmployeeMasterAsync(request.EventId, CurrentEmail(), cancellationToken));
    }

    [HttpPost("participants/send-invitations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendInvitations(
        [FromBody] InvitationBatchRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        return Ok(await oms.SendInvitationBatchAsync(
            request.EventId, CurrentEmail(), request.Offset, request.BatchSize, cancellationToken));
    }

    [HttpPost("participants/issue-qr")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IssueQr(
        [FromBody] IssueQrRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        return Ok(await oms.IssueQrAsync(request.EventId, request.ParticipantId, CurrentEmail(), cancellationToken));
    }

    [HttpGet("reports/attendance.csv")]
    public async Task<IActionResult> ExportAttendance(
        [FromQuery] string eventId,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "VIEWER"], cancellationToken);
        var csv = await oms.ExportAttendanceCsvAsync(eventId, cancellationToken);
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8",
            $"kehadiran-{eventId}.csv");
    }

    [HttpGet("reports/attendance")]
    public async Task<IActionResult> AttendanceReport(
        [FromQuery] string eventId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "VIEWER"], cancellationToken);
        return Ok(await oms.GetAttendanceReportAsync(eventId, page, pageSize, cancellationToken));
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions([FromQuery] string eventId, CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "REGISTRATION", "VIEWER"], cancellationToken);
        return Ok(await oms.ListSessionsAsync(eventId, cancellationToken));
    }

    [HttpGet("participants/search")]
    public async Task<IActionResult> SearchParticipants(
        [FromQuery] string eventId,
        [FromQuery] string query,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "REGISTRATION"], cancellationToken);
        return Ok(await oms.SearchParticipantsAsync(eventId, query, cancellationToken));
    }

    [HttpPost("scanner/check-in")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckIn([FromBody] ScanRequest request, CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "REGISTRATION"], cancellationToken);
        var result = await oms.CheckInQrAsync(
            request.EventId, request.SessionId, request.Token, CurrentEmail(), cancellationToken);
        return Ok(result);
    }

    [HttpPost("scanner/manual-check-in")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ManualCheckIn(
        [FromBody] ManualCheckInRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "REGISTRATION"], cancellationToken);
        var result = await oms.CheckInManualAsync(
            request.EventId, request.SessionId, request.ParticipantId, CurrentEmail(), cancellationToken);
        return Ok(result);
    }

    private string CurrentEmail() =>
        User.FindFirst("email")?.Value
        ?? throw new UnauthorizedAccessException("Email akun Google tidak tersedia.");
}
