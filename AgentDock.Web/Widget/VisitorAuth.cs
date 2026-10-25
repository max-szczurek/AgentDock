using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentDock.Core.Data;
using AgentDock.Core.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AgentDock.Web.Widget;

public static class VisitorAuth
{
    public const string Scheme = "Visitor";
    public const string Policy = "Visitor";
    public const string ItemKey = "agentdock.visitor";

    public static VisitorSession Session(this HttpContext ctx) => (VisitorSession)ctx.Items[ItemKey]!;
}

/// <summary>Bearer-token authentication for widget API calls. Cookies are never used for visitors.</summary>
public sealed class VisitorTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IVisitorSessionService sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var session = await sessions.AuthenticateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (session == null) return AuthenticateResult.Fail("Invalid or expired visitor token.");

        Context.Items[VisitorAuth.ItemKey] = session;
        var identity = new ClaimsIdentity(
            [new Claim("sid", session.Id.ToString()), new Claim("agent", session.AgentId.ToString()), new Claim("mode", session.Mode)],
            VisitorAuth.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), VisitorAuth.Scheme));
    }
}
