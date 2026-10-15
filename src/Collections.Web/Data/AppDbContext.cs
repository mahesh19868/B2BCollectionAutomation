using Collections.Core.Entities;
using Collections.Web.Infrastructure;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Collections.Web.Data;

/// <summary>
/// EF Core context. Tenant isolation is enforced here:
/// <list type="bullet">
/// <item>Global query filters restrict every business-owned entity to the current user's business.</item>
/// <item>SaveChanges stamps BusinessId on new records and refuses to write another business's records.</item>
/// </list>
/// </summary>
public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly ICurrentBusinessContext _business;
    private readonly TimeProvider _time;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentBusinessContext business, TimeProvider time)
        : base(options)
    {
        _business = business;
        _time = time;
    }

    /// <summary>Used by query filters; -1 (matches nothing) when there is no signed-in business.</summary>
    public int CurrentBusinessId => _business.BusinessId ?? -1;

    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<FollowUpActivity> FollowUpActivities => Set<FollowUpActivity>();
    public DbSet<PromiseToPay> PromisesToPay => Set<PromiseToPay>();
    public DbSet<ReminderLog> ReminderLogs => Set<ReminderLog>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Every business-owned entity has the same tenant filter, so required navigations stay consistent.
        optionsBuilder.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(e =>
        {
            e.HasOne(u => u.Business).WithMany().HasForeignKey(u => u.BusinessId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Business>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(200).IsRequired();
            e.Property(b => b.ContactEmail).HasMaxLength(Customer.EmailMaxLength);
            e.Property(b => b.ContactPhone).HasMaxLength(Customer.PhoneMaxLength);
            e.Property(b => b.TimeZoneId).HasMaxLength(64).IsRequired();
            e.Property(b => b.ReminderSubjectTemplate).HasMaxLength(Core.Reminders.ReminderTemplateRenderer.SubjectMaxLength).IsRequired();
            e.Property(b => b.ReminderBodyTemplate).HasMaxLength(Core.Reminders.ReminderTemplateRenderer.BodyMaxLength).IsRequired();
        });

        builder.Entity<Customer>(e =>
        {
            ConfigureTenant(e);
            e.Property(c => c.Name).HasMaxLength(Customer.NameMaxLength).IsRequired();
            e.Property(c => c.CustomerCode).HasMaxLength(Customer.CodeMaxLength);
            e.Property(c => c.Email).HasMaxLength(Customer.EmailMaxLength);
            e.Property(c => c.Phone).HasMaxLength(Customer.PhoneMaxLength);
            e.Property(c => c.Notes).HasMaxLength(Customer.NotesMaxLength);
            e.HasIndex(c => new { c.BusinessId, c.CustomerCode }).IsUnique().HasFilter("[CustomerCode] IS NOT NULL");
            e.HasIndex(c => new { c.BusinessId, c.Name });
        });

        builder.Entity<Invoice>(e =>
        {
            ConfigureTenant(e);
            e.Property(i => i.InvoiceNumber).HasMaxLength(Invoice.NumberMaxLength).IsRequired();
            e.Property(i => i.CurrencyCode).HasMaxLength(3).IsRequired();
            e.Property(i => i.InvoiceAmount).HasPrecision(18, 2);
            e.Property(i => i.ExternalReference).HasMaxLength(Invoice.ExternalReferenceMaxLength);
            e.HasOne(i => i.Customer).WithMany(c => c.Invoices).HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(i => new { i.BusinessId, i.InvoiceNumber }).IsUnique();
            e.HasIndex(i => new { i.BusinessId, i.DueDate });
            e.HasIndex(i => new { i.BusinessId, i.CustomerId });
            e.ToTable(t => t.HasCheckConstraint("CK_Invoices_InvoiceAmount_Positive", "[InvoiceAmount] > 0"));
        });

        builder.Entity<Payment>(e =>
        {
            ConfigureTenant(e);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Reference).HasMaxLength(Payment.ReferenceMaxLength);
            e.Property(p => p.Notes).HasMaxLength(Payment.NotesMaxLength);
            e.Property(p => p.CreatedByUserId).HasMaxLength(450);
            e.HasOne(p => p.Customer).WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(p => new { p.BusinessId, p.PaymentDate });
            e.HasIndex(p => new { p.BusinessId, p.CustomerId });
            e.ToTable(t => t.HasCheckConstraint("CK_Payments_Amount_Positive", "[Amount] > 0"));
        });

        builder.Entity<PaymentAllocation>(e =>
        {
            ConfigureTenant(e);
            e.Property(a => a.AllocatedAmount).HasPrecision(18, 2);
            e.HasOne(a => a.Payment).WithMany(p => p.Allocations).HasForeignKey(a => a.PaymentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Invoice).WithMany(i => i.Allocations).HasForeignKey(a => a.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(a => new { a.PaymentId, a.InvoiceId }).IsUnique();
            e.HasIndex(a => new { a.BusinessId, a.InvoiceId });
            e.ToTable(t => t.HasCheckConstraint("CK_PaymentAllocations_Amount_Positive", "[AllocatedAmount] > 0"));
        });

        builder.Entity<ImportBatch>(e =>
        {
            ConfigureTenant(e);
            e.Property(b => b.FileName).HasMaxLength(Core.Imports.FileNameSanitizer.MaxLength).IsRequired();
            e.Property(b => b.UploadedByUserId).HasMaxLength(450);
            e.Property(b => b.FailureMessage).HasMaxLength(1000);
            e.HasIndex(b => new { b.BusinessId, b.UploadedAtUtc });
        });

        builder.Entity<ImportRow>(e =>
        {
            ConfigureTenant(e);
            e.HasOne(r => r.ImportBatch).WithMany(b => b.Rows).HasForeignKey(r => r.ImportBatchId).OnDelete(DeleteBehavior.Cascade);
            foreach (var name in new[] { nameof(ImportRow.CustomerCode), nameof(ImportRow.CustomerName), nameof(ImportRow.CustomerEmail),
                         nameof(ImportRow.InvoiceNumber), nameof(ImportRow.InvoiceDate), nameof(ImportRow.DueDate), nameof(ImportRow.InvoiceAmount) })
            {
                e.Property(name).HasMaxLength(500);
            }

            e.HasIndex(r => new { r.ImportBatchId, r.RowNumber });
        });

        builder.Entity<FollowUpActivity>(e =>
        {
            ConfigureTenant(e);
            e.Property(a => a.Notes).HasMaxLength(FollowUpActivity.NotesMaxLength).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(FollowUpActivity.OutcomeMaxLength);
            e.Property(a => a.CreatedByUserId).HasMaxLength(450);
            e.HasOne(a => a.Customer).WithMany().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Invoice).WithMany().HasForeignKey(a => a.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(a => new { a.BusinessId, a.CustomerId, a.OccurredAtUtc });
            e.HasIndex(a => new { a.BusinessId, a.InvoiceId });
        });

        builder.Entity<PromiseToPay>(e =>
        {
            ConfigureTenant(e);
            e.Property(p => p.PromisedAmount).HasPrecision(18, 2);
            e.Property(p => p.Notes).HasMaxLength(PromiseToPay.NotesMaxLength);
            e.Property(p => p.ResolutionNotes).HasMaxLength(PromiseToPay.NotesMaxLength);
            e.Property(p => p.CreatedByUserId).HasMaxLength(450);
            e.Property(p => p.ResolvedByUserId).HasMaxLength(450);
            e.HasOne(p => p.Customer).WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.Invoice).WithMany().HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(p => new { p.BusinessId, p.Status, p.PromisedDate });
            e.HasIndex(p => new { p.BusinessId, p.CustomerId });
            e.ToTable(t => t.HasCheckConstraint("CK_PromisesToPay_Amount_Positive", "[PromisedAmount] > 0"));
        });

        builder.Entity<ReminderLog>(e =>
        {
            ConfigureTenant(e);
            e.Property(r => r.TemplateKey).HasMaxLength(50).IsRequired();
            e.Property(r => r.IdempotencyKey).HasMaxLength(64).IsRequired();
            e.Property(r => r.Recipient).HasMaxLength(Customer.EmailMaxLength).IsRequired();
            e.Property(r => r.Subject).HasMaxLength(Core.Reminders.ReminderTemplateRenderer.SubjectMaxLength).IsRequired();
            e.Property(r => r.Body).HasMaxLength(8000).IsRequired();
            e.Property(r => r.OutstandingAmount).HasPrecision(18, 2);
            e.Property(r => r.ProviderMessageId).HasMaxLength(200);
            e.Property(r => r.LastError).HasMaxLength(1000);
            e.Property(r => r.CreatedByUserId).HasMaxLength(450);
            e.HasOne(r => r.Customer).WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Invoice).WithMany().HasForeignKey(r => r.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.BusinessId, r.IdempotencyKey }).IsUnique();
            e.HasIndex(r => new { r.BusinessId, r.InvoiceId, r.TemplateKey, r.Status });
            e.HasIndex(r => new { r.BusinessId, r.CreatedAtUtc });
        });
    }

    private void ConfigureTenant<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : class, ITenantOwned
    {
        entity.HasQueryFilter(e => e.BusinessId == CurrentBusinessId);
        entity.HasOne<Business>().WithMany().HasForeignKey(e => e.BusinessId).OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyTenantAndAuditRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyTenantAndAuditRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyTenantAndAuditRules()
    {
        var now = _time.GetUtcNow();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            switch (entry.Entity)
            {
                case ITenantOwned owned:
                {
                    var current = _business.BusinessId
                        ?? throw new InvalidOperationException("Business-owned data cannot be saved without a signed-in business.");

                    if (entry.State == EntityState.Added && owned.BusinessId == 0)
                    {
                        owned.BusinessId = current;
                    }

                    if (owned.BusinessId != current)
                    {
                        throw new InvalidOperationException("Attempted to write data belonging to another business.");
                    }

                    if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantOwned.BusinessId)).IsModified)
                    {
                        throw new InvalidOperationException("BusinessId cannot be changed.");
                    }

                    break;
                }
                case Business business when entry.State != EntityState.Added:
                    if (business.Id != _business.BusinessId)
                    {
                        throw new InvalidOperationException("Attempted to modify another business.");
                    }

                    break;
            }

            if (entry.Entity is IHasTimestamps stamped)
            {
                if (entry.State == EntityState.Added)
                {
                    stamped.CreatedAtUtc = now;
                    stamped.UpdatedAtUtc = now;
                }
                else if (entry.State == EntityState.Modified)
                {
                    stamped.UpdatedAtUtc = now;
                }
            }
        }
    }
}
