namespace Collections.Core.Entities;

public class Payment : ITenantOwned
{
    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 1000;

    public int Id { get; set; }
    public int BusinessId { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateOnly PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedByUserId { get; set; }

    public List<PaymentAllocation> Allocations { get; set; } = new();
}

public class PaymentAllocation : ITenantOwned
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public int PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal AllocatedAmount { get; set; }
}
