using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Pages.Account;

public class RegisterModel(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    RoleManager<IdentityRole> roles,
    IConfiguration config) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        var user = new ApplicationUser { UserName = Input.Email, Email = Input.Email };
        var result = await users.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return Page();
        }

        await Mathom.Web.Admin.AdminBootstrap.EnsureRoleAndPromoteAsync(roles, users, config["AdminEmail"]);
        await signIn.SignInAsync(user, isPersistent: true);
        return Redirect("/");
    }
}
