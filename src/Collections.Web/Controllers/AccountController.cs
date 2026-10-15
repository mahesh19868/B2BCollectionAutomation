using Collections.Core.Entities;
using Collections.Web.Data;
using Collections.Web.Infrastructure;
using Collections.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Collections.Web.Controllers;

[AllowAnonymous]
public sealed class AccountController : AppController
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppDbContext _db;
    private readonly ILogger<AccountController> _logger;

    public AccountController(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager, AppDbContext db,
        ILogger<AccountController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
        => User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Home") : View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost, EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _signInManager.PasswordSignInAsync(model.Email.Trim(), model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            _logger.LogInformation("User signed in");
            return RedirectToLocal(model.ReturnUrl, RedirectToAction("Index", "Home"));
        }

        if (result.IsLockedOut)
        {
            _logger.LogWarning("Sign-in blocked: account locked out");
            ModelState.AddModelError(string.Empty, "Too many failed attempts. Try again in 15 minutes.");
        }
        else
        {
            _logger.LogWarning("Failed sign-in attempt");
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
        }

        return View(model);
    }

    [HttpGet]
    public IActionResult Register()
        => User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Home") : View(new RegisterViewModel());

    /// <summary>Creates a new business workspace and its first user.</summary>
    [HttpPost, EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var business = new Business { Name = model.BusinessName.Trim(), ContactEmail = model.Email.Trim() };
        _db.Businesses.Add(business);
        await _db.SaveChangesAsync(ct);

        var user = new ApplicationUser { UserName = model.Email.Trim(), Email = model.Email.Trim(), BusinessId = business.Id };
        var created = await _userManager.CreateAsync(user, model.Password);
        if (!created.Succeeded)
        {
            await transaction.RollbackAsync(ct);
            foreach (var error in created.Errors)
            {
                // Identity's DuplicateUserName/DuplicateEmail messages are shown as-is; acceptable for a B2B sign-up form.
                ModelState.AddModelError(error.Code.Contains("Password") ? nameof(model.Password) : string.Empty, error.Description);
            }

            return View(model);
        }

        await transaction.CommitAsync(ct);
        _logger.LogInformation("New business workspace {BusinessId} registered", business.Id);
        await _signInManager.SignInAsync(user, isPersistent: false);
        Flash("Your workspace is ready. Start by importing invoices or adding a customer.");
        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }
}
