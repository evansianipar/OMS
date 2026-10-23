using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMS.Models;
using OMS.Services;
using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace OMS.Controllers;

[Authorize(Policy = "OmsUser")]
public sealed class HomeController(OmsService oms) : Controller
{
    [AllowAnonymous]
    public IActionResult Login() => RedirectToAction("Start", "Account");

    public async Task<IActionResult> Index(string? eventId, string? page, CancellationToken cancellationToken)
    {
        var currentPage = page is "scanner" or "participants" or "data" ? page : "dashboard";
        try
        {
            var events = await oms.ListEventsAsync(cancellationToken);
            var selectedEvent = events.FirstOrDefault(item => item.EventId == eventId)
                ?? events.FirstOrDefault(item => item.Status is "Ongoing" or "Published")
                ?? events.FirstOrDefault();
            var dashboard = selectedEvent is null || currentPage != "dashboard"
                ? null
                : await oms.GetDashboardAsync(selectedEvent.EventId, cancellationToken);
            var scheduleItems = selectedEvent is null || currentPage != "data"
                ? []
                : await oms.ListSessionsAsync(selectedEvent.EventId, cancellationToken);
            return View(new HomeViewModel(events, dashboard, Page: currentPage, ScheduleItems: scheduleItems));
        }
        catch (Exception exception)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<HomeController>>()
                .LogError(exception, "Unable to load OMS home page.");
            return View(new HomeViewModel([], null,
                "Data OMS tidak dapat dimuat. Administrator perlu memeriksa konfigurasi database JSON dan Vercel Blob.",
                currentPage));
        }
    }

    [AllowAnonymous]
    [HttpGet("/event/{eventId}/schedule")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Schedule(string eventId, CancellationToken cancellationToken)
    {
        var schedule = await oms.GetPublicEventScheduleAsync(eventId, cancellationToken);
        return schedule is null ? NotFound() : View("Schedule", schedule);
    }

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [AllowAnonymous]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
