using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Core.Imports;
using Collections.Core.Reminders;
using Collections.Web.Data;
using Collections.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

public class BusinessSettingsInput
{
    public string Name { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public int ReminderCooldownDays { get; set; }
    public string ReminderSubjectTemplate { get; set; } = string.Empty;
    public string ReminderBodyTemplate { get; set; } = string.Empty;
}

/// <summary>The current workspace and its business-local calendar date.</summary>
public sealed class BusinessService
{
    private readonly AppDbContext _db;
    private readonly ICurrentBusinessContext _context;
    private readonly TimeProvider _time;
    private Business? _cached;

    public BusinessService(AppDbContext db, ICurrentBusinessContext context, TimeProvider time)
    {
        _db = db;
        _context = context;
        _time = time;
    }

    public async Task<Business> GetCurrentAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var id = _context.BusinessId ?? throw new InvalidOperationException("No business is associated with the current user.");
        _cached = await _db.Businesses.AsNoTracking().SingleAsync(b => b.Id == id, ct);
        return _cached;
    }

    /// <summary>Today's date in the business's time zone. All due/overdue decisions use this.</summary>
    public async Task<DateOnly> GetTodayAsync(CancellationToken ct)
    {
        var business = await GetCurrentAsync(ct);
        return BusinessCalendar.LocalDate(_time.GetUtcNow(), business.TimeZoneId);
    }

    public async Task<ServiceResult> UpdateSettingsAsync(BusinessSettingsInput input, CancellationToken ct)
    {
        var errors = new List<ServiceError>();

        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
        {
            errors.Add(new(nameof(input.Name), "Business name is required (max 200 characters)."));
        }

        var email = string.IsNullOrWhiteSpace(input.ContactEmail) ? null : input.ContactEmail.Trim();
        if (email is not null && !ImportValueParser.IsValidEmail(email))
        {
            errors.Add(new(nameof(input.ContactEmail), "Enter a valid email address."));
        }

        var phone = string.IsNullOrWhiteSpace(input.ContactPhone) ? null : input.ContactPhone.Trim();
        if (phone is { Length: > Customer.PhoneMaxLength })
        {
            errors.Add(new(nameof(input.ContactPhone), $"Phone must be at most {Customer.PhoneMaxLength} characters."));
        }

        if (input.ReminderCooldownDays is < 1 or > 90)
        {
            errors.Add(new(nameof(input.ReminderCooldownDays), "Cooldown must be between 1 and 90 days."));
        }

        errors.AddRange(ReminderTemplateRenderer.Validate(input.ReminderSubjectTemplate, ReminderTemplateRenderer.SubjectMaxLength)
            .Select(e => new ServiceError(nameof(input.ReminderSubjectTemplate), e)));
        errors.AddRange(ReminderTemplateRenderer.Validate(input.ReminderBodyTemplate, ReminderTemplateRenderer.BodyMaxLength)
            .Select(e => new ServiceError(nameof(input.ReminderBodyTemplate), e)));

        if (errors.Count > 0)
        {
            return ServiceResult.Fail(errors);
        }

        var id = _context.BusinessId ?? throw new InvalidOperationException("No business context.");
        var business = await _db.Businesses.SingleAsync(b => b.Id == id, ct);
        business.Name = name;
        business.ContactEmail = email;
        business.ContactPhone = phone;
        business.ReminderCooldownDays = input.ReminderCooldownDays;
        business.ReminderSubjectTemplate = input.ReminderSubjectTemplate.Trim();
        business.ReminderBodyTemplate = input.ReminderBodyTemplate.Replace("\r\n", "\n").Trim();
        await _db.SaveChangesAsync(ct);
        _cached = null;
        return ServiceResult.Ok(id);
    }
}
