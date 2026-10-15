using System.Net;
using Collections.Core.Entities;
using Collections.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class ImportTests
{
    private const string Header = "CustomerCode,CustomerName,CustomerEmail,InvoiceNumber,InvoiceDate,DueDate,InvoiceAmount";

    private readonly AppFactory _factory;

    public ImportTests(AppFactory factory)
    {
        _factory = factory;
        _factory.Time.UtcNow = AppFactory.FixedNow;
    }

    private static string Csv(params string[] rows) => Header + "\r\n" + string.Join("\r\n", rows) + "\r\n";

    private async Task<int> UploadAsync(Browser user, string csv, string fileName = "invoices.csv")
    {
        var response = await user.UploadAsync(fileName, csv);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Browser.IdFromLocation(response);
    }

    private static Task<HttpResponseMessage> ConfirmAsync(Browser user, int batchId, bool approveUpdates = false)
        => user.PostFormAsync($"/Import/Batch/{batchId}", $"/Import/Confirm/{batchId}",
            approveUpdates ? new() { ["approveUpdates"] = "true" } : new());

    [Fact]
    public async Task Valid_file_is_previewed_without_saving_then_committed_on_confirmation()
    {
        var user = await Browser.RegisterAsync(_factory);
        var csv = Csv(
            "C001,Sharma Traders,accounts@sharma.example,INV-001,2026-08-01,2026-09-01,\"1,25,000.00\"",
            "C001,Sharma Traders,,INV-002,15/08/2026,15/09/2026,5000",
            ",Gupta Stores,,INV-003,01-Sep-2026,01-Oct-2026,999.99");

        var batchId = await UploadAsync(user, csv);
        var preview = await user.GetHtmlAsync($"/Import/Batch/{batchId}");
        Assert.Contains("nothing has been imported yet", preview);

        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            Assert.Equal(0, await db.Invoices.CountAsync());
            Assert.Equal(0, await db.Customers.CountAsync());
        }

        Arrange.AssertRedirect(await ConfirmAsync(user, batchId));

        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            Assert.Equal(3, await db.Invoices.CountAsync());
            var sharma = await db.Customers.SingleAsync(c => c.CustomerCode == "C001");
            Assert.Equal("accounts@sharma.example", sharma.Email);
            Assert.Equal(2, await db.Invoices.CountAsync(i => i.CustomerId == sharma.Id));
            Assert.Equal(125000.00m, (await db.Invoices.SingleAsync(i => i.InvoiceNumber == "INV-001")).InvoiceAmount);
            Assert.Equal(new DateOnly(2026, 9, 15), (await db.Invoices.SingleAsync(i => i.InvoiceNumber == "INV-002")).DueDate);
            var batch = await db.ImportBatches.SingleAsync();
            Assert.Equal(ImportBatchStatus.Committed, batch.Status);
            Assert.Equal((3, 0, 0, 0), (batch.CreatedCount, batch.UpdatedCount, batch.SkippedCount, batch.RejectedRows));
        }

        Assert.DoesNotContain(_factory.Email.Sent, m => m.To == "accounts@sharma.example"); // import never sends reminders
    }

    [Fact]
    public async Task Reimporting_the_same_file_does_not_create_duplicates()
    {
        var user = await Browser.RegisterAsync(_factory);
        var csv = Csv(",Repeat Co,,R-1,2026-08-01,2026-09-01,100", ",Repeat Co,,R-2,2026-08-01,2026-09-01,200");

        Arrange.AssertRedirect(await ConfirmAsync(user, await UploadAsync(user, csv)));
        var second = await UploadAsync(user, csv);
        Arrange.AssertRedirect(await ConfirmAsync(user, second));

        await using var db = _factory.CreateDbContext(user.BusinessId);
        Assert.Equal(2, await db.Invoices.CountAsync());
        Assert.Equal(1, await db.Customers.CountAsync());
        var batch = await db.ImportBatches.SingleAsync(b => b.Id == second);
        Assert.Equal((0, 2), (batch.CreatedCount, batch.SkippedCount));
        Assert.All(await db.ImportRows.Where(r => r.ImportBatchId == second).ToListAsync(), r => Assert.Equal(ImportRowOutcome.SkippedUnchanged, r.Outcome));
    }

    [Fact]
    public async Task Changed_invoices_are_updated_only_when_user_approves()
    {
        var user = await Browser.RegisterAsync(_factory);
        Arrange.AssertRedirect(await ConfirmAsync(user, await UploadAsync(user, Csv(",Update Co,,U-1,2026-08-01,2026-09-01,100"))));

        var changed = Csv(",Update Co,,U-1,2026-08-01,2026-09-10,150");
        var notApproved = await UploadAsync(user, changed);
        Assert.Contains("amount ₹100.00 → ₹150.00", await user.GetHtmlAsync($"/Import/Batch/{notApproved}"));
        Arrange.AssertRedirect(await ConfirmAsync(user, notApproved, approveUpdates: false));

        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            Assert.Equal(100m, (await db.Invoices.SingleAsync()).InvoiceAmount);
            Assert.Equal(ImportRowOutcome.SkippedUpdateNotApproved, (await db.ImportRows.SingleAsync(r => r.ImportBatchId == notApproved)).Outcome);
        }

        var approved = await UploadAsync(user, changed);
        Arrange.AssertRedirect(await ConfirmAsync(user, approved, approveUpdates: true));

        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            var invoice = await db.Invoices.SingleAsync();
            Assert.Equal(150m, invoice.InvoiceAmount);
            Assert.Equal(new DateOnly(2026, 9, 10), invoice.DueDate);
            Assert.True(await db.FollowUpActivities.AnyAsync(a => a.InvoiceId == invoice.Id && a.ActivityType == ActivityType.System && a.Notes.Contains("updated by import")));
        }
    }

    [Fact]
    public async Task Invalid_rows_are_reported_with_row_numbers_and_only_valid_rows_are_committed()
    {
        var user = await Browser.RegisterAsync(_factory);
        var csv = Csv(
            ",Good Co,,G-1,2026-08-01,2026-09-01,100",
            ",Bad Date Co,,G-2,2026-02-30,2026-09-01,100",
            ",Neg Co,,G-3,2026-08-01,2026-09-01,-5",
            ",Dup Co,,G-4,2026-08-01,2026-09-01,10",
            ",Dup Co,,G-4,2026-08-01,2026-09-01,10",
            ",\"=HYPERLINK(\"\"http://evil\"\")\",,G-5,2026-08-01,2026-09-01,abc");

        var batchId = await UploadAsync(user, csv);
        var html = await user.GetHtmlAsync($"/Import/Batch/{batchId}");
        Assert.Contains("'2026-02-30' is not a valid date", html);
        Assert.Contains("cannot be negative", html);
        Assert.Contains("rows 5, 6", html);

        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            var rows = await db.ImportRows.Where(r => r.ImportBatchId == batchId).OrderBy(r => r.RowNumber).ToListAsync();
            Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, rows.Select(r => r.RowNumber));
            Assert.Equal(new[] { ImportRowAction.Create, ImportRowAction.Error, ImportRowAction.Error, ImportRowAction.Error, ImportRowAction.Error, ImportRowAction.Error },
                rows.Select(r => r.PlannedAction));
        }

        var report = await user.Client.GetStringAsync($"/Import/Errors/{batchId}");
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", report);
        Assert.DoesNotContain("G-1", report);

        Arrange.AssertRedirect(await ConfirmAsync(user, batchId));
        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            Assert.Equal("G-1", (await db.Invoices.SingleAsync()).InvoiceNumber);
            var batch = await db.ImportBatches.SingleAsync();
            Assert.Equal((1, 5), (batch.CreatedCount, batch.RejectedRows));
            Assert.Equal(5, await db.ImportRows.CountAsync(r => r.ImportBatchId == batchId && r.Outcome == ImportRowOutcome.Rejected));
        }
    }

    [Theory]
    [InlineData("invoices.xlsx", "CustomerName,InvoiceNumber,InvoiceDate,DueDate,InvoiceAmount\nA,1,2026-01-01,2026-01-02,1\n", "Only .csv files")]
    [InlineData("invoices.csv", "", "Choose a CSV file")]
    [InlineData("invoices.csv", "CustomerName,InvoiceNumber\nA,1\n", "Missing required column")]
    [InlineData("invoices.csv", "CustomerName,InvoiceNumber,InvoiceDate,DueDate,InvoiceAmount\n", "no data rows")]
    public async Task Invalid_files_are_rejected_without_creating_a_batch(string fileName, string content, string expectedMessage)
    {
        var user = await Browser.RegisterAsync(_factory);

        var response = await user.UploadAsync(fileName, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedMessage, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        await using var db = _factory.CreateDbContext(user.BusinessId);
        Assert.False(await db.ImportBatches.AnyAsync());
    }

    [Fact]
    public async Task Oversized_files_and_too_many_rows_are_rejected()
    {
        var user = await Browser.RegisterAsync(_factory);

        var tooManyRows = Csv(Enumerable.Range(1, 201).Select(i => $",Many Co,,M-{i},2026-08-01,2026-09-01,10").ToArray());
        var rowsResponse = await user.UploadAsync("many.csv", tooManyRows);
        Assert.Contains("the limit is 200", WebUtility.HtmlDecode(await rowsResponse.Content.ReadAsStringAsync()));

        var huge = Csv(",Big Co,,B-1,2026-08-01,2026-09-01,10," + new string('x', 70_000));
        var sizeResponse = await user.UploadAsync("big.csv", huge);
        Assert.Contains("larger than the 64 KB limit", WebUtility.HtmlDecode(await sizeResponse.Content.ReadAsStringAsync()));

        await using var db = _factory.CreateDbContext(user.BusinessId);
        Assert.False(await db.ImportBatches.AnyAsync());
    }

    [Fact]
    public async Task Confirming_twice_does_not_import_twice()
    {
        var user = await Browser.RegisterAsync(_factory);
        var batchId = await UploadAsync(user, Csv(",Twice Co,,T-1,2026-08-01,2026-09-01,100"));

        Arrange.AssertRedirect(await ConfirmAsync(user, batchId));
        var second = await user.PostFormAsync("/Import", $"/Import/Confirm/{batchId}", new());
        Arrange.AssertRedirect(second);

        await using var db = _factory.CreateDbContext(user.BusinessId);
        Assert.Equal(1, await db.Invoices.CountAsync());
    }

    [Fact]
    public async Task Import_that_fails_at_commit_saves_nothing_and_is_marked_failed()
    {
        var user = await Browser.RegisterAsync(_factory);
        var batchId = await UploadAsync(user, Csv(",Fail Co,,F-1,2026-08-01,2026-09-01,100", ",Fail Co,,F-2,2026-08-01,2026-09-01,100"));

        // Simulate data that became invalid after the preview: a raw value too long for the column forces a database error at commit.
        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            await db.ImportRows.Where(r => r.ImportBatchId == batchId && r.RowNumber == 3)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.CustomerCode, new string('Z', 50)));
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Customers ADD CONSTRAINT CK_Test_NoZ CHECK (CustomerCode IS NULL OR CustomerCode NOT LIKE 'ZZZZ%')");
        }

        try
        {
            Arrange.AssertRedirect(await ConfirmAsync(user, batchId));

            await using var db = _factory.CreateDbContext(user.BusinessId);
            Assert.Equal(0, await db.Invoices.CountAsync());
            Assert.Equal(0, await db.Customers.CountAsync());
            var batch = await db.ImportBatches.SingleAsync();
            Assert.Equal(ImportBatchStatus.Failed, batch.Status);
            Assert.Contains("nothing was imported", batch.FailureMessage);
        }
        finally
        {
            await using var db = _factory.CreateDbContext(user.BusinessId);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Customers DROP CONSTRAINT CK_Test_NoZ");
        }
    }
}
