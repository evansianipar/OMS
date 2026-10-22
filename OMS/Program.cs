using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OMS.Configuration;
using OMS.Services;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

if (!string.IsNullOrWhiteSpace(builder.Configuration["VERCEL"]))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

builder.Services.Configure<GoogleWorkspaceOptions>(builder.Configuration.GetSection("GoogleWorkspace"));
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GoogleSheetsCredentialStore>();
builder.Services.AddSingleton<GoogleSheetsService>();
builder.Services.AddSingleton<AppsScriptInvitationService>();
builder.Services.AddSingleton<OmsService>();
builder.Services.AddSingleton<UserDirectoryService>();
builder.Services.AddScoped<IAuthorizationHandler, OmsRoleHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("OmsUser", policy => policy.Requirements.Add(new OmsRoleRequirement()));
});

var workspace = builder.Configuration.GetSection("GoogleWorkspace").Get<GoogleWorkspaceOptions>() ?? new();
var auth = builder.Configuration.GetSection("Authentication:Google");
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
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
    })
    .AddCookie("GoogleSheetsSetup", options =>
    {
        options.Cookie.Name = "__Host-OMS.SheetsSetup";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = "https://accounts.google.com";
        options.ClientId = string.IsNullOrWhiteSpace(auth["ClientId"])
            ? "oauth-not-configured"
            : auth["ClientId"]!;
        options.ClientSecret = auth["ClientSecret"] ?? string.Empty;
        options.CallbackPath = "/signin-google";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            RoleClaimType = ClaimTypes.Role,
            ValidateIssuer = true
        };
        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.SetParameter("hd", workspace.Domain);
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var email = context.Principal?.FindFirst("email")?.Value?.Trim().ToLowerInvariant();
                var hostedDomain = context.Principal?.FindFirst("hd")?.Value?.Trim().ToLowerInvariant();
                var verified = context.Principal?.FindFirst("email_verified")?.Value;
                if (string.IsNullOrWhiteSpace(email) ||
                    !string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(hostedDomain, workspace.Domain, StringComparison.OrdinalIgnoreCase) ||
                    !email.EndsWith("@" + workspace.Domain, StringComparison.OrdinalIgnoreCase))
                {
                    context.Fail("Gunakan akun Google Workspace organisasi yang telah diverifikasi.");
                    return;
                }

                var directory = context.HttpContext.RequestServices.GetRequiredService<UserDirectoryService>();
                var authorizedUser = await directory.FindActiveUserAsync(email, context.HttpContext.RequestAborted);
                if (authorizedUser is null)
                {
                    context.Fail("Akun Google Anda belum terdaftar sebagai panitia aktif pada sheet Users.");
                    return;
                }

                var identity = (ClaimsIdentity)context.Principal!.Identity!;
                identity.AddClaim(new Claim(ClaimTypes.Role, authorizedUser.Role));
                identity.AddClaim(new Claim("oms:full_name", authorizedUser.FullName));
            },
            OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect("/account/denied");
                return Task.CompletedTask;
            }
        };
    })
    .AddOpenIdConnect("GoogleSheets", options =>
    {
        options.Authority = "https://accounts.google.com";
        options.ClientId = string.IsNullOrWhiteSpace(auth["ClientId"])
            ? "oauth-not-configured"
            : auth["ClientId"]!;
        options.ClientSecret = auth["ClientSecret"] ?? string.Empty;
        options.CallbackPath = "/signin-google-sheets";
        options.SignInScheme = "GoogleSheetsSetup";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.MapInboundClaims = false;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("https://www.googleapis.com/auth/spreadsheets");
        options.Scope.Add("https://www.googleapis.com/auth/script.send_mail");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            ValidateIssuer = true
        };
        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.SetParameter("hd", workspace.Domain);
                context.ProtocolMessage.SetParameter("access_type", "offline");
                context.ProtocolMessage.SetParameter("prompt", "consent");
                context.ProtocolMessage.SetParameter("include_granted_scopes", "true");
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var email = context.Principal?.FindFirst("email")?.Value?.Trim();
                var hostedDomain = context.Principal?.FindFirst("hd")?.Value?.Trim();
                var verified = context.Principal?.FindFirst("email_verified")?.Value;
                if (string.IsNullOrWhiteSpace(email) ||
                    !email.Equals(workspace.SheetsUser, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(hostedDomain, workspace.Domain, StringComparison.OrdinalIgnoreCase))
                    context.Fail($"Hubungkan menggunakan akun pemilik spreadsheet {workspace.SheetsUser}.");

                return Task.CompletedTask;
            },
            OnTicketReceived = async context =>
            {
                var refreshToken = context.Properties?.GetTokenValue("refresh_token");
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    context.HandleResponse();
                    context.Response.Redirect("/account/sheets-connect-failed?reason=missing-refresh-token");
                    return;
                }

                var store = context.HttpContext.RequestServices.GetRequiredService<GoogleSheetsCredentialStore>();
                await store.SaveAsync(refreshToken, context.HttpContext.RequestAborted);
                context.HandleResponse();
                context.Response.Redirect("/account/sheets-connected");
            },
            OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect("/account/sheets-connect-failed");
                return Task.CompletedTask;
            }
        };
    });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
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
    catch (Google.GoogleApiException exception)
    {
        app.Logger.LogError(exception, "Google Sheets API request failed.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            if (context.Request.Path.StartsWithSegments("/api"))
                await context.Response.WriteAsJsonAsync(new { detail = "Layanan Google Sheets tidak tersedia. Periksa konfigurasi dan izin server." });
            else
                context.Response.Redirect("/Home/Index?serviceError=google-sheets");
        }
    }
});
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
