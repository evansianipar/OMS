using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMS.Services;
using System.Security.Claims;

namespace OMS.Controllers;

[Route("account")]
public sealed class AccountController(UserDirectoryService users) : Controller
{
    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = "/") =>
        View("Login", model: returnUrl);

    [AllowAnonymous]
    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(
        string email,
        string password,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            ViewData["LoginError"] = "Email dan password wajib diisi.";
            return View("Login", model: returnUrl);
        }

        var user = await users.VerifyCredentialsAsync(email, password, cancellationToken);
        if (user is null)
        {
            ViewData["LoginError"] = "Email atau password salah, atau akun tidak aktif.";
            return View("Login", model: returnUrl);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim("email", user.Email),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Role, user.Role)
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
                AllowRefresh = true
            });

        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    [AllowAnonymous]
    [HttpGet("start")]
    public IActionResult Start(string? returnUrl = "/") =>
        View("Login", model: returnUrl);

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
