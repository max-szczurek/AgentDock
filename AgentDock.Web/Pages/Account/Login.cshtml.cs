using System.Security.Claims;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Account;

public class LoginModel(
    SignInManager<IdentityUser> signIn,
    UserManager<IdentityUser> users,
    AccountProvisioner provisioner,
    AccessGate gate,
    IAuthenticationSchemeProvider schemes,
    ILogger<LoginModel> logger) : PageModel
{
    public string? ErrorMessage { get; set; }
    public bool GoogleEnabled { get; private set; }

    public async Task OnGetAsync() => GoogleEnabled = await HasGoogle();

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!await HasGoogle()) { GoogleEnabled = false; return Page(); }
        var redirect = Url.Page("/Account/Login", pageHandler: "Callback", values: new { returnUrl });
        return Challenge(signIn.ConfigureExternalAuthenticationProperties("Google", redirect), "Google");
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl = null, string? remoteError = null)
    {
        returnUrl ??= "/";
        GoogleEnabled = await HasGoogle();
        if (remoteError != null) { ErrorMessage = $"Google sign-in error: {remoteError}"; return Page(); }

        var info = await signIn.GetExternalLoginInfoAsync();
        if (info == null) { ErrorMessage = "Could not retrieve sign-in information from Google. Please try again."; return Page(); }

        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email)) { ErrorMessage = "Google did not return an email address."; return Page(); }

        if (!gate.IsEmailAllowed(email))
        {
            logger.LogWarning("Sign-in refused for {Email}: not on the private beta allowlist", email);
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            return RedirectToPage("/Account/Denied");
        }

        var result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
        if (result.Succeeded)
        {
            var existing = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (existing != null) await provisioner.EnsureAccountAsync(existing);
            return LocalRedirect(returnUrl);
        }

        var user = await provisioner.EnsureUserAsync(email);
        var add = await users.AddLoginAsync(user, info);
        if (!add.Succeeded) { ErrorMessage = string.Join(" ", add.Errors.Select(e => e.Description)); return Page(); }
        await signIn.SignInAsync(user, isPersistent: false);
        return LocalRedirect(returnUrl);
    }

    private async Task<bool> HasGoogle() => await schemes.GetSchemeAsync("Google") != null;
}
