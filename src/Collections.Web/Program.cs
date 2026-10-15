using System.Globalization;
using System.Threading.RateLimiting;
using Collections.Core.Imports;
using Collections.Web.Data;
using Collections.Web.Email;
using Collections.Web.Infrastructure;
using Collections.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Persistence and tenancy
var connectionString = config.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'ConnectionStrings:DefaultConnection' is not configured.");
}

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICurrentBusinessContext, HttpCurrentBusinessContext>();

// Authentication: ASP.NET Core Identity with cookie sign-in
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/Login";
    o.Cookie.Name = "collections.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});

builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "collections.csrf";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Every page requires a signed-in user with a business workspace unless marked [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(HttpCurrentBusinessContext.BusinessIdClaim)
        .Build());

var keysDirectory = config["DataProtection:KeysDirectory"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Collections");
if (!string.IsNullOrWhiteSpace(keysDirectory))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
}

builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.Configure<CookieTempDataProviderOptions>(o =>
{
    o.Cookie.Name = "collections.tempdata";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Import limits
builder.Services.Configure<ImportLimits>(config.GetSection("Import"));
var importLimits = config.GetSection("Import").Get<ImportLimits>() ?? new ImportLimits();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = importLimits.MaxFileBytes + 64 * 1024);

// Email provider: a no-send capture sender in Development; must be configured explicitly elsewhere.
builder.Services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
var emailOptions = config.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();
var emailProvider = emailOptions.Provider ?? (builder.Environment.IsDevelopment() ? EmailOptions.CaptureProvider : null);
switch (emailProvider)
{
    case EmailOptions.CaptureProvider:
        builder.Services.AddScoped<IEmailSender, CaptureEmailSender>();
        break;
    case EmailOptions.SmtpProvider:
        if (string.IsNullOrWhiteSpace(emailOptions.Smtp.Host) || string.IsNullOrWhiteSpace(emailOptions.FromAddress))
        {
            throw new InvalidOperationException("Email:Smtp:Host and Email:FromAddress must be configured when Email:Provider is 'Smtp'.");
        }

        builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
        break;
    default:
        throw new InvalidOperationException("Email:Provider must be set to 'Smtp' or 'Capture' outside the Development environment.");
}

// Rate limits for sign-in and expensive endpoints
var loginPerMinute = config.GetValue("RateLimits:LoginPerMinute", 10);
var expensivePerMinute = config.GetValue("RateLimits:ExpensivePerMinute", 30);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(RateLimitPolicies.Login, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimitPolicies.Expensive, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst(HttpCurrentBusinessContext.BusinessIdClaim)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = expensivePerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// Application services
builder.Services.AddScoped<BusinessService>();
builder.Services.AddScoped<InvoiceQueryService>();
builder.Services.AddScoped<ActivityQueries>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<FollowUpService>();
builder.Services.AddScoped<WorklistService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<ReminderService>();

var app = builder.Build();

if (config.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/Home/Status", "?code={0}");
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseHttpsRedirection();
app.UseRequestLocalization(new RequestLocalizationOptions
{
    // Parsing and display are culture-independent; formatting for INR/dates is explicit in code.
    DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture),
    SupportedCultures = new[] { CultureInfo.InvariantCulture },
    SupportedUICultures = new[] { CultureInfo.InvariantCulture },
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
