using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection.Repositories;
using OMS.Configuration;
using OMS.Services;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OmsAuthOptions>(builder.Configuration.GetSection("OmsAuth"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
var rundownOrigin = builder.Configuration["OmsPublic:RundownOrigin"];
if (!string.IsNullOrWhiteSpace(rundownOrigin))
{
    if (!Uri.TryCreate(rundownOrigin, UriKind.Absolute, out var rundownOriginUri) ||
        rundownOriginUri.Scheme is not ("http" or "https") ||
        rundownOriginUri.AbsolutePath != "/" ||
        !string.IsNullOrEmpty(rundownOriginUri.Query) ||
        !string.IsNullOrEmpty(rundownOriginUri.Fragment) ||
        !string.IsNullOrEmpty(rundownOriginUri.UserInfo))
        throw new InvalidOperationException(
            "OmsPublic:RundownOrigin harus berupa origin saja, misalnya https://username.github.io.");
    if (builder.Environment.IsProduction() && rundownOriginUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("OmsPublic:RundownOrigin wajib menggunakan HTTPS di Production.");
    rundownOrigin = rundownOriginUri.GetLeftPart(UriPartial.Authority);
}
builder.Services.AddCors(options => options.AddPolicy("GitHubPagesRundown", policy =>
{
    if (!string.IsNullOrWhiteSpace(rundownOrigin))
        policy.WithOrigins(rundownOrigin).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.AddDataProtection().SetApplicationName("OMS");
builder.Services.AddSingleton<IXmlRepository, OmsDataProtectionXmlRepository>();
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<IXmlRepository>((options, repository) => options.XmlRepository = repository);
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<VercelBlobStorage>();
builder.Services.AddSingleton<OmsJsonDatabase>();
builder.Services.AddSingleton<GmailSmtpInvitationService>();
builder.Services.AddSingleton<UserDirectoryService>();
builder.Services.AddSingleton<OmsService>();
builder.Services.AddScoped<IAuthorizationHandler, OmsRoleHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("OmsUser", policy => policy.Requirements.Add(new OmsRoleRequirement()));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-OMS.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/denied";
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

var port = Environment.GetEnvironmentVariable("PORT");
if (int.TryParse(port, out var vercelPort) && vercelPort is > 0 and <= 65535)
    builder.WebHost.UseUrls($"http://0.0.0.0:{vercelPort}");

var app = builder.Build();

if (args.Length > 0 && args[0].Equals("--reset-superadmin-password", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine(
            "Penggunaan: dotnet run -- --reset-superadmin-password <email-superadmin>");
        Environment.ExitCode = 2;
        return;
    }

    var password = ReadHiddenPassword("Password baru (12–128 karakter): ");
    var confirmation = ReadHiddenPassword("Ulangi password baru: ");
    if (password.Length is < 12 or > 128)
    {
        Console.Error.WriteLine("Password harus 12–128 karakter.");
        Environment.ExitCode = 2;
        return;
    }
    if (!password.Equals(confirmation, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Konfirmasi password tidak cocok.");
        Environment.ExitCode = 2;
        return;
    }

    using var scope = app.Services.CreateScope();
    var database = scope.ServiceProvider.GetRequiredService<OmsJsonDatabase>();
    var backupPath = await database.ResetSuperadminPasswordAsync(
        args[1], password, CancellationToken.None);
    Console.WriteLine("Password SUPERADMIN lokal berhasil diperbarui.");
    if (!string.IsNullOrWhiteSpace(backupPath))
        Console.WriteLine($"Cadangan database: {backupPath}");
    return;
}

if (!app.Environment.IsDevelopment())
{
    var forwardedHeaders = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                           ForwardedHeaders.XForwardedProto |
                           ForwardedHeaders.XForwardedHost
    };
    forwardedHeaders.KnownIPNetworks.Clear();
    forwardedHeaders.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeaders);
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            if (context.Request.Path.StartsWithSegments("/api"))
                await context.Response.WriteAsJsonAsync(new { detail = exception.Message });
            else
                context.Response.Redirect("/account/denied");
        }
    }
    catch (InvalidOperationException exception) when (context.Request.Path.StartsWithSegments("/api"))
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { detail = exception.Message });
        }
    }
});
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

static string ReadHiddenPassword(string prompt)
{
    Console.Write(prompt);
    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return new string(characters.ToArray());
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (characters.Count > 0) characters.RemoveAt(characters.Count - 1);
            continue;
        }
        if (!char.IsControl(key.KeyChar))
            characters.Add(key.KeyChar);
    }
}

public partial class Program;
