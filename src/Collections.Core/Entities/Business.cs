using Collections.Core.Reminders;

namespace Collections.Core.Entities;

public class Business : IHasTimestamps
{
    public const string DefaultTimeZoneId = "Asia/Kolkata";
    public const int DefaultReminderCooldownDays = 7;

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Shown to customers in reminders and used as the reply-to address.</summary>
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }

    /// <summary>IANA time zone used to decide the business-local calendar date.</summary>
    public string TimeZoneId { get; set; } = DefaultTimeZoneId;

    /// <summary>Minimum days between two reminders for the same invoice and template.</summary>
    public int ReminderCooldownDays { get; set; } = DefaultReminderCooldownDays;

    public string ReminderSubjectTemplate { get; set; } = ReminderTemplateDefaults.Subject;
    public string ReminderBodyTemplate { get; set; } = ReminderTemplateDefaults.Body;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
