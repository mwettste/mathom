using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mathom.Web.Pages.Account;

public class LogoutModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signIn;
    public LogoutModel(SignInManager<ApplicationUser> signIn) => _signIn = signIn;

    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await _signIn.SignOutAsync();
        return Redirect("/Login");
    }
}
