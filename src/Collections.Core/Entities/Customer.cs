namespace Collections.Core.Entities;

public class Customer : ITenantOwned, IHasTimestamps
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;
    public const int EmailMaxLength = 254;
    public const int PhoneMaxLength = 30;
    public const int NotesMaxLength = 2000;

    public int Id { get; set; }
    public int BusinessId { get; set; }

    /// <summary>Optional code from the accounting system; unique within a business when supplied.</summary>
    public string? CustomerCode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }

    /// <summary>Stored for reference only. Not used for WhatsApp in the MVP.</summary>
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public bool RemindersPaused { get; set; }
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<Invoice> Invoices { get; set; } = new();
}
