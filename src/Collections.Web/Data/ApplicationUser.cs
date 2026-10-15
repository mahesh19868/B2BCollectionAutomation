using Collections.Core.Entities;
using Microsoft.AspNetCore.Identity;

namespace Collections.Web.Data;

/// <summary>
/// Sign-in account. In the MVP each account belongs to one business workspace; several accounts
/// may share a workspace, which keeps the model ready for multiple users per business.
/// </summary>
public class ApplicationUser : IdentityUser
{
    public int? BusinessId { get; set; }
    public Business? Business { get; set; }
}
