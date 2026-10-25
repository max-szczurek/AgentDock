using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace AgentDock.Web.Services;

/// <summary>
/// Re-checks the private-beta allowlist on every authenticated request so removing an email
/// locks the user out immediately. Dev sign-in sessions are honoured only while dev sign-in is enabled.
/// </summary>
public class AllowlistMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AccessGate gate, SignInManager<IdentityUser> signIn)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && !ctx.Request.Path.StartsWithSegments("/Account/Denied"))
        {
            var isDev = ctx.User.HasClaim(ClaimTypes.AuthenticationMethod, AccessGate.DevLoginAuthMethod);
            var email = ctx.User.FindFirstValue(ClaimTypes.Email) ?? ctx.User.Identity.Name;
            var allowed = isDev ? gate.DevLoginEnabled : gate.IsEmailAllowed(email);
            if (!allowed)
            {
                await signIn.SignOutAsync();
                ctx.Response.Redirect("/Account/Denied");
                return;
            }
        }
        await next(ctx);
    }
}
