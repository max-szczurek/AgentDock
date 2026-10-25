using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Account;

public class LogoutModel(SignInManager<IdentityUser> signIn) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync();
        return Redirect("/Account/Login");
    }
}
