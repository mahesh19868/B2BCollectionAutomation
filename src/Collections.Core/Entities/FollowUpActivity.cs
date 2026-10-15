namespace Collections.Core.Entities;

public enum ActivityType
{
    Call = 1,
    Email = 2,
    Note = 3,
    Other = 4,

    /// <summary>Recorded automatically by the application (e.g. invoice corrected, promise resolved).</summary>
    System = 99,
}

public class FollowUpActivity : ITenantOwned
{
    public const int NotesMaxLength = 2000;
    public const int OutcomeMaxLength = 200;

    public int Id { get; set; }
    public int BusinessId { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public ActivityType ActivityType { get; set; }
    public string? Outcome { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
