using Collections.Core.Entities;

namespace Collections.Web.Data;

public static class SystemNotes
{
    /// <summary>
    /// Adds (without saving) an automatic history entry so corrections and outcomes are never silent.
    /// </summary>
    public static void AddSystemNote(this AppDbContext db, int customerId, int? invoiceId, string text, string? userId, DateTimeOffset nowUtc)
    {
        db.FollowUpActivities.Add(new FollowUpActivity
        {
            CustomerId = customerId,
            InvoiceId = invoiceId,
            ActivityType = ActivityType.System,
            Notes = text.Length > FollowUpActivity.NotesMaxLength ? text[..FollowUpActivity.NotesMaxLength] : text,
            OccurredAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            CreatedByUserId = userId,
        });
    }
}
