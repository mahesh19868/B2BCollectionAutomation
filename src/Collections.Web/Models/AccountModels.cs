using System.ComponentModel.DataAnnotations;

namespace Collections.Web.Models;

public sealed class LoginViewModel
{
    [Required, EmailAddress, Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), Display(Name = "Password")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Keep me signed in on this device")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public sealed class RegisterViewModel
{
    [Required, StringLength(200), Display(Name = "Business name")]
    public string BusinessName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(254), Display(Name = "Your email")]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 10), DataType(DataType.Password), Display(Name = "Password")]
    public string Password { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "Passwords do not match."), Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
