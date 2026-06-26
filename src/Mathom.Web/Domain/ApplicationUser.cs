using Microsoft.AspNetCore.Identity;

namespace Mathom.Web.Domain;

// Standard Identity user. Login identifier is the email; UserName mirrors it.
public class ApplicationUser : IdentityUser
{
    public bool IsApproved { get; set; }
    // The context the user is currently working in. Null = Inbox.
    public System.Guid? CurrentContextId { get; set; }
}
