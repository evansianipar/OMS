using Microsoft.AspNetCore.Authorization;
using OMS.Models;

namespace OMS.Services;

public sealed class OmsRoleRequirement : IAuthorizationRequirement;

public sealed class OmsRoleHandler(UserDirectoryService directory, IHttpContextAccessor accessor)
    : AuthorizationHandler<OmsRoleRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OmsRoleRequirement requirement)
    {
        var email = context.User.FindFirst("email")?.Value;
        var httpContext = accessor.HttpContext;
        if (string.IsNullOrWhiteSpace(email) || httpContext is null) return;

        try
        {
            var user = await directory.FindActiveUserAsync(email, httpContext.RequestAborted);
            if (user is null) return;

            var identity = context.User.Identity as System.Security.Claims.ClaimsIdentity;
            if (identity is not null && !identity.HasClaim(System.Security.Claims.ClaimTypes.Role, user.Role))
                identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, user.Role));
            context.Succeed(requirement);
        }
        catch (Exception exception)
        {
            httpContext.RequestServices.GetRequiredService<ILogger<OmsRoleHandler>>()
                .LogError(exception, "Failed to authorize OMS user from Google Sheets.");
        }
    }
}
