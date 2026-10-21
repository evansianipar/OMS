using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OMS.Configuration;
using OMS.Services;

namespace OMS.Controllers;

[Route("account")]
public sealed class AccountController : Controller
{
    [AllowAnonymous]
    [HttpGet("connect-sheets")]
    public IActionResult ConnectSheets()
    {
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"]) ||
            string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientSecret"]))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "OAuth Google belum dikonfigurasi pada server OMS");

        return Challenge(
            new AuthenticationProperties { RedirectUri = "/account/sheets-connected" },
            "GoogleSheets");
    }

    [AllowAnonymous]
    [HttpGet("sheets-connected")]
    public IActionResult SheetsConnected()
    {
        var store = HttpContext.RequestServices.GetRequiredService<GoogleSheetsCredentialStore>();
        if (!store.HasCredential)
            return Redirect("/account/sheets-connect-failed");
        return Content(
            "Akses spreadsheet dan email pengirim berhasil dihubungkan. Sekarang buka halaman utama OMS dan login menggunakan akun panitia",
            "text/plain; charset=utf-8");
    }

    [AllowAnonymous]
    [HttpGet("sheets-connect-failed")]
    public IActionResult SheetsConnectFailed()
    {
        var sheetsUser = HttpContext.RequestServices
            .GetRequiredService<IOptions<GoogleWorkspaceOptions>>()
            .Value.SheetsUser;
        return new ContentResult
        {
            Content = $"Akses Google Sheets belum terhubung. Pastikan Anda menggunakan akun {sheetsUser} dan aplikasi OAuth diizinkan oleh kebijakan Workspace",
            ContentType = "text/plain; charset=utf-8",
            StatusCode = StatusCodes.Status400BadRequest
        };
    }

    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = "/")
    {
        ViewData["GoogleDomain"] = HttpContext.RequestServices
            .GetRequiredService<IConfiguration>()["GoogleWorkspace:Domain"] ?? "wom.co.id";
        if (string.IsNullOrWhiteSpace(HttpContext.RequestServices
            .GetRequiredService<IConfiguration>()["Authentication:Google:ClientId"]))
        {
            return View("LoginUnavailable");
        }

        return Challenge(
            new AuthenticationProperties { RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/" },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [AllowAnonymous]
    [HttpGet("start")]
    public IActionResult Start(string? returnUrl = "/")
    {
        ViewData["GoogleDomain"] = HttpContext.RequestServices
            .GetRequiredService<IConfiguration>()["GoogleWorkspace:Domain"] ?? "wom.co.id";
        if (string.IsNullOrWhiteSpace(HttpContext.RequestServices
            .GetRequiredService<IConfiguration>()["Authentication:Google:ClientId"]))
        {
            return View("LoginUnavailable");
        }
        return View("Login");
    }

    [AllowAnonymous]
    [HttpGet("denied")]
    public IActionResult Denied() => View();

    [Authorize]
    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }
}
