namespace Collections.Core.Entities;

public enum ReminderChannel
{
    Email = 1,
}

public enum ReminderStatus
{
    Queued = 1,
    Sent = 2,
    Failed = 3,
    Cancelled = 4,
}

public class ReminderLog : ITenantOwned
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public ReminderChannel Channel { get; set; } = ReminderChannel.Email;
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>Unique per business; a double-submitted or retried request cannot send twice.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>Invoice outstanding at the time the reminder content was rendered.</summary>
    public decimal OutstandingAmount { get; set; }
    public ReminderStatus Status { get; set; } = ReminderStatus.Queued;
    public string? ProviderMessageId { get; set; }
    public int AttemptCount { get; set; }

    /// <summary>Sanitised error text; never contains credentials.</summary>
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public string? CreatedByUserId { get; set; }
}
