namespace Collections.Core.Entities;

public enum ImportType
{
    Invoices = 1,
}

public enum ImportBatchStatus
{
    /// <summary>Parsed and validated; waiting for the user to confirm.</summary>
    Previewed = 1,
    Committing = 2,
    Committed = 3,
    Failed = 4,
    Cancelled = 5,
}

public enum ImportRowAction
{
    Create = 1,
    Update = 2,
    Unchanged = 3,
    Error = 4,
}

public enum ImportRowOutcome
{
    Created = 1,
    Updated = 2,
    SkippedUnchanged = 3,
    SkippedUpdateNotApproved = 4,
    Rejected = 5,
}

public class ImportBatch : ITenantOwned
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public ImportType ImportType { get; set; } = ImportType.Invoices;

    /// <summary>Sanitised original file name, for display only.</summary>
    public string FileName { get; set; } = string.Empty;
    public string? UploadedByUserId { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public ImportBatchStatus Status { get; set; } = ImportBatchStatus.Previewed;
    public bool UpdatesApproved { get; set; }

    /// <summary>File-level notices, one per line.</summary>
    public string? FileMessages { get; set; }
    public string? FailureMessage { get; set; }

    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int RejectedRows { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }

    public List<ImportRow> Rows { get; set; } = new();
}

/// <summary>
/// One data row of an uploaded file: the raw text values (so the import can be confirmed without
/// keeping the uploaded file) plus the validation result and final outcome.
/// </summary>
public class ImportRow : ITenantOwned
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public int ImportBatchId { get; set; }
    public ImportBatch? ImportBatch { get; set; }

    /// <summary>Row number as seen in a spreadsheet (header is row 1).</summary>
    public int RowNumber { get; set; }

    public string? CustomerCode { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? InvoiceDate { get; set; }
    public string? DueDate { get; set; }
    public string? InvoiceAmount { get; set; }

    public ImportRowAction PlannedAction { get; set; }
    public ImportRowOutcome? Outcome { get; set; }

    /// <summary>Validation errors, one per line, formatted "Field: message".</summary>
    public string? Errors { get; set; }

    /// <summary>Non-blocking notices and change descriptions, one per line.</summary>
    public string? Notes { get; set; }
}
