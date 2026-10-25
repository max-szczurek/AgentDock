using AgentDock.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Dev;

[IgnoreAntiforgeryToken]
public class DevLoginModel(
    AccessGate gate,
    AttemptLimiter limiter,
    AccountProvisioner provisioner,
    SignInManager<IdentityUser> signIn,
    IAntiforgery antiforgery,
    ILogger<DevLoginModel> logger) : PageModel
{
    public string? Error { get; set; }
    public string ReturnUrl { get; set; } = "/";
    public IReadOnlyList<string> Emails => gate.DevLoginEmails;

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (!gate.DevLoginEnabled) return NotFound();
        ReturnUrl = SafeReturn(returnUrl);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromForm] string? secret, [FromForm] string? email, [FromForm] string? returnUrl)
    {
        if (!gate.DevLoginEnabled) return NotFound();

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var headerSecret = Request.Headers["X-Dev-Login-Secret"].ToString();
        var scripted = !string.IsNullOrEmpty(headerSecret);
        if (scripted) secret = headerSecret;
        else
        {
            try { await antiforgery.ValidateRequestAsync(HttpContext); }
            catch (AntiforgeryValidationException) { return BadRequest(); }
        }

        if (!limiter.TryAcquire(ip))
        {
            logger.LogWarning("Dev login rate limited for {Ip}", ip);
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        email = email?.Trim().ToLowerInvariant() ?? "";
        var ok = gate.SecretMatches(secret) & gate.IsDevLoginEmail(email);
        logger.LogWarning("Dev login attempt from {Ip} as {Email}: {Result}", ip, email, ok ? "success" : "denied");
        if (!ok)
        {
            Error = "Invalid secret or email.";
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            ReturnUrl = SafeReturn(returnUrl);
            return Page();
        }

        var user = await provisioner.EnsureUserAsync(email);
        await signIn.SignInAsync(user, isPersistent: false, authenticationMethod: AccessGate.DevLoginAuthMethod);
        return LocalRedirect(SafeReturn(returnUrl));
    }

    private string SafeReturn(string? url) => !string.IsNullOrEmpty(url) && Url.IsLocalUrl(url) ? url : "/";
}
