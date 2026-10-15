using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Collections.Web.Controllers;

/// <summary>Base for authenticated controllers: result-to-ModelState mapping and flash messages.</summary>
public abstract class AppController : Controller
{
    public const string StatusMessageKey = "StatusMessage";
    public const string ErrorMessageKey = "ErrorMessage";

    protected void AddErrors(ServiceResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(error.Field, error.Message);
        }
    }

    protected void Flash(string message) => TempData[StatusMessageKey] = message;

    protected void FlashError(string message) => TempData[ErrorMessageKey] = message;

    /// <summary>Redirects to a local return URL (e.g. a filtered worklist) or the fallback.</summary>
    protected IActionResult RedirectToLocal(string? returnUrl, IActionResult fallback)
        => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : fallback;
}
