namespace Collections.Core.Entities;

public class Invoice : ITenantOwned, IHasTimestamps
{
    public const string DefaultCurrency = "INR";
    public const int NumberMaxLength = 50;
    public const int ExternalReferenceMaxLength = 100;

    public int Id { get; set; }
    public int BusinessId { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Unique within a business (case-insensitive). Never changed after creation.</summary>
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string CurrencyCode { get; set; } = DefaultCurrency;
    public decimal InvoiceAmount { get; set; }
    public bool IsDisputed { get; set; }
    public bool RemindersPaused { get; set; }
    public string? ExternalReference { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<PaymentAllocation> Allocations { get; set; } = new();
}
