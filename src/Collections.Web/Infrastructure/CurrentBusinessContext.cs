using System.Globalization;
using System.Security.Claims;
using Collections.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Collections.Web.Infrastructure;

/// <summary>
/// The workspace of the signed-in user. Derived only from the authenticated principal (an encrypted,
/// server-issued cookie), never from request data.
/// </summary>
public interface ICurrentBusinessContext
{
    int? BusinessId { get; }
    string? UserId { get; }
}

public sealed class HttpCurrentBusinessContext : ICurrentBusinessContext
{
    public const string BusinessIdClaim = "business_id";

    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentBusinessContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public int? BusinessId
    {
        get
        {
            var user = _accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var value = user.FindFirstValue(BusinessIdClaim);
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
    }

    public string? UserId
    {
        get
        {
            var user = _accessor.HttpContext?.User;
            return user?.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        }
    }
}

/// <summary>Adds the user's workspace membership to the sign-in cookie.</summary>
public sealed class AppClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser>
{
    public AppClaimsPrincipalFactory(UserManager<ApplicationUser> userManager, IOptions<IdentityOptions> options)
        : base(userManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.BusinessId is { } businessId)
        {
            identity.AddClaim(new Claim(HttpCurrentBusinessContext.BusinessIdClaim, businessId.ToString(CultureInfo.InvariantCulture)));
        }

        return identity;
    }
}
