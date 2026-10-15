namespace Collections.Core.Entities;

public enum PromiseStatus
{
    Open = 1,
    Kept = 2,
    Missed = 3,
    Cancelled = 4,
}

public class PromiseToPay : ITenantOwned
{
    public const int NotesMaxLength = 1000;

    public int Id { get; set; }
    public int BusinessId { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Optional: null means the promise is for the customer account as a whole.</summary>
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal PromisedAmount { get; set; }
    public DateOnly PromisedDate { get; set; }
    public PromiseStatus Status { get; set; } = PromiseStatus.Open;
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedByUserId { get; set; }

    /// <summary>Set once, when a user records the outcome. A resolved promise is not changed again.</summary>
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public string? ResolvedByUserId { get; set; }
    public string? ResolutionNotes { get; set; }
}
