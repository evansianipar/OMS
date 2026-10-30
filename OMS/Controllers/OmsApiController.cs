using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMS.Models;
using OMS.Services;
using QRCoder;

namespace OMS.Controllers;

[ApiController]
[Route("api/oms")]
[Authorize(Policy = "OmsUser")]
public sealed class OmsApiController(
    OmsService oms,
    OmsJsonDatabase database,
    IConfiguration configuration,
    IHostEnvironment environment) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/api/public/events/{eventId}/schedule")]
    public async Task<IActionResult> PublicSchedule(string eventId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var schedule = await oms.GetPublicEventScheduleAsync(eventId, cancellationToken);
        return schedule is null ? NotFound() : Ok(schedule);
    }

    [AllowAnonymous]
    [HttpPost("/api/public/events/{eventId}/participant-qr")]
    public async Task<IActionResult> PublicParticipantQr(
        string eventId,
        [FromBody] PublicQrRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var participant = await oms.FindPublicParticipantByQrAsync(eventId, request.Token, cancellationToken);
        if (participant is null) return NotFound();

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(request.Token, QRCodeGenerator.ECCLevel.Q);
        var qrBytes = new PngByteQRCode(data).GetGraphic(8);
        return Ok(new PublicParticipantQr(
            participant.FullName,
            participant.EmployeeNumber,
            Convert.ToBase64String(qrBytes)));
    }

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

    [HttpPut("events/{eventId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateEvent(
        string eventId,
        [FromBody] EventCreateRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        await oms.UpdateEventAsync(eventId, request, CurrentEmail(), cancellationToken);
        return Ok(new { eventId });
    }

    [HttpDelete("events/{eventId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEvent(string eventId, CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        return Ok(await oms.DeleteEventAsync(eventId, cancellationToken));
    }

    [HttpPost("events/{eventId}/schedule")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateScheduleItem(
        string eventId,
        [FromBody] ScheduleItemRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        var sessionId = await oms.CreateScheduleItemAsync(eventId, request, CurrentEmail(), cancellationToken);
        return Ok(new { sessionId });
    }

    [HttpPut("events/{eventId}/schedule/{sessionId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateScheduleItem(
        string eventId,
        string sessionId,
        [FromBody] ScheduleItemRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        await oms.UpdateScheduleItemAsync(eventId, sessionId, request, CurrentEmail(), cancellationToken);
        return Ok(new { sessionId });
    }

    [HttpDelete("events/{eventId}/schedule/{sessionId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteScheduleItem(
        string eventId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        await oms.DeleteScheduleItemAsync(eventId, sessionId, CurrentEmail(), cancellationToken);
        return NoContent();
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

    [HttpPost("participants")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateParticipant(
        [FromQuery] string eventId,
        [FromBody] ParticipantRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        var participantId = await oms.SaveParticipantAsync(eventId, null, request, CurrentEmail(), cancellationToken);
        return Ok(new { participantId });
    }

    [HttpPut("participants/{participantId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateParticipant(
        string participantId,
        [FromQuery] string eventId,
        [FromBody] ParticipantRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        await oms.SaveParticipantAsync(eventId, participantId, request, CurrentEmail(), cancellationToken);
        return Ok(new { participantId });
    }

    [HttpDelete("events/{eventId}/participants")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAllParticipants(
        string eventId,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        return Ok(await oms.DeleteAllParticipantsAsync(eventId, CurrentEmail(), cancellationToken));
    }

    [HttpDelete("participants/{participantId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteParticipant(
        string participantId,
        [FromQuery] string eventId,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        await oms.DeleteParticipantAsync(eventId, participantId, cancellationToken);
        return NoContent();
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

    [HttpPost("employee-master")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportEmployeeMaster(
        [FromBody] EmployeeMasterImportRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        await database.ImportEmployeeMasterAsync(request, cancellationToken);
        return Ok(new { imported = request.Rows.Count });
    }

    [HttpPost("participants/send-invitations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendInvitations(
        [FromBody] InvitationBatchRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        return Ok(await oms.SendInvitationBatchAsync(
            request.EventId, CurrentEmail(), ScheduleUrl(request.EventId), request.Offset, request.BatchSize, cancellationToken));
    }

    [HttpPost("participants/send-invitation")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendInvitation(
        [FromBody] IndividualInvitationRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN"], cancellationToken);
        return Ok(await oms.SendIndividualInvitationAsync(
            request.EventId, request.ParticipantId, CurrentEmail(), ScheduleUrl(request.EventId), cancellationToken));
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

    [HttpPost("scanner/check-out")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckOut(
        [FromBody] CheckOutRequest request,
        CancellationToken cancellationToken)
    {
        await oms.RequireRoleAsync(CurrentEmail(), ["SUPERADMIN", "ADMIN", "REGISTRATION"], cancellationToken);
        var result = await oms.CheckOutAsync(
            request.EventId, request.SessionId, request.ParticipantId, request.Method, CurrentEmail(), cancellationToken);
        return Ok(result);
    }

    private string CurrentEmail() =>
        User.FindFirst("email")?.Value
        ?? throw new UnauthorizedAccessException("Email akun Google tidak tersedia.");

    private string ScheduleUrl(string eventId)
    {
        var configuredBaseUrl = configuration["OmsPublic:BaseUrl"];
        Uri baseUri;
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var parsedBaseUri) ||
                parsedBaseUri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(parsedBaseUri.UserInfo) ||
                !string.IsNullOrEmpty(parsedBaseUri.Query) ||
                !string.IsNullOrEmpty(parsedBaseUri.Fragment))
                throw new InvalidOperationException("OmsPublic:BaseUrl harus berupa URL HTTP atau HTTPS yang valid.");
            if (environment.IsProduction() && parsedBaseUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("OmsPublic:BaseUrl wajib menggunakan HTTPS di Production.");
            baseUri = parsedBaseUri;
        }
        else
        {
            if (environment.IsProduction())
                throw new InvalidOperationException(
                    "Atur OmsPublic__BaseUrl dengan domain publik OMS agar tautan rundown di email benar.");
            baseUri = new Uri($"{Request.Scheme}://{Request.Host}/", UriKind.Absolute);
        }

        var baseAddress = new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(baseAddress, $"event/{Uri.EscapeDataString(eventId)}/schedule")
            .ToString();
    }
}
