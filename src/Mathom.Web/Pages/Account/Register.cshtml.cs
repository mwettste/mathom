using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Mathom.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace Mathom.Web.Pages.Account;

public class RegisterModel : PageModel
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly RoleManager<IdentityRole> _roles;
    private readonly IConfiguration _config;

    public RegisterModel(
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signIn,
        RoleManager<IdentityRole> roles,
        IConfiguration config)
    {
        _users = users;
        _signIn = signIn;
        _roles = roles;
        _config = config;
    }

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
        var result = await _users.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return Page();
        }

        await Mathom.Web.Admin.AdminBootstrap.EnsureRoleAndPromoteAsync(_roles, _users, _config["AdminEmail"]);
        await _signIn.SignInAsync(user, isPersistent: true);
        return Redirect("/");
    }
}
