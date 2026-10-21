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
            return View(new HomeViewModel(events, dashboard, Page: currentPage));
        }
        catch (Exception exception)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<HomeController>>()
                .LogError(exception, "Unable to load OMS home page.");
            return View(new HomeViewModel([], null,
                "Data OMS tidak dapat dimuat. Administrator perlu memeriksa konfigurasi akses Google Sheets",
                currentPage));
        }
    }

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [AllowAnonymous]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
